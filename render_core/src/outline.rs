//! Writing a document outline (bookmarks).
//!
//! The one thing PDFium cannot do at all. Its public API reads bookmarks
//! (FPDFBookmark_GetTitle, _GetDest, _GetFirstChild, _GetNextSibling) but has
//! no call anywhere to CREATE one, so an outline has to be built as PDF objects
//! directly. That is what lopdf is here for, and the only thing it is here for:
//! rendering, annotations and ordinary saving all stay on PDFium.
//!
//! The file is never edited in place. The caller passes a separate destination
//! and swaps the files itself, which is the same discipline the annotation save
//! path already follows and for the same reason: writing over the file you
//! loaded from is how a document gets destroyed.

use std::collections::BTreeMap;
use std::ffi::{c_char, CStr};
use std::panic;

use lopdf::{Dictionary, Document, Object, ObjectId, StringFormat};

use crate::{STATUS_INVALID_INPUT, STATUS_OK_PDFIUM, STATUS_PANIC, STATUS_UNSUPPORTED};

/// One entry of the outline being written.
struct Entry {
    /// 0 for a top-level entry, 1 for its child. Matches what `get_bookmarks`
    /// reports, so an outline can be read out and written back unchanged.
    depth: i32,
    page_index: i32,
    title: String,
}

/// A malformed or hostile input must not be able to make us allocate for ever.
const MAX_ENTRIES: usize = 20_000;
const MAX_DEPTH: i32 = 32;

/// Replaces the document's outline.
///
/// Reads `src_path`, writes the result to `dst_path`, and leaves the source
/// untouched. An empty entry list REMOVES the outline, which is what makes
/// regenerating one idempotent.
///
/// Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT (bad arguments or malformed
/// buffer), STATUS_UNSUPPORTED (the file could not be parsed or written, which
/// includes encrypted documents), or STATUS_PANIC.
///
/// # Safety
/// `src_path` and `dst_path` must be valid NUL-terminated C strings, and `data`
/// must point to at least `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn write_outline(
    src_path: *const c_char,
    dst_path: *const c_char,
    data: *const u8,
    len: usize,
) -> i32 {
    if src_path.is_null() || dst_path.is_null() || (data.is_null() && len != 0) {
        return STATUS_INVALID_INPUT;
    }

    let src = match unsafe { CStr::from_ptr(src_path) }.to_str() {
        Ok(s) => s.to_owned(),
        Err(_) => return STATUS_INVALID_INPUT,
    };
    let dst = match unsafe { CStr::from_ptr(dst_path) }.to_str() {
        Ok(s) => s.to_owned(),
        Err(_) => return STATUS_INVALID_INPUT,
    };
    let bytes = if len == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(data, len) }.to_vec()
    };

    panic::catch_unwind(move || write_outline_inner(&src, &dst, &bytes))
        .unwrap_or(STATUS_PANIC)
}

fn write_outline_inner(src: &str, dst: &str, bytes: &[u8]) -> i32 {
    let Some(entries) = parse_entries(bytes) else {
        return STATUS_INVALID_INPUT;
    };

    let Ok(mut doc) = Document::load(src) else {
        // Encrypted, truncated, or a PDF lopdf cannot parse. PDFium is the more
        // forgiving reader of the two, so a file can render here and still fail
        // to load for editing; saying so beats writing a broken file.
        return STATUS_UNSUPPORTED;
    };

    let pages: BTreeMap<u32, ObjectId> = doc.get_pages();
    if pages.is_empty() {
        return STATUS_UNSUPPORTED;
    }

    // Whatever outline is already there goes first, otherwise regenerating
    // twice would leave the first tree in the file as unreferenced bloat that
    // grows on every run.
    remove_existing_outline(&mut doc);

    if entries.is_empty() {
        if let Ok(catalog) = doc.catalog_mut() {
            catalog.remove(b"Outlines");
        }
        return save(&mut doc, dst);
    }

    let root_id = doc.new_object_id();
    let ids: Vec<ObjectId> = (0..entries.len()).map(|_| doc.new_object_id()).collect();

    // Built in one pass. `open_parents[d]` is the entry that a new entry at
    // depth d+1 belongs under, and `last_child` remembers who to link a new
    // sibling to.
    let mut dicts: Vec<Dictionary> = entries.iter().map(|_| Dictionary::new()).collect();
    let mut first_child: BTreeMap<usize, usize> = BTreeMap::new();
    let mut last_child: BTreeMap<usize, usize> = BTreeMap::new();
    let mut child_count: BTreeMap<usize, i64> = BTreeMap::new();
    let mut root_first: Option<usize> = None;
    let mut root_last: Option<usize> = None;
    let mut root_count: i64 = 0;
    let mut open_parents: Vec<usize> = Vec::new();

    for (i, entry) in entries.iter().enumerate() {
        // The depth is trusted only as far as the tree already goes. An entry
        // claiming depth 5 when nothing above it is deeper than 1 becomes a
        // child of that level-1 entry rather than a node with no parent, which
        // is not expressible in an outline at all.
        let depth = entry.depth.clamp(0, MAX_DEPTH) as usize;
        let depth = depth.min(open_parents.len());
        open_parents.truncate(depth);

        let parent = open_parents.last().copied();

        match parent {
            Some(p) => {
                first_child.entry(p).or_insert(i);
                if let Some(&prev) = last_child.get(&p) {
                    dicts[prev].set("Next", Object::Reference(ids[i]));
                    dicts[i].set("Prev", Object::Reference(ids[prev]));
                }
                last_child.insert(p, i);
                *child_count.entry(p).or_insert(0) += 1;
                dicts[i].set("Parent", Object::Reference(ids[p]));
            }
            None => {
                if root_first.is_none() {
                    root_first = Some(i);
                }
                if let Some(prev) = root_last {
                    dicts[prev].set("Next", Object::Reference(ids[i]));
                    dicts[i].set("Prev", Object::Reference(ids[prev]));
                }
                root_last = Some(i);
                root_count += 1;
                dicts[i].set("Parent", Object::Reference(root_id));
            }
        }

        dicts[i].set("Title", pdf_text_string(&entry.title));

        // A destination is optional: an entry whose page could not be resolved
        // still belongs in the outline, it simply does not navigate. That
        // mirrors how the reader treats a page index of -1.
        if entry.page_index >= 0 {
            if let Some(&page_id) = pages.get(&((entry.page_index as u32) + 1)) {
                dicts[i].set(
                    "Dest",
                    Object::Array(vec![
                        Object::Reference(page_id),
                        Object::Name(b"Fit".to_vec()),
                    ]),
                );
            }
        }

        open_parents.push(i);
    }

    for (i, mut dict) in dicts.into_iter().enumerate() {
        if let (Some(&f), Some(&l)) = (first_child.get(&i), last_child.get(&i)) {
            dict.set("First", Object::Reference(ids[f]));
            dict.set("Last", Object::Reference(ids[l]));
            // NEGATIVE means closed, and its magnitude is how many entries
            // would appear if it were opened. A freshly generated outline opens
            // collapsed, which is the only readable state for a book with
            // hundreds of headings.
            dict.set("Count", Object::Integer(-child_count.get(&i).copied().unwrap_or(0)));
        }
        doc.objects.insert(ids[i], Object::Dictionary(dict));
    }

    let mut root = Dictionary::new();
    root.set("Type", Object::Name(b"Outlines".to_vec()));
    if let (Some(f), Some(l)) = (root_first, root_last) {
        root.set("First", Object::Reference(ids[f]));
        root.set("Last", Object::Reference(ids[l]));
    }
    // The root is always open, so its count is positive: the top-level entries
    // are the ones visible when the panel is first shown.
    root.set("Count", Object::Integer(root_count));
    doc.objects.insert(root_id, Object::Dictionary(root));

    if doc.catalog_mut().is_err() {
        return STATUS_UNSUPPORTED;
    }
    doc.catalog_mut()
        .expect("catalog checked above")
        .set("Outlines", Object::Reference(root_id));

    save(&mut doc, dst)
}

fn save(doc: &mut Document, dst: &str) -> i32 {
    match doc.save(dst) {
        Ok(_) => STATUS_OK_PDFIUM,
        Err(_) => STATUS_UNSUPPORTED,
    }
}

/// Drops the objects of an outline that is already in the document.
///
/// Bounded and cycle-safe: a malformed outline can point back at itself, and
/// this runs on files we did not write.
fn remove_existing_outline(doc: &mut Document) {
    let Some(Object::Reference(root)) = doc.catalog().ok().and_then(|c| c.get(b"Outlines").ok()).cloned()
    else {
        return;
    };

    let mut seen: Vec<ObjectId> = Vec::new();
    let mut stack: Vec<ObjectId> = vec![root];

    while let Some(id) = stack.pop() {
        if seen.contains(&id) || seen.len() >= MAX_ENTRIES {
            continue;
        }
        seen.push(id);

        if let Ok(Object::Dictionary(dict)) = doc.get_object(id) {
            for key in [b"First".as_ref(), b"Next".as_ref()] {
                if let Ok(Object::Reference(next)) = dict.get(key) {
                    stack.push(*next);
                }
            }
        }
    }

    for id in seen {
        doc.objects.remove(&id);
    }
}

/// Encodes a title as a PDF text string.
///
/// ASCII goes out as a plain literal, which keeps the file readable. Anything
/// else becomes UTF-16BE behind a byte-order mark, because PDFDocEncoding
/// cannot represent Devanagari, Burmese, Arabic or CJK at all and a title
/// written as raw bytes would come back as mojibake.
fn pdf_text_string(title: &str) -> Object {
    if title.is_ascii() {
        return Object::String(title.as_bytes().to_vec(), StringFormat::Literal);
    }

    let mut bytes = vec![0xFE, 0xFF];
    for unit in title.encode_utf16() {
        bytes.extend_from_slice(&unit.to_be_bytes());
    }
    Object::String(bytes, StringFormat::Hexadecimal)
}

/// Parses the same layout `get_bookmarks` produces: a count, then per entry a
/// depth, a page index, and a UTF-8 title. Returns None on a truncated or
/// self-contradictory buffer rather than reading past the end.
fn parse_entries(bytes: &[u8]) -> Option<Vec<Entry>> {
    if bytes.is_empty() {
        return Some(Vec::new());
    }
    if bytes.len() < 4 {
        return None;
    }

    let count = u32::from_le_bytes(bytes[0..4].try_into().ok()?) as usize;
    if count > MAX_ENTRIES {
        return None;
    }

    let mut entries = Vec::with_capacity(count);
    let mut p = 4usize;

    for _ in 0..count {
        if p + 12 > bytes.len() {
            return None;
        }
        let depth = i32::from_le_bytes(bytes[p..p + 4].try_into().ok()?);
        let page_index = i32::from_le_bytes(bytes[p + 4..p + 8].try_into().ok()?);
        let title_len = u32::from_le_bytes(bytes[p + 8..p + 12].try_into().ok()?) as usize;
        p += 12;

        if p + title_len > bytes.len() {
            return None;
        }
        let title = String::from_utf8(bytes[p..p + title_len].to_vec()).ok()?;
        p += title_len;

        entries.push(Entry { depth, page_index, title });
    }

    Some(entries)
}
