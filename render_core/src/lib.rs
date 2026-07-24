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
    let key = TileKey { doc: doc_handle, page: page_index, tier: Tier::Low, width: target_width };

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
    let key = TileKey { doc: doc_handle, page: page_index, tier: Tier::High, width: target_width };

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
        let key = TileKey { doc: u64::MAX, page: 0, tier: Tier::High, width: 1 };

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
