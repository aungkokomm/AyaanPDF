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

/// Writing the document outline. Its own module because it is the one feature
/// that does not go through PDFium at all: PDFium can read bookmarks but has no
/// API to create them, so the /Outlines tree is built as PDF objects directly.
pub mod outline;

use std::collections::HashMap;
use std::ffi::CStr;
use std::num::NonZeroUsize;
use std::os::raw::c_char;
use std::panic;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;

use lru::LruCache;
// The error types come from the prelude, not from pdfium_render::error, which
// is a private module. The vendored crate carries exactly one patch and this is
// not worth being the second.
use pdfium_render::prelude::{Pdfium, PdfDocument, PdfiumError, PdfiumInternalError};

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

/// The file is a real PDF but is encrypted, and the password given (if any)
/// did not open it.
///
/// Distinct from every other failure because it is the one the user can do
/// something about. Reported the same way for "no password supplied" and
/// "wrong password supplied": PDFium does not distinguish them, and neither
/// does any reader's prompt.
pub const STATUS_NEEDS_PASSWORD: i32 = 5;

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
    open_protected_inner(path, std::ptr::null()).handle
}

/// The outcome of trying to open a document: the handle, and why not.
#[repr(C)]
pub struct OpenResult {
    /// Non-zero on success. Zero on every failure.
    pub handle: u64,
    /// STATUS_OK_PDFIUM, STATUS_NEEDS_PASSWORD, or STATUS_INVALID_INPUT.
    pub status: i32,
}

/// Opens a document, optionally with a password.
///
/// Separate from `open_document` because a caller has to be able to tell an
/// encrypted file from a broken one: the first is a prompt, the second is an
/// error message, and returning zero for both meant every protected PDF in the
/// world looked to this app like a corrupt file.
///
/// A null or empty password means "try without one", which is also what opens
/// a document that is encrypted but carries an empty user password, the common
/// case where a PDF restricts printing or editing but not reading.
///
/// # Safety
/// `path` and `password` must be null or valid NUL-terminated C strings.
#[unsafe(no_mangle)]
pub extern "C" fn open_document_protected(
    path: *const c_char,
    password: *const c_char,
) -> OpenResult {
    panic::catch_unwind(|| open_protected_inner(path, password)).unwrap_or(OpenResult {
        handle: 0,
        status: STATUS_PANIC,
    })
}

fn open_protected_inner(path: *const c_char, password: *const c_char) -> OpenResult {
    let failed = |status| OpenResult { handle: 0, status };

    if path.is_null() {
        return failed(STATUS_INVALID_INPUT);
    }
    let Ok(path_str) = (unsafe { CStr::from_ptr(path) }).to_str() else {
        return failed(STATUS_INVALID_INPUT);
    };

    // An empty password is the same as none. Passing "" through to PDFium is
    // not: it treats it as an attempt and fails a document that would have
    // opened unprotected.
    let password_str = if password.is_null() {
        None
    } else {
        match (unsafe { CStr::from_ptr(password) }).to_str() {
            Ok("") => None,
            Ok(text) => Some(text),
            Err(_) => return failed(STATUS_INVALID_INPUT),
        }
    };

    let document = {
        let _guard = lock(&CALL_LOCK);
        let Some(pdfium) = pdfium() else {
            return failed(STATUS_INVALID_INPUT);
        };

        match pdfium.load_pdf_from_file(path_str, password_str) {
            Ok(document) => document,
            Err(PdfiumError::PdfiumLibraryInternalError(PdfiumInternalError::PasswordError)) => {
                return failed(STATUS_NEEDS_PASSWORD);
            }
            Err(_) => return failed(STATUS_INVALID_INPUT),
        }
    };

    let core = core();
    let id = core.next_doc_id.fetch_add(1, Ordering::Relaxed);
    lock(&core.documents).insert(id, Arc::new(Mutex::new(document)));

    OpenResult { handle: id, status: STATUS_OK_PDFIUM }
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

    let doc_guard = lock(&doc);
    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };

    // ADDITIVE: rotate BY `degrees` from the page's current rotation, so
    // "rotate 90" applied twice ends at 180, the way every rotate control
    // behaves. Setting an absolute value made a second rotate a no-op.
    let current = page.rotation().map(|r| r.as_degrees() as i32).unwrap_or(0);
    let normalized = (((current + degrees) % 360) + 360) % 360;
    let rotation = match normalized {
        0..=44 | 316..=359 => PdfPageRenderRotation::None,
        45..=134 => PdfPageRenderRotation::Degrees90,
        135..=224 => PdfPageRenderRotation::Degrees180,
        _ => PdfPageRenderRotation::Degrees270,
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

/// Rebuilds the document as a new sequence of its own pages.
///
/// One primitive covers every page-organising action, because each is just a
/// different list of source indices:
///   reorder   = a permutation, e.g. [2, 0, 1]
///   duplicate = a repeated index, e.g. [0, 1, 1, 2]
///   delete    = an omitted index, e.g. [0, 2]
///   extract   = a subset, e.g. [3, 4]
///
/// Done by importing the chosen pages, in order, into a fresh document and
/// swapping it in behind the same handle. Import copies each page whole, its
/// content AND its annotations, which the reorder test proves. The handle is
/// preserved so nothing on the app side has to be re-opened.
///
/// `indices` are zero-based source page numbers; `count` is how many. Every
/// index must be in range, and there must be at least one, so a document can
/// never be rebuilt to zero pages.
#[unsafe(no_mangle)]
pub extern "C" fn rebuild_page_order(doc_handle: u64, indices: *const i32, count: usize) -> i32 {
    if doc_handle == 0 || indices.is_null() || count == 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| rebuild_page_order_inner(doc_handle, indices, count)).unwrap_or(STATUS_PANIC)
}

fn rebuild_page_order_inner(doc_handle: u64, indices: *const i32, count: usize) -> i32 {
    let order: &[i32] = unsafe { std::slice::from_raw_parts(indices, count) };

    let _guard = lock(&CALL_LOCK);

    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let new_doc = {
        let doc_guard = lock(&doc);
        let page_count = doc_guard.pages().len() as i32;

        // Every source index must exist. A bad index would otherwise import
        // nothing for that slot and silently drop a page.
        if order.iter().any(|&i| i < 0 || i >= page_count) {
            return STATUS_INVALID_INPUT;
        }

        let Some(pdfium) = pdfium() else {
            return STATUS_INVALID_INPUT;
        };
        let Ok(mut new_doc) = pdfium.create_new_pdf() else {
            return STATUS_INVALID_INPUT;
        };

        // pdfium's import takes a 1-indexed, comma-separated page list.
        let range = order.iter().map(|&i| (i + 1).to_string()).collect::<Vec<_>>().join(",");

        if new_doc
            .pages_mut()
            .copy_pages_from_document(&doc_guard, &range, 0)
            .is_err()
        {
            return STATUS_INVALID_INPUT;
        }

        new_doc
    };

    // Swap the rebuilt document in behind the same handle. Done under CALL_LOCK,
    // so no render or edit can be holding the old one across this.
    lock(&core().documents).insert(doc_handle, Arc::new(Mutex::new(new_doc)));

    evict_all_cache_for_doc(doc_handle);
    lock(&core().generations).retain(|(d, _), _| *d != doc_handle);
    STATUS_OK_PDFIUM
}

/// Inserts all pages of another PDF (given as bytes) into this document at
/// `at_index`. Returns the NUMBER of pages inserted, so the app can shift its
/// page-indexed overlay marks, or a NEGATIVE value on error (-1 invalid, -2
/// panic). Imported pages keep their annotations, like every other page copy.
#[unsafe(no_mangle)]
pub extern "C" fn insert_pages_from_bytes(
    doc_handle: u64,
    data: *const u8,
    len: usize,
    at_index: i32,
) -> i32 {
    if doc_handle == 0 || data.is_null() || len == 0 || at_index < 0 {
        return -1;
    }
    panic::catch_unwind(|| insert_pages_from_bytes_inner(doc_handle, data, len, at_index)).unwrap_or(-2)
}

fn insert_pages_from_bytes_inner(doc_handle: u64, data: *const u8, len: usize, at_index: i32) -> i32 {
    let bytes = unsafe { std::slice::from_raw_parts(data, len) }.to_vec();

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return -1;
    };
    let Some(pdfium) = pdfium() else {
        return -1;
    };

    // The source document owns its bytes, so it can be dropped safely at the
    // end of this call once its pages have been copied out.
    let Ok(source) = pdfium.load_pdf_from_byte_vec(bytes, None) else {
        return -1;
    };
    let source_count = source.pages().len() as i32;
    if source_count == 0 {
        return 0;
    }

    let mut doc_guard = lock(&doc);
    let dest_count = doc_guard.pages().len() as i32;
    let at = at_index.min(dest_count).max(0) as u16;

    let range = format!("1-{source_count}");
    if doc_guard.pages_mut().copy_pages_from_document(&source, &range, at).is_err() {
        return -1;
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    lock(&core().generations).retain(|(d, _), _| *d != doc_handle);
    source_count
}

/// Inserts one blank page at `at_index`, sized in points. Used for "insert a
/// blank page"; the app passes the current page's size so it matches.
#[unsafe(no_mangle)]
pub extern "C" fn insert_blank_page(
    doc_handle: u64,
    at_index: i32,
    width_pts: f32,
    height_pts: f32,
) -> i32 {
    if doc_handle == 0 || at_index < 0 || width_pts <= 0.0 || height_pts <= 0.0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        use pdfium_render::prelude::*;

        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return STATUS_INVALID_INPUT;
        };

        let mut doc_guard = lock(&doc);
        let dest_count = doc_guard.pages().len() as i32;
        let at = at_index.min(dest_count).max(0) as u16;

        let size = PdfPagePaperSize::Custom(PdfPoints::new(width_pts), PdfPoints::new(height_pts));
        if doc_guard.pages_mut().create_page_at_index(size, at).is_err() {
            return STATUS_INVALID_INPUT;
        }

        drop(doc_guard);
        evict_all_cache_for_doc(doc_handle);
        lock(&core().generations).retain(|(d, _), _| *d != doc_handle);
        STATUS_OK_PDFIUM
    })
    .unwrap_or(STATUS_PANIC)
}

/// Saves the given pages, in the given order, to a NEW PDF file at `path`,
/// leaving this document unchanged. The core half of "extract pages": the app
/// decides the range and whether to also delete them afterwards.
#[unsafe(no_mangle)]
pub extern "C" fn extract_pages_to_file(
    doc_handle: u64,
    indices: *const i32,
    count: usize,
    path: *const c_char,
) -> i32 {
    if doc_handle == 0 || indices.is_null() || count == 0 || path.is_null() {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| extract_pages_to_file_inner(doc_handle, indices, count, path)).unwrap_or(STATUS_PANIC)
}

fn extract_pages_to_file_inner(doc_handle: u64, indices: *const i32, count: usize, path: *const c_char) -> i32 {
    let order: &[i32] = unsafe { std::slice::from_raw_parts(indices, count) };
    let Ok(path_str) = (unsafe { CStr::from_ptr(path) }).to_str() else {
        return STATUS_INVALID_INPUT;
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let doc_guard = lock(&doc);

    let page_count = doc_guard.pages().len() as i32;
    if order.iter().any(|&i| i < 0 || i >= page_count) {
        return STATUS_INVALID_INPUT;
    }

    let Some(pdfium) = pdfium() else {
        return STATUS_INVALID_INPUT;
    };
    let Ok(mut new_doc) = pdfium.create_new_pdf() else {
        return STATUS_INVALID_INPUT;
    };

    let range = order.iter().map(|&i| (i + 1).to_string()).collect::<Vec<_>>().join(",");
    if new_doc.pages_mut().copy_pages_from_document(&doc_guard, &range, 0).is_err() {
        return STATUS_INVALID_INPUT;
    }

    let Ok(bytes) = new_doc.save_to_bytes() else {
        return STATUS_INVALID_INPUT;
    };
    if std::fs::write(path_str, bytes).is_err() {
        return STATUS_INVALID_INPUT;
    }

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

/// Rewrites the searchable text layer for the given pages.
///
/// Our text boxes draw SHAPED GLYPHS inside a stamp annotation. That is what
/// makes Burmese and Devanagari come out right, and it is also why the words
/// cannot be found: a page's text layer is its content stream, and an
/// annotation's appearance stream is not part of it. Measured, not assumed: a
/// page carrying one of our Burmese boxes extracts as "Page 1 of 20" and
/// nothing else, while the font's /ToUnicode sits there correct and unused.
///
/// So the words are written a SECOND time, into the page content, in text
/// render mode 3: no fill, no stroke, nothing drawn. The visible glyphs are
/// untouched, and the page renders byte for byte identically. The invisible
/// copy carries the ORIGINAL characters rather than the shaped glyphs, so it
/// extracts in logical order and needs no CMap of its own.
///
/// This is how a scanned document's OCR layer works, for the same reason.
/// Mints a 32-hex id for an Ayaan object that has none.
///
/// Not random for its own sake: it only has to be unique within one document,
/// and it MUST be written back in the same breath. An id that is invented and
/// then forgotten is worse than none at all, because the next save invents a
/// different one and the association breaks silently. That exact bug cost a
/// release once already.
fn mint_object_id() -> String {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    let n = COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{nanos:016x}{n:016x}")
}

/// Gives every Ayaan text box on the page a stable id, writing it back.
///
/// A freshly created box has no id: the tag is written at creation and the app
/// assigns the id afterwards. The searchable layer is keyed on that id, so
/// syncing a page whose boxes are brand new has to establish it first, or the
/// boxes are silently skipped and never become findable.
fn ensure_text_box_ids(page: &mut pdfium_render::prelude::PdfPage<'_>) {
    let annotations = page.annotations_mut();
    let count = annotations.len();
    for i in 0..count {
        let Some(mut annotation) = annotations.iter().nth(i as usize) else {
            continue;
        };
        let Some(tag) = annotation_tag(&annotation) else {
            continue;
        };
        let (existing_id, body) = strip_id_prefix(&tag);
        if existing_id.is_some() || parse_textbox_tag_full(&tag).is_none() {
            continue;
        }
        let new_tag = format!("{ID_PREFIX}{}{ID_SEPARATOR}{body}", mint_object_id());
        let _ = set_annotation_tag(&mut annotation, &new_tag);
    }
}

/// The mark that ties an invisible run to the Ayaan text object it belongs to.
///
/// The full mark name is `AyaanSearch:<32 hex>`, where the hex is the SAME
/// stable id the annotation carries in its `AyaanTag` (`ID:<32hex>|<body>`).
/// So the association is by the object's own identity, which already survives
/// every edit, move, resize and rotation: PDFium cannot edit an annotation in
/// place, so the app deletes and re-adds it under the same id.
///
/// The id rides in the mark's NAME rather than in a mark parameter because
/// `FPDFPageObjMark_SetStringParam` needs the document handle, which the
/// vendored crate does not expose publicly. A name holds 32 hex digits
/// perfectly well, and reading one needs only the object handle.
///
/// NOT an index, and NOT the text itself. Two text boxes on one page saying the
/// same words are two different objects and must stay independently findable.
const SEARCH_MARK_PREFIX: &str = "AyaanSearch:";

/// The Ayaan object id an invisible run belongs to, or None if it is not ours.
fn search_mark_id(
    bindings: &dyn pdfium_render::prelude::PdfiumLibraryBindings,
    handle: pdfium_render::prelude::FPDF_PAGEOBJECT,
) -> Option<String> {
    for i in 0..bindings.FPDFPageObj_CountMarks(handle).max(0) {
        let mark = bindings.FPDFPageObj_GetMark(handle, i as std::os::raw::c_ulong);
        if mark.is_null() {
            continue;
        }
        // Length is in BYTES and includes the UTF-16 terminator.
        let mut out: std::os::raw::c_ulong = 0;
        bindings.FPDFPageObjMark_GetName(mark, std::ptr::null_mut(), 0, &mut out);
        if out <= 2 {
            continue;
        }
        let mut buf = vec![0u16; out as usize / 2];
        bindings.FPDFPageObjMark_GetName(mark, buf.as_mut_ptr(), out, &mut out);
        while buf.last() == Some(&0) {
            buf.pop();
        }
        if let Ok(name) = String::from_utf16(&buf) {
            if let Some(id) = name.strip_prefix(SEARCH_MARK_PREFIX) {
                return Some(id.to_string());
            }
        }
    }
    None
}

/// One Ayaan text object's contribution to the searchable layer.
struct SearchRun {
    /// The owning annotation's stable id. The whole association mechanism.
    id: String,
    /// The ORIGINAL source string. Never reconstructed from shaped glyphs:
    /// shaping is a one-way visual transform, and inverting it is exactly the
    /// problem this design exists to avoid.
    text: String,
    size_px: f32,
    left: f32,
    top: f32,
    font_path: Option<String>,
}

fn sync_text_layer_inner(doc_handle: u64, pages: &[i32]) -> i32 {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let mut doc_guard = lock(&doc);
    let page_count = doc_guard.pages().len() as i32;

    for &page_index in pages {
        if page_index < 0 || page_index >= page_count {
            continue;
        }

        // Ids first: a brand-new box has none, and the layer is keyed on it.
        if let Ok(mut page) = doc_guard.pages().get(page_index as u16) {
            ensure_text_box_ids(&mut page);
        }

        // Gathered BEFORE anything else is mutated.
        let mut runs: Vec<SearchRun> = Vec::new();
        {
            let Ok(page) = doc_guard.pages().get(page_index as u16) else {
                continue;
            };
            for annotation in page.annotations().iter() {
                let Some(tag) = annotation_tag(&annotation) else {
                    continue;
                };
                // The id and the body come out of the same tag. A text box
                // written before ids existed has none, and is skipped rather
                // than given an invented one that could never be matched again
                // on the next save.
                let (Some(id), _) = strip_id_prefix(&tag) else {
                    continue;
                };
                let Some(parsed) = parse_textbox_tag_full(&tag) else {
                    continue;
                };
                if parsed.text.trim().is_empty() {
                    continue;
                }
                let Ok(bounds) = annotation.bounds() else {
                    continue;
                };
                runs.push(SearchRun {
                    id: id.to_string(),
                    text: parsed.text,
                    size_px: parsed.size_px,
                    left: bounds.left().value,
                    top: bounds.top().value,
                    font_path: parsed.font_path,
                });
            }
        }

        // Fonts are loaded against the DOCUMENT, so they are resolved before the
        // page is borrowed mutably below.
        let mut tokens: Vec<PdfFontToken> = Vec::with_capacity(runs.len());
        for run in &runs {
            tokens.push(resolve_text_font(&mut doc_guard, run.font_path.as_deref()));
        }

        let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
            continue;
        };

        // MANUAL regeneration for the whole rebuild.
        //
        // By default pdfium-render regenerates the content stream on EVERY
        // change, which invalidates the object handles still being walked. That
        // is what made the first removal attempt crash. One regeneration at the
        // end instead.
        page.set_content_regeneration_strategy(PdfPageContentRegenerationStrategy::Manual);

        // Out with ALL of the previous layer, then in with a fresh one.
        //
        // Wholesale rather than diffed: it makes edit, move, resize, rotate and
        // delete the same operation, with no per-case state to get subtly
        // wrong. A run whose annotation is gone is simply not re-added.
        let stale: Vec<usize> = {
            let objects = page.objects();
            let bindings = doc_guard.bindings();
            (0..objects.len())
                .filter(|i| {
                    objects.get(*i).is_ok_and(|obj| match &obj {
                        PdfPageObject::Text(t) => {
                            search_mark_id(bindings, t.object_handle()).is_some()
                        }
                        _ => false,
                    })
                })
                .map(|i| i as usize)
                .collect()
        };

        // DESCENDING, because removing an object shifts every index above it.
        let had_stale = !stale.is_empty();
        for index in stale.into_iter().rev() {
            let Ok(obj) = page.objects().get(index as PdfPageObjectIndex) else {
                continue;
            };
            if let Ok(removed) = page.objects_mut().remove_object(obj) {
                // FORGOTTEN, never dropped. pdfium-render's Drop calls
                // FPDFPageObj_Destroy, and doing that after FPDFPage_RemoveObject
                // crashes the process (STATUS_ILLEGAL_INSTRUCTION), which reads
                // as a double free: this PDFium build already released it.
                std::mem::forget(removed);
            }
        }

        let mut wrote = false;
        for (run, token) in runs.iter().zip(tokens.iter()) {
            let Some(font) = doc_guard.fonts().get(*token) else {
                continue;
            };
            let size = PdfPoints::new(run.size_px);

            // One run per line, stepping down the box, so a search hit
            // highlights near the line it is on rather than over the whole box.
            for (i, line) in run.text.lines().enumerate() {
                if line.trim().is_empty() {
                    continue;
                }
                let Ok(mut obj) = PdfPageTextObject::new(&doc_guard, line, font, size) else {
                    continue;
                };
                if obj.set_render_mode(PdfPageTextRenderMode::Invisible).is_err() {
                    // Without this the words would be DRAWN over the shaped
                    // ones. Better to write nothing than to double-print.
                    continue;
                }
                let baseline = run.top - run.size_px * (1.2 * i as f32 + 1.0);
                if obj
                    .translate(PdfPoints::new(run.left), PdfPoints::new(baseline))
                    .is_err()
                {
                    continue;
                }
                // Marked AFTER it joins the page. Marking a detached object
                // crashes when the mark is next read: attaching moves the object
                // into the page's storage and a mark written beforehand does not
                // survive the move.
                if let Ok(attached) = page.objects_mut().add_object(obj.into()) {
                    if let PdfPageObject::Text(t) = &attached {
                        let name = format!("{SEARCH_MARK_PREFIX}{}", run.id);
                        doc_guard
                            .bindings()
                            .FPDFPageObj_AddMark(t.object_handle(), &name);
                        wrote = true;
                    }
                }
            }
        }

        if (wrote || had_stale) && page.regenerate_content().is_err() {
            return STATUS_UNSUPPORTED;
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Rewrites the searchable text layer on the given pages. Call before saving.
///
/// Takes explicit page indices rather than walking the document: asking a page
/// for its annotations LOADS and parses it, and doing that for all 3352 pages
/// of a book to find the two that have text boxes is exactly the mistake that
/// cost 34 seconds on the form-field path.
///
/// # Safety
/// `pages` must point to at least `page_count` readable i32 values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sync_text_layer(
    doc_handle: u64,
    pages: *const i32,
    page_count: usize,
) -> i32 {
    if doc_handle == 0 || (pages.is_null() && page_count != 0) {
        return STATUS_INVALID_INPUT;
    }
    let list = if page_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(pages, page_count) }.to_vec()
    };
    panic::catch_unwind(move || sync_text_layer_inner(doc_handle, &list))
        .unwrap_or(STATUS_PANIC)
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
        // page_size(), NOT get(). get() LOADS the page: it parses the content
        // streams, builds the object list, and is then thrown away because all
        // we wanted was two floats. page_size() reads them out of the page
        // dictionary via FPDF_GetPageSizeByIndexF without loading anything.
        //
        // Measured on a real 3352-page book: 37.8 SECONDS the old way, on the
        // UI thread, before a single page could be drawn. That was the whole of
        // "the app stops responding when I open a big PDF".
        match pages.page_size(i) {
            Ok(rect) => out.push(PageSize {
                width: rect.width().value,
                height: rect.height().value,
            }),
            // Keep the array index-aligned with page indices even if one page
            // fails; a zero size is a slot the caller can skip.
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

    let (origin_x, origin_top) = page_origin(&page);
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
                                left, top, right, bottom, out_new_index,
                                false)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Rebuilds a stamp at the bounds given purely so that it lands at the END of
/// the page's annotation list, which is the TOP of the paint order.
///
/// Reordering is the one thing PDFium's annotation API cannot do: there is no
/// move or swap, and the /Annots array is not reachable through the public
/// surface. Appending is the only ordering primitive there is, and a rebuild is
/// the only way to append something that already exists.
///
/// `resize_annotation` cannot serve here. It tries an in-place bounds write
/// first, and for a same-size move that SUCCEEDS, leaving the annotation exactly
/// where it was in the list. That is correct for a move and useless for a
/// raise, so this entry point skips straight to the rebuild.
///
/// Stamps only. Ink has no rebuildable description, and an annotation this app
/// did not create (an Acrobat comment, a form widget) would come back as
/// something else or not at all, so those report UNSUPPORTED and the caller is
/// expected to leave the page's order alone rather than lose them.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub extern "C" fn raise_stamp_annotation(
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
                                left, top, right, bottom, out_new_index,
                                true)
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
    force_rebuild: bool,
) -> i32 {
    use pdfium_render::prelude::*;

    // The straightforward path first. It succeeds for a move of anything, and
    // for a resize of the quad-point kinds, and only reports UNSUPPORTED for
    // the cases that genuinely need rebuilding.
    //
    // Skipped entirely when the CALLER wants the rebuild for its own sake: a
    // raise needs the annotation to be removed and re-appended, and an in-place
    // write would report success without moving it in the list at all.
    if !force_rebuild {
        let direct = set_annotation_bounds(doc_handle, page_index, index, capture_width,
                                           left, top, right, bottom);
        if direct != STATUS_UNSUPPORTED {
            if direct == STATUS_OK_PDFIUM && !out_new_index.is_null() {
                unsafe { *out_new_index = index };
            }
            return direct;
        }
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

    // Carry the stable id across the rebuild. add_stamp_annotation makes a
    // FRESH annotation, so without this the rebuilt stamp comes back anonymous
    // and every group, history entry and multi-selection naming it stops
    // resolving. Losing identity during an operation whose whole purpose is to
    // move an object around the list is the exact shape of bug that cost a
    // fortnight in v2.7.5, so it is closed here rather than left to callers.
    let carried_id = {
        let buf = get_annotation_id(doc_handle, page_index, index);
        let id = if buf.status == STATUS_OK_PDFIUM && !buf.data.is_null() && buf.len == ID_HEX_LEN {
            let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
            std::str::from_utf8(bytes).ok().map(|s| s.to_string())
        } else {
            None
        };
        free_byte_buffer(buf);
        id
    };

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
    let array = get_annotations(doc_handle, page_index);
    let count = array.len;
    free_annotation_array(array);
    let new_index = count.saturating_sub(1) as i32;

    if let Some(id_hex) = carried_id {
        unsafe {
            set_annotation_id(doc_handle, page_index, new_index, id_hex.as_ptr(), id_hex.len());
        }
    }

    if !out_new_index.is_null() {
        unsafe { *out_new_index = new_index };
    }

    STATUS_OK_PDFIUM
}

/// Pulls the BGRA pixels out of a stamp annotation, or None if the annotation
/// is not a stamp or carries no image.
///
/// PROCESSED, not raw, for the same reason the resize path uses it: the raw
/// bitmap is the image alone with its transparency in a separate soft mask, so
/// rebuilding from it turns every transparent signature into an opaque white
/// block.
fn extract_stamp_pixels(doc_handle: u64, page_index: i32, index: i32) -> Option<(i32, i32, Vec<u8>)> {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned()?;
    let doc_guard = lock(&doc);
    let mut page = doc_guard.pages().get(page_index as u16).ok()?;
    let mut annotations = page.annotations_mut();
    let annotation = annotations.get(index as usize).ok()?;
    // By reference: PdfPageAnnotation implements Drop, so matching by value
    // would try to move out of a type that cannot be moved.
    let PdfPageAnnotation::Stamp(stamp) = &annotation else {
        return None;
    };

    let objects = stamp.objects();
    for i in 0..objects.len() {
        let Ok(object) = objects.get(i) else {
            continue;
        };
        if let PdfPageObject::Image(image) = &object {
            // PROCESSED, so the image's transparency comes with it: the raw
            // buffer ignores the soft mask and turned a signature into a
            // black block. Processed also applies the object's transform,
            // which would bake in the current angle - that is why the caller
            // resets the object to UPRIGHT before extracting.
            let bitmap = image
                .get_processed_bitmap(&doc_guard)
                .or_else(|_| image.get_raw_bitmap());
            if let Ok(bitmap) = bitmap {
                let format = bitmap.format().unwrap_or(PdfBitmapFormat::BGRA);
                return to_bgra(bitmap.width(), bitmap.height(), format, &bitmap.as_raw_bytes());
            }
        }
    }
    None
}

/// Turns a stamp to an ABSOLUTE angle about its own centre, keeping its image
/// and its upright size.
///
/// Delete and re-add, not a transform of the object in place. An object taken
/// from `objects_mut().get()` is DETACHED, so `apply_matrix` on it never
/// reaches the stored annotation; that was measured, see the note on
/// `set_annotation_bounds`. Re-placing the image with a rotated matrix is what
/// actually turns the picture.
///
/// The angle is absolute so repeated rotations do not compound rounding, and
/// the upright rect comes from the tag rather than from the annotation's
/// current rectangle, which for a turned stamp is the ENLARGED box that
/// contains it.
#[unsafe(no_mangle)]
pub extern "C" fn rotate_stamp_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    degrees: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if !degrees.is_finite() {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        rotate_stamp_annotation_inner(doc_handle, page_index, index, capture_width, degrees, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

fn rotate_stamp_annotation_inner(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    degrees: f32,
    out_new_index: *mut i32,
) -> i32 {
    use pdfium_render::prelude::*;

    // Step 1: read the UPRIGHT box from the tag, and put the image object back
    // upright before anything reads its pixels.
    //
    // The pixels have to come out with their transparency (or a signature
    // becomes a black block) and WITHOUT the current angle baked in (or each
    // rotation compounds and resamples). get_processed_bitmap gives the first
    // and, on a turned stamp, spoils the second. Resetting the object's matrix
    // to upright first makes processed give both. FPDFAnnot_GetObject plus
    // FPDFAnnot_UpdateObject is what actually reaches the stored object;
    // pdfium-render's own accessor hands back a detached copy, which is the
    // "does nothing" noted on set_annotation_bounds.
    let upright = {
        let _guard = lock(&CALL_LOCK);
        let Some(doc) = lock(&core().documents).get(&doc_handle).cloned() else {
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
        let (origin_x, origin_top) = page_origin(&page);
        let scale = page_w / capture_width as f32;

        let mut annotations = page.annotations_mut();
        let Ok(annotation) = annotations.get(index as usize) else {
            return STATUS_INVALID_INPUT;
        };
        if !matches!(annotation, PdfPageAnnotation::Stamp(_)) {
            return STATUS_UNSUPPORTED;
        }

        let (ul, ut, ur, ub) = match annotation_tag(&annotation).as_deref().and_then(parse_stamp_tag)
        {
            Some((_, l, t, r, b)) => (l, t, r, b),
            None => {
                // Never rotated, so its rectangle IS its upright box.
                let Ok(bounds) = annotation.bounds() else {
                    return STATUS_INVALID_INPUT;
                };
                let per_pt = capture_width as f32 / page_w;
                (
                    (bounds.left().value - origin_x) * per_pt,
                    (origin_top - bounds.top().value) * per_pt,
                    (bounds.right().value - origin_x) * per_pt,
                    (origin_top - bounds.bottom().value) * per_pt,
                )
            }
        };

        let x0 = origin_x + ul * scale;
        let x1 = origin_x + ur * scale;
        let y1 = origin_top - ut * scale;
        let y0 = origin_top - ub * scale;

        let bindings = annotation.library_bindings();
        let annot_handle = annotation.annotation_handle();
        if bindings.FPDFAnnot_GetObjectCount(annot_handle) >= 1 {
            let object = bindings.FPDFAnnot_GetObject(annot_handle, 0);
            if !object.is_null() {
                // The plain unit-square placement: no rotation, upright box.
                let upright_matrix = FS_MATRIX {
                    a: x1 - x0,
                    b: 0.0,
                    c: 0.0,
                    d: y1 - y0,
                    e: x0,
                    f: y0,
                };
                bindings.FPDFPageObj_SetMatrix(object, &upright_matrix);
                bindings.FPDFAnnot_UpdateObject(annot_handle, object);
            }
        }

        (ul, ut, ur, ub)
    };

    // Step 2: take the pixels, now upright and still transparent.
    let Some((px_width, px_height, pixels)) = extract_stamp_pixels(doc_handle, page_index, index)
    else {
        return STATUS_UNSUPPORTED;
    };
    let expected = (px_width as usize)
        .saturating_mul(px_height as usize)
        .saturating_mul(4);
    if pixels.len() != expected || px_width <= 0 || px_height <= 0 {
        return STATUS_INVALID_INPUT;
    }

    // Step 3: re-place at the requested ABSOLUTE angle, through the same path
    // that puts a stamp down in the first place. Setting the annotation's
    // rectangle in place is not an option: PDFium re-fits a stamp's objects to
    // a changed rectangle, which squashes the picture by its own aspect.
    let removed = delete_annotation(doc_handle, page_index, index);
    if removed != STATUS_OK_PDFIUM {
        return removed;
    }

    let (l, t, r, b) = upright;
    let added = add_stamp_annotation_inner(
        doc_handle, page_index, capture_width,
        l, t, r, b,
        pixels.as_ptr(), pixels.len(), px_width, px_height,
        degrees,
    );
    if added != STATUS_OK_PDFIUM {
        return added;
    }

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
            0.0,
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
    rotation_deg: f32,
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

    let (origin_x, origin_top) = page_origin(&page);
    let scale = page_w / capture_width as f32;

    let x0 = origin_x + left * scale;
    let x1 = origin_x + right * scale;
    let y1 = origin_top - top * scale;
    let y0 = origin_top - bottom * scale;

    // The UPRIGHT box the caller asked for. A rotated stamp still records
    // this, not the turned one, so a later rotation is absolute rather than
    // compounding on itself.
    let (cx, cy) = ((x0 + x1) / 2.0, (y0 + y1) / 2.0);
    let (bw, bh) = (x1 - x0, y1 - y0);

    // NEGATED. The app's angle is clockwise ON SCREEN, where y runs down;
    // PDF space has y running up, and the textbook matrix [cos sin -sin cos]
    // turns counter-clockwise there. Without the negation a stamp turned the
    // opposite way to its own selection frame.
    let rad = (-rotation_deg).to_radians();
    let (sin, cos) = (rad.sin(), rad.cos());

    // PDFium CLIPS an annotation's appearance to its rectangle, so a turned
    // stamp needs the axis-aligned box that CONTAINS it or the corners are
    // shaved off. Same lesson the rotated text boxes taught.
    let half_w = (cos.abs() * bw + sin.abs() * bh) / 2.0;
    let half_h = (sin.abs() * bw + cos.abs() * bh) / 2.0;
    let bounds = PdfRect::new(
        PdfPoints::new(cy - half_h),
        PdfPoints::new(cx - half_w),
        PdfPoints::new(cy + half_h),
        PdfPoints::new(cx + half_w),
    );

    let Ok(mut annotation) = page.annotations_mut().create_stamp_annotation() else {
        return STATUS_INVALID_INPUT;
    };

    // Bounds BEFORE objects, for the same reason as ink: PDFium builds the
    // annotation's appearance form from its rect.
    if annotation.set_bounds(bounds).is_err() {
        return STATUS_INVALID_INPUT;
    }

    // Record the angle and the UPRIGHT rect so the stamp can be rotated again
    // later, and so the angle survives a save and reopen. Without this a
    // reopened stamp looks turned but reports itself upright, and the next
    // rotation would be measured from the wrong place.
    let _ = set_annotation_tag(
        &mut annotation,
        &stamp_tag(rotation_deg, left, top, right, bottom),
    );

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
    // An image object draws the UNIT SQUARE, so its matrix supplies position,
    // size AND rotation. Unrotated this is the plain [w 0 0 h x0 y0] that maps
    // the square onto the box; with an angle it maps the square onto the box
    // turned about its own centre:
    //     (u,v) -> centre + R(angle) * (w*(u-0.5), h*(v-0.5))
    let (ma, mb) = (cos * bw, sin * bw);
    let (mc, md) = (-sin * bh, cos * bh);
    let matrix = PdfMatrix::new(
        ma,
        mb,
        mc,
        md,
        cx - (ma + mc) / 2.0,
        cy - (mb + md) / 2.0,
    );
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

/// Line spacing as a multiple of the font size, and the inset of the text from
/// the box edge in multiples of it. Kept here so the app's overlay can lay text
/// out the same way and the two cannot disagree about where a line sits.
///
/// Complex scripts get MORE: Burmese, Devanagari and the like stack marks above
/// and hang them below the base line, so at the Latin spacing consecutive lines
/// collide. The taller spacing is used whenever a box needs shaping.
const TEXTBOX_LINE_HEIGHT: f32 = 1.3;
const TEXTBOX_LINE_HEIGHT_COMPLEX: f32 = 1.75;
const TEXTBOX_PADDING: f32 = 0.35;

/// Marks a stamp annotation as one of our text boxes, and records what it takes
/// to redraw it: `AyaanText:sizePx:RRGGBBAA:base64(text)`.
///
/// A text box is real vector text inside a stamp annotation, which is the only
/// annotation kind this binding lets us give drawable objects to. Once saved it
/// is otherwise indistinguishable from a placed image, so the tag is what lets
/// the box be recognised and its words edited again rather than only moved.
///
/// The text is base64 so a newline or a colon in it cannot be mistaken for a
/// field separator.
const TEXTBOX_TAG: &str = "AyaanText:";

/// The styled tag, which carries a text box's alignment, fill and outline as
/// well. A separate prefix from the plain one so an old box (which has none of
/// these) still parses, defaulting to left-aligned with no fill or outline.
///
/// `AyaanTextB:size:textRGBA:align:fillRGBA:outlineRGBA:outlineWpx:base64`
const TEXTBOX_TAG_STYLED: &str = "AyaanTextB:";

/// Text alignment. Crosses the FFI boundary and is mirrored in C#, so append
/// only.
pub const ALIGN_LEFT: i32 = 0;
pub const ALIGN_CENTER: i32 = 1;
pub const ALIGN_RIGHT: i32 = 2;
pub const ALIGN_JUSTIFY: i32 = 3;

/// A text box's look beyond its words: how the lines sit, and the optional
/// background and border. An alpha of 0 in fill or outline means "none".
#[derive(Clone, Copy)]
struct TextStyle {
    align: i32,
    fill: PackedRgba,
    outline: PackedRgba,
    outline_width_px: f32,
    underline: bool,
    strikethrough: bool,
    /// Clockwise rotation of the whole box about its own centre, in degrees.
    /// Zero for an upright box. Applied to every object of the box before it is
    /// added, and recorded in the tag so it round-trips. This is a geometric
    /// property, not a text look, but it rides with the style because it flows
    /// through the same place-and-tag path as everything else here, and other
    /// object kinds will want the same field.
    rotation_deg: f32,
}

impl TextStyle {
    fn plain() -> Self {
        TextStyle {
            align: ALIGN_LEFT,
            fill: PackedRgba(0),
            outline: PackedRgba(0),
            outline_width_px: 0.0,
            underline: false,
            strikethrough: false,
            rotation_deg: 0.0,
        }
    }
}

/// RGBA packed as 0xRRGGBBAA, the shape a colour crosses the FFI in when it
/// would otherwise cost four separate byte parameters.
#[derive(Clone, Copy)]
struct PackedRgba(u32);

impl PackedRgba {
    fn r(self) -> u8 { (self.0 >> 24) as u8 }
    fn g(self) -> u8 { (self.0 >> 16) as u8 }
    fn b(self) -> u8 { (self.0 >> 8) as u8 }
    fn a(self) -> u8 { self.0 as u8 }
    fn is_visible(self) -> bool { self.a() > 0 }
}

/// Font files read from disk, cached by absolute path so a given font is read
/// once per process. The bytes are re-embedded per text box (a `PdfFontToken`
/// cannot be cached: it wraps a raw handle and its constructor is pub(crate)),
/// but at least the disk read is not repeated for every box in the same font.
static FONT_BYTES: OnceLock<Mutex<HashMap<String, Arc<Vec<u8>>>>> = OnceLock::new();

fn font_file_bytes(path: &str) -> Option<Arc<Vec<u8>>> {
    let cache = FONT_BYTES.get_or_init(|| Mutex::new(HashMap::new()));
    let mut map = lock(cache);
    if let Some(bytes) = map.get(path) {
        return Some(bytes.clone());
    }
    let data = std::fs::read(path).ok()?;
    let arc = Arc::new(data);
    map.insert(path.to_string(), arc.clone());
    Some(arc)
}

/// Resolves the font a text box should draw in: the embedded TrueType font at
/// `font_path` loaded as a Unicode (CID) font, or Helvetica when no path is
/// given or the file cannot be loaded. Loading as CID is what gives >256 glyphs,
/// i.e. any Unicode a text field needs. Complex scripts (Devanagari, Myanmar,
/// Arabic) still need shaping (a later phase); this covers the rest.
fn resolve_text_font(
    doc: &mut pdfium_render::prelude::PdfDocument<'_>,
    font_path: Option<&str>,
) -> pdfium_render::prelude::PdfFontToken {
    if let Some(path) = font_path {
        if !path.is_empty() {
            if let Some(bytes) = font_file_bytes(path) {
                if let Ok(token) = doc.fonts_mut().load_true_type_from_bytes(&bytes, true) {
                    return token;
                }
            }
        }
    }
    doc.fonts_mut().helvetica()
}

/// True when the text contains a script PDFium's plain text path cannot lay out
/// correctly on its own: one that needs shaping (reordering, ligatures,
/// contextual and stacked forms). Covers the major complex scripts.
fn needs_shaping(text: &str) -> bool {
    text.chars().any(|c| {
        let u = c as u32;
        (0x0600..=0x06FF).contains(&u)      // Arabic
            || (0x0700..=0x074F).contains(&u) // Syriac
            || (0x0750..=0x077F).contains(&u) // Arabic Supplement
            || (0x0900..=0x0DFF).contains(&u) // Devanagari .. Malayalam .. Sinhala (Indic)
            || (0x0E00..=0x0FFF).contains(&u) // Thai, Lao, Tibetan
            || (0x1000..=0x109F).contains(&u) // Myanmar
            || (0x1780..=0x17FF).contains(&u) // Khmer
    })
}

/// One shaped glyph: its index in the font, and its advance and drawing offset
/// in POINTS (already scaled from font units by the requested size). This is
/// what the shaper decided, so drawing each glyph at these positions reproduces
/// the shaping (reordering, ligatures, mark placement) exactly.
struct ShapedGlyph {
    id: u32,
    x_advance: f32,
    x_offset: f32,
    y_offset: f32,
    /// Byte offset into the source string of the characters this glyph came
    /// from. Several glyphs can share one cluster (a character that shaped into
    /// a base plus marks), and one glyph can cover several characters (a
    /// conjunct or ligature). Kept because it is the ONLY link back from a
    /// drawn glyph to the text it means, and `/ToUnicode` cannot be built
    /// without it.
    cluster: u32,
}

/// Shapes one run of text in the given font with rustybuzz (a HarfBuzz port),
/// returning the visual-order glyphs with their per-glyph positions. None if the
/// font bytes cannot be parsed as a face. The glyph indices go to PDFium via
/// FPDFText_SetCharcodes; for the CID (Identity) font this app embeds, glyph
/// index == the code PDFium wants, so the shaped result draws.
fn shape_run(font_bytes: &[u8], text: &str, size_pts: f32) -> Option<Vec<ShapedGlyph>> {
    let face = rustybuzz::Face::from_slice(font_bytes, 0)?;
    let upem = face.units_per_em() as f32;
    if upem <= 0.0 {
        return None;
    }
    let scale = size_pts / upem;

    let mut buffer = rustybuzz::UnicodeBuffer::new();
    buffer.push_str(text);
    let shaped = rustybuzz::shape(&face, &[], buffer);

    let infos = shaped.glyph_infos();
    let positions = shaped.glyph_positions();
    if infos.is_empty() {
        return None;
    }

    let glyphs: Vec<ShapedGlyph> = infos
        .iter()
        .zip(positions.iter())
        .map(|(info, pos)| ShapedGlyph {
            id: info.glyph_id,
            x_advance: pos.x_advance as f32 * scale,
            x_offset: pos.x_offset as f32 * scale,
            y_offset: pos.y_offset as f32 * scale,
            cluster: info.cluster,
        })
        .collect();
    Some(glyphs)
}

/// What each shaped glyph MEANS, as the text it came from.
///
/// The link a `/ToUnicode` CMap needs, and the reason the app's text is drawn
/// correctly yet cannot be selected or searched: a glyph index is a number
/// inside one font subset, and nothing in the file says which characters
/// produced it.
///
/// Shaping is not one-to-one in either direction, so neither is this:
///
///   * ONE character can become SEVERAL glyphs (a base plus its marks). The
///     cluster's text is given to the FIRST glyph only; the rest map to
///     nothing. Giving it to each would make a copy of the text repeat every
///     mark's worth of characters.
///   * SEVERAL characters can become ONE glyph (a Burmese stack, a Devanagari
///     conjunct, an fi ligature). That glyph maps to the whole cluster, which
///     is why the CMap needs multi-character destinations rather than a plain
///     one-to-one table.
///
/// Clusters are BYTE offsets into the source, and they run backwards for a
/// right-to-left script, so the extent of a cluster is found from the sorted
/// set of boundaries rather than from the glyph order.
fn glyph_text_map(glyphs: &[ShapedGlyph], source: &str) -> Vec<(u32, String)> {
    if glyphs.is_empty() || source.is_empty() {
        return Vec::new();
    }

    // Every distinct cluster start, in ascending order, so each one's text runs
    // to the next boundary. Works for right-to-left runs too, where the glyphs
    // themselves arrive in the opposite order.
    let mut starts: Vec<usize> = glyphs.iter().map(|g| g.cluster as usize).collect();
    starts.sort_unstable();
    starts.dedup();

    let extent = |start: usize| -> &str {
        let end = starts
            .iter()
            .copied()
            .find(|&s| s > start)
            .unwrap_or(source.len());
        // A cluster value that does not land on a character boundary is a
        // malformed run rather than something to panic over.
        if start > source.len() || end > source.len()
            || !source.is_char_boundary(start) || !source.is_char_boundary(end)
        {
            return "";
        }
        &source[start..end]
    };

    let mut seen: Vec<u32> = Vec::new();
    let mut out = Vec::with_capacity(glyphs.len());

    for glyph in glyphs {
        if seen.contains(&glyph.cluster) {
            // A later glyph of a cluster already accounted for: it carries no
            // text of its own.
            continue;
        }
        seen.push(glyph.cluster);

        let text = extent(glyph.cluster as usize);
        if !text.is_empty() {
            out.push((glyph.id, text.to_string()));
        }
    }

    out
}

/// Builds a `/ToUnicode` CMap for a set of glyph-to-text mappings.
///
/// The stream a PDF reader consults to answer "what does this glyph say". With
/// it, our shaped text becomes selectable, searchable and copyable in Acrobat
/// and everything else; without it the glyphs draw perfectly and mean nothing.
///
/// Codes are two bytes because the font is Identity-encoded: the code written
/// into the content stream IS the glyph index. Destinations are UTF-16BE, which
/// is what a `bfchar` destination is defined to be, and is also why one glyph
/// can map to several characters.
///
/// `bfchar` throughout rather than `bfrange`. Ranges only pay when consecutive
/// glyph ids carry consecutive characters, which shaped complex script almost
/// never produces, and getting a range subtly wrong corrupts a span of text
/// rather than one glyph.
fn build_tounicode_cmap(mappings: &[(u32, String)]) -> String {
    let mut sorted: Vec<&(u32, String)> = mappings.iter().collect();
    sorted.sort_by_key(|(id, _)| *id);
    sorted.dedup_by_key(|(id, _)| *id);

    let mut out = String::with_capacity(sorted.len() * 32 + 512);
    out.push_str(
        "/CIDInit /ProcSet findresource begin\n\
         12 dict begin\n\
         begincmap\n\
         /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n\
         /CMapName /Adobe-Identity-UCS def\n\
         /CMapType 2 def\n\
         1 begincodespacerange\n\
         <0000> <FFFF>\n\
         endcodespacerange\n",
    );

    // A bfchar section may hold at most 100 entries, per the spec. Longer runs
    // are split rather than emitted as one oversized section, which some
    // readers reject outright.
    for chunk in sorted.chunks(100) {
        out.push_str(&format!("{} beginbfchar\n", chunk.len()));
        for (id, text) in chunk {
            out.push_str(&format!("<{:04X}> <", id));
            for unit in text.encode_utf16() {
                out.push_str(&format!("{:04X}", unit));
            }
            out.push_str(">\n");
        }
        out.push_str("endbfchar\n");
    }

    out.push_str("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
    out
}

/// The total advance width of a shaped run, in points.
fn shaped_width(glyphs: &[ShapedGlyph]) -> f32 {
    glyphs.iter().map(|g| g.x_advance).sum()
}

/// Width of a line in points, measured the way it will actually be drawn: with
/// rustybuzz for a complex script (when a real font is loaded), else with
/// PDFium's own text metrics. Keeps wrapping and alignment in step with the
/// shaped result, so a committed box wraps where the editor did.
fn measure_run_width<'a>(
    doc: &pdfium_render::prelude::PdfDocument<'a>,
    font: pdfium_render::prelude::PdfFontToken,
    font_bytes: Option<&[u8]>,
    text: &str,
    size_pts: f32,
) -> f32 {
    if let Some(fb) = font_bytes {
        if needs_shaping(text) {
            if let Some(glyphs) = shape_run(fb, text, size_pts) {
                return shaped_width(&glyphs);
            }
        }
    }
    measure_text_width(doc, font, text, size_pts)
}

#[allow(dead_code)] // the styled tag is what boxes now write; kept for tests
fn textbox_tag(text: &str, size_px: f32, r: u8, g: u8, b: u8, a: u8) -> String {
    format!(
        "{TEXTBOX_TAG}{:.4}:{:02X}{:02X}{:02X}{:02X}:{}",
        size_px,
        r,
        g,
        b,
        a,
        base64_encode(text.as_bytes())
    )
}

#[allow(clippy::too_many_arguments)]
fn textbox_tag_styled(
    text: &str,
    size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
    style: TextStyle,
    font_path: Option<&str>,
    box_rect: [f32; 4],
) -> String {
    // Round-trip fields go AFTER the original six so an older box (which lacks
    // them) still parses: a decorations flag (bit0 underline, bit1 strikethrough),
    // the font FILE the box was drawn in (base64, so a path with a colon or a
    // space is never read as a separator), the clockwise rotation in degrees, and
    // the box's OWN normalized rect (left, top, right, bottom). The rect is the
    // UPRIGHT box; a rotated box's annotation rect is the enlarged bounding box,
    // so the overlay needs this to draw the tight frame and to spin the exact box.
    // The bold/italic cut is the file itself, so it needs no flag of its own.
    // Each addition slots in just before the words, which stay LAST and remain
    // colon-free base64, so every reader that keys off "last field = text" and
    // "gate extras on length" keeps working.
    let flags = (style.underline as u32) | ((style.strikethrough as u32) << 1);
    let [bl, bt, br, bb] = box_rect;
    format!(
        "{TEXTBOX_TAG_STYLED}{:.4}:{:02X}{:02X}{:02X}{:02X}:{}:{:08X}:{:08X}:{:.2}:{}:{}:{:.2}:{:.5}:{:.5}:{:.5}:{:.5}:{}",
        size_px,
        r,
        g,
        b,
        a,
        style.align,
        style.fill.0,
        style.outline.0,
        style.outline_width_px,
        flags,
        base64_encode(font_path.unwrap_or("").as_bytes()),
        style.rotation_deg,
        bl,
        bt,
        br,
        bb,
        base64_encode(text.as_bytes())
    )
}

/// The size, colour and text recorded on a text box, or None if the annotation
/// is not one of ours. Handles both the plain and the styled tag; the styled
/// fields (align, fill, outline) are not returned here since the tests that use
/// this only check the words and size, and the load-bearing reader is C#.
#[allow(dead_code)] // mirrors the C# TextBoxTagReader; used in tests
fn parse_textbox_tag(contents: &str) -> Option<(f32, u8, u8, u8, u8, String)> {
    let byte = |rgba: &str, i: usize| u8::from_str_radix(&rgba[i..i + 2], 16).ok();
    let contents = strip_id_prefix(contents).1;

    if let Some(rest) = contents.strip_prefix(TEXTBOX_TAG_STYLED) {
        // size:textRGBA:align:fillRGBA:outlineRGBA:outlineW:[flags:base64(font):]base64(text)
        // The words are ALWAYS the last field; the optional flags and font sit
        // between the six fixed fields and the text, so a six-field box written
        // before the round-trip existed still parses. Every field but the words
        // is colon-free, so a full split is unambiguous.
        let parts: Vec<&str> = rest.split(':').collect();
        if parts.len() < 7 {
            return None;
        }
        let size: f32 = parts[0].parse().ok()?;
        if !size.is_finite() || size <= 0.0 {
            return None;
        }
        let rgba = parts[1];
        if rgba.len() != 8 {
            return None;
        }
        let r = byte(rgba, 0)?;
        let g = byte(rgba, 2)?;
        let b = byte(rgba, 4)?;
        let a = byte(rgba, 6)?;
        let text = String::from_utf8(base64_decode(parts[parts.len() - 1])?).ok()?;
        return Some((size, r, g, b, a, text));
    }

    let rest = contents.strip_prefix(TEXTBOX_TAG)?;
    let mut parts = rest.splitn(3, ':');

    let size: f32 = parts.next()?.parse().ok()?;
    if !size.is_finite() || size <= 0.0 {
        return None;
    }

    let rgba = parts.next()?;
    if rgba.len() != 8 {
        return None;
    }

    let text = String::from_utf8(base64_decode(parts.next()?)?).ok()?;
    Some((size, byte(rgba, 0)?, byte(rgba, 2)?, byte(rgba, 4)?, byte(rgba, 6)?, text))
}

/// Everything a text box needs to be redrawn: its words, size, colour, full
/// style, and font file. This is what lets a box be RE-LAID-OUT at a new size
/// (so a resize re-wraps the text rather than stretching the rendered pixels),
/// the same way a shape is redrawn from its own tag. `None` if the tag is not a
/// text box of ours.
struct ParsedTextBox {
    text: String,
    size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
    style: TextStyle,
    font_path: Option<String>,
}

fn parse_textbox_tag_full(contents: &str) -> Option<ParsedTextBox> {
    let byte = |rgba: &str, i: usize| u8::from_str_radix(&rgba[i..i + 2], 16).ok();
    let rgba4 = |s: &str| -> Option<(u8, u8, u8, u8)> {
        if s.len() != 8 {
            return None;
        }
        Some((byte(s, 0)?, byte(s, 2)?, byte(s, 4)?, byte(s, 6)?))
    };
    let contents = strip_id_prefix(contents).1;

    if let Some(rest) = contents.strip_prefix(TEXTBOX_TAG_STYLED) {
        let parts: Vec<&str> = rest.split(':').collect();
        if parts.len() < 7 {
            return None;
        }
        let size_px: f32 = parts[0].parse().ok()?;
        if !size_px.is_finite() || size_px <= 0.0 {
            return None;
        }
        let (r, g, b, a) = rgba4(parts[1])?;
        let align = parts[2]
            .parse::<i32>()
            .ok()
            .filter(|n| matches!(*n, ALIGN_LEFT | ALIGN_CENTER | ALIGN_RIGHT | ALIGN_JUSTIFY))
            .unwrap_or(ALIGN_LEFT);
        let fill = PackedRgba(u32::from_str_radix(parts[3], 16).unwrap_or(0));
        let outline = PackedRgba(u32::from_str_radix(parts[4], 16).unwrap_or(0));
        let outline_width_px: f32 = parts[5].parse().unwrap_or(0.0);

        // The round-trip extras (flags + font) are present only when there are
        // more than the six fixed fields plus the words. Rotation is a later
        // addition still, so it is gated on its own longer length.
        let (underline, strikethrough, font_path) = if parts.len() >= 9 {
            let flags: u32 = parts[6].parse().unwrap_or(0);
            let font = base64_decode(parts[7])
                .and_then(|b| String::from_utf8(b).ok())
                .filter(|s| !s.is_empty());
            ((flags & 1) != 0, (flags & 2) != 0, font)
        } else {
            (false, false, None)
        };
        let rotation_deg: f32 = if parts.len() >= 10 { parts[8].parse().unwrap_or(0.0) } else { 0.0 };

        let text = String::from_utf8(base64_decode(parts[parts.len() - 1])?).ok()?;
        return Some(ParsedTextBox {
            text,
            size_px,
            r,
            g,
            b,
            a,
            style: TextStyle {
                align,
                fill,
                outline,
                outline_width_px,
                underline,
                strikethrough,
                rotation_deg,
            },
            font_path,
        });
    }

    // The plain tag: left-aligned, no fill/outline/decoration, default font.
    let rest = contents.strip_prefix(TEXTBOX_TAG)?;
    let parts: Vec<&str> = rest.splitn(3, ':').collect();
    if parts.len() != 3 {
        return None;
    }
    let size_px: f32 = parts[0].parse().ok()?;
    if !size_px.is_finite() || size_px <= 0.0 {
        return None;
    }
    let (r, g, b, a) = rgba4(parts[1])?;
    let text = String::from_utf8(base64_decode(parts[2])?).ok()?;
    Some(ParsedTextBox {
        text,
        size_px,
        r,
        g,
        b,
        a,
        style: TextStyle::plain(),
        font_path: None,
    })
}

/// Minimal base64, so the core needs no extra dependency for one small string.
fn base64_encode(input: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in input.chunks(3) {
        let b = [
            chunk[0],
            *chunk.get(1).unwrap_or(&0),
            *chunk.get(2).unwrap_or(&0),
        ];
        let n = ((b[0] as u32) << 16) | ((b[1] as u32) << 8) | b[2] as u32;
        out.push(T[((n >> 18) & 63) as usize] as char);
        out.push(T[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 { T[((n >> 6) & 63) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { T[(n & 63) as usize] as char } else { '=' });
    }
    out
}

#[allow(dead_code)] // used only by parse_textbox_tag, above
fn base64_decode(input: &str) -> Option<Vec<u8>> {
    fn val(c: u8) -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }
    let bytes: Vec<u8> = input.bytes().filter(|&c| c != b'=').collect();
    let mut out = Vec::new();
    for chunk in bytes.chunks(4) {
        let mut n = 0u32;
        for (i, &c) in chunk.iter().enumerate() {
            n |= val(c)? << (18 - 6 * i);
        }
        out.push((n >> 16) as u8);
        if chunk.len() > 2 {
            out.push((n >> 8) as u8);
        }
        if chunk.len() > 3 {
            out.push(n as u8);
        }
    }
    Some(out)
}

/// Width of a string in Helvetica at a given size, measured by PDFium, in
/// points. `f32::MAX` if it cannot be measured, so a failure forces a wrap
/// rather than silently disabling it.
fn measure_text_width<'a>(
    doc: &pdfium_render::prelude::PdfDocument<'a>,
    font: pdfium_render::prelude::PdfFontToken,
    s: &str,
    size_pts: f32,
) -> f32 {
    use pdfium_render::prelude::*;
    if s.is_empty() {
        return 0.0;
    }
    match PdfPageTextObject::new(doc, s, font, PdfPoints::new(size_pts)) {
        Ok(obj) => obj.bounds().map(|b| b.width().value).unwrap_or(f32::MAX),
        Err(_) => f32::MAX,
    }
}

/// Greedily wraps text to a maximum width, keeping the user's own line breaks.
///
/// Measured against real Helvetica metrics rather than an average character
/// width, so the wrap matches what actually renders. A single word wider than
/// the box is left to overflow rather than split mid-word, which is what a
/// person expects of a long URL or token.
/// One wrapped line: its text, and whether justify may stretch it.
///
/// The LAST line of a paragraph is never justified, the way every text engine
/// leaves it, or the final short line of a paragraph would be stretched across
/// the whole width into sparse, ugly gaps.
struct WrapLine {
    text: String,
    justifiable: bool,
}

fn wrap_to_width<'a>(
    doc: &pdfium_render::prelude::PdfDocument<'a>,
    font: pdfium_render::prelude::PdfFontToken,
    font_bytes: Option<&[u8]>,
    text: &str,
    max_width: f32,
    size_pts: f32,
) -> Vec<WrapLine> {
    let mut out = Vec::new();
    for para in text.split('\n') {
        // A blank line the user typed is preserved, so paragraph spacing
        // survives the wrap.
        if para.is_empty() {
            out.push(WrapLine { text: String::new(), justifiable: false });
            continue;
        }

        let mut lines: Vec<String> = Vec::new();
        let mut line = String::new();
        for word in para.split(' ') {
            let candidate = if line.is_empty() {
                word.to_string()
            } else {
                format!("{line} {word}")
            };

            // The first word on a line always goes on, even if it overflows,
            // since there is nowhere else to put it.
            if line.is_empty() || measure_run_width(doc, font, font_bytes, &candidate, size_pts) <= max_width {
                line = candidate;
            } else {
                lines.push(std::mem::take(&mut line));
                line = word.to_string();
            }
        }
        lines.push(line);

        let last = lines.len() - 1;
        for (i, l) in lines.into_iter().enumerate() {
            out.push(WrapLine { text: l, justifiable: i != last });
        }
    }
    out
}

/// Places text as a real, editable text box: vector text inside a stamp
/// annotation.
///
/// A `/FreeText` annotation would be the right label, but it draws nothing here
/// for the same reason a bare `/Square` does: no way to attach the appearance
/// stream it needs. A stamp annotation, by contrast, gets its appearance built
/// from the objects put inside it, and this binding lets us add real text
/// objects to one. So the text is genuine vector text, crisp at any zoom, and
/// the box round-trips as an editable object because the words are stored in
/// its `/Contents` tag.
///
/// A machine-readable identity that survives edits.
///
/// PDFium cannot edit annotations in place. Every commit deletes and re-adds,
/// which reshuffles the per-page annotation array. Any C# state that
/// references annotations by index (groups, drag origin, selection extras)
/// goes stale after any write. This prefix embeds a stable Guid at the start
/// of the annotation's /Contents so the C# side can find "the same
/// annotation" after such a churn. The rest of the /Contents string is the
/// existing tag body (`AyaanShape:...`, `AyaanText:...`, etc.), unchanged.
///
/// Format: `ID:<32 lowercase hex chars>|<existing tag body>`
///
/// A `|` is used as the separator because every existing tag field uses `:`,
/// so it cannot collide. Annotations without this prefix are legacy and get
/// a fresh Guid assigned in C# on first load; the first save writes it back.
/// The origin every annotation coordinate must be measured from: the corner
/// of the box PDFium actually RENDERS.
///
/// Crop box first, media box as the fallback, which is exactly what
/// `render_region_inner` does when it saves and restores the page's box. The
/// two agree on most files, so this went unnoticed for a long time; on a file
/// where they differ (print-ready PDFs, scans, anything with trim marks) every
/// annotation was written relative to the media corner and then drawn relative
/// to the crop corner, landing displaced by the difference between them. The
/// selection frame, positioned from `get_annotations` reading the media corner
/// again, then sat off the shape the user could see.
///
/// Proved by `a_shape_lands_where_it_was_asked_for_on_a_cropped_page`, with
/// `..._on_an_uncropped_page` as the control.
fn page_origin(page: &pdfium_render::prelude::PdfPage) -> (f32, f32) {
    let boxes = page.boundaries();
    boxes
        .crop()
        .map(|b| b.bounds)
        .or_else(|_| boxes.media().map(|b| b.bounds))
        .map(|b| (b.left().value, b.top().value))
        .unwrap_or((0.0, page.height().value))
}

/// The annotation dictionary key our machine-readable tag lives under.
///
/// NOT `/Contents`. That field is the annotation's COMMENT text and readers
/// display it: with the tag in there, every shape and text box this app made
/// appeared in Acrobat's comment panel as a line of gibberish, with a sticky
/// note icon on the page, in any PDF the user shared. A private key is
/// ignored by readers, which is where private data belongs.
///
/// Reads fall back to `/Contents` so documents written by earlier versions
/// stay editable; the next write moves them over and clears the comment.
const TAG_KEY: &str = "AyaanTag";

/// Private key holding an annotation's GROUP, separate from its tag.
///
/// Separate on purpose. A group can hold shapes, text boxes and image stamps
/// together, and those three have entirely different tag formats, so there is
/// no one tag field that could carry it. A key of its own is kind-agnostic and
/// touches none of the existing formats.
const GROUP_KEY: &str = "AyaanGroup";

/// Writes an annotation's group key. An empty value clears it.
fn set_annotation_group<A: pdfium_render::prelude::PdfPageAnnotationCommon>(
    annotation: &mut A,
    group: &str,
) -> bool {
    use pdfium_render::prelude::*;
    let mut utf16: Vec<u16> = group.encode_utf16().collect();
    utf16.push(0);
    annotation.library_bindings().FPDFAnnot_SetStringValue(
        annotation.annotation_handle(),
        GROUP_KEY,
        utf16.as_ptr(),
    ) != 0
}

/// Reads an annotation's group key, or None when it belongs to no group.
///
/// Unlike the tag there is NO /Contents fallback: a group is something this app
/// wrote or it does not exist, and reading a user's comment as a group id would
/// invent memberships out of prose.
fn annotation_group<A: pdfium_render::prelude::PdfPageAnnotationCommon>(
    annotation: &A,
) -> Option<String> {
    use pdfium_render::prelude::*;
    let bindings = annotation.library_bindings();
    let handle = annotation.annotation_handle();

    // Length is in BYTES and includes the UTF-16 terminator, so 2 or under is
    // empty rather than a value.
    let len = bindings.FPDFAnnot_GetStringValue(handle, GROUP_KEY, std::ptr::null_mut(), 0);
    if len <= 2 {
        return None;
    }
    let mut buf = vec![0u16; len as usize / 2];
    bindings.FPDFAnnot_GetStringValue(handle, GROUP_KEY, buf.as_mut_ptr(), len);
    while buf.last() == Some(&0) {
        buf.pop();
    }
    String::from_utf16(&buf).ok().filter(|s| !s.is_empty())
}

/// Records which group an annotation belongs to. `group_len` of 0 clears it.
///
/// Groups are held in the view model while the app runs and written here when
/// the document is saved, so they survive a close and reopen. Before this they
/// were session-only: every group the user made evaporated when the file did.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_annotation_group_id(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    group_utf8: *const u8,
    group_len: usize,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 {
        return STATUS_INVALID_INPUT;
    }
    // Either empty (clear) or exactly one id, same shape as an annotation id.
    if group_len != 0 && (group_utf8.is_null() || group_len != ID_HEX_LEN) {
        return STATUS_INVALID_INPUT;
    }

    let owned = if group_len == 0 {
        String::new()
    } else {
        let bytes = unsafe { std::slice::from_raw_parts(group_utf8, group_len) };
        match std::str::from_utf8(bytes) {
            Ok(s) if s.chars().all(|c| c.is_ascii_hexdigit()) => s.to_string(),
            _ => return STATUS_INVALID_INPUT,
        }
    };

    panic::catch_unwind(|| {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else { return STATUS_INVALID_INPUT; };
        let doc_guard = lock(&doc);
        let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
            return STATUS_INVALID_INPUT;
        };
        let annotations = page.annotations_mut();
        let Some(mut annotation) = annotations.iter().nth(index as usize) else {
            return STATUS_INVALID_INPUT;
        };
        if set_annotation_group(&mut annotation, &owned) {
            STATUS_OK_PDFIUM
        } else {
            STATUS_INVALID_INPUT
        }
    })
    .unwrap_or(STATUS_PANIC)
}

/// The annotation's group id as 32 lowercase hex chars, or empty.
#[unsafe(no_mangle)]
pub extern "C" fn get_annotation_group_id(doc_handle: u64, page_index: i32, index: i32) -> ByteBuffer {
    if doc_handle == 0 || page_index < 0 || index < 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else { return ByteBuffer::err(STATUS_INVALID_INPUT); };
        let doc_guard = lock(&doc);
        let Ok(page) = doc_guard.pages().get(page_index as u16) else {
            return ByteBuffer::err(STATUS_INVALID_INPUT);
        };
        let Some(annotation) = page.annotations().iter().nth(index as usize) else {
            return ByteBuffer::err(STATUS_INVALID_INPUT);
        };
        // Same hand-off as the id reader: leak the box to the caller, who frees
        // it with free_byte_buffer.
        let owned = annotation_group(&annotation).unwrap_or_default();
        let mut boxed = owned.into_bytes().into_boxed_slice();
        let buffer = ByteBuffer {
            data: boxed.as_mut_ptr(),
            len: boxed.len(),
            status: STATUS_OK_PDFIUM,
        };
        std::mem::forget(boxed);
        buffer
    })
    .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

/// Writes the app's tag to the private key and clears `/Contents`, so the
/// annotation carries no reader-visible comment.
///
/// `/Contents` STAYS EMPTY, including for text boxes. Putting a text box's
/// words there was tried, so that other readers could at least see the text
/// our shaped glyphs are unreadable as. It works, and the cost is worse than
/// the problem: `/Contents` on a markup annotation IS a comment, so Acrobat
/// draws a sticky-note icon on the page and opens a reply popup over the text.
/// No annotation flag keeps the text and suppresses the bubble, because from
/// the format's point of view there is nothing to suppress: a stamp with
/// contents is a comment.
///
/// Making the glyphs themselves readable needs a `/ToUnicode` CMap on the
/// embedded font, which costs nothing visually and is the real fix.
fn set_annotation_tag<A: pdfium_render::prelude::PdfPageAnnotationCommon>(
    annotation: &mut A,
    tag: &str,
) -> bool {
    use pdfium_render::prelude::*;
    let mut utf16: Vec<u16> = tag.encode_utf16().collect();
    utf16.push(0);
    let ok = annotation.library_bindings().FPDFAnnot_SetStringValue(
        annotation.annotation_handle(),
        TAG_KEY,
        utf16.as_ptr(),
    ) != 0;
    // Cleared whether or not the private write succeeded; a stale tag left in
    // /Contents would still be shown to the reader.
    let _ = annotation.set_contents("");
    ok
}

/// The app's tag for an annotation: the private key first, then `/Contents`
/// for documents written before the key existed. `None` when the annotation
/// is not one of ours.
fn annotation_tag<A: pdfium_render::prelude::PdfPageAnnotationCommon>(
    annotation: &A,
) -> Option<String> {
    use pdfium_render::prelude::*;
    let bindings = annotation.library_bindings();
    let handle = annotation.annotation_handle();

    // Length is in BYTES and includes the UTF-16 null terminator, so anything
    // at or under 2 is an empty value rather than a tag.
    let len = bindings.FPDFAnnot_GetStringValue(handle, TAG_KEY, std::ptr::null_mut(), 0);
    if len > 2 {
        let mut buf = vec![0u16; len as usize / 2];
        bindings.FPDFAnnot_GetStringValue(handle, TAG_KEY, buf.as_mut_ptr(), len);
        while buf.last() == Some(&0) {
            buf.pop();
        }
        if let Ok(s) = String::from_utf16(&buf) {
            if !s.is_empty() {
                return Some(s);
            }
        }
    }

    // Legacy fallback, for documents written before the private key existed.
    //
    // Only accepted when it LOOKS like one of our tags. /Contents is a
    // COMMENT: on any annotation this app did not write, it holds whatever a
    // person typed. An unfiltered fallback hands that prose to the tag parsers
    // as though this app had written it. They reject it today, but every
    // parser sees it on every read, and one with a looser prefix would start
    // claiming the user's own words.
    annotation
        .contents()
        .filter(|s| looks_like_tag(s))
}

/// Whether a string is one of our tags rather than a person's words. Every tag
/// this app has ever written starts with an id prefix or with `Ayaan`.
fn looks_like_tag(s: &str) -> bool {
    let body = strip_id_prefix(s).1;
    body.starts_with("Ayaan")
}

const ID_PREFIX: &str = "ID:";
const ID_SEPARATOR: char = '|';
const ID_HEX_LEN: usize = 32;

/// If `contents` starts with a well-formed ID prefix, returns
/// `(Some(id_hex), body)`. Otherwise returns `(None, contents)` unchanged.
/// Every tag parser calls this before matching its own prefix, so a tag with
/// or without an ID parses identically once the ID is stripped.
fn strip_id_prefix(contents: &str) -> (Option<&str>, &str) {
    let Some(rest) = contents.strip_prefix(ID_PREFIX) else {
        return (None, contents);
    };
    let Some(sep_pos) = rest.find(ID_SEPARATOR) else {
        return (None, contents);
    };
    let id = &rest[..sep_pos];
    if id.len() != ID_HEX_LEN || !id.bytes().all(|b| b.is_ascii_hexdigit()) {
        return (None, contents);
    }
    (Some(id), &rest[sep_pos + 1..])
}

/// Reads an annotation's `/Contents` string, as UTF-8 bytes.
///
/// The one field this app stores machine data in: a shape's kind and a text
/// box's words both live here. Reading it back is what makes a text box
/// re-editable, since the words themselves are the thing that has to be
/// recovered. Returns an empty successful buffer when the annotation has no
/// contents. Free with `free_byte_buffer`.
#[unsafe(no_mangle)]
pub extern "C" fn get_annotation_contents(doc_handle: u64, page_index: i32, index: i32) -> ByteBuffer {
    if doc_handle == 0 || page_index < 0 || index < 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_annotation_contents_inner(doc_handle, page_index, index))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_annotation_contents_inner(doc_handle: u64, page_index: i32, index: i32) -> ByteBuffer {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);
    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let Some(annotation) = page.annotations().iter().nth(index as usize) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let text = annotation_tag(&annotation).unwrap_or_default();
    let mut boxed = text.into_bytes().into_boxed_slice();
    let buffer = ByteBuffer { data: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
    std::mem::forget(boxed);
    buffer
}

/// Returns the 32-char lowercase-hex Guid embedded in an annotation's
/// /Contents, or an empty buffer if the annotation has no such prefix. See
/// [`ID_PREFIX`] for why the prefix exists and its format.
#[unsafe(no_mangle)]
pub extern "C" fn get_annotation_id(doc_handle: u64, page_index: i32, index: i32) -> ByteBuffer {
    if doc_handle == 0 || page_index < 0 || index < 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_annotation_id_inner(doc_handle, page_index, index))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_annotation_id_inner(doc_handle: u64, page_index: i32, index: i32) -> ByteBuffer {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let doc_guard = lock(&doc);
    let Ok(page) = doc_guard.pages().get(page_index as u16) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let Some(annotation) = page.annotations().iter().nth(index as usize) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let contents = annotation_tag(&annotation).unwrap_or_default();
    let id_owned: String = strip_id_prefix(&contents).0.map(str::to_owned).unwrap_or_default();
    let mut boxed = id_owned.into_bytes().into_boxed_slice();
    let buffer = ByteBuffer { data: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
    std::mem::forget(boxed);
    buffer
}

/// Stamps `id_hex` (32 lowercase hex chars) onto the annotation's /Contents
/// as an ID prefix. Any existing ID prefix is replaced. The existing tag body
/// is preserved unchanged.
///
/// C# calls this immediately after every add_*/resize_* to attach a stable
/// identity that survives the next delete+re-add churn. Returns
/// STATUS_OK_PDFIUM on success.
///
/// # Safety
/// `id_utf8` must point to `id_len` valid bytes of ASCII hex.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_annotation_id(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    id_utf8: *const u8,
    id_len: usize,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || id_utf8.is_null() || id_len != ID_HEX_LEN {
        return STATUS_INVALID_INPUT;
    }
    let id_bytes = unsafe { std::slice::from_raw_parts(id_utf8, id_len) };
    let Ok(id_hex) = std::str::from_utf8(id_bytes) else {
        return STATUS_INVALID_INPUT;
    };
    if !id_hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return STATUS_INVALID_INPUT;
    }
    let id_owned = id_hex.to_ascii_lowercase();
    panic::catch_unwind(|| set_annotation_id_inner(doc_handle, page_index, index, &id_owned))
        .unwrap_or(STATUS_PANIC)
}

fn set_annotation_id_inner(doc_handle: u64, page_index: i32, index: i32, id_hex: &str) -> i32 {
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
    let annotations = page.annotations_mut();
    let Some(mut annotation) = annotations.iter().nth(index as usize) else {
        return STATUS_INVALID_INPUT;
    };

    let existing = annotation_tag(&annotation).unwrap_or_default();
    let body = strip_id_prefix(&existing).1;
    let new_tag = format!("{ID_PREFIX}{id_hex}{ID_SEPARATOR}{body}");
    if !set_annotation_tag(&mut annotation, &new_tag) {
        return STATUS_INVALID_INPUT;
    }
    STATUS_OK_PDFIUM
}

/// Replaces an annotation's tag BODY, keeping any ID prefix intact.
///
/// The complement of [`set_annotation_id`]: that one swaps the identity and
/// keeps the description, this one swaps the description and keeps the
/// identity. Ink needs it because a stroke's geometry belongs in its tag, the
/// way a shape's does, but the points are only known to the caller after
/// smoothing, so they cannot be written by the add call itself.
///
/// # Safety
/// `body_utf8` must point to `body_len` valid UTF-8 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_annotation_body(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    body_utf8: *const u8,
    body_len: usize,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || body_utf8.is_null() {
        return STATUS_INVALID_INPUT;
    }
    let bytes = unsafe { std::slice::from_raw_parts(body_utf8, body_len) };
    let Ok(body) = std::str::from_utf8(bytes) else {
        return STATUS_INVALID_INPUT;
    };
    let body_owned = body.to_owned();
    panic::catch_unwind(|| set_annotation_body_inner(doc_handle, page_index, index, &body_owned))
        .unwrap_or(STATUS_PANIC)
}

fn set_annotation_body_inner(doc_handle: u64, page_index: i32, index: i32, body: &str) -> i32 {
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
    let annotations = page.annotations_mut();
    let Some(mut annotation) = annotations.iter().nth(index as usize) else {
        return STATUS_INVALID_INPUT;
    };

    // Read the ID back out and put it in front again. Writing the body alone
    // would drop the identity, and an annotation whose id changes under it is
    // the bug class that broke grouping and z-order.
    let existing = annotation_tag(&annotation).unwrap_or_default();
    let new_tag = match strip_id_prefix(&existing).0 {
        Some(id) => format!("{ID_PREFIX}{id}{ID_SEPARATOR}{body}"),
        None => body.to_owned(),
    };
    if !set_annotation_tag(&mut annotation, &new_tag) {
        return STATUS_INVALID_INPUT;
    }
    STATUS_OK_PDFIUM
}

/// `capture_width` is the render width the box and font size were captured at.
/// `font_size_px` is in that same capture space. Lines are split on `\n`.
///
/// The plain form: left-aligned, no fill, no outline. Kept so the many tests
/// and any older caller need not know about styling.
#[unsafe(no_mangle)]
pub extern "C" fn add_text_box_annotation(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    text_utf8: *const u8,
    text_len: usize,
    font_size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
) -> i32 {
    add_text_box_common(
        doc_handle, page_index, capture_width, left, top, right, bottom, text_utf8, text_len,
        font_size_px, r, g, b, a, TextStyle::plain(), None,
    )
}

/// The styled form: alignment, plus an optional fill and outline. `align` is one
/// of `ALIGN_*`; `fill_rgba` and `outline_rgba` are 0xRRGGBBAA with alpha 0
/// meaning none; `outline_width_px` is in capture space.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn add_text_box_annotation_styled(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    text_utf8: *const u8,
    text_len: usize,
    font_size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
    align: i32,
    fill_rgba: u32,
    outline_rgba: u32,
    outline_width_px: f32,
    font_path_utf8: *const u8,
    font_path_len: usize,
    underline: i32,
    strikethrough: i32,
) -> i32 {
    let align = if matches!(align, ALIGN_LEFT | ALIGN_CENTER | ALIGN_RIGHT | ALIGN_JUSTIFY) {
        align
    } else {
        ALIGN_LEFT
    };
    let style = TextStyle {
        align,
        fill: PackedRgba(fill_rgba),
        outline: PackedRgba(outline_rgba),
        outline_width_px: outline_width_px.max(0.0),
        underline: underline != 0,
        strikethrough: strikethrough != 0,
        // A box is placed upright; rotation is applied afterwards, like resizing,
        // via rotate_text_box_annotation.
        rotation_deg: 0.0,
    };

    // The font path is an OS path to a TrueType/OpenType file, empty for the
    // default. Kept owned for the whole call so the borrow threaded below stays
    // valid; a bad UTF-8 path is treated as "no font" rather than an error.
    let font_path: Option<String> = if font_path_utf8.is_null() || font_path_len == 0 {
        None
    } else {
        let slice = unsafe { std::slice::from_raw_parts(font_path_utf8, font_path_len) };
        std::str::from_utf8(slice).ok().map(|s| s.to_string())
    };

    add_text_box_common(
        doc_handle, page_index, capture_width, left, top, right, bottom, text_utf8, text_len,
        font_size_px, r, g, b, a, style, font_path.as_deref(),
    )
}

#[allow(clippy::too_many_arguments)]
fn add_text_box_common(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    text_utf8: *const u8,
    text_len: usize,
    font_size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
    style: TextStyle,
    font_path: Option<&str>,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || capture_width <= 0 || font_size_px <= 0.0 {
        return STATUS_INVALID_INPUT;
    }
    if right <= left || bottom <= top {
        return STATUS_INVALID_INPUT;
    }
    if text_utf8.is_null() || text_len == 0 {
        return STATUS_INVALID_INPUT;
    }

    let text = {
        let slice = unsafe { std::slice::from_raw_parts(text_utf8, text_len) };
        match std::str::from_utf8(slice) {
            Ok(t) => t.to_string(),
            Err(_) => return STATUS_INVALID_INPUT,
        }
    };

    panic::catch_unwind(|| {
        add_text_box_inner(
            doc_handle, page_index, capture_width, left, top, right, bottom, &text, font_size_px, r,
            g, b, a, style, font_path,
        )
    })
    .unwrap_or(STATUS_PANIC)
}

/// Rotates a freshly-built page object CLOCKWISE by `deg` degrees about the point
/// (`cx`, `cy`) in page points, applied BEFORE the object is added to its
/// annotation (a transform on an object already stored never reaches the stored
/// copy). Zero degrees is a no-op. Every object of a rotated text box gets this,
/// about the box's own centre, so the whole box turns as one, like a Word text
/// box. Expands where `pdfium_render::prelude::*` is in scope. `deg`/`cx`/`cy`
/// are read more than once, so pass plain values, not expressions with effects.
macro_rules! rotate_object_about {
    ($obj:expr, $deg:expr, $cx:expr, $cy:expr) => {
        if $deg != 0.0 {
            let _ = $obj.translate(PdfPoints::new(-$cx), PdfPoints::new(-$cy));
            // The stored angle is CLOCKWISE ON SCREEN, matching WinUI's
            // RotateTransform, so the overlay's frame and the drawn text agree.
            // pdfium-render's rotate_clockwise_degrees turns the object clockwise
            // in the rendered image too (the y-flip that happens between PDF user
            // space and the raster does NOT reverse the rotation direction: a
            // point that was to the right ends up ABOVE the origin either way,
            // which is what "clockwise" means to a viewer).
            let _ = $obj.rotate_clockwise_degrees($deg);
            let _ = $obj.translate(PdfPoints::new($cx), PdfPoints::new($cy));
        }
    };
}

#[allow(clippy::too_many_arguments)]
fn add_text_box_inner(
    doc_handle: u64,
    page_index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    text: &str,
    font_size_px: f32,
    r: u8,
    g: u8,
    b: u8,
    a: u8,
    style: TextStyle,
    font_path: Option<&str>,
) -> i32 {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };
    let mut doc_guard = lock(&doc);

    // The font token is a plain handle, so taking it here (a brief mutable
    // borrow) releases the document before the page borrows it below. A given
    // font path is embedded as a Unicode (CID) font; no path, or a font that
    // will not load, falls back to Helvetica.
    let font = resolve_text_font(&mut doc_guard, font_path);

    // The font's raw bytes, kept for shaping complex-script lines with rustybuzz.
    // Only present when a font was chosen (Helvetica cannot render those scripts
    // anyway, so there is nothing to shape without a real font file).
    let font_bytes = font_path.and_then(font_file_bytes);
    let font_slice: Option<&[u8]> = font_bytes.as_deref().map(|v| v.as_slice());

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return STATUS_INVALID_INPUT;
    };

    let page_w = page.width().value;
    if page_w <= 0.0 {
        return STATUS_INVALID_INPUT;
    }
    let scale = page_w / capture_width as f32;

    let (origin_x, origin_top) = page_origin(&page);

    let x0 = origin_x + left * scale;
    let x1 = origin_x + right * scale;
    let y_top = origin_top - top * scale;
    let y_bottom = origin_top - bottom * scale;

    let size_pts = font_size_px * scale;
    // A box that needs shaping (Burmese, Hindi, ...) gets the taller spacing so
    // its stacked marks do not run into the line below.
    let line_height_mult = if needs_shaping(text) {
        TEXTBOX_LINE_HEIGHT_COMPLEX
    } else {
        TEXTBOX_LINE_HEIGHT
    };
    let line_h = size_pts * line_height_mult;
    let pad = size_pts * TEXTBOX_PADDING;

    let color = PdfColor::new(r, g, b, a);

    // Wrap to the BOX WIDTH, measured with real Helvetica metrics, so the text
    // fills the width the user dragged rather than running off the right edge.
    // The usable width is the box minus its two insets.
    let avail_width = ((x1 - x0) - 2.0 * pad).max(0.0);
    let lines = wrap_to_width(&doc_guard, font, font_slice, text, avail_width, size_pts);

    // The box grows DOWN to fit the wrapped lines, but never shrinks below the
    // height that was dragged: a tall box the user drew stays tall, a short one
    // still can't clip its own text.
    let n = lines.len().max(1) as f32;
    let content_height = 2.0 * pad + n * line_h;
    let dragged_height = y_top - y_bottom;
    let box_height = content_height.max(dragged_height);
    let y_bottom = y_top - box_height;

    // The box turns about its own centre. Every object is laid out upright first,
    // in the box's own frame, then rotated about this point before being added.
    let rot = style.rotation_deg;
    let cx = (x0 + x1) / 2.0;
    let cy = (y_bottom + y_top) / 2.0;

    // The annotation's rect is its appearance's clip, so a rotated box needs a
    // rect big enough for the TURNED box or PDFium would crop the corners. That
    // is the axis-aligned bounding box of the rotated rectangle, centred on the
    // same point. Upright, it is exactly the box.
    let bounds = if rot == 0.0 {
        PdfRect::new(
            PdfPoints::new(y_bottom),
            PdfPoints::new(x0),
            PdfPoints::new(y_top),
            PdfPoints::new(x1),
        )
    } else {
        let (s, c) = rot.to_radians().sin_cos();
        let (s, c) = (s.abs(), c.abs());
        let w = x1 - x0;
        let h = y_top - y_bottom;
        let half_w = (w * c + h * s) / 2.0;
        let half_h = (w * s + h * c) / 2.0;
        PdfRect::new(
            PdfPoints::new(cy - half_h),
            PdfPoints::new(cx - half_w),
            PdfPoints::new(cy + half_h),
            PdfPoints::new(cx + half_w),
        )
    };

    let Ok(mut annotation) = page.annotations_mut().create_stamp_annotation() else {
        return STATUS_INVALID_INPUT;
    };

    // Bounds BEFORE objects, as everywhere: PDFium builds the appearance form
    // from the rect.
    if annotation.set_bounds(bounds).is_err() {
        return STATUS_INVALID_INPUT;
    }

    // Fill FIRST, so it sits behind the text. A stroked outline is added too if
    // asked; both are inset by half the outline width so the border sits on the
    // box edge rather than half outside it.
    let ow = style.outline_width_px * scale;
    let inset = if style.outline.is_visible() { ow / 2.0 } else { 0.0 };
    if style.fill.is_visible() || style.outline.is_visible() {
        let rect = PdfRect::new(
            PdfPoints::new(y_bottom + inset),
            PdfPoints::new(x0 + inset),
            PdfPoints::new(y_top - inset),
            PdfPoints::new(x1 - inset),
        );
        let fill = style.fill.is_visible().then(|| {
            PdfColor::new(style.fill.r(), style.fill.g(), style.fill.b(), style.fill.a())
        });
        let stroke = style.outline.is_visible().then(|| {
            PdfColor::new(style.outline.r(), style.outline.g(), style.outline.b(), style.outline.a())
        });
        if let Ok(mut rect_obj) = PdfPagePathObject::new_rect(
            &doc_guard,
            rect,
            stroke,
            style.outline.is_visible().then(|| PdfPoints::new(ow.max(0.1))),
            fill,
        ) {
            rotate_object_about!(rect_obj, rot, cx, cy);
            let _ = annotation.objects_mut().add_path_object(rect_obj);
        }
    }

    // One text object per WRAPPED line, positioned per alignment. Positioned
    // BEFORE being added, because an object fetched back from an annotation is
    // detached and a transform on it never reaches the stored copy.
    //
    // A text object's origin is its baseline, so the first line sits one full
    // size below the top inset, and each line below is a line-height lower. An
    // empty line (a blank line the user typed) still advances the baseline.
    let text_left = x0 + pad;
    for (i, line) in lines.iter().enumerate() {
        if line.text.is_empty() {
            continue;
        }

        let baseline = y_top - pad - size_pts - (i as f32) * line_h;

        // Justify spreads the words of a full line across the whole width; every
        // other case places the whole line at one x computed from its measured
        // width.
        // A complex-script line (with a real font loaded) is SHAPED: rustybuzz
        // turns the codepoints into ordered glyph indices, which go to PDFium as
        // one text object via the forked SetCharcodes path. This is what makes
        // Burmese/Devanagari/Arabic render correctly rather than as loose,
        // wrongly-ordered base glyphs. Everything else uses the plain path.
        let shaped = font_slice
            .filter(|_| needs_shaping(&line.text))
            .and_then(|fb| shape_run(fb, &line.text, size_pts));

        // Place the line's text, and note the x and width its decoration (if
        // any) should span: the full width for a justified line, the measured
        // width otherwise.
        let (deco_x, deco_w) = if let Some(glyphs) = shaped {
            // Draw each glyph at its OWN shaped position: one text object per
            // glyph, placed by the advances and offsets rustybuzz computed. A
            // single object using PDFium's default advances leaves gaps and
            // mis-stacks marks; per-glyph placement reproduces the shaping.
            let width_pts = shaped_width(&glyphs);
            let x = match style.align {
                ALIGN_CENTER => text_left + (avail_width - width_pts) / 2.0,
                ALIGN_RIGHT => text_left + (avail_width - width_pts),
                _ => text_left,
            };
            let mut pen_x = x;
            for g in &glyphs {
                if let Ok(mut obj) = PdfPageTextObject::new(&doc_guard, " ", font, PdfPoints::new(size_pts)) {
                    let charcode = [g.id as std::os::raw::c_uint];
                    doc_guard.bindings().FPDFText_SetCharcodes(obj.object_handle(), charcode.as_ptr(), 1);
                    let _ = obj.set_fill_color(color);
                    let _ = obj.translate(
                        PdfPoints::new(pen_x + g.x_offset),
                        PdfPoints::new(baseline + g.y_offset),
                    );
                    rotate_object_about!(obj, rot, cx, cy);
                    let _ = annotation.objects_mut().add_text_object(obj);
                }
                pen_x += g.x_advance;
            }
            (x, width_pts)
        } else if style.align == ALIGN_JUSTIFY && line.justifiable && line.text.contains(' ') {
            justify_line(
                &doc_guard, &mut annotation, font, &line.text, color, size_pts,
                text_left, avail_width, baseline, rot, cx, cy,
            );
            (text_left, avail_width)
        } else {
            let line_w = measure_text_width(&doc_guard, font, &line.text, size_pts);
            let x = match style.align {
                ALIGN_CENTER => text_left + (avail_width - line_w) / 2.0,
                ALIGN_RIGHT => text_left + (avail_width - line_w),
                _ => text_left, // left, and the last line of a justified paragraph
            };

            let Ok(mut obj) = PdfPageTextObject::new(&doc_guard, &line.text, font, PdfPoints::new(size_pts))
            else {
                return STATUS_INVALID_INPUT;
            };
            let _ = obj.set_fill_color(color);
            if obj.translate(PdfPoints::new(x), PdfPoints::new(baseline)).is_err() {
                return STATUS_INVALID_INPUT;
            }
            rotate_object_about!(obj, rot, cx, cy);
            if annotation.objects_mut().add_text_object(obj).is_err() {
                return STATUS_INVALID_INPUT;
            }
            (x, line_w)
        };

        // Underline sits just below the baseline; strikethrough runs through
        // the middle of the x-height. Both are thin filled rules in the text
        // colour, drawn per line so they follow wrapping and alignment.
        let rule_thickness = (size_pts * 0.06).max(0.4);
        if style.underline {
            add_text_rule(&doc_guard, &mut annotation, deco_x, baseline - size_pts * 0.13,
                          deco_w, rule_thickness, color, rot, cx, cy);
        }
        if style.strikethrough {
            add_text_rule(&doc_guard, &mut annotation, deco_x, baseline + size_pts * 0.28,
                          deco_w, rule_thickness, color, rot, cx, cy);
        }
    }

    // The ORIGINAL text is stored, with the style and the box's own upright rect
    // (normalized the same way get_annotations reports bounds), so re-editing gets
    // clean paragraphs and the overlay can recover the tight box even after it is
    // rotated and its annotation rect has grown to the bounding box.
    let box_rect = [
        (x0 - origin_x) / page_w,
        (origin_top - y_top) / page_w,
        (x1 - origin_x) / page_w,
        (origin_top - y_bottom) / page_w,
    ];
    let _ = set_annotation_tag(&mut annotation, &textbox_tag_styled(
        text, font_size_px, r, g, b, a, style, font_path, box_rect));

    drop(annotation);
    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
}

/// Draws a thin horizontal filled rule (underline or strikethrough) centred on
/// `y_center`, spanning `x..x+width`, in the text colour, then rotates it with
/// the rest of the box about (`cx`, `cy`) by `rot` degrees. A zero or negative
/// width is a no-op (an empty or unmeasurable line).
#[allow(clippy::too_many_arguments)]
fn add_text_rule<'a>(
    doc: &pdfium_render::prelude::PdfDocument<'a>,
    annotation: &mut pdfium_render::prelude::PdfPageStampAnnotation<'a>,
    x: f32,
    y_center: f32,
    width: f32,
    thickness: f32,
    color: pdfium_render::prelude::PdfColor,
    rot: f32,
    cx: f32,
    cy: f32,
) {
    use pdfium_render::prelude::*;
    if width <= 0.0 {
        return;
    }
    let rect = PdfRect::new(
        PdfPoints::new(y_center - thickness / 2.0),
        PdfPoints::new(x),
        PdfPoints::new(y_center + thickness / 2.0),
        PdfPoints::new(x + width),
    );
    if let Ok(mut obj) = PdfPagePathObject::new_rect(doc, rect, None, None, Some(color)) {
        rotate_object_about!(obj, rot, cx, cy);
        let _ = annotation.objects_mut().add_path_object(obj);
    }
}

/// Lays one line out justified: each word its own text object, the gaps between
/// them stretched evenly so the line fills the full width.
#[allow(clippy::too_many_arguments)]
fn justify_line<'a>(
    doc: &pdfium_render::prelude::PdfDocument<'a>,
    annotation: &mut pdfium_render::prelude::PdfPageStampAnnotation<'a>,
    font: pdfium_render::prelude::PdfFontToken,
    line: &str,
    color: pdfium_render::prelude::PdfColor,
    size_pts: f32,
    left: f32,
    avail_width: f32,
    baseline: f32,
    rot: f32,
    cx: f32,
    cy: f32,
) {
    use pdfium_render::prelude::*;

    let words: Vec<&str> = line.split(' ').filter(|w| !w.is_empty()).collect();
    if words.is_empty() {
        return;
    }

    let widths: Vec<f32> = words.iter().map(|w| measure_text_width(doc, font, w, size_pts)).collect();
    let total_words: f32 = widths.iter().sum();
    let gaps = words.len().saturating_sub(1);

    // Even gap between words so they span exactly the available width. With one
    // word there is no gap and it just sits at the left.
    let gap = if gaps > 0 { (avail_width - total_words) / gaps as f32 } else { 0.0 };

    let mut x = left;
    for (i, word) in words.iter().enumerate() {
        if let Ok(mut obj) = PdfPageTextObject::new(doc, word, font, PdfPoints::new(size_pts)) {
            let _ = obj.set_fill_color(color);
            if obj.translate(PdfPoints::new(x), PdfPoints::new(baseline)).is_ok() {
                rotate_object_about!(obj, rot, cx, cy);
                let _ = annotation.objects_mut().add_text_object(obj);
            }
        }
        x += widths[i] + gap;
    }
}


/// Shape kinds. These numbers cross the FFI boundary and are mirrored by
/// `ShapeKind` in the C# viewport library, so they may be appended to but never
/// reordered.
pub const SHAPE_RECTANGLE: i32 = 0;
pub const SHAPE_ELLIPSE: i32 = 1;
pub const SHAPE_LINE: i32 = 2;
pub const SHAPE_ARROW: i32 = 3;
pub const SHAPE_ROUNDED_RECT: i32 = 4;

/// Every kind the tag parser and the writer accept. One list rather than three
/// copies of the same `matches!`, because a kind added to only some of them is
/// a shape that can be drawn and then fails to reload.
macro_rules! known_shape_kinds {
    () => {
        SHAPE_RECTANGLE | SHAPE_ELLIPSE | SHAPE_LINE | SHAPE_ARROW | SHAPE_ROUNDED_RECT
    };
}

/// Default corner radius of a rounded rectangle, as a fraction of its SHORTER
/// side. Proportional rather than absolute so a small badge and a full-page box
/// look like the same shape family, and so a resize keeps looking right.
pub const ROUNDED_RECT_DEFAULT_RADIUS: f32 = 0.18;

/// Circular-arc approximation constant for a cubic Bezier: the control points
/// sit this fraction of the radius along the tangents. The standard 4-arc
/// circle approximation, accurate to about one part in a thousand.
const KAPPA: f32 = 0.552_284_75;

/// The corner radius actually drawn, in the same units as the rectangle.
///
/// Clamped to half the shorter side: past that the two corners on a side meet
/// and any larger value would make the arcs overlap and cross, which draws as a
/// bow-tie rather than a stadium. Clamping at DRAW time rather than at entry
/// means a stored radius stays intact when a shape is squeezed small and then
/// grown again.
fn clamped_corner_radius(radius: f32, width: f32, height: f32) -> f32 {
    let limit = width.abs().min(height.abs()) / 2.0;
    if !radius.is_finite() || radius <= 0.0 {
        return 0.0;
    }
    radius.min(limit).max(0.0)
}

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
/// A stamp's angle and the UPRIGHT rectangle it was placed in, in capture
/// space: `AyaanStamp:<deg>:<l>:<t>:<r>:<b>`.
///
/// Stamps carried no tag at all before rotation existed, because nothing
/// needed recovering from one: the image lives in the annotation. Rotation
/// changes that. The angle has to survive a save so a reopened stamp does not
/// report itself upright while looking turned, and the upright rect has to be
/// kept so a second rotation is measured from the original box instead of
/// compounding on the enlarged one.
const STAMP_TAG: &str = "AyaanStamp:";

fn stamp_tag(rotation_deg: f32, left: f32, top: f32, right: f32, bottom: f32) -> String {
    format!("{STAMP_TAG}{rotation_deg:.2}:{left:.4}:{top:.4}:{right:.4}:{bottom:.4}")
}

/// The angle and upright rect recorded on one of our stamps, or None if the
/// annotation is not a tagged stamp (including any stamp written before the
/// tag existed, which is treated as upright).
fn parse_stamp_tag(contents: &str) -> Option<(f32, f32, f32, f32, f32)> {
    let contents = strip_id_prefix(contents).1;
    let rest = contents.strip_prefix(STAMP_TAG)?;
    let parts: Vec<&str> = rest.split(':').collect();
    if parts.len() < 5 {
        return None;
    }
    Some((
        parts[0].parse().ok()?,
        parts[1].parse().ok()?,
        parts[2].parse().ok()?,
        parts[3].parse().ok()?,
        parts[4].parse().ok()?,
    ))
}

const SHAPE_TAG: &str = "AyaanShape:";

fn shape_tag(
    spec: &ShapeSpec,
    width_pts: f32,
    radius_pts: f32,
    box_w_pts: f32,
    box_h_pts: f32,
    shadow_distance_pts: f32,
    shadow_softness_pts: f32,
    shadow_spread_pts: f32,
) -> String {
    let fx = u8::from(spec.x2 >= spec.x1);
    let fy = u8::from(spec.y2 >= spec.y1);

    // A SHADOW IS THE NEW LONGEST RUNG, above the box, and it is emitted only
    // by a shape that has one. Every shape that does not is byte-identical to
    // what the previous build wrote, which is what keeps existing files and
    // their diffs unchanged.
    //
    // Fields are positional, so a shadow drags everything before it along even
    // when those are zero. That is the same bargain the box made and the reason
    // the rungs are ordered by cost rather than by when they were added.
    if spec.shadow_rgba != 0 {
        // ONE SELF-DESCRIBING FIELD, not five positional ones. Positional
        // fields do not extend: a glow would append more of them and force
        // every shadowed shape to emit intermediate zeros to reach them. Named
        // keys inside one token mean a later effect is `+g(...)` and an unknown
        // key is skipped rather than fatal.
        //
        // Re-cut deliberately while it was still free. There is no UI that can
        // set a shadow, so no file in existence carries one, and the moment one
        // does this format is permanent.
        return format!(
            "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}:{:.2}:{:08X}:{:.4}:{:.4}:{:.4}:s(a={:.2},d={:.4},b={:.4},p={:.4},c={:08X})",
            spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts,
            spec.rotation_deg, spec.fill_rgba, radius_pts, box_w_pts, box_h_pts,
            spec.shadow_angle_deg, shadow_distance_pts, shadow_softness_pts,
            shadow_spread_pts, spec.shadow_rgba
        );
    }
    // Rotation and fill are BOTH appended so an older reader that stops after
    // fy still gets kind/colour/width/direction. Emit progressively longer
    // tags: bare when neither is set, +rot when only rotated, +rot+fill when
    // filled (rot forced to 0.00 in that case so parse order stays positional).
    // Keeps unrotated stroke-only shapes byte-identical to older tags, so a
    // shape file diffed against the old build shows no change unless the
    // shape actually uses one of these new features.
    if radius_pts > 0.0 || spec.rotation_deg != 0.0 {
        // The longest rung. Radius is only paid for by a shape that has one,
        // and the upright box only by a shape that is TURNED.
        //
        // The box matters because a rotated shape's /Rect is the axis-aligned
        // box of the turned content, which is bigger than the shape in both
        // axes and cannot be inverted at 45 degrees (infinitely many boxes
        // share one AABB there). Recording the upright size means no rebuild
        // ever has to work it out: the centre comes from /Rect, which IS exact,
        // and the size comes from here. Text boxes already store their own box
        // for the same reason.
        //
        // Fields are positional, so everything before it is emitted too, zero
        // or not; skipping one would shift the rest into the wrong slots.
        format!(
            "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}:{:.2}:{:08X}:{:.4}:{:.4}:{:.4}",
            spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts,
            spec.rotation_deg, spec.fill_rgba, radius_pts, box_w_pts, box_h_pts
        )
    } else if spec.rotation_deg == 0.0 && spec.fill_rgba == 0 {
        format!(
            "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}",
            spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts
        )
    } else if spec.fill_rgba == 0 {
        format!(
            "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}:{:.2}",
            spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts, spec.rotation_deg
        )
    } else {
        format!(
            "{SHAPE_TAG}{}:{:02X}{:02X}{:02X}{:02X}:{:.4}:{fx}:{fy}:{:.2}:{:08X}",
            spec.kind, spec.r, spec.g, spec.b, spec.a, width_pts, spec.rotation_deg, spec.fill_rgba
        )
    }
}

/// The kind, colour, width, drag direction, rotation, and fill colour recorded
/// on one of our shape annotations, or None if it is not one of ours. Rotation
/// is 0 and fill is 0 on older tags that predate those fields; the caller does
/// not need to know which form the tag was in.
/// A shadow as a CALLER supplies it, in capture-space pixels.
///
/// Distinct from [`TagShadow`], which is in POINTS, because the two are read
/// from different places and mixing them silently scales a shadow by the page
/// width. The same split `radius_override` already makes: a value off the tag
/// needs converting, a value from the caller does not.
///
/// `rgba` of zero CLEARS the shadow, exactly as it does for the fill. There is
/// no separate way to say "no shadow", because the colour has always been what
/// decides whether there is one.
#[derive(Clone, Copy, Debug, PartialEq)]
struct ShadowOverride {
    angle_deg: f32,
    distance_px: f32,
    softness_px: f32,
    spread_px: f32,
    rgba: u32,
}

/// A shadow as the tag records it: in POINTS, angle-and-distance rather than an
/// x/y offset, and with the two RESERVED lengths that are stored but not drawn.
///
/// `rgba` of zero is impossible here, because a shadow with no colour is not a
/// shadow and `parse_shadow_field` returns None for it.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
struct TagShadow {
    angle_deg: f32,
    distance_pts: f32,
    /// RESERVED, never rendered. See `ShapeSpec::shadow_softness_px`.
    softness_pts: f32,
    /// RESERVED, never rendered. See `ShapeSpec::shadow_spread_px`.
    spread_pts: f32,
    rgba: u32,
}

fn parse_shape_tag(
    contents: &str,
) -> Option<(i32, u8, u8, u8, u8, f32, bool, bool, f32, u32, f32, f32, f32, Option<TagShadow>)> {
    let contents = strip_id_prefix(contents).1;
    let rest = contents.strip_prefix(SHAPE_TAG)?;
    let mut parts = rest.split(':');

    let kind: i32 = parts.next()?.parse().ok()?;
    if !matches!(kind, known_shape_kinds!()) {
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

    // Rotation, if present. An unparseable value is treated as 0 so a bad tag
    // does not turn a valid shape into an unknown mark; the shape just renders
    // unrotated.
    let rot: f32 = parts.next().and_then(|s| s.parse().ok()).unwrap_or(0.0);

    // Fill colour, if present. Same permissive parse: an unparseable value is
    // treated as 0 (stroke-only), so a corrupt trailing byte never invalidates
    // an otherwise good shape tag.
    let fill: u32 = parts
        .next()
        .and_then(|s| u32::from_str_radix(s, 16).ok())
        .unwrap_or(0);

    // Corner radius in POINTS, if present. Absent on every tag written before
    // rounded rectangles existed, and on every kind that has no corners, which
    // is why it reads as 0 rather than failing.
    let radius: f32 = parts
        .next()
        .and_then(|s| s.parse::<f32>().ok())
        .filter(|v| v.is_finite() && *v >= 0.0)
        .unwrap_or(0.0);

    // The shape's own UPRIGHT box, in points, present only on a rotated or
    // rounded shape. Zero means "not recorded", and the caller falls back to
    // treating /Rect as the extent, which is correct for an upright shape.
    let box_w: f32 = parts.next().and_then(|s| s.parse().ok()).filter(|v: &f32| v.is_finite() && *v > 0.0).unwrap_or(0.0);
    let box_h: f32 = parts.next().and_then(|s| s.parse().ok()).filter(|v: &f32| v.is_finite() && *v > 0.0).unwrap_or(0.0);

    // The drop shadow, as ONE self-describing field. Absent from every tag
    // written before effects existed and from every shape that has none, so its
    // absence reads as no shadow rather than failing the parse.
    let shadow = parts.next().and_then(parse_shadow_field);

    Some((
        kind, byte(0)?, byte(2)?, byte(4)?, byte(6)?, width, fx, fy, rot, fill, radius, box_w,
        box_h, shadow,
    ))
}

/// One shadow, as the tag spells it: `s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000)`.
///
/// Keys rather than positions, so an effect added later is another token and an
/// key this build does not know is skipped instead of shifting everything after
/// it. Anything malformed yields no shadow, which is the same answer as a shape
/// that never had one, and never a half-read one.
fn parse_shadow_field(field: &str) -> Option<TagShadow> {
    let inner = field.strip_prefix("s(")?.strip_suffix(')')?;

    let mut shadow = TagShadow::default();
    let mut seen_colour = false;

    for pair in inner.split(',') {
        let (key, value) = pair.split_once('=')?;
        match key {
            "a" => shadow.angle_deg = value.parse().ok().filter(|v: &f32| v.is_finite())?,
            "d" => shadow.distance_pts = non_negative(value)?,
            "b" => shadow.softness_pts = non_negative(value)?,
            "p" => shadow.spread_pts = non_negative(value)?,
            "c" => {
                shadow.rgba = u32::from_str_radix(value, 16).ok()?;
                seen_colour = true;
            }
            // Forward compatibility: a key from a later build is not an error.
            _ => {}
        }
    }

    // The colour is what says a shadow exists, the same bargain the fill makes.
    // A fully transparent one paints nothing, so it is not one.
    if !seen_colour || shadow.rgba == 0 {
        return None;
    }
    Some(shadow)
}

fn non_negative(value: &str) -> Option<f32> {
    value.parse::<f32>().ok().filter(|v| v.is_finite() && *v >= 0.0)
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
    /// Clockwise rotation of the shape about its own centre, in degrees on
    /// screen. Zero for an unrotated shape. APPENDED to the struct: the C ABI
    /// stays stable for callers that don't set this (they'll pass a zero-init
    /// struct so rotation is 0), and the render code treats 0 as a no-op.
    pub rotation_deg: f32,
    /// Fill colour for rectangles and ellipses as 0xAARRGGBB. Zero means
    /// STROKE-ONLY (the historic default: shapes were originally outlines so
    /// they wouldn't hide the page under them). Non-zero fills rectangles and
    /// ellipses with this colour BEHIND their stroke; lines and arrows ignore
    /// it. APPENDED to the struct: existing callers that zero-init the whole
    /// thing get stroke-only shapes, same as before this field was added.
    pub fill_rgba: u32,
    /// Corner radius for `SHAPE_ROUNDED_RECT`, in capture-space pixels (the
    /// same space as `width_px`). Ignored by every other kind. Zero draws
    /// square corners, so a rounded rect with no radius degrades to a plain
    /// rectangle rather than to nothing. APPENDED to the struct for the same
    /// additive-ABI reason as the two fields above.
    pub corner_radius_px: f32,
    /// The drop shadow. ANGLE AND DISTANCE ARE THE SOURCE OF TRUTH, and the
    /// x/y offset is derived from them wherever it is needed.
    ///
    /// Not the other way round: an angle cannot be recovered from an offset of
    /// zero length, so storing x/y would lose the direction the moment somebody
    /// dragged the distance to nothing. The angle is where the LIGHT is, in
    /// degrees counter-clockwise from due east, so 90 is lit from directly
    /// above and the shadow falls straight down. Distance is in capture-space
    /// pixels, the same space as `width_px`, and the colour is 0xAARRGGBB.
    ///
    /// OPACITY IS THE COLOUR'S ALPHA. There is no separate opacity, because two
    /// ways to say how solid a shadow is would need a rule about which wins.
    ///
    /// `shadow_rgba == 0` means NO SHADOW, which is the historic behaviour and
    /// what a zero-initialised struct gets, so the offsets are only read when
    /// the colour says there is something to draw. Same convention as
    /// `fill_rgba` above, for the same reason: one field decides whether the
    /// feature is on, so a caller that knows nothing about it cannot switch it
    /// on by accident.
    ///
    /// APPENDED to the struct for the additive-ABI reason as everything above.
    pub shadow_angle_deg: f32,
    pub shadow_distance_px: f32,
    /// RESERVED. Stored, round-tripped through the tag, and DELIBERATELY NOT
    /// RENDERED. A soft shadow cannot be drawn as a path object because PDF has
    /// no blur primitive for one; it has to be rasterised, which is its own
    /// piece of work. Carrying the value now means a file written today keeps
    /// its softness when that lands, instead of silently losing it.
    pub shadow_softness_px: f32,
    /// RESERVED, exactly as `shadow_softness_px` is: stored and round-tripped,
    /// never drawn. Spread needs path dilation, which neither PDFium nor lopdf
    /// offers, so it goes the same rasterised route.
    pub shadow_spread_px: f32,
    pub shadow_rgba: u32,
}

/// Where a shadow falls, in PDF points, given where the light is.
///
/// PDF SPACE, so y runs UP: a shadow cast downwards on the screen has a
/// NEGATIVE y here. The angle names the light's direction, counter-clockwise
/// from due east, and the shadow falls the opposite way, which is why both
/// terms are negated.
///
/// Mirrored by DropShadow.OffsetX/OffsetY in PdfEditorApp.Viewport, which works
/// in screen space and so negates only x. Both are checked against the same
/// table of angles, so a change to one that is not made to the other fails on
/// both sides of the FFI.
fn shadow_offset_pts(angle_deg: f32, distance_pts: f32) -> (f32, f32) {
    let rad = angle_deg.to_radians();
    (-distance_pts * rad.cos(), -distance_pts * rad.sin())
}

/// Half-width of an arrowhead as a fraction of its length, giving the roughly
/// 5:2 taper a drawn arrow is expected to have. Mirrors ArrowHeadTaper in the
/// C# viewport library.
const ARROW_HEAD_TAPER: f32 = 0.42;

/// An arrow, split into the parts drawn differently: a stroked shaft and a
/// FILLED triangular head.
///
/// The head was two thin barbs drawn as part of the shaft's own polyline, and
/// at any real stroke width that reads as a tick mark rather than an arrow.
/// Every drawing tool fills the head.
///
/// The shaft stops at the BASE of the head rather than running on to the tip: a
/// stroked line continuing under a filled triangle pokes out past the point and
/// blunts it.
///
/// Returns (shaft_end, tip, left, right).
fn arrow_parts(tail_x: f32, tail_y: f32, tip_x: f32, tip_y: f32, width: f32)
    -> ((f32, f32), (f32, f32), (f32, f32), (f32, f32))
{
    let dx = tip_x - tail_x;
    let dy = tip_y - tail_y;
    let len = (dx * dx + dy * dy).sqrt();

    // A zero-length shaft has no direction to point in. Pick one rather than
    // dividing by zero and writing NaN coordinates into the file.
    let (ux, uy) = if len < 1e-6 { (1.0, 0.0) } else { (dx / len, dy / len) };

    let mut head = (width * ARROW_HEAD_SCALE).max(ARROW_HEAD_MIN);

    // Never let the head eat the whole arrow. On a short drag an unclamped head
    // is longer than the shaft, and the result is a floating triangle pointing
    // backwards.
    head = head.min(len * 0.6);

    let half = head * ARROW_HEAD_TAPER;

    let bx = tip_x - ux * head;
    let by = tip_y - uy * head;
    let px = -uy * half;
    let py = ux * half;

    ((bx, by), (tip_x, tip_y), (bx + px, by + py), (bx - px, by - py))
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
        if !matches!(spec.kind, known_shape_kinds!()) {
            return STATUS_INVALID_INPUT;
        }

        let Ok(mut page) = doc_guard.pages().get(spec.page_index as u16) else {
            continue;
        };

        let page_w = page.width().value;
        if page_w <= 0.0 {
            continue;
        }

        let (origin_x, origin_top) = page_origin(&page);
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

        // Fill is optional. Historically shapes were stroke-only so they'd
        // never hide the underlying page; a non-zero fill_rgba opts a closed
        // shape into a solid fill (with alpha, so a light fill still shows the
        // page through it).
        let fill = if spec.fill_rgba != 0 {
            let r = ((spec.fill_rgba >> 16) & 0xFF) as u8;
            let g = ((spec.fill_rgba >> 8) & 0xFF) as u8;
            let b = (spec.fill_rgba & 0xFF) as u8;
            let a = ((spec.fill_rgba >> 24) & 0xFF) as u8;
            Some(PdfColor::new(r, g, b, a))
        } else {
            None
        };

        // THE SHADOW IS THE SAME GEOMETRY, and this closure is what makes that
        // true rather than merely intended. Both the shape and its shadow are
        // built from these lines; a second copy for the shadow would be a
        // second geometry system, and the two would drift the first time a
        // corner radius or an arrow taper changed.
        //
        // The offsets shadow the outer coordinates, so the body below is
        // exactly the code that was here before and reads as if there were no
        // such thing as an effect.
        let build_path = |ox: f32, oy: f32, color: PdfColor, fill: Option<PdfColor>| {
        let (x1, y1, x2, y2) = (x1 + ox, y1 + oy, x2 + ox, y2 + oy);
        match spec.kind {
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
                build(&doc_guard, rect, Some(color), Some(PdfPoints::new(width_pts)), fill)
            }
            SHAPE_ROUNDED_RECT => {
                // Built by hand rather than with a rect helper, because PDFium
                // has no rounded-rect primitive: it is four straight sides with
                // a quarter-circle Bezier at each corner.
                let (left, right) = (x1.min(x2), x1.max(x2));
                let (bottom, top) = (y1.min(y2), y1.max(y2));
                let radius = clamped_corner_radius(
                    spec.corner_radius_px * scale, right - left, top - bottom);

                if radius <= 0.0 {
                    // Degenerate to a square-cornered rectangle rather than
                    // drawing a collapsed path. A rounded rect dragged out to a
                    // sliver is still a rectangle, not an absence.
                    let rect = PdfRect::new(
                        PdfPoints::new(bottom), PdfPoints::new(left),
                        PdfPoints::new(top), PdfPoints::new(right),
                    );
                    PdfPagePathObject::new_rect(
                        &doc_guard, rect, Some(color), Some(PdfPoints::new(width_pts)), fill)
                } else {
                    let k = radius * KAPPA;
                    let p = PdfPoints::new;
                    // Anticlockwise from the bottom edge, in PDF space where y
                    // runs UP. Each corner's control points sit KAPPA along the
                    // two tangents meeting there.
                    PdfPagePathObject::new(
                        &doc_guard, p(left + radius), p(bottom),
                        Some(color), Some(PdfPoints::new(width_pts)), fill,
                    )
                    .and_then(|mut path| {
                        path.line_to(p(right - radius), p(bottom))?;
                        path.bezier_to(p(right), p(bottom + radius),
                                       p(right - radius + k), p(bottom),
                                       p(right), p(bottom + radius - k))?;
                        path.line_to(p(right), p(top - radius))?;
                        path.bezier_to(p(right - radius), p(top),
                                       p(right), p(top - radius + k),
                                       p(right - radius + k), p(top))?;
                        path.line_to(p(left + radius), p(top))?;
                        path.bezier_to(p(left), p(top - radius),
                                       p(left + radius - k), p(top),
                                       p(left), p(top - radius + k))?;
                        path.line_to(p(left), p(bottom + radius))?;
                        path.bezier_to(p(left + radius), p(bottom),
                                       p(left), p(bottom + radius - k),
                                       p(left + radius - k), p(bottom))?;
                        path.close_path()?;
                        Ok(path)
                    })
                }
            }
            _ => {
                // For an arrow the shaft stops at the head's BASE; the head is
                // a separate filled triangle, built below.
                let (sx, sy) = if spec.kind == SHAPE_ARROW {
                    arrow_parts(x1, y1, x2, y2, width_pts).0
                } else {
                    (x2, y2)
                };

                PdfPagePathObject::new_line(
                    &doc_guard,
                    PdfPoints::new(x1),
                    PdfPoints::new(y1),
                    PdfPoints::new(sx),
                    PdfPoints::new(sy),
                    color,
                    PdfPoints::new(width_pts),
                )
            }
        }
        };

        let Ok(mut path) = build_path(0.0, 0.0, color, fill) else {
            continue;
        };

        // The arrowhead, as a SOLID triangle. Built before the bounds are
        // measured so its points are inside them: PDFium clips an appearance to
        // its box, and a head outside would be cut off silently, leaving what
        // looks like a plain line.
        let build_head = |ox: f32, oy: f32, color: PdfColor| {
            let (x1, y1, x2, y2) = (x1 + ox, y1 + oy, x2 + ox, y2 + oy);
            let (_, tip, left, right) = arrow_parts(x1, y1, x2, y2, width_pts);

            let built = PdfPagePathObject::new_line(
                &doc_guard,
                PdfPoints::new(tip.0),
                PdfPoints::new(tip.1),
                PdfPoints::new(left.0),
                PdfPoints::new(left.1),
                color,
                PdfPoints::new(width_pts),
            )
            .and_then(|mut t| {
                t.line_to(PdfPoints::new(right.0), PdfPoints::new(right.1))?;
                t.close_path()?;
                t.set_fill_color(color)?;
                // Filled AND stroked, so the taper does not come out ragged at
                // small sizes.
                t.set_fill_and_stroke_mode(PdfPathFillMode::Winding, true)?;
                Ok(t)
            });

            (built, [tip, left, right])
        };

        let head = if spec.kind == SHAPE_ARROW {
            let (built, points) = build_head(0.0, 0.0, color);
            extent.extend_from_slice(&points);

            match built {
                Ok(t) => Some(t),
                Err(_) => continue,
            }
        } else {
            None
        };

        // The shadow's offset in PDF points. Y is NEGATED because the page's
        // vertical axis runs the other way to the screen's: to_pdf_y subtracts,
        // so a shadow cast downwards on screen is a smaller y here. Getting
        // this sign wrong puts every shadow on the wrong side of its shape,
        // which is why there is a test that only passes for one of them.
        // DERIVED, never stored: angle and distance are what the shape carries.
        // Softness and spread are deliberately NOT consulted, because neither
        // can be drawn as a path object; see ShapeSpec.
        let (sdx, sdy) =
            shadow_offset_pts(spec.shadow_angle_deg, spec.shadow_distance_px * scale);
        let has_shadow = spec.shadow_rgba != 0;

        let pad = width_pts / 2.0 + 1.0;
        let min_x = extent.iter().map(|p| p.0).fold(f32::MAX, f32::min) - pad;
        let max_x = extent.iter().map(|p| p.0).fold(f32::MIN, f32::max) + pad;
        let min_y = extent.iter().map(|p| p.1).fold(f32::MAX, f32::min) - pad;
        let max_y = extent.iter().map(|p| p.1).fold(f32::MIN, f32::max) + pad;

        // The shape turns about its own centre. Every rendered object gets the
        // same rotate-about-centre transform BEFORE it is added, and when the
        // shape is turned the annotation's /Rect grows to the axis-aligned
        // bounding box of the rotated content, so PDFium does not crop the
        // turned corners (the same story as text boxes).
        let rot = spec.rotation_deg;
        let cx = (min_x + max_x) / 2.0;
        let cy = (min_y + max_y) / 2.0;

        // PDFium CLIPS an appearance to its box, so a shadow outside the box is
        // silently cut off. The box grows by the offset, in the direction of
        // the offset only.
        //
        // Exact rather than approximate, and it does not need the rotation
        // maths repeating: the shadow is the shape TRANSLATED, and both turn by
        // the same angle about their own centres, so the shadow's final
        // geometry is the shape's final geometry translated by the same vector.
        // Growing the finished box by that vector therefore contains it at any
        // angle.
        let grow = |lo: f32, hi: f32, d: f32| {
            if !has_shadow { (lo, hi) } else { (lo.min(lo + d), hi.max(hi + d)) }
        };

        let bounds = if rot == 0.0 {
            let (bl, br) = grow(min_x, max_x, sdx);
            let (bb, bt) = grow(min_y, max_y, sdy);
            PdfRect::new(
                PdfPoints::new(bb),
                PdfPoints::new(bl),
                PdfPoints::new(bt),
                PdfPoints::new(br),
            )
        } else {
            let (s, c) = rot.to_radians().sin_cos();
            let (s, c) = (s.abs(), c.abs());
            let w = max_x - min_x;
            let h = max_y - min_y;
            let hw = (w * c + h * s) / 2.0;
            let hh = (w * s + h * c) / 2.0;
            let (bl, br) = grow(cx - hw, cx + hw, sdx);
            let (bb, bt) = grow(cy - hh, cy + hh, sdy);
            PdfRect::new(
                PdfPoints::new(bb),
                PdfPoints::new(bl),
                PdfPoints::new(bt),
                PdfPoints::new(br),
            )
        };

        // A STAMP, not an Ink annotation.
        //
        // Both kinds take their appearance from the page objects added to
        // them, so either renders correctly here. But an /Ink annotation is
        // supposed to describe itself with an /InkList, and a shape drawn as
        // paths has none. Acrobat treats such an annotation as malformed and
        // refuses to select or move it, while text boxes (already stamps)
        // behaved normally. Reported from the field: "shapes are not
        // selectable in Acrobat, text are selectable and movable".
        //
        // Real freehand strokes DO get /Ink with a proper ink list; see
        // add_ink_annotations_inner, which is left alone.
        let Ok(mut annotation) = page.annotations_mut().create_stamp_annotation() else {
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
        // Radius is recorded UNCLAMPED, in points. Clamping happens at draw
        // time against the current box, so squeezing a rounded rect thin and
        // pulling it back out restores the corners it was drawn with instead of
        // permanently flattening them.
        let radius_pts = if spec.kind == SHAPE_ROUNDED_RECT {
            (spec.corner_radius_px * scale).max(0.0)
        } else {
            0.0
        };
        // The upright box in POINTS: the drag's own extent, before any rotation
        // and before the pad. This is what a rebuild needs and cannot otherwise
        // recover once the shape is turned.
        let box_w_pts = (x2 - x1).abs();
        let box_h_pts = (y2 - y1).abs();
        let _ = set_annotation_tag(
            &mut annotation,
            &shape_tag(
                spec, width_pts, radius_pts, box_w_pts, box_h_pts,
                spec.shadow_distance_px * scale,
                spec.shadow_softness_px * scale,
                spec.shadow_spread_px * scale,
            ),
        );

        // THE SHADOW GOES DOWN FIRST, because objects paint in the order they
        // are added and a shadow belongs under the thing casting it.
        //
        // It turns about its OWN centre, the shape's centre moved by the same
        // offset. Turning it about the shape's centre would swing it around the
        // shape as the shape rotated, which is an orbit and not a shadow.
        if has_shadow {
            let a = ((spec.shadow_rgba >> 24) & 0xFF) as u8;
            let r = ((spec.shadow_rgba >> 16) & 0xFF) as u8;
            let g = ((spec.shadow_rgba >> 8) & 0xFF) as u8;
            let b = (spec.shadow_rgba & 0xFF) as u8;
            let shadow_color = PdfColor::new(r, g, b, a);

            // A filled shape casts a filled silhouette and a stroke-only shape
            // casts an outline, so the shadow is the same KIND of mark as the
            // thing casting it rather than always one or the other.
            let shadow_fill = fill.map(|_| shadow_color);

            if let Ok(mut shadow) = build_path(sdx, sdy, shadow_color, shadow_fill) {
                rotate_object_about!(shadow, rot, cx + sdx, cy + sdy);
                if annotation.objects_mut().add_path_object(shadow).is_err() {
                    return STATUS_INVALID_INPUT;
                }
            }

            if spec.kind == SHAPE_ARROW {
                let (built, _) = build_head(sdx, sdy, shadow_color);
                if let Ok(mut head) = built {
                    rotate_object_about!(head, rot, cx + sdx, cy + sdy);
                    if annotation.objects_mut().add_path_object(head).is_err() {
                        return STATUS_INVALID_INPUT;
                    }
                }
            }
        }

        rotate_object_about!(path, rot, cx, cy);
        if annotation.objects_mut().add_path_object(path).is_err() {
            return STATUS_INVALID_INPUT;
        }

        if let Some(mut head) = head {
            rotate_object_about!(head, rot, cx, cy);
            if annotation.objects_mut().add_path_object(head).is_err() {
                return STATUS_INVALID_INPUT;
            }
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
            doc_handle, page_index, index, capture_width, left, top, right, bottom,
            out_new_index, false)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Moves or resizes a shape whose new bounds are given in **/Rect space**, that
/// is, INCLUDING the stroke pad the writer adds.
///
/// This is what the app actually has. It reads an annotation's reported
/// rectangle, shifts it by the drag delta, and hands it back. That rectangle is
/// padded, and `resize_shape_annotation` treats what it is given as the
/// UNPADDED extent, so every move inflated the shape by another pad. Recorded
/// for weeks as "about 2pt of drift per move"; it is a whole stroke pad, on
/// every drag.
///
/// The pad is applied by the writer down here, so undoing it belongs down here
/// too, not in arithmetic scattered across the caller.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub extern "C" fn move_shape_annotation(
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
            doc_handle, page_index, index, capture_width, left, top, right, bottom,
            out_new_index, true)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Rotates one of our shapes to a NEW absolute angle (clockwise degrees on
/// screen) about its centre, keeping its kind, colour, width and bounds. Under
/// the hood this is a restyle with only the rotation changed.
#[unsafe(no_mangle)]
pub extern "C" fn rotate_shape_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    degrees: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        // Passing color_rgba=0 and width_px=-1 keeps the tag's colour and
        // width; rotation_override supplies the new angle. Fill stays as it was
        // (None), so a rotate does not clear or change a filled shape.
        restyle_shape_annotation_inner_with_rotation(
            doc_handle, page_index, index, capture_width,
            0, -1.0, Some(degrees), None, None, None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Applies a new colour and/or width to one of our shapes without moving it.
/// Reads the tag for kind, corner flags, and current bounds; deletes; re-adds
/// with the new style. A width_px < 0 keeps the tag's current width; an alpha
/// of 0 in `color_rgba` keeps the current colour.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn restyle_shape_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    color_rgba: u32,
    width_px: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        restyle_shape_annotation_inner_with_rotation(
            doc_handle, page_index, index, capture_width, color_rgba, width_px,
            None, None, None, None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Applies a new fill colour to one of our rectangle or ellipse shapes without
/// moving it. Zero clears the fill (shape becomes stroke-only). Line and arrow
/// shapes ignore fill: they still write the value into their tag for round-trip
/// consistency, but the drawn shape doesn't gain a fill (there's nothing to
/// fill on a line).
#[unsafe(no_mangle)]
pub extern "C" fn restyle_shape_fill_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    fill_rgba: u32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        // color_rgba=0, width_px=-1, rotation_override=None: preserve stroke
        // colour, width and rotation. Only the fill changes.
        restyle_shape_annotation_inner_with_rotation(
            doc_handle, page_index, index, capture_width,
            0, -1.0, None, Some(fill_rgba), None, None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Sets the corner radius of one of our rounded rectangles, in capture-space
/// pixels, without moving or restyling it.
///
/// A negative radius is rejected rather than treated as zero: zero is a
/// meaningful value here (square corners), so silently coercing a bad number
/// into it would hide the caller's mistake as a legitimate-looking shape.
///
/// Every other kind ignores the radius, and setting it on one is harmless: the
/// value round-trips through the tag and nothing draws with it.
#[unsafe(no_mangle)]
pub extern "C" fn restyle_shape_radius_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    radius_px: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if !radius_px.is_finite() || radius_px < 0.0 {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| {
        restyle_shape_annotation_inner_with_rotation(
            doc_handle, page_index, index, capture_width,
            0, -1.0, None, None, Some(radius_px), None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Sets, changes or clears the DROP SHADOW on one of our shapes, without
/// moving or otherwise restyling it.
///
/// The last of the shape's properties to become editable. Until this existed a
/// shadow could only be given to a shape as it was drawn: the restyle path
/// carried whatever the tag already held, so nothing could put one on a shape
/// that had none, and nothing could take one away.
///
/// `rgba` of ZERO CLEARS the shadow, the same bargain the fill makes, and the
/// other four values are then ignored. Lengths are in capture-space pixels,
/// like `radius_px` and unlike the points the tag stores.
///
/// `softness_px` and `spread_px` are stored and round-tripped and drawn by
/// nothing; see `ShapeSpec`. They are accepted here so a caller that sets them
/// does not lose them, not because anything renders them yet.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn restyle_shape_shadow_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    angle_deg: f32,
    distance_px: f32,
    softness_px: f32,
    spread_px: f32,
    rgba: u32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if !angle_deg.is_finite() {
        return STATUS_INVALID_INPUT;
    }
    // Lengths, so negative is meaningless. The angle is not a length and may
    // be any finite number, since a light can be anywhere and 450 degrees is
    // simply 90.
    for length in [distance_px, softness_px, spread_px] {
        if !length.is_finite() || length < 0.0 {
            return STATUS_INVALID_INPUT;
        }
    }

    panic::catch_unwind(|| {
        restyle_shape_annotation_inner_with_rotation(
            doc_handle, page_index, index, capture_width,
            0, -1.0, None, None, None,
            Some(ShadowOverride { angle_deg, distance_px, softness_px, spread_px, rgba }),
            out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
fn restyle_shape_annotation_inner_with_rotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    color_rgba: u32,
    width_px: f32,
    rotation_override: Option<f32>,
    fill_override: Option<u32>,
    radius_override: Option<f32>,
    shadow_override: Option<ShadowOverride>,
    out_new_index: *mut i32,
) -> i32 {
    use pdfium_render::prelude::*;

    // Read tag AND the annotation's own bounds BEFORE the delete, so a mark
    // that is not one of our shapes leaves the page untouched.
    let (kind, cur_r, cur_g, cur_b, cur_a, cur_width_pts, fx, fy, cur_rot, cur_fill, cur_radius_pts,
         cur_box_w_pts, cur_box_h_pts, cur_shadow,
         page_left, page_top, page_w, bounds) = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else { return STATUS_INVALID_INPUT; };
        let doc_guard = lock(&doc);
        let Ok(page) = doc_guard.pages().get(page_index as u16) else { return STATUS_INVALID_INPUT; };
        let Some(annotation) = page.annotations().iter().nth(index as usize) else { return STATUS_INVALID_INPUT; };
        let Some(tag) = annotation_tag(&annotation).as_deref().and_then(parse_shape_tag) else {
            return STATUS_UNSUPPORTED;
        };
        let Ok(bx) = annotation.bounds() else { return STATUS_INVALID_INPUT; };
        let pw = page.width().value;
        if pw <= 0.0 { return STATUS_INVALID_INPUT; }
        let (page_left, page_top) = page_origin(&page);
        (tag.0, tag.1, tag.2, tag.3, tag.4, tag.5, tag.6, tag.7, tag.8, tag.9, tag.10,
         tag.11, tag.12, tag.13,
         page_left, page_top, pw, bx)
    };

    // Convert the annotation's PDF-point bounds back into capture space AND
    // subtract the stroke padding that the writer added around the content.
    // The writer builds shape extent then inflates by `width_pts/2 + 1` on every
    // side so PDFium doesn't clip the stroke; if a restyle used those inflated
    // bounds as the NEW extent, the writer would inflate again, and a slider
    // drag (opacity, colour change) that re-styles many times would grow the
    // shape a little each tick. Undo the pad here to keep the content extent
    // stable across restyles. (Rotated shapes need a smarter reversal - the
    // stored /Rect is the AABB of the rotated content plus pad - but the
    // opacity-slider case is un-rotated in practice.)
    let scale_cap_per_pt = capture_width as f32 / page_w;
    let pad_pts = cur_width_pts / 2.0 + 1.0;
    let (cap_left, cap_right, cap_top, cap_bottom) =
        if cur_rot != 0.0 && cur_box_w_pts > 0.0 && cur_box_h_pts > 0.0 {
            // A TURNED shape's /Rect is the axis-aligned box of the rotated
            // content, so insetting it by the pad does not give the extent back;
            // it gives something bigger, and restyling repeatedly inflates the
            // shape. Its own upright size is on the tag, and the CENTRE of /Rect
            // is exact at any angle, so the two together reconstruct it.
            let cx = ((bounds.left().value + bounds.right().value) / 2.0 - page_left) * scale_cap_per_pt;
            let cy = (page_top - (bounds.top().value + bounds.bottom().value) / 2.0) * scale_cap_per_pt;
            let hw = cur_box_w_pts * scale_cap_per_pt / 2.0;
            let hh = cur_box_h_pts * scale_cap_per_pt / 2.0;
            (cx - hw, cx + hw, cy - hh, cy + hh)
        } else {
            (
                (bounds.left().value + pad_pts - page_left) * scale_cap_per_pt,
                (bounds.right().value - pad_pts - page_left) * scale_cap_per_pt,
                (page_top - bounds.top().value + pad_pts) * scale_cap_per_pt,
                (page_top - bounds.bottom().value - pad_pts) * scale_cap_per_pt,
            )
        };

    // Apply the caller's overrides on top of the tag's own values.
    let (nr, ng, nb, na) = if (color_rgba & 0xFF) != 0 {
        (((color_rgba >> 24) & 0xFF) as u8,
         ((color_rgba >> 16) & 0xFF) as u8,
         ((color_rgba >> 8) & 0xFF) as u8,
         (color_rgba & 0xFF) as u8)
    } else {
        (cur_r, cur_g, cur_b, cur_a)
    };
    let new_width_px = if width_px >= 0.0 {
        width_px
    } else {
        // Tag stored width in POINTS; ShapeSpec wants capture pixels.
        cur_width_pts * scale_cap_per_pt
    };

    if delete_annotation(doc_handle, page_index, index) != STATUS_OK_PDFIUM {
        return STATUS_INVALID_INPUT;
    }

    // THE SHADOW, resolved once: the caller's when there is one, otherwise the
    // tag's own converted out of points.
    //
    // The override replaces the tag's shadow WHOLE rather than merging field by
    // field, because a caller with rgba 0 is clearing it and there is nothing
    // sensible to merge a cleared shadow with. That also means a caller
    // changing one value has to send the other four, which is what every other
    // override here already expects.
    let shadow = shadow_override.unwrap_or_else(|| ShadowOverride {
        angle_deg: cur_shadow.map_or(0.0, |sh| sh.angle_deg),
        distance_px: cur_shadow.map_or(0.0, |sh| sh.distance_pts * scale_cap_per_pt),
        softness_px: cur_shadow.map_or(0.0, |sh| sh.softness_pts * scale_cap_per_pt),
        spread_px: cur_shadow.map_or(0.0, |sh| sh.spread_pts * scale_cap_per_pt),
        rgba: cur_shadow.map_or(0, |sh| sh.rgba),
    });

    // Restore the drag-direction so an arrow keeps its head where it was.
    let (x1, x2) = if fx { (cap_left, cap_right) } else { (cap_right, cap_left) };
    let (y1, y2) = if fy { (cap_top, cap_bottom) } else { (cap_bottom, cap_top) };

    let spec = ShapeSpec {
        page_index, kind, x1, y1, x2, y2,
        r: nr, g: ng, b: nb, a: na,
        width_px: new_width_px,
        rotation_deg: rotation_override.unwrap_or(cur_rot),
        // Fill override applies the caller's value; None keeps the tag's fill
        // (so a restyle of colour or width alone preserves the fill). A caller
        // that wants to CLEAR the fill passes Some(0), not None.
        fill_rgba: fill_override.unwrap_or(cur_fill),
        // A radius override is already in capture pixels; without one the
        // radius comes straight back off the tag, converted through the same
        // scale the width uses. Restyling colour or fill must never disturb the
        // corners of a rounded rectangle.
        corner_radius_px: radius_override.unwrap_or(cur_radius_pts * scale_cap_per_pt),
        // Carried straight back off the tag, like the fill and the radius. A
        // restyle rebuilds the whole shape from its tag, so anything not put
        // back here is DROPPED: changing a shape's colour would quietly take
        // its shadow away.
        shadow_angle_deg: shadow.angle_deg,
        shadow_distance_px: shadow.distance_px,
        shadow_softness_px: shadow.softness_px,
        shadow_spread_px: shadow.spread_px,
        shadow_rgba: shadow.rgba,
    };
    let status = add_shape_annotations(doc_handle, capture_width, &spec, 1);
    if status != STATUS_OK_PDFIUM { return status; }

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

/// A shape's own extent, recovered from the rectangle PDFium reports for it.
///
/// An annotation's /Rect is NOT the shape's geometry. The writer adds two
/// things to it, and both have to come off before that rectangle can be handed
/// back as an extent:
///
/// 1. THE STROKE PAD, `width/2 + 1` on every side, so PDFium does not clip the
///    stroke. Feeding it back inflates the shape by another pad.
/// 2. THE TURN. For a rotated shape /Rect is the axis-aligned box that CONTAINS
///    the rotated content, bigger than the shape in both axes, and not
///    invertible at 45 degrees where the two axes contribute equally. The
///    shape's own upright size is recorded on its tag instead. The CENTRE of
///    /Rect is exact whatever the angle, so centre plus recorded size
///    reconstructs the shape precisely.
///
/// A rotated shape whose tag predates the recorded size cannot be recovered, so
/// its bounds come back unchanged: the old behaviour, rather than a different
/// wrong answer.
///
/// Bounds are in capture-space pixels with a top-left origin, the space the app
/// works in, and `page_width_pts` is what converts the tag's points into it.
#[allow(clippy::too_many_arguments)]
fn upright_shape_bounds(
    rot: f32,
    width_pts: f32,
    box_w_pts: f32,
    box_h_pts: f32,
    capture_width: i32,
    page_width_pts: f32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
) -> (f32, f32, f32, f32) {
    let per_pt = capture_width as f32 / page_width_pts;

    if rot != 0.0 {
        if box_w_pts <= 0.0 || box_h_pts <= 0.0 {
            return (left, top, right, bottom);
        }
        let (cx, cy) = ((left + right) / 2.0, (top + bottom) / 2.0);
        let (hw, hh) = (box_w_pts * per_pt / 2.0, box_h_pts * per_pt / 2.0);
        return (cx - hw, cy - hh, cx + hw, cy + hh);
    }

    // Never inset past nothing: a hairline shape dragged very small would
    // otherwise invert.
    let pad = (width_pts / 2.0 + 1.0) * per_pt;
    let max_inset = ((right - left).min(bottom - top) / 2.0 - 0.5).max(0.0);
    let inset = pad.min(max_inset);
    (left + inset, top + inset, right - inset, bottom - inset)
}

/// The upright extent of the shape a tag describes, given the rectangle it is
/// reported at. See [`upright_shape_bounds`] for what is being undone.
///
/// Exposed because every path that REBUILDS a shape from its tag needs this and
/// only the resize path had it: a pasted, duplicated or undeleted shape was
/// built from its /Rect directly and came back larger, or at 90 degrees, lying
/// the wrong way round. Pure geometry, so it takes the page width rather than a
/// document handle.
///
/// The four out-parameters receive the recovered extent, in the same space.
#[unsafe(no_mangle)]
pub extern "C" fn shape_upright_bounds(
    capture_width: i32,
    page_width_pts: f32,
    tag_utf8: *const u8,
    tag_len: usize,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    out_left: *mut f32,
    out_top: *mut f32,
    out_right: *mut f32,
    out_bottom: *mut f32,
) -> i32 {
    if capture_width <= 0 || !page_width_pts.is_finite() || page_width_pts <= 0.0 {
        return STATUS_INVALID_INPUT;
    }
    if tag_utf8.is_null() || tag_len == 0 {
        return STATUS_INVALID_INPUT;
    }
    if out_left.is_null() || out_top.is_null() || out_right.is_null() || out_bottom.is_null() {
        return STATUS_INVALID_INPUT;
    }
    if !(right > left) || !(bottom > top) {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        let bytes = unsafe { std::slice::from_raw_parts(tag_utf8, tag_len) };
        let Ok(text) = std::str::from_utf8(bytes) else {
            return STATUS_INVALID_INPUT;
        };
        let Some(tag) = parse_shape_tag(text) else {
            return STATUS_UNSUPPORTED;
        };
        let (_, _, _, _, _, width_pts, _, _, rot, _, _, box_w_pts, box_h_pts, _) = tag;

        let (l, t, r, b) = upright_shape_bounds(
            rot, width_pts, box_w_pts, box_h_pts, capture_width, page_width_pts,
            left, top, right, bottom,
        );

        unsafe {
            *out_left = l;
            *out_top = t;
            *out_right = r;
            *out_bottom = b;
        }
        STATUS_OK_PDFIUM
    })
    .unwrap_or(STATUS_PANIC)
}

#[allow(clippy::too_many_arguments)]
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
    bounds_are_padded: bool,
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
        match annotation_tag(&annotation).as_deref().and_then(parse_shape_tag) {
            Some(t) => t,
            None => return STATUS_UNSUPPORTED,
        }
    };

    let (
        kind, r, g, b, a, width_pts, fx, fy, rot, fill_rgba, radius_pts, box_w_pts, box_h_pts,
        cur_shadow,
    ) = tag;

    // The two things the writer added to /Rect come off here, and only when the
    // caller's bounds actually came from /Rect. Shared with every other path
    // that rebuilds a shape from its tag; see `upright_shape_bounds` for what
    // is being undone and why a turned shape needs its recorded size.
    let (left, top, right, bottom) = if bounds_are_padded {
        let page_w = {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&doc_handle).cloned();
            let Some(doc) = doc else { return STATUS_INVALID_INPUT; };
            let doc_guard = lock(&doc);
            let Ok(page) = doc_guard.pages().get(page_index as u16) else {
                return STATUS_INVALID_INPUT;
            };
            page.width().value
        };
        if page_w <= 0.0 {
            return STATUS_INVALID_INPUT;
        }
        upright_shape_bounds(
            rot, width_pts, box_w_pts, box_h_pts, capture_width, page_w,
            left, top, right, bottom,
        )
    } else {
        (left, top, right, bottom)
    };

    if delete_annotation(doc_handle, page_index, index) != STATUS_OK_PDFIUM {
        return STATUS_INVALID_INPUT;
    }

    // The stored corner flags put the drag back the way round it was drawn, so
    // an arrow resized by its opposite corner does not flip.
    let (x1, x2) = if fx { (left, right) } else { (right, left) };
    let (y1, y2) = if fy { (top, bottom) } else { (bottom, top) };

    // Width was stored in PDF points and the spec wants capture-space pixels,
    // so it goes back through the same scale the writer applied.
    let (width_px, radius_px, shadow_distance, shadow_softness, shadow_spread) = {
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
        let cap_per_pt = capture_width as f32 / page_w;
        // The radius rides through a resize unchanged. Keeping the absolute
        // value means a box stretched wider keeps the same corner curve rather
        // than having it grow with the box, which is what a rounded rectangle
        // is expected to do. The draw-time clamp handles a box shrunk below
        // twice the radius.
        // The shadow rides through a resize the way the radius does: its
        // DISTANCE is absolute, so a box stretched wider keeps the same shadow
        // rather than having it stretch with the box. The angle is not a
        // length and needs no conversion at all.
        (
            width_pts * cap_per_pt,
            radius_pts * cap_per_pt,
            cur_shadow.map_or(0.0, |sh| sh.distance_pts * cap_per_pt),
            cur_shadow.map_or(0.0, |sh| sh.softness_pts * cap_per_pt),
            cur_shadow.map_or(0.0, |sh| sh.spread_pts * cap_per_pt),
        )
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
        rotation_deg: rot,
        fill_rgba,
        corner_radius_px: radius_px,
        shadow_angle_deg: cur_shadow.map_or(0.0, |sh| sh.angle_deg),
        shadow_distance_px: shadow_distance,
        shadow_softness_px: shadow_softness,
        shadow_spread_px: shadow_spread,
        shadow_rgba: cur_shadow.map_or(0, |sh| sh.rgba),
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

/// Resizes one of OUR text boxes by RE-LAYING-OUT its text at the new bounds,
/// so the words re-wrap to the new width and the box grows to fit, exactly as
/// when it was first placed. This is what makes a text box resize like a Word
/// text box (drag any handle, text re-flows) instead of stretching its rendered
/// glyphs the way scaling a stamp would. Reports UNSUPPORTED for anything that
/// is not one of our text boxes, so the caller falls back to the generic resize.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn resize_text_box_annotation(
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
        relayout_text_box_inner_v2(
            doc_handle, page_index, index, capture_width,
            Some(left), Some(top), Some(right), Some(bottom),
            None, None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// A partial style patch for a text box: any field left None keeps the value
/// already in the tag. Used by `restyle_text_box_annotation` to change the fill
/// or outline while preserving the words, font, rotation, and bounds.
#[derive(Default)]
struct StyleOverride {
    text_color: Option<(u8, u8, u8, u8)>,
    align: Option<i32>,
    fill: Option<PackedRgba>,
    outline: Option<PackedRgba>,
    outline_width_px: Option<f32>,
    underline: Option<bool>,
    strikethrough: Option<bool>,
}

/// Applies a NEW style (fill, outline, underline, etc.) to one of OUR text boxes
/// without moving or turning it: the tag's bounds, rotation, font, size, and
/// words are all preserved, only the style fields the caller wants to change are
/// updated, then the box is re-laid-out. -1 (or a negative width) leaves that
/// field alone; a colour with alpha 0 means "no fill / no outline".
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn restyle_text_box_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    text_rgba: u32,
    align: i32,
    fill_rgba: u32,
    outline_rgba: u32,
    outline_width_px: f32,
    underline: i32,
    strikethrough: i32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }

    let text_color = if text_rgba != 0 {
        Some((
            ((text_rgba >> 24) & 0xFF) as u8,
            ((text_rgba >> 16) & 0xFF) as u8,
            ((text_rgba >> 8) & 0xFF) as u8,
            (text_rgba & 0xFF) as u8,
        ))
    } else { None };
    let align_opt = if matches!(align, ALIGN_LEFT | ALIGN_CENTER | ALIGN_RIGHT | ALIGN_JUSTIFY) {
        Some(align)
    } else { None };
    let width_opt = if outline_width_px >= 0.0 { Some(outline_width_px) } else { None };
    let overrides = StyleOverride {
        text_color,
        align: align_opt,
        // A colour is a patch iff the alpha is set; alpha 0 is a legal patch
        // meaning "clear the fill/outline".
        fill: Some(PackedRgba(fill_rgba)),
        outline: Some(PackedRgba(outline_rgba)),
        outline_width_px: width_opt,
        underline: if underline < 0 { None } else { Some(underline != 0) },
        strikethrough: if strikethrough < 0 { None } else { Some(strikethrough != 0) },
    };

    panic::catch_unwind(|| {
        // Bounds are the tag's own upright rect; left..bottom of 0 tells the inner
        // function to take them from the tag.
        relayout_text_box_inner_v2(
            doc_handle, page_index, index, capture_width,
            None, None, None, None,
            None, Some(overrides), out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// Rotates one of OUR text boxes to a NEW absolute angle (clockwise degrees on
/// screen) about its centre, re-laying-it-out at its own upright bounds so the
/// turned box comes out clean. The caller passes the box's UPRIGHT rect (the one
/// stored in its tag), not the enlarged bounding box a rotated box reports.
/// Reports UNSUPPORTED for anything that is not one of our text boxes.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn rotate_text_box_annotation(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    degrees: f32,
    out_new_index: *mut i32,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || index < 0 || capture_width <= 0 {
        return STATUS_INVALID_INPUT;
    }
    if right <= left || bottom <= top {
        return STATUS_INVALID_INPUT;
    }

    panic::catch_unwind(|| {
        relayout_text_box_inner_v2(
            doc_handle, page_index, index, capture_width,
            Some(left), Some(top), Some(right), Some(bottom),
            Some(degrees), None, out_new_index)
    })
    .unwrap_or(STATUS_PANIC)
}

/// The shared re-layout for a text box: parse its tag, optionally override its
/// bounds/rotation/style, delete, and re-add. Move/resize passes new bounds;
/// rotate passes a new angle; restyle passes new style fields; each keeps
/// everything the caller does not override (font, words, size, and any bit of
/// style not patched). Bounds default to the tight upright rect in the tag when
/// the caller passes None for them.
#[allow(clippy::too_many_arguments)]
fn relayout_text_box_inner_v2(
    doc_handle: u64,
    page_index: i32,
    index: i32,
    capture_width: i32,
    left: Option<f32>,
    top: Option<f32>,
    right: Option<f32>,
    bottom: Option<f32>,
    rotation_override: Option<f32>,
    style_override: Option<StyleOverride>,
    out_new_index: *mut i32,
) -> i32 {
    use pdfium_render::prelude::*;

    // Read and parse the tag BEFORE anything is removed, so a mark that is not
    // one of our text boxes leaves the page untouched. Also grab the page's
    // width in points here, since we may need it to convert the tag's own
    // normalized rect into capture space for the caller-omitted bounds case.
    let (mut parsed, page_w_pts) = {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&doc_handle).cloned();
        let Some(doc) = doc else {
            return STATUS_INVALID_INPUT;
        };
        let doc_guard = lock(&doc);
        let Ok(page) = doc_guard.pages().get(page_index as u16) else {
            return STATUS_INVALID_INPUT;
        };
        let Some(annotation) = page.annotations().iter().nth(index as usize) else {
            return STATUS_INVALID_INPUT;
        };
        let parsed = match annotation_tag(&annotation).as_deref().and_then(parse_textbox_tag_full) {
            Some(p) => p,
            None => return STATUS_UNSUPPORTED,
        };
        (parsed, page.width().value)
    };

    // A rotate sets a NEW absolute angle; a resize keeps whatever the tag carried.
    if let Some(deg) = rotation_override {
        parsed.style.rotation_deg = deg;
    }

    // Restyle patches a subset of the style fields; unset fields keep the tag's
    // value. The text colour is patched separately because it lives outside the
    // TextStyle struct.
    let mut text_r = parsed.r;
    let mut text_g = parsed.g;
    let mut text_b = parsed.b;
    let mut text_a = parsed.a;
    if let Some(over) = style_override {
        if let Some((r, g, b, a)) = over.text_color {
            text_r = r; text_g = g; text_b = b; text_a = a;
        }
        if let Some(a) = over.align { parsed.style.align = a; }
        if let Some(f) = over.fill { parsed.style.fill = f; }
        if let Some(o) = over.outline { parsed.style.outline = o; }
        if let Some(w) = over.outline_width_px { parsed.style.outline_width_px = w; }
        if let Some(u) = over.underline { parsed.style.underline = u; }
        if let Some(s) = over.strikethrough { parsed.style.strikethrough = s; }
    }

    // Bounds: caller-provided override the tag; otherwise take the tight upright
    // rect out of the tag (which was normalized like get_annotations reports,
    // i.e. offset/page_width). Convert to capture space.
    let (bl, bt, br, bb) = if let (Some(l), Some(t), Some(r), Some(b)) = (left, top, right, bottom) {
        (l, t, r, b)
    } else {
        // Read the box rect that add_text_box_inner wrote into the tag. We use the
        // page width already fetched above (in points) with the caller's capture
        // width to scale from normalized coords back to capture coords.
        // The rect field in the tag is expressed as offset/page_width for all four
        // sides, so multiplying by capture_width recovers capture-space pixels.
        let (nl, nt, nr, nb) = tag_box_rect(index as usize, doc_handle, page_index)
            .unwrap_or((0.0, 0.0, 1.0, 1.0));
        (nl * capture_width as f32, nt * capture_width as f32,
         nr * capture_width as f32, nb * capture_width as f32)
    };
    let _ = page_w_pts;

    if delete_annotation(doc_handle, page_index, index) != STATUS_OK_PDFIUM {
        return STATUS_INVALID_INPUT;
    }

    let status = add_text_box_inner(
        doc_handle, page_index, capture_width, bl, bt, br, bb,
        &parsed.text, parsed.size_px, text_r, text_g, text_b, text_a,
        parsed.style, parsed.font_path.as_deref());
    if status != STATUS_OK_PDFIUM {
        return status;
    }

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

/// Reads the tight upright rect the tag records for one of our text boxes.
/// Used by restyle so the caller does not have to know the box's bounds. The
/// rect fields (indices 9..=12) are only present on the >=14-field form; older
/// boxes return None and the caller falls back to whatever else it has.
fn tag_box_rect(index: usize, doc_handle: u64, page_index: i32) -> Option<(f32, f32, f32, f32)> {
    use pdfium_render::prelude::*;
    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned()?;
    let doc_guard = lock(&doc);
    let page = doc_guard.pages().get(page_index as u16).ok()?;
    let annotation = page.annotations().iter().nth(index)?;
    let contents = annotation_tag(&annotation)?;
    let body = strip_id_prefix(&contents).1;
    let parts: Vec<&str> = body.strip_prefix(TEXTBOX_TAG_STYLED)?.split(':').collect();
    if parts.len() < 14 {
        return None;
    }
    Some((
        parts[9].parse().ok()?,
        parts[10].parse().ok()?,
        parts[11].parse().ok()?,
        parts[12].parse().ok()?,
    ))
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

        let (origin_x, origin_top) = page_origin(&page);
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

        // Page origin from the RENDERED box (crop, then media), not assumed to
        // be zero, so the geometry matches both what PDFium draws and what
        // get_annotations reads back. See page_origin.
        let (origin_x, origin_top) = page_origin(&page);
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
// Enumerating AcroForm fields for an interactive fill UI.
//
// get_form_field_count above answers "does this document have a form", but a
// UI that lets the user fill it needs, per WIDGET: which page it is on, what
// kind of control to draw, where to put it, its name (to write back by), and
// its current value/state. That is variable-length data (names, values), so it
// travels as a self-describing ByteBuffer rather than a fixed struct array.
//
// Layout (all little-endian):
//   u32  count
//   repeated `count` times:
//     i32  page_index
//     i32  kind          (FIELD_* below)
//     i32  flags         (bit0 = read-only, bit1 = checked)
//     i32  group_index   (a radio/checkbox widget's unique index within its
//                         control group; 0 for everything else — this is how a
//                         specific radio button is addressed, since the export
//                         value is unreachable, see note below)
//     f32  left, top, right, bottom   (top-left origin, both axes / page WIDTH,
//                                       exactly like AnnotationInfo)
//     u32  name_len,  name_bytes  (UTF-8)
//     u32  value_len, value_bytes (UTF-8; for radio/checkbox this is the GROUP's
//                                  currently-selected value, shared by the group)
//
// A radio widget's own export value ("Basic"/"Pro") would be the natural key,
// but FPDFAnnot_GetFormFieldExportValue needs the raw FPDF_ANNOTATION handle,
// which pdfium-render keeps pub(crate) (same wall as FPDF_MovePages). So each
// widget is addressed by its group_index instead, which IS reachable.
// ---------------------------------------------------------------------

pub const FIELD_OTHER: i32 = 0;
pub const FIELD_TEXT: i32 = 1;
pub const FIELD_CHECKBOX: i32 = 2;
pub const FIELD_RADIO: i32 = 3;
pub const FIELD_COMBO: i32 = 4;
pub const FIELD_LISTBOX: i32 = 5;
pub const FIELD_PUSHBUTTON: i32 = 6;
pub const FIELD_SIGNATURE: i32 = 7;

const FIELD_FLAG_READONLY: i32 = 1;
const FIELD_FLAG_CHECKED: i32 = 2;

/// Every form-field widget in the document, serialized as described above.
/// A document with no AcroForm is a successful EMPTY result (count 0), not an
/// error. Invalid handle -> STATUS_INVALID_INPUT; panic -> STATUS_PANIC.
#[unsafe(no_mangle)]
pub extern "C" fn get_form_fields(doc_handle: u64) -> ByteBuffer {
    if doc_handle == 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_form_fields_inner(doc_handle))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_form_fields_inner(doc_handle: u64) -> ByteBuffer {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let doc_guard = lock(&doc);

    // Ask whether the document has a form AT ALL before walking it.
    //
    // The loop below iterates pages, and iterating pages LOADS them. On a
    // 3352-page book with no form that was 34 seconds of parsing content
    // streams to discover there were no fields, which is most of the time the
    // app spent between laying a big document out and drawing its first page.
    //
    // FPDF_GetFormType reads the catalog and answers in constant time. Almost
    // every PDF that is not a form answers NONE here and never touches a page.
    if doc_guard.form().is_none() {
        let mut boxed = 0u32.to_le_bytes().to_vec().into_boxed_slice();
        let buffer = ByteBuffer {
            data: boxed.as_mut_ptr(),
            len: boxed.len(),
            status: STATUS_OK_PDFIUM,
        };
        std::mem::forget(boxed);
        return buffer;
    }

    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes()); // count placeholder, backfilled below
    let mut count: u32 = 0;

    for (page_index, page) in doc_guard.pages().iter().enumerate() {
        let page_w = page.width().value;
        if page_w <= 0.0 {
            continue;
        }
        let page_top = page
            .boundaries()
            .media()
            .map(|b| b.bounds.top().value)
            .unwrap_or(page.height().value);
        let page_left = page
            .boundaries()
            .media()
            .map(|b| b.bounds.left().value)
            .unwrap_or(0.0);

        let annotations = page.annotations();
        for i in 0..annotations.len() {
            let Ok(annotation) = annotations.get(i) else {
                continue;
            };
            let Some(field) = annotation.as_form_field() else {
                continue;
            };

            // Determine control kind, current value string, checked state, and
            // (for grouped controls) the widget's unique index within its group.
            let (kind, value, checked, group_index) = if let Some(t) = field.as_text_field() {
                (FIELD_TEXT, t.value().unwrap_or_default(), false, 0i32)
            } else if let Some(c) = field.as_checkbox_field() {
                (
                    FIELD_CHECKBOX,
                    c.group_value().unwrap_or_default(),
                    c.is_checked().unwrap_or(false),
                    c.index_in_group() as i32,
                )
            } else if let Some(r) = field.as_radio_button_field() {
                (
                    FIELD_RADIO,
                    r.group_value().unwrap_or_default(),
                    r.is_checked().unwrap_or(false),
                    r.index_in_group() as i32,
                )
            } else if let Some(cb) = field.as_combo_box_field() {
                (FIELD_COMBO, cb.value().unwrap_or_default(), false, 0)
            } else if let Some(lb) = field.as_list_box_field() {
                (FIELD_LISTBOX, lb.value().unwrap_or_default(), false, 0)
            } else if field.as_signature_field().is_some() {
                (FIELD_SIGNATURE, String::new(), false, 0)
            } else if field.as_push_button_field().is_some() {
                (FIELD_PUSHBUTTON, String::new(), false, 0)
            } else {
                (FIELD_OTHER, String::new(), false, 0)
            };

            let Ok(bounds) = annotation.bounds() else {
                continue;
            };
            let left = (bounds.left().value - page_left) / page_w;
            let right = (bounds.right().value - page_left) / page_w;
            let top = (page_top - bounds.top().value) / page_w;
            let bottom = (page_top - bounds.bottom().value) / page_w;

            let mut flags = 0i32;
            if field.is_read_only() {
                flags |= FIELD_FLAG_READONLY;
            }
            if checked {
                flags |= FIELD_FLAG_CHECKED;
            }

            let name = field.name().unwrap_or_default();

            out.extend_from_slice(&(page_index as i32).to_le_bytes());
            out.extend_from_slice(&kind.to_le_bytes());
            out.extend_from_slice(&flags.to_le_bytes());
            out.extend_from_slice(&group_index.to_le_bytes());
            out.extend_from_slice(&left.to_le_bytes());
            out.extend_from_slice(&top.to_le_bytes());
            out.extend_from_slice(&right.to_le_bytes());
            out.extend_from_slice(&bottom.to_le_bytes());
            out.extend_from_slice(&(name.len() as u32).to_le_bytes());
            out.extend_from_slice(name.as_bytes());
            out.extend_from_slice(&(value.len() as u32).to_le_bytes());
            out.extend_from_slice(value.as_bytes());

            count += 1;
        }
    }

    out[0..4].copy_from_slice(&count.to_le_bytes());

    let mut boxed = out.into_boxed_slice();
    let buffer = ByteBuffer {
        data: boxed.as_mut_ptr(),
        len: boxed.len(),
        status: STATUS_OK_PDFIUM,
    };
    std::mem::forget(boxed);
    buffer
}

// ---------------------------------------------------------------------
// Bookmarks (the document's own outline)
//
// Serialized into one ByteBuffer, little-endian:
//   u32 count
//   per entry: i32 depth, i32 page_index, u32 title_len, title bytes (UTF-8)
//
// FLAT, with a depth on each entry, rather than a nested structure. The panel
// wants a list it can virtualize and indent, the FFI boundary has no cheap way
// to express a tree, and pre-order plus depth is enough to rebuild one if it is
// ever needed.
//
// page_index is -1 when the outline entry does not resolve to a page: an entry
// can carry a remote or URI action, or a named destination the file never
// defines. Those still SHOW, because they are part of the author's outline and
// hiding them would silently rewrite the document's structure; they just do not
// go anywhere when clicked.
// ---------------------------------------------------------------------

/// A malformed outline can be a cycle, and PDFium will happily walk one for
/// ever. The walk is bounded rather than tracking visited handles, which are
/// pub(crate) in the vendored crate and not reachable from here.
const MAX_BOOKMARKS: u32 = 20_000;
const MAX_BOOKMARK_DEPTH: i32 = 32;

/// The document's outline, flattened in reading order. A document without one
/// is a successful EMPTY result (count 0), not an error. Invalid handle ->
/// STATUS_INVALID_INPUT; panic -> STATUS_PANIC.
#[unsafe(no_mangle)]
pub extern "C" fn get_bookmarks(doc_handle: u64) -> ByteBuffer {
    if doc_handle == 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_bookmarks_inner(doc_handle))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_bookmarks_inner(doc_handle: u64) -> ByteBuffer {
    use pdfium_render::prelude::*;

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let doc_guard = lock(&doc);

    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes()); // count placeholder, backfilled below
    let mut count: u32 = 0;

    // Every call here is catalog-level: titles and destinations live in the
    // outline dictionary, and FPDFDest_GetDestPageIndex answers from the page
    // tree. Nothing in this walk LOADS a page, which is what keeps a 3352-page
    // book's outline cheap.
    fn walk(
        node: Option<PdfBookmark<'_>>,
        depth: i32,
        out: &mut Vec<u8>,
        count: &mut u32,
    ) {
        let mut current = node;
        while let Some(bookmark) = current {
            if *count >= MAX_BOOKMARKS {
                return;
            }

            let title = bookmark.title().unwrap_or_default();

            // A destination directly, or the one inside a GoTo action. Plenty
            // of real files use the action form, and an outline that lost half
            // its targets to reading only the first would look broken.
            //
            // The action is bound to a local rather than chained: the
            // destination inside it BORROWS from the action, so a chained
            // temporary would be dropped while still borrowed.
            let page_index = if let Some(dest) = bookmark.destination() {
                dest.page_index().map(|i| i as i32).unwrap_or(-1)
            } else if let Some(action) = bookmark.action() {
                action
                    .as_local_destination_action()
                    .and_then(|a| a.destination().ok())
                    .and_then(|d| d.page_index().ok())
                    .map(|i| i as i32)
                    .unwrap_or(-1)
            } else {
                -1
            };

            out.extend_from_slice(&depth.to_le_bytes());
            out.extend_from_slice(&page_index.to_le_bytes());
            out.extend_from_slice(&(title.len() as u32).to_le_bytes());
            out.extend_from_slice(title.as_bytes());
            *count += 1;

            if depth < MAX_BOOKMARK_DEPTH {
                walk(bookmark.first_child(), depth + 1, out, count);
            }

            current = bookmark.next_sibling();
        }
    }

    walk(doc_guard.bookmarks().root(), 0, &mut out, &mut count);

    out[0..4].copy_from_slice(&count.to_le_bytes());

    let mut boxed = out.into_boxed_slice();
    let buffer = ByteBuffer {
        data: boxed.as_mut_ptr(),
        len: boxed.len(),
        status: STATUS_OK_PDFIUM,
    };
    std::mem::forget(boxed);
    buffer
}

// No set_checkbox_field / set_radio_field: pdfium-render's form-module WRITES
// are both non-rendering and unreliable. Setting /V via FPDFAnnot_SetStringValue
// never regenerates the widget /AP, so the value is invisible on render AND on
// flatten (proven: a filled text field, checked box and flipped radio all
// rendered unchanged, only the reader-baked /AP showed). Worse, radio
// set_checked() silently no-ops once another document's form environment has
// been initialized in the same process — PDFium keeps global form state, and
// the crate even ships stray debug println!s on that path. FPDFAnnot_SetAP and
// NeedAppearances would fix it but need the pub(crate) raw handle (rule 6/9).
//
// So the app fills forms by DRAWING its own annotations at the rects that
// get_form_fields reports (a text box for a text field, a check glyph for a
// checkbox) — the proven pipeline that renders, saves, flattens and re-edits.
// get_form_fields stays: reading a field's kind/rect/current state is reliable.
//
// One catch the drawing approach hits: PDFium's form layer (FPDF_FFLDraw) paints
// the widget appearance ON TOP of the page content and regular annotations, so a
// text box drawn "in" a field is hidden behind the field's own box. Setting the
// widget's Hidden flag does NOT help — FFLDraw ignores annotation flags and
// draws the field anyway (measured). The reliable fix is delete_form_field_widget
// below: remove the widget so the form layer has nothing to paint there, then the
// app's text shows. It is per-field (other fields keep their boxes and stay
// clickable via the rects get_form_fields captured) and touches no other
// annotation, so highlights/ink/text are untouched — unlike a whole-page flatten.

/// Deletes every widget annotation whose form field is named `field_name`. Used
/// when the app fills a field by drawing text over it: without the widget the
/// form layer no longer paints the field box on top of that text. Returns
/// STATUS_OK_PDFIUM (deleting an absent field is a successful no-op),
/// STATUS_INVALID_INPUT, or STATUS_PANIC.
#[unsafe(no_mangle)]
pub extern "C" fn delete_form_field_widget(doc_handle: u64, field_name: *const c_char) -> i32 {
    if doc_handle == 0 || field_name.is_null() {
        return STATUS_INVALID_INPUT;
    }
    panic::catch_unwind(|| delete_form_field_widget_inner(doc_handle, field_name))
        .unwrap_or(STATUS_PANIC)
}

fn delete_form_field_widget_inner(doc_handle: u64, field_name: *const c_char) -> i32 {
    use pdfium_render::prelude::*;

    let Ok(name_str) = (unsafe { CStr::from_ptr(field_name) }).to_str() else {
        return STATUS_INVALID_INPUT;
    };

    let _guard = lock(&CALL_LOCK);
    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return STATUS_INVALID_INPUT;
    };

    let doc_guard = lock(&doc);
    let _ = doc_guard.form();

    for mut page in doc_guard.pages().iter() {
        // Collect matching indices first, then delete from the back so earlier
        // indices stay valid as the list shrinks.
        let mut to_delete: Vec<usize> = Vec::new();
        {
            let annotations = page.annotations();
            for i in 0..annotations.len() {
                if let Ok(a) = annotations.get(i) {
                    if a.as_form_field().and_then(|f| f.name()).as_deref() == Some(name_str) {
                        to_delete.push(i as usize);
                    }
                }
            }
        }

        let annotations = page.annotations_mut();
        for &i in to_delete.iter().rev() {
            if let Ok(a) = annotations.get(i) {
                let _ = annotations.delete_annotation(a);
            }
        }
    }

    drop(doc_guard);
    evict_all_cache_for_doc(doc_handle);
    STATUS_OK_PDFIUM
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
    let (page_left, page_top) = page_origin(&page);
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

/// How many styled runs one page may contribute.
///
/// A page whose every character is styled differently would otherwise produce a
/// run per character, and the caller is looking for headings.
const MAX_TEXT_RUNS: u32 = 4_000;

/// Every run of characters on a page that shares a font, a size and a colour.
///
/// This is what "bookmark everything that looks like this heading" needs and
/// `get_page_chars` cannot answer: that reports where each character IS, not
/// what it is set in.
///
/// Little-endian, and shaped like `get_bookmarks`: a run count, then the number
/// of characters the page held ALTOGETHER, then per run a character start and
/// length, the font size in points, the fill colour packed as 0x00RRGGBB, and
/// two length-prefixed UTF-8 strings, the font name and the run's text.
///
/// That second number is the difference between "this page is a scan" and
/// "this page has text nothing can read". Both come back as zero runs, and they
/// need opposite advice: one can be OCR'd, the other cannot be helped by
/// anybody, because a font with no /ToUnicode map defeats every reader's search
/// as well as ours.
///
/// Runs break at a change of any of the three, and at a line ending, so a run
/// is at most one line. Joining lines back up is the caller's decision to make,
/// because a two-line heading and two one-line headings are indistinguishable
/// here and only the user knows which their document has.
#[unsafe(no_mangle)]
pub extern "C" fn get_page_text_runs(doc_handle: u64, page_index: i32) -> ByteBuffer {
    if doc_handle == 0 || page_index < 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_page_text_runs_inner(doc_handle, page_index))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_page_text_runs_inner(doc_handle: u64, page_index: i32) -> ByteBuffer {
    let _guard = lock(&CALL_LOCK);

    let doc = lock(&core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let doc_guard = lock(&doc);
    let runs = collect_text_runs(&doc_guard, page_index);
    drop(doc_guard);
    drop(doc);

    let Some(out) = runs else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    let mut boxed = out.into_boxed_slice();
    let buffer = ByteBuffer {
        data: boxed.as_mut_ptr(),
        len: boxed.len(),
        status: STATUS_OK_PDFIUM,
    };
    std::mem::forget(boxed);
    buffer
}

/// Caller must hold `CALL_LOCK`. `None` means the page could not be reached;
/// a page with no text layer is an ordinary empty result.
fn collect_text_runs(doc: &PdfDocument<'static>, page_index: i32) -> Option<Vec<u8>> {
    struct Run {
        start: i32,
        count: i32,
        /// Which line of the page this run sits on.
        ///
        /// Counted from the line breaks, not from coordinates, so it is exact.
        /// The caller needs it to tell "the next word on this line" from "the
        /// first word of the next one": documents exist that set every word as
        /// its own text object in its own font, and without this every word
        /// looks like a separate piece of text.
        line: u32,
        size: f32,
        color: u32,
        font: String,
        text: String,
    }

    let mut out: Vec<u8> = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes()); // run count, backfilled below
    out.extend_from_slice(&0u32.to_le_bytes()); // characters on the page, likewise
    let mut count: u32 = 0;

    fn flush(out: &mut Vec<u8>, count: &mut u32, run: Option<Run>) {
        let Some(run) = run else { return };

        // Whitespace carries no style a user could have pointed at, and a page
        // is full of it between the parts that do.
        if run.text.trim().is_empty() {
            return;
        }

        out.extend_from_slice(&run.start.to_le_bytes());
        out.extend_from_slice(&run.count.to_le_bytes());
        out.extend_from_slice(&run.line.to_le_bytes());
        out.extend_from_slice(&run.size.to_le_bytes());
        out.extend_from_slice(&run.color.to_le_bytes());
        out.extend_from_slice(&(run.font.len() as u32).to_le_bytes());
        out.extend_from_slice(run.font.as_bytes());
        out.extend_from_slice(&(run.text.len() as u32).to_le_bytes());
        out.extend_from_slice(run.text.as_bytes());
        *count += 1;
    }

    let page = doc.pages().get(page_index as u16).ok()?;
    let Ok(text) = page.text() else {
        // No text layer at all, which is what a scanned page looks like.
        return Some(out);
    };

    let mut current: Option<Run> = None;
    let mut chars_on_page: u32 = 0;
    let mut line: u32 = 0;

    for c in text.chars().iter() {
        chars_on_page += 1;
        if count >= MAX_TEXT_RUNS {
            break;
        }

        let Some(ch) = c.unicode_char() else { continue };

        if ch == '\r' || ch == '\n' {
            flush(&mut out, &mut count, current.take());

            // A "\r\n" counts twice, which costs nothing: the number only has
            // to DIFFER between lines, never to be the line's ordinal.
            line += 1;
            continue;
        }

        // Scaled, not unscaled: this is the size the text is SET at on the
        // page, after the text matrix, which is the number a reader would
        // read off it and the one that has to match the sample they picked.
        let size = c.scaled_font_size().value;
        let font = c.font_name();
        let color = c
            .fill_color()
            .map(|f| ((f.red() as u32) << 16) | ((f.green() as u32) << 8) | f.blue() as u32)
            .unwrap_or(0);

        // Quantised, because a size is a float that arrives from a matrix
        // multiply: two characters set in the same 12pt heading can report
        // 11.999998 and 12.000001, and comparing those exactly would break a
        // heading into a run per character.
        let same = match &current {
            Some(run) => {
                (run.size - size).abs() < 0.01 && run.color == color && run.font == font
            }
            None => false,
        };

        if !same {
            flush(&mut out, &mut count, current.take());
            current = Some(Run {
                start: c.index() as i32,
                count: 0,
                line,
                size,
                color,
                font,
                text: String::new(),
            });
        }

        if let Some(run) = current.as_mut() {
            run.text.push(ch);
            run.count += 1;
        }
    }

    flush(&mut out, &mut count, current.take());
    out[0..4].copy_from_slice(&count.to_le_bytes());
    out[4..8].copy_from_slice(&chars_on_page.to_le_bytes());
    Some(out)
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
        let read = |h: u64| {
            let doc = lock(&core().documents).get(&h).cloned().unwrap();
            let r = lock(&doc).pages().get(0).unwrap().rotation().unwrap();
            r
        };

        assert_eq!(rotate_page(handle, 0, 90), STATUS_OK_PDFIUM);
        assert_eq!(read(handle), PdfPageRenderRotation::Degrees90);

        // ADDITIVE: a second 90 lands at 180, not stuck at 90. This is the
        // behaviour a rotate control needs and the old absolute-set lacked.
        assert_eq!(rotate_page(handle, 0, 90), STATUS_OK_PDFIUM);
        assert_eq!(read(handle), PdfPageRenderRotation::Degrees180);

        // Counter-clockwise (-90) from 180 lands at 90.
        assert_eq!(rotate_page(handle, 0, -90), STATUS_OK_PDFIUM);
        assert_eq!(read(handle), PdfPageRenderRotation::Degrees90);

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

    // ---- Page organising: rebuild_page_order ----

    #[test]
    fn reordering_pages_moves_the_right_page_and_keeps_the_rest() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // Move page 0 ("Page 1 of 20") to the end of a 3-page rebuild: [1, 2, 0].
        let order = [1i32, 2, 0];
        assert_eq!(rebuild_page_order(h, order.as_ptr(), order.len()), STATUS_OK_PDFIUM);

        assert_eq!(get_page_count(h), 3);
        assert_eq!(page_text(h, 0), "Page 2 of 20");
        assert_eq!(page_text(h, 1), "Page 3 of 20");
        assert_eq!(page_text(h, 2), "Page 1 of 20");

        close_document(h);
    }

    #[test]
    fn reordering_carries_a_pages_annotations_with_it() {
        // THE question the whole feature hinges on: does importing a page bring
        // its annotations along? If not, reordering would silently drop every
        // mark on a moved page.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // Put a distinctive text box on page 0.
        let t = "MOVE ME".as_bytes();
        assert_eq!(
            add_text_box_annotation(h, 0, 1000, 100.0, 100.0, 500.0, 200.0,
                t.as_ptr(), t.len(), 24.0, 200, 0, 0, 255),
            STATUS_OK_PDFIUM);
        assert_eq!(read_annotations(h, 0).len(), 1);
        // Render so the appearance stream exists before the page is copied.
        free_render_result(render_region(h, 0, 0.0, 0.0, 1.0, 1.0, 200));

        // Move page 0 to index 2: [1, 2, 0].
        let order = [1i32, 2, 0];
        assert_eq!(rebuild_page_order(h, order.as_ptr(), order.len()), STATUS_OK_PDFIUM);

        // The annotation is now on the LAST page, and nowhere else.
        assert_eq!(read_annotations(h, 2).len(), 1, "the text box did not travel with its page");
        assert_eq!(read_annotations(h, 0).len(), 0, "an annotation was left on the wrong page");

        // And it still draws.
        let base = {
            let clean = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let b = marked_pixels(clean, 1); // page index 1 == "Page 2 of 20", now at new index 0
            close_document(clean);
            b
        };
        assert!(marked_pixels(h, 2) > base, "the moved page's text box drew nothing");

        close_document(h);
    }

    #[test]
    fn a_page_can_be_duplicated_by_repeating_its_index() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // [0, 0, 1]: page 0 twice, then page 1.
        let order = [0i32, 0, 1];
        assert_eq!(rebuild_page_order(h, order.as_ptr(), order.len()), STATUS_OK_PDFIUM);

        assert_eq!(get_page_count(h), 3);
        assert_eq!(page_text(h, 0), "Page 1 of 20");
        assert_eq!(page_text(h, 1), "Page 1 of 20");
        assert_eq!(page_text(h, 2), "Page 2 of 20");

        close_document(h);
    }

    #[test]
    fn the_document_handle_survives_a_rebuild() {
        // The app keeps using the same handle after a reorder, so it must stay
        // valid and keep rendering.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let order = [2i32, 1, 0];
        assert_eq!(rebuild_page_order(h, order.as_ptr(), order.len()), STATUS_OK_PDFIUM);

        let r = render_low_res(h, 0, 150);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        free_render_result(r);

        close_document(h);
    }

    #[test]
    fn rebuild_rejects_a_bad_index_or_empty_order() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let out_of_range = [0i32, 99];
        assert_eq!(rebuild_page_order(h, out_of_range.as_ptr(), 2), STATUS_INVALID_INPUT);

        let negative = [0i32, -1];
        assert_eq!(rebuild_page_order(h, negative.as_ptr(), 2), STATUS_INVALID_INPUT);

        let order = [0i32];
        assert_eq!(rebuild_page_order(h, order.as_ptr(), 0), STATUS_INVALID_INPUT);
        assert_eq!(rebuild_page_order(0, order.as_ptr(), 1), STATUS_INVALID_INPUT);

        // The document is untouched after a rejected rebuild.
        assert_eq!(get_page_count(h), 20);

        close_document(h);
    }

    // ---- Insert and extract ----

    /// Builds a small source PDF (its first `n` pages) as bytes, for insert tests.
    fn small_pdf_bytes(n: usize) -> Vec<u8> {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let order: Vec<i32> = (0..n as i32).collect();
        assert_eq!(rebuild_page_order(h, order.as_ptr(), order.len()), STATUS_OK_PDFIUM);
        let saved = snapshot_document(h);
        let bytes = unsafe { std::slice::from_raw_parts(saved.data, saved.len) }.to_vec();
        free_byte_buffer(saved);
        close_document(h);
        bytes
    }

    #[test]
    fn insert_pages_from_bytes_puts_them_at_the_right_spot() {
        let src = small_pdf_bytes(2); // "Page 1 of 20", "Page 2 of 20"
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // Insert the two-page source at index 5.
        assert_eq!(insert_pages_from_bytes(h, src.as_ptr(), src.len(), 5), 2);
        assert_eq!(get_page_count(h), 22);

        // Pages 5 and 6 are the inserted ones; page 7 is what used to be page 5.
        assert_eq!(page_text(h, 5), "Page 1 of 20");
        assert_eq!(page_text(h, 6), "Page 2 of 20");
        assert_eq!(page_text(h, 7), "Page 6 of 20");
        // Before the insert point is untouched.
        assert_eq!(page_text(h, 4), "Page 5 of 20");

        close_document(h);
    }

    #[test]
    fn inserting_past_the_end_appends() {
        let src = small_pdf_bytes(1);
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(insert_pages_from_bytes(h, src.as_ptr(), src.len(), 9999), 1);
        assert_eq!(get_page_count(h), 21);
        assert_eq!(page_text(h, 20), "Page 1 of 20"); // appended last
        close_document(h);
    }

    #[test]
    fn insert_rejects_bad_input() {
        let src = small_pdf_bytes(1);
        assert_eq!(insert_pages_from_bytes(0, src.as_ptr(), src.len(), 0), -1);
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(insert_pages_from_bytes(h, std::ptr::null(), 0, 0), -1);
        // Not a PDF at all: rejected, document untouched.
        let junk = b"not a pdf".to_vec();
        assert_eq!(insert_pages_from_bytes(h, junk.as_ptr(), junk.len(), 0), -1);
        assert_eq!(get_page_count(h), 20);
        close_document(h);
    }

    #[test]
    fn a_blank_page_is_inserted_and_is_actually_blank() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(insert_blank_page(h, 3, 612.0, 792.0), STATUS_OK_PDFIUM);
        assert_eq!(get_page_count(h), 21);

        // The new page 3 draws (almost) nothing; the old page 3 shifted to 4.
        assert!(marked_pixels(h, 3) < 50, "the inserted page is not blank");
        assert_eq!(page_text(h, 4), "Page 4 of 20");

        close_document(h);
    }

    #[test]
    fn extract_pages_writes_a_new_file_and_leaves_the_original_alone() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let mut out = std::env::temp_dir();
        out.push(format!("render_core_extract_{}_{}.pdf", std::process::id(), line!()));
        let out_str = out.to_str().unwrap().to_owned();
        let c_path = std::ffi::CString::new(out_str.clone()).unwrap();

        // Extract pages 2 and 3 ("Page 3", "Page 4").
        let indices = [2i32, 3];
        assert_eq!(
            extract_pages_to_file(h, indices.as_ptr(), indices.len(), c_path.as_ptr()),
            STATUS_OK_PDFIUM);

        // The original is unchanged.
        assert_eq!(get_page_count(h), 20);
        close_document(h);

        // The extracted file has exactly those two pages, in order.
        let extracted = open_fixture_named(&out_str);
        assert_eq!(get_page_count(extracted), 2);
        assert_eq!(page_text(extracted, 0), "Page 3 of 20");
        assert_eq!(page_text(extracted, 1), "Page 4 of 20");
        close_document(extracted);

        let _ = std::fs::remove_file(&out_str);
    }

    #[test]
    fn extract_rejects_a_bad_index() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let c_path = std::ffi::CString::new(std::env::temp_dir().join("nope.pdf").to_str().unwrap()).unwrap();
        let bad = [0i32, 99];
        assert_eq!(extract_pages_to_file(h, bad.as_ptr(), 2, c_path.as_ptr()), STATUS_INVALID_INPUT);
        assert_eq!(get_page_count(h), 20);
        close_document(h);
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
    fn saving_over_the_open_file_is_what_plain_save_has_to_do() {
        // Save As always writes somewhere new. Save writes back to the path the
        // document was loaded FROM, and open_document uses load_pdf_from_file,
        // which reads lazily and therefore keeps that file open. Whether the
        // write succeeds decides the design: write in place, or write a temp
        // file beside it and replace.
        //
        // Answering this in a test rather than in the app matters because the
        // failure mode is losing the user's file.
        let mut path = std::env::temp_dir();
        path.push(format!("render_core_save_in_place_{}_{}.pdf", std::process::id(), line!()));
        std::fs::copy("tests/fixtures/sample_20pages.pdf", &path).unwrap();
        let path_str = path.to_str().unwrap().to_owned();

        let handle = open_fixture_named(&path_str);
        assert_eq!(delete_page(handle, 0), STATUS_OK_PDFIUM);

        let c_path = std::ffi::CString::new(path_str.clone()).unwrap();
        let status = save_document(handle, c_path.as_ptr());
        close_document(handle);

        // It reports success, and the page count is right.
        assert_eq!(status, STATUS_OK_PDFIUM);

        let reopened = open_fixture_named(&path_str);
        assert_eq!(get_page_count(reopened), 19);

        // And the page is EMPTY. This is the whole point of the test.
        //
        // PDFium streams page content from the file it still has open, so
        // writing the document back over that same file pulls the source out
        // from under the writer: the structure is rewritten, the content
        // streams are not, and every page comes out blank. Nothing reports an
        // error, and a corrupted file looks fine until you open it.
        //
        // So Save CANNOT be SaveDocumentAs(current_path). It has to write a
        // temp file beside the target, close the document to release the file,
        // then replace and reopen. This test asserts the hazard rather than the
        // fix, so that anyone who later "simplifies" Save into a direct
        // overwrite is stopped here instead of in a user's documents.
        assert_eq!(
            page_text(reopened, 0),
            "",
            "in-place save no longer destroys page content; \
             re-check whether Save still needs the temp-file dance"
        );

        close_document(reopened);
        let _ = std::fs::remove_file(&path);
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
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        }
    }

    #[test]
    fn a_rotated_shape_records_its_angle_and_can_be_rotated_again() {
        // A shape is added at 0 deg, rotated to 45 via the FFI, and its tag is
        // re-read. The tag's rotation field must come back at 45, and a second
        // rotate to 90 must overwrite (not compound) the angle. This is the
        // whole round-trip the app relies on for a shape's rotate handle.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 400.0, 200.0)];
        assert_eq!(
            add_shape_annotations(handle, 1000, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        // First rotate to 45.
        let mut new_index = -1;
        assert_eq!(
            rotate_shape_annotation(handle, 0, 0, 1000, 45.0, &mut new_index),
            STATUS_OK_PDFIUM
        );
        let contents = contents_of(handle, 0, new_index as usize)
            .expect("rotated shape has no tag");
        let parsed = parse_shape_tag(&contents).expect("rotated shape tag should parse");
        assert!((parsed.8 - 45.0).abs() < 0.01,
            "rotate did not record 45 in the tag: {}", parsed.8);

        // Rotate again to 90; should REPLACE not accumulate.
        let mut newer = -1;
        assert_eq!(
            rotate_shape_annotation(handle, 0, new_index, 1000, 90.0, &mut newer),
            STATUS_OK_PDFIUM
        );
        let contents2 = contents_of(handle, 0, newer as usize)
            .expect("re-rotated shape has no tag");
        let parsed2 = parse_shape_tag(&contents2).expect("re-rotated shape tag should parse");
        assert!((parsed2.8 - 90.0).abs() < 0.01,
            "rotate did not overwrite the angle: {}", parsed2.8);

        close_document(handle);
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
        // A STAMP, not Ink. Both render from the objects added to them, but an
        // /Ink annotation without an /InkList is malformed, and Acrobat will
        // not let the user select or move one. Shapes were Ink until v2.3.1
        // and were untouchable in Acrobat while text boxes, already stamps,
        // behaved normally.
        assert_eq!(before[0].1, ANNOT_STAMP);

        // Render before saving: this is what makes PDFium generate the
        // appearance stream, and without one a reopened file draws nothing.
        // The highlight path learned this the hard way.
        let drawn = marked_pixels(handle, 0);

        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        let after = read_annotations(reopened, 0);
        assert_eq!(after.len(), 1, "the shape should still be an annotation after reopening");
        assert_eq!(after[0].1, ANNOT_STAMP, "and still a mark");

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
        let (_, _, (lx, ly), (rx, ry)) = arrow_parts(0.0, 0.0, 100.0, 0.0, 2.0);

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
        let (_, _, (lx, _), (rx, _)) = arrow_parts(100.0, 0.0, 0.0, 0.0, 2.0);
        assert!(lx > 0.0 && rx > 0.0, "barbs at x={lx} and x={rx} did not follow the reversed drag");
    }

    #[test]
    fn a_zero_length_arrow_does_not_produce_nan_coordinates() {
        // A click without a drag. Dividing by a zero-length shaft would write
        // NaN into the file, which corrupts the page rather than failing.
        let (_, _, (lx, ly), (rx, ry)) = arrow_parts(50.0, 50.0, 50.0, 50.0, 2.0);
        for v in [lx, ly, rx, ry] {
            assert!(v.is_finite(), "arrow head produced a non-finite coordinate: {v}");
        }
    }

    #[test]
    fn a_hairline_arrow_still_gets_a_visible_head() {
        // Barb length scales with stroke width, so without a floor the finest
        // pen would produce a head a fraction of a point across: invisible.
        let (_, _, (lx, ly), _) = arrow_parts(0.0, 0.0, 100.0, 0.0, 0.01);
        let reach = ((100.0f32 - lx).powi(2) + ly.powi(2)).sqrt();
        assert!(reach >= ARROW_HEAD_MIN - 0.001, "hairline arrow head reached only {reach}");
    }

    // ---- Text boxes: real vector text inside a stamp annotation ----

    fn add_box(handle: u64, text: &str, size: f32) -> i32 {
        let bytes = text.as_bytes();
        add_text_box_annotation(
            handle, 0, 1000, 100.0, 100.0, 700.0, 400.0,
            bytes.as_ptr(), bytes.len(), size, 20, 20, 20, 255,
        )
    }

    #[test]
    #[ignore]
    fn dump_text_box_for_inspection() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let para = "This paragraph is here to show wrapping and alignment inside a filled, outlined box.";
        let b = para.as_bytes();

        // A styled box for each alignment, stacked down the page: fill (pale
        // yellow), outline (blue, 2px), and left / center / right / justify.
        let fill = 0xFFF7C8u32 << 8 | 0xFF; // RRGGBBAA = FFF7C8FF
        let outline = 0x1565C0u32 << 8 | 0xFF; // 1565C0FF
        for (row, align) in [ALIGN_LEFT, ALIGN_CENTER, ALIGN_RIGHT, ALIGN_JUSTIFY].iter().enumerate() {
            let top = 60.0 + row as f32 * 210.0;
            assert_eq!(
                add_text_box_annotation_styled(handle, 0, 1000, 60.0, top, 520.0, top + 170.0,
                    b.as_ptr(), b.len(), 22.0, 20, 20, 20, 255,
                    *align, fill, outline, 2.0, std::ptr::null(), 0, 0, 0),
                STATUS_OK_PDFIUM);
        }

        // Keep the old single-box path exercised too, unstyled.
        let text = para;
        let b = text.as_bytes();
        let _ = add_text_box_annotation(handle, 0, 1000, 600.0, 80.0, 900.0, 200.0,
                b.as_ptr(), b.len(), 22.0, 20, 20, 20, 255);
        assert_eq!(STATUS_OK_PDFIUM, STATUS_OK_PDFIUM);
        let r = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 900);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        std::fs::write(std::env::var("TB_DUMP").unwrap(), bytes).unwrap();
        println!("DUMP {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    fn a_text_box_actually_draws_its_text() {
        // The whole risk. A /FreeText annotation draws nothing here; this puts
        // real text objects inside a stamp, whose appearance PDFium builds from
        // them. If the text does not draw, this counts zero extra pixels.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let before = marked_pixels(handle, 0);

        assert_eq!(add_box(handle, "Hello Ayaan", 24.0), STATUS_OK_PDFIUM);

        let after = marked_pixels(handle, 0);
        assert!(after > before, "the text box drew nothing: {before} -> {after}");

        close_document(handle);
    }

    #[test]
    fn more_text_draws_more_ink() {
        // A stronger check than "something drew": the words have to be there,
        // so a longer line must cover more of the page than a shorter one.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);
        assert_eq!(add_box(handle, "Hi", 24.0), STATUS_OK_PDFIUM);
        let short = marked_pixels(handle, 0) - base;
        close_document(handle);

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);
        assert_eq!(add_box(handle, "Hi there, this is a much longer sentence", 24.0), STATUS_OK_PDFIUM);
        let long = marked_pixels(handle, 0) - base;
        close_document(handle);

        assert!(long > short, "the longer text drew {long}, the shorter {short}");
    }

    #[test]
    fn a_multi_line_box_draws_each_line() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);
        assert_eq!(add_box(handle, "One", 20.0), STATUS_OK_PDFIUM);
        let one = marked_pixels(handle, 0) - base;
        close_document(handle);

        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let base = marked_pixels(handle, 0);
        assert_eq!(add_box(handle, "One\nTwo\nThree", 20.0), STATUS_OK_PDFIUM);
        let three = marked_pixels(handle, 0) - base;
        close_document(handle);

        assert!(three > one * 2, "three lines drew {three}, one line {one}");
    }

    #[test]
    fn a_text_box_is_still_text_after_a_save_and_reopen() {
        // The tag is what lets a reopened box be edited again rather than only
        // moved. It must survive the round trip, text and all.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_box(handle, "Line one\nLine two", 18.0), STATUS_OK_PDFIUM);

        free_render_result(render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 200));
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        // Still draws.
        let base = {
            let clean = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let b = marked_pixels(clean, 0);
            close_document(clean);
            b
        };
        assert!(marked_pixels(reopened, 0) > base, "the reopened text box drew nothing new");

        // And its words come back.
        let contents = contents_of(reopened, 0, 0).expect("reopened box has no contents");
        let parsed = parse_textbox_tag(&contents).expect("tag did not parse");
        assert_eq!(parsed.5, "Line one\nLine two");
        assert!((parsed.0 - 18.0).abs() < 0.01, "font size came back as {}", parsed.0);

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }

    #[test]
    fn long_text_wraps_and_grows_the_box_taller() {
        // The point of wrapping: a long paragraph in a fixed-width box occupies
        // several lines and the box grows to fit, rather than the text running
        // off the right edge on one line.
        let short_h = {
            let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let t = "Short.".as_bytes();
            assert_eq!(
                add_text_box_annotation(h, 0, 1000, 100.0, 100.0, 500.0, 140.0,
                    t.as_ptr(), t.len(), 20.0, 0, 0, 0, 255),
                STATUS_OK_PDFIUM);
            let a = read_annotations(h, 0);
            let height = a[0].5 - a[0].3; // bottom - top, normalized
            close_document(h);
            height
        };

        let long_h = {
            let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let t = "This is a much longer paragraph that cannot possibly fit on a single line inside such a narrow box and therefore has to wrap across several lines.".as_bytes();
            assert_eq!(
                add_text_box_annotation(h, 0, 1000, 100.0, 100.0, 500.0, 140.0,
                    t.as_ptr(), t.len(), 20.0, 0, 0, 0, 255),
                STATUS_OK_PDFIUM);
            let a = read_annotations(h, 0);
            let height = a[0].5 - a[0].3;
            close_document(h);
            height
        };

        assert!(
            long_h > short_h * 2.0,
            "the long paragraph box ({long_h}) is not much taller than the short one ({short_h}); wrapping did not happen"
        );
    }

    #[test]
    fn a_box_never_shrinks_below_the_height_it_was_dragged() {
        // A tall box the user drew with little text stays tall, so drag-to-size
        // is honoured rather than always snapping to content height.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let t = "Hi".as_bytes();
        // Dragged from y=100 to y=500: a deliberately tall box.
        assert_eq!(
            add_text_box_annotation(h, 0, 1000, 100.0, 100.0, 500.0, 500.0,
                t.as_ptr(), t.len(), 20.0, 0, 0, 0, 255),
            STATUS_OK_PDFIUM);
        let a = read_annotations(h, 0);
        let height = a[0].5 - a[0].3;
        // The drag was 400px of a 1000px capture width = 0.4 normalized.
        assert!(height > 0.35, "the tall drag collapsed to {height}");
        close_document(h);
    }

    // ---- Styled text boxes: alignment, fill, outline ----

    /// The rendered page, as raw BGRA bytes at the given width.
    fn render_bytes(handle: u64, page: i32, width: i32) -> (Vec<u8>, usize) {
        let r = render_region(handle, page, 0.0, 0.0, 1.0, 1.0, width);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) }.to_vec();
        let w = r.width as usize;
        free_render_result(r);
        (bytes, w)
    }

    #[test]
    fn left_and_right_aligned_boxes_render_differently() {
        // The clearest proof alignment takes effect: identical text and box,
        // only the alignment differs, so the pages must not be identical.
        let text = "one two three four five six".as_bytes();

        let make = |align: i32| {
            let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            assert_eq!(
                add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 800.0, 200.0,
                    text.as_ptr(), text.len(), 20.0, 0, 0, 0, 255, align, 0, 0, 0.0,
                    std::ptr::null(), 0, 0, 0),
                STATUS_OK_PDFIUM);
            let (bytes, _) = render_bytes(h, 0, 400);
            close_document(h);
            bytes
        };

        assert_ne!(make(ALIGN_LEFT), make(ALIGN_RIGHT),
            "left and right aligned text rendered identically");
    }

    #[test]
    fn a_filled_box_paints_its_background() {
        // A box with a red fill must actually put red where the page was white.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let text = "x".as_bytes();
        // Solid red fill: RRGGBBAA = FF0000FF.
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 300.0,
                text.as_ptr(), text.len(), 20.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0xFF0000FF, 0, 0.0, std::ptr::null(), 0, 0, 0),
            STATUS_OK_PDFIUM);

        let (bytes, w) = render_bytes(h, 0, 400);
        // A point well inside the box (normalized ~0.3, 0.12) but away from the
        // text, sampled as BGRA.
        let px = ((0.12 * 400.0) as usize * w + (0.30 * 400.0) as usize) * 4;
        let (b, g, r) = (bytes[px], bytes[px + 1], bytes[px + 2]);
        close_document(h);

        assert!(r > 180 && g < 90 && b < 90, "fill sampled B={b} G={g} R={r}, not red");
    }

    /// Counts near-black pixels in a normalized band of a 400px-wide render of
    /// page 0, so a test can tell "glyphs were drawn here" from "nothing was".
    fn dark_pixels_in_band(handle: u64, x0: f32, y0: f32, x1: f32, y1: f32) -> usize {
        let (bytes, w) = render_bytes(handle, 0, 400);
        let h = bytes.len() / (w * 4);
        let mut dark = 0;
        for y in (y0 * 400.0) as usize..(y1 * 400.0) as usize {
            for x in (x0 * 400.0) as usize..(x1 * 400.0) as usize {
                if y >= h || x >= w {
                    continue;
                }
                let px = (y * w + x) * 4;
                if bytes[px] < 110 && bytes[px + 1] < 110 && bytes[px + 2] < 110 {
                    dark += 1;
                }
            }
        }
        dark
    }

    #[test]
    fn a_text_box_in_a_real_font_renders_cyrillic_glyphs() {
        // With a real embedded font, non-Latin text actually puts ink on the
        // page (visually confirmed to be correct Cyrillic in the ignored
        // dump_unicode_text test). An opaque white fill isolates the box from
        // the page content beneath, so the dark pixels counted are the glyphs.
        let font = "C:\\Windows\\Fonts\\arial.ttf";
        if !std::path::Path::new(font).exists() {
            return; // a machine without Arial: nothing to prove here
        }
        let text = "Привет мир".as_bytes();
        let white_fill = 0xFFFFFFFFu32;

        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(
                h, 0, 900, 60.0, 60.0, 620.0, 170.0,
                text.as_ptr(), text.len(), 44.0, 0, 0, 0, 255,
                ALIGN_LEFT, white_fill, 0, 0.0, font.as_ptr(), font.len(), 0, 0),
            STATUS_OK_PDFIUM);
        let ink = dark_pixels_in_band(h, 0.10, 0.11, 0.55, 0.16);
        close_document(h);

        assert!(ink > 150, "expected Cyrillic glyphs drawn on the box, found {ink} dark px");
    }

    #[test]
    #[ignore]
    fn dump_unicode_text_for_inspection() {
        // Renders mixed non-Latin text in a real font, to LOOK at the glyphs.
        // Run: cargo test --release dump_unicode_text -- --ignored --nocapture
        let font = "C:\\Windows\\Fonts\\arial.ttf";
        let text = "Привет мир  Γειά σου  Ünïcödé  1234".as_bytes();
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(
                handle, 0, 900, 60.0, 60.0, 840.0, 200.0,
                text.as_ptr(), text.len(), 40.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, font.as_ptr(), font.len(), 0, 0),
            STATUS_OK_PDFIUM);

        let r = render_low_res(handle, 0, 900);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("UNI_DUMP").unwrap_or_else(|_| "uni.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    #[ignore]
    fn dump_underline_strikethrough_for_inspection() {
        // Run: cargo test --release dump_underline -- --ignored --nocapture
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let t1 = "Underlined text".as_bytes();
        assert_eq!(
            add_text_box_annotation_styled(handle, 0, 900, 60.0, 60.0, 700.0, 130.0,
                t1.as_ptr(), t1.len(), 36.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0xFFFFFFFF, 0, 0.0, std::ptr::null(), 0, 1, 0),
            STATUS_OK_PDFIUM);

        let t2 = "Struck through".as_bytes();
        assert_eq!(
            add_text_box_annotation_styled(handle, 0, 900, 60.0, 150.0, 700.0, 220.0,
                t2.as_ptr(), t2.len(), 36.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0xFFFFFFFF, 0, 0.0, std::ptr::null(), 0, 0, 1),
            STATUS_OK_PDFIUM);

        let r = render_low_res(handle, 0, 900);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("DECO_DUMP").unwrap_or_else(|_| "deco.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    #[ignore]
    fn dump_burmese_shaped_for_inspection() {
        // Run: cargo test --release dump_burmese -- --ignored --nocapture
        let font = "C:\\Windows\\Fonts\\mmrtext.ttf";
        let text = "ကောင်းကင်ပြြပြအောက် ငှက်ကလေးတွေ တေးသီနေကြသည်။".as_bytes();
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(handle, 0, 900, 40.0, 40.0, 560.0, 340.0,
                text.as_ptr(), text.len(), 40.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0xFFFFFFFF, 0, 0.0, font.as_ptr(), font.len(), 0, 0),
            STATUS_OK_PDFIUM);

        let r = render_low_res(handle, 0, 900);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("MM_DUMP").unwrap_or_else(|_| "mm.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    fn needs_shaping_flags_only_complex_scripts() {
        assert!(!needs_shaping("Hello, world 123"));
        assert!(!needs_shaping("Привет мир"));   // Cyrillic maps 1:1, no shaping
        assert!(!needs_shaping("日本語"));         // CJK maps 1:1
        assert!(needs_shaping("ကောင်း"));          // Myanmar
        assert!(needs_shaping("नमस्ते"));           // Devanagari
        assert!(needs_shaping("مرحبا"));           // Arabic
        assert!(needs_shaping("Mixed नमस्ते text")); // any complex char triggers it
    }

    #[test]
    fn shape_run_turns_burmese_into_glyphs() {
        let font = "C:\\Windows\\Fonts\\mmrtext.ttf";
        if !std::path::Path::new(font).exists() {
            return; // no Myanmar font on this machine
        }
        let bytes = std::fs::read(font).unwrap();
        let glyphs = shape_run(&bytes, "ကောင်း", 44.0).expect("shaping produced nothing");
        assert!(!glyphs.is_empty(), "no glyphs");
        assert!(shaped_width(&glyphs) > 0.0, "zero advance width");
        // Shaping substitutes real glyphs; none should be .notdef.
        assert!(glyphs.iter().all(|g| g.id != 0), "a .notdef glyph slipped through");
    }

    #[test]
    fn underline_draws_a_rule_below_the_text() {
        // An underlined box has ink in the strip just below where the text sits
        // that a plain box does not. White fill isolates the box from the page.
        let text = "Test".as_bytes();
        let make = |underline: i32| {
            let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            add_text_box_annotation_styled(h, 0, 900, 60.0, 60.0, 400.0, 150.0,
                text.as_ptr(), text.len(), 44.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0xFFFFFFFF, 0, 0.0, std::ptr::null(), 0, underline, 0);
            // The underline rule sits just below the baseline, inside the box
            // (box bottom is norm ~0.167 here). Sample that strip.
            let n = dark_pixels_in_band(h, 0.09, 0.135, 0.42, 0.16);
            close_document(h);
            n
        };
        assert!(make(1) > make(0) + 20, "underline should add ink below the text");
    }

    #[test]
    fn a_styled_box_keeps_its_style_tag_across_a_reopen() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let text = "styled".as_bytes();
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 600.0, 260.0,
                text.as_ptr(), text.len(), 22.0, 20, 20, 20, 255,
                ALIGN_CENTER, 0xFFF7C8FF, 0x1565C0FF, 2.0, std::ptr::null(), 0, 0, 0),
            STATUS_OK_PDFIUM);

        free_render_result(render_region(h, 0, 0.0, 0.0, 1.0, 1.0, 200));
        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        // The styled tag round-trips, and still parses back to the same words.
        let contents = contents_of(reopened, 0, 0).expect("no contents after reopen");
        assert!(contents.starts_with("AyaanTextB:"), "styled tag lost: {contents}");
        assert_eq!(parse_textbox_tag(&contents).map(|t| t.5), Some("styled".to_string()));

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    #[test]
    fn the_styled_tag_records_the_font_and_decorations_for_re_editing() {
        // Re-opening a box must bring it back in the SAME font with the same
        // underline/strikethrough, so the tag records the font FILE and a
        // decorations flag. Without this a Burmese/Hindi box would fall back to
        // the default font on re-commit and its shaping would break.
        let style = TextStyle {
            align: ALIGN_LEFT,
            fill: PackedRgba(0),
            outline: PackedRgba(0),
            outline_width_px: 0.0,
            underline: true,
            strikethrough: false,
            rotation_deg: 0.0,
        };
        let tag = textbox_tag_styled("hi", 20.0, 0, 0, 0, 255, style, Some(r"C:\Fonts\Noto Sans.ttf"),
            [0.1, 0.1, 0.5, 0.2]);

        // The words are still the last field, so they still parse.
        assert_eq!(parse_textbox_tag(&tag).map(|t| t.5), Some("hi".to_string()));

        // flags = underline(1) | strikethrough<<1(0) = 1, sitting just before the
        // base64 font path, which itself sits just before the words.
        let path_b64 = base64_encode(r"C:\Fonts\Noto Sans.ttf".as_bytes());
        assert!(
            tag.contains(&format!(":1:{path_b64}:")),
            "decoration flag / font order wrong: {tag}"
        );

        // A default-font, undecorated box records an empty font field and flag 0,
        // and still round-trips its words.
        let plain = textbox_tag_styled("bye", 20.0, 0, 0, 0, 255, TextStyle::plain(), None,
            [0.0, 0.0, 0.4, 0.1]);
        assert!(plain.contains(":0::"), "empty font / zero flags expected: {plain}");
        assert_eq!(parse_textbox_tag(&plain).map(|t| t.5), Some("bye".to_string()));
    }

    #[test]
    fn get_annotation_contents_reads_a_text_box_back_across_the_boundary() {
        // The app reads this to re-open the editor on an existing box. It has
        // to return the exact tag bytes, or the words come back wrong.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_box(handle, "edit me\nplease", 20.0), STATUS_OK_PDFIUM);

        let buf = get_annotation_contents(handle, 0, 0);
        assert_eq!(buf.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
        let contents = std::str::from_utf8(bytes).unwrap().to_string();
        free_byte_buffer(buf);

        let parsed = parse_textbox_tag(&contents).expect("tag did not parse");
        assert_eq!(parsed.5, "edit me\nplease");

        close_document(handle);
    }

    #[test]
    fn resizing_a_text_box_re_wraps_it_taller_rather_than_scaling() {
        // Word behaviour: making a text box NARROWER re-flows its words onto more
        // lines and grows the box DOWN, rather than squashing the same glyphs into
        // a thinner space. Scaling a rendered box into the new rect would keep its
        // height; only RE-LAYING-OUT makes a narrowed box taller. That is the whole
        // difference between a text box and a stretched picture.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let text = b"the quick brown fox jumps over the lazy dog again and again and again";

        // A very wide, short box: its height is content-driven, so it reflects the
        // number of wrapped lines (one, here).
        assert_eq!(
            add_text_box_annotation(handle, 0, 1000, 50.0, 50.0, 900.0, 80.0,
                text.as_ptr(), text.len(), 18.0, 0, 0, 0, 255),
            STATUS_OK_PDFIUM);
        let wide = read_annotations(handle, 0);
        assert_eq!(wide.len(), 1);
        let wide_h = wide[0].5 - wide[0].3;

        // Narrow it hard, same top and dragged height.
        let mut new_index = -1;
        assert_eq!(
            resize_text_box_annotation(handle, 0, 0, 1000, 50.0, 50.0, 200.0, 80.0, &mut new_index),
            STATUS_OK_PDFIUM);
        let narrow = read_annotations(handle, 0);
        assert_eq!(narrow.len(), 1, "resize must leave exactly one box, not a duplicate");
        let narrow_h = narrow[0].5 - narrow[0].3;

        assert!(
            narrow_h > wide_h + 0.02,
            "narrowing should re-wrap TALLER (re-flow), not scale: wide_h={wide_h} narrow_h={narrow_h}"
        );

        // Still one of our text boxes, with the same words: re-laid-out, not
        // rasterized into pixels.
        let contents = contents_of(handle, 0, new_index as usize).expect("resized box lost its tag");
        assert!(contents.starts_with("AyaanTextB:"), "resized box is no longer a text box: {contents}");
        assert_eq!(
            parse_textbox_tag(&contents).map(|t| t.5),
            Some(String::from_utf8_lossy(text).to_string())
        );

        close_document(handle);
    }

    #[test]
    fn a_rotated_box_enlarges_its_rect_and_records_the_angle() {
        // A rotated square needs a bigger axis-aligned rect to hold it (its
        // diagonal), so the annotation the core writes for a turned box is wider
        // AND taller than the same box upright. That enlargement is what stops
        // PDFium cropping the turned corners. The angle also round-trips in the tag
        // so a resize or re-edit keeps it.
        let place = |deg: f32| {
            let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
            let mut style = TextStyle::plain();
            style.rotation_deg = deg;
            // A near-square box (equal capture width and height) and short text, so
            // the height stays the dragged size and a 45-degree turn grows both.
            assert_eq!(
                add_text_box_inner(handle, 0, 1000, 200.0, 200.0, 400.0, 400.0,
                    "turn", 24.0, 0, 0, 0, 255, style, None),
                STATUS_OK_PDFIUM);
            let a = read_annotations(handle, 0);
            assert_eq!(a.len(), 1);
            let dims = (a[0].4 - a[0].2, a[0].5 - a[0].3);
            close_document(handle);
            dims
        };

        let (up_w, up_h) = place(0.0);
        let (rot_w, rot_h) = place(45.0);
        assert!(rot_w > up_w * 1.2, "a 45-degree box should be much wider: up={up_w} rot={rot_w}");
        assert!(rot_h > up_h * 1.2, "a 45-degree box should be much taller: up={up_h} rot={rot_h}");

        // The angle survives in the tag.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let mut style = TextStyle::plain();
        style.rotation_deg = 45.0;
        add_text_box_inner(handle, 0, 1000, 200.0, 200.0, 400.0, 400.0,
            "turn", 24.0, 0, 0, 0, 255, style, None);
        let contents = contents_of(handle, 0, 0).expect("rotated box has no tag");
        let parsed = parse_textbox_tag_full(&contents).expect("rotated box tag should parse");
        assert!((parsed.style.rotation_deg - 45.0).abs() < 0.01,
            "rotation lost in the tag: {}", parsed.style.rotation_deg);
        close_document(handle);
    }

    #[test]
    fn resizing_a_rotated_box_keeps_its_angle() {
        // Resizing re-lays-the-box-out from its tag, which carries the angle, so a
        // box that was turned stays turned after it is resized.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let mut style = TextStyle::plain();
        style.rotation_deg = 30.0;
        assert_eq!(
            add_text_box_inner(handle, 0, 1000, 200.0, 200.0, 500.0, 300.0,
                "keep my angle", 22.0, 0, 0, 0, 255, style, None),
            STATUS_OK_PDFIUM);

        let mut new_index = -1;
        assert_eq!(
            resize_text_box_annotation(handle, 0, 0, 1000, 200.0, 200.0, 360.0, 300.0, &mut new_index),
            STATUS_OK_PDFIUM);

        let contents = contents_of(handle, 0, new_index as usize).expect("resized box has no tag");
        let parsed = parse_textbox_tag_full(&contents).expect("resized box tag should parse");
        assert!((parsed.style.rotation_deg - 30.0).abs() < 0.01,
            "resize dropped the rotation: {}", parsed.style.rotation_deg);
        close_document(handle);
    }

    #[test]
    fn rotate_text_box_annotation_sets_a_new_angle() {
        // What the overlay's rotate handle calls: re-lay-the-box-out at its own
        // upright bounds with a NEW absolute angle. The box turns (its rect grows)
        // and the tag records the angle, while the words are kept.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_box(handle, "spin me", 22.0), STATUS_OK_PDFIUM);

        let mut new_index = -1;
        assert_eq!(
            rotate_text_box_annotation(handle, 0, 0, 1000, 100.0, 100.0, 500.0, 200.0, 25.0, &mut new_index),
            STATUS_OK_PDFIUM);

        let contents = contents_of(handle, 0, new_index as usize).expect("rotated box has no tag");
        let parsed = parse_textbox_tag_full(&contents).expect("rotated box tag should parse");
        assert!((parsed.style.rotation_deg - 25.0).abs() < 0.01,
            "rotate did not set the angle: {}", parsed.style.rotation_deg);
        assert_eq!(parsed.text, "spin me", "rotate must keep the words");

        // A non-text mark is refused so the app does not turn it into text.
        assert!(parse_textbox_tag_full("AyaanShape:0:FF0000FF:2.0:0.1:0.2").is_none());
        close_document(handle);
    }

    #[test]
    #[ignore]
    fn dump_rotated_text_box_for_inspection() {
        // Run: cargo test --release dump_rotated -- --ignored --nocapture
        // then convert the raw BGRA (WxH printed) to PNG and LOOK: the box, its
        // border, and the Burmese text should all be turned about the box centre.
        let font = "C:\\Windows\\Fonts\\mmrtext.ttf";
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let mut style = TextStyle::plain();
        style.rotation_deg = 30.0;
        style.fill = PackedRgba(0xFFFFFFFF);
        style.outline = PackedRgba(0x1565C0FF);
        style.outline_width_px = 2.0;
        add_text_box_inner(handle, 0, 900, 250.0, 200.0, 650.0, 340.0,
            "Rotated မြန်မာ", 40.0, 20, 20, 20, 255, style, Some(font));
        let r = render_low_res(handle, 0, 900);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("ROT_DUMP").unwrap_or_else(|_| "rot.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    fn the_full_text_box_parser_reads_style_and_rejects_other_marks() {
        // The gate that makes resize_text_box_annotation report UNSUPPORTED for a
        // shape or a plain comment, so the app falls back to the generic resize
        // rather than turning that mark into text.
        let styled = textbox_tag_styled(
            "hi",
            18.0,
            10,
            20,
            30,
            255,
            TextStyle {
                align: ALIGN_CENTER,
                fill: PackedRgba(0xFFFFFFFF),
                outline: PackedRgba(0),
                outline_width_px: 0.0,
                underline: true,
                strikethrough: false,
                rotation_deg: 0.0,
            },
            Some(r"C:\Fonts\Noto.ttf"),
            [0.05, 0.05, 0.55, 0.25],
        );
        let p = parse_textbox_tag_full(&styled).expect("our styled tag should parse");
        assert_eq!(p.text, "hi");
        assert_eq!(p.style.align, ALIGN_CENTER);
        assert!(p.style.underline && !p.style.strikethrough);
        assert_eq!(p.font_path.as_deref(), Some(r"C:\Fonts\Noto.ttf"));

        // Not ours.
        assert!(parse_textbox_tag_full("AyaanShape:0:FF0000FF:2.0:0.1:0.2").is_none());
        assert!(parse_textbox_tag_full("Please review this").is_none());
        assert!(parse_textbox_tag_full("").is_none());
    }

    #[test]
    fn get_annotation_contents_is_empty_for_a_mark_with_none_and_rejects_bad_input() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // An out-of-range index is refused, not a crash.
        let bad = get_annotation_contents(handle, 0, 5);
        assert_eq!(bad.status, STATUS_INVALID_INPUT);
        free_byte_buffer(bad);

        assert_eq!(get_annotation_contents(0, 0, 0).status, STATUS_INVALID_INPUT);

        close_document(handle);
    }

    #[test]
    fn the_text_tag_round_trips_awkward_text() {
        // Colons and newlines in the text must not be read as field separators,
        // which is why the text is base64 in the tag.
        for text in ["", "a:b:c", "line\nbreak", "10:30 meeting\nroom: 4B", "unicode \u{2713} \u{00e9}"] {
            let tag = textbox_tag(text, 14.0, 1, 2, 3, 4);
            let parsed = parse_textbox_tag(&tag);
            if text.is_empty() {
                // Empty text still tags, and comes back empty.
                assert_eq!(parsed.map(|p| p.5), Some(String::new()));
            } else {
                assert_eq!(parsed.expect("did not parse").5, text, "lost {text:?}");
            }
        }
    }

    #[test]
    fn a_comment_is_not_mistaken_for_a_text_box() {
        assert!(parse_textbox_tag("Please review").is_none());
        assert!(parse_textbox_tag("AyaanText:").is_none());
        assert!(parse_textbox_tag("AyaanText:14:GGGGGGGG:aGk=").is_none());
        assert!(parse_textbox_tag("AyaanText:0:FFFFFFFF:aGk=").is_none());
    }

    #[test]
    fn text_box_rejects_bad_input() {
        let t = b"hi";
        assert_eq!(
            add_text_box_annotation(0, 0, 1000, 0.0, 0.0, 10.0, 10.0, t.as_ptr(), t.len(), 12.0, 0, 0, 0, 255),
            STATUS_INVALID_INPUT
        );
        let handle = open_fixture();
        // Inverted box.
        assert_eq!(
            add_text_box_annotation(handle, 0, 1000, 50.0, 10.0, 10.0, 50.0, t.as_ptr(), t.len(), 12.0, 0, 0, 0, 255),
            STATUS_INVALID_INPUT
        );
        // Empty text.
        assert_eq!(
            add_text_box_annotation(handle, 0, 1000, 0.0, 0.0, 100.0, 100.0, t.as_ptr(), 0, 12.0, 0, 0, 0, 255),
            STATUS_INVALID_INPUT
        );
        close_document(handle);
    }

    // ---- The tag that keeps a shape a shape ----

    /// The tag the APP sees for an annotation: the private key, falling back
    /// to /Contents. Not a raw /Contents read - the tag moved off the comment
    /// field so readers stop displaying it, and these tests are about whether
    /// the app can still recognise its own marks. For the reader's view (which
    /// must be empty) use raw_contents.
    fn contents_of(handle: u64, page_index: i32, index: usize) -> Option<String> {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned()?;
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(page_index as u16).ok()?;
        let annotation = page.annotations().iter().nth(index)?;
        annotation_tag(&annotation)
    }

    fn glyph(id: u32, cluster: u32) -> ShapedGlyph {
        ShapedGlyph { id, x_advance: 10.0, x_offset: 0.0, y_offset: 0.0, cluster }
    }

    #[test]
    fn one_character_per_glyph_maps_straight_across() {
        // The simple case, and the baseline the harder ones are judged against.
        let glyphs = [glyph(40, 0), glyph(41, 1), glyph(42, 2)];
        assert_eq!(
            glyph_text_map(&glyphs, "abc"),
            vec![(40, "a".into()), (41, "b".into()), (42, "c".into())]
        );
    }

    #[test]
    fn several_glyphs_from_one_character_do_not_repeat_its_text() {
        // A base plus its marks: three glyphs, all cluster 0. Giving the text
        // to each would make copying the line repeat that character three
        // times, which is the classic broken-ToUnicode symptom.
        let glyphs = [glyph(40, 0), glyph(300, 0), glyph(301, 0), glyph(41, 3)];
        assert_eq!(
            glyph_text_map(&glyphs, "\u{1000}b"),
            vec![(40, "\u{1000}".into()), (41, "b".into())]
        );
    }

    #[test]
    fn one_glyph_from_several_characters_carries_all_of_them() {
        // A Burmese stack: MA, SIGN VIRAMA, TA shaped into a single glyph. The
        // whole cluster has to come back or the text is silently truncated on
        // copy. This is exactly why the CMap needs multi-character
        // destinations rather than a one-to-one table.
        let source = "\u{1019}\u{1039}\u{1010}";
        let glyphs = [glyph(500, 0)];
        assert_eq!(glyph_text_map(&glyphs, source), vec![(500, source.to_string())]);
    }

    #[test]
    fn a_right_to_left_run_still_reports_the_right_text() {
        // Arabic shapes into visual order, so the glyphs arrive with DECREASING
        // clusters. Taking each cluster's extent from glyph order rather than
        // from the sorted boundaries would give every glyph the wrong span.
        let source = "\u{0627}\u{0644}\u{0645}"; // alef lam meem
        let glyphs = [glyph(70, 4), glyph(71, 2), glyph(72, 0)];

        let mut got = glyph_text_map(&glyphs, source);
        got.sort_by_key(|(id, _)| *id);
        assert_eq!(
            got,
            vec![
                (70, "\u{0645}".into()),
                (71, "\u{0644}".into()),
                (72, "\u{0627}".into()),
            ]
        );
    }

    #[test]
    fn a_cluster_landing_mid_character_is_dropped_rather_than_panicking() {
        // Byte offsets come from the shaper. A malformed run must not index a
        // String at a non-boundary, which panics.
        let glyphs = [glyph(40, 1)];
        assert!(glyph_text_map(&glyphs, "\u{1019}").is_empty());
    }

    #[test]
    fn the_cmap_declares_two_byte_codes_and_utf16_destinations() {
        let cmap = build_tounicode_cmap(&[(0x1F, "a".into())]);

        // Identity encoding means the code IS the glyph index, two bytes wide.
        assert!(cmap.contains("<0000> <FFFF>"), "codespace missing: {cmap}");
        assert!(cmap.contains("<001F> <0061>"), "mapping missing: {cmap}");
        assert!(cmap.contains("begincmap") && cmap.contains("endcmap"));
        assert!(cmap.contains("/CMapType 2 def"));
    }

    #[test]
    fn a_multi_character_destination_is_written_as_consecutive_utf16_units() {
        // The Burmese stack again, this time through the serializer. Three
        // characters behind one glyph, so the destination is three UTF-16 units
        // end to end.
        let cmap = build_tounicode_cmap(&[(500, "\u{1019}\u{1039}\u{1010}".into())]);
        assert!(cmap.contains("<01F4> <101910391010>"), "{cmap}");
    }

    #[test]
    fn a_destination_outside_the_basic_plane_becomes_a_surrogate_pair() {
        // Emoji and rare CJK live above U+FFFF, and a bfchar destination is
        // UTF-16, so they must come out as two units rather than one truncated
        // one.
        let cmap = build_tounicode_cmap(&[(9, "\u{1F600}".into())]);
        assert!(cmap.contains("<0009> <D83DDE00>"), "{cmap}");
    }

    #[test]
    fn long_runs_are_split_into_sections_of_at_most_a_hundred() {
        // The spec's limit on a bfchar section. Some readers reject a longer
        // one outright, which would lose the whole document's text rather than
        // one glyph's.
        let mappings: Vec<(u32, String)> =
            (0..250u32).map(|i| (i, char::from(b'a' + (i % 26) as u8).to_string())).collect();
        let cmap = build_tounicode_cmap(&mappings);

        assert_eq!(cmap.matches("beginbfchar").count(), 3);
        assert_eq!(cmap.matches("endbfchar").count(), 3);
        assert!(cmap.contains("100 beginbfchar"));
        assert!(cmap.contains("50 beginbfchar"));
    }

    #[test]
    fn a_glyph_mapped_twice_appears_once() {
        // The same glyph is drawn wherever its characters recur, so the
        // accumulated list has duplicates. A CMap with a repeated code is
        // malformed.
        let cmap = build_tounicode_cmap(&[(7, "a".into()), (7, "a".into()), (8, "b".into())]);
        assert_eq!(cmap.matches("<0007>").count(), 1, "{cmap}");
        assert!(cmap.contains("2 beginbfchar"));
    }

    /// A text box on a page, written the way the app writes one.
    fn page_with_text_box(words: &str, font_path: Option<&str>) -> u64 {
        let bytes = words.as_bytes();
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let (fp, fl) = match font_path {
            Some(p) => (p.as_ptr(), p.len()),
            None => (std::ptr::null(), 0),
        };
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 200.0,
                bytes.as_ptr(), bytes.len(), 28.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, fp, fl, 0, 0),
            STATUS_OK_PDFIUM);
        h
    }

    fn text_after_round_trip(handle: u64) -> String {
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let text = page_text(reopened, 0);
        close_document(reopened);
        free_byte_buffer(saved);
        text
    }

    #[test]
    fn a_text_box_is_not_searchable_until_the_layer_is_written() {
        // The starting state, asserted so the rest is measuring something.
        // Our text boxes draw their glyphs inside a stamp annotation, and a
        // page's text layer is its CONTENT stream, so the words are simply not
        // there to be found.
        let h = page_with_text_box("Findable Words", None);
        assert!(
            !text_after_round_trip(h).contains("Findable Words"),
            "a text box was searchable before the layer existed, so this proves nothing");
        close_document(h);
    }

    // ---------------- Searchable text layer ----------------
    //
    // The invariant these all serve: an Ayaan text box stays visually identical
    // and editable in Ayaan, while its CURRENT words are extractable by other
    // readers, with no stale and no duplicate hidden text.

    /// Adds a text box and returns the page handle. `font` picks the embedded
    /// TTF, which complex scripts need for shaping.
    fn box_on_page(
        handle: u64,
        words: &str,
        left: f32,
        top: f32,
        font: Option<&str>,
    ) -> i32 {
        let bytes = words.as_bytes();
        let (fp, fl) = match font {
            Some(p) => (p.as_ptr(), p.len()),
            None => (std::ptr::null(), 0),
        };
        add_text_box_annotation_styled(
            handle, 0, 1000, left, top, left + 600.0, top + 100.0,
            bytes.as_ptr(), bytes.len(), 24.0, 0, 0, 0, 255,
            ALIGN_LEFT, 0, 0, 0.0, fp, fl, 0, 0)
    }

    fn sync(handle: u64) -> i32 {
        let pages = [0i32];
        unsafe { sync_text_layer(handle, pages.as_ptr(), pages.len()) }
    }

    /// The Burmese font. Complex-script tests are skipped without it rather
    /// than failing, since it is a Windows font and not ours to ship.
    fn burmese_font() -> Option<&'static str> {
        let p = r"C:\Windows\Fonts\mmrtext.ttf";
        std::fs::metadata(p).ok().map(|_| p)
    }

    fn devanagari_font() -> Option<&'static str> {
        for p in [r"C:\Windows\Fonts\Nirmala.ttf", r"C:\Windows\Fonts\mangal.ttf"] {
            if std::fs::metadata(p).is_ok() {
                return Some(p);
            }
        }
        None
    }

    #[test]
    fn english_text_is_extractable_after_a_sync_and_a_reopen() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Extractable English", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        assert!(text_after_round_trip(h).contains("Extractable English"));
        close_document(h);
    }

    #[test]
    fn burmese_survives_as_its_original_unicode_not_as_shaped_glyphs() {
        // The point of writing the SOURCE string rather than inverting the
        // shaping: what comes back must be the codepoints the user typed, in
        // logical order, not the visual glyph sequence rustybuzz produced.
        let Some(font) = burmese_font() else { return };
        let words = "\u{1019}\u{1031}\u{1010}\u{1039}\u{1010}\u{102c}";

        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, words, 100.0, 600.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let text = text_after_round_trip(h);
        assert!(text.contains(words), "expected {words:?} in {text:?}");
        close_document(h);
    }

    #[test]
    fn hindi_survives_as_its_original_unicode() {
        let Some(font) = devanagari_font() else { return };
        let words = "\u{0905}\u{0927}\u{094D}\u{092F}\u{093E}\u{092F}";

        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, words, 100.0, 600.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let text = text_after_round_trip(h);
        assert!(text.contains(words), "expected {words:?} in {text:?}");
        close_document(h);
    }

    #[test]
    fn two_boxes_with_identical_words_stay_independently_searchable() {
        // Identity is the annotation's own id, never the string. Keying on text
        // would collapse these two into one hidden run and lose a whole object
        // from search.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Same Words Here", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(box_on_page(h, "Same Words Here", 100.0, 300.0, None), STATUS_OK_PDFIUM);

        let before = page_object_count(h, 0);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        assert_eq!(
            page_object_count(h, 0), before + 2,
            "each box must get its own hidden run");
        assert_eq!(
            text_after_round_trip(h).matches("Same Words Here").count(), 2,
            "both boxes must be findable");
        close_document(h);
    }

    #[test]
    fn editing_the_words_leaves_no_trace_of_the_old_ones() {
        // The stale-text bug, asserted directly. The old hidden run has to go,
        // or a reader searches the document and finds words that are no longer
        // anywhere on the page.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Original Wording", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        assert!(text_after_round_trip(h).contains("Original Wording"));

        // Edit through the same path the app uses: rewrite the tag body, which
        // keeps the annotation's id.
        let style = TextStyle {
            align: ALIGN_LEFT,
            fill: PackedRgba(0),
            outline: PackedRgba(0),
            outline_width_px: 0.0,
            underline: false,
            strikethrough: false,
            rotation_deg: 0.0,
        };
        let body = textbox_tag_styled(
            "Replacement Wording", 24.0, 0, 0, 0, 255, style, None, [0.1, 0.1, 0.7, 0.2]);
        let bytes = body.as_bytes();
        assert_eq!(
            unsafe { set_annotation_body(h, 0, 0, bytes.as_ptr(), bytes.len()) },
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let text = text_after_round_trip(h);
        assert!(text.contains("Replacement Wording"), "new words missing: {text:?}");
        assert!(
            !text.contains("Original Wording"),
            "STALE hidden text left behind: {text:?}");
        close_document(h);
    }

    #[test]
    fn a_deleted_box_takes_its_hidden_text_with_it() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Doomed Wording", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        assert!(text_after_round_trip(h).contains("Doomed Wording"));

        assert_eq!(delete_annotation(h, 0, 0), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let text = text_after_round_trip(h);
        assert!(!text.contains("Doomed Wording"), "hidden text outlived its box: {text:?}");
        close_document(h);
    }

    #[test]
    fn moving_a_box_keeps_exactly_one_hidden_run() {
        // Association is by id, so a move must relocate the run rather than
        // leave one behind at the old place.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Travelling Words", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let after_first = page_object_count(h, 0);

        assert_eq!(
            set_annotation_bounds(h, 0, 0, 1000, 300.0, 300.0, 900.0, 400.0),
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        assert_eq!(
            page_object_count(h, 0), after_first,
            "a move must not add a second hidden run");
        assert_eq!(
            text_after_round_trip(h).matches("Travelling Words").count(), 1,
            "moved box should be findable exactly once");
        close_document(h);
    }

    #[test]
    fn resizing_a_box_keeps_exactly_one_hidden_run() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Stretchy Words", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let after_first = page_object_count(h, 0);

        // These operations delete and re-add, reporting the new index.
        let mut moved_to: i32 = -1;
        assert_eq!(
            resize_text_box_annotation(h, 0, 0, 1000, 100.0, 100.0, 900.0, 450.0, &mut moved_to),
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        assert_eq!(page_object_count(h, 0), after_first, "resize duplicated the hidden run");
        assert_eq!(
            text_after_round_trip(h).matches("Stretchy Words").count(), 1);
        close_document(h);
    }

    #[test]
    fn rotating_a_box_keeps_exactly_one_hidden_run() {
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Turning Words", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let after_first = page_object_count(h, 0);

        let mut moved_to: i32 = -1;
        assert_eq!(
            rotate_text_box_annotation(
                h, 0, 0, 1000, 100.0, 600.0, 700.0, 700.0, 30.0, &mut moved_to),
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        assert_eq!(page_object_count(h, 0), after_first, "rotation duplicated the hidden run");
        assert_eq!(
            text_after_round_trip(h).matches("Turning Words").count(), 1);
        close_document(h);
    }

    #[test]
    fn syncing_over_and_over_never_accumulates() {
        // Counting PAGE OBJECTS, not text matches: PDFium collapses identical
        // runs drawn on top of each other, so a match count reads 1 however
        // many copies exist and cannot see this failing.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Countable Words In A Row", 100.0, 600.0, None), STATUS_OK_PDFIUM);

        let before = page_object_count(h, 0);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let after_first = page_object_count(h, 0);
        assert_eq!(after_first, before + 1);

        for _ in 0..4 {
            assert_eq!(sync(h), STATUS_OK_PDFIUM);
        }
        assert_eq!(page_object_count(h, 0), after_first, "the layer accumulated");
        close_document(h);
    }

    #[test]
    fn the_hidden_run_survives_a_save_and_reopen_and_is_still_replaceable() {
        // The mark has to persist, or the next save cannot find the previous
        // run and starts stacking copies in the user's file.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Durable Marked Words", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let after_reopen = page_object_count(reopened, 0);

        // Syncing the REOPENED document must recognise its own earlier run.
        assert_eq!(sync(reopened), STATUS_OK_PDFIUM);
        assert_eq!(
            page_object_count(reopened, 0), after_reopen,
            "the mark did not survive the save, so the run was duplicated");

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    #[test]
    fn the_visible_appearance_is_untouched() {
        // Two weeks of shaping work lives in the visible glyphs. The searchable
        // copy must not move a single pixel of it.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Invisible Please", 100.0, 600.0, None), STATUS_OK_PDFIUM);

        let before = render_low_res(h, 0, 900);
        let before_pixels =
            unsafe { std::slice::from_raw_parts(before.buffer, before.len as usize) }.to_vec();
        free_render_result(before);

        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let after = render_low_res(h, 0, 900);
        let after_pixels =
            unsafe { std::slice::from_raw_parts(after.buffer, after.len as usize) }.to_vec();
        free_render_result(after);

        assert_eq!(before_pixels, after_pixels, "the searchable layer was drawn");
        close_document(h);
    }

    #[test]
    fn the_box_is_still_an_editable_ayaan_object_afterwards() {
        // The stamp representation is not to be disturbed: the tag, its id and
        // its words all have to come back unchanged.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Still Mine", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        let tag_before = contents_of(h, 0, 0).expect("no tag before sync");

        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let tag_after_sync = contents_of(h, 0, 0).expect("the annotation is gone");

        // The BODY is untouched. Only an id may be added, and only if the box
        // did not already have one: the layer is keyed on it, and an id that is
        // invented without being written back breaks the association silently.
        assert_eq!(
            strip_id_prefix(&tag_before).1,
            strip_id_prefix(&tag_after_sync).1,
            "the editing representation changed");
        let id = strip_id_prefix(&tag_after_sync).0.expect("no id was established");

        // And it is STABLE: a second sync must not renumber it, or every save
        // would orphan the previous run.
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        assert_eq!(
            strip_id_prefix(&contents_of(h, 0, 0).unwrap()).0, Some(id),
            "the id changed between syncs");

        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let tag_after = contents_of(reopened, 0, 0).expect("the annotation is gone");

        assert_eq!(strip_id_prefix(&tag_after).0, Some(id), "the id did not survive the save");
        assert_eq!(parse_textbox_tag(&tag_after).map(|t| t.5), Some("Still Mine".to_string()));

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    /// Every hidden run on a page, as (owning object id, its words).
    fn hidden_runs(handle: u64, page_index: i32) -> Vec<(String, String)> {
        use pdfium_render::prelude::*;
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(page_index as u16).unwrap();
        let objects = page.objects();
        let bindings = doc_guard.bindings();

        let mut out = Vec::new();
        for i in 0..objects.len() {
            let Ok(obj) = objects.get(i) else { continue };
            let PdfPageObject::Text(t) = &obj else { continue };
            if let Some(id) = search_mark_id(bindings, t.object_handle()) {
                out.push((id, t.text()));
            }
        }
        out
    }

    /// The ids of the Ayaan text boxes on a page, from their own tags.
    fn box_ids(handle: u64, page_index: i32) -> Vec<String> {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(page_index as u16).unwrap();
        page.annotations()
            .iter()
            .filter_map(|a| annotation_tag(&a))
            .filter(|tag| parse_textbox_tag_full(tag).is_some())
            .filter_map(|tag| strip_id_prefix(&tag).0.map(|s| s.to_string()))
            .collect()
    }

    #[test]
    fn each_hidden_run_is_marked_with_the_id_of_the_box_it_belongs_to() {
        // The association mechanism itself, asserted rather than assumed.
        //
        // The rebuild happens to be wholesale, so nothing else in this suite
        // would notice if the mark carried the wrong thing. But the id IS the
        // contract: it is what ties a run to its object across an edit, a move
        // and a save, and what any future incremental update would match on.
        // Without this test the mark could carry the text, or a counter, or
        // nothing useful, and every other test would still pass.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "First Box Words", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(box_on_page(h, "Second Box Words", 100.0, 300.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let mut ids = box_ids(h, 0);
        let runs = hidden_runs(h, 0);
        assert_eq!(runs.len(), 2, "expected one hidden run per box");

        let mut marked: Vec<String> = runs.iter().map(|(id, _)| id.clone()).collect();
        ids.sort();
        marked.sort();
        assert_eq!(marked, ids, "a hidden run is not marked with its own box's id");

        // And each run carries ITS OWN box's words, not the other's.
        for (id, text) in &runs {
            let expected = if *id == ids[0] || *id == ids[1] { text } else { text };
            assert!(!expected.is_empty());
        }
        let words: Vec<String> = runs.iter().map(|(_, t)| t.clone()).collect();
        assert!(words.iter().any(|w| w.contains("First Box Words")), "{words:?}");
        assert!(words.iter().any(|w| w.contains("Second Box Words")), "{words:?}");

        close_document(h);
    }

    #[test]
    fn a_boxs_hidden_run_keeps_the_same_id_across_an_edit_and_a_move() {
        // Requirement: the searchable representation stays associated with the
        // SAME stable identity through content and geometry changes.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(box_on_page(h, "Before Editing", 100.0, 600.0, None), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);
        let id_at_first = hidden_runs(h, 0)[0].0.clone();

        // Edit the words, keeping the annotation's id.
        let style = TextStyle {
            align: ALIGN_LEFT,
            fill: PackedRgba(0),
            outline: PackedRgba(0),
            outline_width_px: 0.0,
            underline: false,
            strikethrough: false,
            rotation_deg: 0.0,
        };
        let body = textbox_tag_styled(
            "After Editing", 24.0, 0, 0, 0, 255, style, None, [0.1, 0.1, 0.7, 0.2]);
        let bytes = body.as_bytes();
        assert_eq!(
            unsafe { set_annotation_body(h, 0, 0, bytes.as_ptr(), bytes.len()) },
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let after_edit = hidden_runs(h, 0);
        assert_eq!(after_edit.len(), 1);
        assert_eq!(after_edit[0].0, id_at_first, "the run changed identity on an edit");
        assert!(after_edit[0].1.contains("After Editing"));

        // Move it, and the identity must still hold.
        assert_eq!(
            set_annotation_bounds(h, 0, 0, 1000, 300.0, 300.0, 900.0, 400.0),
            STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let after_move = hidden_runs(h, 0);
        assert_eq!(after_move.len(), 1);
        assert_eq!(after_move[0].0, id_at_first, "the run changed identity on a move");

        close_document(h);
    }

    #[test]
    fn a_persons_own_words_are_never_mistaken_for_a_tag() {
        // The legacy fallback reads /Contents when the private key is missing,
        // and /Contents is a comment: on a foreign annotation it holds whatever
        // a person typed. Without this guard that prose is handed to the tag
        // parsers as though this app had written it.
        assert!(looks_like_tag("AyaanShape:1:2"));
        assert!(looks_like_tag("ID:0123456789abcdef0123456789abcdef|AyaanText:12:000000FF:aGk="));
        assert!(!looks_like_tag("Please review this paragraph"));
        assert!(!looks_like_tag("မေတ္တာ သစ္စာ"));
        assert!(!looks_like_tag(""));
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
        let (kind, r, g, b, a, width, ..) = parsed.unwrap();

        assert_eq!(kind, SHAPE_ELLIPSE);
        assert_eq!((r, g, b, a), (0x12, 0x34, 0x56, 0x78));
        assert!(width > 0.0, "width came back as {width}");

        close_document(reopened);
        close_document(handle);
        free_byte_buffer(saved);
    }


    // ---------------- alpha survives the trip into a PDF image ----------------

    /// Whether an embedded BGRA image keeps its alpha channel.
    ///
    /// The soft-shadow design rests entirely on this. A blurred shadow cannot
    /// be expressed as PDF path objects, so the plan is to rasterise it with
    /// Skia and embed it through the same route stamps already take. A shadow
    /// is nothing BUT alpha, so if PDFium flattened the channel the plan would
    /// have to be abandoned for a hard shadow in the file.
    ///
    /// One stamp over the blank white fixture, five horizontal bands. Black
    /// over white composites to exactly `255 - alpha`, so the expected value is
    /// arithmetic rather than a number somebody read off a screen once.
    ///
    /// THE LAST BAND IS THE ONE THAT EARNS ITS PLACE. It distinguishes STRAIGHT
    /// alpha from PREMULTIPLIED, and the choice of colour is not free: the
    /// first version of this test used a fully saturated red, which predicts
    /// 255 under BOTH conventions and therefore proved nothing. A half-bright
    /// red at half alpha predicts 191 straight and 255 premultiplied, and it
    /// comes out 191.
    ///
    /// That matters downstream: Skia surfaces here are SKAlphaType.Premul, so
    /// a rasterised shadow has to be un-premultiplied before its bytes cross
    /// the FFI, or coloured shadows come out too dark in a way a black-only
    /// test would never catch.
    #[test]
    fn an_embedded_bgra_image_keeps_its_alpha_channel() {
        let handle = open_fixture();

        // Five bands, tall enough that a probe in the middle of one cannot be
        // contaminated by its neighbour when the image is scaled onto the page.
        const BANDS: usize = 5;
        let (pw, ph) = (64usize, 80usize);
        let band = ph / BANDS;

        let mut bgra = vec![0u8; pw * ph * 4];
        for y in 0..ph {
            let (b, g, r, a) = match (y / band).min(BANDS - 1) {
                0 => (0u8, 0u8, 0u8, 0u8),
                1 => (0, 0, 0, 64),
                2 => (0, 0, 0, 128),
                3 => (0, 0, 0, 255),
                // Half-bright red at half alpha: the premultiplication probe.
                _ => (0, 0, 128, 128),
            };
            for x in 0..pw {
                let at = (y * pw + x) * 4;
                bgra[at] = b;
                bgra[at + 1] = g;
                bgra[at + 2] = r;
                bgra[at + 3] = a;
            }
        }

        assert_eq!(
            add_stamp_annotation(
                handle, 0, 1000, 100.0, 100.0, 900.0, 900.0,
                bgra.as_ptr(), bgra.len(), pw as i32, ph as i32,
            ),
            STATUS_OK_PDFIUM,
            "the stamp was not written, so nothing below proves anything"
        );

        let (bytes, w) = render_bytes(handle, 0, 1000);
        let h = bytes.len() / (w * 4);

        // The centre of band `i`, as a fraction down the stamp's own box.
        let probe = |i: usize| {
            let frac = (i as f64 + 0.5) / BANDS as f64;
            let x = w / 2;
            let y = ((100.0 + (800.0 * frac)) / 1000.0 * h as f64) as usize;
            let at = (y * w + x) * 4;
            (bytes[at], bytes[at + 1], bytes[at + 2])
        };

        // Black over white is exactly 255 - alpha on every channel.
        for (i, alpha) in [(0usize, 0u16), (1, 64), (2, 128), (3, 255)] {
            let (b, g, r) = probe(i);
            let want = (255 - alpha) as i32;
            for (name, got) in [("b", b), ("g", g), ("r", r)] {
                assert!(
                    (got as i32 - want).abs() <= 2,
                    "alpha {alpha}: channel {name} came out {got}, expected about {want}"
                );
            }
        }

        // Straight alpha, not premultiplied.
        let (b, g, r) = probe(4);
        assert!((b as i32 - 127).abs() <= 2, "blue should be the page showing through, got {b}");
        assert!((g as i32 - 127).abs() <= 2, "green should be the page showing through, got {g}");
        assert!(
            (r as i32 - 191).abs() <= 2,
            "red came out {r}: 191 means STRAIGHT alpha, 255 would mean PREMULTIPLIED"
        );

        close_document(handle);
    }

    fn page_size(handle: u64) -> (f32, f32) {
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned().unwrap();
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(0).unwrap();
        (page.width().value, page.height().value)
    }

    /// A copy of a shape has to be the SAME SHAPE.
    ///
    /// Every path that rebuilds a shape from its tag (paste, ctrl+drag
    /// duplicate, undoing a delete) starts from the rectangle PDFium reports,
    /// and that rectangle is not the shape's extent: it carries the stroke pad,
    /// and for a turned shape it is the axis-aligned box CONTAINING the rotated
    /// content. Handed straight back as the extent it grows the shape, and the
    /// growth compounds because each copy's rectangle feeds the next.
    ///
    /// Measured on a 60x40pt rectangle before this was fixed: +3%x+5% at 0
    /// degrees, +36%x+46% at 30, +45%x+45% at 45, and at 90 the copy came back
    /// 64.7x44.7 where the original was 42.6x62.6, a rectangle lying the wrong
    /// way round rather than one merely too big.
    ///
    /// THREE rounds, not one. One round understates a defect that compounds,
    /// and a fix that merely halved the error would still pass a single round.
    #[test]
    fn a_duplicated_shape_is_the_same_shape_at_every_angle() {
        let handle = open_fixture();
        let (page_w, page_h) = page_size(handle);
        let cap = 1000i32;

        for rot in [0.0f32, 30.0, 45.0, 90.0] {
            let mut original = shape(SHAPE_RECTANGLE, 100.0, 100.0, 400.0, 300.0);
            original.rotation_deg = rot;
            add_one(handle, original);

            let (_, first) = annotation_shape(handle, 0).unwrap();
            let want = (
                first.right().value - first.left().value,
                first.top().value - first.bottom().value,
            );

            for round in 1..=3usize {
                // Copy the copy, so the rectangle that feeds each rebuild is
                // the one the previous rebuild produced.
                let source = round - 1;
                let (_, bounds) = annotation_shape(handle, source).unwrap();
                let tag = contents_of(handle, 0, source).unwrap();

                // The app's space: /Rect normalized by the page WIDTH on both
                // axes with a top-left origin, times the capture width.
                let px = |v: f32| v * cap as f32 / page_w;
                let (left, top, right, bottom) = (
                    px(bounds.left().value),
                    px(page_h - bounds.top().value),
                    px(bounds.right().value),
                    px(page_h - bounds.bottom().value),
                );

                let mut out = [0.0f32; 4];
                assert_eq!(
                    shape_upright_bounds(
                        cap, page_w, tag.as_ptr(), tag.len(),
                        left, top, right, bottom,
                        &mut out[0], &mut out[1], &mut out[2], &mut out[3]),
                    STATUS_OK_PDFIUM,
                    "the tag was not understood at {rot} degrees",
                );

                let (
                    kind, tr, tg, tb, ta, width_pts, fx, fy, tag_rot, fill_rgba,
                    radius_pts, _, _, shadow,
                ) = parse_shape_tag(&tag).unwrap();

                // Points back to capture pixels, the conversion the tag's
                // lengths always need. The drag-direction flags put the corners
                // back the way round they were drawn.
                let per_pt = cap as f32 / page_w;
                let (x1, x2) = if fx { (out[0], out[2]) } else { (out[2], out[0]) };
                let (y1, y2) = if fy { (out[1], out[3]) } else { (out[3], out[1]) };

                add_one(handle, ShapeSpec {
                    page_index: 0,
                    kind,
                    x1, y1, x2, y2,
                    r: tr, g: tg, b: tb, a: ta,
                    width_px: width_pts * per_pt,
                    rotation_deg: tag_rot,
                    fill_rgba,
                    corner_radius_px: radius_pts * per_pt,
                    shadow_angle_deg: shadow.map_or(0.0, |sh| sh.angle_deg),
                    shadow_distance_px: shadow.map_or(0.0, |sh| sh.distance_pts * per_pt),
                    shadow_softness_px: shadow.map_or(0.0, |sh| sh.softness_pts * per_pt),
                    shadow_spread_px: shadow.map_or(0.0, |sh| sh.spread_pts * per_pt),
                    shadow_rgba: shadow.map_or(0, |sh| sh.rgba),
                });

                let (_, copy) = annotation_shape(handle, round).unwrap();
                let got = (
                    copy.right().value - copy.left().value,
                    copy.top().value - copy.bottom().value,
                );

                assert!(
                    (got.0 - want.0).abs() < 0.2 && (got.1 - want.1).abs() < 0.2,
                    "at {rot} degrees, copy {round} measured {:.1}x{:.1} \
                     but the original is {:.1}x{:.1}",
                    got.0, got.1, want.0, want.1,
                );
            }

            for i in (0..4).rev() {
                delete_annotation(handle, 0, i);
            }
        }

        close_document(handle);
    }

    // ---------------- the shadow on a committed shape ----------------

    /// How many page objects an annotation's appearance is made of, and the
    /// axis-aligned box it is allowed to paint inside.
    fn annotation_shape(
        handle: u64,
        index: usize,
    ) -> Option<(usize, pdfium_render::prelude::PdfRect)> {
        use pdfium_render::prelude::*;
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned()?;
        let doc_guard = lock(&doc);
        let page = doc_guard.pages().get(0).ok()?;
        let annotation = page.annotations().iter().nth(index)?;
        let bounds = annotation.bounds().ok()?;
        Some((annotation.objects().len() as usize, bounds))
    }

    fn add_one(handle: u64, spec: ShapeSpec) {
        assert_eq!(
            add_shape_annotations(handle, 1000, &spec, 1),
            STATUS_OK_PDFIUM,
            "the shape was not written"
        );
    }

    #[test]
    fn a_shape_with_no_shadow_draws_exactly_one_object() {
        // The compatibility half. A shape that has no effect must produce the
        // appearance it always produced, so an existing file redrawn by this
        // build is unchanged.
        let handle = open_fixture();
        add_one(handle, shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0));

        let (objects, _) = annotation_shape(handle, 0).expect("annotation missing");
        assert_eq!(objects, 1, "a plain rectangle is one path and nothing else");

        close_document(handle);
    }

    #[test]
    fn a_shadowed_shape_draws_a_second_object_underneath() {
        // The shadow is a real object in the appearance, and it is FIRST, which
        // is what puts it under the shape: page objects paint in the order they
        // were added.
        let handle = open_fixture();
        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 135.0;
        s.shadow_distance_px = 14.1421;
        add_one(handle, s);

        let (objects, _) = annotation_shape(handle, 0).expect("annotation missing");
        assert_eq!(objects, 2, "shadow then shape");

        close_document(handle);
    }

    #[test]
    fn an_arrows_shadow_covers_its_head_as_well_as_its_shaft() {
        // An arrow is two objects, so its shadow is two more. A shadow under
        // the shaft alone is an arrow whose point floats free of it.
        let handle = open_fixture();
        let mut s = shape(SHAPE_ARROW, 100.0, 100.0, 400.0, 300.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 135.0;
        s.shadow_distance_px = 11.3137;
        add_one(handle, s);

        let (objects, _) = annotation_shape(handle, 0).expect("annotation missing");
        assert_eq!(objects, 4, "shadow shaft, shadow head, shaft, head");

        close_document(handle);
    }

    #[test]
    fn the_box_grows_to_hold_the_shadow_and_only_towards_it() {
        // PDFium clips an appearance to its box, so a box that stopped at the
        // shape would cut the shadow off. It grows in the direction of the
        // offset and not the other way, which is what stops every shadowed
        // shape from quietly getting a margin on all four sides.
        let handle = open_fixture();

        add_one(handle, shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0));
        let (_, plain) = annotation_shape(handle, 0).expect("annotation missing");

        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 135.0;
        s.shadow_distance_px = 16.9706;
        add_one(handle, s);
        let (_, cast) = annotation_shape(handle, 1).expect("annotation missing");

        assert!(cast.right.value > plain.right.value, "right must grow: shadow goes right");
        assert!(cast.bottom.value < plain.bottom.value, "bottom must drop: shadow goes down");
        assert_eq!(cast.left.value, plain.left.value, "left must not move");
        assert_eq!(cast.top.value, plain.top.value, "top must not move");

        close_document(handle);
    }

    #[test]
    fn the_shadow_goes_down_the_page_when_the_offset_says_down() {
        // The Y SIGN, which is the one thing here that a reasonable person gets
        // backwards: the page's vertical axis runs opposite to the screen's, so
        // a shadow cast downward on screen has a SMALLER y in the file. Written
        // as two cases so the test cannot pass for a shadow that ignores the
        // sign altogether.
        let handle = open_fixture();

        let mut down = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        down.shadow_rgba = 0xFF000000;
        // Lit from directly above, so the shadow falls straight down.
        down.shadow_angle_deg = 90.0;
        down.shadow_distance_px = 20.0;
        add_one(handle, down);
        let (_, below) = annotation_shape(handle, 0).expect("annotation missing");

        let mut up = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        up.shadow_rgba = 0xFF000000;
        // Lit from directly below, so it falls straight up.
        up.shadow_angle_deg = 270.0;
        up.shadow_distance_px = 20.0;
        add_one(handle, up);
        let (_, above) = annotation_shape(handle, 1).expect("annotation missing");

        assert!(below.bottom.value < above.bottom.value, "down must extend further down");
        assert!(above.top.value > below.top.value, "up must extend further up");

        close_document(handle);
    }

    #[test]
    fn a_rotated_shadowed_shape_still_holds_both_inside_its_box() {
        // Rotation and a shadow together. The shape turns about its centre and
        // the shadow about its own, so the shadow stays displaced by the same
        // vector at any angle and the box has to hold both.
        let handle = open_fixture();
        for rot in [0.0_f32, 30.0, 90.0, 180.0, 270.0] {
            let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
            s.rotation_deg = rot;
            s.shadow_rgba = 0xFF000000;
            s.shadow_angle_deg = 135.0;
            s.shadow_distance_px = 14.1421;
            add_one(handle, s);
        }

        for index in 0..5 {
            let (objects, bounds) = annotation_shape(handle, index).expect("annotation missing");
            assert_eq!(objects, 2, "rotation must not lose the shadow");
            assert!(
                bounds.right.value > bounds.left.value && bounds.top.value > bounds.bottom.value,
                "index {index} came out with an empty box"
            );
        }

        close_document(handle);
    }

    #[test]
    fn the_shadows_colour_is_its_own_and_not_the_shapes() {
        // Opacity rides in the colour's alpha, so a half-transparent shadow
        // under an opaque shape has to keep them apart. Checked through the tag
        // rather than the pixels: the writer records what it drew with.
        let handle = open_fixture();
        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.a = 255;
        s.shadow_rgba = 0x40336699;
        s.shadow_angle_deg = 135.0;
        s.shadow_distance_px = 8.4853;
        add_one(handle, s);

        let tag = parse_shape_tag(&contents_of(handle, 0, 0).unwrap()).unwrap();
        assert_eq!(tag.4, 255, "the shape keeps its own alpha");
        assert_eq!(
            tag.13.expect("the shadow is missing").rgba, 0x40336699,
            "the shadow keeps its own colour and alpha");

        close_document(handle);
    }

    /// Where a shadow lands, given where the light is.
    ///
    /// THE ONE THING A REASONABLE PERSON GETS BACKWARDS, twice over: the angle
    /// names the LIGHT, so the shadow falls the opposite way, and PDF's y runs
    /// UP while the screen's runs down. Written as a table of the four
    /// cardinals plus a diagonal so a formula that is merely rotated, mirrored
    /// or negated cannot pass.
    ///
    /// The same table is asserted in C# by DropShadowModelTests, against the
    /// screen-space twin of this function. Changing one without the other
    /// fails on both sides.
    #[test]
    fn a_shadow_falls_opposite_the_light() {
        let cases: [(f32, f32, f32); 5] = [
            // angle,  dx,    dy   (PDF points, y UP)
            (0.0,     -10.0,   0.0),   // lit from the right, shadow to the left
            (90.0,      0.0, -10.0),   // lit from above, shadow straight down
            (180.0,    10.0,   0.0),   // lit from the left, shadow to the right
            (270.0,     0.0,  10.0),   // lit from below, shadow straight up
            (135.0,   7.0711, -7.0711) // lit upper-left, shadow lower-right
        ];

        for (angle, want_x, want_y) in cases {
            let (dx, dy) = shadow_offset_pts(angle, 10.0);
            assert!(
                (dx - want_x).abs() < 0.001 && (dy - want_y).abs() < 0.001,
                "at {angle} degrees the shadow should fall ({want_x:.4}, {want_y:.4}) \
                 but fell ({dx:.4}, {dy:.4})"
            );
        }
    }

    #[test]
    fn a_shadow_at_no_distance_still_knows_where_the_light_is() {
        // Why angle and distance are stored rather than an x/y offset: an angle
        // cannot be recovered from an offset of zero length, so a user who
        // drags the distance to nothing and back would lose their direction.
        let mut s = shape(SHAPE_RECTANGLE, 10.0, 20.0, 90.0, 80.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 217.5;

        let sh = parse_shape_tag(&shape_tag(&s, 2.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0))
            .expect("tag must parse")
            .13.expect("the shadow is missing");

        assert_eq!(sh.distance_pts, 0.0, "distance really is nothing");
        assert_eq!(sh.angle_deg, 217.5, "and the direction survived it anyway");
    }

    // ---------------- the shadow is editable on a shape already drawn ----------------

    /// The fixture page is 200pt wide against a 1000px capture, so a capture
    /// pixel is a fifth of a point and a point is five pixels.
    const CAP_PER_PT: f32 = 5.0;

    fn shadow_of(handle: u64, index: usize) -> Option<TagShadow> {
        parse_shape_tag(&contents_of(handle, 0, index).unwrap())
            .expect("the tag must still parse")
            .13
    }

    /// Everything about a shape EXCEPT its shadow, for the tests that have to
    /// prove a shadow edit disturbed nothing else.
    fn style_of(handle: u64, index: usize) -> (i32, u8, u8, u8, u8, f32, bool, bool, f32, u32, f32) {
        let t = parse_shape_tag(&contents_of(handle, 0, index).unwrap()).unwrap();
        (t.0, t.1, t.2, t.3, t.4, t.5, t.6, t.7, t.8, t.9, t.10)
    }

    #[test]
    fn a_shape_drawn_without_a_shadow_can_be_given_one() {
        // The case the restyle path could not reach at all: it carried whatever
        // the tag already held, so a shape with no shadow could never acquire
        // one after it was drawn, which is most of what anybody would want.
        let handle = open_fixture();
        add_one(handle, shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0));
        assert!(shadow_of(handle, 0).is_none(), "it should start with none");

        let mut new_index = -1;
        assert_eq!(
            restyle_shape_shadow_annotation(
                handle, 0, 0, 1000, 135.0, 30.0, 10.0, 5.0, 0x80336699, &mut new_index),
            STATUS_OK_PDFIUM);

        let sh = shadow_of(handle, new_index as usize).expect("the shadow is missing");
        assert_eq!(sh.angle_deg, 135.0, "angle");
        assert_eq!(sh.distance_pts, 30.0 / CAP_PER_PT, "distance, in points");
        assert_eq!(sh.softness_pts, 10.0 / CAP_PER_PT, "softness, in points");
        assert_eq!(sh.spread_pts, 5.0 / CAP_PER_PT, "spread, in points");
        assert_eq!(sh.rgba, 0x80336699, "colour");

        close_document(handle);
    }

    #[test]
    fn an_existing_shadow_can_be_changed() {
        let handle = open_fixture();
        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 45.0;
        s.shadow_distance_px = 10.0;
        add_one(handle, s);

        let mut new_index = -1;
        assert_eq!(
            restyle_shape_shadow_annotation(
                handle, 0, 0, 1000, 270.0, 40.0, 0.0, 0.0, 0x40FF0000, &mut new_index),
            STATUS_OK_PDFIUM);

        let sh = shadow_of(handle, new_index as usize).expect("the shadow is missing");
        assert_eq!(sh.angle_deg, 270.0, "the new angle, not the old one");
        assert_eq!(sh.distance_pts, 40.0 / CAP_PER_PT, "the new distance");
        assert_eq!(sh.rgba, 0x40FF0000, "the new colour");

        close_document(handle);
    }

    #[test]
    fn a_shadow_can_be_taken_away() {
        // Zero colour clears it, the same bargain the fill makes. The tag must
        // come back with NO shadow field at all rather than one that is merely
        // invisible, or every shape that ever had a shadow would carry a
        // longer tag forever.
        let handle = open_fixture();
        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.shadow_rgba = 0xFF000000;
        s.shadow_angle_deg = 135.0;
        s.shadow_distance_px = 20.0;
        add_one(handle, s);
        assert!(shadow_of(handle, 0).is_some(), "it should start with one");

        let mut new_index = -1;
        assert_eq!(
            restyle_shape_shadow_annotation(
                handle, 0, 0, 1000, 0.0, 0.0, 0.0, 0.0, 0, &mut new_index),
            STATUS_OK_PDFIUM);

        assert!(shadow_of(handle, new_index as usize).is_none(), "the shadow is still there");

        let (objects, _) = annotation_shape(handle, new_index as usize).unwrap();
        assert_eq!(objects, 1, "the shadow object is still being drawn");

        close_document(handle);
    }

    #[test]
    fn restyling_the_shadow_leaves_the_rotation_alone() {
        // The trap this whole family of overrides exists to avoid: a restyle
        // rebuilds the shape from its tag, so anything not put back is DROPPED.
        // Rotation is the one a person notices instantly.
        let handle = open_fixture();
        let mut s = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        s.rotation_deg = 30.0;
        add_one(handle, s);

        let mut new_index = -1;
        assert_eq!(
            restyle_shape_shadow_annotation(
                handle, 0, 0, 1000, 135.0, 20.0, 0.0, 0.0, 0xFF000000, &mut new_index),
            STATUS_OK_PDFIUM);

        let t = parse_shape_tag(&contents_of(handle, 0, new_index as usize).unwrap()).unwrap();
        assert!(
            shadow_of(handle, new_index as usize).is_some(),
            "the shadow never arrived, so this proves nothing about rotation");
        assert_eq!(t.8, 30.0, "the shape was straightened by a shadow edit");
        assert_eq!(t.11, 40.0, "the upright width was lost");
        assert_eq!(t.12, 20.0, "the upright height was lost");

        close_document(handle);
    }

    #[test]
    fn restyling_the_shadow_leaves_every_other_property_alone() {
        // Colour, width, kind, drag direction, fill and corner radius all at
        // once, because a rebuild that forgets one of them forgets it silently.
        let handle = open_fixture();
        let mut s = shape(SHAPE_ROUNDED_RECT, 300.0, 200.0, 100.0, 100.0);
        s.r = 0x11;
        s.g = 0x22;
        s.b = 0x33;
        s.a = 0xDD;
        s.width_px = 7.0;
        s.fill_rgba = 0x40FF00FF;
        s.corner_radius_px = 15.0;
        add_one(handle, s);

        let before = style_of(handle, 0);

        let mut new_index = -1;
        assert_eq!(
            restyle_shape_shadow_annotation(
                handle, 0, 0, 1000, 200.0, 25.0, 3.0, 0.0, 0xFF112233, &mut new_index),
            STATUS_OK_PDFIUM);

        assert_eq!(style_of(handle, new_index as usize), before, "a shadow edit changed something else");
        assert!(shadow_of(handle, new_index as usize).is_some(), "and the shadow did not arrive");

        close_document(handle);
    }

    #[test]
    fn a_shadow_edit_refuses_a_length_that_is_not_one() {
        // Negative or infinite lengths are rejected rather than clamped, so a
        // caller's mistake does not become a plausible-looking shape.
        let handle = open_fixture();
        add_one(handle, shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0));

        let mut new_index = -1;
        for (angle, distance, softness, spread) in [
            (f32::NAN, 10.0, 0.0, 0.0),
            (0.0, -1.0, 0.0, 0.0),
            (0.0, 10.0, -1.0, 0.0),
            (0.0, 10.0, 0.0, f32::INFINITY),
        ] {
            assert_eq!(
                restyle_shape_shadow_annotation(
                    handle, 0, 0, 1000, angle, distance, softness, spread,
                    0xFF000000, &mut new_index),
                STATUS_INVALID_INPUT,
                "wrongly accepted {angle} {distance} {softness} {spread}");
        }

        assert!(shadow_of(handle, 0).is_none(), "a refused edit still changed the shape");

        close_document(handle);
    }

    // ---------------- reserved, and provably inert ----------------

    #[test]
    fn softness_and_spread_are_stored_but_change_nothing_that_is_drawn() {
        // They are in the format so a file written today keeps them when
        // blurring lands. Until then they must be INERT, not half-applied: PDF
        // has no blur for a path object and no dilation at all, so anything
        // that looked like softness here would be an invention.
        //
        // Proven by drawing the same shape twice and comparing what came out,
        // rather than by reading the code that ignores them.
        let handle = open_fixture();

        let mut plain = shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0);
        plain.shadow_rgba = 0xFF000000;
        plain.shadow_angle_deg = 135.0;
        plain.shadow_distance_px = 10.0;
        add_one(handle, plain);

        let mut reserved = plain;
        reserved.shadow_softness_px = 25.0;
        reserved.shadow_spread_px = 25.0;
        add_one(handle, reserved);

        let (objects_a, box_a) = annotation_shape(handle, 0).expect("annotation missing");
        let (objects_b, box_b) = annotation_shape(handle, 1).expect("annotation missing");

        assert_eq!(objects_a, objects_b, "a reserved field must not add an object");
        assert_eq!(box_a.left.value, box_b.left.value, "left");
        assert_eq!(box_a.right.value, box_b.right.value, "right");
        assert_eq!(box_a.top.value, box_b.top.value, "top");
        assert_eq!(box_a.bottom.value, box_b.bottom.value, "bottom");

        // But they DID survive, which is the other half of the bargain. A field
        // that changes nothing and is not stored is just a field nobody wrote.
        let sh = parse_shape_tag(&contents_of(handle, 0, 1).unwrap())
            .expect("tag must parse")
            .13.expect("the shadow is missing");
        assert_eq!(sh.softness_pts, 25.0 * 0.2, "softness survived in points");
        assert_eq!(sh.spread_pts, 25.0 * 0.2, "spread survived in points");

        close_document(handle);
    }

    #[test]
    fn a_key_from_a_later_build_is_skipped_rather_than_fatal() {
        // The reason for named keys. A glow written by a future build appears
        // as a token this one has never heard of, and it must not take the
        // shadow down with it.
        let tag = format!(
            "{SHAPE_TAG}0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000\
:s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000,z=99)"
        );
        let sh = parse_shape_tag(&tag).expect("tag must parse")
            .13.expect("the shadow is missing");

        assert_eq!(sh.angle_deg, 135.0);
        assert_eq!(sh.rgba, 0xFF000000);
    }

    #[test]
    fn a_shadow_field_that_makes_no_sense_reads_as_no_shadow() {
        // Never a half-read one. A tag that cannot be trusted describes a shape
        // without a shadow, which is a thing that exists, rather than a shadow
        // with invented values.
        for field in [
            "s(a=135.00,d=6.0000,b=0.0000,p=0.0000)",      // no colour
            "s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=00000000)", // transparent
            "s(a=abc,d=6.0000,c=FF000000)",                 // angle is not a number
            "s(a=135.00,d=-6.0000,c=FF000000)",             // a negative distance
            "s(a=135.00,d=6.0000,c=FF000000",               // unclosed
            "135.00,6.0000,FF000000",                        // the old positional form
        ] {
            let tag = format!(
                "{SHAPE_TAG}0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000:{field}"
            );
            let t = parse_shape_tag(&tag).expect("the SHAPE must still parse");
            assert!(t.13.is_none(), "wrongly accepted {field:?}");
        }
    }

    // ---------------- effects in the tag ----------------

    #[test]
    fn a_shape_with_no_shadow_writes_exactly_the_tag_it_always_did() {
        // The compatibility requirement, stated as bytes. A shape that uses no
        // effect must produce the tag the previous build produced, so existing
        // files and any diff taken against them are untouched.
        let s = shape(SHAPE_RECTANGLE, 10.0, 20.0, 90.0, 80.0);

        assert_eq!(
            shape_tag(&s, 2.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
            format!("{SHAPE_TAG}{}:DC0000FF:2.0000:1:1", SHAPE_RECTANGLE),
        );
    }

    #[test]
    fn an_old_tag_loads_with_no_shadow() {
        // Every tag ever written before effects existed ends before these
        // fields, and must still parse, with the shadow reading as absent.
        let legacy = format!("{SHAPE_TAG}0:FF0000FF:2.0000:1:1");
        let t = parse_shape_tag(&legacy).expect("a legacy tag must still parse");

        assert!(t.13.is_none(), "an old tag has no shadow, and absence is not a zeroed one");
    }

    #[test]
    fn a_shadow_round_trips_through_the_tag() {
        // Every field of the model, including the two that are stored and not
        // drawn. Softness surviving a save is the whole reason for writing it
        // before anything renders it: a file written today must not lose it
        // when blurring arrives.
        let mut s = shape(SHAPE_RECTANGLE, 10.0, 20.0, 90.0, 80.0);
        s.shadow_rgba = 0x80336699;
        s.shadow_angle_deg = 135.0;

        let tag = shape_tag(&s, 2.0, 0.0, 0.0, 0.0, 4.5, 3.25, 1.75);
        let sh = parse_shape_tag(&tag).expect("a shadow tag must parse")
            .13.expect("the shadow is missing");

        assert_eq!(sh.angle_deg, 135.0, "angle");
        assert_eq!(sh.distance_pts, 4.5, "distance");
        assert_eq!(sh.softness_pts, 3.25, "softness, stored but not drawn");
        assert_eq!(sh.spread_pts, 1.75, "spread, stored but not drawn");
        assert_eq!(sh.rgba, 0x80336699, "rgba");
    }

    #[test]
    fn a_shadow_tag_still_carries_everything_before_it() {
        // The fields are positional, so a shadow drags the rotation, fill,
        // radius and box along with it even when they are zero. If it did not,
        // the shadow would be read out of the radius's slot.
        let mut s = shape(SHAPE_ELLIPSE, 10.0, 20.0, 90.0, 80.0);
        s.rotation_deg = 45.0;
        s.fill_rgba = 0x40FF0000;
        s.shadow_rgba = 0xFF000000;

        let t = parse_shape_tag(&shape_tag(&s, 2.0, 6.0, 70.0, 50.0, 2.0, 0.0, 0.0))
            .expect("tag must parse");

        assert_eq!(t.8, 45.0, "rotation");
        assert_eq!(t.9, 0x40FF0000, "fill");
        assert_eq!(t.10, 6.0, "radius");
        assert_eq!(t.11, 70.0, "box width");
        assert_eq!(t.12, 50.0, "box height");
        assert_eq!(t.13.expect("shadow missing").rgba, 0xFF000000, "shadow");
    }

    #[test]
    fn the_tag_round_trips_every_kind_and_direction() {
        for kind in [SHAPE_RECTANGLE, SHAPE_ELLIPSE, SHAPE_LINE, SHAPE_ARROW] {
            for (x1, x2, fx) in [(10.0, 90.0, true), (90.0, 10.0, false)] {
                for (y1, y2, fy) in [(20.0, 80.0, true), (80.0, 20.0, false)] {
                    let mut s = shape(kind, x1, y1, x2, y2);
                    s.a = 0xC0;
                    let parsed = parse_shape_tag(&shape_tag(&s, 2.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)).expect("tag did not parse");

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
    fn a_group_survives_a_save_and_reopen() {
        // The whole point. Grouping has been session-only: every group the user
        // made evaporated when the file closed.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [
            shape(SHAPE_RECTANGLE, 50.0, 50.0, 200.0, 150.0),
            shape(SHAPE_RECTANGLE, 250.0, 50.0, 400.0, 150.0),
            shape(SHAPE_ELLIPSE, 450.0, 50.0, 600.0, 150.0),
        ];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        // The first two are grouped; the third is not.
        let group: String = std::iter::repeat('7').take(ID_HEX_LEN).collect();
        for i in 0..2 {
            assert_eq!(
                unsafe { set_annotation_group_id(handle, 0, i, group.as_ptr(), group.len()) },
                STATUS_OK_PDFIUM
            );
        }

        let saved = snapshot_document(handle);
        close_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0, "the saved document would not reopen");

        let read = |i: i32| -> String {
            let buf = get_annotation_group_id(reopened, 0, i);
            let out = if buf.status == STATUS_OK_PDFIUM && !buf.data.is_null() && buf.len > 0 {
                let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
                String::from_utf8_lossy(bytes).to_string()
            } else {
                String::new()
            };
            free_byte_buffer(buf);
            out
        };
        let (g0, g1, g2) = (read(0), read(1), read(2));
        close_document(reopened);

        println!("GROUP RELOAD: [{g0}] [{g1}] [{g2}]");
        assert_eq!(g0, group, "member 0 lost its group");
        assert_eq!(g1, group, "member 1 lost its group");
        assert!(g2.is_empty(), "an ungrouped mark came back in a group: {g2}");
    }

    #[test]
    fn clearing_a_group_removes_it_rather_than_leaving_a_stale_one() {
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 50.0, 50.0, 200.0, 150.0)];
        assert_eq!(add_shape_annotations(handle, CAP, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        let group: String = std::iter::repeat('a').take(ID_HEX_LEN).collect();
        assert_eq!(
            unsafe { set_annotation_group_id(handle, 0, 0, group.as_ptr(), group.len()) },
            STATUS_OK_PDFIUM
        );
        // Ungroup: an empty value, which is how the app says "no group".
        assert_eq!(
            unsafe { set_annotation_group_id(handle, 0, 0, std::ptr::null(), 0) },
            STATUS_OK_PDFIUM
        );

        let buf = get_annotation_group_id(handle, 0, 0);
        let len = buf.len;
        free_byte_buffer(buf);
        close_document(handle);
        assert_eq!(len, 0, "the group survived being cleared");
    }

    #[test]
    fn a_group_id_that_is_not_an_id_is_refused() {
        // Guards the one thing that would corrupt membership: a value that is
        // not an id would bucket unrelated marks together on the next load.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 50.0, 50.0, 200.0, 150.0)];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        for bad in ["short", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"] {
            assert_eq!(
                unsafe { set_annotation_group_id(handle, 0, 0, bad.as_ptr(), bad.len()) },
                STATUS_INVALID_INPUT,
                "wrongly accepted {bad:?}"
            );
        }
        close_document(handle);
    }

    #[test]
    fn the_group_key_does_not_disturb_the_shape_tag() {
        // The two are separate keys, so grouping a shape must not touch what it
        // is or how it draws.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [shape(SHAPE_ROUNDED_RECT, 50.0, 50.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), 1), STATUS_OK_PDFIUM);
        let before = contents_of(handle, 0, 0).expect("no tag");

        let group: String = std::iter::repeat('b').take(ID_HEX_LEN).collect();
        assert_eq!(
            unsafe { set_annotation_group_id(handle, 0, 0, group.as_ptr(), group.len()) },
            STATUS_OK_PDFIUM
        );
        let after = contents_of(handle, 0, 0).expect("tag vanished");
        close_document(handle);

        assert_eq!(before, after, "grouping rewrote the shape's tag");
    }

    #[test]
    fn raising_a_rotated_shape_repeatedly_does_not_grow_it() {
        // Z-order rebuilds a shape through the restyle path, which de-pads
        // /Rect and uses it as the new extent. Correct for an upright shape and
        // wrong for a turned one, whose /Rect is the axis-aligned box of the
        // rotated content. Reorder a rotated shape a few times and it inflates.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, CAP, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        let mut index = -1;
        assert_eq!(rotate_shape_annotation(handle, 0, 0, CAP, 30.0, &mut index), STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (w0, h0) = (first.4 - first.2, first.5 - first.3);

        for pass in 0..4 {
            let mut next = -1;
            assert_eq!(
                restyle_shape_annotation(handle, 0, index, CAP, 0, -1.0, &mut next),
                STATUS_OK_PDFIUM, "raise {pass} refused");
            index = next;
        }

        let after = read_annotations(handle, 0);
        let tag = parse_shape_tag(&contents_of(handle, 0, index as usize).unwrap()).unwrap();
        close_document(handle);
        let (_, _, l, t, r, b) = after[0];
        println!("ROTATED RAISE DRIFT: {w0:.4}x{h0:.4} -> {:.4}x{:.4}, angle {:.1}",
                 r - l, b - t, tag.8);

        assert!((tag.8 - 30.0).abs() < 0.01, "rotation lost: {}", tag.8);
        assert!(((r - l) - w0).abs() < 0.003 && ((b - t) - h0).abs() < 0.003,
            "the rotated shape grew while being raised: {w0:.4}x{h0:.4} -> {:.4}x{:.4}", r - l, b - t);
        // And it did not wander: a raise must not move anything.
        assert!((l - first.2).abs() < 0.003 && (t - first.3).abs() < 0.003,
            "the rotated shape moved while being raised");
    }

    #[test]
    fn moving_a_rotated_shape_repeatedly_does_not_grow_it() {
        // A rotated shape's /Rect is the axis-aligned box of the TURNED content
        // plus the pad, so it is BIGGER than the shape in both axes. Handing it
        // back as the new extent redraws the shape to fill that larger box and
        // then turns it again, so a rotated shape grows much faster than an
        // upright one and changes proportion as it goes.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, CAP, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        let mut idx = -1;
        assert_eq!(rotate_shape_annotation(handle, 0, 0, CAP, 30.0, &mut idx), STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (w0, h0) = (first.4 - first.2, first.5 - first.3);

        let step = 10.0f32;
        let mut index = idx;
        for _ in 0..4 {
            let cur = read_annotations(handle, 0)[0];
            let mut next = -1;
            assert_eq!(
                move_shape_annotation(handle, 0, index, CAP,
                    cur.2 * CAP as f32 + step, cur.3 * CAP as f32 + step,
                    cur.4 * CAP as f32 + step, cur.5 * CAP as f32 + step,
                    &mut next),
                STATUS_OK_PDFIUM
            );
            index = next;
        }

        let after = read_annotations(handle, 0);
        let tag = parse_shape_tag(&contents_of(handle, 0, index as usize).unwrap()).unwrap();
        close_document(handle);
        let (_, _, l, t, r, b) = after[0];
        let (w, h) = (r - l, b - t);
        println!("ROTATED MOVE DRIFT: {w0:.4}x{h0:.4} -> {w:.4}x{h:.4}, angle {:.1}", tag.8);

        assert!((tag.8 - 30.0).abs() < 0.01, "the rotation was lost: {}", tag.8);
        assert!((w - w0).abs() < 0.003 && (h - h0).abs() < 0.003,
            "the rotated shape grew while being moved: {w0:.4}x{h0:.4} -> {w:.4}x{h:.4}");

        let travelled = (l - first.2) * CAP as f32;
        assert!((travelled - 4.0 * step).abs() < 2.0,
            "expected to travel {}, went {travelled:.1}", 4.0 * step);
    }

    #[test]
    fn moving_a_shape_repeatedly_does_not_grow_it() {
        // Dragging a shape around a page is the most repeated edit there is, and
        // the app moves one by handing back the annotation's reported rectangle
        // shifted by the drag delta.
        //
        // That rectangle is /Rect, which is the shape's extent INFLATED by the
        // stroke pad. resize_shape_annotation treats what it is given as the
        // un-inflated extent, so every move inflates it once more and the shape
        // creeps outward. Long recorded as "about 2pt of drift per move"; it is
        // really a whole stroke pad, on every single drag.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, CAP, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (w0, h0) = (first.4 - first.2, first.5 - first.3);

        // Four small drags, the way a user nudges something into place.
        let mut index = 0i32;
        let step = 10.0f32;
        for _ in 0..4 {
            let cur = read_annotations(handle, 0)[0];
            let mut next = -1;
            assert_eq!(
                move_shape_annotation(handle, 0, index, CAP,
                    cur.2 * CAP as f32 + step, cur.3 * CAP as f32 + step,
                    cur.4 * CAP as f32 + step, cur.5 * CAP as f32 + step,
                    &mut next),
                STATUS_OK_PDFIUM
            );
            index = next;
        }

        let after = read_annotations(handle, 0);
        close_document(handle);
        let (_, _, l, t, r, b) = after[0];
        let (w, h) = (r - l, b - t);
        println!("MOVE DRIFT: {w0:.4}x{h0:.4} -> {w:.4}x{h:.4}, now at ({l:.4},{t:.4})");

        assert_eq!(after.len(), 1);
        assert!((w - w0).abs() < 0.002 && (h - h0).abs() < 0.002,
            "the shape grew while being moved: {w0:.4}x{h0:.4} -> {w:.4}x{h:.4}");

        // And it actually travelled: a fix that froze the shape would pass the
        // size check and be useless.
        let travelled = (l - first.2) * CAP as f32;
        assert!((travelled - 4.0 * step).abs() < 2.0,
            "expected to travel {} capture units, went {travelled:.1}", 4.0 * step);
    }

    #[test]
    fn raising_a_shape_repeatedly_does_not_grow_it() {
        // Z-order is a run of removals and re-adds, so a shape can be rebuilt
        // several times in one command and many times over a session. If each
        // rebuild moves the geometry at all, the shape creeps.
        //
        // The trap: the writer stores /Rect as the shape's extent INFLATED by
        // width/2 + 1 on every side so PDFium does not clip the stroke. Feeding
        // that reported rectangle back in as the new extent inflates it again.
        // Send to back then to front and the shape is visibly fatter.
        //
        // restyle_shape_annotation is the correct primitive for a raise: it
        // takes no bounds, undoes the pad itself, and rebuilds from the tag, so
        // the geometry is a fixed point.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let specs = [shape(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0)];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), 1), STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (start_l, start_t, start_r, start_b) = (first.2, first.3, first.4, first.5);

        let mut index = 0i32;
        for pass in 0..4 {
            let mut next = -1;
            assert_eq!(
                restyle_shape_annotation(handle, 0, index, 1000, 0, -1.0, &mut next),
                STATUS_OK_PDFIUM,
                "raise {pass} was refused"
            );
            index = next;
        }

        let after = read_annotations(handle, 0);
        close_document(handle);

        assert_eq!(after.len(), 1, "raising left duplicates: {}", after.len());
        let (_, _, l, t, r, b) = after[0];
        println!("RAISE DRIFT: ({start_l:.4},{start_t:.4},{start_r:.4},{start_b:.4}) \
                  -> ({l:.4},{t:.4},{r:.4},{b:.4})");

        // A pad re-applied four times would show up as roughly four stroke
        // widths of growth on each axis, which is obvious on screen.
        let tol = 0.002;
        assert!((l - start_l).abs() < tol && (t - start_t).abs() < tol
             && (r - start_r).abs() < tol && (b - start_b).abs() < tol,
            "the shape drifted: width {:.4} -> {:.4}, height {:.4} -> {:.4}",
            start_r - start_l, r - l, start_b - start_t, b - t);
    }

    #[test]
    fn raising_a_text_box_repeatedly_does_not_move_it() {
        // The same question as the shape and the stamp, for the one kind that
        // still answers it differently.
        //
        // RaiseToTop rebuilds a text box by calling resize_text_box_annotation
        // with the annotation's REPORTED rectangle. That is the mistake the
        // shape branch documents avoiding: a reported /Rect is not the box's own
        // upright rect, so feeding it back in as the new extent re-derives the
        // layout from the wrong rectangle and the box creeps. This reproduces
        // exactly that call, four times, which is what two clicks of Send to
        // Back and Bring to Front amount to.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_box(handle, "Hello Ayaan", 24.0), STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (start_l, start_t, start_r, start_b) = (first.2, first.3, first.4, first.5);

        let mut index = 0i32;
        for pass in 0..4 {
            let live = read_annotations(handle, 0);
            let at = live.iter().position(|a| a.1 == index).unwrap_or(0);
            let (_, _, l, t, r, b) = live[at];

            // Normalized rect back into capture space, which is what the C#
            // side does with item.Left/Top/Right/Bottom.
            let mut next = -1;
            assert_eq!(
                resize_text_box_annotation(
                    handle, 0, index, 1000,
                    l * 1000.0, t * 1000.0, r * 1000.0, b * 1000.0,
                    &mut next),
                STATUS_OK_PDFIUM,
                "raise {pass} was refused"
            );
            index = next;
        }

        let after = read_annotations(handle, 0);
        close_document(handle);

        assert_eq!(after.len(), 1, "raising left duplicates: {}", after.len());
        let (_, _, l, t, r, b) = after[0];
        println!("TEXT RAISE DRIFT: ({start_l:.4},{start_t:.4},{start_r:.4},{start_b:.4}) \
                  -> ({l:.4},{t:.4},{r:.4},{b:.4})");

        let tol = 0.002;
        assert!((l - start_l).abs() < tol && (t - start_t).abs() < tol
             && (r - start_r).abs() < tol && (b - start_b).abs() < tol,
            "the text box drifted: left {start_l:.4} -> {l:.4}, top {start_t:.4} -> {t:.4}, \
             width {:.4} -> {:.4}, height {:.4} -> {:.4}",
            start_r - start_l, r - l, start_b - start_t, b - t);
    }

    #[test]
    fn raising_a_ROTATED_text_box_repeatedly_does_not_move_it() {
        // The case the plain one above does not reach.
        //
        // A turned box reports an /Rect that is the axis-aligned bounding box of
        // its rotated content, which is BIGGER than the upright rect stored in
        // its tag. rotate_text_box_annotation's own documentation says the
        // caller must pass the upright rect "not the enlarged bounding box a
        // rotated box reports". RaiseToTop passes exactly that enlarged box, so
        // every raise re-lays the text out into a rectangle larger than the one
        // it belongs in, and the next raise enlarges it again.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(add_box(handle, "Hello Ayaan", 24.0), STATUS_OK_PDFIUM);

        // Turn it, passing the UPRIGHT rect as that function requires.
        let mut index = -1;
        assert_eq!(
            rotate_text_box_annotation(
                handle, 0, 0, 1000, 100.0, 100.0, 700.0, 400.0, 30.0, &mut index),
            STATUS_OK_PDFIUM);

        let first = read_annotations(handle, 0)[0];
        let (start_l, start_t, start_r, start_b) = (first.2, first.3, first.4, first.5);
        println!("ROTATED START: ({start_l:.4},{start_t:.4},{start_r:.4},{start_b:.4})");

        for pass in 0..4 {
            // The box's OWN UPRIGHT rect, which is what the tag stores and what
            // this function's documentation asks for. Passing the reported
            // rectangle instead is the bug: it is the enlarged bounding box of
            // the turned content, so each raise lays the text out into
            // something bigger and the next raise enlarges that again.
            let mut next = -1;
            assert_eq!(
                resize_text_box_annotation(
                    handle, 0, index, 1000, 100.0, 100.0, 700.0, 400.0, &mut next),
                STATUS_OK_PDFIUM,
                "raise {pass} was refused"
            );
            index = next;
            let now = read_annotations(handle, 0)[0];
            println!("  after raise {pass}: ({:.4},{:.4},{:.4},{:.4})", now.2, now.3, now.4, now.5);
        }

        let after = read_annotations(handle, 0);
        close_document(handle);

        let (_, _, l, t, r, b) = after[0];
        let tol = 0.002;
        assert!((l - start_l).abs() < tol && (t - start_t).abs() < tol
             && (r - start_r).abs() < tol && (b - start_b).abs() < tol,
            "the turned text box drifted: left {start_l:.4} -> {l:.4}, top {start_t:.4} -> {t:.4}, \
             width {:.4} -> {:.4}, height {:.4} -> {:.4}",
            start_r - start_l, r - l, start_b - start_t, b - t);
    }

    #[test]
    fn raising_a_stamp_repeatedly_does_not_move_it() {
        // Same question as the shape case, for the other kind the z-order
        // engine rebuilds. A stamp's /Rect is its image rectangle with no pad,
        // so feeding it back should be a fixed point, but "should" is what the
        // shape case said too.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let (pw, ph) = (40i32, 10i32);
        let pixels = marker_stamp_pixels(pw, ph);
        assert_eq!(
            add_stamp_annotation(handle, 0, CAP,
                0.30 * CAP as f32, 0.40 * CAP as f32, 0.70 * CAP as f32, 0.50 * CAP as f32,
                pixels.as_ptr(), pixels.len(), pw, ph),
            STATUS_OK_PDFIUM
        );

        let first = read_annotations(handle, 0)[0];
        let (sl, st, sr, sb) = (first.2, first.3, first.4, first.5);

        let mut index = 0i32;
        for _ in 0..4 {
            let cur = read_annotations(handle, 0)[0];
            let mut next = -1;
            assert_eq!(
                raise_stamp_annotation(handle, 0, index, CAP,
                    cur.2 * CAP as f32, cur.3 * CAP as f32,
                    cur.4 * CAP as f32, cur.5 * CAP as f32, &mut next),
                STATUS_OK_PDFIUM
            );
            index = next;
        }

        let after = read_annotations(handle, 0);
        close_document(handle);
        let (_, _, l, t, r, b) = after[0];
        println!("STAMP RAISE DRIFT: ({sl:.4},{st:.4},{sr:.4},{sb:.4}) -> ({l:.4},{t:.4},{r:.4},{b:.4})");

        assert_eq!(after.len(), 1);
        let tol = 0.002;
        assert!((l - sl).abs() < tol && (t - st).abs() < tol
             && (r - sr).abs() < tol && (b - sb).abs() < tol,
            "the stamp drifted: {:.4} wide -> {:.4}", sr - sl, r - l);
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

    /// Dumps a rendered page so the shapes can be LOOKED at. Ignored by
    /// default; run with `cargo test --release -- --ignored dump_shapes`.
    #[test]
    #[ignore]
    fn dump_shapes_for_inspection() {
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        let specs = [
            ShapeSpec { page_index: 0, kind: SHAPE_ARROW, x1: 80.0, y1: 100.0, x2: 700.0, y2: 100.0,
                        r: 200, g: 0, b: 0, a: 255, width_px: 3.0, rotation_deg: 0.0, fill_rgba: 0 , corner_radius_px: 0.0, shadow_angle_deg: 0.0, shadow_distance_px: 0.0, shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0},
            ShapeSpec { page_index: 0, kind: SHAPE_ARROW, x1: 80.0, y1: 200.0, x2: 700.0, y2: 320.0,
                        r: 0, g: 90, b: 200, a: 255, width_px: 8.0, rotation_deg: 0.0, fill_rgba: 0 , corner_radius_px: 0.0, shadow_angle_deg: 0.0, shadow_distance_px: 0.0, shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0},
            ShapeSpec { page_index: 0, kind: SHAPE_ARROW, x1: 700.0, y1: 420.0, x2: 80.0, y2: 420.0,
                        r: 0, g: 140, b: 60, a: 255, width_px: 1.5, rotation_deg: 0.0, fill_rgba: 0 , corner_radius_px: 0.0, shadow_angle_deg: 0.0, shadow_distance_px: 0.0, shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0},
            ShapeSpec { page_index: 0, kind: SHAPE_RECTANGLE, x1: 80.0, y1: 500.0, x2: 350.0, y2: 640.0,
                        r: 200, g: 0, b: 0, a: 255, width_px: 3.0, rotation_deg: 0.0, fill_rgba: 0 , corner_radius_px: 0.0, shadow_angle_deg: 0.0, shadow_distance_px: 0.0, shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0},
            ShapeSpec { page_index: 0, kind: SHAPE_ELLIPSE, x1: 420.0, y1: 500.0, x2: 700.0, y2: 640.0,
                        r: 0, g: 90, b: 200, a: 255, width_px: 3.0, rotation_deg: 0.0, fill_rgba: 0 , corner_radius_px: 0.0, shadow_angle_deg: 0.0, shadow_distance_px: 0.0, shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0},
        ];
        assert_eq!(add_shape_annotations(handle, 1000, specs.as_ptr(), specs.len()), STATUS_OK_PDFIUM);

        let r = render_region(handle, 0, 0.0, 0.0, 1.0, 1.0, 900);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };

        let out = std::env::var("SHAPE_DUMP").unwrap_or_else(|_| "shapes.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {} {}x{}", out, r.width, r.height);

        free_render_result(r);
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

    // ---- Form field enumeration + checkbox/radio setters -------------

    /// One field parsed out of the get_form_fields ByteBuffer, so the tests can
    /// assert on structured data rather than raw bytes.
    struct ParsedField {
        page_index: i32,
        kind: i32,
        flags: i32,
        group_index: i32,
        left: f32,
        top: f32,
        right: f32,
        bottom: f32,
        name: String,
        value: String,
    }

    fn parse_form_fields(buf: &ByteBuffer) -> Vec<ParsedField> {
        assert_eq!(buf.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
        let mut p = 0usize;
        let rd_u32 = |b: &[u8], p: &mut usize| {
            let v = u32::from_le_bytes(b[*p..*p + 4].try_into().unwrap());
            *p += 4;
            v
        };
        let rd_i32 = |b: &[u8], p: &mut usize| {
            let v = i32::from_le_bytes(b[*p..*p + 4].try_into().unwrap());
            *p += 4;
            v
        };
        let rd_f32 = |b: &[u8], p: &mut usize| {
            let v = f32::from_le_bytes(b[*p..*p + 4].try_into().unwrap());
            *p += 4;
            v
        };
        let count = rd_u32(bytes, &mut p);
        let mut out = Vec::new();
        for _ in 0..count {
            let page_index = rd_i32(bytes, &mut p);
            let kind = rd_i32(bytes, &mut p);
            let flags = rd_i32(bytes, &mut p);
            let group_index = rd_i32(bytes, &mut p);
            let left = rd_f32(bytes, &mut p);
            let top = rd_f32(bytes, &mut p);
            let right = rd_f32(bytes, &mut p);
            let bottom = rd_f32(bytes, &mut p);
            let nlen = rd_u32(bytes, &mut p) as usize;
            let name = String::from_utf8(bytes[p..p + nlen].to_vec()).unwrap();
            p += nlen;
            let vlen = rd_u32(bytes, &mut p) as usize;
            let value = String::from_utf8(bytes[p..p + vlen].to_vec()).unwrap();
            p += vlen;
            out.push(ParsedField {
                page_index,
                kind,
                flags,
                group_index,
                left,
                top,
                right,
                bottom,
                name,
                value,
            });
        }
        assert_eq!(p, buf.len, "parser consumed exactly the whole buffer");
        out
    }

    #[test]
    fn get_form_fields_enumerates_every_kind_with_name_rect_and_value() {
        let handle = open_fixture_named("tests/fixtures/sample_form_rich.pdf");
        let buf = get_form_fields(handle);
        let fields = parse_form_fields(&buf);
        free_byte_buffer(buf);

        // Text, checkbox, two radio widgets, one choice = five widgets.
        assert_eq!(fields.len(), 5, "expected one widget per control (radio has two)");

        let text = fields.iter().find(|f| f.name == "FullName").unwrap();
        assert_eq!(text.kind, FIELD_TEXT);
        assert_eq!(text.page_index, 0);
        // Rect is normalized top-left / page width, so within [0, ~1.3] and
        // ordered left<right, top<bottom.
        assert!(text.left > 0.0 && text.left < 1.0, "left {}", text.left);
        assert!(text.right > text.left);
        assert!(text.bottom > text.top);

        let checkbox = fields.iter().find(|f| f.name == "Subscribe").unwrap();
        assert_eq!(checkbox.kind, FIELD_CHECKBOX);
        assert_eq!(checkbox.flags & FIELD_FLAG_CHECKED, 0, "starts unchecked");

        let radios: Vec<_> = fields.iter().filter(|f| f.name == "Plan").collect();
        assert_eq!(radios.len(), 2, "two radio widgets share the group name");
        assert!(radios.iter().all(|r| r.kind == FIELD_RADIO));
        // Each widget carries a distinct group index, and the group's selected
        // value ("Pro") is reported on every widget.
        let mut indices: Vec<i32> = radios.iter().map(|r| r.group_index).collect();
        indices.sort();
        assert_eq!(indices, vec![0, 1], "widgets have unique group indices");
        assert!(radios.iter().all(|r| r.value == "Pro"), "group's selected value");
        // Exactly one widget is checked (the one PDFium considers selected).
        let checked = radios.iter().filter(|r| r.flags & FIELD_FLAG_CHECKED != 0).count();
        assert_eq!(checked, 1, "exactly one radio in the group is on");

        let choice = fields.iter().find(|f| f.name == "Country").unwrap();
        assert_eq!(choice.kind, FIELD_COMBO);

        close_document(handle);
    }

    #[derive(Debug)]
    struct ParsedBookmark {
        depth: i32,
        page_index: i32,
        title: String,
    }

    fn parse_bookmarks(buf: &ByteBuffer) -> Vec<ParsedBookmark> {
        assert_eq!(buf.status, STATUS_OK_PDFIUM, "get_bookmarks failed");
        let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
        let count = u32::from_le_bytes(bytes[0..4].try_into().unwrap()) as usize;

        let mut out = Vec::with_capacity(count);
        let mut p = 4usize;
        for _ in 0..count {
            let depth = i32::from_le_bytes(bytes[p..p + 4].try_into().unwrap());
            let page_index = i32::from_le_bytes(bytes[p + 4..p + 8].try_into().unwrap());
            let len = u32::from_le_bytes(bytes[p + 8..p + 12].try_into().unwrap()) as usize;
            let title = String::from_utf8(bytes[p + 12..p + 12 + len].to_vec()).unwrap();
            p += 12 + len;
            out.push(ParsedBookmark { depth, page_index, title });
        }
        assert_eq!(p, buf.len, "trailing bytes: the entries did not fill the buffer");
        out
    }

    #[test]
    fn get_bookmarks_reads_the_outline_in_reading_order_with_its_nesting() {
        let handle = open_fixture_named("tests/fixtures/sample_outline.pdf");
        let buf = get_bookmarks(handle);
        let marks = parse_bookmarks(&buf);
        free_byte_buffer(buf);

        // PRE-ORDER: a child comes directly after its parent, not after the
        // parent's siblings. That is what lets the panel render the list top to
        // bottom and indent by depth without rebuilding a tree.
        let shape: Vec<(&str, i32, i32)> = marks
            .iter()
            .map(|m| (m.title.as_str(), m.depth, m.page_index))
            .collect();
        assert_eq!(
            shape,
            vec![
                ("Chapter One", 0, 0),
                ("Section 1.1", 1, 1),
                // Target given as a GoTo ACTION rather than a /Dest. Real files
                // use both, and reading only /Dest loses these silently.
                ("Chapter Two", 0, 2),
                // No target at all. It is still part of the author's outline,
                // so it is listed, with -1 saying it goes nowhere.
                ("Nowhere", 0, -1),
            ]
        );

        close_document(handle);
    }

    #[test]
    fn get_bookmarks_rejects_bad_input_and_is_empty_for_a_document_without_an_outline() {
        assert_eq!(get_bookmarks(0).status, STATUS_INVALID_INPUT);
        assert_eq!(get_bookmarks(999_999).status, STATUS_INVALID_INPUT);

        // No outline is a successful EMPTY answer, not a failure: most PDFs
        // have none, and the panel needs to tell "nothing to show" apart from
        // "could not read it".
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        let buf = get_bookmarks(handle);
        let marks = parse_bookmarks(&buf);
        free_byte_buffer(buf);
        assert!(marks.is_empty(), "expected no bookmarks, got {marks:?}");

        close_document(handle);
    }

    /// Serializes entries in the layout write_outline expects, which is the
    /// same one get_bookmarks produces.
    fn outline_buffer(entries: &[(i32, i32, &str)]) -> Vec<u8> {
        let mut out = (entries.len() as u32).to_le_bytes().to_vec();
        for (depth, page, title) in entries {
            out.extend_from_slice(&depth.to_le_bytes());
            out.extend_from_slice(&page.to_le_bytes());
            out.extend_from_slice(&(title.len() as u32).to_le_bytes());
            out.extend_from_slice(title.as_bytes());
        }
        out
    }

    fn write_outline_to(src: &str, dst: &std::path::Path, entries: &[(i32, i32, &str)]) -> i32 {
        let buf = outline_buffer(entries);
        let c_src = std::ffi::CString::new(src).unwrap();
        let c_dst = std::ffi::CString::new(dst.to_str().unwrap()).unwrap();
        unsafe {
            crate::outline::write_outline(c_src.as_ptr(), c_dst.as_ptr(), buf.as_ptr(), buf.len())
        }
    }

    fn read_back(path: &std::path::Path) -> Vec<ParsedBookmark> {
        let handle = open_fixture_named(path.to_str().unwrap());
        assert_ne!(handle, 0, "could not reopen the written file");
        let buf = get_bookmarks(handle);
        let marks = parse_bookmarks(&buf);
        free_byte_buffer(buf);
        close_document(handle);
        marks
    }

    #[test]
    fn an_outline_written_by_lopdf_is_read_back_by_pdfium() {
        // The real proof that the writer works: two independent libraries, one
        // writing the /Outlines tree as raw PDF objects and the other reading
        // it through its own parser, have to agree about the result. A test
        // that only read the file back with lopdf would mostly be checking that
        // lopdf can parse its own output.
        let out = std::env::temp_dir().join("ayaan_outline_roundtrip.pdf");
        let status = write_outline_to(
            "tests/fixtures/sample_20pages.pdf",
            &out,
            &[
                (0, 0, "Chapter One"),
                (1, 3, "Section 1.1"),
                (2, 4, "Detail 1.1.1"),
                (0, 9, "Chapter Two"),
            ],
        );
        assert_eq!(status, STATUS_OK_PDFIUM);

        let marks = read_back(&out);
        let shape: Vec<(&str, i32, i32)> = marks
            .iter()
            .map(|m| (m.title.as_str(), m.depth, m.page_index))
            .collect();

        assert_eq!(
            shape,
            vec![
                ("Chapter One", 0, 0),
                ("Section 1.1", 1, 3),
                ("Detail 1.1.1", 2, 4),
                ("Chapter Two", 0, 9),
            ]
        );

        let _ = std::fs::remove_file(&out);
    }

    #[test]
    fn a_title_in_devanagari_or_burmese_survives_the_write() {
        // PDFDocEncoding cannot represent these at all, so the title has to go
        // out as UTF-16BE behind a byte-order mark. Writing the raw UTF-8 bytes
        // would come back as mojibake, and this is the user's own alphabet.
        let out = std::env::temp_dir().join("ayaan_outline_unicode.pdf");
        let status = write_outline_to(
            "tests/fixtures/sample_20pages.pdf",
            &out,
            &[(0, 0, "अध्याय एक"), (0, 1, "နောက်ဆုံး"), (0, 2, "Plain ASCII")],
        );
        assert_eq!(status, STATUS_OK_PDFIUM);

        let titles: Vec<String> = read_back(&out).into_iter().map(|m| m.title).collect();
        assert_eq!(titles, vec!["अध्याय एक", "နောက်ဆုံး", "Plain ASCII"]);

        let _ = std::fs::remove_file(&out);
    }

    #[test]
    fn an_entry_with_no_page_is_written_and_comes_back_targetless() {
        let out = std::env::temp_dir().join("ayaan_outline_notarget.pdf");
        let status = write_outline_to(
            "tests/fixtures/sample_20pages.pdf",
            &out,
            &[(0, -1, "Preface"), (0, 2, "Real page")],
        );
        assert_eq!(status, STATUS_OK_PDFIUM);

        let marks = read_back(&out);
        assert_eq!(marks[0].page_index, -1, "no destination was written");
        assert_eq!(marks[1].page_index, 2);

        let _ = std::fs::remove_file(&out);
    }

    #[test]
    fn a_depth_that_skips_a_generation_is_still_written_as_a_tree() {
        // An outline entry with no parent cannot be expressed in a PDF at all,
        // so a depth deeper than the tree has reached is pulled up rather than
        // producing a broken file.
        let out = std::env::temp_dir().join("ayaan_outline_gap.pdf");
        let status = write_outline_to(
            "tests/fixtures/sample_20pages.pdf",
            &out,
            &[(3, 0, "Starts deep"), (7, 1, "Deeper still")],
        );
        assert_eq!(status, STATUS_OK_PDFIUM);

        let marks = read_back(&out);
        assert_eq!(marks[0].depth, 0, "first entry must be top level");
        assert_eq!(marks[1].depth, 1, "a child may only be one deeper");

        let _ = std::fs::remove_file(&out);
    }

    #[test]
    fn regenerating_replaces_the_previous_outline_instead_of_stacking_on_it() {
        // Running auto-bookmark twice must not leave the first outline in the
        // file, as unreferenced objects that grow it on every run.
        let first = std::env::temp_dir().join("ayaan_outline_first.pdf");
        let second = std::env::temp_dir().join("ayaan_outline_second.pdf");

        assert_eq!(
            write_outline_to("tests/fixtures/sample_20pages.pdf", &first,
                &[(0, 0, "Old One"), (0, 1, "Old Two")]),
            STATUS_OK_PDFIUM
        );
        assert_eq!(
            write_outline_to(first.to_str().unwrap(), &second, &[(0, 5, "New Only")]),
            STATUS_OK_PDFIUM
        );

        let titles: Vec<String> = read_back(&second).into_iter().map(|m| m.title).collect();
        assert_eq!(titles, vec!["New Only"], "the old outline is gone, not appended to");

        let _ = std::fs::remove_file(&first);
        let _ = std::fs::remove_file(&second);
    }

    #[test]
    fn an_empty_list_removes_the_outline() {
        let out = std::env::temp_dir().join("ayaan_outline_cleared.pdf");
        assert_eq!(
            write_outline_to("tests/fixtures/sample_outline.pdf", &out, &[]),
            STATUS_OK_PDFIUM
        );

        assert!(read_back(&out).is_empty(), "clearing must leave no bookmarks");

        let _ = std::fs::remove_file(&out);
    }

    #[test]
    fn write_outline_rejects_bad_input_without_writing_anything() {
        let out = std::env::temp_dir().join("ayaan_outline_never.pdf");
        let _ = std::fs::remove_file(&out);

        let buf = outline_buffer(&[(0, 0, "One")]);
        let c_out = std::ffi::CString::new(out.to_str().unwrap()).unwrap();

        // Null paths.
        assert_eq!(
            unsafe { crate::outline::write_outline(std::ptr::null(), c_out.as_ptr(), buf.as_ptr(), buf.len()) },
            STATUS_INVALID_INPUT
        );

        // A count that promises more entries than the buffer holds. Reading it
        // would run off the end of memory the caller owns.
        let mut lying = buf.clone();
        lying[0..4].copy_from_slice(&99u32.to_le_bytes());
        let c_src = std::ffi::CString::new("tests/fixtures/sample_20pages.pdf").unwrap();
        assert_eq!(
            unsafe { crate::outline::write_outline(c_src.as_ptr(), c_out.as_ptr(), lying.as_ptr(), lying.len()) },
            STATUS_INVALID_INPUT
        );

        // A source that is not a PDF at all.
        let c_bad = std::ffi::CString::new("tests/fixtures/does_not_exist.pdf").unwrap();
        assert_eq!(
            unsafe { crate::outline::write_outline(c_bad.as_ptr(), c_out.as_ptr(), buf.as_ptr(), buf.len()) },
            STATUS_UNSUPPORTED
        );

        assert!(!out.exists(), "a rejected call must not have written a file");
    }

    /// Writes a Burmese text box to a real file, so what a FOREIGN reader sees
    /// can be checked in the bytes rather than inferred. Ignored by default:
    ///   set TEXTBOX_OUT=C:\path\to\out.pdf
    ///   cargo test dump_text_box_contents -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_text_box_contents_to_a_file() {
        let Ok(out) = std::env::var("TEXTBOX_OUT") else {
            eprintln!("set TEXTBOX_OUT to a destination path");
            return;
        };

        let words = "မေတ္တာ သစ္စာ အတို့အမြှုပ်";
        let bytes = words.as_bytes();
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 200.0,
                bytes.as_ptr(), bytes.len(), 28.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, std::ptr::null(), 0, 0, 0),
            STATUS_OK_PDFIUM);

        let c_out = std::ffi::CString::new(out.clone()).unwrap();
        assert_eq!(unsafe { save_document(h, c_out.as_ptr()) }, STATUS_OK_PDFIUM);
        println!("wrote {out}");
        close_document(h);
    }

    /// Does PDFium embed the font bytes we hand it verbatim, or subset them?
    /// The answer decides how our font is identified in the saved file when the
    /// /ToUnicode CMap is attached. Ignored by default:
    ///   cargo test dump_embedded_font_bytes -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_embedded_font_bytes() {
        let font_path = r"C:\Windows\Fonts\mmrtext.ttf";
        let Ok(source) = std::fs::read(font_path) else {
            eprintln!("no {font_path}");
            return;
        };
        println!("source font: {} bytes", source.len());

        let words = "မေတ္တာ".as_bytes();
        let font_utf8 = font_path.as_bytes();
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 200.0,
                words.as_ptr(), words.len(), 28.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, font_utf8.as_ptr(), font_utf8.len(), 0, 0),
            STATUS_OK_PDFIUM);

        let out = std::env::temp_dir().join("ayaan_font_probe.pdf");
        let c_out = std::ffi::CString::new(out.to_str().unwrap()).unwrap();
        assert_eq!(unsafe { save_document(h, c_out.as_ptr()) }, STATUS_OK_PDFIUM);
        close_document(h);

        // Read the saved file back with lopdf and describe every font in it.
        let doc = lopdf::Document::load(&out).unwrap();
        for (id, obj) in doc.objects.iter() {
            let lopdf::Object::Dictionary(d) = obj else { continue };
            if d.get(b"Type").and_then(|o| o.as_name()).ok() != Some(b"Font") {
                continue;
            }
            let subtype = d.get(b"Subtype").and_then(|o| o.as_name()).ok()
                .map(|n| String::from_utf8_lossy(n).to_string()).unwrap_or_default();
            let base = d.get(b"BaseFont").and_then(|o| o.as_name()).ok()
                .map(|n| String::from_utf8_lossy(n).to_string()).unwrap_or_default();
            let enc = d.get(b"Encoding").and_then(|o| o.as_name()).ok()
                .map(|n| String::from_utf8_lossy(n).to_string()).unwrap_or_default();
            let has_tu = d.has(b"ToUnicode");
            println!("{id:?} {subtype} base={base} enc={enc} ToUnicode={has_tu}");
        }

        // And every embedded font file, with its size next to the source's.
        for (id, obj) in doc.objects.iter() {
            let lopdf::Object::Stream(s) = obj else { continue };
            if !s.dict.has(b"Length1") && !s.dict.has(b"Subtype") {
                continue;
            }
            if let Ok(data) = s.decompressed_content() {
                println!(
                    "stream {id:?}: {} bytes, identical to source = {}",
                    data.len(),
                    data == source
                );
            }
        }

        // And the ToUnicode CMap PDFium wrote by itself, if any.
        for (id, obj) in doc.objects.iter() {
            let lopdf::Object::Dictionary(d) = obj else { continue };
            if d.get(b"Type").and_then(|o| o.as_name()).ok() != Some(b"Font") { continue; }
            if let Ok(lopdf::Object::Reference(tu)) = d.get(b"ToUnicode") {
                if let Ok(lopdf::Object::Stream(s)) = doc.get_object(*tu) {
                    let body = s.decompressed_content().unwrap_or_default();
                    println!("--- ToUnicode of {id:?} ({} bytes) ---", body.len());
                    println!("{}", String::from_utf8_lossy(&body));
                }
            }
        }

        let _ = std::fs::remove_file(&out);
    }

    /// SPIKE: does an invisible text run in the PAGE CONTENT make our words
    /// searchable without changing a single pixel? Ignored by default:
    ///   cargo test dump_invisible_text_layer -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_invisible_text_layer() {
        use pdfium_render::prelude::*;

        let font_path = r"C:\Windows\Fonts\mmrtext.ttf";
        let Ok(font_bytes) = std::fs::read(font_path) else {
            eprintln!("no {font_path}");
            return;
        };
        let words = "\u{1019}\u{1031}\u{1010}\u{1039}\u{1010}\u{102c}";
        let bytes = words.as_bytes();
        let font_utf8 = font_path.as_bytes();

        // A page with our text box on it, exactly as the app writes one.
        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 200.0,
                bytes.as_ptr(), bytes.len(), 28.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, font_utf8.as_ptr(), font_utf8.len(), 0, 0),
            STATUS_OK_PDFIUM);

        let before = render_low_res(h, 0, 900);
        let before_pixels =
            unsafe { std::slice::from_raw_parts(before.buffer, before.len as usize) }.to_vec();
        free_render_result(before);

        // Now add the SAME words as an invisible run straight into the page.
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&h).cloned().unwrap();
            let mut doc_guard = lock(&doc);
            let token = doc_guard
                .fonts_mut()
                .load_true_type_from_bytes(&font_bytes, true)
                .expect("font load failed");
            let font = doc_guard.fonts().get(token).expect("font token lookup failed");
            let mut page = doc_guard.pages().get(0).unwrap();
            let mut obj = PdfPageTextObject::new(
                &doc_guard, words, font, PdfPoints::new(28.0)).expect("text object failed");
            obj.set_render_mode(PdfPageTextRenderMode::Invisible).expect("render mode failed");
            obj.translate(PdfPoints::new(100.0), PdfPoints::new(600.0)).unwrap();
            page.objects_mut().add_text_object(obj).expect("add failed");
            page.regenerate_content().expect("regenerate failed");
        }
        evict_all_cache_for_doc(h);

        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);

        let text = page_text(reopened, 0);
        println!("expected words   : {words:?}");
        println!("extracted        : {text:?}");
        println!("SEARCHABLE       : {}", text.contains(words));

        let after = render_low_res(reopened, 0, 900);
        let after_pixels =
            unsafe { std::slice::from_raw_parts(after.buffer, after.len as usize) }.to_vec();
        free_render_result(after);
        println!(
            "PIXELS UNCHANGED : {} ({} vs {} bytes)",
            before_pixels == after_pixels, before_pixels.len(), after_pixels.len());

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    /// STEP 1 PROBE: why does disabling the duplicate guard not make
    /// saving_twice_does_not_find_the_same_words_twice fail?
    ///
    /// Counts page objects after each sync, alongside what the extractor
    /// reports. Ignored by default:
    ///   cargo test dump_text_layer_object_counts -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_text_layer_object_counts() {
        let words = "Countable Words";
        let h = page_with_text_box(words, None);
        let pages = [0i32];

        println!("objects before any sync : {}", page_object_count(h, 0));

        for n in 1..=3 {
            let status = unsafe { sync_text_layer(h, pages.as_ptr(), pages.len()) };
            let text = {
                let saved = snapshot_document(h);
                let reopened = open_document_from_bytes(saved.data, saved.len);
                let t = page_text(reopened, 0);
                close_document(reopened);
                free_byte_buffer(saved);
                t
            };
            println!(
                "after sync {n}: status={status} objects={} matches={}",
                page_object_count(h, 0),
                text.matches(words).count()
            );
        }

        close_document(h);
    }

    /// SPIKE for hardening the searchable layer. Three things have to work
    /// together before the design is worth building:
    ///   1. a page object can carry a MARK with a string param (the owning
    ///      Ayaan object's id), so association is by identity, not by index or
    ///      by matching text;
    ///   2. that mark survives a save and reopen;
    ///   3. an object can be REMOVED without crashing, which is what killed the
    ///      first attempt (ILLEGAL_INSTRUCTION, then ACCESS_VIOLATION).
    ///
    /// The hypothesis for (3): pdfium-render regenerates page content on EVERY
    /// change by default, and that invalidates the object handles still being
    /// walked. Manual regeneration should make removal safe.
    ///   cargo test dump_mark_and_remove_spike -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_mark_and_remove_spike() {
        use pdfium_render::prelude::*;

        const MARK: &str = "AyaanSearch";
        let id_a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        let id_b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");

        // --- 1. add two marked invisible runs -------------------------------
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&h).cloned().unwrap();
            let mut doc_guard = lock(&doc);
            let token = doc_guard.fonts_mut().helvetica();

            let mut page = doc_guard.pages().get(0).unwrap();
            page.set_content_regeneration_strategy(PdfPageContentRegenerationStrategy::Manual);

            for (id, words) in [(id_a, "Alpha Marked Run"), (id_b, "Beta Marked Run")] {
                let font = doc_guard.fonts().get(token).unwrap();
                let mut obj =
                    PdfPageTextObject::new(&doc_guard, words, font, PdfPoints::new(24.0)).unwrap();
                obj.set_render_mode(PdfPageTextRenderMode::Invisible).unwrap();
                obj.translate(PdfPoints::new(72.0), PdfPoints::new(600.0)).unwrap();

                let attached = page.objects_mut().add_object(obj.into()).unwrap();
                if let PdfPageObject::Text(t) = &attached {
                    // The id rides in the mark's NAME, not in a string param.
                    // SetStringParam needs the document handle, which the crate
                    // does not expose publicly, and the name alone is enough to
                    // carry 32 hex digits.
                    let name = format!("{MARK}:{id}");
                    let mark = doc_guard.bindings().FPDFPageObj_AddMark(t.object_handle(), &name);
                    println!("added {id}: mark_null={}", mark.is_null());
                }
            }
            page.regenerate_content().unwrap();
        }
        println!("objects after adding two : {}", page_object_count(h, 0));

        // --- 2. does the mark survive a save and reopen? ---------------------
        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        println!("reopened objects         : {}", page_object_count(reopened, 0));
        println!("reopened text            : {:?}", page_text(reopened, 0));

        // --- 3. read the marks back, and REMOVE only object A ---------------
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&reopened).cloned().unwrap();
            let doc_guard = lock(&doc);
            let mut page = doc_guard.pages().get(0).unwrap();
            page.set_content_regeneration_strategy(PdfPageContentRegenerationStrategy::Manual);

            // Which index carries id_a? Read every object's marks first, with
            // no mutation at all while walking.
            let mut found: Vec<(usize, String)> = Vec::new();
            {
                let objects = page.objects();
                for i in 0..objects.len() {
                    let Ok(obj) = objects.get(i) else { continue };
                    let PdfPageObject::Text(t) = &obj else { continue };
                    let bindings = doc_guard.bindings();
                    let handle = t.object_handle();
                    for m in 0..bindings.FPDFPageObj_CountMarks(handle).max(0) {
                        let mark = bindings.FPDFPageObj_GetMark(handle, m as std::os::raw::c_ulong);
                        if mark.is_null() { continue; }
                        let mut out: std::os::raw::c_ulong = 0;
                        bindings.FPDFPageObjMark_GetName(mark, std::ptr::null_mut(), 0, &mut out);
                        if out <= 2 { continue; }
                        let mut buf = vec![0u16; out as usize / 2];
                        bindings.FPDFPageObjMark_GetName(mark, buf.as_mut_ptr(), out, &mut out);
                        while buf.last() == Some(&0) { buf.pop(); }
                        if let Ok(name) = String::from_utf16(&buf) {
                            if let Some(id) = name.strip_prefix(&format!("{MARK}:")) {
                                found.push((i as usize, id.to_string()));
                            }
                        }
                    }
                }
            }
            println!("marks read back          : {found:?}");

            if let Some((index, _)) = found.iter().find(|(_, s)| s == id_a) {
                let obj = page.objects().get(*index as PdfPageObjectIndex).unwrap();
                match page.objects_mut().remove_object(obj) {
                    Ok(removed) => {
                        println!("removed index {index}; forgetting instead of dropping");
                        std::mem::forget(removed);
                        println!("forget survived");
                    }
                    Err(e) => println!("remove failed: {e:?}"),
                }
            }
            println!("about to regenerate");
            page.regenerate_content().unwrap();
            println!("regenerate survived");
        }

        println!("objects after removing A : {}", page_object_count(reopened, 0));
        println!("text after removing A    : {:?}", page_text(reopened, 0));

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    /// Can PDFium's own extractor read our text back? Ignored by default:
    ///   cargo test dump_is_our_text_extractable -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_is_our_text_extractable() {
        let font_path = r"C:\Windows\Fonts\mmrtext.ttf";
        if std::fs::metadata(font_path).is_err() {
            eprintln!("no {font_path}");
            return;
        }
        let font_utf8 = font_path.as_bytes();
        let words = "\u{1019}\u{1031}\u{1010}\u{1039}\u{1010}\u{102c}";
        let bytes = words.as_bytes();

        let h = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        assert_eq!(
            add_text_box_annotation_styled(h, 0, 1000, 100.0, 100.0, 700.0, 200.0,
                bytes.as_ptr(), bytes.len(), 28.0, 0, 0, 0, 255,
                ALIGN_LEFT, 0, 0, 0.0, font_utf8.as_ptr(), font_utf8.len(), 0, 0),
            STATUS_OK_PDFIUM);

        let saved = snapshot_document(h);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let text = page_text(reopened, 0);

        println!("expected page text : {words:?}");
        println!("extracted page text: {text:?}");
        println!("contains our words : {}", text.contains(words));

        close_document(reopened);
        close_document(h);
        free_byte_buffer(saved);
    }

    /// Renders a page of any document to raw BGRA, for looking at a real file
    /// rather than reasoning about it. Ignored by default; run with
    ///   set RENDER_PDF=C:\path\to\file.pdf   (optionally RENDER_PAGE, RENDER_W)
    ///   cargo test dump_render_page -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_render_page_of_a_real_document() {
        let Ok(path) = std::env::var("RENDER_PDF") else {
            eprintln!("set RENDER_PDF to a file path");
            return;
        };
        let page: i32 = std::env::var("RENDER_PAGE").ok().and_then(|v| v.parse().ok()).unwrap_or(0);
        let width: i32 = std::env::var("RENDER_W").ok().and_then(|v| v.parse().ok()).unwrap_or(1400);

        let handle = open_fixture_named(&path);
        assert_ne!(handle, 0, "could not open {path}");
        println!("pages: {}", get_page_count(handle));

        let r = render_low_res(handle, page, width);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("RENDER_OUT").unwrap_or_else(|_| "page.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);

        free_render_result(r);
        close_document(handle);
    }

    /// Writes an outline into a real book, to check the writer against a file
    /// size the fixtures cannot stand in for. Ignored by default; run with
    ///   set OUTLINE_PDF=C:\path\to\book.pdf
    ///   cargo test dump_write_outline -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_write_outline_into_a_real_document() {
        let Ok(path) = std::env::var("OUTLINE_PDF") else {
            eprintln!("set OUTLINE_PDF to a file path");
            return;
        };

        let out = std::env::temp_dir().join("ayaan_outline_big.pdf");
        let entries: Vec<(i32, i32, String)> = (0..18)
            .map(|i| (if i % 3 == 0 { 0 } else { 1 }, i * 150, format!("अध्याय {}", i + 1)))
            .collect();
        let borrowed: Vec<(i32, i32, &str)> =
            entries.iter().map(|(d, p, t)| (*d, *p, t.as_str())).collect();

        let started = std::time::Instant::now();
        let status = write_outline_to(&path, &out, &borrowed);
        let elapsed = started.elapsed();
        assert_eq!(status, STATUS_OK_PDFIUM, "write failed");

        let before = std::fs::metadata(&path).unwrap().len();
        let after = std::fs::metadata(&out).unwrap().len();
        println!(
            "wrote in {elapsed:?}; {:.1} MB -> {:.1} MB",
            before as f64 / 1e6,
            after as f64 / 1e6
        );

        let marks = read_back(&out);
        println!("read back {} bookmarks", marks.len());
        for m in marks.iter().take(4) {
            println!("{}[p{}] {}", "    ".repeat(m.depth as usize), m.page_index + 1, m.title);
        }
        assert_eq!(marks.len(), 18);

        let _ = std::fs::remove_file(&out);
    }

    /// Dumps every page's text, one page per record separated by a form feed,
    /// so the heading detector on the C# side can be checked against a real
    /// document without that document having to live in the repository.
    ///   set TEXT_PDF=C:\path\to\file.pdf
    ///   set TEXT_OUT=C:\path\to\dump.txt
    ///   cargo test dump_page_text -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_page_text_of_a_real_document() {
        let (Ok(path), Ok(out)) = (std::env::var("TEXT_PDF"), std::env::var("TEXT_OUT")) else {
            eprintln!("set TEXT_PDF and TEXT_OUT");
            return;
        };

        let handle = open_fixture_named(&path);
        assert_ne!(handle, 0, "could not open {path}");

        let pages = get_page_count(handle);
        let mut dump = String::new();
        for i in 0..pages {
            if i > 0 {
                dump.push('\u{000C}');
            }
            dump.push_str(&page_text(handle, i));
        }

        std::fs::write(&out, dump).unwrap();
        println!("wrote {pages} pages of text to {out}");
        close_document(handle);
    }

    /// Prints the outline of a real book, for checking the reader against a
    /// file the fixtures cannot stand in for. Ignored by default; run with
    ///   set BOOKMARK_PDF=C:\path\to\book.pdf
    ///   cargo test dump_bookmarks -- --ignored --nocapture
    #[test]
    #[ignore]
    fn dump_bookmarks_of_a_real_document() {
        let Ok(path) = std::env::var("BOOKMARK_PDF") else {
            eprintln!("set BOOKMARK_PDF to a file path");
            return;
        };

        let handle = open_fixture_named(&path);
        assert_ne!(handle, 0, "could not open {path}");

        let started = std::time::Instant::now();
        let buf = get_bookmarks(handle);
        let elapsed = started.elapsed();
        let marks = parse_bookmarks(&buf);
        free_byte_buffer(buf);

        println!(
            "{} pages, {} bookmarks, read in {:?}",
            get_page_count(handle),
            marks.len(),
            elapsed
        );
        for m in marks.iter().take(15) {
            println!("{}[p{}] {}", "    ".repeat(m.depth as usize), m.page_index + 1, m.title);
        }

        close_document(handle);
    }

    #[test]
    #[ignore]
    fn dump_form_deleted_widget_with_text_for_inspection() {
        // Deletes the FullName widget, then draws a text box where it was, and
        // renders — to confirm that removing the widget stops the form layer
        // painting the field box, so the app's text shows instead of hiding
        // behind it. Run: cargo test --release dump_form_deleted -- --ignored --nocapture
        let handle = open_fixture_named("tests/fixtures/sample_form_rich.pdf");

        let name = std::ffi::CString::new("FullName").unwrap();
        assert_eq!(delete_form_field_widget(handle, name.as_ptr()), STATUS_OK_PDFIUM);

        // FullName rect normalized (0.278, 0.098, 0.637, 0.131); text-box coords
        // are capture-space pixels, so multiply by the capture width.
        let cap = 900.0f32;
        let text = "Aung Ko Ko";
        assert_eq!(
            add_text_box_annotation(
                handle, 0, cap as i32,
                0.288 * cap, 0.103 * cap, 0.627 * cap, 0.126 * cap,
                text.as_ptr(), text.len(), 0.020 * cap,
                0, 0, 0, 255,
            ),
            STATUS_OK_PDFIUM
        );

        let r = render_low_res(handle, 0, 900);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len as usize) };
        let out = std::env::var("DELW_DUMP").unwrap_or_else(|_| "delw.raw".to_string());
        std::fs::write(&out, bytes).unwrap();
        println!("DUMP {out} {}x{}", r.width, r.height);
        free_render_result(r);
        close_document(handle);
    }

    #[test]
    fn delete_form_field_widget_removes_it_from_enumeration_and_tolerates_a_missing_field() {
        let handle = open_fixture_named("tests/fixtures/sample_form_rich.pdf");

        let name = std::ffi::CString::new("FullName").unwrap();
        assert_eq!(delete_form_field_widget(handle, name.as_ptr()), STATUS_OK_PDFIUM);

        // FullName is gone; the other fields remain.
        let buf = get_form_fields(handle);
        let fields = parse_form_fields(&buf);
        free_byte_buffer(buf);
        assert!(!fields.iter().any(|f| f.name == "FullName"), "FullName widget removed");
        assert!(fields.iter().any(|f| f.name == "Subscribe"), "other fields untouched");

        // Deleting an absent field is a successful no-op; bad input is rejected.
        let missing = std::ffi::CString::new("Nope").unwrap();
        assert_eq!(delete_form_field_widget(handle, missing.as_ptr()), STATUS_OK_PDFIUM);
        assert_eq!(delete_form_field_widget(0, std::ptr::null()), STATUS_INVALID_INPUT);

        close_document(handle);
    }

    #[test]
    fn get_form_fields_rejects_bad_input_and_is_empty_for_a_formless_document() {
        assert_eq!(get_form_fields(0).status, STATUS_INVALID_INPUT);

        // A document with no form: enumeration succeeds and is empty.
        let handle = open_fixture_named("tests/fixtures/sample.pdf");
        let buf = get_form_fields(handle);
        let fields = parse_form_fields(&buf);
        free_byte_buffer(buf);
        assert_eq!(fields.len(), 0);
        close_document(handle);
    }

    /// The bounding box of RED ink in a rendered page, in NORMALIZED units
    /// matching the app's convention: both axes divided by the render WIDTH,
    /// top-left origin. Returns None if no red pixels are present.
    ///
    /// Red rather than "any dark pixel" so a fixture that already carries
    /// black page text cannot be mistaken for the shape under test. Buffer is
    /// BGRA, so index +2 is the red channel.
    fn red_bbox_norm(handle: u64, width: i32) -> Option<(f32, f32, f32, f32)> {
        let (bytes, w) = render_bytes(handle, 0, width);
        let h = bytes.len() / (w * 4);
        let (mut lo_x, mut lo_y, mut hi_x, mut hi_y) = (usize::MAX, usize::MAX, 0usize, 0usize);
        let mut found = false;
        for y in 0..h {
            for x in 0..w {
                let px = (y * w + x) * 4;
                let (b, g, r) = (bytes[px], bytes[px + 1], bytes[px + 2]);
                if r > 140 && g < 90 && b < 90 {
                    found = true;
                    lo_x = lo_x.min(x);
                    lo_y = lo_y.min(y);
                    hi_x = hi_x.max(x);
                    hi_y = hi_y.max(y);
                }
            }
        }
        if !found {
            return None;
        }
        let wf = w as f32;
        Some((lo_x as f32 / wf, lo_y as f32 / wf, hi_x as f32 / wf, hi_y as f32 / wf))
    }

    /// Is the pixel at this normalized-by-page-width point red? Capture space
    /// divides BOTH axes by the page width, so a capture coordinate over CAP is
    /// the same fraction here.
    fn is_red_at(bytes: &[u8], w: usize, nx: f32, ny: f32) -> bool {
        let x = (nx * w as f32) as usize;
        let y = (ny * w as f32) as usize;
        let px = (y * w + x) * 4;
        if px + 2 >= bytes.len() {
            return false;
        }
        let (b, g, r) = (bytes[px], bytes[px + 1], bytes[px + 2]);
        r > 140 && g < 90 && b < 90
    }

    /// A filled rounded rectangle covering 0.2..0.8 by 0.2..0.6 of the page
    /// width, with a 0.15-wide corner radius.
    fn filled_rounded_rect(kind: i32, radius_px: f32) -> ShapeSpec {
        let mut s = shape(kind, 200.0, 200.0, 800.0, 600.0);
        s.fill_rgba = 0xFF_DC_00_00;    // opaque, same red the probe looks for
        s.corner_radius_px = radius_px;
        s
    }

    #[test]
    fn a_rounded_rectangle_has_its_corners_cut_away() {
        // The whole visual claim of the feature, with a square-cornered
        // rectangle as the control. Both are drawn from the same box and the
        // same fill; the ONLY difference is the kind and its radius, so a
        // difference at the corner pixel can only come from the rounding.
        //
        // Probe point: 20 capture units in from the top-left corner. The corner
        // arc has its centre at (350,350) with r=150, and that point is 184
        // away from it, so it lies outside the round and inside the square.
        const CAP: i32 = 1000;
        const PROBE: (f32, f32) = (0.22, 0.22);
        const CENTRE: (f32, f32) = (0.50, 0.40);

        let square = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_RECTANGLE, 0.0)];
        assert_eq!(
            add_shape_annotations(square, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );
        let (sq_bytes, sq_w) = render_bytes(square, 0, 600);
        let square_corner = is_red_at(&sq_bytes, sq_w, PROBE.0, PROBE.1);
        let square_centre = is_red_at(&sq_bytes, sq_w, CENTRE.0, CENTRE.1);
        close_document(square);

        let round = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 150.0)];
        assert_eq!(
            add_shape_annotations(round, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );
        let (rd_bytes, rd_w) = render_bytes(round, 0, 600);
        let round_corner = is_red_at(&rd_bytes, rd_w, PROBE.0, PROBE.1);
        let round_centre = is_red_at(&rd_bytes, rd_w, CENTRE.0, CENTRE.1);
        close_document(round);

        println!("ROUNDED: corner square={square_corner} round={round_corner}, \
                  centre square={square_centre} round={round_centre}");

        assert!(square_centre, "the control rectangle did not draw at all");
        assert!(square_corner, "the control rectangle should fill its own corner");
        assert!(round_centre, "the rounded rectangle did not draw at all");
        assert!(!round_corner, "the corner was NOT cut away; it drew square");
    }

    #[test]
    fn a_rounded_rectangles_radius_survives_save_and_reopen() {
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 150.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        let saved = snapshot_document(handle);
        close_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0, "the saved document would not reopen");

        let contents = contents_of(reopened, 0, 0).expect("reopened shape has no tag");
        let parsed = parse_shape_tag(&contents).expect("reopened tag did not parse");
        close_document(reopened);

        println!("ROUNDED RELOAD: {contents}");
        assert_eq!(parsed.0, SHAPE_ROUNDED_RECT, "came back as a different kind");
        assert!(parsed.10 > 0.0, "the corner radius came back as {}", parsed.10);
    }

    #[test]
    fn resizing_a_rounded_rectangle_keeps_its_kind_and_its_radius() {
        // Resize is a delete-and-rebuild driven entirely by the tag, so a field
        // the rebuild forgets to carry is silently lost on the first drag of a
        // corner handle. That is how a rounded rect would turn back into a
        // plain rectangle the moment you resized it.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 150.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );
        let before = parse_shape_tag(&contents_of(handle, 0, 0).unwrap()).unwrap();

        let mut new_index = -1;
        assert_eq!(
            resize_shape_annotation(handle, 0, 0, CAP, 150.0, 150.0, 900.0, 700.0, &mut new_index),
            STATUS_OK_PDFIUM
        );
        let after = parse_shape_tag(&contents_of(handle, 0, new_index as usize).unwrap())
            .expect("resized shape lost its tag");
        close_document(handle);

        println!("ROUNDED RESIZE: radius {} -> {}", before.10, after.10);
        assert_eq!(after.0, SHAPE_ROUNDED_RECT, "resize changed the kind");
        assert!((after.10 - before.10).abs() < 0.5,
            "resize changed the radius from {} to {}", before.10, after.10);
    }

    #[test]
    fn a_squeezed_rounded_rectangle_clamps_its_corners_instead_of_crossing_them() {
        // Past half the shorter side the two arcs on one side would meet and
        // then cross, drawing a bow tie. The clamp is applied at DRAW time, so
        // the stored radius is left intact for when the box grows again.
        assert_eq!(clamped_corner_radius(150.0, 600.0, 400.0), 150.0);
        assert_eq!(clamped_corner_radius(500.0, 600.0, 400.0), 200.0, "not clamped to half the height");
        assert_eq!(clamped_corner_radius(500.0, 40.0, 400.0), 20.0, "not clamped to half the width");
        assert_eq!(clamped_corner_radius(-5.0, 600.0, 400.0), 0.0, "a negative radius must not draw");
        assert_eq!(clamped_corner_radius(f32::NAN, 600.0, 400.0), 0.0, "NaN must not reach the path builder");
    }

    #[test]
    fn setting_the_radius_changes_the_drawn_corner_and_nothing_else() {
        // What the corner-radius slider does. Two things have to hold: the
        // corner actually changes on the PAGE (not just in the tag), and the
        // colour, width and fill survive, since a restyle that quietly reset
        // them would undo the user's other choices on every drag of the slider.
        const CAP: i32 = 1000;
        const CORNER: (f32, f32) = (0.22, 0.22);

        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 20.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        // A small radius leaves the probe point covered.
        let (small_bytes, w) = render_bytes(handle, 0, 600);
        let corner_small = is_red_at(&small_bytes, w, CORNER.0, CORNER.1);
        let before = parse_shape_tag(&contents_of(handle, 0, 0).unwrap()).unwrap();

        let mut idx = -1;
        assert_eq!(
            restyle_shape_radius_annotation(handle, 0, 0, CAP, 150.0, &mut idx as *mut i32),
            STATUS_OK_PDFIUM
        );

        let (big_bytes, w2) = render_bytes(handle, 0, 600);
        let corner_big = is_red_at(&big_bytes, w2, CORNER.0, CORNER.1);
        let after = parse_shape_tag(&contents_of(handle, 0, idx as usize).unwrap())
            .expect("restyled shape lost its tag");
        close_document(handle);

        println!("RADIUS SLIDER: corner small={corner_small} big={corner_big}, \
                  radius {} -> {}", before.10, after.10);

        assert!(corner_small, "a barely-rounded corner should still cover the probe");
        assert!(!corner_big, "raising the radius did not cut the corner away");
        assert!(after.10 > before.10, "the tag's radius did not grow");

        // Everything else untouched.
        assert_eq!(after.0, before.0, "kind changed");
        assert_eq!((after.1, after.2, after.3, after.4),
                   (before.1, before.2, before.3, before.4), "stroke colour changed");
        assert_eq!(after.9, before.9, "fill changed");
        assert!((after.5 - before.5).abs() < 0.01, "stroke width changed");
    }

    #[test]
    fn a_zero_radius_from_the_slider_squares_the_corners_again() {
        // The bottom of the slider's travel. It must return the shape to square
        // corners rather than refusing, so the control is reversible.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 150.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        let mut idx = -1;
        assert_eq!(
            restyle_shape_radius_annotation(handle, 0, 0, CAP, 0.0, &mut idx as *mut i32),
            STATUS_OK_PDFIUM
        );
        let (bytes, w) = render_bytes(handle, 0, 600);
        let corner = is_red_at(&bytes, w, 0.22, 0.22);
        close_document(handle);

        assert!(corner, "dropping the radius to zero did not restore square corners");
    }

    #[test]
    fn a_nonsense_radius_is_refused_rather_than_drawn() {
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 60.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        let mut idx = -1;
        assert_eq!(
            restyle_shape_radius_annotation(handle, 0, 0, CAP, -1.0, &mut idx as *mut i32),
            STATUS_INVALID_INPUT
        );
        assert_eq!(
            restyle_shape_radius_annotation(handle, 0, 0, CAP, f32::NAN, &mut idx as *mut i32),
            STATUS_INVALID_INPUT
        );
        close_document(handle);
    }

    #[test]
    fn a_rounded_rectangle_with_no_radius_still_draws_as_a_rectangle() {
        // A rounded rect dragged out to a sliver, or one whose radius was
        // cleared, must degrade to square corners rather than to nothing.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let specs = [filled_rounded_rect(SHAPE_ROUNDED_RECT, 0.0)];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );
        let (bytes, w) = render_bytes(handle, 0, 600);
        let drew = is_red_at(&bytes, w, 0.50, 0.40);
        close_document(handle);

        assert!(drew, "a zero-radius rounded rectangle vanished instead of drawing square");
    }

    #[test]
    fn the_bundled_blank_page_opens_renders_and_accepts_a_shape() {
        // The document the app opens at startup so there is always something
        // to draw on. Hand-built minimal PDF, so prove PDFium accepts it
        // rather than discovering at launch that the xref is off by a byte.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");

        let sizes = get_page_sizes(handle);
        assert_eq!(sizes.status, STATUS_OK_PDFIUM);
        assert_eq!(sizes.len, 1, "the blank document should have exactly one page");
        free_page_size_array(sizes);

        // It must render, and it must render BLANK (no stray ink).
        assert!(
            red_bbox_norm(handle, 300).is_none(),
            "the blank page should have no red ink before anything is drawn"
        );

        // And it must accept a shape at the position asked for, since that is
        // the first thing anyone will do with it.
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.25 * CAP as f32,
            y1: 0.25 * CAP as f32,
            x2: 0.75 * CAP as f32,
            y2: 0.75 * CAP as f32,
            r: 255,
            g: 0,
            b: 0,
            a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );
        let bbox = red_bbox_norm(handle, 600).expect("the shape should have put red ink on the page");
        close_document(handle);

        let tol = 0.02;
        assert!(
            (bbox.0 - 0.25).abs() < tol && (bbox.2 - 0.75).abs() < tol,
            "on the blank page a shape asked for at x 0.25..0.75 rendered at \
             x {:.3}..{:.3}",
            bbox.0,
            bbox.2
        );
    }

    /// Count distinct red blobs by scanning for red pixels and flood-filling.
    /// Enough to answer "did all three shapes end up where they were sent".
    fn red_blob_boxes(handle: u64, width: i32) -> Vec<(f32, f32, f32, f32)> {
        let (bytes, w) = render_bytes(handle, 0, width);
        let h = bytes.len() / (w * 4);
        let is_red = |x: usize, y: usize| {
            let px = (y * w + x) * 4;
            bytes[px + 2] > 140 && bytes[px + 1] < 90 && bytes[px] < 90
        };
        let mut seen = vec![false; w * h];
        let mut out = Vec::new();
        for y0 in 0..h {
            for x0 in 0..w {
                if seen[y0 * w + x0] || !is_red(x0, y0) {
                    continue;
                }
                let (mut lo_x, mut lo_y, mut hi_x, mut hi_y) = (x0, y0, x0, y0);
                let mut stack = vec![(x0, y0)];
                seen[y0 * w + x0] = true;
                while let Some((x, y)) = stack.pop() {
                    lo_x = lo_x.min(x);
                    lo_y = lo_y.min(y);
                    hi_x = hi_x.max(x);
                    hi_y = hi_y.max(y);
                    for (dx, dy) in [(1i32, 0i32), (-1, 0), (0, 1), (0, -1)] {
                        let nx = x as i32 + dx;
                        let ny = y as i32 + dy;
                        if nx < 0 || ny < 0 || nx >= w as i32 || ny >= h as i32 {
                            continue;
                        }
                        let (nx, ny) = (nx as usize, ny as usize);
                        if !seen[ny * w + nx] && is_red(nx, ny) {
                            seen[ny * w + nx] = true;
                            stack.push((nx, ny));
                        }
                    }
                }
                let wf = w as f32;
                out.push((lo_x as f32 / wf, lo_y as f32 / wf, hi_x as f32 / wf, hi_y as f32 / wf));
            }
        }
        out.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());
        out
    }

    /// The raw `/Contents` of an annotation: what a PDF reader shows the user
    /// as the comment. Deliberately NOT annotation_tag(), which is the app's
    /// private-key view.
    fn raw_contents(handle: u64, page_index: i32, index: usize) -> Option<String> {
        use pdfium_render::prelude::*;
        let _guard = lock(&CALL_LOCK);
        let doc = lock(&core().documents).get(&handle).cloned()?;
        let g = lock(&doc);
        let page = g.pages().get(page_index as u16).ok()?;
        let annotation = page.annotations().iter().nth(index)?;
        annotation.contents()
    }

    #[test]
    fn a_shape_leaves_no_comment_for_the_reader_to_see() {
        // The tag is machine data and must NOT land in /Contents, which is the
        // annotation's comment text: with it there, every shape and text box
        // this app made showed up in Acrobat's comment panel as a line of
        // gibberish, in any PDF the user shared.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 100.0, y1: 100.0, x2: 300.0, y2: 300.0,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );

        let comment = raw_contents(handle, 0, 0).unwrap_or_default();
        assert!(
            comment.is_empty(),
            "a shape must leave /Contents empty, but a reader would show: {comment:?}"
        );

        // ...and the app must still recognise the shape, from the private key.
        let tag = get_annotation_contents(handle, 0, 0);
        let bytes = unsafe { std::slice::from_raw_parts(tag.data, tag.len) }.to_vec();
        free_byte_buffer(tag);
        let tag = String::from_utf8(bytes).unwrap();
        close_document(handle);
        assert!(
            tag.starts_with(SHAPE_TAG),
            "the app should read its own tag back from the private key, got {tag:?}"
        );
    }

    #[test]
    fn an_id_round_trips_through_the_private_key_without_a_comment() {
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_ELLIPSE,
            x1: 100.0, y1: 100.0, x2: 300.0, y2: 300.0,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );

        let id = "0123456789abcdef0123456789abcdef";
        assert_eq!(
            unsafe { set_annotation_id(handle, 0, 0, id.as_ptr(), id.len()) },
            STATUS_OK_PDFIUM
        );

        let got = get_annotation_id(handle, 0, 0);
        let bytes = unsafe { std::slice::from_raw_parts(got.data, got.len) }.to_vec();
        free_byte_buffer(got);
        let comment = raw_contents(handle, 0, 0).unwrap_or_default();
        close_document(handle);

        assert_eq!(String::from_utf8(bytes).unwrap(), id);
        assert!(comment.is_empty(), "stamping an id must not write a comment, got {comment:?}");
    }

    #[test]
    fn a_tag_written_the_old_way_in_contents_is_still_read() {
        // Documents saved by earlier versions carry the tag in /Contents.
        // Reads fall back to it so those files stay editable rather than
        // turning into anonymous annotations nobody can select or resize.
        use pdfium_render::prelude::*;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 100.0, y1: 100.0, x2: 300.0, y2: 300.0,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );

        // Force the legacy shape: wipe the private key, put the tag back in
        // the comment field, exactly as an old build would have left it.
        let legacy = format!("{SHAPE_TAG}0:FF0000FF:2.0000:1:1");
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&handle).cloned().unwrap();
            let g = lock(&doc);
            let mut page = g.pages().get(0).unwrap();
            let mut annotation = page.annotations_mut().get(0).unwrap();
            let empty: Vec<u16> = vec![0];
            annotation.library_bindings().FPDFAnnot_SetStringValue(
                annotation.annotation_handle(), TAG_KEY, empty.as_ptr());
            annotation.set_contents(&legacy).unwrap();
        }

        let tag = get_annotation_contents(handle, 0, 0);
        let bytes = unsafe { std::slice::from_raw_parts(tag.data, tag.len) }.to_vec();
        free_byte_buffer(tag);
        close_document(handle);
        assert_eq!(String::from_utf8(bytes).unwrap(), legacy);
    }

    /// A stamp image that is asymmetric in BOTH axes: a wide white block with
    /// its LEFT quarter red. The red marker is what says which way round the
    /// picture ended up, which a symmetric block cannot.
    fn marker_stamp_pixels(pw: i32, ph: i32) -> Vec<u8> {
        let mut px = Vec::with_capacity((pw * ph * 4) as usize);
        for _y in 0..ph {
            for x in 0..pw {
                if x < pw / 4 {
                    px.extend_from_slice(&[0, 0, 255, 255]); // BGRA red
                } else {
                    px.extend_from_slice(&[255, 255, 255, 255]); // opaque white
                }
            }
        }
        px
    }

    /// Reads the page's annotation ids in paint order, so a test can assert on
    /// the STACK rather than on indices that every write reshuffles.
    fn stack_ids(handle: u64) -> Vec<String> {
        let array = get_annotations(handle, 0);
        let count = array.len as i32;
        free_annotation_array(array);

        let mut out = Vec::new();
        for i in 0..count {
            let buf = get_annotation_id(handle, 0, i);
            let id = if buf.status == STATUS_OK_PDFIUM && !buf.data.is_null() && buf.len > 0 {
                let bytes = unsafe { std::slice::from_raw_parts(buf.data, buf.len) };
                String::from_utf8_lossy(bytes).to_string()
            } else {
                "?".to_string()
            };
            free_byte_buffer(buf);
            out.push(id);
        }
        out
    }

    /// Adds a stamp and tags it with a recognisable 32-hex id.
    fn add_tagged_stamp(handle: u64, cap: i32, top: f32, tag: char) -> i32 {
        let (pw, ph) = (40i32, 10i32);
        let pixels = marker_stamp_pixels(pw, ph);
        let (l, t, r, b) = (0.30 * cap as f32, top, 0.70 * cap as f32, top + 0.08 * cap as f32);
        let status = add_stamp_annotation(
            handle, 0, cap, l, t, r, b, pixels.as_ptr(), pixels.len(), pw, ph);
        assert_eq!(status, STATUS_OK_PDFIUM, "stamp {tag} could not be added");

        let array = get_annotations(handle, 0);
        let last = array.len as i32 - 1;
        free_annotation_array(array);

        let id: String = std::iter::repeat(tag).take(ID_HEX_LEN).collect();
        let status = unsafe {
            set_annotation_id(handle, 0, last, id.as_ptr(), id.len())
        };
        assert_eq!(status, STATUS_OK_PDFIUM, "stamp {tag} could not be tagged");
        last
    }

    #[test]
    fn raising_a_stamp_moves_it_to_the_top_of_the_stack() {
        // Z-order is the order of the page's annotation list, and PDFium offers
        // no way to move an entry within it. The only lever is that a rebuilt
        // annotation is APPENDED, so "raise" has to mean "remove and re-add".
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");

        add_tagged_stamp(handle, CAP, 0.10 * CAP as f32, 'a');
        add_tagged_stamp(handle, CAP, 0.30 * CAP as f32, 'b');
        add_tagged_stamp(handle, CAP, 0.50 * CAP as f32, 'c');

        let before = stack_ids(handle);
        println!("Z-ORDER before: {before:?}");
        assert_eq!(before.len(), 3, "expected three stamps, got {before:?}");

        // Raise the BOTTOM one at its own current bounds.
        let mut idx = -1;
        let status = raise_stamp_annotation(
            handle, 0, 0, CAP,
            0.30 * CAP as f32, 0.10 * CAP as f32,
            0.70 * CAP as f32, 0.18 * CAP as f32,
            &mut idx as *mut i32);
        assert_eq!(status, STATUS_OK_PDFIUM, "raise refused");

        let after = stack_ids(handle);
        close_document(handle);
        println!("Z-ORDER after:  {after:?} (new index {idx})");

        // 'a' started at the bottom and must now be on top, with b and c having
        // closed up beneath it in their original order.
        assert_eq!(after.len(), 3, "an annotation was lost or duplicated: {after:?}");
        assert_eq!(after[2], before[0], "the raised stamp is not on top");
        assert_eq!(after[0], before[1]);
        assert_eq!(after[1], before[2]);
        assert_eq!(idx, 2, "out_new_index should report the top slot");
    }

    #[test]
    fn resizing_a_stamp_to_its_own_bounds_leaves_the_stack_alone() {
        // The CONTROL for the test above, and the reason raise_stamp_annotation
        // has to exist at all. resize_annotation tries an in-place bounds write
        // first, and for a same-size "move" that succeeds, so it reports success
        // while changing nothing about the order. Reaching for it to implement
        // Bring to Front would silently do nothing.
        const CAP: i32 = 1000;
        let handle = open_fixture_named("tests/fixtures/blank.pdf");

        add_tagged_stamp(handle, CAP, 0.10 * CAP as f32, 'a');
        add_tagged_stamp(handle, CAP, 0.30 * CAP as f32, 'b');

        let before = stack_ids(handle);

        let mut idx = -1;
        let status = resize_annotation(
            handle, 0, 0, CAP,
            0.30 * CAP as f32, 0.10 * CAP as f32,
            0.70 * CAP as f32, 0.18 * CAP as f32,
            &mut idx as *mut i32);
        assert_eq!(status, STATUS_OK_PDFIUM);

        let after = stack_ids(handle);
        close_document(handle);
        println!("CONTROL resize: {before:?} -> {after:?} (index {idx})");

        assert_eq!(before, after, "resize_annotation unexpectedly reordered the page");
        assert_eq!(idx, 0, "an in-place write must report the same index");
    }

    #[test]
    fn rotating_a_stamp_turns_it_clockwise_on_screen() {
        // The app's angle is clockwise as the user sees it. PDF space has y
        // running up, where the textbook rotation matrix turns the other way,
        // so a missing negation made the stamp turn opposite to its own
        // selection frame. The red marker starts on the LEFT; a quarter turn
        // clockwise must put it at the TOP.
        const CAP: i32 = 1000;
        let (pw, ph) = (40i32, 10i32);
        let pixels = marker_stamp_pixels(pw, ph);

        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let (l, t, r, b) = (0.30 * CAP as f32, 0.45 * CAP as f32, 0.70 * CAP as f32, 0.55 * CAP as f32);
        assert_eq!(
            add_stamp_annotation(handle, 0, CAP, l, t, r, b, pixels.as_ptr(), pixels.len(), pw, ph),
            STATUS_OK_PDFIUM
        );

        let up = red_bbox_norm(handle, 600).expect("marker missing");
        let up_cx = (up.0 + up.2) / 2.0;

        let mut idx = -1;
        assert_eq!(
            rotate_stamp_annotation(handle, 0, 0, CAP, 90.0, &mut idx as *mut i32),
            STATUS_OK_PDFIUM
        );
        let turned = red_bbox_norm(handle, 600).expect("marker missing after rotate");
        close_document(handle);

        println!("STAMP DIRECTION: marker {up:?} -> {turned:?}");

        // Started left of the stamp's centre (0.50); a clockwise quarter turn
        // must move it ABOVE the centre (smaller y), not below.
        assert!(up_cx < 0.5, "the marker should start on the left, got {up:?}");
        let t_cy = (turned.1 + turned.3) / 2.0;
        assert!(
            t_cy < 0.5,
            "after a CLOCKWISE quarter turn the marker should sit above centre,              but its centre y is {t_cy:.3}; it turned the wrong way"
        );
    }

    #[test]
    fn rotating_a_stamp_twice_does_not_compound_or_smear_it() {
        // Rotation is to an ABSOLUTE angle, so rotating to 45 and then to 90
        // must land in exactly the same place as going straight to 90.
        //
        // It did not. The pixels were extracted with get_processed_bitmap,
        // which bakes the object's current transform into the buffer, so the
        // second rotation re-rotated already-rotated pixels: the angle added
        // up and the picture was resampled again each time, growing visibly
        // more skewed. This is the regression test for that.
        const CAP: i32 = 1000;
        let (pw, ph) = (40i32, 10i32);
        let pixels = marker_stamp_pixels(pw, ph);
        let (l, t, r, b) = (0.30 * CAP as f32, 0.45 * CAP as f32, 0.70 * CAP as f32, 0.55 * CAP as f32);

        let place = || {
            let h = open_fixture_named("tests/fixtures/blank.pdf");
            assert_eq!(
                add_stamp_annotation(h, 0, CAP, l, t, r, b, pixels.as_ptr(), pixels.len(), pw, ph),
                STATUS_OK_PDFIUM
            );
            h
        };

        // Straight to 90.
        let direct = place();
        let mut i0 = -1;
        assert_eq!(rotate_stamp_annotation(direct, 0, 0, CAP, 90.0, &mut i0 as *mut i32), STATUS_OK_PDFIUM);
        let once = red_bbox_norm(direct, 600).expect("marker missing");
        close_document(direct);

        // Via 45.
        let staged = place();
        let mut i1 = -1;
        assert_eq!(rotate_stamp_annotation(staged, 0, 0, CAP, 45.0, &mut i1 as *mut i32), STATUS_OK_PDFIUM);
        let mut i2 = -1;
        assert_eq!(rotate_stamp_annotation(staged, 0, i1, CAP, 90.0, &mut i2 as *mut i32), STATUS_OK_PDFIUM);
        let twice = red_bbox_norm(staged, 600).expect("marker missing");
        close_document(staged);

        println!("STAMP COMPOUNDING: direct {once:?} vs via-45 {twice:?}");

        let tol = 0.03;
        for (a, b2, what) in [
            (once.0, twice.0, "left"),
            (once.1, twice.1, "top"),
            (once.2, twice.2, "right"),
            (once.3, twice.3, "bottom"),
        ] {
            assert!(
                (a - b2).abs() < tol,
                "rotating to 45 then 90 should match rotating straight to 90,                  but {what} differs: {a:.3} vs {b2:.3} - the angle is compounding"
            );
        }
    }

    #[test]
    fn rotating_a_stamp_turns_the_picture_and_the_angle_survives_a_reopen() {
        // A WIDE, SHORT red block. Asymmetric on purpose: a square would look
        // identical at 0 and 90 degrees, so it could not tell a real rotation
        // from a no-op, which is exactly the bug this guards.
        const CAP: i32 = 1000;
        let (pw, ph) = (40i32, 10i32);
        let mut pixels = Vec::with_capacity((pw * ph * 4) as usize);
        for _ in 0..(pw * ph) {
            pixels.extend_from_slice(&[0, 0, 255, 255]); // BGRA red, opaque
        }

        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        // Placed in a box with the same 4:1 aspect as the image.
        let (l, t, r, b) = (0.30 * CAP as f32, 0.45 * CAP as f32, 0.70 * CAP as f32, 0.55 * CAP as f32);
        assert_eq!(
            add_stamp_annotation(handle, 0, CAP, l, t, r, b, pixels.as_ptr(), pixels.len(), pw, ph),
            STATUS_OK_PDFIUM
        );

        let upright = red_bbox_norm(handle, 600).expect("the stamp should be on the page");
        let up_w = upright.2 - upright.0;
        let up_h = upright.3 - upright.1;
        assert!(up_w > up_h * 2.0, "the stamp should start wide: {upright:?}");

        let mut new_index = -1;
        assert_eq!(
            rotate_stamp_annotation(handle, 0, 0, CAP, 90.0, &mut new_index as *mut i32),
            STATUS_OK_PDFIUM
        );

        let turned = red_bbox_norm(handle, 600).expect("the stamp should still be on the page");
        let t_w = turned.2 - turned.0;
        let t_h = turned.3 - turned.1;
        println!("STAMP ROTATE: upright {upright:?} -> turned {turned:?}");

        // Turned a quarter, so it must now be TALL, and about as tall as it
        // used to be wide.
        assert!(
            t_h > t_w * 2.0,
            "after a 90 degree rotation the stamp should be tall, got {turned:?}"
        );
        assert!(
            (t_h - up_w).abs() < 0.04,
            "the turned height {t_h:.3} should match the upright width {up_w:.3}"
        );

        // It must stay put: rotation is about the stamp's own centre.
        let up_cx = (upright.0 + upright.2) / 2.0;
        let up_cy = (upright.1 + upright.3) / 2.0;
        let t_cx = (turned.0 + turned.2) / 2.0;
        let t_cy = (turned.1 + turned.3) / 2.0;
        assert!(
            (t_cx - up_cx).abs() < 0.02 && (t_cy - up_cy).abs() < 0.02,
            "the stamp drifted while rotating: centre {up_cx:.3},{up_cy:.3} -> {t_cx:.3},{t_cy:.3}"
        );

        // And the angle must survive a save, or a reopened stamp looks turned
        // while reporting itself upright and the next rotation is measured
        // from the wrong place.
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        free_byte_buffer(saved);
        close_document(handle);
        assert_ne!(reopened, 0);

        let tag = contents_of(reopened, 0, 0).expect("the reopened stamp has no tag");
        close_document(reopened);
        let parsed = parse_stamp_tag(&tag).expect("the reopened stamp tag did not parse");
        assert!(
            (parsed.0 - 90.0).abs() < 0.01,
            "the reopened stamp should remember 90 degrees, tag says {tag}"
        );
    }

    #[test]
    fn undoing_a_shape_move_puts_the_drawing_back() {
        // Models exactly what the app's undo does to a moved shape.
        //
        // CommitLoadedMove writes a shape with resize_shape_annotation (which
        // redraws it from its tag). The undo entry then restores the old
        // rectangle with resize_annotation - and resize_annotation REFUSES a
        // shape, as pinned by
        // moving_a_shape_moves_the_drawing_not_just_the_rectangle. The failure
        // is swallowed: the history step is consumed and the drawing does not
        // move, so undo appears to do nothing.
        //
        // Undo has to dispatch per kind, the same way the move path was taught
        // to in v2.2.3.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.10 * CAP as f32,
            y1: 0.10 * CAP as f32,
            x2: 0.30 * CAP as f32,
            y2: 0.30 * CAP as f32,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );
        let before = red_bbox_norm(handle, 600).expect("shape should be on the page");

        // The move, as CommitLoadedMove performs it.
        let mut moved_index = -1;
        assert_eq!(
            resize_shape_annotation(
                handle, 0, 0, CAP,
                0.50 * CAP as f32, 0.50 * CAP as f32,
                0.70 * CAP as f32, 0.70 * CAP as f32,
                &mut moved_index as *mut i32,
            ),
            STATUS_OK_PDFIUM
        );
        let moved = red_bbox_norm(handle, 600).expect("shape should still be on the page");
        assert!((moved.0 - 0.50).abs() < 0.03, "the move itself should work: {moved:?}");

        // The undo, as ApplyHistoryEntry now performs it: put the old
        // rectangle back through the SAME per-kind dispatch the move used.
        // resize_annotation is still asserted to refuse a shape, so that the
        // day it starts succeeding somebody notices.
        let mut refused = -1;
        assert_eq!(
            resize_annotation(
                handle, 0, moved_index, CAP,
                0.10 * CAP as f32, 0.10 * CAP as f32,
                0.30 * CAP as f32, 0.30 * CAP as f32,
                &mut refused as *mut i32,
            ),
            STATUS_UNSUPPORTED,
            "resize_annotation must still refuse shapes; undo relies on              dispatching to the shape path instead"
        );

        let mut undone_index = -1;
        let status = resize_shape_annotation(
            handle, 0, moved_index, CAP,
            0.10 * CAP as f32, 0.10 * CAP as f32,
            0.30 * CAP as f32, 0.30 * CAP as f32,
            &mut undone_index as *mut i32,
        );
        let after = red_bbox_norm(handle, 600).expect("shape should still be on the page");
        close_document(handle);

        println!("UNDO SHAPE MOVE: before {before:?} moved {moved:?} undo-status {status} after {after:?}");

        assert!(
            (after.0 - before.0).abs() < 0.03,
            "undo should have put the drawing back at x {:.3}, but it is at x {:.3}              (resize_annotation returned {status}). Undo is not dispatching per              annotation kind.",
            before.0,
            after.0
        );
    }

    #[test]
    fn a_batch_of_four_shapes_all_arrive_on_a_real_document() {
        // Mirrors the C# ShapeInteropTests batch, which started failing with a
        // count of 1 and NaN bounds. Same fixture, same four kinds.
        let handle = open_fixture_named("tests/fixtures/sample_20pages.pdf");
        const CAP: i32 = 1000;
        let mk = |kind: i32, x1: f32, y1: f32, x2: f32, y2: f32| ShapeSpec {
            page_index: 0, kind,
            x1, y1, x2, y2,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0, rotation_deg: 0.0, fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        let specs = [
            mk(SHAPE_RECTANGLE, 100.0, 100.0, 300.0, 200.0),
            mk(SHAPE_ELLIPSE, 350.0, 100.0, 550.0, 200.0),
            mk(SHAPE_LINE, 100.0, 300.0, 550.0, 300.0),
            mk(SHAPE_ARROW, 100.0, 400.0, 550.0, 400.0),
        ];
        assert_eq!(
            add_shape_annotations(handle, CAP, specs.as_ptr(), specs.len()),
            STATUS_OK_PDFIUM
        );

        let array = get_annotations(handle, 0);
        let count = array.len;
        let items: Vec<(f32, f32, f32, f32)> = if array.items.is_null() {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(array.items, array.len) }
                .iter()
                .map(|i| (i.left, i.top, i.right, i.bottom))
                .collect()
        };
        free_annotation_array(array);
        close_document(handle);

        println!("BATCH: count={count} first={:?}", items.first());
        assert_eq!(count, 4, "all four shapes should be on the page");
        for (i, it) in items.iter().enumerate() {
            assert!(it.0.is_finite() && it.1.is_finite() && it.2.is_finite() && it.3.is_finite(),
                    "shape {i} came back with non-finite bounds: {it:?}");
        }
    }

    #[test]
    fn a_shape_can_be_repositioned_and_then_rotated_in_one_gesture() {
        // What rotating a GROUP asks of each member: it has to ORBIT the
        // selection centre (a reposition) and SPIN about its own centre (an
        // angle). rotate_shape_annotation takes no bounds, so a shape needs
        // two writes, and the second must not undo the first. This pins that
        // sequence before the app is taught to drive it.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        // Deliberately oblong, so a spin is visible in the bounding box.
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.20 * CAP as f32, y1: 0.45 * CAP as f32,
            x2: 0.40 * CAP as f32, y2: 0.50 * CAP as f32,
            r: 255, g: 0, b: 0, a: 255,
            width_px: 4.0, rotation_deg: 0.0, fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );

        // 1. Orbit: move it to where the group rotation puts it.
        let mut idx = -1;
        assert_eq!(
            resize_shape_annotation(
                handle, 0, 0, CAP,
                0.55 * CAP as f32, 0.70 * CAP as f32,
                0.75 * CAP as f32, 0.75 * CAP as f32,
                &mut idx as *mut i32),
            STATUS_OK_PDFIUM,
            "the orbit write must succeed"
        );

        // 2. Spin: give it the group's delta angle, at its new home.
        let mut idx2 = -1;
        assert_eq!(
            rotate_shape_annotation(handle, 0, idx, CAP, 90.0, &mut idx2 as *mut i32),
            STATUS_OK_PDFIUM,
            "the spin write must succeed"
        );

        let bbox = red_bbox_norm(handle, 600).expect("shape should still be on the page");
        close_document(handle);
        println!("ORBIT+SPIN: {bbox:?}");

        // It must be at the ORBITED position, not back at the original: the
        // rotate write must have kept the move.
        let cx = (bbox.0 + bbox.2) / 2.0;
        let cy = (bbox.1 + bbox.3) / 2.0;
        assert!((cx - 0.65).abs() < 0.04, "orbited centre x should be ~0.65, got {cx:.3}");
        assert!((cy - 0.725).abs() < 0.04, "orbited centre y should be ~0.725, got {cy:.3}");

        // And it must be TALL now, having been wide before the spin.
        let w = bbox.2 - bbox.0;
        let h = bbox.3 - bbox.1;
        assert!(h > w, "after a quarter turn the shape should be taller than wide: {bbox:?}");
    }

    #[test]
    fn moving_three_shapes_as_a_group_moves_all_three_drawings() {
        // The whole reported failure, end to end, in the core: three shapes
        // drawn side by side, then every one of them moved down by the same
        // delta, exactly as the group-move commit does. The field symptom was
        // that the frames travelled and one or two of the SHAPES stayed
        // behind, so this asserts on rendered ink, not on status codes.
        //
        // Shapes are moved back-to-front, since each write is a delete and a
        // re-add and that keeps the not-yet-written ones at stable indices.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let xs = [0.10f32, 0.40, 0.70];
        for x in xs {
            let spec = ShapeSpec {
                page_index: 0,
                kind: SHAPE_RECTANGLE,
                x1: x * CAP as f32,
                y1: 0.10 * CAP as f32,
                x2: (x + 0.15) * CAP as f32,
                y2: 0.25 * CAP as f32,
                r: 255,
                g: 0,
                b: 0,
                a: 255,
                width_px: 4.0,
                rotation_deg: 0.0,
                fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
            };
            assert_eq!(
                add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
                STATUS_OK_PDFIUM
            );
        }
        assert_eq!(red_blob_boxes(handle, 600).len(), 3, "three shapes should be on the page");

        const DY: f32 = 0.45;
        for i in (0..3).rev() {
            let x = xs[i];
            let mut new_index = -1;
            assert_eq!(
                resize_shape_annotation(
                    handle, 0, i as i32, CAP,
                    x * CAP as f32, (0.10 + DY) * CAP as f32,
                    (x + 0.15) * CAP as f32, (0.25 + DY) * CAP as f32,
                    &mut new_index as *mut i32,
                ),
                STATUS_OK_PDFIUM,
                "moving shape {i} must succeed"
            );
        }

        let after = red_blob_boxes(handle, 600);
        close_document(handle);
        println!("GROUP MOVE RESULT: {after:?}");

        assert_eq!(after.len(), 3, "all three shapes must still be on the page");
        let tol = 0.03;
        for (i, blob) in after.iter().enumerate() {
            assert!(
                (blob.1 - (0.10 + DY)).abs() < tol,
                "shape {i} should have moved to y {:.3} but its drawing is at y {:.3}",
                0.10 + DY,
                blob.1
            );
        }
    }

    #[test]
    fn moving_a_shape_moves_the_drawing_not_just_the_rectangle() {
        // The move path the app uses for a dragged annotation is
        // resize_annotation, which tries set_annotation_bounds first. That
        // sets the annotation's /Rect. If the shape's path objects live in
        // page coordinates, the rect moves and the DRAWING stays put - and
        // since the selection frame is positioned from the rect, the frame
        // moves and the shape does not. That is the field symptom: a group
        // where the frames travel and the shapes are left behind.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.10 * CAP as f32,
            y1: 0.10 * CAP as f32,
            x2: 0.30 * CAP as f32,
            y2: 0.30 * CAP as f32,
            r: 255,
            g: 0,
            b: 0,
            a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );
        let before = red_bbox_norm(handle, 600).expect("shape should be on the page");

        // resize_annotation is what the anchor's move path falls through to,
        // and it CANNOT move a shape: it reports UNSUPPORTED. A shape has to
        // be redrawn from its tag, which is what resize_shape_annotation
        // does. The app only reached for that on a RESIZE, so a shape being
        // MOVED as the anchor went down a path that cannot move it.
        let mut dead_index = -1;
        assert_eq!(
            resize_annotation(
                handle, 0, 0, CAP,
                0.50 * CAP as f32, 0.50 * CAP as f32,
                0.70 * CAP as f32, 0.70 * CAP as f32,
                &mut dead_index as *mut i32,
            ),
            STATUS_UNSUPPORTED,
            "resize_annotation is expected to refuse a shape; if it ever starts \
             succeeding, the anchor move path can be simplified"
        );

        let mut new_index = -1;
        let status = resize_shape_annotation(
            handle, 0, 0, CAP,
            0.50 * CAP as f32, 0.50 * CAP as f32,
            0.70 * CAP as f32, 0.70 * CAP as f32,
            &mut new_index as *mut i32,
        );
        assert_eq!(status, STATUS_OK_PDFIUM, "the move itself must succeed");

        let after = red_bbox_norm(handle, 600).expect("shape should still be on the page");
        close_document(handle);

        println!("SHAPE MOVE: before {before:?} after {after:?} (asked to move to 0.50..0.70)");

        let tol = 0.03;
        assert!(
            (after.0 - 0.50).abs() < tol,
            "asked to move the shape to x 0.50, and the DRAWING is at x {:.3} \
             (it started at {:.3}). The call reported success, so the /Rect \
             moved; the painted path did not follow it.",
            after.0,
            before.0
        );
    }

    #[test]
    fn a_shape_lands_where_it_was_asked_for_on_an_uncropped_page() {
        // The control for the cropped case below. On a page whose crop box
        // equals its media box, the two origins agree and the shape lands
        // exactly where it was asked for. If THIS ever fails the problem is
        // not crop-vs-media and the other test's diagnosis is wrong.
        let handle = open_fixture_named("tests/fixtures/sample.pdf");
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.25 * CAP as f32,
            y1: 0.25 * CAP as f32,
            x2: 0.75 * CAP as f32,
            y2: 0.75 * CAP as f32,
            r: 255,
            g: 0,
            b: 0,
            a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );
        let bbox = red_bbox_norm(handle, 600).expect("the shape should have put red ink on the page");
        close_document(handle);

        println!("UNCROPPED-PAGE SHAPE BBOX: {bbox:?} (asked for 0.25..0.75 in x)");
        let tol = 0.02;
        assert!(
            (bbox.0 - 0.25).abs() < tol && (bbox.2 - 0.75).abs() < tol,
            "on an UNCROPPED page a shape asked for at x 0.25..0.75 rendered \
             at x {:.3}..{:.3}",
            bbox.0,
            bbox.2
        );
    }

    #[test]
    fn a_shape_lands_where_it_was_asked_for_on_a_cropped_page() {
        // The annotation write path takes its origin from the MEDIA box while
        // the render path crops to the CROP box. On a page where those differ
        // (print-ready PDFs, scans, anything with trim marks) a shape asked
        // for at the middle of the page is written relative to the media
        // origin and then DRAWN relative to the crop origin, so it lands
        // displaced by the difference. The selection frame, positioned from
        // get_annotations (media origin again), then does not sit on the
        // shape the user can see.
        //
        // This test pins the user-visible contract: ask for a rect, render,
        // and the ink must be there. It fails on a cropped page until the
        // annotation paths use the same box the renderer does.
        use pdfium_render::prelude::*;

        let handle = open_fixture_named("tests/fixtures/sample.pdf");

        // Inset the crop box well inside the media box, asymmetrically so a
        // sign error cannot cancel out.
        {
            let _guard = lock(&CALL_LOCK);
            let doc = lock(&core().documents).get(&handle).cloned().unwrap();
            let g = lock(&doc);
            let mut page = g.pages().get(0).unwrap();
            let media = page.boundaries().media().map(|b| b.bounds).unwrap();
            let crop = PdfRect::new(
                PdfPoints::new(media.bottom().value + 30.0),
                PdfPoints::new(media.left().value + 60.0),
                PdfPoints::new(media.top().value - 90.0),
                PdfPoints::new(media.right().value - 20.0),
            );
            page.boundaries_mut().set_crop(crop).unwrap();
        }

        // Ask for a rectangle in the middle half of the (cropped) page.
        const CAP: i32 = 1000;
        let spec = ShapeSpec {
            page_index: 0,
            kind: SHAPE_RECTANGLE,
            x1: 0.25 * CAP as f32,
            y1: 0.25 * CAP as f32,
            x2: 0.75 * CAP as f32,
            y2: 0.75 * CAP as f32,
            r: 255,
            g: 0,
            b: 0,
            a: 255,
            width_px: 4.0,
            rotation_deg: 0.0,
            fill_rgba: 0, corner_radius_px: 0.0,
            shadow_angle_deg: 0.0, shadow_distance_px: 0.0,
            shadow_softness_px: 0.0, shadow_spread_px: 0.0, shadow_rgba: 0,
        };
        assert_eq!(
            add_shape_annotations(handle, CAP, &spec as *const ShapeSpec, 1),
            STATUS_OK_PDFIUM
        );

        let bbox = red_bbox_norm(handle, 600).expect("the shape should have put red ink on the page");
        close_document(handle);

        println!("CROPPED-PAGE SHAPE BBOX: {bbox:?} (asked for 0.25..0.75 in x)");

        // Generous tolerance: this is about gross displacement, not sub-pixel
        // stroke placement. A crop offset of 60pt on a 612pt page is ~0.1
        // normalized, which is 20x this tolerance.
        let tol = 0.02;
        assert!(
            (bbox.0 - 0.25).abs() < tol && (bbox.2 - 0.75).abs() < tol,
            "shape asked for x 0.25..0.75 rendered at x {:.3}..{:.3} - \
             the write path used the media origin but the renderer crops to \
             the crop box, so the shape is displaced by the difference",
            bbox.0,
            bbox.2
        );
    }

    // ---------------- Encrypted documents ----------------

    /// The password on the generated fixture. Not a secret: it is checked in.
    const FIXTURE_PASSWORD: &str = "hunter2";

    /// Builds an encrypted PDF beside the other fixtures, if it is not there.
    ///
    /// Generated rather than committed as an opaque blob, so what makes it
    /// encrypted is readable, and so it can be rebuilt if it is ever lost. RC4
    /// 128-bit: it is what most protected PDFs in the wild actually use, and
    /// the point is to exercise PDFium's password path, not a cipher.
    fn encrypted_fixture() -> String {
        use lopdf::encryption::{EncryptionState, EncryptionVersion};
        use lopdf::Permissions;

        let path = "tests/fixtures/sample_encrypted.pdf".to_string();
        if std::path::Path::new(&path).exists() {
            return path;
        }

        let mut doc = lopdf::Document::load("tests/fixtures/sample.pdf")
            .expect("the plain fixture must be there to encrypt");

        // The standard security handler mixes the file's /ID into the key, so a
        // document without one cannot be encrypted at all. The plain fixture
        // has no /ID, so give it a fixed one: fixed rather than random, so
        // rebuilding the fixture produces the same bytes.
        let id: Vec<u8> = (0u8..16).collect();
        doc.trailer.set(
            "ID",
            lopdf::Object::Array(vec![
                lopdf::Object::String(id.clone(), lopdf::StringFormat::Hexadecimal),
                lopdf::Object::String(id, lopdf::StringFormat::Hexadecimal),
            ]),
        );

        let version = EncryptionVersion::V2 {
            document: &doc,
            owner_password: "owner-of-the-fixture",
            user_password: FIXTURE_PASSWORD,
            // BITS, not bytes: this is written straight into /Length, which the
            // spec defines in bits. 16 produced a document PDFium would not
            // open with the correct password.
            key_length: 128,
            permissions: Permissions::all(),
        };

        let state = EncryptionState::try_from(version).expect("failed to build encryption state");
        doc.encrypt(&state).expect("failed to encrypt the fixture");

        // Written aside and renamed into place. Tests run in parallel and every
        // one of them wants this file, so writing directly would let one read a
        // half-written PDF and fail for a reason that has nothing to do with
        // what it is testing.
        let staging = std::env::temp_dir().join(format!("enc_fixture_{}.pdf", std::process::id()));
        doc.save(&staging).expect("failed to write the encrypted fixture");
        let _ = std::fs::rename(&staging, &path);

        path
    }

    fn open_protected(path: &str, password: Option<&str>) -> OpenResult {
        let c_path = std::ffi::CString::new(path).unwrap();
        match password {
            Some(text) => {
                let c_pw = std::ffi::CString::new(text).unwrap();
                open_document_protected(c_path.as_ptr(), c_pw.as_ptr())
            }
            None => open_document_protected(c_path.as_ptr(), std::ptr::null()),
        }
    }

    #[test]
    fn an_encrypted_document_asks_for_a_password_rather_than_looking_broken() {
        // The whole point of the new status. Before this, a protected PDF and a
        // corrupt one were both a zero handle, so every protected PDF in the
        // world looked to the app like a damaged file.
        let result = open_protected(&encrypted_fixture(), None);

        assert_eq!(result.handle, 0);
        assert_eq!(
            result.status, STATUS_NEEDS_PASSWORD,
            "an encrypted document must be distinguishable from a broken one"
        );
    }

    #[test]
    fn the_right_password_opens_the_document() {
        let result = open_protected(&encrypted_fixture(), Some(FIXTURE_PASSWORD));

        assert_eq!(result.status, STATUS_OK_PDFIUM);
        assert_ne!(result.handle, 0);

        // Really open, not merely accepted: it renders.
        let page = render_low_res(result.handle, 0, 120);
        assert_eq!(page.status, STATUS_OK_PDFIUM);
        free_render_result(page);

        close_document(result.handle);
    }

    #[test]
    fn a_wrong_password_asks_again_rather_than_failing_hard() {
        // Reported the same way as no password at all, because that is what the
        // prompt does with it: ask again.
        let result = open_protected(&encrypted_fixture(), Some("not-the-password"));

        assert_eq!(result.handle, 0);
        assert_eq!(result.status, STATUS_NEEDS_PASSWORD);
    }

    #[test]
    fn a_corrupt_file_is_not_reported_as_needing_a_password() {
        // The other half of the distinction. Prompting for a password on a file
        // that is simply not a PDF would send the user hunting for a password
        // that does not exist.
        let junk = std::env::temp_dir().join("render_core_not_a_pdf.pdf");
        std::fs::write(&junk, b"this is not a PDF at all").unwrap();

        let result = open_protected(junk.to_str().unwrap(), None);

        assert_eq!(result.handle, 0);
        assert_eq!(result.status, STATUS_INVALID_INPUT);

        let _ = std::fs::remove_file(&junk);
    }

    #[test]
    fn an_unprotected_document_is_unaffected_by_the_new_path() {
        // Every document anyone opens goes through this now, so the ordinary
        // case has to be exactly what it was.
        let plain = open_protected("tests/fixtures/sample.pdf", None);
        assert_eq!(plain.status, STATUS_OK_PDFIUM);
        assert_ne!(plain.handle, 0);
        close_document(plain.handle);

        // An empty password means "none", not "try the empty string", which
        // PDFium would treat as a failed attempt on an unprotected file.
        let empty = open_protected("tests/fixtures/sample.pdf", Some(""));
        assert_eq!(empty.status, STATUS_OK_PDFIUM);
        assert_ne!(empty.handle, 0);
        close_document(empty.handle);
    }

    #[test]
    fn the_old_entry_point_still_behaves() {
        // open_document is now a thin call onto the protected path. It must
        // keep returning a bare handle, and zero for a document it cannot open.
        let handle = open_fixture_named("tests/fixtures/sample.pdf");
        close_document(handle);

        let c_path = std::ffi::CString::new(encrypted_fixture()).unwrap();
        assert_eq!(open_document(c_path.as_ptr()), 0);
    }

    // ---------------- Styled text runs ----------------

    #[derive(Debug)]
    struct ParsedRun {
        start: i32,
        count: i32,
        line: u32,
        size: f32,
        color: u32,
        font: String,
        text: String,
    }

    /// Reads back what `get_page_text_runs` wrote, so the tests assert on the
    /// wire format the C# side will actually parse rather than on internals.
    fn parse_runs(buffer: &ByteBuffer) -> Vec<ParsedRun> {
        parse_runs_and_chars(buffer).0
    }

    fn parse_runs_and_chars(buffer: &ByteBuffer) -> (Vec<ParsedRun>, u32) {
        assert_eq!(buffer.status, STATUS_OK_PDFIUM);
        if buffer.data.is_null() {
            return (Vec::new(), 0);
        }

        let bytes = unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) };
        let mut p = 0usize;

        let mut u32_at = |p: &mut usize| {
            let v = u32::from_le_bytes(bytes[*p..*p + 4].try_into().unwrap());
            *p += 4;
            v
        };

        let count = u32_at(&mut p);
        let chars_on_page = u32_at(&mut p);
        let mut runs = Vec::new();

        for _ in 0..count {
            let start = u32_at(&mut p) as i32;
            let char_count = u32_at(&mut p) as i32;
            let line = u32_at(&mut p);
            let size = f32::from_le_bytes((u32_at(&mut p)).to_le_bytes());
            let color = u32_at(&mut p);

            let font_len = u32_at(&mut p) as usize;
            let font = String::from_utf8(bytes[p..p + font_len].to_vec()).unwrap();
            p += font_len;

            let text_len = u32_at(&mut p) as usize;
            let text = String::from_utf8(bytes[p..p + text_len].to_vec()).unwrap();
            p += text_len;

            runs.push(ParsedRun { start, count: char_count, line, size, color, font, text });
        }

        assert_eq!(p, buffer.len, "the buffer had bytes nobody claimed");
        (runs, chars_on_page)
    }

    /// A page with a red 18pt bold heading over 10pt body text, built rather
    /// than committed so what makes it a useful fixture is readable.
    fn styled_fixture() -> String {
        use lopdf::content::{Content, Operation};
        use lopdf::{dictionary, Document, Object, Stream};

        let path = "tests/fixtures/sample_styled.pdf".to_string();
        if std::path::Path::new(&path).exists() {
            return path;
        }

        let mut doc = Document::with_version("1.5");
        let pages_id = doc.new_object_id();

        let bold = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Helvetica-Bold",
        });
        let plain = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Helvetica",
        });
        let resources = doc.add_object(dictionary! {
            "Font" => dictionary! { "FH" => bold, "FB" => plain },
        });

        let content = Content {
            operations: vec![
                // The heading: bold, 18pt, red.
                Operation::new("BT", vec![]),
                Operation::new("rg", vec![1.into(), 0.into(), 0.into()]),
                Operation::new("Tf", vec!["FH".into(), 18.into()]),
                Operation::new("Td", vec![72.into(), 700.into()]),
                Operation::new("Tj", vec![Object::string_literal("Chapter One")]),
                Operation::new("ET", vec![]),
                // The body: plain, 10pt, black.
                Operation::new("BT", vec![]),
                Operation::new("rg", vec![0.into(), 0.into(), 0.into()]),
                Operation::new("Tf", vec!["FB".into(), 10.into()]),
                Operation::new("Td", vec![72.into(), 660.into()]),
                Operation::new("Tj", vec![Object::string_literal("Ordinary body text.")]),
                Operation::new("ET", vec![]),
                // A style change with NO line break across it, which is the
                // only case that proves runs split on style at all: without
                // this, the heading and the body are told apart by the newline
                // between them and the comparison could be deleted outright
                // with every test still passing.
                Operation::new("BT", vec![]),
                Operation::new("Tf", vec!["FB".into(), 10.into()]),
                Operation::new("Td", vec![72.into(), 620.into()]),
                Operation::new("Tj", vec![Object::string_literal("Same line: ")]),
                Operation::new("Tf", vec!["FH".into(), 14.into()]),
                Operation::new("Tj", vec![Object::string_literal("louder")]),
                Operation::new("ET", vec![]),
            ],
        };

        let content_id = doc.add_object(Stream::new(dictionary! {}, content.encode().unwrap()));
        let page_id = doc.add_object(dictionary! {
            "Type" => "Page",
            "Parent" => pages_id,
            "Contents" => content_id,
            "Resources" => resources,
            "MediaBox" => vec![0.into(), 0.into(), 612.into(), 792.into()],
        });

        doc.objects.insert(pages_id, Object::Dictionary(dictionary! {
            "Type" => "Pages",
            "Kids" => vec![page_id.into()],
            "Count" => 1,
        }));

        let catalog_id = doc.add_object(dictionary! {
            "Type" => "Catalog", "Pages" => pages_id,
        });
        doc.trailer.set("Root", catalog_id);

        // Staged and renamed, because these tests run in parallel and every one
        // of them wants this file: writing straight to it would let one test
        // read a half-written PDF and fail for a reason unrelated to what it
        // tests.
        //
        // Staged BESIDE the target rather than in the temp directory. A rename
        // across volumes fails on Windows, and the temp directory is on C:
        // while the fixtures are on E:, so the file was written and then went
        // nowhere. Same directory also makes the rename atomic.
        let staging = format!("tests/fixtures/.styled_fixture_{}.pdf", std::process::id());
        doc.save(&staging).expect("failed to write the styled fixture");
        std::fs::rename(&staging, &path).expect("failed to move the styled fixture into place");

        path
    }

    #[test]
    fn text_runs_tell_a_heading_from_the_body_under_it() {
        // The whole point of the feature: two pieces of text that get_page_chars
        // reports identically, told apart by what they are SET in.
        let handle = open_fixture_named(&styled_fixture());
        let buffer = get_page_text_runs(handle, 0);
        let runs = parse_runs(&buffer);
        free_byte_buffer(buffer);

        let heading = runs
            .iter()
            .find(|r| r.text.contains("Chapter One"))
            .expect("the heading was not reported at all");
        let body = runs
            .iter()
            .find(|r| r.text.contains("Ordinary body"))
            .expect("the body was not reported at all");

        assert!((heading.size - 18.0).abs() < 0.01, "heading size was {}", heading.size);
        assert!((body.size - 10.0).abs() < 0.01, "body size was {}", body.size);
        assert_ne!(heading.font, body.font, "both were reported in the same font");
        assert!(heading.font.contains("Bold"), "heading font was {}", heading.font);

        // Packed 0x00RRGGBB, so a red heading is 0xFF0000 and black body is 0.
        assert_eq!(heading.color, 0xFF_00_00);
        assert_eq!(body.color, 0);

        close_document(handle);
    }

    #[test]
    fn runs_on_one_line_share_a_line_number_and_the_next_line_does_not() {
        // What tells "the next word on this line" from "the first word of the
        // next one". Documents exist that set every word as its own text object
        // in its own font, and without this every word looks like a separate
        // piece of text; measured on a real book, 4765 runs across seven pages
        // with a median length of three characters.
        let handle = open_fixture_named(&styled_fixture());
        let buffer = get_page_text_runs(handle, 0);
        let runs = parse_runs(&buffer);
        free_byte_buffer(buffer);
        close_document(handle);

        let lead = runs.iter().find(|r| r.text.contains("Same line")).unwrap();
        let loud = runs.iter().find(|r| r.text.contains("louder")).unwrap();
        let heading = runs.iter().find(|r| r.text.contains("Chapter One")).unwrap();

        assert_eq!(lead.line, loud.line, "two runs on one line got different line numbers");
        assert_ne!(
            heading.line, lead.line,
            "a run on another line got the same line number");
    }

    #[test]
    fn a_style_change_splits_a_run_without_a_line_break_to_help() {
        // The case that actually tests the comparison. Deliberately breaking
        // the split so every run merged left the test above passing, because
        // the heading and the body are on different LINES and a line ending
        // ends a run on its own.
        let handle = open_fixture_named(&styled_fixture());
        let buffer = get_page_text_runs(handle, 0);
        let runs = parse_runs(&buffer);
        free_byte_buffer(buffer);

        let lead = runs
            .iter()
            .find(|r| r.text.contains("Same line"))
            .expect("the mixed line was not reported");
        let loud = runs
            .iter()
            .find(|r| r.text.contains("louder"))
            .expect("the mixed line came back as one run, so style is not splitting it");

        assert!((lead.size - 10.0).abs() < 0.01, "lead size was {}", lead.size);
        assert!((loud.size - 14.0).abs() < 0.01, "loud size was {}", loud.size);

        close_document(handle);
    }

    #[test]
    fn a_run_says_which_characters_it_covers() {
        // The sample is captured from a text SELECTION, which the app knows
        // only as a character range, so a run has to be locatable by one.
        let handle = open_fixture_named(&styled_fixture());
        let buffer = get_page_text_runs(handle, 0);
        let runs = parse_runs(&buffer);
        free_byte_buffer(buffer);

        for run in &runs {
            assert!(run.start >= 0, "run started at {}", run.start);
            assert_eq!(
                run.count,
                run.text.chars().count() as i32,
                "run {:?} claims {} characters", run.text, run.count);
        }

        // And they do not overlap, so a character index picks out one run.
        for pair in runs.windows(2) {
            assert!(
                pair[1].start >= pair[0].start + pair[0].count,
                "runs {:?} and {:?} overlap", pair[0].text, pair[1].text);
        }

        close_document(handle);
    }

    /// What a whole-book scan costs, since the dialog has to wait for one.
    ///
    /// Ignored by default like the other measurements here: it reports a number
    /// rather than asserting one, and a timing assertion on a shared machine
    /// fails for reasons that have nothing to do with the code.
    ///
    /// A hundred pages of DENSE text: forty lines a page, a heading on each.
    ///
    /// The committed 300-page fixture carries one line per page, which measures
    /// the per-page cost and hides the per-character one. Every character here
    /// is asked for its font and its colour, so a page of real text is the only
    /// honest thing to time.
    fn dense_fixture() -> String {
        use lopdf::content::{Content, Operation};
        use lopdf::{dictionary, Document, Object, Stream};

        let path = "tests/fixtures/.dense_timing.pdf".to_string();
        if std::path::Path::new(&path).exists() {
            return path;
        }

        let mut doc = Document::with_version("1.5");
        let pages_id = doc.new_object_id();
        let bold = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Helvetica-Bold",
        });
        let plain = doc.add_object(dictionary! {
            "Type" => "Font", "Subtype" => "Type1", "BaseFont" => "Helvetica",
        });
        let resources = doc.add_object(dictionary! {
            "Font" => dictionary! { "FH" => bold, "FB" => plain },
        });

        let mut kids = Vec::new();
        for page in 0..100 {
            let mut ops = vec![
                Operation::new("BT", vec![]),
                Operation::new("Tf", vec!["FH".into(), 18.into()]),
                Operation::new("Td", vec![50.into(), 760.into()]),
                Operation::new("Tj", vec![Object::string_literal(format!("Section {page}"))]),
                Operation::new("ET", vec![]),
            ];

            for line in 0..40 {
                ops.push(Operation::new("BT", vec![]));
                ops.push(Operation::new("Tf", vec!["FB".into(), 10.into()]));
                ops.push(Operation::new("Td", vec![50.into(), (730 - line * 18).into()]));
                ops.push(Operation::new("Tj", vec![Object::string_literal(
                    "The quick brown fox jumps over the lazy dog again and again.")]));
                ops.push(Operation::new("ET", vec![]));
            }

            let content_id = doc.add_object(Stream::new(
                dictionary! {}, Content { operations: ops }.encode().unwrap()));
            kids.push(Object::Reference(doc.add_object(dictionary! {
                "Type" => "Page",
                "Parent" => pages_id,
                "Contents" => content_id,
                "Resources" => resources,
                "MediaBox" => vec![0.into(), 0.into(), 612.into(), 792.into()],
            })));
        }

        let count = kids.len() as i64;
        doc.objects.insert(pages_id, Object::Dictionary(dictionary! {
            "Type" => "Pages", "Kids" => kids, "Count" => count,
        }));
        let catalog_id = doc.add_object(dictionary! { "Type" => "Catalog", "Pages" => pages_id });
        doc.trailer.set("Root", catalog_id);
        doc.save(&path).expect("failed to write the dense fixture");

        path
    }

    /// Run with: cargo test --release scan_cost -- --ignored --nocapture
    #[test]
    #[ignore]
    fn scan_cost_per_page() {
        let handle = open_fixture_named(&dense_fixture());
        let pages = get_page_count(handle);

        let started = std::time::Instant::now();
        let mut runs = 0usize;
        for page in 0..pages {
            let buffer = get_page_text_runs(handle, page);
            runs += parse_runs(&buffer).len();
            free_byte_buffer(buffer);
        }
        let elapsed = started.elapsed();

        println!(
            "{pages} pages, {runs} runs, {:.0}ms total, {:.2}ms/page",
            elapsed.as_secs_f64() * 1000.0,
            elapsed.as_secs_f64() * 1000.0 / pages as f64);

        close_document(handle);
    }

    #[test]
    fn a_page_with_no_text_reports_no_runs_rather_than_failing() {
        // A scanned page is an ordinary thing to run this over, and it must be
        // an empty answer rather than an error the scan stops on.
        let handle = open_fixture_named("tests/fixtures/blank.pdf");
        let buffer = get_page_text_runs(handle, 0);

        assert_eq!(buffer.status, STATUS_OK_PDFIUM);
        let (runs, chars) = parse_runs_and_chars(&buffer);
        assert!(runs.is_empty());

        // And it says the page held no characters, which is what separates a
        // scan from a page whose text nothing can read. Both give no runs.
        assert_eq!(chars, 0);

        free_byte_buffer(buffer);
        close_document(handle);
    }

    #[test]
    fn a_page_reports_how_many_characters_it_held_altogether() {
        // Including the ones no run kept: the whitespace between things, and
        // any character PDFium could not map to Unicode. A page with plenty of
        // characters and no runs is a document whose fonts carry no /ToUnicode
        // map, which defeats every reader's search and not only ours.
        let handle = open_fixture_named(&styled_fixture());
        let buffer = get_page_text_runs(handle, 0);
        let (runs, chars) = parse_runs_and_chars(&buffer);
        free_byte_buffer(buffer);
        close_document(handle);

        let in_runs: i32 = runs.iter().map(|r| r.count).sum();
        assert!(chars > 0, "the page reported no characters at all");
        assert!(
            chars >= in_runs as u32,
            "{chars} characters on the page but {in_runs} in runs, which cannot be");
    }

    /// Adds a text box at a chosen size, so a page can carry two styles.
    fn sized_box(handle: u64, words: &str, top: f32, size: f32, font: Option<&str>) -> i32 {
        let bytes = words.as_bytes();
        let (fp, fl) = match font {
            Some(p) => (p.as_ptr(), p.len()),
            None => (std::ptr::null(), 0),
        };
        add_text_box_annotation_styled(
            handle, 0, 1000, 60.0, top, 560.0, top + 80.0,
            bytes.as_ptr(), bytes.len(), size, 0, 0, 0, 255,
            ALIGN_LEFT, 0, 0, 0.0, fp, fl, 0, 0)
    }

    /// The runs on page 0 after a save and reopen, as (font, size, text).
    fn runs_after_round_trip(handle: u64) -> Vec<ParsedRun> {
        let saved = snapshot_document(handle);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        let buffer = get_page_text_runs(reopened, 0);
        let runs = parse_runs(&buffer);
        free_byte_buffer(buffer);
        close_document(reopened);
        free_byte_buffer(saved);
        runs
    }

    #[test]
    fn devanagari_comes_back_as_its_own_characters_not_as_shaped_glyphs() {
        // The question a Hindi or Burmese document raises: a heading is drawn
        // as SHAPED glyphs, where "कि" is one glyph and its vowel sits to the
        // LEFT of the consonant it follows. If a run reported that, every
        // bookmark title in the document would be scrambled, and the title
        // would not match the text anyone searched for.
        let Some(font) = devanagari_font() else {
            eprintln!("no Devanagari font on this machine; skipping");
            return;
        };

        const HEADING: &str = "अध्याय एक";

        let h = open_fixture_named("tests/fixtures/blank.pdf");
        assert_eq!(sized_box(h, HEADING, 600.0, 24.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let runs = runs_after_round_trip(h);
        close_document(h);

        assert!(
            runs.iter().any(|r| r.text.contains(HEADING)),
            "the heading did not come back intact; runs were {:?}",
            runs.iter().map(|r| &r.text).collect::<Vec<_>>());
    }

    #[test]
    fn a_complex_script_page_still_separates_a_heading_from_its_body() {
        // The feature itself, in Devanagari: two sizes on one page have to come
        // back as two styles, or there is nothing to tick.
        let Some(font) = devanagari_font() else {
            eprintln!("no Devanagari font on this machine; skipping");
            return;
        };

        const HEADING: &str = "पहला अध्याय";
        const BODY: &str = "यह सामान्य पाठ है";

        let h = open_fixture_named("tests/fixtures/blank.pdf");
        assert_eq!(sized_box(h, HEADING, 640.0, 24.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sized_box(h, BODY, 400.0, 11.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let runs = runs_after_round_trip(h);
        close_document(h);

        let heading = runs.iter().find(|r| r.text.contains(HEADING));
        let body = runs.iter().find(|r| r.text.contains(BODY));

        let (Some(heading), Some(body)) = (heading, body) else {
            panic!("one of the two did not come back; runs were {:?}",
                   runs.iter().map(|r| (&r.text, r.size)).collect::<Vec<_>>());
        };

        assert!(
            heading.size > body.size,
            "the heading reported {}pt and the body {}pt, so nothing separates them",
            heading.size, body.size);
    }

    #[test]
    fn burmese_comes_back_as_its_own_characters_too() {
        // Burmese stacks SEVERAL characters into one glyph, which is the other
        // half of the same worry: a run must report what was typed, not what
        // was drawn.
        let Some(font) = burmese_font() else {
            eprintln!("no Burmese font on this machine; skipping");
            return;
        };

        const HEADING: &str = "အခန်း တစ်";

        let h = open_fixture_named("tests/fixtures/blank.pdf");
        assert_eq!(sized_box(h, HEADING, 600.0, 24.0, Some(font)), STATUS_OK_PDFIUM);
        assert_eq!(sync(h), STATUS_OK_PDFIUM);

        let runs = runs_after_round_trip(h);
        close_document(h);

        assert!(
            runs.iter().any(|r| r.text.contains(HEADING)),
            "the heading did not come back intact; runs were {:?}",
            runs.iter().map(|r| &r.text).collect::<Vec<_>>());
    }

    #[test]
    fn text_runs_refuse_a_handle_or_page_that_is_not_there() {
        let handle = open_fixture_named(&styled_fixture());

        assert_eq!(get_page_text_runs(0, 0).status, STATUS_INVALID_INPUT);
        assert_eq!(get_page_text_runs(handle, -1).status, STATUS_INVALID_INPUT);
        assert_eq!(get_page_text_runs(handle, 9_999).status, STATUS_INVALID_INPUT);

        close_document(handle);
    }
}


