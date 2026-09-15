//! The OCR text layer: words recognised on a scanned page, written back into
//! the page as invisible text so it can be searched, selected and copied.
//!
//! The same technique as the text-box searchable layer in `sync_text_layer`,
//! written separately so nothing there changes (that subsystem is frozen):
//! invisible text objects (render mode 3) carrying the recognised characters,
//! each marked after it joins the page so it can be told apart from text the
//! document already had.

use std::ffi::{c_char, CStr};
use std::panic;

use crate::{
    call_guard, evict_page_cache, lock, resolve_text_font, STATUS_INVALID_INPUT, STATUS_PANIC,
    STATUS_UNSUPPORTED,
};

/// The mark every OCR run wears. No id: the layer belongs to the page, and
/// recognising the page again replaces all of it.
pub(crate) const OCR_MARK: &str = "AyaanOcr";

/// The device size the recogniser's fractions are scaled to before PDFium maps
/// them back to page space. Device coordinates are whole numbers, so this sets
/// the precision: 100,000 across a Letter page is under a hundredth of a point.
const DEVICE_SPAN: f32 = 100_000.0;

/// True when a text object belongs to the OCR layer this app wrote.
///
/// By the mark, never by render mode 3: another program's OCR layer is render
/// mode 3 as well, and it is the page's own text as far as this app is concerned.
pub(crate) fn is_ocr_run(
    bindings: &dyn pdfium_render::prelude::PdfiumLibraryBindings,
    handle: pdfium_render::prelude::FPDF_PAGEOBJECT,
) -> bool {
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
        if String::from_utf16_lossy(&buf) == OCR_MARK {
            return true;
        }
    }
    false
}

/// One recognised word, as the recogniser saw it.
///
/// The box is a fraction of the page AS RENDERED: 0..1 across and down from
/// the top-left corner, in whatever orientation the page is displayed. The core
/// maps it back through PDFium's own device-to-page transform, so the page's
/// rotation and crop box are undone exactly the way rendering applied them.
#[repr(C)]
pub struct OcrWord {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    /// UTF-8, not NUL-terminated.
    pub text: *const u8,
    pub text_len: usize,
}

struct Word {
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
    text: String,
}

/// Replaces the OCR layer on one page with the given words.
///
/// Returns how many words were written (none clears the layer), or a negative
/// status: `-STATUS_INVALID_INPUT` for a bad handle or page, and
/// `-STATUS_UNSUPPORTED` when the page could not be rewritten.
///
/// `font_path` names a TrueType font whose glyphs cover the words' script, or
/// is null for Helvetica. The text is invisible, but a reader still maps each
/// character through the font to extract it, so a font without the script would
/// leave the words unsearchable.
///
/// # Safety
/// `words` must point to `count` readable [`OcrWord`]s, each `text` covering
/// `text_len` bytes; `font_path` must be null or a NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn add_ocr_words(
    doc_handle: u64,
    page_index: i32,
    words: *const OcrWord,
    count: usize,
    font_path: *const c_char,
) -> i32 {
    if doc_handle == 0 || page_index < 0 || (words.is_null() && count != 0) {
        return -STATUS_INVALID_INPUT;
    }

    // Copied out first, so the caller's buffers are read here and nowhere else.
    let raw: &[OcrWord] = if count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(words, count) }
    };
    let mut owned = Vec::with_capacity(raw.len());
    for w in raw {
        if w.text.is_null() || w.text_len == 0 {
            continue;
        }
        let bytes = unsafe { std::slice::from_raw_parts(w.text, w.text_len) };
        let Ok(text) = std::str::from_utf8(bytes) else {
            continue;
        };
        owned.push(Word {
            left: w.left,
            top: w.top,
            right: w.right,
            bottom: w.bottom,
            text: text.to_string(),
        });
    }
    let font = if font_path.is_null() {
        None
    } else {
        unsafe { CStr::from_ptr(font_path) }.to_str().ok().map(str::to_string)
    };

    panic::catch_unwind(move || add_ocr_words_inner(doc_handle, page_index, &owned, font.as_deref()))
        .unwrap_or(-STATUS_PANIC)
}

/// The font each open document's OCR layers are written in, by document and font
/// file, as the token's address.
///
/// ⚠️ LOADED ONCE PER DOCUMENT. Loading it for every page put a whole copy of the
/// font into the document for every page recognised: a 359-page Hindi book grew
/// by about 680 KB a page, in memory and in every recovery snapshot, and the app
/// went down about 80% of the way through.
static LOADED_FONTS: std::sync::Mutex<std::collections::BTreeMap<(u64, String), usize>> =
    std::sync::Mutex::new(std::collections::BTreeMap::new());

/// Forgets a closed document's fonts.
pub(crate) fn forget_document(doc_handle: u64) {
    lock(&LOADED_FONTS).retain(|(doc, _), _| *doc != doc_handle);
}

/// The document's font for this file, loaded the first time it is asked for.
/// A remembered token is used only after the document confirms it still holds
/// that font, so a stale entry can never hand PDFium a font it does not have.
fn ocr_font(
    doc_handle: u64,
    doc: &mut pdfium_render::prelude::PdfDocument<'_>,
    font_path: Option<&str>,
) -> pdfium_render::prelude::PdfFontToken {
    use pdfium_render::prelude::PdfFontToken;

    let key = (doc_handle, font_path.unwrap_or_default().to_string());
    if let Some(address) = lock(&LOADED_FONTS).get(&key).copied() {
        let token = PdfFontToken::from_address(address);
        if doc.fonts().get(token).is_some() {
            return token;
        }
    }

    let token = resolve_text_font(doc, font_path);
    lock(&LOADED_FONTS).insert(key, token.to_address());
    token
}

fn add_ocr_words_inner(doc_handle: u64, page_index: i32, words: &[Word], font_path: Option<&str>) -> i32 {
    use pdfium_render::prelude::*;

    let _guard = call_guard();
    let doc = lock(&crate::core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return -STATUS_INVALID_INPUT;
    };
    let mut doc_guard = lock(&doc);
    if page_index >= doc_guard.pages().len() as i32 {
        return -STATUS_INVALID_INPUT;
    }

    // Loaded against the DOCUMENT, so before the page is borrowed, and found
    // before anything is removed, so a failure leaves the old layer as it was.
    // Once per document: see LOADED_FONTS.
    let token = ocr_font(doc_handle, &mut doc_guard, font_path);
    let Some(font) = doc_guard.fonts().get(token) else {
        return -STATUS_UNSUPPORTED;
    };

    let Ok(mut page) = doc_guard.pages().get(page_index as u16) else {
        return -STATUS_INVALID_INPUT;
    };
    let page_w = page.width().value;
    let page_h = page.height().value;
    if page_w <= 0.0 || page_h <= 0.0 {
        return -STATUS_INVALID_INPUT;
    }

    // One regeneration at the end: regenerating on every change invalidates the
    // handles still being walked (see sync_text_layer_inner).
    page.set_content_regeneration_strategy(PdfPageContentRegenerationStrategy::Manual);

    let bindings = doc_guard.bindings();

    // Out with the previous layer. Recognising a page again replaces it.
    let stale: Vec<PdfPageObjectIndex> = {
        let objects = page.objects();
        (0..objects.len())
            .filter(|i| {
                objects.get(*i).is_ok_and(|o| match &o {
                    PdfPageObject::Text(t) => is_ocr_run(bindings, t.object_handle()),
                    _ => false,
                })
            })
            .collect()
    };
    let had_stale = !stale.is_empty();
    // DESCENDING, because removing an object shifts every index above it.
    for index in stale.into_iter().rev() {
        let Ok(obj) = page.objects().get(index) else {
            continue;
        };
        if let Ok(removed) = page.objects_mut().remove_object(obj) {
            // FORGOTTEN, never dropped, for the reason recorded in
            // sync_text_layer_inner: Drop after FPDFPage_RemoveObject crashes.
            std::mem::forget(removed);
        }
    }

    let handle = page.page_handle();
    let span_x = DEVICE_SPAN.round() as i32;
    let span_y = ((DEVICE_SPAN * page_h / page_w).round() as i32).max(1);
    let to_page = |fx: f32, fy: f32| -> Option<(f32, f32)> {
        let (mut x, mut y) = (0.0f64, 0.0f64);
        let ok = bindings.FPDF_DeviceToPage(
            handle,
            0,
            0,
            span_x,
            span_y,
            0,
            (fx.clamp(0.0, 1.0) * span_x as f32).round() as i32,
            (fy.clamp(0.0, 1.0) * span_y as f32).round() as i32,
            &mut x,
            &mut y,
        );
        (ok != 0).then_some((x as f32, y as f32))
    };

    let mut written = 0i32;
    for word in words {
        let text = word.text.trim();
        if text.is_empty() || !(word.right > word.left) || !(word.bottom > word.top) {
            continue;
        }

        // Three corners of the box on the page: where the baseline starts, where
        // it ends, and the top of the word above its start. On a turned page
        // these point wherever the page turned them, which is how the words come
        // to run the same way as the picture.
        let (Some(origin), Some(along), Some(up)) = (
            to_page(word.left, word.bottom),
            to_page(word.right, word.bottom),
            to_page(word.left, word.top),
        ) else {
            continue;
        };
        let (ux, uy) = (along.0 - origin.0, along.1 - origin.1);
        let (vx, vy) = (up.0 - origin.0, up.1 - origin.1);
        let height = (vx * vx + vy * vy).sqrt();
        if height <= 0.0 || ux * ux + uy * uy <= 0.0 {
            continue;
        }

        let Ok(mut obj) = PdfPageTextObject::new(&doc_guard, text, font, PdfPoints::new(height)) else {
            continue;
        };
        if obj.set_render_mode(PdfPageTextRenderMode::Invisible).is_err() {
            // Without this the recognised words would be DRAWN over the picture.
            continue;
        }

        // The glyphs' own extent at that size, measured before any transform, so
        // the word can be laid exactly onto its box. A search hit or a selection
        // then covers the word in the picture rather than a guess at it.
        let Ok(extent) = obj.bounds() else {
            continue;
        };
        let x0 = extent.left().value;
        let y0 = extent.bottom().value;
        let w0 = extent.right().value - x0;
        let h0 = extent.top().value - y0;
        if w0 <= 0.0 || h0 <= 0.0 {
            continue;
        }
        let (a, b, c, d) = (ux / w0, uy / w0, vx / h0, vy / h0);
        let e = origin.0 - a * x0 - c * y0;
        let f = origin.1 - b * x0 - d * y0;
        if obj.transform(a, b, c, d, e, f).is_err() {
            continue;
        }

        // Marked AFTER it joins the page: a mark written on a detached object
        // crashes the next time it is read.
        if let Ok(attached) = page.objects_mut().add_object(obj.into()) {
            if let PdfPageObject::Text(t) = &attached {
                bindings.FPDFPageObj_AddMark(t.object_handle(), OCR_MARK);
                written += 1;
            }
        }
    }

    if (written > 0 || had_stale) && page.regenerate_content().is_err() {
        return -STATUS_UNSUPPORTED;
    }

    drop(page);
    drop(doc_guard);
    drop(doc);
    evict_page_cache(doc_handle, page_index);
    written
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        close_document, free_byte_buffer, free_char_info_array, free_render_result,
        get_page_chars, get_page_text_objects, open_document, open_document_from_bytes,
        render_uncached, rotate_page, snapshot_document, STATUS_OK_PDFIUM,
    };
    use std::ffi::CString;

    const BLANK: &str = "tests/fixtures/blank.pdf";
    const TWENTY_PAGES: &str = "tests/fixtures/sample_20pages.pdf";
    const DEVANAGARI_FONT: &str = r"C:\Windows\Fonts\Nirmala.ttf";
    const MYANMAR_FONT: &str = r"C:\Windows\Fonts\mmrtext.ttf";

    fn open(path: &str) -> u64 {
        let c = CString::new(path).unwrap();
        let handle = open_document(c.as_ptr());
        assert_ne!(handle, 0, "expected {path} to open");
        handle
    }

    /// Words with their boxes as fractions of the rendered page.
    fn write(handle: u64, page: i32, words: &[(&str, [f32; 4])], font: Option<&str>) -> i32 {
        let native: Vec<OcrWord> = words
            .iter()
            .map(|(text, b)| OcrWord {
                left: b[0],
                top: b[1],
                right: b[2],
                bottom: b[3],
                text: text.as_ptr(),
                text_len: text.len(),
            })
            .collect();
        let font = font.map(|f| CString::new(f).unwrap());
        unsafe {
            add_ocr_words(
                handle,
                page,
                native.as_ptr(),
                native.len(),
                font.as_ref().map_or(std::ptr::null(), |f| f.as_ptr()),
            )
        }
    }

    /// Height over width of the page as it actually renders.
    fn render_aspect(handle: u64, page: i32) -> f32 {
        let r = render_uncached(handle, page, 1000);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let aspect = r.height as f32 / r.width as f32;
        free_render_result(r);
        aspect
    }

    fn pixels(handle: u64, page: i32) -> Vec<u8> {
        let r = render_uncached(handle, page, 600);
        assert_eq!(r.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(r.buffer, r.len) }.to_vec();
        free_render_result(r);
        bytes
    }

    /// The page's characters as selection and search read them. Both axes are
    /// fractions of the page WIDTH, because get_page_chars scales both by one
    /// factor.
    fn chars(handle: u64, page: i32) -> Vec<(char, f32, f32, f32, f32)> {
        let width = 1000;
        let array = get_page_chars(handle, page, width);
        // 3 is a valid, empty answer: the page has no text layer.
        assert!(array.status == STATUS_OK_PDFIUM || array.status == 3, "status {}", array.status);
        let all = if array.len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(array.chars, array.len) }
        };
        let w = width as f32;
        let out = all
            .iter()
            .filter_map(|c| char::from_u32(c.codepoint).map(|ch| (ch, c.left / w, c.top / w, c.right / w, c.bottom / w)))
            .collect();
        free_char_info_array(array);
        out
    }

    fn visible_text(handle: u64, page: i32) -> String {
        chars(handle, page).iter().map(|c| c.0).filter(|c| !c.is_whitespace()).collect()
    }

    fn object_count(handle: u64, page: i32) -> usize {
        use pdfium_render::prelude::*;
        let _guard = call_guard();
        let doc = lock(&crate::core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        g.pages().get(page as u16).unwrap().objects().len() as usize
    }

    fn ocr_run_count(handle: u64, page: i32) -> usize {
        use pdfium_render::prelude::*;
        let _guard = call_guard();
        let doc = lock(&crate::core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(page as u16).unwrap();
        let objects = page.objects();
        (0..objects.len())
            .filter(|i| {
                objects.get(*i).is_ok_and(|o| match &o {
                    PdfPageObject::Text(t) => is_ocr_run(g.bindings(), t.object_handle()),
                    _ => false,
                })
            })
            .count()
    }

    fn text_object_count(handle: u64, page: i32) -> u32 {
        let buffer = get_page_text_objects(handle, page);
        assert_eq!(buffer.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) };
        let count = u32::from_le_bytes(bytes[0..4].try_into().unwrap());
        free_byte_buffer(buffer);
        count
    }

    /// Where PDFium would draw the page's one OCR run, as fractions of the
    /// render, measured independently of the writer: the object's page-space
    /// bounds sent forward through FPDF_PageToDevice at the RENDER's aspect.
    fn drawn_box_of_the_ocr_run(handle: u64, page_index: i32, aspect: f32) -> (f32, f32, f32, f32) {
        use pdfium_render::prelude::*;
        let _guard = call_guard();
        let doc = lock(&crate::core().documents).get(&handle).cloned().unwrap();
        let g = lock(&doc);
        let page = g.pages().get(page_index as u16).unwrap();
        let (sx, sy) = (100_000i32, (100_000.0 * aspect).round() as i32);
        let objects = page.objects();
        for i in 0..objects.len() {
            let Ok(o) = objects.get(i) else { continue };
            let PdfPageObject::Text(t) = &o else { continue };
            if !is_ocr_run(g.bindings(), t.object_handle()) {
                continue;
            }
            let q = o.bounds().unwrap();
            let (mut lo_x, mut lo_y, mut hi_x, mut hi_y) = (f32::MAX, f32::MAX, f32::MIN, f32::MIN);
            for (px, py) in [
                (q.left().value, q.bottom().value),
                (q.right().value, q.bottom().value),
                (q.left().value, q.top().value),
                (q.right().value, q.top().value),
            ] {
                let (mut dx, mut dy) = (0i32, 0i32);
                g.bindings().FPDF_PageToDevice(page.page_handle(), 0, 0, sx, sy, 0, px as f64, py as f64, &mut dx, &mut dy);
                let (fx, fy) = (dx as f32 / sx as f32, dy as f32 / sy as f32);
                lo_x = lo_x.min(fx);
                lo_y = lo_y.min(fy);
                hi_x = hi_x.max(fx);
                hi_y = hi_y.max(fy);
            }
            return (lo_x, lo_y, hi_x, hi_y);
        }
        panic!("no OCR run on the page");
    }

    #[test]
    fn recognised_words_become_page_text_over_the_words_in_the_picture() {
        let h = open(BLANK);
        let boxes = [("Recognised", [0.10, 0.20, 0.35, 0.24]), ("words", [0.38, 0.20, 0.52, 0.24])];
        assert_eq!(write(h, 0, &boxes, None), 2);
        assert_eq!(visible_text(h, 0), "Recognisedwords");

        // Each character sits inside its own word's box, and each word runs the
        // width of its box rather than sitting small in one corner of it.
        let aspect = render_aspect(h, 0);
        let got: Vec<_> = chars(h, 0).into_iter().filter(|c| !c.0.is_whitespace()).collect();
        let mut at = 0;
        for (word, b) in boxes {
            let n = word.chars().count();
            let run = &got[at..at + n];
            at += n;
            for c in run {
                let middle = (c.2 + c.4) / 2.0 / aspect;
                assert!(c.1 >= b[0] - 0.005 && c.3 <= b[2] + 0.005, "{:?} across {:.3}..{:.3}, box {:?}", c.0, c.1, c.3, b);
                assert!(middle >= b[1] && middle <= b[3], "{:?} middle {middle:.3}, box {:?}", c.0, b);
            }
            assert!((run[0].1 - b[0]).abs() < 0.01, "{word} starts at {:.3}, box {:?}", run[0].1, b);
            assert!((run[n - 1].3 - b[2]).abs() < 0.01, "{word} ends at {:.3}, box {:?}", run[n - 1].3, b);
        }
        close_document(h);
    }

    #[test]
    fn burmese_and_hindi_come_back_as_the_characters_that_were_recognised() {
        for (font, phrase) in [(MYANMAR_FONT, "မင်္ဂလာပါ ဆရာမ"), (DEVANAGARI_FONT, "क्षमा कीजिए")] {
            if !std::path::Path::new(font).exists() {
                println!("{font} is not on this machine");
                continue;
            }
            let h = open(BLANK);
            let parts: Vec<&str> = phrase.split(' ').collect();
            let boxes: Vec<(&str, [f32; 4])> = parts
                .iter()
                .enumerate()
                .map(|(i, p)| (*p, [0.10 + 0.30 * i as f32, 0.30, 0.35 + 0.30 * i as f32, 0.35]))
                .collect();
            assert_eq!(write(h, 0, &boxes, Some(font)), parts.len() as i32);
            assert_eq!(visible_text(h, 0), phrase.replace(' ', ""), "{font}");
            close_document(h);
        }
    }

    #[test]
    fn recognising_a_page_again_replaces_its_layer() {
        let h = open(BLANK);
        assert_eq!(write(h, 0, &[("First", [0.1, 0.1, 0.3, 0.14])], None), 1);
        let after_first = object_count(h, 0);

        assert_eq!(write(h, 0, &[("Second", [0.1, 0.1, 0.3, 0.14])], None), 1);
        assert_eq!(object_count(h, 0), after_first, "a second pass stacked a second layer");
        assert_eq!(visible_text(h, 0), "Second");

        assert_eq!(write(h, 0, &[], None), 0);
        assert_eq!(visible_text(h, 0), "", "an empty pass should clear the layer");
        close_document(h);
    }

    #[test]
    fn the_layer_changes_nothing_that_is_drawn() {
        let h = open(BLANK);
        let before = pixels(h, 0);
        assert_eq!(write(h, 0, &[("Invisible", [0.2, 0.5, 0.6, 0.56])], None), 1);
        assert!(!visible_text(h, 0).is_empty(), "control: the word must really be on the page");
        assert!(pixels(h, 0) == before, "the recognised words were drawn");
        close_document(h);
    }

    #[test]
    fn the_layer_survives_saving_and_is_still_known_as_ours() {
        let h = open(BLANK);
        assert_eq!(write(h, 0, &[("Kept", [0.1, 0.1, 0.3, 0.15])], None), 1);

        let saved = snapshot_document(h);
        assert_eq!(saved.status, STATUS_OK_PDFIUM);
        let reopened = open_document_from_bytes(saved.data, saved.len);
        assert_ne!(reopened, 0);

        assert_eq!(visible_text(reopened, 0), "Kept");
        assert_eq!(ocr_run_count(reopened, 0), 1, "the mark did not survive the save");
        assert_eq!(text_object_count(reopened, 0), 0, "a recognised word was offered as an object to select");

        close_document(reopened);
        free_byte_buffer(saved);
        close_document(h);
    }

    /// The saved size of the document as it stands.
    fn saved_len(handle: u64) -> usize {
        let saved = snapshot_document(handle);
        assert_eq!(saved.status, STATUS_OK_PDFIUM);
        let len = saved.len;
        free_byte_buffer(saved);
        len
    }

    #[test]
    fn a_font_goes_into_the_document_once_however_many_pages_are_recognised() {
        // ⚠️ A 359-page Hindi book once took a copy of Nirmala (1.3 MB) into
        // the document for EVERY page it recognised: memory and recovery
        // snapshots grew until the app went down about 80% of the way through.
        if !std::path::Path::new(DEVANAGARI_FONT).exists() {
            println!("{DEVANAGARI_FONT} is not on this machine");
            return;
        }
        let h = open(TWENTY_PAGES);
        assert_eq!(write(h, 0, &[("क्षमा", [0.1, 0.1, 0.3, 0.14])], Some(DEVANAGARI_FONT)), 1);
        let one_page = saved_len(h);

        for page in 1..6 {
            assert_eq!(write(h, page, &[("कीजिए", [0.1, 0.1, 0.3, 0.14])], Some(DEVANAGARI_FONT)), 1);
        }
        let six_pages = saved_len(h);

        // Five more words cost a few kilobytes. Another copy of the font is
        // hundreds of kilobytes each.
        assert!(
            six_pages - one_page < 50_000,
            "five more pages added {} bytes to the file",
            six_pages - one_page
        );
        // The sample's pages already say "Page N of 20"; the recognised word comes after.
        let last = visible_text(h, 5);
        assert!(last.ends_with("कीजिए"), "control: the last page's word must really be there, got {last}");
        close_document(h);
    }

    #[test]
    fn on_a_turned_page_the_words_land_where_the_turned_picture_shows_them() {
        for degrees in [90, 180, 270] {
            let h = open(BLANK);
            assert_eq!(rotate_page(h, 0, degrees), STATUS_OK_PDFIUM);
            let aspect = render_aspect(h, 0);
            let b = [0.10f32, 0.20, 0.45, 0.26];
            assert_eq!(write(h, 0, &[("Turned", b)], None), 1);

            let (l, t, r, bt) = drawn_box_of_the_ocr_run(h, 0, aspect);
            assert!(
                (l - b[0]).abs() < 0.01 && (r - b[2]).abs() < 0.01 && (t - b[1]).abs() < 0.01 && (bt - b[3]).abs() < 0.01,
                "{degrees} degrees: drawn at {l:.3},{t:.3}..{r:.3},{bt:.3}, asked for {b:?}"
            );
            close_document(h);
        }
    }
}
