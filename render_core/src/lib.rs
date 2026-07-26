//! Render/interaction core for the PDF editor. Exposes a thin C ABI so the
//! WinUI 3 (C#) shell can call into PDFium via `pdfium-render` without a GC
//! in the hot render path.
//!
//! Phase 0 shape: a document is opened once and kept alive behind a handle;
//! rendering is dual-tier — `render_low_res` is a cheap, cache-first,
//! synchronous call meant to paint something during active pan/zoom, while
//! `request_high_res` / `poll_high_res` render the full-fidelity page on a
//! background thread and can be superseded mid-flight by a newer request for
//! the same page (generation counters make the stale one's result get
//! dropped instead of delivered). Both tiers share one LRU tile cache keyed
//! by (document, page, tier, width).
//!
//! PDFium itself is not thread-safe. pdfium-render's default `thread_safe`
//! feature is easy to misread as "every call is serialized" — it isn't: its
//! `ThreadSafePdfiumBindings` wrapper only takes a lock around
//! `FPDF_InitLibrary`/`FPDF_DestroyLibrary` (see its own source comment and
//! implementation), and forwards every other call — including
//! `FPDF_RenderPageBitmap` — straight through with no locking at all.
//! Spawning an OS thread per high-res request while `render_low_res` can run
//! on the calling thread at the same moment therefore means two real
//! concurrent native calls, which reliably heap-corrupts (caught by a test
//! that opened multiple documents across parallel test threads). `CALL_LOCK`
//! below is this crate's own serialization of every native-touching call —
//! open, render, and page-count — and is the actual thread-safety mechanism.
//! One consequence: a `render_low_res` call can block briefly behind an
//! in-flight high-res render holding that lock. Low-res target widths are
//! small enough that this is a short stall today; if it ever becomes
//! visible, splitting high-res rendering into cancellable sub-page tiles
//! (rather than one whole-page call) is the fix — natural follow-on work
//! once page virtualization (phase 2) exists.

use std::collections::HashMap;
use std::ffi::CStr;
use std::num::NonZeroUsize;
use std::os::raw::c_char;
use std::panic;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;

use lru::LruCache;
use pdfium_render::prelude::{Pdfium, PdfDocument};

// ---------------------------------------------------------------------
// FFI result type
// ---------------------------------------------------------------------

#[repr(C)]
pub struct RenderResult {
    pub width: i32,
    pub height: i32,
    /// Pointer to a heap-allocated BGRA8 buffer of `len` bytes (width * height * 4).
    /// Null on failure. Ownership passes to the caller; release with `free_render_result`.
    pub buffer: *mut u8,
    pub len: usize,
    /// 0 = ok, rendered via PDFium (fresh or cached). 1 = invalid input.
    /// 2 = panic inside the call. 3 = ok, but this is the placeholder
    /// fallback bitmap (PDFium unavailable or the page failed to load).
    pub status: i32,
}

pub const STATUS_OK_PDFIUM: i32 = 0;
pub const STATUS_INVALID_INPUT: i32 = 1;
pub const STATUS_PANIC: i32 = 2;
pub const STATUS_OK_PLACEHOLDER: i32 = 3;

/// The request was well formed but PDFium cannot carry it out at this version.
///
/// Distinct from STATUS_INVALID_INPUT so a caller can tell "you asked wrongly"
/// from "ask a different way", and fall back rather than surface an error.
pub const STATUS_UNSUPPORTED: i32 = 4;

pub const POLL_PENDING: i32 = 0;
pub const POLL_READY: i32 = 1;
pub const POLL_CANCELLED: i32 = 2;
pub const POLL_UNKNOWN_REQUEST: i32 = 3;

impl RenderResult {
    fn failure(status: i32) -> Self {
        RenderResult { width: 0, height: 0, buffer: std::ptr::null_mut(), len: 0, status }
    }
}

/// Frees a buffer previously returned in a `RenderResult` (by `render_low_res`
/// or a `poll_high_res` that returned `POLL_READY`). Safe to call with a
/// zeroed/failed result (null buffer is a no-op).
#[unsafe(no_mangle)]
pub extern "C" fn free_render_result(result: RenderResult) {
    if result.buffer.is_null() || result.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(result.buffer, result.len));
    }
}

// ---------------------------------------------------------------------
// Global state: one Pdfium instance, open documents, tile cache, in-flight
// high-res requests.
// ---------------------------------------------------------------------

static PDFIUM: OnceLock<Option<Pdfium>> = OnceLock::new();

/// Every call that touches PDFium — opening a document, reading its page
/// count, rendering a page — must hold this for the whole call. PDFium is
/// not safe for concurrent access from multiple threads and, contrary to
/// what its name suggests, pdfium-render's `thread_safe` feature does not
/// provide that serialization (see the module docs above).
static CALL_LOCK: Mutex<()> = Mutex::new(());

/// Locks a mutex, recovering from poisoning instead of panicking.
///
/// `Mutex::lock().unwrap()` panics forever once any thread has panicked while
/// holding that lock. The FFI entry points wrap their work in
/// `catch_unwind`, so a single panic does not kill the host app -- but with
/// plain `unwrap()` it would leave every shared map permanently poisoned, and
/// every later call would panic on acquisition. That turns one recoverable
/// failure into a dead render core for the rest of the process's life.
///
/// Everything behind these locks is a plain map or cache with no invariant a
/// half-finished mutation could break, so taking the data back with
/// `into_inner()` is safe and strictly better than refusing to work.
fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Binds PDFium once (relative to the host executable's directory — see
/// module docs on why cwd can't be trusted) and reuses that single instance
/// for the process's lifetime. `None` means PDFium isn't available; callers
/// fall back to the placeholder bitmap.
fn pdfium() -> Option<&'static Pdfium> {
    PDFIUM
        .get_or_init(|| {
            let exe_dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
            Pdfium::bind_to_library(Pdfium::pdfium_platform_library_name_at_path(
                exe_dir.to_string_lossy().as_ref(),
            ))
            .or_else(|_| Pdfium::bind_to_system_library())
            .ok()
            .map(Pdfium::new)
        })
        .as_ref()
}

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
enum Tier {
    Low,
    High,
}

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
struct TileKey {
    doc: u64,
    page: i32,
    tier: Tier,
    width: i32,
    /// Level-of-detail for tiles: the page is `256 * 2^level` pixels wide at
    /// this level. -1 for whole-page entries, which have no tile grid.
    level: i32,
    /// Tile column and row within that level. -1 for whole-page entries.
    col: i32,
    row: i32,
}

impl TileKey {
    /// Key for a whole-page render, which is not part of any tile grid.
    fn page_key(doc: u64, page: i32, tier: Tier, width: i32) -> Self {
        TileKey { doc, page, tier, width, level: -1, col: -1, row: -1 }
    }
}

#[derive(Clone)]
struct CachedTile {
    width: i32,
    height: i32,
    bytes: Arc<[u8]>,
}

fn tile_to_result(tile: &CachedTile, status: i32) -> RenderResult {
    buffer_to_result(tile.width, tile.height, tile.bytes.to_vec(), status)
}

/// Total pixel bytes the tile cache may hold. The LRU's own capacity is an
/// entry *count*, which is the wrong unit here: entry size scales with the
/// square of the zoom level, so a count-bounded cache silently balloons.
/// At a 6000px render width one page is ~178MB, so a 32-entry cache could
/// pin ~5.5GB. Evict on bytes instead and let the entry count run high.
const CACHE_BUDGET_BYTES: usize = 320 * 1024 * 1024;

/// Entries this large are never cached: a single tile bigger than a third of
/// the budget would evict essentially everything else to hold one page.
const MAX_CACHEABLE_TILE_BYTES: usize = CACHE_BUDGET_BYTES / 3;

/// Inserts a tile and evicts least-recently-used entries until the cache is
/// back inside `CACHE_BUDGET_BYTES`. Oversized tiles are simply not cached
/// (the caller still gets its copy of the render).
fn cache_put(key: TileKey, tile: CachedTile) {
    if tile.bytes.len() > MAX_CACHEABLE_TILE_BYTES {
        return;
    }

    let mut cache = lock(&core().cache);
    cache.put(key, tile);

    let mut total: usize = cache.iter().map(|(_, t)| t.bytes.len()).sum();
    while total > CACHE_BUDGET_BYTES {
        match cache.pop_lru() {
            Some((_, evicted)) => total -= evicted.bytes.len(),
            None => break,
        }
    }
}

enum RequestSlot {
    Pending { doc: u64, page: i32, generation: u64 },
    Ready(CachedTile),
    Cancelled,
    /// The caller discarded this request while it was still `Pending` — a
    /// tombstone so the background thread's eventual completion doesn't
    /// resurrect it with a `Ready`/`Cancelled` slot nobody will ever poll
    /// (that race is exactly why this can't just be a plain removal).
    Discarded,
}

struct Core {
    /// The inner `Mutex<PdfDocument>` isn't for thread-safety (`CALL_LOCK`
    /// already serializes every native call) — it's so Rust's type system
    /// allows `&mut PdfDocument` access at all, since documents are shared
    /// via `Arc` for cheap clone-out-of-the-map-lock. Needed for
    /// `delete_page`, the one operation that mutates the document itself
    /// rather than just an owned `PdfPage` handle obtained from it.
    documents: Mutex<HashMap<u64, Arc<Mutex<PdfDocument<'static>>>>>,
    next_doc_id: AtomicU64,
    cache: Mutex<LruCache<TileKey, CachedTile>>,
    /// Current generation per (doc, page); a fresh `request_high_res` call
    /// bumps this, which is how an older in-flight render for the same page
    /// gets recognized as stale and its result gets dropped instead of
    /// delivered.
    generations: Mutex<HashMap<(u64, i32), u64>>,
    next_generation: AtomicU64,
    requests: Mutex<HashMap<u64, RequestSlot>>,
    next_request_id: AtomicU64,
}

static CORE: OnceLock<Core> = OnceLock::new();

fn core() -> &'static Core {
    CORE.get_or_init(|| Core {
        documents: Mutex::new(HashMap::new()),
        next_doc_id: AtomicU64::new(1),
        // Deliberately generous: eviction is driven by CACHE_BUDGET_BYTES in
        // cache_put, so this count only exists as a backstop against tiny
        // tiles (thumbnails) accumulating without ever hitting the byte cap.
        cache: Mutex::new(LruCache::new(NonZeroUsize::new(512).unwrap())),
        generations: Mutex::new(HashMap::new()),
        next_generation: AtomicU64::new(1),
        requests: Mutex::new(HashMap::new()),
        next_request_id: AtomicU64::new(1),
    })
}

// ---------------------------------------------------------------------
// Document handles
// ---------------------------------------------------------------------

/// Opens a PDF and returns an opaque handle (0 = failure: bad path, PDFium
/// unavailable, or the document failed to load). Keep the handle for the
/// lifetime of the document; release it with `close_document`.
#[unsafe(no_mangle)]
pub extern "C" fn open_document(path: *const c_char) -> u64 {
    panic::catch_unwind(|| open_document_inner(path)).unwrap_or(0)
}

fn open_document_inner(path: *const c_char) -> u64 {
    if path.is_null() {
        return 0;
    }
    let Ok(path_str) = (unsafe { CStr::from_ptr(path) }).to_str() else {
        return 0;
    };

    let document = {
        let _guard = lock(&CALL_LOCK);
        let Some(pdfium) = pdfium() else {
            return 0;
        };
        let Ok(document) = pdfium.load_pdf_from_file(path_str, None) else {
            return 0;
        };
        document
    };

    let core = core();
    let id = core.next_doc_id.fetch_add(1, Ordering::Relaxed);
    lock(&core.documents).insert(id, Arc::new(Mutex::new(document)));
    id
}

/// Closes a document opened by `open_document` and evicts any cached tiles
/// / in-flight bookkeeping for it. A no-op for an unknown or zero handle.
#[unsafe(no_mangle)]
pub extern "C" fn close_document(doc_handle: u64) {
    if doc_handle == 0 {
        return;
    }
    let core = core();

    // Dropping the removed Arc can run PDFium's native close (if this was
    // the last reference) — hold CALL_LOCK across that, same as any other
    // native-touching call.
    {
        let _guard = lock(&CALL_LOCK);
        let removed = lock(&core.documents).remove(&doc_handle);
        drop(removed);
    }

    {
        let mut cache = lock(&core.cache);
        let stale_keys: Vec<TileKey> =
            cache.iter().filter(|(k, _)| k.doc == doc_handle).map(|(k, _)| *k).collect();
        for key in stale_keys {
            cache.pop(&key);
        }
    }

    lock(&core.generations).retain(|(doc, _), _| *doc != doc_handle);
}

/// Returns the page count for a handle, or -1 if the handle is unknown.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_count(doc_handle: u64) -> i32 {
    let _guard = lock(&CALL_LOCK);
    match lock(&core().documents).get(&doc_handle) {
        Some(doc) => lock(&doc).pages().len() as i32,
        None => -1,
    }
}

// ---------------------------------------------------------------------
// Page operations: rotate, delete, save. (No page reorder — pdfium-render
// has no move/swap primitive on PdfPages; reordering would need direct
// page-tree Kids-array manipulation that the crate doesn't expose. Rotate
// and delete only need an *owned* PdfPage/PdfPages handle obtained through
// a shared `&PdfDocument`, except delete, which needs `pages_mut()` and so
// needs the `Mutex<PdfDocument>` wrapper.)
// ---------------------------------------------------------------------

/// Rotates a page to the nearest multiple of 90 degrees clockwise. Evicts
/// that page's cached tiles, since a re-render would now look different.
/// Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT, or STATUS_PANIC.
#[unsafe(no_mangle)]
pub extern "C" fn rotate_page(doc_handle: u64, page_index: i32, degrees: i32) -> i32 {
    if doc_handle == 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| rotate_page_inner(doc_handle, page_index, degrees)).unwrap_or(STATUS_PANIC)
}

fn rotate_page_inner(doc_handle: u64, page_index: i32, degrees: i32) -> i32 {
    use pdfium_render::prelude::PdfPageRenderRotation;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let normalized = ((degrees % 360) + 360) % 360;
    let rotation = match normalized {
        0..=44 | 316..=359 => PdfPageRenderRotation::None,
        45..=134 => PdfPageRenderRotation::Degrees90,
        135..=224 => PdfPageRenderRotation::Degrees180,
        _ => PdfPageRenderRotation::Degrees270,
    };

    let doc_guard = lock(&doc);
    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };
    page.set_rotation(rotation);
    drop(page);
    drop(doc_guard);
    drop(doc);

    evict_page_cache(doc_handle, page_index);
    STATUS_OK_PDFIUM
}

/// Deletes a page. Page indices above it shift down by one, so every cached
/// tile and in-flight request for the whole document is evicted rather than
/// trying to renumber them individually.
#[unsafe(no_mangle)]
pub extern "C" fn delete_page(doc_handle: u64, page_index: i32) -> i32 {
    if doc_handle == 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| delete_page_inner(doc_handle, page_index)).unwrap_or(STATUS_PANIC)
}

fn delete_page_inner(doc_handle: u64, page_index: i32) -> i32 {
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = lock(&doc);
    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };
    if page.delete().is_err() {
        return STATUS_INVALID_INPUT;
    }
    drop(doc_guard);
    drop(doc);

    evict_all_cache_for_doc(doc_handle);
    lock(&core().generations).retain(|(d, _), _| *d != doc_handle);
    STATUS_OK_PDFIUM
}

/// Saves the document (including any rotate/delete/form-fill changes) to a
/// new file path.
#[unsafe(no_mangle)]
pub extern "C" fn save_document(doc_handle: u64, path: *const c_char) -> i32 {
    if doc_handle == 0 || path.is_null() {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| save_document_inner(doc_handle, path)).unwrap_or(STATUS_PANIC)
}

fn save_document_inner(doc_handle: u64, path: *const c_char) -> i32 {
    let Ok(path_str) = (unsafe { CStr::from_ptr(path) }).to_str() else {
        return STATUS_INVALID_INPUT;
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = lock(&doc);
    match doc_guard.save_to_file(path_str) {
        Ok(()) => STATUS_OK_PDFIUM,
        Err(_) => STATUS_INVALID_INPUT,
    }
}

/// Renders only a RECTANGLE of a page, at whatever resolution is asked for.
///
/// This is what makes deep zoom viable. Rendering a whole page at the
/// resolution deep zoom needs is quadratic: at 800% an A4 page wants roughly
/// 9600x13500 pixels, over 500MB, so the render has to be capped and the
/// result is upscaled and soft. Rendering only the part actually on screen
/// makes cost track the VIEWPORT instead of the page, so 800% and 6400% cost
/// the same and both stay sharp.
///
/// The region arrives in NORMALIZED page coordinates with a top-left origin
/// (the same space annotations use), so callers never deal in PDF points.
///
/// Implemented by temporarily narrowing the page's CropBox, which is the
/// PDF-native way to say "this is the visible area" (ISO 32000 14.11.2).
/// PDFium then renders exactly that box. The original box is always put back,
/// including on failure, because page width and height are DERIVED from it and
/// leaving it narrowed would silently corrupt every later layout and save.
#[unsafe(no_mangle)]
pub extern "C" fn render_region(
    doc_handle: u64,
    page_index: i32,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    out_width: i32,
) -> RenderResult {
    if doc_handle == 0 || out_width <= 0 || w <= 0.0 || h <= 0.0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    panic::catch_unwind(|| render_region_inner(doc_handle, page_index, x, y, w, h, out_width))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_region_inner(
    doc_handle: u64,
    page_index: i32,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    out_width: i32,
) -> RenderResult {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    };

    let page_w = page.width().value;
    let page_h = page.height().value;
    if page_w <= 0.0 || page_h <= 0.0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    // Remember the box to restore. A page without an explicit CropBox falls
    // back to its MediaBox, which is what PDFium was already using.
    let original = page
        .boundaries()
        .crop()
        .map(|b| b.bounds)
        .or_else(|_| page.boundaries().media().map(|b| b.bounds));
    let Ok(original) = original else {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    };

    // Normalized, top-left origin -> PDF points, bottom-left origin.
    let left = original.left().value + x * page_w;
    let right = left + w * page_w;
    let top = original.top().value - y * page_h;
    let bottom = top - h * page_h;

    let region = PdfRect::new(
        PdfPoints::new(bottom),
        PdfPoints::new(left),
        PdfPoints::new(top),
        PdfPoints::new(right),
    );

    if page.boundaries_mut().set_crop(region).is_err() {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    let rendered = render_page_via_pdfium_page(&page, out_width);

    // ALWAYS restore, including when the render failed: page dimensions are
    // derived from this box, so a leaked crop would corrupt layout and saving.
    let _ = page.boundaries_mut().set_crop(original);

    match rendered {
        Some((width, height, bytes)) => buffer_to_result(width, height, bytes, STATUS_OK_PDFIUM),
        None => RenderResult::failure(STATUS_INVALID_INPUT),
    }
}

/// Edge length of a tile, in pixels.
///
/// 256 is the usual choice and it is a balance: smaller tiles waste time on
/// per-call overhead and produce more seams to manage, larger ones lose the
/// benefit of fine-grained reuse when panning and cost more to discard.
/// Pixel edge of one tile. MUST equal TileGrid.TileSize in the app, since the
/// two sides derive the same grid geometry from it independently.
///
/// See TileGrid.TileSize for why this is 512: fewer, larger tiles cover a
/// viewport, so there is less to arrive one piece at a time and less to line up.
pub const TILE_SIZE: i32 = 512;

/// Renders one tile of a page's level-of-detail pyramid, with caching.
///
/// This is what makes deep zoom feel weightless, and it is the difference
/// between us and every viewer that stutters. A region render already keeps
/// cost proportional to the viewport, but it re-renders EVERYTHING whenever
/// the view moves, so panning at high zoom repeats work it just did. Tiles are
/// addressed on a fixed grid, so panning reuses every tile that stays on
/// screen and pays only for the strip that scrolled in.
///
/// The grid is a quadtree: at `level` the whole page is `TILE_SIZE * 2^level`
/// pixels wide and `2^level` tiles across. Levels are powers of two so a tile
/// stays valid across a range of zooms, and so a coarser level is always
/// available to display immediately while the exact one renders.
///
/// Memory is bounded twice over: a tile is a fixed 256KB regardless of zoom,
/// and the shared cache evicts by total bytes. That is why this works in a
/// small memory budget at any magnification.
#[unsafe(no_mangle)]
pub extern "C" fn render_tile(doc_handle: u64, page_index: i32, level: i32, col: i32, row: i32) -> RenderResult {
    if doc_handle == 0 || level < 0 || level > 20 || col < 0 || row < 0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    panic::catch_unwind(|| render_tile_inner(doc_handle, page_index, level, col, row))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_tile_inner(doc_handle: u64, page_index: i32, level: i32, col: i32, row: i32) -> RenderResult {
    let key = TileKey {
        doc: doc_handle,
        page: page_index,
        tier: Tier::High,
        width: TILE_SIZE,
        level,
        col,
        row,
    };

    // A cache hit is the common case while panning, and it is the whole point.
    if let Some(tile) = lock(&core().cache).get(&key) {
        return tile_to_result(tile, STATUS_OK_PDFIUM);
    }

    let across = (1i64 << level) as f32;

    // Page aspect decides how many tile ROWS exist, since tiles are square in
    // pixel space but the page is not.
    let aspect = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        };
        let g = lock(&doc);
        let Ok(page) = g.pages().get(page_index as u16) else {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        };
        let w = page.width().value;
        if w <= 0.0 {
            return RenderResult::failure(STATUS_INVALID_INPUT);
        }
        page.height().value / w
    };

    let rows_down = (across * aspect).ceil();
    if (col as f32) >= across || (row as f32) >= rows_down {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }

    // Tile bounds as fractions of page width and height respectively. The
    // vertical divisor carries the aspect so the tile stays SQUARE in pixels.
    let x = col as f32 / across;
    let w = 1.0 / across;
    let y = row as f32 / (across * aspect);
    let h = 1.0 / (across * aspect);

    let result = render_region(doc_handle, page_index, x, y, w, h, TILE_SIZE);
    if result.status != STATUS_OK_PDFIUM || result.buffer.is_null() {
        return result;
    }

    // Copy into the cache, then hand the caller its own view of the same
    // bytes, so a cached tile and a fresh one behave identically.
    let bytes = unsafe { std::slice::from_raw_parts(result.buffer, result.len) }.to_vec();
    let tile = CachedTile { width: result.width, height: result.height, bytes: Arc::from(bytes) };
    free_render_result(result);

    let out = tile_to_result(&tile, STATUS_OK_PDFIUM);
    cache_put(key, tile);
    out
}

/// The level whose rendered page width first meets or exceeds `needed_px`.
///
/// Exposed so the caller picks levels by the same rule the renderer uses,
/// rather than reimplementing the pyramid and drifting out of step.
#[unsafe(no_mangle)]
pub extern "C" fn tile_level_for_width(needed_px: i32) -> i32 {
    if needed_px <= TILE_SIZE {
        return 0;
    }

    let mut level = 0;
    let mut width = TILE_SIZE as i64;
    while width < needed_px as i64 && level < 20 {
        width *= 2;
        level += 1;
    }
    level
}

/// One page's intrinsic size in PDF points.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct PageSize {
    pub width: f32,
    pub height: f32,
}

/// Sizes for every page in a document. Must be released with
/// `free_page_size_array`.
#[repr(C)]
pub struct PageSizeArray {
    pub sizes: *mut PageSize,
    pub len: usize,
    pub status: i32,
}

impl PageSizeArray {
    fn failure(status: i32) -> Self {
        PageSizeArray { sizes: std::ptr::null_mut(), len: 0, status }
    }
}

/// Returns every page's size in one call.
///
/// A continuous-scroll viewport has to lay out ALL page slots before it
/// renders anything, or the scrollbar and every scroll-offset-to-page
/// mapping would be wrong until the last page happened to be rasterized.
/// Rendering 300 pages just to measure them is not viable, and calling a
/// per-page size function 300 times would take the global PDFium lock 300
/// times, so sizes come back in a single locked pass.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_sizes(doc_handle: u64) -> PageSizeArray {
    if doc_handle == 0 {
        return PageSizeArray::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_page_sizes_inner(doc_handle))
        .unwrap_or_else(|_| PageSizeArray::failure(STATUS_PANIC))
}

fn get_page_sizes_inner(doc_handle: u64) -> PageSizeArray {
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return PageSizeArray::failure(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);

    let pages = doc_guard.pages();
    let count = pages.len();
    let mut out: Vec<PageSize> = Vec::with_capacity(count as usize);
    for i in 0..count {
        match pages.get(i) {
            Ok(page) => out.push(PageSize {
                width: page.width().value,
                height: page.height().value,
            }),
            // Keep the array index-aligned with page indices even if one page
            // fails to load; a zero size is a slot the caller can skip.
            Err(_) => out.push(PageSize { width: 0.0, height: 0.0 }),
        }
    }

    let mut boxed = out.into_boxed_slice();
    let array = PageSizeArray {
        sizes: boxed.as_mut_ptr(),
        len: boxed.len(),
        status: STATUS_OK_PDFIUM,
    };
    std::mem::forget(boxed);
    array
}

#[unsafe(no_mangle)]
pub extern "C" fn free_page_size_array(array: PageSizeArray) {
    if array.sizes.is_null() || array.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(array.sizes, array.len));
    }
}

/// An owned byte buffer handed across the FFI boundary. Must be released with
/// `free_byte_buffer`. A non-zero `status` means `data` is null.
#[repr(C)]
pub struct ByteBuffer {
    pub data: *mut u8,
    pub len: usize,
    pub status: i32,
}

impl ByteBuffer {
    fn err(status: i32) -> Self {
        ByteBuffer { data: std::ptr::null_mut(), len: 0, status }
    }
}

/// Serializes the whole document to memory. This is the document-level undo
/// snapshot: page deletes and rotations restructure the document in ways no
/// per-object inverse can express, so the only reliable undo is to keep the
/// bytes and reopen them.
#[unsafe(no_mangle)]
pub extern "C" fn snapshot_document(doc_handle: u64) -> ByteBuffer {
    if doc_handle == 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| snapshot_document_inner(doc_handle))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn snapshot_document_inner(doc_handle: u64) -> ByteBuffer {
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let doc_guard = lock(&doc);
    let Ok(bytes) = doc_guard.save_to_bytes() else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    // Hand ownership to the caller as a boxed slice, so free_byte_buffer can
    // reconstruct it with the exact same layout.
    let mut boxed = bytes.into_boxed_slice();
    let buffer = ByteBuffer { data: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
    std::mem::forget(boxed);
    buffer
}

/// Releases a buffer returned by `snapshot_document`. Safe to call on an
/// error buffer (null data).
#[unsafe(no_mangle)]
pub extern "C" fn free_byte_buffer(buffer: ByteBuffer) {
    if buffer.data.is_null() || buffer.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(buffer.data, buffer.len));
    }
}

/// Opens a document from an in-memory snapshot, yielding a fresh handle. The
/// bytes are copied, so the caller keeps ownership of its own buffer and can
/// restore the same snapshot repeatedly (undo, redo, undo again).
#[unsafe(no_mangle)]
pub extern "C" fn open_document_from_bytes(data: *const u8, len: usize) -> u64 {
    if data.is_null() || len == 0 {
        return 0;
    }
    panic::catch_unwind(|| open_document_from_bytes_inner(data, len)).unwrap_or(0)
}

fn open_document_from_bytes_inner(data: *const u8, len: usize) -> u64 {
    let bytes = unsafe { std::slice::from_raw_parts(data, len) }.to_vec();

    let document = {
        let _guard = lock(&CALL_LOCK);
        let Some(pdfium) = pdfium() else {
            return 0;
        };
        let Ok(document) = pdfium.load_pdf_from_byte_vec(bytes, None) else {
            return 0;
        };
        document
    };

    let core = core();
    let id = core.next_doc_id.fetch_add(1, Ordering::Relaxed);
    lock(&core.documents).insert(id, Arc::new(Mutex::new(document)));
    id
}

// ---------------------------------------------------------------------
// Annotation burning: flatten overlay annotations into real page content.
//
// Annotations are captured in RENDER-PIXEL space (top-left origin, Y down)
// at some render width. PDF page objects live in POINTS with a BOTTOM-left
// origin and Y up. Both conversions are needed:
//
//     scale  = page_width_pt / capture_width_px
//     pdf_x  = x_px * scale
//     pdf_y  = page_height_pt - y_px * scale
//
// The Y flip is the part that has no analogue in a top-down drawing API, so
// it is easy to omit and produces annotations mirrored about the page's
// horizontal centre line.
//
// These write page CONTENT, not PDF annotation objects, so the marks are
// permanent and render identically in every viewer.
// ---------------------------------------------------------------------

/// One axis-aligned filled rectangle (a highlight) in render-pixel space.
#[repr(C)]
pub struct BurnRect {
    pub page_index: i32,
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

/// One point of a polyline. Strokes are passed as a flat point array plus a
/// separate array of per-stroke lengths, which keeps the FFI to plain slices
/// instead of a pointer-to-pointer.
#[repr(C)]
pub struct BurnPoint {
    pub x: f32,
    pub y: f32,
}

/// Header describing one ink stroke inside the flat point array.
#[repr(C)]
pub struct BurnStroke {
    pub page_index: i32,
    /// Index of this stroke's first point in the shared point array.
    pub point_offset: u32,
    pub point_count: u32,
    pub width_px: f32,
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

/// One axis-aligned quad of a highlight, in render-pixel space.
#[repr(C)]
pub struct HighlightQuad {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
}

/// Header describing one highlight ANNOTATION inside the flat quad array.
///
/// A highlight over several lines of text is one annotation carrying several
/// quads, not several annotations. That is what makes it behave as a single
/// object afterwards: one click selects the whole highlight, one delete
/// removes it, one colour change recolours all of it. Splitting it per line
/// would leave the user picking fragments apart.
#[repr(C)]
pub struct HighlightSpec {
    pub page_index: i32,
    /// Index of this highlight's first quad in the shared quad array.
    pub quad_offset: u32,
    pub quad_count: u32,
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
}

/// A sticky note: a position in render-pixel space plus its text.
///
/// The text is a separate NUL-terminated UTF-8 pointer rather than an inline
/// buffer because note text is unbounded, and a fixed-size array would either
/// truncate what the user wrote or make every note pay for the longest one.
#[repr(C)]
pub struct BurnNote {
    pub page_index: i32,
    pub x: f32,
    pub y: f32,
    pub text: *const c_char,
}

/// Adds sticky notes as real PDF text annotations.
///
/// Notes are NOT burned into the content stream like highlights and ink. A
/// note's value is its TEXT, and flattening it to a marker graphic would throw
/// exactly that away: the mark would survive and the words would not. A PDF
/// text annotation keeps the text as text, so other readers show it as a note
/// and it stays selectable and searchable. That also means notes are naturally
/// idempotent to re-save in a way burned content is not.
/// Removes one annotation from a page.
///
/// `index` is the position reported by `get_annotations`, and is only valid
/// until the page's annotation list next changes.
#[unsafe(no_mangle)]
pub extern "C" fn delete_annotation(doc_handle: u64, page_index: i32, index: i32) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| delete_annotation_inner(doc_handle, page_index, index))
        .unwrap_or(STATUS_PANIC)
}

fn delete_annotation_inner(doc_handle: u64, page_index: i32, index: i32) -> i32 {
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };

    let annotations = page.annotations_mut();
    let Ok(annotation) = annotations.get(index as usize) else {
        return STATUS_INVALID_INPUT;
    };
    if annotations.delete_annotation(annotation).is_err() {
        return STATUS_INVALID_INPUT;
    }

    drop(page);
    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Moves, and where possible resizes, an annotation to a new rectangle.
///
/// An annotation's rectangle is not always what gets drawn, so what has to
/// happen depends on where the annotation keeps its shape:
///
/// - Notes and shapes are drawn from the rectangle, so setting it is enough.
/// - A highlight's shape is its QUAD POINTS, which are transformed to match.
/// - A stamp and ink own page objects. PDFium repositions their appearance to
///   follow the rectangle, so moving works, but it does NOT scale it.
///
/// That last case is a real limit rather than an oversight. Transforming the
/// contained objects is the obvious fix and it does nothing: an object taken
/// from `objects_mut().get()` is detached, and `apply_matrix` on it never
/// reaches the stored annotation. Verified by running the same resize with the
/// transform skipped and getting a pixel-identical render.
///
/// So a request to SCALE one of those returns STATUS_UNSUPPORTED, and the
/// caller should delete it and add it again at the new size. That is not a
/// hardship: the app holds the source PNG or the stroke points already, and
/// re-adding produces a correct appearance instead of a stretched one.
#[unsafe(no_mangle)]
pub extern "C" fn set_annotation_bounds(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if !(right > left) || !(bottom > top) {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        set_annotation_bounds_inner(doc_handle, page_index, index, capture_width,
                                    left, top, right, bottom)
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
fn set_annotation_bounds_inner(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
) -> i32 {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };

    let page_w = page.width().value;
    if page_w <= 0.0 {
        return STATUS_INVALID_INPUT;
    }

    let media = page.boundaries().media().map(|b| b.bounds);
    let (origin_x, origin_top) = match media {
        Ok(b) => (b.left().value, b.top().value),
        Err(_) => (0.0, page.height().value),
    };
    let scale = page_w / capture_width as f32;

    let new_x0 = origin_x + left * scale;
    let new_x1 = origin_x + right * scale;
    let new_y1 = origin_top - top * scale;
    let new_y0 = origin_top - bottom * scale;

    let Ok(mut annotation) = page.annotations_mut().get(index as usize) else {
        return STATUS_INVALID_INPUT;
    };

    let Ok(old) = annotation.bounds() else {
        return STATUS_INVALID_INPUT;
    };
    let (old_x0, old_y0) = (old.left().value, old.bottom().value);
    let (old_w, old_h) = (old.right().value - old_x0, old.top().value - old_y0);
    if old_w.abs() < 1e-6 || old_h.abs() < 1e-6 {
        return STATUS_INVALID_INPUT;
    }

    // The affine taking the old rectangle onto the new one: scale about the
    // old origin, then move that origin to the new one.
    let sx = (new_x1 - new_x0) / old_w;
    let sy = (new_y1 - new_y0) / old_h;
    let tx = new_x0 - sx * old_x0;
    let ty = new_y0 - sy * old_y0;
    let delta = PdfMatrix::new(sx, 0.0, 0.0, sy, tx, ty);

    // Whether this is a pure move or also a scale. A scale is what the
    // object-owning kinds cannot do.
    let is_scaling = (sx - 1.0).abs() > 1e-4 || (sy - 1.0).abs() > 1e-4;

    /// Markup kinds whose shape is their quad points, not their rectangle.
    macro_rules! move_quads {
        ($a:expr) => {{
            let points = $a.attachment_points_mut();
            let count = points.len();
            for i in 0..count {
                if let Ok(quad) = points.get(i) {
                    let _ = points.set_attachment_point_at_index(i, quad.transform(delta));
                }
            }
        }};
    }

    match &mut annotation {
        PdfPageAnnotation::Highlight(a) => move_quads!(a),
        PdfPageAnnotation::Underline(a) => move_quads!(a),
        PdfPageAnnotation::Strikeout(a) => move_quads!(a),
        PdfPageAnnotation::Squiggly(a) => move_quads!(a),

        // Stamps and ink: PDFium carries their appearance along with the
        // rectangle, so a move needs nothing extra, but it will not scale it
        // and there is no way from here to make it. Say so rather than
        // silently leaving the picture at its old size inside a bigger box.
        PdfPageAnnotation::Stamp(_) | PdfPageAnnotation::Ink(_) if is_scaling => {
            return STATUS_UNSUPPORTED;
        }

        // Notes and the rest are drawn from their rectangle alone, so setting
        // it below is all they need.
        _ => {}
    }

    let bounds = PdfRect::new(
        PdfPoints::new(new_y0),
        PdfPoints::new(new_x0),
        PdfPoints::new(new_y1),
        PdfPoints::new(new_x1),
    );
    if annotation.set_bounds(bounds).is_err() {
        return STATUS_INVALID_INPUT;
    }

    drop(annotation);
    drop(page);
    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Normalizes a PDFium bitmap to the tightly packed BGRA everything else here
/// speaks, or None if the buffer does not match its stated size.
///
/// PDFium hands back whatever the stored image actually uses, which is not
/// always four bytes per pixel: an opaque image comes back as BGR, three bytes
/// wide, and a black-and-white one as a single grey byte. Assuming BGRA is how
/// an 8x8 image arrives as 192 bytes when 256 were expected.
fn to_bgra(
    width: i32,
    height: i32,
    format: pdfium_render::prelude::PdfBitmapFormat,
    bytes: &[u8],
) -> Option<(i32, i32, Vec<u8>)> {
    use pdfium_render::prelude::PdfBitmapFormat;

    if width <= 0 || height <= 0 {
        return None;
    }

    let pixels = (width as usize).checked_mul(height as usize)?;
    let stride = match format {
        PdfBitmapFormat::Gray => 1,
        PdfBitmapFormat::BGR => 3,
        _ => 4,   // BGRA and BGRx
    };

    if bytes.len() < pixels.checked_mul(stride)? {
        return None;
    }

    // Already what we want, so hand it straight over.
    if matches!(format, PdfBitmapFormat::BGRA) {
        return Some((width, height, bytes[..pixels * 4].to_vec()));
    }

    let mut out = Vec::with_capacity(pixels * 4);
    for i in 0..pixels {
        let p = i * stride;
        match stride {
            1 => {
                let v = bytes[p];
                out.extend_from_slice(&[v, v, v, 255]);
            }
            3 => out.extend_from_slice(&[bytes[p], bytes[p + 1], bytes[p + 2], 255]),
            // BGRx: the fourth byte is padding, not alpha, so it is replaced
            // rather than carried through as a transparency of whatever
            // happened to be in it.
            _ => out.extend_from_slice(&[bytes[p], bytes[p + 1], bytes[p + 2], 255]),
        }
    }

    Some((width, height, out))
}

/// Resizes an annotation to a new rectangle, rebuilding it when PDFium will
/// not scale it in place.
///
/// `set_annotation_bounds` handles a MOVE for everything and a resize for the
/// kinds whose shape is their quad points, but it refuses to scale a stamp:
/// the picture lives in an image object that cannot be transformed from
/// outside, so growing the rectangle alone would leave the image at its old
/// size inside a bigger box.
///
/// The fix is to rebuild the annotation, and doing it HERE rather than in the
/// app is what makes it work at all. Rebuilding needs the original pixels, and
/// the app only has those for a stamp it placed this session; one loaded from
/// a saved file, or made in Acrobat, it has never seen. Down here the image is
/// simply part of the annotation and can be read straight back out, so a stamp
/// resizes the same way whoever created it.
///
/// The rebuilt annotation goes to the END of the page's list, so its index
/// changes. That is why the new index is returned rather than assumed.
#[unsafe(no_mangle)]
pub extern "C" fn resize_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if !(right > left) || !(bottom > top) {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        resize_annotation_inner(doc_handle, page_index, index, capture_width,
                                left, top, right, bottom, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
fn resize_annotation_inner(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    out_new_index: *mut i32,
) -> i32 {
    use pdfium_render::prelude::*;

    // The straightforward path first. It succeeds for a move of anything, and
    // for a resize of the quad-point kinds, and only reports UNSUPPORTED for
    // the cases that genuinely need rebuilding.
    let direct = set_annotation_bounds(doc_handle, page_index, index, capture_width,
                                       left, top, right, bottom);
    if direct != STATUS_UNSUPPORTED {
        if direct == STATUS_OK_PDFIUM && !out_new_index.is_null() {
            unsafe { *out_new_index = index };
        }
        return direct;
    }

    // Rebuild. Pull the pixels out of the existing annotation before removing
    // it, since removing it takes the image with it.
    let extracted = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return STATUS_INVALID_INPUT;
        };
        let doc_guard = lock(&doc);
        let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
            return STATUS_INVALID_INPUT;
        };
        let Ok(mut annotation) = page.annotations_mut().get(index as usize) else {
            return STATUS_INVALID_INPUT;
        };

        let PdfPageAnnotation::Stamp(stamp) = &mut annotation else {
            // Ink is the other object-owning kind. Its shape is a path rather
            // than an image, so rebuilding it would mean reading path segments
            // back out and re-emitting them, which is a different job. Say so
            // instead of pretending.
            return STATUS_UNSUPPORTED;
        };

        let objects = stamp.objects_mut();
        let mut found = None;
        for i in 0..objects.len() {
            let Ok(object) = objects.get(i) else {
                continue;
            };
            // By reference: PdfPageObject implements Drop, so destructuring it
            // by value would try to move out of a type that cannot be moved.
            if let PdfPageObject::Image(image) = &object {
                // PROCESSED, not raw. The raw bitmap is the image alone, with
                // its transparency living separately in a soft mask, so
                // rebuilding from it would turn every transparent signature
                // into an opaque white block. The processed one has the mask
                // applied.
                let bitmap = image
                    .get_processed_bitmap(&doc_guard)
                    .or_else(|_| image.get_raw_bitmap());

                if let Ok(bitmap) = bitmap {
                    let format = bitmap.format().unwrap_or(PdfBitmapFormat::BGRA);
                    found = to_bgra(bitmap.width(), bitmap.height(), format, &bitmap.as_raw_bytes());
                }
                break;
            }
        }
        found
    };

    let Some((px_width, px_height, pixels)) = extracted else {
        return STATUS_UNSUPPORTED;
    };

    // to_bgra has already checked the source against its own format and
    // produced exactly four bytes per pixel, but this is the buffer that goes
    // back into PDFium, so it is checked again at the point of use.
    let expected = (px_width as usize)
        .saturating_mul(px_height as usize)
        .saturating_mul(4);
    if pixels.len() != expected || px_width <= 0 || px_height <= 0 {
        return STATUS_INVALID_INPUT;
    }

    // Remove, then re-place at the new size. Order matters: adding first would
    // briefly leave two copies, and a failure between the two would leave the
    // duplicate behind.
    let removed = delete_annotation(doc_handle, page_index, index);
    if removed != STATUS_OK_PDFIUM {
        return removed;
    }

    let added = add_stamp_annotation(
        doc_handle, page_index, capture_width,
        left, top, right, bottom,
        pixels.as_ptr(), pixels.len(), px_width, px_height);
    if added != STATUS_OK_PDFIUM {
        return added;
    }

    // The rebuilt annotation is now last on the page.
    if !out_new_index.is_null() {
        let array = get_annotations(doc_handle, page_index);
        let count = array.len;
        free_annotation_array(array);
        unsafe { *out_new_index = count.saturating_sub(1) as i32 };
    }

    STATUS_OK_PDFIUM
}

/// Places an image as a real PDF `/Stamp` annotation object.
///
/// Pixels arrive already decoded, as tightly packed BGRA, rather than as PNG
/// bytes. Decoding happens on the app side, which already has an image decoder
/// and needs the same pixels to show a preview of the stamp anyway. Doing it
/// there keeps the `image` crate and its encoder stack out of this binary,
/// which is a decision the crate's dependencies were deliberately chosen for,
/// and reuses the BGRA convention every other buffer in this FFI already uses.
///
/// `capture_width` and the rectangle follow the same top-left capture space as
/// the highlight, ink and note paths.
#[unsafe(no_mangle)]
pub extern "C" fn add_stamp_annotation(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    bgra: *const u8,
    byte_len: usize,
    pixel_width: i32,
    pixel_height: i32,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 || page_index < 0 {
        return STATUS_INVALID_INPUT;
    }
    if bgra.is_null() || pixel_width <= 0 || pixel_height <= 0 {
        return STATUS_INVALID_INPUT;
    }

    // The buffer is handed straight to PDFium, so a length that disagrees with
    // the stated dimensions would read past the end of it.
    let expected = (pixel_width as usize)
        .saturating_mul(pixel_height as usize)
        .saturating_mul(4);
    if byte_len != expected {
        return STATUS_INVALID_INPUT;
    }

    // A stamp with no area would be invisible and impossible to grab again.
    if !(right > left) || !(bottom > top) {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        add_stamp_annotation_inner(
            doc_handle, page_index, capture_width,
            left, top, right, bottom,
            bgra, byte_len, pixel_width, pixel_height,
        )
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
fn add_stamp_annotation_inner(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    bgra: *const u8,
    byte_len: usize,
    pixel_width: i32,
    pixel_height: i32,
) -> i32 {
    use pdfium_render::prelude::*;

    let Some(pdfium) = pdfium() else {
        return STATUS_INVALID_INPUT;
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };

    let page_w = page.width().value;
    if page_w <= 0.0 {
        return STATUS_INVALID_INPUT;
    }

    let media = page.boundaries().media().map(|b| b.bounds);
    let (origin_x, origin_top) = match media {
        Ok(b) => (b.left().value, b.top().value),
        Err(_) => (0.0, page.height().value),
    };
    let scale = page_w / capture_width as f32;

    let x0 = origin_x + left * scale;
    let x1 = origin_x + right * scale;
    let y1 = origin_top - top * scale;
    let y0 = origin_top - bottom * scale;

    let bounds = PdfRect::new(
        PdfPoints::new(y0),
        PdfPoints::new(x0),
        PdfPoints::new(y1),
        PdfPoints::new(x1),
    );

    let Ok(mut annotation) = page.annotations_mut().create_stamp_annotation() else {
        return STATUS_INVALID_INPUT;
    };

    // Bounds BEFORE objects, for the same reason as ink: PDFium builds the
    // annotation's appearance form from its rect.
    if annotation.set_bounds(bounds).is_err() {
        return STATUS_INVALID_INPUT;
    }

    // PdfBitmap borrows the buffer mutably, so this needs an owned copy rather
    // than the caller's memory, which is only valid for the call.
    let mut pixels: Vec<u8> = unsafe { std::slice::from_raw_parts(bgra, byte_len) }.to_vec();

    let bitmap = unsafe {
        PdfBitmap::from_bytes(
            pixel_width,
            pixel_height,
            PdfBitmapFormat::BGRA,
            pixels.as_mut_slice(),
            pdfium.bindings(),
        )
    };
    let Ok(bitmap) = bitmap else {
        return STATUS_INVALID_INPUT;
    };

    let Ok(mut image) = PdfPageImageObject::new(&doc_guard) else {
        return STATUS_INVALID_INPUT;
    };
    if image.set_bitmap(&bitmap).is_err() {
        return STATUS_INVALID_INPUT;
    }

    // A PDF image object draws the UNIT SQUARE, so its matrix is what gives it
    // a size and a position. Without this every stamp would be one point
    // square in the corner of the page regardless of the rectangle asked for.
    //
    // apply_matrix composes with what is already there, which is the identity
    // on a freshly created object, so this sets exactly this matrix.
    let matrix = PdfMatrix::new(x1 - x0, 0.0, 0.0, y1 - y0, x0, y0);
    if image.apply_matrix(matrix).is_err() {
        return STATUS_INVALID_INPUT;
    }

    if annotation.objects_mut().add_image_object(image).is_err() {
        return STATUS_INVALID_INPUT;
    }

    drop(annotation);
    drop(page);
    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Shape kinds. These numbers cross the FFI boundary and are mirrored by
/// `ShapeKind` in the C# viewport library, so they may be appended to but never
/// reordered.
pub const SHAPE_RECTANGLE: i32 = 0;
pub const SHAPE_ELLIPSE: i32 = 1;
pub const SHAPE_LINE: i32 = 2;
pub const SHAPE_ARROW: i32 = 3;

/// How far an arrow's barbs spread from the shaft, in radians (about 26°).
const ARROW_HEAD_ANGLE: f32 = 0.45;

/// Barb length as a multiple of the stroke width, with a floor so a hairline
/// arrow still has a visible head rather than a dot.
const ARROW_HEAD_SCALE: f32 = 6.0;
const ARROW_HEAD_MIN: f32 = 6.0;

/// Marks an annotation as one of our shapes, and records what it takes to
/// redraw it: `AyaanShape:kind:RRGGBBAA:width:fx:fy`.
///
/// This lives in `/Contents` because a shape written as ink is otherwise
/// indistinguishable from a freehand scribble once the file is reopened, and
/// then it can only be moved and deleted, never resized as the shape it is.
/// `/NM`, the annotation name field, would be the tidier home for machine data
/// since viewers do not display it, but this binding exposes `/NM` read-only.
///
/// The colour and width are in the tag because they cannot be read back:
/// querying an annotation's colour access-violates once it has an appearance
/// stream, and rendering creates one. See `get_annotations`.
///
/// `fx` and `fy` record which corner of the box the drag STARTED at, so a line
/// or arrow keeps pointing the way it was drawn. A bounding box alone cannot
/// say that.
const SHAPE_TAG: &str = "AyaanShape:";

fn shape_tag(spec: &ShapeSpec, width_pts: f32) -> String {
    let fx = u8::from(spec.x2 >= spec.x1);
    let fy = u8::from(spec.y2 >= spec.y1);
    format!(
        "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}",
        spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts
    )
}

/// The kind, colour, width and drag direction recorded on an annotation, or
/// None if it is not one of our shapes.
fn parse_shape_tag(contents: &str) -> Option<(i32, u8, u8, u8, u8, f32, bool, bool)> {
    let rest = contents.strip_prefix(SHAPE_TAG)?;
    let mut parts = rest.split(':');

    let kind: i32 = parts.next()?.parse().ok()?;
    if !matches!(kind, SHAPE_RECTANGLE | SHAPE_ELLIPSE | SHAPE_LINE | SHAPE_ARROW) {
        return None;
    }

    let rgba = parts.next()?;
    if rgba.len() != 8 {
        return None;
    }
    let byte = |i: usize| u8::from_str_radix(&rgba[i..i + 2], 16).ok();

    let width: f32 = parts.next()?.parse().ok()?;
    if !width.is_finite() || width <= 0.0 {
        return None;
    }

    let flag = |s: Option<&str>| matches!(s, Some("1"));
    let fx = flag(parts.next());
    let fy = flag(parts.next());

    Some((kind, byte(0)?, byte(2)?, byte(4)?, byte(6)?, width, fx, fy))
}

/// One shape to add, in render-pixel space.
///
/// Carries the drag's START and END rather than a normalized rectangle, because
/// a line and an arrow have DIRECTION: an arrow drawn right-to-left points
/// left, and a rectangle built from min/max would have thrown that away. The
/// rectangle and ellipse kinds normalize the two corners themselves.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ShapeSpec {
    pub page_index: i32,
    pub kind: i32,
    pub x1: f32,
    pub y1: f32,
    pub x2: f32,
    pub y2: f32,
    pub r: u8,
    pub g: u8,
    pub b: u8,
    pub a: u8,
    pub width_px: f32,
}

/// The two barb endpoints of an arrowhead at (`tip_x`, `tip_y`), for a shaft
/// coming from (`tail_x`, `tail_y`).
///
/// Split out and tested on its own because it is the only part of a shape that
/// is not simply its bounding box, and because the app draws the same head in
/// its live preview: two implementations of one piece of geometry is two things
/// that can disagree on screen.
fn arrow_head(tail_x: f32, tail_y: f32, tip_x: f32, tip_y: f32, width: f32)
    -> ((f32, f32), (f32, f32))
{
    let dx = tip_x - tail_x;
    let dy = tip_y - tail_y;
    let len = (dx * dx + dy * dy).sqrt();

    // A zero-length shaft has no direction to point in. Pick one rather than
    // dividing by zero and writing NaN coordinates into the file.
    let (ux, uy) = if len < 1e-6 { (1.0, 0.0) } else { (dx / len, dy / len) };

    let barb = (width * ARROW_HEAD_SCALE).max(ARROW_HEAD_MIN);
    let (sin, cos) = ARROW_HEAD_ANGLE.sin_cos();

    // Rotate the REVERSED shaft direction by plus and minus the head angle, so
    // the barbs sweep back from the tip.
    let (bx, by) = (-ux * barb, -uy * barb);
    (
        (tip_x + bx * cos - by * sin, tip_y + bx * sin + by * cos),
        (tip_x + bx * cos + by * sin, tip_y - bx * sin + by * cos),
    )
}

/// Adds rectangles, ellipses, lines and arrows as real PDF annotation objects.
///
/// All four are `/Ink` annotations carrying one path object. `/Square` would be
/// the better label for a rectangle, and was tried first, but this binding
/// exposes `objects_mut` on ink and stamp annotations ONLY, so a square
/// annotation cannot be given a path to draw. A square with no path relies on
/// the viewer synthesizing an appearance from `/C` and `/BS`, which is exactly
/// the kind of assumption that made created highlights render nothing at all.
/// Ink is the path this app has already proven draws, in this app and after a
/// round trip.
///
/// The cost is that a shape carries no record of its KIND in the file, so
/// reopening gives back a mark that can be moved and deleted but not resized as
/// a shape. Storing the kind needs a custom annotation key, which this binding
/// does not reach.
///
/// `capture_width` is the render width, in pixels, the coordinates were
/// captured at, matching every other annotation path.
#[unsafe(no_mangle)]
pub extern "C" fn add_shape_annotations(
    doc_handle: u64,
    capture_width: i32,
    specs: *const ShapeSpec,
    spec_count: usize,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if specs.is_null() || spec_count == 0 {
        return STATUS_OK_PDFIUM;
    }

    panic::catch_unwind(|| add_shape_annotations_inner(doc_handle, capture_width, specs, spec_count))
        .unwrap_or(STATUS_PANIC)
}

fn add_shape_annotations_inner(
    doc_handle: u64,
    capture_width: i32,
    specs: *const ShapeSpec,
    spec_count: usize,
) -> i32 {
    use pdfium_render::prelude::*;

    let specs: &[ShapeSpec] = unsafe { std::slice::from_raw_parts(specs, spec_count) };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    for spec in specs {
        if !matches!(spec.kind, SHAPE_RECTANGLE | SHAPE_ELLIPSE | SHAPE_LINE | SHAPE_ARROW) {
            return STATUS_INVALID_INPUT;
        }

        let Ok(mut page) = doc_guard.pages().get(spec.page_index as u16) else {
            continue;
        };

        let page_w = page.width().value;
        if page_w <= 0.0 {
            continue;
        }

        let media = page.boundaries().media().map(|b| b.bounds);
        let (origin_x, origin_top) = match media {
            Ok(b) => (b.left().value, b.top().value),
            Err(_) => (0.0, page.height().value),
        };
        let scale = page_w / capture_width as f32;

        let to_pdf_x = |x: f32| origin_x + x * scale;
        let to_pdf_y = |y: f32| origin_top - y * scale;

        let color = PdfColor::new(spec.r, spec.g, spec.b, spec.a);
        let width_pts = (spec.width_px * scale).max(0.1);

        let (x1, y1) = (to_pdf_x(spec.x1), to_pdf_y(spec.y1));
        let (x2, y2) = (to_pdf_x(spec.x2), to_pdf_y(spec.y2));

        // Every point the shape actually touches, so the bounds can contain it.
        // PDFium CLIPS an annotation's appearance to its box, so a box that
        // merely spans the drag would shave off the outer half of the stroke,
        // and for an arrow it would cut the barbs clean off.
        let mut extent: Vec<(f32, f32)> = vec![(x1, y1), (x2, y2)];

        let path = match spec.kind {
            SHAPE_RECTANGLE | SHAPE_ELLIPSE => {
                let rect = PdfRect::new(
                    PdfPoints::new(y1.min(y2)),
                    PdfPoints::new(x1.min(x2)),
                    PdfPoints::new(y1.max(y2)),
                    PdfPoints::new(x1.max(x2)),
                );
                let build = if spec.kind == SHAPE_RECTANGLE {
                    PdfPagePathObject::new_rect
                } else {
                    PdfPagePathObject::new_ellipse
                };
                // Stroke only, no fill: a filled shape hides the page under it,
                // and an outline is what marking up a document calls for.
                build(&doc_guard, rect, Some(color), Some(PdfPoints::new(width_pts)), None)
            }
            _ => {
                let mut p = PdfPagePathObject::new_line(
                    &doc_guard,
                    PdfPoints::new(x1),
                    PdfPoints::new(y1),
                    PdfPoints::new(x2),
                    PdfPoints::new(y2),
                    color,
                    PdfPoints::new(width_pts),
                );

                if spec.kind == SHAPE_ARROW {
                    if let Ok(path) = p.as_mut() {
                        let (left, right) = arrow_head(x1, y1, x2, y2, width_pts);
                        extent.push(left);
                        extent.push(right);

                        // One continuous polyline: tip, barb, back to the tip,
                        // other barb. Retracing the tip costs nothing on a
                        // stroked path and avoids needing a second subpath.
                        let ok = path.line_to(PdfPoints::new(left.0), PdfPoints::new(left.1)).is_ok()
                            && path.line_to(PdfPoints::new(x2), PdfPoints::new(y2)).is_ok()
                            && path.line_to(PdfPoints::new(right.0), PdfPoints::new(right.1)).is_ok();
                        if !ok {
                            continue;
                        }
                    }
                }
                p
            }
        };

        let Ok(path) = path else {
            continue;
        };

        let pad = width_pts / 2.0 + 1.0;
        let min_x = extent.iter().map(|p| p.0).fold(f32::MAX, f32::min) - pad;
        let max_x = extent.iter().map(|p| p.0).fold(f32::MIN, f32::max) + pad;
        let min_y = extent.iter().map(|p| p.1).fold(f32::MAX, f32::min) - pad;
        let max_y = extent.iter().map(|p| p.1).fold(f32::MIN, f32::max) + pad;

        let bounds = PdfRect::new(
            PdfPoints::new(min_y),
            PdfPoints::new(min_x),
            PdfPoints::new(max_y),
            PdfPoints::new(max_x),
        );

        let Ok(mut annotation) = page.annotations_mut().create_ink_annotation() else {
            return STATUS_INVALID_INPUT;
        };

        // Bounds BEFORE objects: PDFium builds the appearance form from the
        // rect, and appending to an annotation with no rect yet crashes inside
        // the library rather than failing.
        if annotation.set_bounds(bounds).is_err() {
            return STATUS_INVALID_INPUT;
        }

        let _ = annotation.set_stroke_color(color);

        // Records what this mark IS, so a reopened file still knows. Without
        // it a shape is just ink, and can then only be moved or deleted.
        let _ = annotation.set_contents(&shape_tag(spec, width_pts));

        if annotation.objects_mut().add_path_object(path).is_err() {
            return STATUS_INVALID_INPUT;
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Resizes a shape by redrawing it inside a new rectangle.
///
/// A shape is the one mark that can be resized exactly, because it is fully
/// described by its kind and its box. Ink cannot: an arbitrary point cloud has
/// no such description, which is why scaling one is refused. Stamps get there
/// only by pulling their own image back out and re-placing it.
///
/// The old annotation is removed and a new one built, so `out_new_index`
/// reports where the replacement landed; it may differ from `index`.
#[unsafe(no_mangle)]
pub extern "C" fn resize_shape_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if right <= left || bottom <= top {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        resize_shape_annotation_inner(
            doc_handle, page_index, index, capture_width, left, top, right, bottom, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
fn resize_shape_annotation_inner(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    out_new_index: *mut i32,
) -> i32 {
    use pdfium_render::prelude::*;

    let tag = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return STATUS_INVALID_INPUT;
        };
        let doc_guard = lock(&doc);
        let Ok(page) = doc_guard.pages().get(page_index as u16) else {
            return STATUS_INVALID_INPUT;
        };
        // Reached by position rather than by the collection's own index type,
        // which is not nameable from here.
        let Some(annotation) = page.annotations().iter().nth(index as usize) else {
            return STATUS_INVALID_INPUT;
        };

        // Contents is read BEFORE anything is removed, so a mark that turns out
        // not to be one of ours leaves the page untouched.
        match annotation.contents().as_deref().and_then(parse_shape_tag) {
            Some(t) => t,
            None => return STATUS_UNSUPPORTED,
        }
    };

    let (kind, r, g, b, a, width_pts, fx, fy) = tag;

    if delete_annotation(doc_handle, page_index, index) != STATUS_OK_PDFIUM {
        return STATUS_INVALID_INPUT;
    }

    // The stored corner flags put the drag back the way round it was drawn, so
    // an arrow resized by its opposite corner does not flip.
    let (x1, x2) = if fx { (left, right) } else { (right, left) };
    let (y1, y2) = if fy { (top, bottom) } else { (bottom, top) };

    // Width was stored in PDF points and the spec wants capture-space pixels,
    // so it goes back through the same scale the writer applied.
    let width_px = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return STATUS_INVALID_INPUT;
        };
        let doc_guard = lock(&doc);
        let Ok(page) = doc_guard.pages().get(page_index as u16) else {
            return STATUS_INVALID_INPUT;
        };
        let page_w = page.width().value;
        if page_w <= 0.0 {
            return STATUS_INVALID_INPUT;
        }
        width_pts * capture_width as f32 / page_w
    };

    let spec = ShapeSpec {
        page_index,
        kind,
        x1,
        y1,
        x2,
        y2,
        r,
        g,
        b,
        a,
        width_px,
    };

    let status = add_shape_annotations(doc_handle, capture_width, &spec, 1);
    if status != STATUS_OK_PDFIUM {
        return status;
    }

    // The rebuild is appended, so it is the last annotation on the page.
    if !out_new_index.is_null() {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        if let Some(doc) = doc {
            let doc_guard = lock(&doc);
            if let Ok(page) = doc_guard.pages().get(page_index as u16) {
                let count = page.annotations().len() as i32;
                unsafe { *out_new_index = count - 1 };
            }
        }
    }

    STATUS_OK_PDFIUM
}

/// Adds freehand strokes as real PDF `/Ink` annotation objects.
///
/// One annotation per stroke, each holding a single path object, so a stroke
/// stays one thing to select, move, recolour or delete. Takes the same stroke
/// and point layout as the burn path, so the only difference at the call site
/// is which function you call.
#[unsafe(no_mangle)]
pub extern "C" fn add_ink_annotations(
    doc_handle: u64,
    capture_width: i32,
    strokes: *const BurnStroke,
    stroke_count: usize,
    points: *const BurnPoint,
    point_count: usize,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if strokes.is_null() || stroke_count == 0 {
        return STATUS_OK_PDFIUM;
    }
    if points.is_null() && point_count > 0 {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        add_ink_annotations_inner(doc_handle, capture_width, strokes, stroke_count, points, point_count)
    })
    .unwrap_or(STATUS_PANIC)
}

fn add_ink_annotations_inner(
    doc_handle: u64,
    capture_width: i32,
    strokes: *const BurnStroke,
    stroke_count: usize,
    points: *const BurnPoint,
    point_count: usize,
) -> i32 {
    use pdfium_render::prelude::*;

    let strokes: &[BurnStroke] = unsafe { std::slice::from_raw_parts(strokes, stroke_count) };
    let points: &[BurnPoint] = if point_count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(points, point_count) }
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    for stroke in strokes {
        let start = stroke.point_offset as usize;
        let end = start.saturating_add(stroke.point_count as usize);

        // A stroke needs two points to be a line at all, and the offset/count
        // pair indexes a shared flat array, so a bad pair would read past its
        // end.
        if stroke.point_count < 2 || end > points.len() {
            return STATUS_INVALID_INPUT;
        }
        let pts = &points[start..end];

        let Ok(mut page) = doc_guard.pages().get(stroke.page_index as u16) else {
            continue;
        };

        let page_w = page.width().value;
        if page_w <= 0.0 {
            continue;
        }

        let media = page.boundaries().media().map(|b| b.bounds);
        let (origin_x, origin_top) = match media {
            Ok(b) => (b.left().value, b.top().value),
            Err(_) => (0.0, page.height().value),
        };
        let scale = page_w / capture_width as f32;

        let to_pdf_x = |x: f32| PdfPoints::new(origin_x + x * scale);
        let to_pdf_y = |y: f32| PdfPoints::new(origin_top - y * scale);

        let color = PdfColor::new(stroke.r, stroke.g, stroke.b, stroke.a);
        let width_pts = (stroke.width_px * scale).max(0.1);

        let Ok(mut path) = PdfPagePathObject::new_line(
            &doc_guard,
            to_pdf_x(pts[0].x),
            to_pdf_y(pts[0].y),
            to_pdf_x(pts[1].x),
            to_pdf_y(pts[1].y),
            color,
            PdfPoints::new(width_pts),
        ) else {
            continue;
        };

        let mut ok = true;
        for p in &pts[2..] {
            if path.line_to(to_pdf_x(p.x), to_pdf_y(p.y)).is_err() {
                ok = false;
                break;
            }
        }
        if !ok {
            continue;
        }

        // Bounds must actually contain the stroke, padded by half the pen
        // width. PDFium clips an annotation's appearance to its box, so a box
        // measured from the centre line alone would shave the outer edge off
        // every stroke, worst at the thickest pen.
        let (mut min_x, mut max_x) = (f32::MAX, f32::MIN);
        let (mut min_y, mut max_y) = (f32::MAX, f32::MIN);
        for p in pts {
            let x = origin_x + p.x * scale;
            let y = origin_top - p.y * scale;
            min_x = min_x.min(x);
            max_x = max_x.max(x);
            min_y = min_y.min(y);
            max_y = max_y.max(y);
        }
        let pad = width_pts / 2.0 + 1.0;

        let bounds = PdfRect::new(
            PdfPoints::new(min_y - pad),
            PdfPoints::new(min_x - pad),
            PdfPoints::new(max_y + pad),
            PdfPoints::new(max_x + pad),
        );

        let Ok(mut annotation) = page.annotations_mut().create_ink_annotation() else {
            return STATUS_INVALID_INPUT;
        };

        // Bounds BEFORE objects. PDFium builds the annotation's appearance
        // form from its rect, and appending an object to an annotation that
        // has no rect yet crashes inside the library rather than failing.
        if annotation.set_bounds(bounds).is_err() {
            return STATUS_INVALID_INPUT;
        }

        let _ = annotation.set_stroke_color(color);

        if annotation.objects_mut().add_path_object(path).is_err() {
            return STATUS_INVALID_INPUT;
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Adds highlights as real PDF `/Highlight` annotation objects.
///
/// The editable counterpart to burning them into page content. A burned
/// highlight is pixels: reopening the file gives you something that cannot be
/// moved, recoloured or removed, and no other viewer sees it as markup. An
/// annotation stays an object, so it round-trips through this app, Acrobat and
/// anything else that reads PDF.
///
/// `capture_width` is the render width, in pixels, the coordinates were
/// captured at, matching the burn and note paths.
#[unsafe(no_mangle)]
pub extern "C" fn add_highlight_annotations(
    doc_handle: u64,
    capture_width: i32,
    specs: *const HighlightSpec,
    spec_count: usize,
    quads: *const HighlightQuad,
    quad_count: usize,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if specs.is_null() || spec_count == 0 {
        return STATUS_OK_PDFIUM;
    }
    if quads.is_null() && quad_count > 0 {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        add_highlight_annotations_inner(doc_handle, capture_width, specs, spec_count, quads, quad_count)
    })
    .unwrap_or(STATUS_PANIC)
}

fn add_highlight_annotations_inner(
    doc_handle: u64,
    capture_width: i32,
    specs: *const HighlightSpec,
    spec_count: usize,
    quads: *const HighlightQuad,
    quad_count: usize,
) -> i32 {
    use pdfium_render::prelude::*;

    let specs: &[HighlightSpec] = unsafe { std::slice::from_raw_parts(specs, spec_count) };
    let quads: &[HighlightQuad] = if quad_count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(quads, quad_count) }
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    for spec in specs {
        let start = spec.quad_offset as usize;
        let end = start + spec.quad_count as usize;
        if spec.quad_count == 0 || end > quads.len() {
            return STATUS_INVALID_INPUT;
        }

        let Ok(mut page) = doc_guard.pages().get(spec.page_index as u16) else {
            continue;
        };

        let page_w = page.width().value;
        if page_w <= 0.0 {
            continue;
        }

        // Page origin from the media box, not assumed to be zero, so the
        // geometry matches what get_annotations reads back.
        let media = page.boundaries().media().map(|b| b.bounds);
        let (origin_x, origin_top) = match media {
            Ok(b) => (b.left().value, b.top().value),
            Err(_) => (0.0, page.height().value),
        };
        let scale = page_w / capture_width as f32;

        // Convert every quad first, so the bounding box is known before the
        // annotation exists. Order matters here: PDFium builds a markup
        // annotation's appearance from its rectangle, colour and quad points,
        // and setting them after the fact does not make it regenerate.
        let mut rects = Vec::with_capacity(end - start);
        let (mut min_x, mut max_x) = (f32::MAX, f32::MIN);
        let (mut min_y, mut max_y) = (f32::MAX, f32::MIN);

        for quad in &quads[start..end] {
            // Capture space is top-left origin and Y-down; PDF is bottom-left
            // and Y-up.
            let left = origin_x + quad.left * scale;
            let right = origin_x + quad.right * scale;
            let top = origin_top - quad.top * scale;
            let bottom = origin_top - quad.bottom * scale;

            min_x = min_x.min(left);
            max_x = max_x.max(right);
            min_y = min_y.min(bottom);
            max_y = max_y.max(top);

            rects.push(PdfRect::new(
                PdfPoints::new(bottom),
                PdfPoints::new(left),
                PdfPoints::new(top),
                PdfPoints::new(right),
            ));
        }

        let Ok(mut annotation) = page.annotations_mut().create_highlight_annotation() else {
            return STATUS_INVALID_INPUT;
        };

        let bounds = PdfRect::new(
            PdfPoints::new(min_y),
            PdfPoints::new(min_x),
            PdfPoints::new(max_y),
            PdfPoints::new(max_x),
        );
        if annotation.set_bounds(bounds).is_err() {
            return STATUS_INVALID_INPUT;
        }

        // set_STROKE_color, despite this being a fill, because that is the one
        // that writes the annotation's /C entry. set_fill_color writes /IC,
        // the interior colour, and PDFium generates a markup annotation's
        // appearance from /C.
        if annotation
            .set_stroke_color(PdfColor::new(spec.r, spec.g, spec.b, spec.a))
            .is_err()
        {
            return STATUS_INVALID_INPUT;
        }

        for rect in &rects {
            // Corners in the order PDF actually defines for /QuadPoints:
            // top-left, top-right, bottom-left, bottom-right.
            //
            // NOT PdfQuadPoints::from_rect, which winds them counter-clockwise
            // from the bottom-left instead. PDFium reads pair 1 as the
            // top-right corner and pair 2 as the bottom-left, so a rectangle
            // built that way hands it two corners with the same x, it computes
            // a zero-width rectangle, and the highlight draws NOTHING at all
            // while remaining a perfectly well formed annotation with correct
            // bounds, colour and quad count. That is what it did.
            let quad = PdfQuadPoints::new_from_values(
                rect.left().value,  rect.top().value,     // top-left
                rect.right().value, rect.top().value,     // top-right
                rect.left().value,  rect.bottom().value,  // bottom-left
                rect.right().value, rect.bottom().value,  // bottom-right
            );

            if annotation
                .attachment_points_mut()
                .create_attachment_point_at_end(quad)
                .is_err()
            {
                return STATUS_INVALID_INPUT;
            }
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

#[unsafe(no_mangle)]
pub extern "C" fn add_note_annotations(
    doc_handle: u64,
    capture_width: i32,
    notes: *const BurnNote,
    note_count: usize,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if notes.is_null() || note_count == 0 {
        return STATUS_OK_PDFIUM;
    }

    panic::catch_unwind(|| add_note_annotations_inner(doc_handle, capture_width, notes, note_count))
        .unwrap_or(STATUS_PANIC)
}

fn add_note_annotations_inner(
    doc_handle: u64,
    capture_width: i32,
    notes: *const BurnNote,
    note_count: usize,
) -> i32 {
    use pdfium_render::prelude::*;

    let notes: &[BurnNote] = unsafe { std::slice::from_raw_parts(notes, note_count) };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    // Standard sticky-note icon box, in points.
    const NOTE_SIZE: f32 = 20.0;

    for note in notes {
        let Ok(mut page) = doc_guard.pages().get(note.page_index as u16) else {
            continue;
        };

        let page_width = page.width().value;
        let page_height = page.height().value;
        if page_width <= 0.0 {
            continue;
        }
        let scale = page_width / capture_width as f32;

        let text = if note.text.is_null() {
            String::new()
        } else {
            match unsafe { CStr::from_ptr(note.text) }.to_str() {
                Ok(s) => s.to_owned(),
                Err(_) => continue,
            }
        };

        // Same vertical flip as the burn path: capture space is top-left
        // origin, PDF is bottom-left.
        let x = note.x * scale;
        let y = page_height - note.y * scale;

        let Ok(mut annotation) = page.annotations_mut().create_text_annotation(&text) else {
            return STATUS_INVALID_INPUT;
        };

        // The icon hangs DOWN from the anchor, so the click point stays the
        // top-left of the marker and matches where the user placed it.
        let bounds = PdfRect::new(
            PdfPoints::new(y - NOTE_SIZE),
            PdfPoints::new(x),
            PdfPoints::new(y),
            PdfPoints::new(x + NOTE_SIZE),
        );
        if annotation.set_bounds(bounds).is_err() {
            return STATUS_INVALID_INPUT;
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Flattens highlights and ink strokes into page content.
///
/// `capture_width` is the render width, in pixels, the annotation
/// coordinates were captured at. Pass 0 pointers/counts for a category with
/// nothing to burn. Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT or
/// STATUS_PANIC.
///
/// Callers must save to a NEW file after this and then reload the document,
/// otherwise a second save re-burns the same annotations on top of the
/// already-burned ones.
#[unsafe(no_mangle)]
pub extern "C" fn burn_annotations(
    doc_handle: u64,
    capture_width: i32,
    rects: *const BurnRect,
    rect_count: usize,
    strokes: *const BurnStroke,
    stroke_count: usize,
    points: *const BurnPoint,
    point_count: usize,
) -> i32 {
    if doc_handle == 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        burn_annotations_inner(
            doc_handle,
            capture_width,
            rects,
            rect_count,
            strokes,
            stroke_count,
            points,
            point_count,
        )
    })
    .unwrap_or(STATUS_PANIC)
}

fn burn_annotations_inner(
    doc_handle: u64,
    capture_width: i32,
    rects: *const BurnRect,
    rect_count: usize,
    strokes: *const BurnStroke,
    stroke_count: usize,
    points: *const BurnPoint,
    point_count: usize,
) -> i32 {
    use pdfium_render::prelude::*;

    let rects: &[BurnRect] = if rects.is_null() || rect_count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(rects, rect_count) }
    };
    let strokes: &[BurnStroke] = if strokes.is_null() || stroke_count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(strokes, stroke_count) }
    };
    let points: &[BurnPoint] = if points.is_null() || point_count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(points, point_count) }
    };

    if rects.is_empty() && strokes.is_empty() {
        return STATUS_OK_PDFIUM;
    }

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    // Group work by page so each page is opened once.
    let mut pages: Vec<i32> = rects.iter().map(|r| r.page_index).collect();
    pages.extend(strokes.iter().map(|s| s.page_index));
    pages.sort_unstable();
    pages.dedup();

    for page_index in pages {
        let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
            continue;
        };

        let page_width = page.width().value;
        let page_height = page.height().value;
        if page_width <= 0.0 {
            continue;
        }
        let scale = page_width / capture_width as f32;

        // Render pixels to PDF points, flipping the vertical axis.
        let to_pdf_x = |x: f32| PdfPoints::new(x * scale);
        let to_pdf_y = |y: f32| PdfPoints::new(page_height - y * scale);

        for rect in rects.iter().filter(|r| r.page_index == page_index) {
            let bounds = PdfRect::new(
                to_pdf_y(rect.bottom),
                to_pdf_x(rect.left),
                to_pdf_y(rect.top),
                to_pdf_x(rect.right),
            );
            let fill = PdfColor::new(rect.r, rect.g, rect.b, rect.a);
            if page
                .objects_mut()
                .create_path_object_rect(bounds, None, None, Some(fill))
                .is_err()
            {
                return STATUS_INVALID_INPUT;
            }
        }

        for stroke in strokes.iter().filter(|s| s.page_index == page_index) {
            let start = stroke.point_offset as usize;
            let end = start.saturating_add(stroke.point_count as usize);
            if stroke.point_count < 2 || end > points.len() {
                continue;
            }
            let pts = &points[start..end];

            let color = PdfColor::new(stroke.r, stroke.g, stroke.b, stroke.a);
            let width = PdfPoints::new((stroke.width_px * scale).max(0.1));

            // Build one path per stroke: move to the first point, then line
            // to each subsequent one, so the stroke stays a single object
            // rather than N disconnected segments.
            let Ok(mut path) = PdfPagePathObject::new_line(
                &doc_guard,
                to_pdf_x(pts[0].x),
                to_pdf_y(pts[0].y),
                to_pdf_x(pts[1].x),
                to_pdf_y(pts[1].y),
                color,
                width,
            ) else {
                continue;
            };

            let mut ok = true;
            for p in &pts[2..] {
                if path.line_to(to_pdf_x(p.x), to_pdf_y(p.y)).is_err() {
                    ok = false;
                    break;
                }
            }
            if !ok {
                continue;
            }

            if page.objects_mut().add_path_object(path).is_err() {
                return STATUS_INVALID_INPUT;
            }
        }

        // Content must be regenerated for the new objects to appear in the
        // saved bytes; the default strategy does not do it per change.
        if page.regenerate_content().is_err() {
            return STATUS_INVALID_INPUT;
        }
    }

    drop(doc_guard);
    drop(doc);

    // Burned pages look different now, so nothing cached for this document
    // is still valid.
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

fn evict_page_cache(doc_handle: u64, page_index: i32) {
    let mut cache = lock(&core().cache);
    let stale: Vec<TileKey> =
        cache.iter().filter(|(k, _)| k.doc == doc_handle && k.page == page_index).map(|(k, _)| *k).collect();
    for key in stale {
        cache.pop(&key);
    }
}

fn evict_all_cache_for_doc(doc_handle: u64) {
    let mut cache = lock(&core().cache);
    let stale: Vec<TileKey> = cache.iter().filter(|(k, _)| k.doc == doc_handle).map(|(k, _)| *k).collect();
    for key in stale {
        cache.pop(&key);
    }
}

// ---------------------------------------------------------------------
// AcroForm fill: enumerate + fill text fields. Uses the plain-text-search
// approach (match by field name) rather than PDFium's own stateful search
// cursor, keeping the FFI surface to two simple calls.
// ---------------------------------------------------------------------

/// Counts form fields (of any type) across every page. 0 for a document
/// with no AcroForm; -1 for an unknown handle.
#[unsafe(no_mangle)]
pub extern "C" fn get_form_field_count(doc_handle: u64) -> i32 {
    if doc_handle == 0 {
        return -1;
    }
    panic::catch_unwind(|| get_form_field_count_inner(doc_handle)).unwrap_or(-1)
}

fn get_form_field_count_inner(doc_handle: u64) -> i32 {
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return -1;
    };

    let doc_guard = lock(&doc);
    let mut count = 0i32;
    for page in doc_guard.pages().iter() {
        for i in 0..page.annotations().len() {
            if let Ok(annotation) = page.annotations().get(i) {
                if annotation.as_form_field().is_some() {
                    count += 1;
                }
            }
        }
    }
    count
}

/// Sets a text form field's value by field name (the PDF's /T entry).
/// Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT (bad handle/args, no such
/// field, or it isn't a text field), or STATUS_PANIC.
#[unsafe(no_mangle)]
pub extern "C" fn fill_text_field(doc_handle: u64, field_name: *const c_char, value: *const c_char) -> i32 {
    if doc_handle == 0 || field_name.is_null() || value.is_null() {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| fill_text_field_inner(doc_handle, field_name, value)).unwrap_or(STATUS_PANIC)
}

fn fill_text_field_inner(doc_handle: u64, field_name: *const c_char, value: *const c_char) -> i32 {
    use pdfium_render::prelude::PdfFormFieldCommon;

    let Ok(name_str) = (unsafe { CStr::from_ptr(field_name) }).to_str() else {
        return STATUS_INVALID_INPUT;
    };
    let Ok(value_str) = (unsafe { CStr::from_ptr(value) }).to_str() else {
        return STATUS_INVALID_INPUT;
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = lock(&doc);
    let _ = doc_guard.form(); // ensure the form-fill environment is bound before touching fields

    for page in doc_guard.pages().iter() {
        for i in 0..page.annotations().len() {
            let Ok(mut annotation) = page.annotations().get(i) else {
                continue;
            };
            let Some(field) = annotation.as_form_field_mut() else {
                continue;
            };
            if field.name().as_deref() != Some(name_str) {
                continue;
            }
            let Some(text_field) = field.as_text_field_mut() else {
                continue;
            };
            return match text_field.set_value(value_str) {
                Ok(()) => STATUS_OK_PDFIUM,
                Err(_) => STATUS_INVALID_INPUT,
            };
        }
    }

    STATUS_INVALID_INPUT
}

// ---------------------------------------------------------------------
// Text layer: per-character Unicode codepoint + bounding box, in the same
// render-pixel space as a bitmap rendered at `target_width` (top-left
// origin, Y down) — everything a caller needs to build text selection and
// search highlighting without further PDFium calls.
// ---------------------------------------------------------------------

// ---------------------------------------------------------------------
// Reading the annotations a document already carries.
//
// Until this existed the app could only ever see marks it had made itself
// in the current session: highlights and ink were flattened into the page
// content on save, so reopening your own file gave you pixels, and a file
// annotated in Acrobat looked completely unmarked. Everything editable
// about an annotation starts with being able to read it back.
// ---------------------------------------------------------------------

/// The annotation kinds the app can display and edit. Anything else in the
/// file is reported as Other so it can be listed and left strictly alone
/// rather than silently dropped on the next save.
pub const ANNOT_OTHER: i32 = 0;
pub const ANNOT_TEXT: i32 = 1;
pub const ANNOT_HIGHLIGHT: i32 = 2;
pub const ANNOT_INK: i32 = 3;
pub const ANNOT_STAMP: i32 = 4;
pub const ANNOT_SQUARE: i32 = 5;
pub const ANNOT_FREE_TEXT: i32 = 6;
pub const ANNOT_UNDERLINE: i32 = 7;
pub const ANNOT_STRIKEOUT: i32 = 8;
pub const ANNOT_SQUIGGLY: i32 = 9;

#[repr(C)]
pub struct AnnotationInfo {
    /// Position in the page's annotation list. This is the handle passed back
    /// to update_annotation and delete_annotation, so it is only valid until
    /// the page's annotations are next added to or removed from.
    pub index: i32,

    /// One of the ANNOT_* constants.
    pub subtype: i32,

    /// Bounds with a TOP-LEFT origin, both axes divided by the page WIDTH.
    ///
    /// The same convention the app normalizes its own annotations into, so
    /// these drop straight into the overlay model. Dividing both axes by the
    /// width (rather than each by its own extent) keeps the scale uniform, so
    /// a square stays square.
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,

    /// 0xRRGGBB, or -1 when the annotation carries no colour of its own.
    pub color: i32,

    /// 0.0 to 1.0, from the colour's alpha. 1.0 when there is no colour.
    pub opacity: f32,
}

#[repr(C)]
pub struct AnnotationArray {
    pub items: *mut AnnotationInfo,
    pub len: usize,
    /// 0 = ok, 1 = invalid input / unknown handle, 2 = panic.
    pub status: i32,
}

impl AnnotationArray {
    fn failure(status: i32) -> Self {
        AnnotationArray { items: std::ptr::null_mut(), len: 0, status }
    }
}

/// Every annotation on a page, in document order.
///
/// A page with no annotations is a successful, EMPTY result, not a failure:
/// most pages of most documents are exactly that, and making it an error
/// would put an error path on the common case.
#[unsafe(no_mangle)]
pub extern "C" fn get_annotations(doc_handle: u64, page_index: i32) -> AnnotationArray {
    if doc_handle == 0 || page_index < 0 {
        return AnnotationArray::failure(STATUS_INVALID_INPUT);
    }

    panic::catch_unwind(|| get_annotations_inner(doc_handle, page_index))
        .unwrap_or_else(|_| AnnotationArray::failure(STATUS_PANIC))
}

fn get_annotations_inner(doc_handle: u64, page_index: i32) -> AnnotationArray {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return AnnotationArray::failure(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);

    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return AnnotationArray::failure(STATUS_INVALID_INPUT);
    };

    let page_w = page.width().value;
    let page_top = page.boundaries().media().map(|b| b.bounds.top().value).unwrap_or(page.height().value);
    let page_left = page.boundaries().media().map(|b| b.bounds.left().value).unwrap_or(0.0);
    if page_w <= 0.0 {
        return AnnotationArray::failure(STATUS_INVALID_INPUT);
    }

    let annotations = page.annotations();
    let mut items = Vec::with_capacity(annotations.len() as usize);

    for i in 0..annotations.len() {
        let Ok(annotation) = annotations.get(i) else {
            continue;
        };

        let subtype = match annotation.annotation_type() {
            PdfPageAnnotationType::Text => ANNOT_TEXT,
            PdfPageAnnotationType::Highlight => ANNOT_HIGHLIGHT,
            PdfPageAnnotationType::Ink => ANNOT_INK,
            PdfPageAnnotationType::Stamp => ANNOT_STAMP,
            PdfPageAnnotationType::Square => ANNOT_SQUARE,
            PdfPageAnnotationType::FreeText => ANNOT_FREE_TEXT,
            PdfPageAnnotationType::Underline => ANNOT_UNDERLINE,
            PdfPageAnnotationType::Strikeout => ANNOT_STRIKEOUT,
            PdfPageAnnotationType::Squiggly => ANNOT_SQUIGGLY,
            _ => ANNOT_OTHER,
        };

        let Ok(bounds) = annotation.bounds() else {
            continue;
        };

        // PDF is bottom-left origin and Y-up; the app is top-left and Y-down.
        let left = (bounds.left().value - page_left) / page_w;
        let right = (bounds.right().value - page_left) / page_w;
        let top = (page_top - bounds.top().value) / page_w;
        let bottom = (page_top - bounds.bottom().value) / page_w;

        // Colour is deliberately NOT queried, for any subtype. Ever.
        //
        // FPDFAnnot_GetColor access-violates on an annotation that has an
        // appearance stream. That sounds avoidable until you know that
        // RENDERING a page GENERATES appearance streams for annotations that
        // lack them. So a freshly created highlight answers the colour query
        // safely, and the same annotation kills the process the moment the
        // page has been drawn once, which in the app is immediately and on a
        // background thread. That timing is why this survived every test that
        // read before rendering and then took the app down in the field: the
        // fault was measured at exactly add -> render -> read, single
        // threaded, round zero.
        //
        // An earlier guard skipped Ink and Stamp because those crashed when
        // measured; that was the right instances and the wrong rule. The rule
        // is appearance streams, this crate's API exposes no way to check for
        // one, and the crash cannot be caught. The only defence is not asking.
        //
        // Costs nothing today: PDFium draws every annotation from its own
        // appearance, and the app keeps its own colours for marks it makes.
        // When a property bar needs a loaded annotation's colour, it must
        // come from a path that never touches FPDFAnnot_GetColor.
        let (color, opacity) = (-1, 1.0);

        items.push(AnnotationInfo {
            index: i as i32,
            subtype,
            left,
            top,
            right,
            bottom,
            color,
            opacity,
        });
    }

    let len = items.len();
    if len == 0 {
        return AnnotationArray { items: std::ptr::null_mut(), len: 0, status: STATUS_OK_PDFIUM };
    }

    let boxed = items.into_boxed_slice();
    AnnotationArray {
        items: Box::into_raw(boxed) as *mut AnnotationInfo,
        len,
        status: STATUS_OK_PDFIUM,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn free_annotation_array(array: AnnotationArray) {
    if array.items.is_null() || array.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(array.items, array.len));
    }
}

#[repr(C)]
pub struct CharInfo {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    /// Unicode scalar value of this character (0 if PDFium reported a value
    /// that isn't a valid Unicode scalar, e.g. an unmapped glyph).
    pub codepoint: u32,
}

#[repr(C)]
pub struct CharInfoArray {
    pub chars: *mut CharInfo,
    pub len: usize,
    /// 0 = ok, 1 = invalid input / unknown handle, 2 = panic, 3 = the page
    /// has no text layer (still a valid, empty result).
    pub status: i32,
}

impl CharInfoArray {
    fn failure(status: i32) -> Self {
        CharInfoArray { chars: std::ptr::null_mut(), len: 0, status }
    }
}

/// Extracts every character's position and codepoint for a page, scaled to
/// `target_width` — the same width a caller would pass to `render_low_res`/
/// `request_high_res` for that page, so the boxes line up with whatever
/// bitmap is currently displayed at that width.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_chars(doc_handle: u64, page_index: i32, target_width: i32) -> CharInfoArray {
    if doc_handle == 0 || target_width <= 0 {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    }

    panic::catch_unwind(|| get_page_chars_inner(doc_handle, page_index, target_width))
        .unwrap_or_else(|_| CharInfoArray::failure(STATUS_PANIC))
}

fn get_page_chars_inner(doc_handle: u64, page_index: i32, target_width: i32) -> CharInfoArray {
    let _guard = lock(&CALL_LOCK);

    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    };

    let doc_guard = lock(&doc);
    let result = extract_char_infos(&doc_guard, page_index, target_width);
    drop(doc_guard);
    drop(doc);

    let Some(infos) = result else {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    };

    let mut boxed = infos.into_boxed_slice();
    let len = boxed.len();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);

    CharInfoArray { chars: ptr, len, status: STATUS_OK_PDFIUM }
}

/// Caller must hold `CALL_LOCK`.
fn extract_char_infos(doc: &PdfDocument<'static>, page_index: i32, target_width: i32) -> Option<Vec<CharInfo>> {
    let page = doc.pages().get(page_index as u16).ok()?;
    let page_height = page.height().value;
    let scale = target_width as f32 / page.width().value;

    let text = page.text().ok()?;
    let chars = text.chars();

    let mut infos = Vec::with_capacity(chars.len() as usize);
    for c in chars.iter() {
        // loose_bounds (the font's full glyph-cell box), not tight_bounds
        // (glyph-shape-specific ink extent): tight boxes vary per glyph — an
        // 'o' and an 'f' on the same line have different heights — which
        // breaks same-line grouping downstream (PageTextLayer.GetRangeRects
        // groups characters by Top). loose_bounds is consistent across a
        // line regardless of which glyphs are on it.
        let Ok(bounds) = c.loose_bounds() else { continue };
        infos.push(CharInfo {
            left: bounds.left().value * scale,
            top: (page_height - bounds.top().value) * scale,
            right: bounds.right().value * scale,
            bottom: (page_height - bounds.bottom().value) * scale,
            codepoint: c.unicode_value(),
        });
    }

    Some(infos)
}

/// Frees an array returned by `get_page_chars`. Safe to call on a
/// zeroed/failed array (null pointer is a no-op).
#[unsafe(no_mangle)]
pub extern "C" fn free_char_info_array(array: CharInfoArray) {
    if array.chars.is_null() || array.len == 0 {
        return;
    }
    unsafe {
        let _ = Box::from_raw(std::slice::from_raw_parts_mut(array.chars, array.len));
    }
}

// ---------------------------------------------------------------------
// Low-res tier: cache-first, synchronous, expected to be fast.
// ---------------------------------------------------------------------

#[unsafe(no_mangle)]
pub extern "C" fn render_low_res(doc_handle: u64, page_index: i32, target_width: i32) -> RenderResult {
    if target_width <= 0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| render_low_res_inner(doc_handle, page_index, target_width))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_low_res_inner(doc_handle: u64, page_index: i32, target_width: i32) -> RenderResult {
    let key = TileKey::page_key(doc_handle, page_index, Tier::Low, target_width);

    if let Some(tile) = lock(&core().cache).get(&key) {
        return tile_to_result(tile, STATUS_OK_PDFIUM);
    }

    let rendered = {
        // Hold CALL_LOCK across both the render call and the drop of our
        // Arc clone below — dropping the last reference to a PdfDocument
        // runs PDFium's native close, which needs the same serialization as
        // any other native call.
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let result = doc.as_ref().and_then(|d| {
            let guard = lock(&d);
            render_page_via_pdfium(&guard, page_index, target_width)
        });
        drop(doc);
        result
    };

    match rendered {
        Some((width, height, bytes)) => {
            let tile = CachedTile { width, height, bytes: Arc::from(bytes) };
            let result = tile_to_result(&tile, STATUS_OK_PDFIUM);
            cache_put(key, tile);
            result
        }
        None => {
            let height = placeholder_height(target_width);
            buffer_to_result(target_width, height, render_placeholder_buffer(target_width, height), STATUS_OK_PLACEHOLDER)
        }
    }
}

/// Renders a page WITHOUT touching the tile cache, in either direction: it
/// neither serves from the cache nor stores what it produces.
///
/// This is the sharpening tier for the continuous viewport. Those bitmaps are
/// huge (a page at deep zoom can be tens of megabytes) and are wanted for
/// exactly as long as the page is near the viewport, so caching them would
/// evict the entire modest base tier to hold a couple of pages the user is
/// about to scroll past. Bypassing the cache keeps the sharpening tier's
/// memory owned by the caller, which drops it the moment the page leaves.
#[unsafe(no_mangle)]
pub extern "C" fn render_uncached(doc_handle: u64, page_index: i32, target_width: i32) -> RenderResult {
    if target_width <= 0 {
        return RenderResult::failure(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| render_uncached_inner(doc_handle, page_index, target_width))
        .unwrap_or_else(|_| RenderResult::failure(STATUS_PANIC))
}

fn render_uncached_inner(doc_handle: u64, page_index: i32, target_width: i32) -> RenderResult {
    let rendered = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let result = doc.as_ref().and_then(|d| {
            let guard = lock(&d);
            render_page_via_pdfium(&guard, page_index, target_width)
        });
        drop(doc);
        result
    };

    match rendered {
        Some((width, height, bytes)) => buffer_to_result(width, height, bytes, STATUS_OK_PDFIUM),
        None => RenderResult::failure(STATUS_INVALID_INPUT),
    }
}

// ---------------------------------------------------------------------
// High-res tier: async, cancellable.
// ---------------------------------------------------------------------

/// Enqueues a full-fidelity render on a background thread and returns a
/// request id to poll. Any earlier request for the same (doc, page) —
/// in-flight or still queued — is superseded: its eventual result, if any,
/// is discarded rather than delivered. Returns 0 on invalid input.
#[unsafe(no_mangle)]
pub extern "C" fn request_high_res(doc_handle: u64, page_index: i32, target_width: i32) -> u64 {
    if doc_handle == 0 || target_width <= 0 {
        return 0;
    }

    let core = core();
    let generation = core.next_generation.fetch_add(1, Ordering::Relaxed);
    lock(&core.generations).insert((doc_handle, page_index), generation);

    let request_id = core.next_request_id.fetch_add(1, Ordering::Relaxed);
    let key = TileKey::page_key(doc_handle, page_index, Tier::High, target_width);

    // Fast path: already cached at this exact width, no thread needed.
    if let Some(tile) = lock(&core.cache).get(&key) {
        lock(&core.requests).insert(request_id, RequestSlot::Ready(tile.clone()));
        return request_id;
    }

    lock(&core.requests).insert(
        request_id,
        RequestSlot::Pending { doc: doc_handle, page: page_index, generation },
    );

    let doc_arc = lock(&core.documents).get(&doc_handle).cloned();

    thread::spawn(move || {
        let rendered = {
            // Same reasoning as render_low_res_inner: hold CALL_LOCK across
            // both the render call and the drop of doc_arc at the end of
            // this scope, since dropping the last Arc<PdfDocument> reference
            // runs PDFium's native close.
            let _guard = lock(&CALL_LOCK);
            let result = doc_arc.as_ref().and_then(|doc| {
                let guard = lock(&doc);
                render_page_via_pdfium(&guard, page_index, target_width)
            });
            drop(doc_arc);
            result
        };

        let still_current =
            lock(&core.generations).get(&(doc_handle, page_index)).copied() == Some(generation);

        let slot = match rendered {
            Some((width, height, bytes)) => {
                let tile = CachedTile { width, height, bytes: Arc::from(bytes) };
                cache_put(key, tile.clone());
                if still_current { RequestSlot::Ready(tile) } else { RequestSlot::Cancelled }
            }
            None => RequestSlot::Cancelled,
        };

        let mut requests = lock(&core.requests);
        if matches!(requests.get(&request_id), Some(RequestSlot::Discarded)) {
            // The caller gave up on this id while we were rendering; drop
            // the tombstone now instead of resurrecting it as Ready/Cancelled.
            requests.remove(&request_id);
        } else {
            requests.insert(request_id, slot);
        }
    });

    request_id
}

/// Polls a request from `request_high_res`. Writes into `*out_result` and
/// returns `POLL_READY` exactly once per request (the entry is consumed);
/// `POLL_PENDING` means keep polling; `POLL_CANCELLED` means a newer request
/// superseded this one (stop polling, nothing will ever arrive);
/// `POLL_UNKNOWN_REQUEST` means the id was never issued or was already
/// consumed.
#[unsafe(no_mangle)]
pub extern "C" fn poll_high_res(request_id: u64, out_result: *mut RenderResult) -> i32 {
    if out_result.is_null() {
        return POLL_UNKNOWN_REQUEST;
    }

    let core = core();

    enum Snapshot {
        Unknown,
        StillPending,
        NewlyStale,
        Cancelled,
        Ready(CachedTile),
    }

    let snapshot = {
        let requests = lock(&core.requests);
        match requests.get(&request_id) {
            None => Snapshot::Unknown,
            Some(RequestSlot::Cancelled) | Some(RequestSlot::Discarded) => Snapshot::Cancelled,
            Some(RequestSlot::Ready(tile)) => Snapshot::Ready(tile.clone()),
            Some(RequestSlot::Pending { doc, page, generation }) => {
                let still_current =
                    lock(&core.generations).get(&(*doc, *page)).copied() == Some(*generation);
                if still_current { Snapshot::StillPending } else { Snapshot::NewlyStale }
            }
        }
    };

    match snapshot {
        Snapshot::Unknown => POLL_UNKNOWN_REQUEST,
        Snapshot::StillPending => POLL_PENDING,
        Snapshot::Cancelled => {
            lock(&core.requests).remove(&request_id);
            POLL_CANCELLED
        }
        Snapshot::NewlyStale => {
            lock(&core.requests).insert(request_id, RequestSlot::Cancelled);
            POLL_CANCELLED
        }
        Snapshot::Ready(tile) => {
            lock(&core.requests).remove(&request_id);
            let result = tile_to_result(&tile, STATUS_OK_PDFIUM);
            unsafe {
                *out_result = result;
            }
            POLL_READY
        }
    }
}

/// Drops a request's bookkeeping without needing its result — for when the
/// caller no longer cares (e.g. it navigated to a different page before this
/// one's high-res render arrived). Without this, an abandoned `Ready` slot
/// would sit in the requests map forever, holding its rendered bitmap alive.
/// No-op for an unknown or already-consumed id.
///
/// If the request is still `Pending`, this leaves a `Discarded` tombstone
/// rather than removing the entry outright: the background thread doing the
/// render is going to `insert` its result into this same slot once it
/// finishes, and a plain removal here would just let that completion
/// resurrect a `Ready`/`Cancelled` entry nobody will ever poll.
#[unsafe(no_mangle)]
pub extern "C" fn discard_request(request_id: u64) {
    let mut requests = lock(&core().requests);
    match requests.get(&request_id) {
        Some(RequestSlot::Pending { .. }) => {
            requests.insert(request_id, RequestSlot::Discarded);
        }
        _ => {
            requests.remove(&request_id);
        }
    }
}

// ---------------------------------------------------------------------
// Shared rendering helpers
// ---------------------------------------------------------------------

/// Callers must hold `CALL_LOCK` for the duration of this call (and, since
/// dropping the last `Arc<PdfDocument>` reference runs PDFium's native
/// close, for as long as `doc`'s owner might be dropped too).
fn render_page_via_pdfium(
    doc: &PdfDocument<'static>,
    page_index: i32,
    target_width: i32,
) -> Option<(i32, i32, Vec<u8>)> {
    let page = doc.pages().get(page_index as u16).ok()?;
    render_page_via_pdfium_page(&page, target_width)
}

/// Rasterizes an already-resolved page. Split out so the region renderer can
/// share it after narrowing the page's crop box.
fn render_page_via_pdfium_page(
    page: &pdfium_render::prelude::PdfPage<'_>,
    target_width: i32,
) -> Option<(i32, i32, Vec<u8>)> {
    use pdfium_render::prelude::*;

    // set_reverse_byte_order defaults to TRUE in PdfRenderConfig::new(), which
    // makes PDFium write RGBA while bitmap.format() still reports BGRA. Our
    // consumer is a WinUI WriteableBitmap, whose PixelBuffer is always BGRA8,
    // so leaving the default on swaps red and blue in every colored page. It
    // is invisible on black-on-white fixtures, which is why it survived this
    // long. Turning the flag off makes PDFium write the byte order we want
    // directly, so the buffer still needs no conversion pass.
    let render_config = PdfRenderConfig::new()
        .set_target_width(target_width)
        .set_reverse_byte_order(false)
        .rotate_if_landscape(PdfPageRenderRotation::None, false);

    let bitmap = page.render_with_config(&render_config).ok()?;
    let width = bitmap.width() as i32;
    let height = bitmap.height() as i32;

    // `as_raw_bytes()` hands back PDFium's own buffer untouched, which is
    // already BGRA — exactly the layout WriteableBitmap wants on the C# side.
    //
    // Do NOT reach for `as_image()`/`as_rgba_bytes()` here: those normalize
    // BGRA to RGBA, which then has to be swapped straight back to BGRA for
    // display. That round trip is two extra full-buffer passes that cancel
    // each other out, and at a 3000px render width it was the single
    // largest cost in this function after PDFium's own rasterization.
    let bgra = bitmap.as_raw_bytes();

    debug_assert_eq!(
        bgra.len(),
        (width as usize) * (height as usize) * 4,
        "expected a tightly-packed 4-bytes-per-pixel BGRA buffer"
    );

    Some((width, height, bgra))
}

fn placeholder_height(width: i32) -> i32 {
    // US Letter aspect ratio (11 / 8.5) as a stand-in page shape.
    ((width as f32) * (11.0 / 8.5)) as i32
}

fn render_placeholder_buffer(width: i32, height: i32) -> Vec<u8> {
    let w = width.max(1) as usize;
    let h = height.max(1) as usize;
    let mut buf = vec![0u8; w * h * 4];

    for y in 0..h {
        for x in 0..w {
            let idx = (y * w + x) * 4;
            let is_light = ((x / 20) + (y / 20)) % 2 == 0;
            let (r, g, b) = if is_light { (235u8, 235u8, 240u8) } else { (200u8, 205u8, 215u8) };
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
            buf[idx + 3] = 255;
        }
    }

    buf
}

fn buffer_to_result(width: i32, height: i32, buf: Vec<u8>, status: i32) -> RenderResult {
    let w = width.max(1);
    let h = height.max(1);
    let mut boxed = buf.into_boxed_slice();
    let len = boxed.len();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);

    RenderResult { width: w, height: h, buffer: ptr, len, status }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::thread::sleep;
    use std::time::Duration;

    fn open_fixture() -> u64 {
        open_fixture_named("tests/fixtures/sample.pdf")
    }

    fn open_fixture_named(path: &str) -> u64 {
        let c_path = std::ffi::CString::new(path).unwrap();
        let handle = open_document(c_path.as_ptr());
        assert_ne!(handle, 0, "expected {path} to open");
        handle
    }

    #[test]
    fn placeholder_fills_expected_size() {
        let buf = render_placeholder_buffer(100, 129);
        let result = buffer_to_result(100, 129, buf, STATUS_OK_PLACEHOLDER);
        assert_eq!(result.status, STATUS_OK_PLACEHOLDER);
        assert_eq!(result.len, 100 * 129 * 4);
        free_render_result(result);
    }

    #[test]
    fn open_document_rejects_missing_file() {
        let path = std::ffi::CString::new("does-not-exist.pdf").unwrap();
        assert_eq!(open_document(path.as_ptr()), 0);
    }

    #[test]
    fn render_low_res_renders_real_page_and_caches_it() {
        let handle = open_fixture();

        let first = render_low_res(handle, 0, 150);
        assert_eq!(first.status, STATUS_OK_PDFIUM);
        assert_eq!(first.width, 150);
        assert_eq!(first.height, 150); // fixture page is square
        free_render_result(first);

        // Second call at the same width should be a cache hit — still a
        // real render, same dimensions.
        let second = render_low_res(handle, 0, 150);
        assert_eq!(second.status, STATUS_OK_PDFIUM);
        assert_eq!(second.width, 150);
        assert_eq!(second.height, 150);
        free_render_result(second);

        close_document(handle);
    }

    #[test]
    fn get_page_count_reports_fixture_and_rejects_unknown_handle() {
        let handle = open_fixture();
        assert_eq!(get_page_count(handle), 1);
        assert_eq!(get_page_count(handle + 999_999), -1);
        close_document(handle);
    }

    #[test]
    fn request_high_res_delivers_a_real_render() {
        let handle = open_fixture();
        let request_id = request_high_res(handle, 0, 400);
        assert_ne!(request_id, 0);

        let mut result = RenderResult::failure(STATUS_INVALID_INPUT);
        let mut status;
        loop {
            status = poll_high_res(request_id, &mut result);
            if status != POLL_PENDING {
                break;
            }
            sleep(Duration::from_millis(5));
        }

        assert_eq!(status, POLL_READY);
        assert_eq!(result.status, STATUS_OK_PDFIUM);
        assert_eq!(result.width, 400);
        free_render_result(result);

        close_document(handle);
    }

    #[test]
    fn superseding_a_high_res_request_cancels_the_earlier_one() {
        let handle = open_fixture();

        let stale_id = request_high_res(handle, 0, 300);
        let fresh_id = request_high_res(handle, 0, 600); // same page -> supersedes stale_id
        assert_ne!(stale_id, fresh_id);

        let mut discard = RenderResult::failure(STATUS_INVALID_INPUT);
        let mut fresh_result = RenderResult::failure(STATUS_INVALID_INPUT);

        let mut stale_final = None;
        let mut fresh_final = None;

        for _ in 0..200 {
            if stale_final.is_none() {
                let s = poll_high_res(stale_id, &mut discard);
                if s != POLL_PENDING {
                    stale_final = Some(s);
                }
            }
            if fresh_final.is_none() {
                let s = poll_high_res(fresh_id, &mut fresh_result);
                if s != POLL_PENDING {
                    fresh_final = Some(s);
                }
            }
            if stale_final.is_some() && fresh_final.is_some() {
                break;
            }
            sleep(Duration::from_millis(5));
        }

        assert_eq!(stale_final, Some(POLL_CANCELLED), "the superseded request should be cancelled, not delivered");
        assert_eq!(fresh_final, Some(POLL_READY));
        assert_eq!(fresh_result.width, 600);
        free_render_result(fresh_result);

        close_document(handle);
    }

    #[test]
    fn poll_unknown_request_id_is_reported_distinctly() {
        let mut result = RenderResult::failure(STATUS_INVALID_INPUT);
        assert_eq!(poll_high_res(999_999_999, &mut result), POLL_UNKNOWN_REQUEST);
    }

    #[test]
    fn get_page_count_reports_multi_page_documents() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(get_page_count(handle), 20);
        close_document(handle);

        let handle_300 = open_fixture_named("tests/fixtures/sample_300pages.pdf");
        assert_eq!(get_page_count(handle_300), 300);
        close_document(handle_300);
    }

    #[test]
    fn different_pages_render_genuinely_different_content() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let page0 = render_low_res(handle, 0, 150);
        let page5 = render_low_res(handle, 5, 150);
        let page19 = render_low_res(handle, 19, 150);

        assert_eq!(page0.status, STATUS_OK_PDFIUM);
        assert_eq!(page5.status, STATUS_OK_PDFIUM);
        assert_eq!(page19.status, STATUS_OK_PDFIUM);

        let bytes0 = unsafe { std::slice::from_raw_parts(page0.buffer, page0.len) }.to_vec();
        let bytes5 = unsafe { std::slice::from_raw_parts(page5.buffer, page5.len) }.to_vec();
        let bytes19 = unsafe { std::slice::from_raw_parts(page19.buffer, page19.len) }.to_vec();

        assert_ne!(bytes0, bytes5, "page 0 and page 5 render different text, expected different pixels");
        assert_ne!(bytes5, bytes19, "page 5 and page 19 render different text, expected different pixels");

        free_render_result(page0);
        free_render_result(page5);
        free_render_result(page19);
        close_document(handle);
    }

    #[test]
    fn spot_check_pages_across_a_300_page_document() {
        let handle = open_fixture_named("tests/fixtures/sample_300pages.pdf");

        for page_index in [0, 1, 100, 200, 299] {
            let result = render_low_res(handle, page_index, 120);
            assert_eq!(result.status, STATUS_OK_PDFIUM, "page {page_index} should render via PDFium");
            assert_eq!(result.width, 120);
            assert_eq!(result.height, 120);
            free_render_result(result);
        }

        // Out of range: PDFium should fail to load the page and render_low_res
        // falls back to the placeholder rather than panicking.
        let out_of_range = render_low_res(handle, 300, 120);
        assert_eq!(out_of_range.status, STATUS_OK_PLACEHOLDER);
        free_render_result(out_of_range);

        close_document(handle);
    }

    #[test]
    fn discard_request_makes_a_later_poll_report_unknown() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let request_id = request_high_res(handle, 3, 300);

        discard_request(request_id);

        let mut result = RenderResult::failure(STATUS_INVALID_INPUT);
        // Whether the render finished before or after the discard, the
        // caller gave up on it: a later poll must never resurrect it.
        for _ in 0..200 {
            sleep(Duration::from_millis(5));
        }
        assert_eq!(poll_high_res(request_id, &mut result), POLL_UNKNOWN_REQUEST);

        close_document(handle);
    }

    #[test]
    fn discard_request_is_a_no_op_for_unknown_id() {
        discard_request(123_456_789); // must not panic
    }

    #[test]
    fn get_page_chars_extracts_expected_text_in_order() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let array = get_page_chars(handle, 0, 200);
        assert_eq!(array.status, STATUS_OK_PDFIUM);

        let chars = unsafe { std::slice::from_raw_parts(array.chars, array.len) };
        let text: String = chars.iter().filter_map(|c| char::from_u32(c.codepoint)).collect();
        assert_eq!(text, "Page 1 of 20");

        // Left-to-right layout: each character should start no further left
        // than the previous one (allowing for kerning/overlap noise).
        for pair in chars.windows(2) {
            assert!(pair[1].left >= pair[0].left - 1.0, "expected left-to-right character order");
        }

        // Boxes should land within the requested render width, not off in
        // some other coordinate space entirely.
        for c in chars {
            assert!(c.left >= 0.0 && c.right <= 200.0 + 1.0, "char box {}..{} outside render width", c.left, c.right);
            assert!(c.top >= 0.0 && c.bottom > c.top, "expected a top-left-origin box with positive height");
        }

        free_char_info_array(array);
        close_document(handle);
    }

    #[test]
    fn get_page_chars_reports_different_text_on_different_pages() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let page0 = get_page_chars(handle, 0, 200);
        let page5 = get_page_chars(handle, 5, 200);

        let text0: String = unsafe { std::slice::from_raw_parts(page0.chars, page0.len) }
            .iter()
            .filter_map(|c| char::from_u32(c.codepoint))
            .collect();
        let text5: String = unsafe { std::slice::from_raw_parts(page5.chars, page5.len) }
            .iter()
            .filter_map(|c| char::from_u32(c.codepoint))
            .collect();

        assert_eq!(text0, "Page 1 of 20");
        assert_eq!(text5, "Page 6 of 20");

        free_char_info_array(page0);
        free_char_info_array(page5);
        close_document(handle);
    }

    #[test]
    fn get_page_chars_rejects_invalid_input() {
        let result = get_page_chars(0, 0, 200);
        assert_eq!(result.status, STATUS_INVALID_INPUT);
        assert_eq!(result.len, 0);
        assert!(result.chars.is_null());
        free_char_info_array(result); // must be a no-op, not a crash
    }

    fn page_text(handle: u64, page_index: i32) -> String {
        let array = get_page_chars(handle, page_index, 200);
        assert_eq!(array.status, STATUS_OK_PDFIUM);
        let chars = unsafe { std::slice::from_raw_parts(array.chars, array.len) };
        let text: String = chars.iter().filter_map(|c| char::from_u32(c.codepoint)).collect();
        free_char_info_array(array);
        text
    }

    #[test]
    fn rotate_page_sets_persisted_rotation() {
        use pdfium_render::prelude::PdfPageRenderRotation;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(rotate_page(handle, 0, 90), STATUS_OK_PDFIUM);

        // White-box check: read the rotation straight back off the
        // in-memory document (there's no FFI getter — the app doesn't need
        // one, only render_core's own tests do).
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let rotation = lock(&doc).pages().get(0).unwrap().rotation().unwrap();
        assert_eq!(rotation, PdfPageRenderRotation::Degrees90);

        close_document(handle);
    }

    #[test]
    fn rotate_page_rejects_invalid_handle() {
        assert_eq!(rotate_page(0, 0, 90), STATUS_INVALID_INPUT);
    }

    #[test]
    fn delete_page_shifts_later_pages_down() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(get_page_count(handle), 20);

        assert_eq!(delete_page(handle, 5), STATUS_OK_PDFIUM); // removes "Page 6 of 20"
        assert_eq!(get_page_count(handle), 19);

        // What used to be page index 6 ("Page 7 of 20") is now at index 5.
        assert_eq!(page_text(handle, 5), "Page 7 of 20");

        close_document(handle);
    }

    #[test]
    fn delete_page_rejects_invalid_handle() {
        assert_eq!(delete_page(0, 0), STATUS_INVALID_INPUT);
    }

    #[test]
    fn save_document_persists_changes_to_a_real_file_on_disk() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(delete_page(handle, 0), STATUS_OK_PDFIUM); // now starts at "Page 2 of 20"

        let mut save_path = std::env::temp_dir();
        save_path.push(format!("render_core_test_save_{}_{}.pdf", std::process::id(), line!()));
        let save_path_str = save_path.to_str().unwrap().to_owned();
        let c_path = std::ffi::CString::new(save_path_str.clone()).unwrap();

        assert_eq!(save_document(handle, c_path.as_ptr()), STATUS_OK_PDFIUM);
        close_document(handle);

        // Reopen the SAVED file as a completely independent handle — this
        // is the real guarantee that matters: the edit was actually written
        // to disk, not just held in this process's in-memory document.
        let reopened = open_fixture_named(&save_path_str);
        assert_eq!(get_page_count(reopened), 19);
        assert_eq!(page_text(reopened, 0), "Page 2 of 20");

        close_document(reopened);
        let _ = std::fs::remove_file(&save_path);
    }

    #[test]
    fn save_document_rejects_invalid_handle() {
        let path = std::ffi::CString::new("wherever.pdf").unwrap();
        assert_eq!(save_document(0, path.as_ptr()), STATUS_INVALID_INPUT);
    }

    #[test]
    fn get_form_field_count_is_zero_for_a_document_without_a_form() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(get_form_field_count(handle), 0);
        close_document(handle);
    }

    #[test]
    fn get_form_field_count_rejects_invalid_handle() {
        assert_eq!(get_form_field_count(0), -1);
    }

    #[test]
    fn fill_text_field_fails_gracefully_when_no_such_field_exists() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let name = std::ffi::CString::new("DoesNotExist").unwrap();
        let value = std::ffi::CString::new("hello").unwrap();
        assert_eq!(fill_text_field(handle, name.as_ptr(), value.as_ptr()), STATUS_INVALID_INPUT);
        close_document(handle);
    }

    /// Guards the `as_raw_bytes()` fast path: our text fixtures are black on
    /// white, which is byte-identical under a red/blue swap, so a format
    /// regression here would be invisible to every other test. Assert the
    /// format PDFium actually produced instead of eyeballing pixels.
    #[test]
    fn rendered_bitmap_is_native_bgra_without_byte_order_reversal() {
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(0).unwrap();

        let config = PdfRenderConfig::new()
            .set_target_width(200)
            .rotate_if_landscape(PdfPageRenderRotation::None, false);
        let bitmap = page.render_with_config(&config).unwrap();

        assert_eq!(
            bitmap.format().unwrap(),
            PdfBitmapFormat::BGRA,
            "render_page_via_pdfium hands as_raw_bytes() straight to a BGRA consumer"
        );

        let raw = bitmap.as_raw_bytes();
        assert_eq!(raw.len(), 200 * bitmap.height() as usize * 4, "expected tightly packed BGRA");

        // A mostly-white page: every fully-opaque pixel should be neutral
        // grey/white, i.e. B == G == R. That holds regardless of channel
        // order, but it does prove we are not reading a misaligned/strided
        // buffer as if it were tightly packed.
        let neutral = raw.chunks_exact(4).filter(|p| p[0] == p[1] && p[1] == p[2]).count();
        let total = raw.len() / 4;
        assert!(
            neutral * 100 / total > 90,
            "expected a mostly-neutral page, got {neutral}/{total} neutral pixels"
        );

        drop(doc_guard);
        drop(doc);
        drop(_guard);
        close_document(handle);
    }

    /// The cache used to be bounded by entry count only. Entry size scales
    /// with zoom squared, so at a 6000px render width 32 entries is ~5.5GB.
    /// Every other test passed throughout, because none of them looked at
    /// resident bytes.
    #[test]
    fn cache_stays_within_its_byte_budget_under_many_large_renders() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // 20 distinct pages at a large width: uncapped this would try to hold
        // far more than the budget at once.
        for page in 0..20 {
            let r = render_low_res(handle, page, 1600);
            assert_eq!(r.status, STATUS_OK_PDFIUM);
            free_render_result(r);
        }

        let resident: usize = lock(&core().cache).iter().map(|(_, t)| t.bytes.len()).sum();
        assert!(
            resident <= CACHE_BUDGET_BYTES,
            "cache held {resident} bytes, over the {CACHE_BUDGET_BYTES} byte budget"
        );

        close_document(handle);
    }

    #[test]
    fn oversized_tiles_are_not_cached_at_all() {
        // Assert on THIS key's presence, not on the cache's total length.
        // The cache is process-global and the other tests render into it
        // concurrently, so a length comparison is inherently racy — and when
        // it failed it did so while holding the cache lock inside assert_eq!,
        // poisoning the mutex and cascading into every later test.
        let key = TileKey::page_key(u64::MAX, 0, Tier::High, 1);

        let huge = CachedTile {
            width: 1,
            height: 1,
            bytes: Arc::from(vec![0u8; MAX_CACHEABLE_TILE_BYTES + 1]),
        };
        cache_put(key, huge);

        let present = lock(&core().cache).contains(&key);
        assert!(!present, "an oversized tile must not enter the cache");
    }

    /// Counts page objects, so a burn can be proven by the count going up
    /// rather than by the call merely returning OK.
    fn page_object_count(handle: u64, page_index: i32) -> usize {
        use pdfium_render::prelude::*;
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(page_index as u16).unwrap();
        page.objects().len() as usize
    }

    #[test]
    fn burn_annotations_adds_objects_that_survive_save_and_reopen() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = page_object_count(handle, 0);

        // A highlight band and a 3-point ink stroke, in the coordinate space
        // of a 1000px-wide render.
        let rects = [BurnRect {
            page_index: 0,
            left: 100.0,
            top: 100.0,
            right: 500.0,
            bottom: 200.0,
            r: 255, g: 255, b: 0, a: 128,
        }];
        let pts = [
            BurnPoint { x: 100.0, y: 400.0 },
            BurnPoint { x: 300.0, y: 450.0 },
            BurnPoint { x: 500.0, y: 400.0 },
        ];
        let strokes = [BurnStroke {
            page_index: 0,
            point_offset: 0,
            point_count: 3,
            width_px: 4.0,
            r: 255, g: 0, b: 0, a: 255,
        }];

        assert_eq!(
            burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                             strokes.as_ptr(), strokes.len(), pts.as_ptr(), pts.len()),
            STATUS_OK_PDFIUM
        );

        let after = page_object_count(handle, 0);
        assert_eq!(after, before + 2, "expected one rect object and one ink path object");

        // The real guarantee: the objects must be in the SAVED BYTES, not
        // just the in-memory document.
        let mut path = std::env::temp_dir();
        path.push(format!("render_core_burn_{}.pdf", std::process::id()));
        let path_str = path.to_str().unwrap().to_owned();
        let c_path = std::ffi::CString::new(path_str.clone()).unwrap();
        assert_eq!(save_document(handle, c_path.as_ptr()), STATUS_OK_PDFIUM);
        close_document(handle);

        let reopened = open_fixture_named(&path_str);
        assert_eq!(
            page_object_count(reopened, 0),
            after,
            "burned objects did not survive the save/reopen round trip"
        );
        close_document(reopened);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn burn_annotations_leaves_other_pages_untouched() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before_p1 = page_object_count(handle, 1);

        let rects = [BurnRect {
            page_index: 0,
            left: 10.0, top: 10.0, right: 100.0, bottom: 50.0,
            r: 0, g: 255, b: 0, a: 90,
        }];
        assert_eq!(
            burn_annotations(handle, 800, rects.as_ptr(), rects.len(),
                             std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_OK_PDFIUM
        );

        assert_eq!(page_object_count(handle, 1), before_p1, "page 1 must be untouched");
        close_document(handle);
    }

    #[test]
    fn burn_annotations_flips_y_into_pdf_space() {
        // A band near the TOP in render space must land near the TOP of the
        // page in PDF space, i.e. at a HIGH y value, since PDF's origin is
        // bottom-left. Getting this wrong mirrors every annotation.
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let rects = [BurnRect {
            page_index: 0,
            left: 0.0, top: 0.0, right: 100.0, bottom: 100.0,   // top strip
            r: 255, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                             std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_OK_PDFIUM
        );

        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(0).unwrap();
        let page_height = page.height().value;

        // The object we just added is the last one on the page.
        let obj = page.objects().iter().last().unwrap();
        let bounds = obj.bounds().unwrap();

        assert!(
            bounds.top().value > page_height * 0.8,
            "a top-of-screen annotation should sit near the top of the page in PDF \
             coords (y={} of {}), not mirrored to the bottom",
            bounds.top().value, page_height
        );

        drop(g);
        drop(doc);
        drop(_guard);
        close_document(handle);
    }

    /// Mean BGRA of a box in a freshly rendered page, sampled at `width` px.
    fn sample_mean(handle: u64, page: i32, width: i32, x0: i32, y0: i32, x1: i32, y1: i32)
        -> (f64, f64, f64)
    {
        let r = render_low_res(handle, page, width);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len) };
        let (mut b, mut g, mut rd, mut n) = (0f64, 0f64, 0f64, 0f64);
        for y in y0..y1 {
            for x in x0..x1 {
                let i = ((y * r.width + x) * 4) as usize;
                b += bytes[i] as f64;
                g += bytes[i + 1] as f64;
                rd += bytes[i + 2] as f64;
                n += 1.0;
            }
        }
        free_render_result(r);
        (b / n, g / n, rd / n)
    }

    #[test]
    fn burned_highlight_is_actually_visible_where_it_was_drawn() {
        // The object-count tests prove an object was added; they do not prove
        // it renders, nor that it renders in the right half of the page. An
        // inverted Y flip, a zero alpha or a bad blend mode all pass a count
        // check and still show the user nothing (or a mark in the wrong
        // place). So: burn a saturated blue band across the TOP quarter, then
        // re-render and compare that band against the BOTTOM quarter.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const W: i32 = 400;

        // The fixture page is square, so a W-wide render is W tall.
        let before_top = sample_mean(handle, 0, W, 20, 25, W - 20, 75);
        let before_bottom = sample_mean(handle, 0, W, 20, 320, W - 20, 390);

        // Coordinates are in the space of a 1000px-wide capture; the render
        // above is 400px wide, so the burn must survive that rescale too.
        let rects = [BurnRect {
            page_index: 0,
            left: 50.0, top: 50.0, right: 950.0, bottom: 200.0,
            r: 0, g: 0, b: 255, a: 255,
        }];
        assert_eq!(
            burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                             std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_OK_PDFIUM
        );

        let after_top = sample_mean(handle, 0, W, 20, 25, W - 20, 75);
        let after_bottom = sample_mean(handle, 0, W, 20, 320, W - 20, 390);

        // The page is already near-white (~235 on every channel), so "blue"
        // shows up as channel DOMINANCE, not as a rise in absolute blue.
        let blueness = |(b, _g, r): (f64, f64, f64)| b - r;
        assert!(
            blueness(before_top).abs() < 10.0,
            "fixture should start neutral, got (b,g,r)={before_top:?}"
        );
        assert!(
            blueness(after_top) > 150.0,
            "top band should be strongly blue after the burn: \
             before(b,g,r)={before_top:?} after={after_top:?}"
        );
        assert!(
            blueness(after_bottom).abs() < 10.0 && (after_bottom.2 - before_bottom.2).abs() < 5.0,
            "bottom of the page must be untouched, but it changed: \
             before={before_bottom:?} after={after_bottom:?} (annotation mirrored?)"
        );

        close_document(handle);
    }

    #[test]
    fn saving_twice_does_not_burn_the_same_annotation_twice() {
        // Guards the core burn invariant: flattening the same input onto a
        // freshly opened document twice yields the same object count both
        // times, so nothing stacks. The C# save path achieves this by
        // reopening the saved file with its overlay list cleared, so a second
        // save finds nothing to burn; this test covers the underlying
        // property regardless of which reopen source that path chooses.
        let src = "tests/fixtures/sample_20pages.pdf";
        let rects = [BurnRect {
            page_index: 0,
            left: 100.0, top: 100.0, right: 500.0, bottom: 200.0,
            r: 255, g: 255, b: 0, a: 128,
        }];

        let mut counts = Vec::new();
        let mut outs = Vec::new();
        for pass in 0..2 {
            // Every pass starts from the clean original: this is the reload.
            let handle = open_fixture_named(src);
            assert_eq!(
                burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                                 std::ptr::null(), 0, std::ptr::null(), 0),
                STATUS_OK_PDFIUM
            );
            let mut path = std::env::temp_dir();
            path.push(format!("render_core_double_{}_{pass}.pdf", std::process::id()));
            let path_str = path.to_str().unwrap().to_owned();
            let c_path = std::ffi::CString::new(path_str.clone()).unwrap();
            assert_eq!(save_document(handle, c_path.as_ptr()), STATUS_OK_PDFIUM);
            close_document(handle);

            let reopened = open_fixture_named(&path_str);
            counts.push(page_object_count(reopened, 0));
            close_document(reopened);
            outs.push(path);
        }

        assert_eq!(
            counts[0], counts[1],
            "the second save added extra objects, so the annotation was burned twice"
        );

        for p in outs {
            let _ = std::fs::remove_file(p);
        }
    }

    #[test]
    fn render_buffer_channel_order_is_bgra_not_rgba() {
        // The buffer goes straight into a WinUI WriteableBitmap, whose
        // PixelBuffer is always BGRA8. The neutral-grey fixture check cannot
        // tell BGRA from RGBA, so paint a known ASYMMETRIC color onto the page
        // and read back which byte it lands in. PdfRenderConfig defaults to
        // reverse_byte_order(true), which yields RGBA here and silently swaps
        // red and blue on screen for every colored PDF.
        for (name, (r, g, b), want) in [
            ("red", (255u8, 0u8, 0u8), [0u8, 0, 255]),
            ("blue", (0u8, 0u8, 255u8), [255u8, 0, 0]),
        ] {
            let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let rects = [BurnRect {
                page_index: 0,
                left: 50.0, top: 50.0, right: 950.0, bottom: 200.0,
                r, g, b, a: 255,
            }];
            assert_eq!(
                burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                                 std::ptr::null(), 0, std::ptr::null(), 0),
                STATUS_OK_PDFIUM
            );

            let res = render_low_res(handle, 0, 400);
            assert_eq!(res.status, STATUS_OK_PDFIUM);
            let bytes = unsafe { std::slice::from_raw_parts(res.buffer, res.len) };
            let i = ((50 * res.width + 200) * 4) as usize;
            let got = [bytes[i], bytes[i + 1], bytes[i + 2]];
            assert_eq!(
                got, want,
                "pure {name} (rgb {r},{g},{b}) should read back as BGRA {want:?} \
                 but came out {got:?}; the buffer is RGBA, so WriteableBitmap \
                 will show red and blue swapped"
            );
            free_render_result(res);
            close_document(handle);
        }
    }

    #[test]
    fn render_uncached_produces_a_real_page_without_populating_the_cache() {
        // The sharpening tier must not evict the base tier. A huge bitmap that
        // landed in the cache would push every modest tile out.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let width = 613; // distinctive, so the key cannot collide with another test

        let key = TileKey::page_key(handle, 0, Tier::Low, width);
        assert!(lock(&core().cache).get(&key).is_none(), "precondition: nothing cached yet");

        let r = render_uncached(handle, 0, width);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        assert_eq!(r.width, width);
        assert!(r.len > 0);
        free_render_result(r);

        assert!(
            lock(&core().cache).get(&key).is_none(),
            "render_uncached must not store its result in the tile cache"
        );

        close_document(handle);
    }

    #[test]
    fn render_uncached_does_not_serve_a_stale_cached_tile() {
        // It must also not READ the cache: a sharpen pass asking for width W
        // has to get a genuine W-wide render, not whatever the base tier
        // happened to leave under that key.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let width = 617;

        // Poison the cache with a wrong-sized tile under the exact key.
        lock(&core().cache).put(
            TileKey::page_key(handle, 0, Tier::Low, width),
            CachedTile { width: 4, height: 4, bytes: Arc::from(vec![7u8; 4 * 4 * 4]) },
        );

        let r = render_uncached(handle, 0, width);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        assert_eq!(r.width, width, "served the poisoned cache entry instead of rendering");
        free_render_result(r);

        close_document(handle);
    }

    #[test]
    fn render_uncached_rejects_bad_input() {
        assert_eq!(render_uncached(0, 0, 100).status, STATUS_INVALID_INPUT);
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(render_uncached(handle, 0, 0).status, STATUS_INVALID_INPUT);
        assert_eq!(render_uncached(handle, 9999, 100).status, STATUS_INVALID_INPUT);
        close_document(handle);
    }

    #[test]
    fn get_page_sizes_returns_one_entry_per_page_with_real_dimensions() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let array = get_page_sizes(handle);
        assert_eq!(array.status, STATUS_OK_PDFIUM);
        assert_eq!(array.len, get_page_count(handle) as usize);

        let sizes = unsafe { std::slice::from_raw_parts(array.sizes, array.len) };
        for (i, s) in sizes.iter().enumerate() {
            assert!(s.width > 0.0 && s.height > 0.0, "page {i} reported {}x{}", s.width, s.height);
        }

        // Sizes must match what a render actually produces, or every slot in
        // the continuous viewport would be the wrong height.
        let r = render_low_res(handle, 0, 400);
        let aspect_from_size = sizes[0].height / sizes[0].width;
        let aspect_from_render = r.height as f32 / r.width as f32;
        assert!(
            (aspect_from_size - aspect_from_render).abs() < 0.02,
            "reported aspect {aspect_from_size} disagrees with rendered {aspect_from_render}"
        );
        free_render_result(r);

        free_page_size_array(array);
        close_document(handle);
    }

    #[test]
    fn get_page_sizes_tracks_a_page_delete() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = get_page_sizes(handle);
        let n = before.len;
        free_page_size_array(before);

        assert_eq!(delete_page(handle, 0), STATUS_OK_PDFIUM);
        let after = get_page_sizes(handle);
        assert_eq!(after.len, n - 1, "slot list must follow page deletions");
        free_page_size_array(after);
        close_document(handle);
    }

    #[test]
    fn get_page_sizes_rejects_bad_input() {
        assert_eq!(get_page_sizes(0).status, STATUS_INVALID_INPUT);
        assert_eq!(get_page_sizes(999_999).status, STATUS_INVALID_INPUT);
        free_page_size_array(PageSizeArray::failure(STATUS_INVALID_INPUT));
    }

    #[test]
    fn snapshot_and_restore_round_trips_a_page_delete() {
        // The document-level undo path. Delete a page, and the snapshot taken
        // beforehand must still reopen with the original page count.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = get_page_count(handle);

        let snap = snapshot_document(handle);
        assert_eq!(snap.status, STATUS_OK_PDFIUM);
        assert!(snap.len > 0, "snapshot should not be empty");

        assert_eq!(delete_page(handle, 3), STATUS_OK_PDFIUM);
        assert_eq!(get_page_count(handle), before - 1);

        let restored = open_document_from_bytes(snap.data, snap.len);
        assert_ne!(restored, 0, "snapshot should reopen");
        assert_eq!(get_page_count(restored), before, "undo must bring the page back");

        // Restoring twice from the same snapshot must work: undo, redo, undo
        // all replay the same bytes, so the buffer cannot be consumed.
        let restored_again = open_document_from_bytes(snap.data, snap.len);
        assert_ne!(restored_again, 0);
        assert_eq!(get_page_count(restored_again), before);

        close_document(restored);
        close_document(restored_again);
        close_document(handle);
        free_byte_buffer(snap);
    }

    /// Collects a page's annotations and frees the FFI array.
    fn read_annotations(handle: u64, page_index: i32) -> Vec<(i32, i32, f32, f32, f32, f32)> {
        let array = get_annotations(handle, page_index);
        assert_eq!(array.status, STATUS_OK_PDFIUM);
        let out = if array.items.is_null() {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(array.items, array.len) }
                .iter()
                .map(|a| (a.index, a.subtype, a.left, a.top, a.right, a.bottom))
                .collect()
        };
        free_annotation_array(array);
        out
    }

    #[test]
    fn a_clean_page_reports_no_annotations_rather_than_failing() {
        // Most pages of most documents have none. An error here would put an
        // error path on the overwhelmingly common case.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let array = get_annotations(handle, 0);
        assert_eq!(array.status, STATUS_OK_PDFIUM);
        assert_eq!(array.len, 0);
        free_annotation_array(array);
        close_document(handle);
    }

    #[test]
    fn annotations_can_be_read_back_after_being_added() {
        // The capability the app never had: seeing markup that is already in
        // the file. Notes are the one kind already written as real annotation
        // objects, so they are what proves the read path end to end.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert!(read_annotations(handle, 0).is_empty());

        let text = std::ffi::CString::new("a note").unwrap();
        let notes = [BurnNote { page_index: 0, x: 100.0, y: 150.0, text: text.as_ptr() }];
        assert_eq!(
            add_note_annotations(handle, 1000, notes.as_ptr(), notes.len()),
            STATUS_OK_PDFIUM
        );

        let found = read_annotations(handle, 0);
        println!("READ BACK: {found:?}");
        assert_eq!(found.len(), 1, "the note just added should be readable");
        assert_eq!(found[0].0, 0, "first annotation on the page is index 0");
        assert_eq!(found[0].1, ANNOT_TEXT);

        close_document(handle);
    }

    #[test]
    fn read_back_bounds_land_where_the_annotation_was_put() {
        // Guards the coordinate flip. PDF is bottom-left origin and Y-up, the
        // app is top-left and Y-down, and both axes are normalized by the page
        // WIDTH. Getting this wrong would put every loaded annotation in the
        // wrong place, mirrored vertically, which is easy to miss on a mark
        // that happens to sit near the middle of the page.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // Place a note near the TOP of the page in capture space.
        let text = std::ffi::CString::new("top").unwrap();
        let notes = [BurnNote { page_index: 0, x: 0.0, y: 0.0, text: text.as_ptr() }];
        assert_eq!(
            add_note_annotations(handle, 1000, notes.as_ptr(), notes.len()),
            STATUS_OK_PDFIUM
        );

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        let (_, _, left, top, _, bottom) = found[0];
        println!("BOUNDS: left={left} top={top} bottom={bottom}");

        assert!(left.abs() < 0.01, "placed at x=0, read back at {left}");
        assert!(top.abs() < 0.01, "placed at the top, read back at {top}");
        assert!(bottom > top, "top must be ABOVE bottom in a Y-down space");

        close_document(handle);
    }

    #[test]
    fn a_highlight_survives_save_and_reopen_as_an_editable_object() {
        // THE point of Phase A. Burning a highlight into page content makes it
        // pixels: reopen the file and it cannot be moved, recoloured or
        // removed. As an annotation object it comes back as an object, which
        // is what every editing feature after this depends on.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let quads = [HighlightQuad { left: 100.0, top: 100.0, right: 500.0, bottom: 140.0 }];
        let specs = [HighlightSpec {
            page_index: 0,
            quad_offset: 0,
            quad_count: 1,
            r: 255,
            g: 235,
            b: 59,
            a: 128,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), specs.len(),
                                      quads.as_ptr(), quads.len()),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        assert_eq!(before.len(), 1);
        assert_eq!(before[0].1, ANNOT_HIGHLIGHT);

        // Render BEFORE saving, as the app inevitably does since the page is
        // on screen. This is not incidental: rendering is what makes PDFium
        // generate the highlight's appearance stream, and the appearance is
        // what a viewer draws. A file saved without ever rendering carries an
        // AP-less highlight that PDFium itself will NOT draw after reopening,
        // which this test originally proved by accident.
        free_render_result(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 400));

        // Round trip through actual PDF bytes, not just the in-memory page.
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0, "the saved document should reopen");

        let after = read_annotations(reopened, 0);
        println!("HIGHLIGHT before={before:?}\n           after={after:?}");

        assert_eq!(after.len(), 1, "the highlight should still be an annotation after reopening");
        assert_eq!(after[0].1, ANNOT_HIGHLIGHT, "and still a highlight");

        for (b, a) in [(before[0].2, after[0].2), (before[0].3, after[0].3),
                       (before[0].4, after[0].4), (before[0].5, after[0].5)] {
            assert!((b - a).abs() < 0.001, "geometry drifted across the round trip: {b} vs {a}");
        }

        // Colour must survive IN THE FILE. get_annotations no longer reports
        // it, deliberately: FPDFAnnot_GetColor access-violates on an
        // annotation with an appearance stream, and rendering generates
        // those. So the proof is the pixels: render the reopened page and the
        // highlight must still draw in its yellow, which PDFium takes from
        // the stored /C.
        let r = render_region(reopened, 0, 0.0, 0.0, 1.0, 1.0, 400);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        // The quads are normalized 0.1..0.5 x, 0.1..0.14 y, so on a 400px
        // render (120, 48) is inside the highlight.
        let i = (48 * r.width as usize + 120) * 4;
        let (b, g, red) = (bytes[i], bytes[i + 1], bytes[i + 2]);
        free_render_result(r);
        assert!(
            red > 200 && g > 200 && b < 160,
            "reopened highlight rendered B={b} G={g} R={red}, not its yellow,              so the colour did not survive the round trip"
        );

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    // ---- Shapes: rectangles, ellipses, lines and arrows ----

    /// Counts pixels that are neither white nor near-white in a rendered page.
    ///
    /// The measurement that matters for a shape. Creating the annotation and
    /// reading it back only proves an object exists; a highlight once passed
    /// exactly that bar while drawing ZERO pixels, because its quad points
    /// wound the wrong way. Only counting ink on the page proves it draws.
    fn marked_pixels(handle: u64, page: i32) -> usize {
        let r = render_region(handle, page, 0.0, 0.0, 1.0, 1.0, 400);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let count = bytes
            .chunks_exact(4)
            .filter(|p| p[0] < 200 || p[1] < 200 || p[2] < 200)
            .count();
        free_render_result(r);
        count
    }

    fn shape(kind: i32, x1: f32, y1: f32, x2: f32, y2: f32) -> ShapeSpec {
        ShapeSpec {
            page_index: 0,
            kind,
            x1,
            y1,
            x2,
            y2,
            r: 220,
            g: 0,
            b: 0,
            a: 255,
            width_px: 3.0,
        }
    }

    /// Why shapes are not `/Square` and `/Circle` annotations.
    ///
    /// A rectangle SHOULD be a `/Square`: it would resize by changing four
    /// numbers and would read as a rectangle in other PDF software. This test
    /// is the measurement that says we cannot have that, and it is kept so we
    /// find out if it ever changes.
    ///
    /// A `/Square` carrying `/Rect` and a stroke colour draws NOTHING, both in
    /// the session that created it and after a full save and reopen. PDFium
    /// does generate missing appearance streams, but only while building a
    /// page's annotation list, and an annotation added afterwards has missed
    /// that pass. Reopening does not rescue it either.
    ///
    /// Supplying the appearance ourselves needs `FPDFAnnot_SetAP`, which takes
    /// a raw `FPDF_ANNOTATION`. That function is public on the bindings trait,
    /// but every raw handle in pdfium-render is `pub(crate)`, so an annotation
    /// created through its safe API can never be handed to it. Ink and stamp
    /// annotations escape this only because `FPDFAnnot_AppendObject` writes the
    /// appearance stream itself, which is precisely why shapes ride on ink.
    ///
    /// The kind goes in `/Contents` instead, which this test also proves round
    /// trips, so a reopened shape is still a shape to us.
    #[test]
    fn a_bare_square_annotation_still_draws_nothing() {
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = marked_pixels(handle, 0);

        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&handle).cloned().unwrap();
            let doc_guard = lock(&doc);
            let mut page = doc_guard.pages().get(0).unwrap();

            let mut annot = page.annotations_mut().create_square_annotation().unwrap();
            annot.set_bounds(PdfRect::new(
                PdfPoints::new(300.0),
                PdfPoints::new(100.0),
                PdfPoints::new(500.0),
                PdfPoints::new(400.0),
            )).unwrap();
            let _ = annot.set_stroke_color(PdfColor::new(220, 0, 0, 255));
            let _ = annot.set_contents("AyaanShape:Rectangle");
        }
        evict_all_cache_for_doc(handle);

        let after = marked_pixels(handle, 0);
        println!("BARE SQUARE: {before} marked pixels before, {after} after");

        // Also prove the kind survives in a field we can read back.
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&handle).cloned().unwrap();
            let doc_guard = lock(&doc);
            let page = doc_guard.pages().get(0).unwrap();
            let annot = page.annotations().get(0).unwrap();
            println!("CONTENTS READ BACK: {:?}", annot.contents());
        }

        println!("SAME SESSION: bare square drew {} extra pixels", after as i64 - before as i64);

        // The real question is the FILE. PDFium builds a page's annotation list
        // once, and generates missing appearance streams while doing so, so an
        // annotation added afterwards may simply have missed that pass. A save
        // and reopen rebuilds the list from scratch.
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let after_reopen = marked_pixels(reopened, 0);
        println!("AFTER REOPEN: {before} before, {after_reopen} after");

        let annots = read_annotations(reopened, 0);
        println!("REOPENED ANNOTS: {annots:?}");

        // Asserting the LIMITATION, not the wish. If a future PDFium starts
        // generating this appearance, these fail and tell us that shapes can
        // become real /Square and /Circle annotations.
        assert_eq!(
            after, before,
            "a bare /Square now draws in-session; the /Square upgrade has become possible"
        );
        assert_eq!(
            after_reopen, before,
            "a bare /Square now draws after a reopen; the /Square upgrade has become possible"
        );

        // The subtype itself survives, so all that is missing is the appearance.
        assert_eq!(annots.len(), 1);
        assert_eq!(annots[0].1, ANNOT_SQUARE);

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn every_shape_kind_actually_draws_on_the_page() {
        for kind in [SHAPE_RECTANGLE, SHAPE_ELLIPSE, SHAPE_LINE, SHAPE_ARROW] {
            let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let before = marked_pixels(handle, 0);

            let specs = [shape(kind, 200.0, 200.0, 700.0, 500.0)];
            assert_eq!(
                add_shape_annotations(handle, 1000, specs.as_ptr(), specs.len()),
                STATUS_OK_PDFIUM,
                "kind {kind} was rejected"
            );

            let after = marked_pixels(handle, 0);
            assert!(
                after > before,
                "kind {kind} added an annotation that drew nothing: {before} marked pixels before, {after} after"
            );

            close_document(handle);
        }
    }

    #[test]
    fn a_shape_survives_save_and_reopen_as_an_editable_object() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 600.0, 400.0)];
        assert_eq!(
            add_shape_annotations(handle, 1000, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        assert_eq!(before.len(), 1);
        assert_eq!(before[0].1, ANNOT_INK);

        // Render before saving: this is what makes PDFium generate the
        // appearance stream, and without one a reopened file draws nothing.
        // The highlight path learned this the hard way.
        let drawn = marked_pixels(handle, 0);

        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        let after = read_annotations(reopened, 0);
        assert_eq!(after.len(), 1, "the shape should still be an annotation after reopening");
        assert_eq!(after[0].1, ANNOT_INK, "and still a mark");

        for (b, a) in [
            (before[0].2, after[0].2),
            (before[0].3, after[0].3),
            (before[0].4, after[0].4),
            (before[0].5, after[0].5),
        ] {
            assert!((b - a).abs() < 0.001, "geometry drifted across the round trip: {b} vs {a}");
        }

        // And it still DRAWS after the round trip, not merely exists.
        let redrawn = marked_pixels(reopened, 0);
        assert!(
            redrawn as f64 > drawn as f64 * 0.5,
            "the reopened shape drew {redrawn} marked pixels against {drawn} before saving"
        );

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn an_arrow_covers_more_than_its_bare_line() {
        // The head has to be inside the annotation's bounds. PDFium clips an
        // appearance to its box, so barbs outside it are silently cut off and
        // the arrow renders as a plain line: the failure looks like nothing
        // went wrong.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);

        let line = [shape(SHAPE_LINE, 200.0, 300.0, 700.0, 300.0)];
        assert_eq!(add_shape_annotations(handle, 1000, line.as_ptr(), 1), STATUS_OK_PDFIUM);
        let line_px = marked_pixels(handle, 0) - base;
        close_document(handle);

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let arrow = [shape(SHAPE_ARROW, 200.0, 300.0, 700.0, 300.0)];
        assert_eq!(add_shape_annotations(handle, 1000, arrow.as_ptr(), 1), STATUS_OK_PDFIUM);
        let arrow_px = marked_pixels(handle, 0) - base;
        close_document(handle);

        assert!(
            arrow_px > line_px,
            "the arrow drew {arrow_px} pixels and the plain line {line_px}, so the head is missing or clipped"
        );
    }

    #[test]
    fn an_arrow_head_sweeps_back_from_the_tip() {
        // Pointing right: both barbs must sit BEHIND the tip and straddle the
        // shaft. Getting the rotation sign wrong puts them ahead of the tip,
        // which draws a bowtie rather than an arrow.
        let ((lx, ly), (rx, ry)) = arrow_head(0.0, 0.0, 100.0, 0.0, 2.0);

        assert!(lx < 100.0 && rx < 100.0, "barbs at x={lx} and x={rx} are not behind the tip");
        assert!(
            (ly > 0.0 && ry < 0.0) || (ly < 0.0 && ry > 0.0),
            "barbs at y={ly} and y={ry} are on the same side of the shaft"
        );
        assert!((ly + ry).abs() < 1e-3, "barbs are not symmetric about the shaft: {ly} vs {ry}");
    }

    #[test]
    fn an_arrow_head_follows_the_direction_it_was_drawn() {
        // Dragged right to left, the head belongs on the LEFT end. Normalizing
        // the drag to a rectangle first would have lost this, which is why the
        // spec carries the drag's start and end rather than a box.
        let ((lx, _), (rx, _)) = arrow_head(100.0, 0.0, 0.0, 0.0, 2.0);
        assert!(lx > 0.0 && rx > 0.0, "barbs at x={lx} and x={rx} did not follow the reversed drag");
    }

    #[test]
    fn a_zero_length_arrow_does_not_produce_nan_coordinates() {
        // A click without a drag. Dividing by a zero-length shaft would write
        // NaN into the file, which corrupts the page rather than failing.
        let ((lx, ly), (rx, ry)) = arrow_head(50.0, 50.0, 50.0, 50.0, 2.0);
        for v in [lx, ly, rx, ry] {
            assert!(v.is_finite(), "arrow head produced a non-finite coordinate: {v}");
        }
    }

    #[test]
    fn a_hairline_arrow_still_gets_a_visible_head() {
        // Barb length scales with stroke width, so without a floor the finest
        // pen would produce a head a fraction of a point across: invisible.
        let ((lx, ly), _) = arrow_head(0.0, 0.0, 100.0, 0.0, 0.01);
        let reach = ((100.0f32 - lx).powi(2) + ly.powi(2)).sqrt();
        assert!(reach >= ARROW_HEAD_MIN - 0.001, "hairline arrow head reached only {reach}");
    }

    // ---- The tag that keeps a shape a shape ----

    fn contents_of(handle: u64, page_index: i32, index: usize) -> Option<String> {
        use pdfium_render::prelude::*;
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned()?;
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(page_index as u16).ok()?;
        let annotation = page.annotations().iter().nth(index)?;
        annotation.contents()
    }

    #[test]
    fn a_shapes_kind_and_style_survive_a_save_and_reopen() {
        // The whole point of the tag. Written as ink, a reopened shape is
        // otherwise indistinguishable from a freehand scribble, and can then
        // only be moved or deleted.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let mut spec = shape(SHAPE_ELLIPSE, 100.0, 100.0, 600.0, 400.0);
        spec.r = 0x12;
        spec.g = 0x34;
        spec.b = 0x56;
        spec.a = 0x78;
        assert_eq!(add_shape_annotations(handle, 1000, [spec].as_ptr(), 1), STATUS_OK_PDFIUM);

        free_render_result(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 200));
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        let contents = contents_of(reopened, 0, 0).expect("the reopened shape has no contents");
        let parsed = parse_shape_tag(&contents);
        assert!(parsed.is_some(), "tag did not parse: {contents}");
        let (kind, r, g, b, a, width, _, _) = parsed.unwrap();

        assert_eq!(kind, SHAPE_ELLIPSE);
        assert_eq!((r, g, b, a), (0x12, 0x34, 0x56, 0x78));
        assert!(width > 0.0, "width came back as {width}");

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn the_tag_round_trips_every_kind_and_direction() {
        for kind in [SHAPE_RECTANGLE, SHAPE_ELLIPSE, SHAPE_LINE, SHAPE_ARROW] {
            for (x1, x2, fx) in [(10.0, 90.0, true), (90.0, 10.0, false)] {
                for (y1, y2, fy) in [(20.0, 80.0, true), (80.0, 20.0, false)] {
                    let mut s = shape(kind, x1, y1, x2, y2);
                    s.a = 0xC0;
                    let parsed = parse_shape_tag(&shape_tag(&s, 2.5)).expect("tag did not parse");

                    assert_eq!(parsed.0, kind);
                    assert_eq!(parsed.4, 0xC0);
                    assert!((parsed.5 - 2.5).abs() < 0.001);
                    assert_eq!(parsed.6, fx, "x direction lost for kind {kind}");
                    assert_eq!(parsed.7, fy, "y direction lost for kind {kind}");
                }
            }
        }
    }

    #[test]
    fn anything_that_is_not_our_tag_is_left_alone() {
        // A comment written by the user, or an annotation from another app,
        // must never be mistaken for a shape and rebuilt as one.
        for text in ["", "Please review this", "AyaanShape", "AyaanShape:", "AyaanShape:9:FFFFFFFF:1:1:1"] {
            assert!(parse_shape_tag(text).is_none(), "wrongly parsed {text:?}");
        }

        // Malformed values must fail rather than produce a shape with a
        // nonsense width that then draws as nothing.
        assert!(parse_shape_tag("AyaanShape:0:GGGGGGGG:2:1:1").is_none());
        assert!(parse_shape_tag("AyaanShape:0:FFFFFFFF:0:1:1").is_none());
        assert!(parse_shape_tag("AyaanShape:0:FFFFFFFF:abc:1:1").is_none());
    }

    #[test]
    fn resizing_a_shape_redraws_it_in_the_new_box() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), 1), STATUS_OK_PDFIUM);
        free_render_result(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 200));

        let mut new_index = -1;
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, 1000, 100.0, 100.0, 800.0, 600.0, &mut new_index),
            STATUS_OK_PDFIUM
        );
        assert!(new_index >= 0, "resize did not report where the shape went");

        // Still exactly one annotation: the old one is gone, not left behind.
        let after = read_annotations(handle, 0);
        assert_eq!(after.len(), 1, "resize left a duplicate or removed too much");

        // And it occupies the larger box it was given.
        let (_, _, left, top, right, bottom) = after[0];
        assert!(right - left > 0.6, "resized width is only {}", right - left);
        assert!(bottom - top > 0.4, "resized height is only {}", bottom - top);

        // The tag survives the rebuild, so it can be resized again.
        let contents = contents_of(handle, 0, new_index as usize).expect("rebuilt shape lost its tag");
        assert_eq!(parse_shape_tag(&contents).map(|t| t.0), Some(SHAPE_RECTANGLE));

        close_document(handle);
    }

    #[test]
    fn a_resized_shape_still_draws() {
        // Rebuilding is only worth anything if the result is visible. An
        // annotation with correct bounds and no appearance would pass every
        // other check in this file.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);

        let specs = [shape(SHAPE_ELLIPSE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), 1), STATUS_OK_PDFIUM);
        let small = marked_pixels(handle, 0) - base;

        let mut new_index = -1;
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, 1000, 100.0, 100.0, 800.0, 600.0, &mut new_index),
            STATUS_OK_PDFIUM
        );

        let large = marked_pixels(handle, 0) - base;
        assert!(large > small, "the resized ellipse drew {large} pixels against {small} before");

        close_document(handle);
    }

    #[test]
    fn resizing_something_that_is_not_a_shape_is_refused_not_mangled() {
        // An ink stroke has no description to rebuild from. Refusing says so;
        // guessing would silently replace a scribble with a rectangle.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let points = [
            BurnPoint { x: 100.0, y: 100.0 },
            BurnPoint { x: 200.0, y: 180.0 },
            BurnPoint { x: 300.0, y: 120.0 },
        ];
        let strokes = [BurnStroke {
            page_index: 0,
            point_offset: 0,
            point_count: 3,
            width_px: 3.0,
            r: 0,
            g: 0,
            b: 0,
            a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), 1, points.as_ptr(), 3),
            STATUS_OK_PDFIUM
        );

        let mut new_index = -1;
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, 1000, 50.0, 50.0, 500.0, 500.0, &mut new_index),
            STATUS_UNSUPPORTED
        );

        // And the stroke is untouched.
        assert_eq!(read_annotations(handle, 0).len(), 1);

        close_document(handle);
    }

    #[test]
    fn resize_rejects_bad_input() {
        let handle = open_fixture();
        let mut idx = -1;
        assert_eq!(
            resize_shape_annotation(0, 0, 0, 1000, 0.0, 0.0, 10.0, 10.0, &mut idx),
            STATUS_INVALID_INPUT
        );
        assert_eq!(
            resize_shape_annotation(handle, -1, 0, 1000, 0.0, 0.0, 10.0, 10.0, &mut idx),
            STATUS_INVALID_INPUT
        );
        // An inverted or empty box would produce a shape with no extent.
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, 1000, 10.0, 10.0, 10.0, 50.0, &mut idx),
            STATUS_INVALID_INPUT
        );
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, 1000, 90.0, 10.0, 10.0, 50.0, &mut idx),
            STATUS_INVALID_INPUT
        );
        close_document(handle);
    }

    #[test]
    fn an_unknown_shape_kind_is_rejected_rather_than_guessed() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(99, 10.0, 10.0, 100.0, 100.0)];
        assert_eq!(
            add_shape_annotations(handle, 1000, specs.as_ptr(), 1),
            STATUS_INVALID_INPUT
        );
        close_document(handle);
    }

    #[test]
    fn shapes_reject_a_bad_handle_and_accept_an_empty_batch() {
        let specs = [shape(SHAPE_RECTANGLE, 0.0, 0.0, 10.0, 10.0)];
        assert_eq!(add_shape_annotations(0, 1000, specs.as_ptr(), 1), STATUS_INVALID_INPUT);

        let handle = open_fixture();
        assert_eq!(add_shape_annotations(handle, 0, specs.as_ptr(), 1), STATUS_INVALID_INPUT);
        assert_eq!(add_shape_annotations(handle, 1000, std::ptr::null(), 0), STATUS_OK_PDFIUM);
        close_document(handle);
    }

    #[test]
    #[test]
    fn an_ink_stroke_survives_save_and_reopen_as_an_editable_object() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let points = [
            BurnPoint { x: 100.0, y: 100.0 },
            BurnPoint { x: 200.0, y: 150.0 },
            BurnPoint { x: 300.0, y: 120.0 },
        ];
        let strokes = [BurnStroke {
            page_index: 0,
            point_offset: 0,
            point_count: 3,
            width_px: 4.0,
            r: 33, g: 150, b: 243, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), strokes.len(),
                                points.as_ptr(), points.len()),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        assert_eq!(before.len(), 1);
        assert_eq!(before[0].1, ANNOT_INK, "should be an /Ink annotation");

        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        let after = read_annotations(reopened, 0);
        println!("INK before={before:?} after={after:?}");
        assert_eq!(after.len(), 1, "the stroke should still be an annotation after reopening");
        assert_eq!(after[0].1, ANNOT_INK);

        for (b, a) in [(before[0].2, after[0].2), (before[0].3, after[0].3),
                       (before[0].4, after[0].4), (before[0].5, after[0].5)] {
            assert!((b - a).abs() < 0.001, "geometry drifted across the round trip: {b} vs {a}");
        }

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn ink_bounds_contain_the_whole_pen_width() {
        // PDFium clips an annotation's appearance to its bounding box, so a
        // box measured from the stroke's centre line would shave the outer
        // edge off every stroke, worst at the thickest pen. The box must be
        // padded by at least half the pen width.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // A dead flat horizontal line: with no padding its box would have zero
        // height and the stroke would vanish entirely.
        let points = [BurnPoint { x: 100.0, y: 200.0 }, BurnPoint { x: 400.0, y: 200.0 }];
        let strokes = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 2,
            width_px: 40.0,
            r: 0, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), strokes.len(),
                                points.as_ptr(), points.len()),
            STATUS_OK_PDFIUM
        );

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        let (_, _, left, top, right, bottom) = found[0];
        let height = bottom - top;
        println!("INK BOUNDS: l={left} t={top} r={right} b={bottom} height={height}");

        // 40px at capture width 1000 on a 200pt page is 8pt of pen, so the box
        // needs at least 8pt of height: 0.04 of the page width.
        assert!(height >= 0.04, "box height {height} does not cover a 40px pen");
        assert!(left < 0.1, "box should extend left of the first point");
        assert!(right > 0.4, "box should extend right of the last point");

        close_document(handle);
    }

    /// A tiny BGRA image: a solid square with a fully transparent left half,
    /// so transparency is actually exercised rather than assumed.
    fn test_stamp_pixels(w: i32, h: i32) -> Vec<u8> {
        let mut px = Vec::with_capacity((w * h * 4) as usize);
        for _y in 0..h {
            for x in 0..w {
                if x < w / 2 {
                    px.extend_from_slice(&[0, 0, 0, 0]);
                } else {
                    px.extend_from_slice(&[32, 64, 200, 255]);
                }
            }
        }
        px
    }

    #[test]
    fn a_png_stamp_survives_save_and_reopen_as_an_editable_object() {
        // The user's own stamps: place a PNG, and have it still be a movable,
        // resizable object after the file has been written and reopened.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let px = test_stamp_pixels(16, 16);

        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 200.0, 300.0, 400.0, 400.0,
                                 px.as_ptr(), px.len(), 16, 16),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        assert_eq!(before.len(), 1);
        assert_eq!(before[0].1, ANNOT_STAMP);

        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        let after = read_annotations(reopened, 0);
        println!("STAMP before={before:?} after={after:?}");
        assert_eq!(after.len(), 1, "the stamp should still be an annotation after reopening");
        assert_eq!(after[0].1, ANNOT_STAMP);

        for (b, a) in [(before[0].2, after[0].2), (before[0].3, after[0].3),
                       (before[0].4, after[0].4), (before[0].5, after[0].5)] {
            assert!((b - a).abs() < 0.001, "geometry drifted across the round trip: {b} vs {a}");
        }

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn a_stamp_lands_at_the_size_and_place_it_was_given() {
        // A PDF image object draws the unit square, so without a matrix every
        // stamp would come out one point square in the corner of the page no
        // matter what rectangle was asked for. This is that guard.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let px = test_stamp_pixels(8, 8);

        // Capture width 1000 on a 200pt page: a quarter of the width across,
        // starting a quarter in.
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 250.0, 100.0, 500.0, 350.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_OK_PDFIUM
        );

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        let (_, _, left, top, right, bottom) = found[0];
        println!("STAMP PLACE: l={left} t={top} r={right} b={bottom}");

        assert!((left - 0.25).abs() < 0.001, "left {left} should be 0.25");
        assert!((top - 0.10).abs() < 0.001, "top {top} should be 0.10");
        assert!((right - 0.50).abs() < 0.001, "right {right} should be 0.50");
        assert!((bottom - 0.35).abs() < 0.001, "bottom {bottom} should be 0.35");

        close_document(handle);
    }

    #[test]
    fn a_stamp_actually_draws_and_keeps_its_transparency() {
        // Bounds being right does not mean anything appeared. The annotation
        // could hold no image, or an image transformed off its own rect, and
        // every geometry assertion above would still pass. So this renders the
        // page and looks at the pixels.
        //
        // Comparing before against after, rather than checking for an absolute
        // colour, keeps it honest whatever the fixture already has on the page.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const W: i32 = 400;

        let sample = |result: &RenderResult, x: usize, y: usize| -> (u8, u8, u8) {
            let bytes = unsafe { std::slice::from_raw_parts(result.buffer, result.len as usize) };
            let i = (y * result.width as usize + x) * 4;
            (bytes[i], bytes[i + 1], bytes[i + 2]) // BGRA
        };

        let before = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        assert_eq!(before.status, STATUS_OK_PDFIUM);

        // Left half of these pixels is fully transparent, right half is opaque
        // B=32 G=64 R=200.
        let px = test_stamp_pixels(16, 16);
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 250.0, 100.0, 500.0, 350.0,
                                 px.as_ptr(), px.len(), 16, 16),
            STATUS_OK_PDFIUM
        );

        let after = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        assert_eq!(after.status, STATUS_OK_PDFIUM);

        // The stamp covers normalized x 0.25..0.50, y 0.10..0.35, so on a
        // 400px render that is x 100..200, y 40..140.
        let clear_px = (125, 90); // inside the transparent half
        let solid_px = (175, 90); // inside the opaque half

        let clear_before = sample(&before, clear_px.0, clear_px.1);
        let clear_after = sample(&after, clear_px.0, clear_px.1);
        let solid_before = sample(&before, solid_px.0, solid_px.1);
        let solid_after = sample(&after, solid_px.0, solid_px.1);

        println!("TRANSPARENT half: {clear_before:?} -> {clear_after:?}");
        println!("OPAQUE half:      {solid_before:?} -> {solid_after:?}");

        free_render_result(before);
        free_render_result(after);
        close_document(handle);

        assert_eq!(
            clear_before, clear_after,
            "the transparent half of the stamp changed the page, so alpha was lost"
        );

        assert_ne!(
            solid_before, solid_after,
            "the opaque half of the stamp did not draw at all"
        );
        let (b, g, r) = solid_after;
        assert!(
            r > 150 && g < 120 && b < 100,
            "opaque half rendered as B={b} G={g} R={r}, not the stamp's colour, \
             so the channels are being swapped somewhere"
        );
    }

    #[test]
    fn moving_a_stamp_moves_the_picture_and_not_just_the_handle() {
        // The trap this whole function exists to avoid. A stamp's picture
        // lives in an image object with its OWN matrix in page coordinates, so
        // setting the annotation's rectangle alone would move its handle and
        // hit box while the visible stamp stayed exactly where it was. Every
        // bounds assertion would pass and the user would see nothing move.
        //
        // So this checks the PIXELS: the old place must go back to how it was,
        // and the new place must now hold the stamp.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const W: i32 = 400;

        let sample = |result: &RenderResult, x: usize, y: usize| -> (u8, u8, u8) {
            let bytes = unsafe { std::slice::from_raw_parts(result.buffer, result.len as usize) };
            let i = (y * result.width as usize + x) * 4;
            (bytes[i], bytes[i + 1], bytes[i + 2])
        };

        let clean = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);

        // Fully opaque so every sampled pixel is the stamp.
        let px: Vec<u8> = std::iter::repeat([32u8, 64, 200, 255]).take(64).flatten().collect();
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 100.0, 100.0, 200.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_OK_PDFIUM
        );

        // Normalized 0.1..0.2 both axes, so pixels 40..80 on a 400px render.
        let old_spot = (60, 60);
        let placed = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        assert_ne!(sample(&clean, old_spot.0, old_spot.1), sample(&placed, old_spot.0, old_spot.1),
                   "the stamp did not draw in the first place");

        // Move it to 0.5..0.6, pixels 200..240.
        assert_eq!(
            set_annotation_bounds(handle, 0, 0, 1000, 500.0, 500.0, 600.0, 600.0),
            STATUS_OK_PDFIUM
        );
        let new_spot = (220, 220);
        let moved = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);

        let old_after = sample(&moved, old_spot.0, old_spot.1);
        let new_after = sample(&moved, new_spot.0, new_spot.1);
        let clean_old = sample(&clean, old_spot.0, old_spot.1);
        println!("MOVE: old spot clean={clean_old:?} after={old_after:?},                   new spot after={new_after:?}");

        // And the annotation's own rectangle should agree with where it drew.
        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        assert!((found[0].2 - 0.5).abs() < 0.001, "rect left is {}", found[0].2);

        free_render_result(clean);
        free_render_result(placed);
        free_render_result(moved);
        close_document(handle);

        assert_eq!(
            old_after, clean_old,
            "the old position did not go back to how the page looked before the stamp,              so the picture is still drawn there and only the rectangle moved"
        );
        assert_eq!(
            new_after, (32, 64, 200),
            "nothing was drawn at the new position"
        );
    }

    #[test]
    fn resizing_a_transparent_stamp_keeps_it_transparent() {
        // The case that matters for a real signature or seal, which is a
        // shape on a transparent background.
        //
        // Rebuilding reads the image back out of the annotation, and the RAW
        // image does not carry transparency: alpha lives separately in a soft
        // mask, so rebuilding from the raw bitmap would turn every signature
        // into an opaque rectangle covering the text underneath. The processed
        // bitmap has the mask applied, which is why it is used.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const W: i32 = 400;

        let sample = |r: &RenderResult, x: usize, y: usize| -> (u8, u8, u8) {
            let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
            let i = (y * r.width as usize + x) * 4;
            (bytes[i], bytes[i + 1], bytes[i + 2])
        };

        // Left half transparent, right half solid.
        let px = test_stamp_pixels(16, 16);
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 200.0, 200.0, 600.0, 600.0,
                                 px.as_ptr(), px.len(), 16, 16),
            STATUS_OK_PDFIUM
        );

        let clean = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let clear_before = sample(&clean, 120, 160);   // in the transparent half
        free_render_result(clean);

        // Grow it. Normalized 0.2..0.6 becomes 0.2..0.7, so the transparent
        // half still covers x around 0.3, i.e. pixel 120.
        let mut new_index = -1;
        assert_eq!(
            resize_annotation(handle, 0, 0, 1000, 200.0, 200.0, 700.0, 700.0, &mut new_index),
            STATUS_OK_PDFIUM
        );

        let after = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let clear_after = sample(&after, 120, 160);
        // Resized to normalized 0.2..0.7, which on a 400px render is pixels
        // 80..280: the transparent half covers 80..180 and the solid half
        // 180..280. Sampling at 300 would land OUTSIDE the stamp entirely,
        // which is a wrong probe rather than a wrong result.
        let solid_after = sample(&after, 230, 160);
        free_render_result(after);

        println!("TRANSPARENCY: clear {clear_before:?} -> {clear_after:?}, solid {solid_after:?}");
        close_document(handle);

        assert_eq!(
            clear_after, clear_before,
            "the transparent half became opaque when resized, so the mask was lost"
        );
        assert_eq!(solid_after, (32, 64, 200), "the solid half lost its colour when resized");
    }
    #[test]
    fn resizing_a_stamp_grows_the_picture_not_just_the_box() {
        // The whole point. set_annotation_bounds refuses to scale a stamp
        // because the image cannot be transformed from outside, and the
        // failure mode it was protecting against is a bigger rectangle with
        // the same small picture inside it. So this checks PIXELS at a point
        // that is OUTSIDE the original stamp and INSIDE the resized one.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const W: i32 = 400;

        let sample = |r: &RenderResult, x: usize, y: usize| -> (u8, u8, u8) {
            let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
            let i = (y * r.width as usize + x) * 4;
            (bytes[i], bytes[i + 1], bytes[i + 2])
        };

        // Opaque, so every sampled pixel inside it is the stamp.
        let px: Vec<u8> = std::iter::repeat([32u8, 64, 200, 255]).take(64).flatten().collect();
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 100.0, 100.0, 200.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_OK_PDFIUM
        );

        // Normalized 0.1..0.2, so pixel 120 (0.3) is well outside it.
        let probe = (120, 120);
        let small = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let before = sample(&small, probe.0, probe.1);
        free_render_result(small);
        assert_ne!(before, (32, 64, 200), "the probe should start outside the stamp");

        // Grow to 0.1..0.4, which now covers the probe.
        let mut new_index = -1;
        let status = resize_annotation(handle, 0, 0, 1000, 100.0, 100.0, 400.0, 400.0,
                                       &mut new_index);
        assert_eq!(status, STATUS_OK_PDFIUM, "resize failed");
        assert!(new_index >= 0, "resize did not report the rebuilt annotation's index");

        let big = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let after = sample(&big, probe.0, probe.1);
        free_render_result(big);

        println!("RESIZE: probe {before:?} -> {after:?}, new index {new_index}");
        assert_eq!(after, (32, 64, 200), "the picture did not grow with its rectangle");

        // Still exactly one annotation: rebuilding must replace, not duplicate.
        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1, "resize left a duplicate behind: {found:?}");
        assert_eq!(found[0].0, new_index, "the reported index is not where it ended up");
        assert_eq!(found[0].1, ANNOT_STAMP, "the rebuilt annotation is not a stamp");

        // And its rectangle is the one asked for.
        assert!((found[0].2 - 0.1).abs() < 0.001, "left is {}", found[0].2);
        assert!((found[0].4 - 0.4).abs() < 0.001, "right is {}", found[0].4);

        close_document(handle);
    }

    #[test]
    fn resizing_a_highlight_uses_the_direct_path_and_keeps_its_index() {
        // A highlight's shape is its quad points, which CAN be transformed in
        // place, so it must not be rebuilt: rebuilding would move it to the
        // end of the page's list and invalidate an index the caller still
        // holds, for no reason.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let quads = [HighlightQuad { left: 300.0, top: 300.0, right: 700.0, bottom: 380.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1,
            r: 255, g: 235, b: 59, a: 200,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM
        );

        let mut new_index = -1;
        assert_eq!(
            resize_annotation(handle, 0, 0, 1000, 300.0, 300.0, 900.0, 460.0, &mut new_index),
            STATUS_OK_PDFIUM
        );
        assert_eq!(new_index, 0, "a highlight should keep its index");

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        assert!((found[0].4 - 0.9).abs() < 0.001, "right is {}", found[0].4);

        close_document(handle);
    }

    #[test]
    fn resizing_ink_reports_that_it_cannot_rather_than_mangling_it() {
        // Ink also owns page objects, but its shape is a path rather than an
        // image, so rebuilding would mean reading path segments back out and
        // re-emitting them. Refusing is honest; silently leaving the stroke at
        // its old size inside a bigger box is not.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let pts = [BurnPoint { x: 300.0, y: 300.0 }, BurnPoint { x: 500.0, y: 400.0 }];
        let strokes = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 2,
            width_px: 6.0, r: 200, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), 1, pts.as_ptr(), 2),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        let mut new_index = -1;
        assert_eq!(
            resize_annotation(handle, 0, 0, 1000, 300.0, 300.0, 900.0, 700.0, &mut new_index),
            STATUS_UNSUPPORTED
        );

        // A refusal must leave the stroke exactly as it was.
        let after = read_annotations(handle, 0);
        assert_eq!(after.len(), 1, "the refused resize removed the stroke");
        assert_eq!(before[0], after[0], "the refused resize changed the stroke anyway");

        close_document(handle);
    }

    #[test]
    fn a_stamp_can_be_resized_after_being_saved_and_reopened() {
        // The case the app cannot do for itself: a stamp in a file it never
        // placed, so it has no source pixels. Resizing has to read them back
        // out of the annotation, which is the whole reason this lives in the
        // core rather than in the app.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let px: Vec<u8> = std::iter::repeat([32u8, 64, 200, 255]).take(64).flatten().collect();
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 100.0, 100.0, 200.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_OK_PDFIUM
        );
        free_render_result(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 400));

        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        close_document(handle);
        free_byte_buffer(saved);
        assert_ne!(reopened, 0);

        let mut new_index = -1;
        assert_eq!(
            resize_annotation(reopened, 0, 0, 1000, 100.0, 100.0, 500.0, 500.0, &mut new_index),
            STATUS_OK_PDFIUM,
            "a reopened stamp should still be resizable"
        );

        let found = read_annotations(reopened, 0);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].1, ANNOT_STAMP);
        assert!((found[0].4 - 0.5).abs() < 0.001, "right is {}", found[0].4);

        close_document(reopened);
    }

    #[test]
    fn scaling_an_object_owning_annotation_reports_that_it_cannot() {
        // A limit PDFium imposes, pinned so it cannot regress into silence.
        //
        // Stamps and ink keep their drawing in page objects. PDFium carries
        // that appearance along when the rectangle MOVES, but it will not
        // scale it, and transforming the contained objects from here does
        // nothing: an object taken from objects_mut().get() is detached, and
        // apply_matrix on it never reaches the stored annotation. That was
        // confirmed by running the same resize with the transform skipped and
        // getting a pixel-identical render.
        //
        // Silently leaving the picture at its old size inside a bigger box is
        // the worst outcome, so a scale is refused and the caller re-adds the
        // stamp at the new size instead, which it can do because it holds the
        // source pixels.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let px: Vec<u8> = std::iter::repeat([32u8, 64, 200, 255]).take(64).flatten().collect();
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 100.0, 100.0, 200.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_OK_PDFIUM
        );

        // Same size, different place: a move, which works.
        assert_eq!(
            set_annotation_bounds(handle, 0, 0, 1000, 500.0, 500.0, 600.0, 600.0),
            STATUS_OK_PDFIUM,
            "moving a stamp should work"
        );

        // Different size: refused, and distinguishable from bad input.
        assert_eq!(
            set_annotation_bounds(handle, 0, 0, 1000, 500.0, 500.0, 800.0, 800.0),
            STATUS_UNSUPPORTED,
            "scaling a stamp should report that it cannot, not fail as bad input"
        );

        // The refusal must leave the annotation untouched.
        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        assert!((found[0].2 - 0.5).abs() < 0.001, "left moved to {} despite the refusal", found[0].2);
        assert!((found[0].4 - 0.6).abs() < 0.001, "right moved to {} despite the refusal", found[0].4);

        close_document(handle);
    }
    /// Pixels changed on page 0 by whatever `build` does to the document.
    fn pixels_changed_by(build: &dyn Fn(u64)) -> usize {
        const W: i32 = 400;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let bb = unsafe { std::slice::from_raw_parts(before.buffer, before.len as usize) }.to_vec();

        build(handle);

        let after = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, W);
        let ab = unsafe { std::slice::from_raw_parts(after.buffer, after.len as usize) };
        let changed = bb.chunks(4).zip(ab.chunks(4)).filter(|(a, b)| a != b).count();

        free_render_result(before);
        free_render_result(after);
        close_document(handle);
        changed
    }

    #[test]
    fn every_kind_of_annotation_we_create_actually_draws() {
        // The test that was missing, and whose absence let a highlight that
        // drew NOTHING pass as working through three earlier steps. Existing
        // with the right subtype, geometry and colour is not the same as
        // appearing on the page, and only one of those is what a user sees.
        //
        // Each kind is placed on blank paper at the same spot so the fixture's
        // own printing cannot be mistaken for the mark.
        let highlight = pixels_changed_by(&|h| {
            let quads = [HighlightQuad { left: 500.0, top: 500.0, right: 600.0, bottom: 600.0 }];
            let specs = [HighlightSpec {
                page_index: 0, quad_offset: 0, quad_count: 1,
                r: 255, g: 235, b: 59, a: 255,
            }];
            add_highlight_annotations(h, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1);
        });

        let ink = pixels_changed_by(&|h| {
            let pts = [BurnPoint { x: 500.0, y: 500.0 }, BurnPoint { x: 600.0, y: 600.0 }];
            let strokes = [BurnStroke {
                page_index: 0, point_offset: 0, point_count: 2,
                width_px: 20.0, r: 255, g: 0, b: 0, a: 255,
            }];
            add_ink_annotations(h, 1000, strokes.as_ptr(), 1, pts.as_ptr(), 2);
        });

        let stamp = pixels_changed_by(&|h| {
            let px: Vec<u8> = std::iter::repeat([32u8, 64, 200, 255]).take(64).flatten().collect();
            add_stamp_annotation(h, 0, 1000, 500.0, 500.0, 600.0, 600.0,
                                 px.as_ptr(), px.len(), 8, 8);
        });

        let note = pixels_changed_by(&|h| {
            let t = std::ffi::CString::new("x").unwrap();
            let notes = [BurnNote { page_index: 0, x: 500.0, y: 500.0, text: t.as_ptr() }];
            add_note_annotations(h, 1000, notes.as_ptr(), 1);
        });

        println!("DRAWS: highlight={highlight} ink={ink} stamp={stamp} note={note}");

        assert!(highlight > 0, "a highlight draws nothing");
        assert!(ink > 0, "an ink stroke draws nothing");
        assert!(stamp > 0, "a stamp draws nothing");
        assert!(note > 0, "a note draws nothing");
    }

    #[test]
    fn highlight_quad_points_use_the_order_pdf_defines() {
        // The root cause of the highlight drawing nothing, pinned directly.
        //
        // PDF orders /QuadPoints top-left, top-right, bottom-left,
        // bottom-right. PdfQuadPoints::from_rect winds them counter-clockwise
        // from the bottom-left instead, so PDFium read two corners with the
        // same x, computed a zero-width rectangle, and drew nothing at all
        // while every other property of the annotation stayed correct.
        use pdfium_render::prelude::*;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // 0.5..0.6 of a 200pt page: x 100..120, and y 100 down to 80.
        let quads = [HighlightQuad { left: 500.0, top: 500.0, right: 600.0, bottom: 600.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1,
            r: 255, g: 235, b: 59, a: 255,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM
        );

        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let mut page = g.pages().get(0).unwrap();
        let mut annot = page.annotations_mut().get(0).unwrap();

        let PdfPageAnnotation::Highlight(h) = &mut annot else {
            panic!("expected a highlight");
        };
        let points = h.attachment_points_mut();
        assert_eq!(points.len(), 1);
        let q = points.get(0).unwrap();
        println!("QUAD: ({},{}) ({},{}) ({},{}) ({},{})",
                 q.x1.value, q.y1.value, q.x2.value, q.y2.value,
                 q.x3.value, q.y3.value, q.x4.value, q.y4.value);

        assert_eq!((q.x1.value, q.y1.value), (100.0, 100.0), "pair 1 must be TOP-LEFT");
        assert_eq!((q.x2.value, q.y2.value), (120.0, 100.0), "pair 2 must be TOP-RIGHT");
        assert_eq!((q.x3.value, q.y3.value), (100.0, 80.0), "pair 3 must be BOTTOM-LEFT");
        assert_eq!((q.x4.value, q.y4.value), (120.0, 80.0), "pair 4 must be BOTTOM-RIGHT");

        // The two corners PDFium derives its rectangle from must not collapse.
        assert_ne!(q.x2.value, q.x3.value,
                   "top-right and bottom-left share an x, so the rectangle is zero-width");

        drop(annot);
        drop(page);
        drop(g);
        drop(_guard);
        close_document(handle);
    }
    #[test]
    fn moving_a_highlight_moves_its_rectangle_and_quads() {
        // Geometry only. Checking the drawn pixels would be better, but a
        // created /Highlight does not render at all yet, which is tracked
        // separately: PDFium does not generate an appearance for it and the
        // safe API exposes no way to supply one.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let quads = [HighlightQuad { left: 500.0, top: 500.0, right: 600.0, bottom: 600.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1,
            r: 255, g: 235, b: 59, a: 255,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM
        );

        let before = read_annotations(handle, 0);
        assert_eq!(before.len(), 1);
        assert!((before[0].2 - 0.5).abs() < 0.001);

        assert_eq!(
            set_annotation_bounds(handle, 0, 0, 1000, 700.0, 700.0, 800.0, 800.0),
            STATUS_OK_PDFIUM
        );

        let after = read_annotations(handle, 0);
        assert_eq!(after.len(), 1);
        println!("HL MOVE: {before:?} -> {after:?}");
        assert!((after[0].2 - 0.7).abs() < 0.001, "left is {}", after[0].2);
        assert!((after[0].3 - 0.7).abs() < 0.001, "top is {}", after[0].3);
        assert!((after[0].4 - 0.8).abs() < 0.001, "right is {}", after[0].4);

        close_document(handle);
    }
    /// One round of what the crashing C# test does per iteration:
    /// render the page the annotation lives on, then edit it.
    fn one_render_edit_round(handle: u64, round: i32) {
        // Renders FIRST, so the page's annotation state is built by the
        // renderer before the edits touch it.
        free_render_result(render_low_res(handle, 0, 900));
        free_render_result(render_tile(handle, 0, 4, (round % 16) as i32, ((round / 16) % 16) as i32));

        let quads = [HighlightQuad { left: 300.0, top: 300.0, right: 700.0, bottom: 380.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1,
            r: 255, g: 235, b: 59, a: 200,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM, "add failed on round {round}"
        );

        // Render BETWEEN the add and the read, matching the app: the page
        // redraws the moment the annotation lands, before any click.
        free_render_result(render_low_res(handle, 0, 900));

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1, "read lost the annotation on round {round}");
        let index = found[0].0;

        let _ = set_annotation_bounds(handle, 0, index, 1000, 350.0, 350.0, 750.0, 430.0);

        // And again after the move, matching InvalidateLoadedPage.
        free_render_result(render_low_res(handle, 0, 900));

        assert_eq!(delete_annotation(handle, 0, index), STATUS_OK_PDFIUM,
                   "delete failed on round {round}");
    }




    #[test]
    fn reading_annotations_after_a_render_does_not_crash() {
        // The minimal form of the crash the user reported from the app, found
        // by bisection: add an annotation, RENDER the page, then read the
        // page's annotations. Rendering generates appearance streams for
        // annotations that lack them, and FPDFAnnot_GetColor access-violates
        // on an annotation that has one, so any colour query in the read path
        // turns this exact sequence into a process kill. get_annotations must
        // therefore never ask for colour; this pins that.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        for _round in 0..25i32 {
            let quads = [HighlightQuad { left: 300.0, top: 300.0, right: 700.0, bottom: 380.0 }];
            let specs = [HighlightSpec {
                page_index: 0, quad_offset: 0, quad_count: 1,
                r: 255, g: 235, b: 59, a: 200,
            }];
            assert_eq!(
                add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
                STATUS_OK_PDFIUM
            );

            free_render_result(render_low_res(handle, 0, 900));

            let found = read_annotations(handle, 0);
            let index = found.last().expect("read lost the annotation").0;
            assert_eq!(delete_annotation(handle, 0, index), STATUS_OK_PDFIUM);
        }
        close_document(handle);
    }

    #[test]
    fn renders_interleaved_with_edits_single_threaded() {
        // The C# reproduction that faults changes TWO things against the
        // passing test: it adds threads AND it adds renders between the edits.
        // This runs the same mix on ONE thread. If this faults, the crash was
        // never a race: it is deterministic state corruption from mixing
        // rendering and annotation editing, which is a very different, much
        // easier bug.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        for round in 0..25 {
            one_render_edit_round(handle, round);
        }
        close_document(handle);
    }

    #[test]
    fn renders_racing_edits_across_threads() {
        // The other half: same mix, with renders genuinely concurrent on the
        // SAME document. The cargo suite runs tests in parallel, but each test
        // opens its own document, so same-document concurrency has never been
        // exercised natively. This is the C# reproduction minus the C#.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let mut workers = Vec::new();
        for t in 0..3u64 {
            let stop = stop.clone();
            workers.push(std::thread::spawn(move || {
                let mut i = t;
                while !stop.load(std::sync::atomic::Ordering::Relaxed) {
                    free_render_result(render_low_res(handle, (i % 20) as i32, 900));
                    free_render_result(render_tile(handle, 0, 4, (i % 16) as i32, ((i / 16) % 16) as i32));
                    i += 1;
                }
            }));
        }

        for round in 0..25 {
            one_render_edit_round(handle, round);
        }

        stop.store(true, std::sync::atomic::Ordering::Relaxed);
        for w in workers {
            let _ = w.join();
        }
        close_document(handle);
    }

    #[test]
    fn the_exact_sequence_the_app_performs_on_a_loaded_annotation() {
        // Mirrors what the app does when you click a mark that was already in
        // the file and drag it: read the page's annotations, then move one by
        // index, on a document that carries BOTH a highlight and an ink
        // stroke, since ink is the subtype with the colour-query landmine.
        //
        // Doing it in-process here rather than by launching the app, because
        // an access violation takes the whole app down with a dialog and no
        // log line, which tells you nothing about where it happened.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let quads = [HighlightQuad { left: 300.0, top: 300.0, right: 700.0, bottom: 380.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1,
            r: 255, g: 235, b: 59, a: 200,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM
        );

        let pts = [BurnPoint { x: 300.0, y: 500.0 },
                   BurnPoint { x: 500.0, y: 600.0 },
                   BurnPoint { x: 700.0, y: 500.0 }];
        let strokes = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 3,
            width_px: 8.0, r: 220, g: 30, b: 30, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), 1, pts.as_ptr(), 3),
            STATUS_OK_PDFIUM
        );

        // Save and reopen, so these are genuinely LOADED annotations rather
        // than ones still warm in memory. This is the state the app is in.
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        close_document(handle);
        free_byte_buffer(saved);
        assert_ne!(reopened, 0);

        let found = read_annotations(reopened, 0);
        println!("APP SEQUENCE: read {} annotations: {found:?}", found.len());
        assert_eq!(found.len(), 2);

        // Move each one, including a re-read in between, exactly as the app
        // does when it invalidates the page after an edit.
        for (index, subtype, left, top, right, bottom) in found {
            let (w, h) = (right - left, bottom - top);
            let status = set_annotation_bounds(
                reopened, 0, index, 1000,
                (left + 0.1) * 1000.0, (top + 0.1) * 1000.0,
                (left + 0.1 + w) * 1000.0, (top + 0.1 + h) * 1000.0);
            println!("APP SEQUENCE: move #{index} subtype={subtype} -> {status}");
            assert!(
                status == STATUS_OK_PDFIUM || status == STATUS_UNSUPPORTED,
                "moving annotation {index} of subtype {subtype} failed with {status}"
            );

            let again = read_annotations(reopened, 0);
            assert_eq!(again.len(), 2, "re-reading after a move lost an annotation");
        }

        // And a render, since the app redraws the page after every edit.
        let r = render_region(reopened, 0, 0.0, 0.0, 1.0, 1.0, 400);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        free_render_result(r);

        // Then delete one and re-read, which is where stale indices bite.
        assert_eq!(delete_annotation(reopened, 0, 0), STATUS_OK_PDFIUM);
        let left_over = read_annotations(reopened, 0);
        println!("APP SEQUENCE: after delete {left_over:?}");
        assert_eq!(left_over.len(), 1);

        close_document(reopened);
    }

    #[test]
    fn deleting_an_annotation_removes_it() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let text = std::ffi::CString::new("gone").unwrap();
        let notes = [BurnNote { page_index: 0, x: 50.0, y: 50.0, text: text.as_ptr() }];
        assert_eq!(add_note_annotations(handle, 1000, notes.as_ptr(), 1), STATUS_OK_PDFIUM);

        let quads = [HighlightQuad { left: 100.0, top: 100.0, right: 200.0, bottom: 140.0 }];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 1, r: 0, g: 255, b: 0, a: 128,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), 1, quads.as_ptr(), 1),
            STATUS_OK_PDFIUM
        );
        assert_eq!(read_annotations(handle, 0).len(), 2);

        // Remove the first: the second must survive and become index 0.
        assert_eq!(delete_annotation(handle, 0, 0), STATUS_OK_PDFIUM);
        let left = read_annotations(handle, 0);
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].1, ANNOT_HIGHLIGHT, "the wrong annotation was deleted");
        assert_eq!(left[0].0, 0, "indices should close up after a delete");

        assert_eq!(delete_annotation(handle, 0, 0), STATUS_OK_PDFIUM);
        assert_eq!(read_annotations(handle, 0).len(), 0);

        // Deleting past the end is refused rather than doing something worse.
        assert_eq!(delete_annotation(handle, 0, 0), STATUS_INVALID_INPUT);
        assert_eq!(delete_annotation(handle, 0, 99), STATUS_INVALID_INPUT);
        assert_eq!(delete_annotation(0, 0, 0), STATUS_INVALID_INPUT);

        close_document(handle);
    }

    #[test]
    fn bounds_edits_that_do_not_add_up_are_rejected() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let text = std::ffi::CString::new("n").unwrap();
        let notes = [BurnNote { page_index: 0, x: 50.0, y: 50.0, text: text.as_ptr() }];
        assert_eq!(add_note_annotations(handle, 1000, notes.as_ptr(), 1), STATUS_OK_PDFIUM);

        // Degenerate and inverted rectangles.
        assert_eq!(set_annotation_bounds(handle, 0, 0, 1000, 10.0, 10.0, 10.0, 50.0),
                   STATUS_INVALID_INPUT);
        assert_eq!(set_annotation_bounds(handle, 0, 0, 1000, 50.0, 10.0, 10.0, 50.0),
                   STATUS_INVALID_INPUT);
        // No such annotation.
        assert_eq!(set_annotation_bounds(handle, 0, 7, 1000, 10.0, 10.0, 50.0, 50.0),
                   STATUS_INVALID_INPUT);
        assert_eq!(set_annotation_bounds(handle, 0, 0, 0, 10.0, 10.0, 50.0, 50.0),
                   STATUS_INVALID_INPUT);

        close_document(handle);
    }

    #[test]
    fn stamp_input_that_does_not_add_up_is_rejected() {
        // The pixel buffer goes straight to PDFium, so a length that disagrees
        // with the stated dimensions would read past the end of it.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let px = test_stamp_pixels(8, 8);

        // Buffer says 8x8 but the dimensions claim 32x32.
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 0.0, 0.0, 100.0, 100.0,
                                 px.as_ptr(), px.len(), 32, 32),
            STATUS_INVALID_INPUT
        );

        // A rectangle with no area would be invisible and impossible to grab.
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 100.0, 100.0, 100.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_INVALID_INPUT
        );
        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 200.0, 100.0, 100.0, 200.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_INVALID_INPUT,
            "a right edge left of the left edge is not a rectangle"
        );

        assert_eq!(
            add_stamp_annotation(handle, 0, 1000, 0.0, 0.0, 10.0, 10.0,
                                 std::ptr::null(), 0, 8, 8),
            STATUS_INVALID_INPUT
        );
        assert_eq!(
            add_stamp_annotation(0, 0, 1000, 0.0, 0.0, 10.0, 10.0,
                                 px.as_ptr(), px.len(), 8, 8),
            STATUS_INVALID_INPUT
        );

        close_document(handle);
    }

    #[test]
    fn reading_a_page_holding_object_owning_annotations_does_not_crash() {
        // Regression guard for a hard crash, not a wrong answer. Asking PDFium
        // for the colour of an annotation that owns page objects, which Ink and
        // Stamp both do, access-violates: it takes the process down rather than
        // returning an error, so no amount of catch_unwind helps.
        //
        // get_annotations shipped without this guard because it had only ever
        // been pointed at /Text and /Highlight. Opening any PDF containing an
        // ink annotation would have killed the app.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let points = [BurnPoint { x: 10.0, y: 10.0 }, BurnPoint { x: 90.0, y: 90.0 }];
        let strokes = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 2,
            width_px: 3.0, r: 200, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, strokes.as_ptr(), 1, points.as_ptr(), points.len()),
            STATUS_OK_PDFIUM
        );

        // Reaching the next line at all is the assertion.
        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].1, ANNOT_INK);

        close_document(handle);
    }

    #[test]
    fn ink_input_that_does_not_add_up_is_rejected() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let points = [BurnPoint { x: 0.0, y: 0.0 }, BurnPoint { x: 10.0, y: 10.0 }];

        let overrun = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 9,
            width_px: 2.0, r: 0, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, overrun.as_ptr(), 1, points.as_ptr(), points.len()),
            STATUS_INVALID_INPUT
        );

        // A single point is not a stroke.
        let single = [BurnStroke {
            page_index: 0, point_offset: 0, point_count: 1,
            width_px: 2.0, r: 0, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_ink_annotations(handle, 1000, single.as_ptr(), 1, points.as_ptr(), points.len()),
            STATUS_INVALID_INPUT
        );

        close_document(handle);
    }

    #[test]
    fn a_highlight_over_several_lines_is_one_annotation() {
        // A highlight spanning three lines of text must behave as ONE object,
        // so that a click selects all of it and a delete removes all of it.
        // Writing one annotation per line would leave the user picking
        // fragments apart, which is exactly what a highlight is not.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let quads = [
            HighlightQuad { left: 100.0, top: 100.0, right: 500.0, bottom: 130.0 },
            HighlightQuad { left: 100.0, top: 140.0, right: 480.0, bottom: 170.0 },
            HighlightQuad { left: 100.0, top: 180.0, right: 300.0, bottom: 210.0 },
        ];
        let specs = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 3,
            r: 0, g: 255, b: 0, a: 100,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, specs.as_ptr(), specs.len(),
                                      quads.as_ptr(), quads.len()),
            STATUS_OK_PDFIUM
        );

        let found = read_annotations(handle, 0);
        assert_eq!(found.len(), 1, "three lines must be one annotation, not three");

        // Its bounds must cover the union of all three lines, or hit-testing
        // and the selection marquee would miss part of the highlight.
        let (_, _, left, top, right, bottom) = found[0];
        println!("UNION: l={left} t={top} r={right} b={bottom}");
        assert!((left - 0.1).abs() < 0.001, "left should be the leftmost quad");
        assert!((right - 0.5).abs() < 0.001, "right should be the widest quad");
        assert!((top - 0.1).abs() < 0.001, "top should be the highest quad");
        assert!((bottom - 0.21).abs() < 0.001, "bottom should be the lowest quad");

        close_document(handle);
    }

    #[test]
    fn highlight_input_that_does_not_add_up_is_rejected() {
        // The offset/count pair indexes a shared flat array, so a bad pair
        // would read past the end of it. Cheap to check, catastrophic to miss.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let quads = [HighlightQuad { left: 0.0, top: 0.0, right: 10.0, bottom: 10.0 }];

        let overrun = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 5,
            r: 255, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, overrun.as_ptr(), overrun.len(),
                                      quads.as_ptr(), quads.len()),
            STATUS_INVALID_INPUT
        );

        let empty = [HighlightSpec {
            page_index: 0, quad_offset: 0, quad_count: 0,
            r: 255, g: 0, b: 0, a: 255,
        }];
        assert_eq!(
            add_highlight_annotations(handle, 1000, empty.as_ptr(), empty.len(),
                                      quads.as_ptr(), quads.len()),
            STATUS_INVALID_INPUT
        );

        assert_eq!(add_highlight_annotations(0, 1000, overrun.as_ptr(), 1,
                                             quads.as_ptr(), 1), STATUS_INVALID_INPUT);
        assert_eq!(add_highlight_annotations(handle, 0, overrun.as_ptr(), 1,
                                             quads.as_ptr(), 1), STATUS_INVALID_INPUT);

        close_document(handle);
    }

    #[test]
    fn snapshot_captures_burned_annotations() {
        // Annotation undo at document granularity: a snapshot taken BEFORE a
        // burn must reopen without the burned objects.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let clean = snapshot_document(handle);
        let before = page_object_count(handle, 0);

        let rects = [BurnRect {
            page_index: 0,
            left: 100.0, top: 100.0, right: 500.0, bottom: 200.0,
            r: 255, g: 255, b: 0, a: 128,
        }];
        assert_eq!(
            burn_annotations(handle, 1000, rects.as_ptr(), rects.len(),
                             std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_OK_PDFIUM
        );
        assert_eq!(page_object_count(handle, 0), before + 1);

        let restored = open_document_from_bytes(clean.data, clean.len);
        assert_eq!(
            page_object_count(restored, 0), before,
            "restoring the pre-burn snapshot should drop the burned object"
        );

        close_document(restored);
        close_document(handle);
        free_byte_buffer(clean);
    }

    #[test]
    fn snapshot_apis_reject_bad_input() {
        assert_eq!(snapshot_document(0).status, STATUS_INVALID_INPUT);
        assert_eq!(open_document_from_bytes(std::ptr::null(), 10), 0);
        let one = [0u8];
        assert_eq!(open_document_from_bytes(one.as_ptr(), 0), 0);
        // Not a PDF at all.
        let garbage = *b"this is not a pdf";
        assert_eq!(open_document_from_bytes(garbage.as_ptr(), garbage.len()), 0);
        // Freeing an error buffer must not crash.
        free_byte_buffer(ByteBuffer::err(STATUS_INVALID_INPUT));
    }

    /// Number of annotations on a page.
    fn page_annotation_count(handle: u64, page_index: i32) -> usize {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(page_index as u16).unwrap();
        page.annotations().len() as usize
    }

    #[test]
    fn notes_survive_a_save_with_their_text_intact() {
        // The whole point of a note is its text. Burning it to a marker
        // graphic would keep the mark and lose the words, which is the data
        // loss this exists to prevent, so the test reads the TEXT back.
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = page_annotation_count(handle, 0);

        let body = std::ffi::CString::new("Check this figure against table 3").unwrap();
        let notes = [BurnNote { page_index: 0, x: 200.0, y: 300.0, text: body.as_ptr() }];
        assert_eq!(
            add_note_annotations(handle, 1000, notes.as_ptr(), notes.len()),
            STATUS_OK_PDFIUM
        );
        assert_eq!(page_annotation_count(handle, 0), before + 1);

        let mut path = std::env::temp_dir();
        path.push(format!("render_core_note_{}.pdf", std::process::id()));
        let path_str = path.to_str().unwrap().to_owned();
        let c_path = std::ffi::CString::new(path_str.clone()).unwrap();
        assert_eq!(save_document(handle, c_path.as_ptr()), STATUS_OK_PDFIUM);
        close_document(handle);

        let reopened = open_fixture_named(&path_str);
        assert_eq!(page_annotation_count(reopened, 0), before + 1, "note did not survive the save");

        let found = {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&reopened).cloned().unwrap();
            let g = lock(&doc);
            let page = g.pages().get(0).unwrap();
            page.annotations()
                .iter()
                .filter_map(|a| a.contents())
                .any(|c| c.contains("table 3"))
        };
        assert!(found, "the note's TEXT was lost; only a marker survived");

        close_document(reopened);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn notes_land_on_their_own_page_and_flip_into_pdf_space() {
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before_p0 = page_annotation_count(handle, 0);

        let body = std::ffi::CString::new("on page two").unwrap();
        let notes = [BurnNote { page_index: 1, x: 0.0, y: 0.0, text: body.as_ptr() }];
        assert_eq!(
            add_note_annotations(handle, 1000, notes.as_ptr(), notes.len()),
            STATUS_OK_PDFIUM
        );

        assert_eq!(page_annotation_count(handle, 0), before_p0, "page 0 must be untouched");
        assert_eq!(page_annotation_count(handle, 1), 1);

        // Placed at the TOP of the capture, so it must sit near the top of the
        // page in PDF coords, i.e. a HIGH y.
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(1).unwrap();
        let page_height = page.height().value;
        let bounds = page.annotations().iter().next().unwrap().bounds().unwrap();
        assert!(
            bounds.top().value > page_height * 0.9,
            "note placed at the top should be near the page top (y={} of {}), not mirrored",
            bounds.top().value, page_height
        );

        drop(g);
        drop(doc);
        drop(_guard);
        close_document(handle);
    }

    #[test]
    fn add_note_annotations_rejects_bad_input() {
        assert_eq!(add_note_annotations(0, 1000, std::ptr::null(), 0), STATUS_INVALID_INPUT);
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_note_annotations(handle, 0, std::ptr::null(), 0), STATUS_INVALID_INPUT);
        // No notes is success, not failure: saving a document with no notes
        // must not report an error.
        assert_eq!(add_note_annotations(handle, 1000, std::ptr::null(), 0), STATUS_OK_PDFIUM);
        close_document(handle);
    }

    #[test]
    fn a_page_with_no_text_reports_zero_chars_rather_than_failing() {
        // A scanned page is an image with no text operators. The viewer must
        // be able to tell "this page has no selectable text" apart from "the
        // extraction failed", because the first needs a message to the user
        // and the second is a bug. Returning a hard error for both made a
        // scanned document look identical to a broken one.
        let handle = open_fixture_named("tests/fixtures/sample_scanned.pdf");
        let array = get_page_chars(handle, 0, 800);

        assert_eq!(
            array.status, STATUS_OK_PDFIUM,
            "an image-only page is a valid page, not an extraction failure"
        );
        assert_eq!(array.len, 0, "an image-only page has no characters");

        free_char_info_array(array);
        close_document(handle);
    }

    #[test]
    fn render_region_restores_the_page_box_afterwards() {
        // The most dangerous part of cropping to render: page width and height
        // are DERIVED from the crop box, so a leaked crop would silently
        // corrupt every later layout, text extraction and save.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let before = get_page_sizes(handle);
        let (w0, h0) = {
            let s = unsafe { std::slice::from_raw_parts(before.sizes, before.len) };
            (s[0].width, s[0].height)
        };
        free_page_size_array(before);

        let r = render_region(handle, 0, 0.25, 0.25, 0.5, 0.5, 400);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        free_render_result(r);

        let after = get_page_sizes(handle);
        let (w1, h1) = {
            let s = unsafe { std::slice::from_raw_parts(after.sizes, after.len) };
            (s[0].width, s[0].height)
        };
        free_page_size_array(after);

        assert!(
            (w0 - w1).abs() < 0.01 && (h0 - h1).abs() < 0.01,
            "page box was left cropped: {w0}x{h0} became {w1}x{h1}"
        );

        close_document(handle);
    }

    #[test]
    fn render_region_returns_different_pixels_for_different_regions() {
        // Proves the region is actually honoured rather than the whole page
        // being rendered and scaled. The fixture has its text near the top, so
        // the top strip and the bottom strip must not be identical.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let top = render_region(handle, 0, 0.0, 0.0, 1.0, 0.25, 300);
        let bottom = render_region(handle, 0, 0.0, 0.75, 1.0, 0.25, 300);
        assert_eq!(top.status, STATUS_OK_PDFIUM);
        assert_eq!(bottom.status, STATUS_OK_PDFIUM);

        let a = unsafe { std::slice::from_raw_parts(top.buffer, top.len) }.to_vec();
        let b = unsafe { std::slice::from_raw_parts(bottom.buffer, bottom.len) }.to_vec();
        assert_ne!(a, b, "two different regions rendered identical pixels");

        free_render_result(top);
        free_render_result(bottom);
        close_document(handle);
    }

    #[test]
    fn render_region_cost_tracks_the_region_not_the_zoom() {
        // The whole point: a deep zoom asks for a SMALL slice at high
        // resolution. The bitmap must be the size requested, so memory is
        // bounded by the viewport and 800% costs the same as 6400%.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let r = render_region(handle, 0, 0.4, 0.4, 0.05, 0.05, 900);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        assert_eq!(r.width, 900, "bitmap must be exactly the width asked for");
        assert!(
            r.len < 4 * 1024 * 1024,
            "a viewport-sized slice should stay small, was {} bytes",
            r.len
        );
        free_render_result(r);

        close_document(handle);
    }

    #[test]
    fn tile_level_grows_with_the_resolution_needed() {
        // Written against TILE_SIZE, not literals, so that changing the tile
        // size cannot leave this test and TileGrid describing different
        // pyramids while both still pass.
        assert_eq!(tile_level_for_width(TILE_SIZE), 0);
        assert_eq!(tile_level_for_width(TILE_SIZE * 2), 1);
        assert_eq!(tile_level_for_width(TILE_SIZE * 4), 2);
        assert_eq!(
            tile_level_for_width(TILE_SIZE * 4 + 1),
            3,
            "must round UP, never render softer than asked"
        );
        assert_eq!(tile_level_for_width(TILE_SIZE * 32), 5);
    }

    #[test]
    fn a_tile_is_always_the_same_size_whatever_the_zoom() {
        // The property that makes deep zoom cost nothing extra: a tile is a
        // fixed 256x256 at every level, so memory per visible tile never
        // grows and 6400% costs exactly what 100% costs.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        for level in [0, 3, 6] {
            let t = render_tile(handle, 0, level, 0, 0);
            assert_eq!(t.status, STATUS_OK_PDFIUM, "level {level} failed");
            assert_eq!(t.width, TILE_SIZE);
            assert_eq!(t.height, TILE_SIZE);
            assert_eq!(t.len, (TILE_SIZE * TILE_SIZE * 4) as usize);
            free_render_result(t);
        }

        close_document(handle);
    }

    #[test]
    fn tiles_are_cached_so_panning_back_costs_nothing() {
        // Panning re-visits tiles constantly. The second fetch must come from
        // the cache, which is what turns a pan into a cheap blit instead of a
        // re-render.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let first = render_tile(handle, 0, 4, 2, 3);
        assert_eq!(first.status, STATUS_OK_PDFIUM);
        let a = unsafe { std::slice::from_raw_parts(first.buffer, first.len) }.to_vec();
        free_render_result(first);

        let key = TileKey { doc: handle, page: 0, tier: Tier::High, width: TILE_SIZE, level: 4, col: 2, row: 3 };
        assert!(lock(&core().cache).get(&key).is_some(), "tile was not cached");

        let second = render_tile(handle, 0, 4, 2, 3);
        let b = unsafe { std::slice::from_raw_parts(second.buffer, second.len) }.to_vec();
        free_render_result(second);

        assert_eq!(a, b, "the cached tile must be identical to the rendered one");
        close_document(handle);
    }

    #[test]
    fn neighbouring_tiles_show_different_parts_of_the_page() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let a = render_tile(handle, 0, 2, 0, 0);
        let b = render_tile(handle, 0, 2, 1, 0);
        assert_eq!(a.status, STATUS_OK_PDFIUM);
        assert_eq!(b.status, STATUS_OK_PDFIUM);

        let av = unsafe { std::slice::from_raw_parts(a.buffer, a.len) }.to_vec();
        let bv = unsafe { std::slice::from_raw_parts(b.buffer, b.len) }.to_vec();
        assert_ne!(av, bv, "adjacent tiles rendered identical pixels");

        free_render_result(a);
        free_render_result(b);
        close_document(handle);
    }

    #[test]
    fn render_tile_rejects_tiles_outside_the_grid() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        // Level 2 is 4 tiles across, so column 4 does not exist.
        assert_eq!(render_tile(handle, 0, 2, 4, 0).status, STATUS_INVALID_INPUT);
        assert_eq!(render_tile(handle, 0, -1, 0, 0).status, STATUS_INVALID_INPUT);
        assert_eq!(render_tile(0, 0, 0, 0, 0).status, STATUS_INVALID_INPUT);
        close_document(handle);
    }

    #[test]
    fn a_viewport_of_tiles_renders_fast_enough_to_feel_instant() {
        // The claim this whole design rests on. A 1080p viewport at deep zoom
        // is roughly 40 tiles; if that is slow, panning stutters no matter how
        // clever the caching is.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let start = std::time::Instant::now();
        let mut rendered = 0;
        for col in 0..8 {
            for row in 0..5 {
                let t = render_tile(handle, 0, 5, col, row);
                if t.status == STATUS_OK_PDFIUM {
                    rendered += 1;
                }
                free_render_result(t);
            }
        }
        let cold = start.elapsed();

        // Second pass is all cache hits: this is what panning actually costs.
        let start = std::time::Instant::now();
        for col in 0..8 {
            for row in 0..5 {
                free_render_result(render_tile(handle, 0, 5, col, row));
            }
        }
        let warm = start.elapsed();

        println!("TILES: {rendered} tiles cold={cold:?} warm={warm:?}");
        assert!(rendered > 0);
        assert!(
            warm < cold,
            "cached pass ({warm:?}) should beat the cold pass ({cold:?})"
        );

        close_document(handle);
    }

    /// Page height divided by width, from a cheap render.
    fn page_aspect(handle: u64) -> f64 {
        let probe = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 100);
        assert_eq!(probe.status, STATUS_OK_PDFIUM);
        let aspect = probe.height as f64 / probe.width as f64;
        free_render_result(probe);
        aspect
    }

    /// Normalized (x of width, y of height) of the darkest pixel on the page.
    ///
    /// Sampling a fixed spot is a trap that already cost a wrong diagnosis
    /// here: tile (0,0) is the top-left CORNER and the middle of a synthetic
    /// fixture is blank paper, so both read as zero detail at every level and
    /// prove nothing about the renderer. Find where the ink actually is, then
    /// zoom in on THAT.
    fn darkest_point(handle: u64) -> (f32, f32) {
        let full = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 512);
        assert_eq!(full.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(full.buffer, full.len as usize) };
        let (w, h) = (full.width as usize, full.height as usize);

        let mut best = (255i32, 0usize, 0usize);
        for y in 0..h {
            for x in 0..w {
                let g = bytes[(y * w + x) * 4 + 1] as i32;
                if g < best.0 {
                    best = (g, x, y);
                }
            }
        }

        let point = (best.1 as f32 / w as f32, best.2 as f32 / h as f32);
        free_render_result(full);
        assert!(best.0 < 200, "fixture page is blank, nothing to zoom into");
        point
    }

    #[test]
    fn a_tile_matches_the_same_patch_of_a_full_resolution_render() {
        // The honest test of the tile path, and the one that does not depend on
        // guessing where the ink is: at level L the page is TILE_SIZE * 2^L
        // pixels wide, so tile (col,row) must be pixel-for-pixel the block at
        // (col*256, row*256) of a single render at that width. If the crop-box
        // arithmetic is off by anything, or the region comes back soft, this
        // diverges immediately.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let level = 4i32;
        let across = 1i32 << level;
        let aspect = page_aspect(handle) as f32;

        let full = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, TILE_SIZE * across);
        assert_eq!(full.status, STATUS_OK_PDFIUM);
        let fw = full.width as usize;
        let full_bytes = unsafe { std::slice::from_raw_parts(full.buffer, full.len as usize) };

        // Compare the tile over known ink, so the patch is not blank paper.
        let point = darkest_point(handle);
        let col = (point.0 * across as f32) as i32;
        let row = (point.1 * across as f32 * aspect) as i32;

        let tile = render_tile(handle, 0, level, col, row);
        assert_eq!(tile.status, STATUS_OK_PDFIUM);
        let tile_bytes = unsafe { std::slice::from_raw_parts(tile.buffer, tile.len as usize) };

        let (x0, y0) = ((col * TILE_SIZE) as usize, (row * TILE_SIZE) as usize);
        let side = TILE_SIZE as usize;

        let mut diff = 0.0f64;
        let mut tile_ink = 0u32;
        for y in 0..side {
            for x in 0..side {
                let t = tile_bytes[(y * side + x) * 4 + 1] as f64;
                let f = full_bytes[((y0 + y) * fw + (x0 + x)) * 4 + 1] as f64;
                diff += (t - f).abs();
                if t < 200.0 {
                    tile_ink += 1;
                }
            }
        }
        let mean_diff = diff / (side * side) as f64;

        println!(
            "TILE vs FULL: level={level} tile=({col},{row}) mean_diff={mean_diff:.2} ink_px={tile_ink}"
        );

        free_render_result(tile);
        free_render_result(full);
        close_document(handle);

        assert!(tile_ink > 0, "the compared patch is blank, the test proves nothing");
        assert!(
            mean_diff < 8.0,
            "tile does not match the same patch of a full-resolution render \
             (mean channel difference {mean_diff:.2}), so tiling is not reproducing the page"
        );
    }

    #[test]
    fn the_crop_box_still_resolves_at_deep_tile_sizes() {
        // The primitive every deep tile rests on. At level 10 a tile is about a
        // thousandth of the page, so if PDFium quietly floored a crop box at
        // some minimum, deep tiles would silently render the wrong area while
        // every status code stayed OK.
        use pdfium_render::prelude::*;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let mut page = g.pages().get(0).unwrap();

        let media = page.boundaries().media().map(|b| b.bounds).unwrap();
        let page_w = page.width().value;

        let mut observed = Vec::new();
        for divisor in [4.0f32, 64.0, 1024.0] {
            let wanted = page_w / divisor;
            let rect = PdfRect::new(
                PdfPoints::new(media.bottom().value),
                PdfPoints::new(media.left().value),
                PdfPoints::new(media.bottom().value + wanted),
                PdfPoints::new(media.left().value + wanted),
            );
            page.boundaries_mut().set_crop(rect).unwrap();
            observed.push((wanted, page.width().value));
        }
        page.boundaries_mut().set_crop(media).unwrap();

        println!("CROP RESOLUTION: {observed:?}");

        drop(page);
        drop(g);
        drop(_guard);
        close_document(handle);

        for (wanted, got) in observed {
            assert!(
                (got - wanted).abs() < 0.01,
                "asked for a {wanted}pt crop and the page reported {got}pt, \
                 so the box was clamped and deep tiles cannot address the page"
            );
        }
    }

    #[test]
    fn render_region_rejects_bad_input() {
        assert_eq!(render_region(0, 0, 0.0, 0.0, 1.0, 1.0, 100).status, STATUS_INVALID_INPUT);
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(render_region(handle, 0, 0.0, 0.0, 0.0, 1.0, 100).status, STATUS_INVALID_INPUT);
        assert_eq!(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 0).status, STATUS_INVALID_INPUT);
        assert_eq!(render_region(handle, 9999, 0.0, 0.0, 1.0, 1.0, 100).status, STATUS_INVALID_INPUT);
        close_document(handle);
    }

    #[test]
    fn burn_annotations_rejects_bad_input() {
        assert_eq!(
            burn_annotations(0, 1000, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_INVALID_INPUT
        );
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            burn_annotations(handle, 0, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0),
            STATUS_INVALID_INPUT,
            "a zero capture width would divide by zero"
        );
        close_document(handle);
    }

    #[test]
    fn get_form_field_count_finds_the_field_in_the_form_fixture() {
        let handle = open_fixture_named("tests/fixtures/sample_form.pdf");
        assert_eq!(get_form_field_count(handle), 1);
        close_document(handle);
    }

    #[test]
    fn fill_text_field_updates_an_existing_field() {
        use pdfium_render::prelude::PdfFormFieldCommon;

        let handle = open_fixture_named("tests/fixtures/sample_form.pdf");

        let name = std::ffi::CString::new("Name").unwrap();
        let value = std::ffi::CString::new("Aung").unwrap();
        assert_eq!(fill_text_field(handle, name.as_ptr(), value.as_ptr()), STATUS_OK_PDFIUM);

        // White-box verification: read the field value straight back off
        // the in-memory document rather than trusting the status code alone.
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(0).unwrap();

        let mut found_value = None;
        for i in 0..page.annotations().len() {
            if let Ok(annotation) = page.annotations().get(i) {
                if let Some(field) = annotation.as_form_field() {
                    if field.name().as_deref() == Some("Name") {
                        found_value = field.as_text_field().and_then(|f| f.value());
                    }
                }
            }
        }
        assert_eq!(found_value.as_deref(), Some("Aung"));

        drop(doc_guard);
        drop(doc);
        close_document(handle);
    }
}
