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
use pdfium_render::prelude::{Pdfium, PdfDocument, PdfPageRenderRotation, PdfRenderConfig};

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

    let mut cache = core().cache.lock().unwrap();
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
        let _guard = CALL_LOCK.lock().unwrap();
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
    core.documents.lock().unwrap().insert(id, Arc::new(Mutex::new(document)));
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
        let _guard = CALL_LOCK.lock().unwrap();
        let removed = core.documents.lock().unwrap().remove(&doc_handle);
        drop(removed);
    }

    {
        let mut cache = core.cache.lock().unwrap();
        let stale_keys: Vec<TileKey> =
            cache.iter().filter(|(k, _)| k.doc == doc_handle).map(|(k, _)| *k).collect();
        for key in stale_keys {
            cache.pop(&key);
        }
    }

    core.generations.lock().unwrap().retain(|(doc, _), _| *doc != doc_handle);
}

/// Returns the page count for a handle, or -1 if the handle is unknown.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_count(doc_handle: u64) -> i32 {
    let _guard = CALL_LOCK.lock().unwrap();
    match core().documents.lock().unwrap().get(&doc_handle) {
        Some(doc) => doc.lock().unwrap().pages().len() as i32,
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

    let _guard = CALL_LOCK.lock().unwrap();
    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
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

    let doc_guard = doc.lock().unwrap();
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
    let _guard = CALL_LOCK.lock().unwrap();
    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = doc.lock().unwrap();
    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };
    if page.delete().is_err() {
        return STATUS_INVALID_INPUT;
    }
    drop(doc_guard);
    drop(doc);

    evict_all_cache_for_doc(doc_handle);
    core().generations.lock().unwrap().retain(|(d, _), _| *d != doc_handle);
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

    let _guard = CALL_LOCK.lock().unwrap();
    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = doc.lock().unwrap();
    match doc_guard.save_to_file(path_str) {
        Ok(()) => STATUS_OK_PDFIUM,
        Err(_) => STATUS_INVALID_INPUT,
    }
}

fn evict_page_cache(doc_handle: u64, page_index: i32) {
    let mut cache = core().cache.lock().unwrap();
    let stale: Vec<TileKey> =
        cache.iter().filter(|(k, _)| k.doc == doc_handle && k.page == page_index).map(|(k, _)| *k).collect();
    for key in stale {
        cache.pop(&key);
    }
}

fn evict_all_cache_for_doc(doc_handle: u64) {
    let mut cache = core().cache.lock().unwrap();
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
    let _guard = CALL_LOCK.lock().unwrap();
    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return -1;
    };

    let doc_guard = doc.lock().unwrap();
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

    let _guard = CALL_LOCK.lock().unwrap();
    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = doc.lock().unwrap();
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
    let _guard = CALL_LOCK.lock().unwrap();

    let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return CharInfoArray::failure(STATUS_INVALID_INPUT);
    };

    let doc_guard = doc.lock().unwrap();
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
    let key = TileKey { doc: doc_handle, page: page_index, tier: Tier::Low, width: target_width };

    if let Some(tile) = core().cache.lock().unwrap().get(&key) {
        return tile_to_result(tile, STATUS_OK_PDFIUM);
    }

    let rendered = {
        // Hold CALL_LOCK across both the render call and the drop of our
        // Arc clone below — dropping the last reference to a PdfDocument
        // runs PDFium's native close, which needs the same serialization as
        // any other native call.
        let _guard = CALL_LOCK.lock().unwrap();
        let doc = core().documents.lock().unwrap().get(&doc_handle).cloned();
        let result = doc.as_ref().and_then(|d| {
            let guard = d.lock().unwrap();
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
    core.generations.lock().unwrap().insert((doc_handle, page_index), generation);

    let request_id = core.next_request_id.fetch_add(1, Ordering::Relaxed);
    let key = TileKey { doc: doc_handle, page: page_index, tier: Tier::High, width: target_width };

    // Fast path: already cached at this exact width, no thread needed.
    if let Some(tile) = core.cache.lock().unwrap().get(&key) {
        core.requests.lock().unwrap().insert(request_id, RequestSlot::Ready(tile.clone()));
        return request_id;
    }

    core.requests.lock().unwrap().insert(
        request_id,
        RequestSlot::Pending { doc: doc_handle, page: page_index, generation },
    );

    let doc_arc = core.documents.lock().unwrap().get(&doc_handle).cloned();

    thread::spawn(move || {
        let rendered = {
            // Same reasoning as render_low_res_inner: hold CALL_LOCK across
            // both the render call and the drop of doc_arc at the end of
            // this scope, since dropping the last Arc<PdfDocument> reference
            // runs PDFium's native close.
            let _guard = CALL_LOCK.lock().unwrap();
            let result = doc_arc.as_ref().and_then(|doc| {
                let guard = doc.lock().unwrap();
                render_page_via_pdfium(&guard, page_index, target_width)
            });
            drop(doc_arc);
            result
        };

        let still_current =
            core.generations.lock().unwrap().get(&(doc_handle, page_index)).copied() == Some(generation);

        let slot = match rendered {
            Some((width, height, bytes)) => {
                let tile = CachedTile { width, height, bytes: Arc::from(bytes) };
                cache_put(key, tile.clone());
                if still_current { RequestSlot::Ready(tile) } else { RequestSlot::Cancelled }
            }
            None => RequestSlot::Cancelled,
        };

        let mut requests = core.requests.lock().unwrap();
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
        let requests = core.requests.lock().unwrap();
        match requests.get(&request_id) {
            None => Snapshot::Unknown,
            Some(RequestSlot::Cancelled) | Some(RequestSlot::Discarded) => Snapshot::Cancelled,
            Some(RequestSlot::Ready(tile)) => Snapshot::Ready(tile.clone()),
            Some(RequestSlot::Pending { doc, page, generation }) => {
                let still_current =
                    core.generations.lock().unwrap().get(&(*doc, *page)).copied() == Some(*generation);
                if still_current { Snapshot::StillPending } else { Snapshot::NewlyStale }
            }
        }
    };

    match snapshot {
        Snapshot::Unknown => POLL_UNKNOWN_REQUEST,
        Snapshot::StillPending => POLL_PENDING,
        Snapshot::Cancelled => {
            core.requests.lock().unwrap().remove(&request_id);
            POLL_CANCELLED
        }
        Snapshot::NewlyStale => {
            core.requests.lock().unwrap().insert(request_id, RequestSlot::Cancelled);
            POLL_CANCELLED
        }
        Snapshot::Ready(tile) => {
            core.requests.lock().unwrap().remove(&request_id);
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
    let mut requests = core().requests.lock().unwrap();
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

    let render_config = PdfRenderConfig::new()
        .set_target_width(target_width)
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
        let doc = core().documents.lock().unwrap().get(&handle).cloned().unwrap();
        let rotation = doc.lock().unwrap().pages().get(0).unwrap().rotation().unwrap();
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
        let _guard = CALL_LOCK.lock().unwrap();
        let doc = core().documents.lock().unwrap().get(&handle).cloned().unwrap();
        let doc_guard = doc.lock().unwrap();
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

        let resident: usize = core().cache.lock().unwrap().iter().map(|(_, t)| t.bytes.len()).sum();
        assert!(
            resident <= CACHE_BUDGET_BYTES,
            "cache held {resident} bytes, over the {CACHE_BUDGET_BYTES} byte budget"
        );

        close_document(handle);
    }

    #[test]
    fn oversized_tiles_are_not_cached_at_all() {
        let before = core().cache.lock().unwrap().len();

        let huge = CachedTile {
            width: 1,
            height: 1,
            bytes: Arc::from(vec![0u8; MAX_CACHEABLE_TILE_BYTES + 1]),
        };
        cache_put(TileKey { doc: u64::MAX, page: 0, tier: Tier::High, width: 1 }, huge);

        assert_eq!(core().cache.lock().unwrap().len(), before, "an oversized tile must not enter the cache");
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
        let doc = core().documents.lock().unwrap().get(&handle).cloned().unwrap();
        let doc_guard = doc.lock().unwrap();
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
