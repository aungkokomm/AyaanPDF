//! Document properties: the Info dictionary, the XMP packet that repeats it,
//! and the fonts a file carries.
//!
//! PDFium reads the eight Info fields (FPDF_GetMetaText) and writes none of
//! them, so writing goes through lopdf, as the outline does. Two things make it
//! more than setting a dictionary:
//!
//! * Most real files keep the same facts TWICE, in /Info and in an XMP packet
//!   hung off the catalog (/Root /Metadata). 17 of the 25 real-world files on
//!   the test drive did. Acrobat and Windows search can prefer the XMP, so a
//!   title changed only in /Info can go on showing the old one. Every field
//!   written here is written to both, and ONLY the fields written: a property
//!   this write does not own is left exactly as the file had it. Mirroring an
//!   absent Info title into the XMP would delete a title that only the XMP had.
//! * The write is APPENDED as an incremental update rather than rewriting the
//!   file. The file is parsed to find the objects, but only the changed Info
//!   and metadata objects go out, after the existing bytes, so a 200 MB book
//!   gains a few hundred bytes instead of being written out again.
//!
//! Reading comes from PDFium on the OPEN document, which answers from the
//! trailer and catalog without loading a page, so the dialog opens at once
//! whatever the size of the book.

use std::collections::BTreeSet;
use std::ffi::{c_char, CStr};
use std::panic;

use lopdf::{Dictionary, Document, IncrementalDocument, Object, Stream, StringFormat};

use crate::outline::pdf_text_string;
use crate::{
    ByteBuffer, STATUS_INVALID_INPUT, STATUS_OK_PDFIUM, STATUS_PANIC, STATUS_UNSUPPORTED,
};

/// What a document says about itself, as NUL-separated UTF-8 key/value pairs:
/// the eight Info fields that are present (dates as the file's own "D:..."
/// strings), and Version, Pages, PageWidth and PageHeight (points, first page),
/// Tagged (0 or 1), Security (the handler revision, -1 for none) and
/// Permissions (the /P bits).
///
/// Every answer comes from the trailer, the catalog or the page tree, so
/// nothing here loads a page. Invalid handle -> STATUS_INVALID_INPUT.
#[unsafe(no_mangle)]
pub extern "C" fn get_document_properties(doc_handle: u64) -> ByteBuffer {
    if doc_handle == 0 {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    panic::catch_unwind(|| get_document_properties_inner(doc_handle))
        .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

fn get_document_properties_inner(doc_handle: u64) -> ByteBuffer {
    let _guard = crate::call_guard();
    let doc = crate::lock(&crate::core().documents).get(&doc_handle).cloned();
    let Some(doc) = doc else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };
    let doc_guard = crate::lock(&doc);

    let mut pairs: Vec<(&str, String)> = Vec::new();
    let bindings = doc_guard.bindings();
    let handle = doc_guard.handle();

    // Asked of PDFium directly rather than through pdfium-render's metadata(),
    // which requests "ModificationDate" where the Info key is "ModDate" and so
    // never finds a modified date in any file.
    for key in ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"] {
        let length = bindings.FPDF_GetMetaText(handle, key, std::ptr::null_mut(), 0);
        if length <= 2 {
            continue;
        }
        let mut buffer = vec![0u8; length as usize];
        let written = bindings.FPDF_GetMetaText(handle, key, buffer.as_mut_ptr() as *mut std::ffi::c_void, length);
        let units: Vec<u16> = buffer[..(written.min(length) as usize)]
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .take_while(|u| *u != 0)
            .collect();
        let text = String::from_utf16_lossy(&units);
        if !text.is_empty() {
            pairs.push((key, text));
        }
    }

    let mut version: std::os::raw::c_int = 0;
    if bindings.FPDF_GetFileVersion(handle, &mut version) != 0 && version > 0 {
        pairs.push(("Version", format!("{}.{}", version / 10, version % 10)));
    }

    let pages = doc_guard.pages();
    pairs.push(("Pages", pages.len().to_string()));
    if pages.len() > 0 {
        if let Ok(size) = pages.page_size(0) {
            pairs.push(("PageWidth", format!("{:.2}", size.width().value)));
            pairs.push(("PageHeight", format!("{:.2}", size.height().value)));
        }
    }

    let tagged = bindings.FPDFCatalog_IsTagged(handle) != 0;
    pairs.push(("Tagged", if tagged { "1" } else { "0" }.to_owned()));
    pairs.push(("Security", bindings.FPDF_GetSecurityHandlerRevision(handle).to_string()));
    pairs.push(("Permissions", (bindings.FPDF_GetDocPermissions(handle) as u32).to_string()));

    buffer_of(&pairs)
}

/// Appends the given properties to the file at `path`, in place.
///
/// `data` is NUL-separated UTF-8 key/value pairs:
///
/// * Title, Author, Subject, Keywords: set; an EMPTY value removes the field.
/// * Producer: set.
/// * ModDate: the "D:..." string for /Info; XmpDate: the same moment in the
///   ISO form XMP uses, written to xmp:ModifyDate and xmp:MetadataDate.
/// * CreatorIfPdfium: replaces a /Creator that says "PDFium". PDFium names
///   itself as the creator of every document it creates, and in this app every
///   such document was created by Ayaan PDF.
///
/// The caller passes the file PDFium has just written, which nothing has open.
/// It is read whole, the update is written beside it and swapped in, so a
/// failure part way leaves the saved file as it was.
///
/// Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT, STATUS_UNSUPPORTED (a file
/// lopdf cannot parse, or an encrypted one), or STATUS_PANIC.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string, and `data` must point to at
/// least `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn write_document_info(path: *const c_char, data: *const u8, len: usize) -> i32 {
    if path.is_null() || (data.is_null() && len != 0) {
        return STATUS_INVALID_INPUT;
    }
    let Ok(path) = unsafe { CStr::from_ptr(path) }.to_str().map(str::to_owned) else {
        return STATUS_INVALID_INPUT;
    };
    let bytes = if len == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(data, len) }.to_vec()
    };

    panic::catch_unwind(move || write_document_info_inner(&path, &bytes)).unwrap_or(STATUS_PANIC)
}

fn write_document_info_inner(path: &str, data: &[u8]) -> i32 {
    let Some(pairs) = parse_pairs(data) else {
        return STATUS_INVALID_INPUT;
    };
    let Ok(file) = std::fs::read(path) else {
        return STATUS_INVALID_INPUT;
    };

    let Some(out) = with_document_info(file, &pairs) else {
        return STATUS_UNSUPPORTED;
    };

    let temp = format!("{path}.ayaan-info");
    if std::fs::write(&temp, &out).is_err() {
        let _ = std::fs::remove_file(&temp);
        return STATUS_UNSUPPORTED;
    }
    if std::fs::rename(&temp, path).is_err() {
        let _ = std::fs::remove_file(&temp);
        return STATUS_UNSUPPORTED;
    }
    STATUS_OK_PDFIUM
}

/// The file's bytes with the properties appended, or None when lopdf cannot
/// parse it or it is encrypted.
pub(crate) fn with_document_info(file: Vec<u8>, pairs: &[(String, String)]) -> Option<Vec<u8>> {
    if value(pairs, REMOVE_PERSONAL) == Some("1") {
        return without_personal_info(file, pairs);
    }

    let prev = Document::load_mem(&file).ok()?;

    // Info strings in an encrypted file are encrypted too, and lopdf will only
    // append to one it could decrypt. Refused outright rather than half done.
    if prev.is_encrypted() || prev.encryption_state.is_some() {
        return None;
    }

    // Found before the incremental document takes ownership of the parse.
    let info_ref = match prev.trailer.get(b"Info") {
        Ok(Object::Reference(id)) => Some(*id),
        _ => None,
    };
    let inline_info = match prev.trailer.get(b"Info") {
        Ok(Object::Dictionary(d)) => Some(d.clone()),
        _ => None,
    };
    let metadata = metadata_stream(&prev);

    let mut inc = IncrementalDocument::create_from(file, prev);

    // An xref stream section's own keys, and a hybrid file's pointer to its
    // stream, belong to the section they came from. Carried into the new
    // trailer they would describe this update wrongly.
    for key in [b"XRefStm".as_slice(), b"Index", b"W", b"Filter", b"DecodeParms", b"Length", b"Type"] {
        inc.new_document.trailer.remove(key);
    }

    let info_id = match info_ref {
        Some(id) => {
            inc.opt_clone_object_to_new_document(id).ok()?;
            id
        }
        None => {
            let id = inc.new_document.add_object(inline_info.unwrap_or_default());
            inc.new_document.trailer.set("Info", Object::Reference(id));
            id
        }
    };

    {
        let info = inc.new_document.get_object_mut(info_id).ok()?.as_dict_mut().ok()?;
        apply_to_info(info, pairs);
    }

    if let Some((id, stream)) = metadata {
        if let Some(edited) = edited_metadata(&stream, pairs) {
            inc.new_document.objects.insert(id, Object::Stream(edited));
        }
    }

    let mut out = Vec::new();
    inc.save_to(&mut out).ok()?;
    Some(out)
}

/// The value given for `key`, if the caller named it.
fn value<'a>(pairs: &'a [(String, String)], key: &str) -> Option<&'a str> {
    pairs.iter().find(|(k, _)| k == key).map(|(_, v)| v.as_str())
}

fn apply_to_info(info: &mut Dictionary, pairs: &[(String, String)]) {
    let get = |key: &str| value(pairs, key);

    // First, so a field the caller also sets below is set on what is left.
    // Everything but what the document is and when it was made goes: the
    // author, the program (which often names the source file), and custom
    // keys, where Acrobat's PDFMaker puts the company and Word's own
    // properties.
    if get(REMOVE_PERSONAL) == Some("1") {
        let doomed: Vec<Vec<u8>> = info
            .iter()
            .map(|(key, _)| key.clone())
            .filter(|key| !INFO_KEPT_WHEN_REMOVING.contains(&key.as_slice()))
            .collect();
        for key in doomed {
            info.remove(&key);
        }
    }

    for key in ["Title", "Author", "Subject", "Keywords"] {
        match get(key) {
            Some("") => {
                info.remove(key.as_bytes());
            }
            Some(value) => info.set(key, pdf_text_string(value)),
            None => {}
        }
    }
    if let Some(producer) = get("Producer") {
        info.set("Producer", pdf_text_string(producer));
    }
    if let Some(date) = get("ModDate") {
        info.set("ModDate", Object::String(date.as_bytes().to_vec(), StringFormat::Literal));
    }
    if let Some(creator) = get("CreatorIfPdfium") {
        let says_pdfium = info
            .get(b"Creator")
            .ok()
            .and_then(|o| lopdf::decode_text_string(o).ok())
            .is_some_and(|c| c.trim() == "PDFium");
        if says_pdfium {
            info.set("Creator", pdf_text_string(creator));
        }
    }
}

/// The catalog's XMP stream, when it has one held by reference.
fn metadata_stream(doc: &Document) -> Option<(lopdf::ObjectId, Stream)> {
    let root = doc.trailer.get(b"Root").ok()?.as_reference().ok()?;
    let catalog = doc.get_object(root).ok()?.as_dict().ok()?;
    let id = catalog.get(b"Metadata").ok()?.as_reference().ok()?;
    let stream = doc.get_object(id).ok()?.as_stream().ok()?.clone();
    Some((id, stream))
}

/// The XMP stream with this write's fields replaced, uncompressed, or None
/// when it has none of them to change or is not XMP this can edit safely.
fn edited_metadata(stream: &Stream, pairs: &[(String, String)]) -> Option<Stream> {
    let get = |key: &str| value(pairs, key);
    let content = if stream.dict.has(b"Filter") {
        stream.decompressed_content().ok()?
    } else {
        stream.content.clone()
    };
    let xml = String::from_utf8(content).ok()?;

    let mut changes: Vec<(XmpProperty, &str)> = Vec::new();
    for (key, property) in [
        ("Title", XmpProperty::Title),
        ("Author", XmpProperty::Creator),
        ("Subject", XmpProperty::Description),
        ("Keywords", XmpProperty::Keywords),
        ("Producer", XmpProperty::Producer),
    ] {
        if let Some(value) = get(key) {
            changes.push((property, value));
        }
    }
    if let Some(date) = get("XmpDate") {
        changes.push((XmpProperty::ModifyDate, date));
        changes.push((XmpProperty::MetadataDate, date));
    }
    let removals = if get(REMOVE_PERSONAL) == Some("1") { personal_xmp_names(&xml) } else { Vec::new() };
    if changes.is_empty() && removals.is_empty() {
        return None;
    }

    let edited = edit_xmp(&xml, &changes, &removals)?;

    // Written plain: XMP is meant to be readable by tools that do not parse
    // PDF, which is why the specification recommends leaving it uncompressed.
    let mut dict = stream.dict.clone();
    dict.remove(b"Filter");
    dict.remove(b"DecodeParms");
    Some(Stream::new(dict, edited.into_bytes()))
}

/// The XMP properties that mirror an Info field.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum XmpProperty {
    /// /Title. An rdf:Alt with an x-default entry.
    Title,
    /// /Author. An rdf:Seq, because XMP holds a list of creators.
    Creator,
    /// /Subject. An rdf:Alt.
    Description,
    Keywords,
    Producer,
    ModifyDate,
    MetadataDate,
}

impl XmpProperty {
    fn qname(self) -> &'static str {
        match self {
            XmpProperty::Title => "dc:title",
            XmpProperty::Creator => "dc:creator",
            XmpProperty::Description => "dc:description",
            XmpProperty::Keywords => "pdf:Keywords",
            XmpProperty::Producer => "pdf:Producer",
            XmpProperty::ModifyDate => "xmp:ModifyDate",
            XmpProperty::MetadataDate => "xmp:MetadataDate",
        }
    }

    fn element(self, value: &str) -> String {
        let v = xml_escape(value);
        let q = self.qname();
        match self {
            XmpProperty::Title | XmpProperty::Description => {
                format!("<{q}><rdf:Alt><rdf:li xml:lang=\"x-default\">{v}</rdf:li></rdf:Alt></{q}>")
            }
            XmpProperty::Creator => format!("<{q}><rdf:Seq><rdf:li>{v}</rdf:li></rdf:Seq></{q}>"),
            _ => format!("<{q}>{v}</{q}>"),
        }
    }
}

/// The packet with each changed property removed wherever it was, in element
/// or attribute form, and the new values added in one rdf:Description of their
/// own. An empty value removes the property and adds nothing.
///
/// A fresh description rather than editing each property where it stood: XMP
/// allows any number of descriptions about the same resource, and writers put
/// these properties in every shape XMP permits (an element, an attribute on
/// the description, a self-closing empty element). Removing is the one
/// operation that is the same for all of them.
///
/// None when the packet has no rdf:RDF to add to, in which case the XMP is
/// left alone rather than guessed at.
pub(crate) fn edit_xmp(xml: &str, changes: &[(XmpProperty, &str)], removals: &[String]) -> Option<String> {
    xml.rfind("</rdf:RDF>")?;
    let mut out = xml.to_owned();

    for (property, _) in changes {
        remove_property(&mut out, property.qname());
    }
    for name in removals {
        remove_property(&mut out, name);
    }

    let added: Vec<String> = changes
        .iter()
        .filter(|(_, value)| !value.is_empty())
        .map(|(property, value)| property.element(value))
        .collect();

    if !added.is_empty() {
        let description = format!(
            "<rdf:Description rdf:about=\"\" \
             xmlns:dc=\"http://purl.org/dc/elements/1.1/\" \
             xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" \
             xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">{}</rdf:Description>\n",
            added.join("")
        );
        let at = out.rfind("</rdf:RDF>")?;
        out.insert_str(at, &description);
    }

    Some(out)
}

/// Removes every element and every attribute named `qname`.
fn remove_property(xml: &mut String, qname: &str) {
    // Element form: <q ...>...</q> or <q .../>.
    let open = format!("<{qname}");
    let close = format!("</{qname}>");
    let mut from = 0;
    while let Some(found) = xml[from..].find(&open) {
        let start = from + found;
        let after = start + open.len();
        let next = xml[after..].chars().next();
        if !matches!(next, Some(c) if c.is_whitespace() || c == '>' || c == '/') {
            from = after;
            continue;
        }
        let Some(tag_end) = xml[start..].find('>').map(|i| start + i) else {
            return;
        };
        let end = if xml[..tag_end].ends_with('/') {
            tag_end + 1
        } else {
            match xml[tag_end..].find(&close) {
                Some(i) => tag_end + i + close.len(),
                None => return,
            }
        };
        xml.replace_range(start..end, "");
        from = start;
    }

    // Attribute form: whitespace, q, optional whitespace, '=', a quoted value,
    // and only inside a tag (the last '<' before it is later than the last '>').
    let mut from = 0;
    while let Some(found) = xml[from..].find(qname) {
        let at = from + found;
        from = at + qname.len();

        let before = xml[..at].chars().next_back();
        if !matches!(before, Some(c) if c.is_whitespace()) {
            continue;
        }
        let in_tag = match (xml[..at].rfind('<'), xml[..at].rfind('>')) {
            (Some(lt), Some(gt)) => lt > gt,
            (Some(_), None) => true,
            _ => false,
        };
        if !in_tag {
            continue;
        }

        let rest = &xml[from..];
        let trimmed = rest.trim_start();
        let Some(after_eq) = trimmed.strip_prefix('=') else {
            continue;
        };
        let value = after_eq.trim_start();
        let Some(quote) = value.chars().next().filter(|c| *c == '"' || *c == '\'') else {
            continue;
        };
        let value_start = xml.len() - value.len() + 1;
        let Some(value_len) = xml[value_start..].find(quote) else {
            return;
        };

        // From the whitespace before the name through the closing quote.
        let ws_start = xml[..at].trim_end().len();
        xml.replace_range(ws_start..value_start + value_len + 1, "");
        from = ws_start;
    }
}

// ---------------------------------------------------------------------------
// Removing personal info
// ---------------------------------------------------------------------------

/// The pair that asks `write_document_info` to remove personal info.
const REMOVE_PERSONAL: &str = "RemovePersonal";

/// Info keys that say what the document is and when it was made. Every other
/// key goes when personal info is removed. Producer is kept because every save
/// sets it to Ayaan PDF anyway.
const INFO_KEPT_WHEN_REMOVING: &[&[u8]] =
    &[b"Title", b"Subject", b"Keywords", b"CreationDate", b"ModDate", b"Producer", b"Trapped"];

/// XMP properties that name a person, a program, or the files and steps a
/// document came from. Custom properties (pdfx:*) go as well, found per file.
const PERSONAL_XMP: &[&str] = &[
    "dc:creator",
    "xmp:CreatorTool",
    "xmpMM:History",
    "xmpMM:DerivedFrom",
    "xmpMM:Ingredients",
    "xmpMM:Manifest",
    "xmpMM:Pantry",
    "photoshop:AuthorsPosition",
    "photoshop:CaptionWriter",
];

/// Annotation types whose /T is the name of the person who made them. A
/// widget's /T is its FORM FIELD's name and must never be touched, and a link
/// or popup has no author.
const COMMENT_SUBTYPES: &[&[u8]] = &[
    b"Text", b"FreeText", b"Line", b"Square", b"Circle", b"Polygon", b"PolyLine", b"Highlight",
    b"Underline", b"Squiggly", b"StrikeOut", b"Stamp", b"Caret", b"Ink", b"FileAttachment",
    b"Sound", b"Redact",
];

fn personal_xmp_names(xml: &str) -> Vec<String> {
    let mut names: Vec<String> = PERSONAL_XMP.iter().map(|s| (*s).to_owned()).collect();
    names.extend(names_with_prefix(xml, "pdfx:"));
    names
}

/// The file rewritten WHOLE without its personal info.
///
/// Not appended like an ordinary save's properties: an incremental update
/// leaves the earlier revision's bytes in the file, author and all, where
/// anyone with a text editor can read them. A full rewrite keeps only the
/// objects the document still uses, which also drops every earlier saved
/// version the file was carrying.
fn without_personal_info(file: Vec<u8>, pairs: &[(String, String)]) -> Option<Vec<u8>> {
    let mut doc = Document::load_mem(&file).ok()?;
    if doc.is_encrypted() || doc.encryption_state.is_some() {
        return None;
    }

    let info_ref = match doc.trailer.get(b"Info") {
        Ok(Object::Reference(id)) => Some(*id),
        _ => None,
    };
    match info_ref {
        Some(id) => apply_to_info(doc.get_object_mut(id).ok()?.as_dict_mut().ok()?, pairs),
        None => {
            let mut info = match doc.trailer.get(b"Info") {
                Ok(Object::Dictionary(d)) => d.clone(),
                _ => Dictionary::new(),
            };
            apply_to_info(&mut info, pairs);
            let id = doc.add_object(info);
            doc.trailer.set("Info", Object::Reference(id));
        }
    }

    if let Some((id, stream)) = metadata_stream(&doc) {
        if let Some(edited) = edited_metadata(&stream, pairs) {
            doc.objects.insert(id, Object::Stream(edited));
        }
    }

    strip_personal_objects(&mut doc);

    // The old Info and XMP, and anything else nothing refers to any more.
    doc.prune_objects();

    let mut out = Vec::new();
    doc.save_to(&mut out).ok()?;
    Some(out)
}

/// Comment authors, and the XMP hung off pages and pictures (Photoshop and
/// cameras write the author and the original file's path into an image's own
/// metadata). The catalog's XMP is edited, not dropped, by the caller.
fn strip_personal_objects(doc: &mut Document) {
    let root = doc.trailer.get(b"Root").and_then(Object::as_reference).ok();
    for (id, object) in doc.objects.iter_mut() {
        let dict = match object {
            Object::Dictionary(d) => d,
            Object::Stream(s) => &mut s.dict,
            _ => continue,
        };
        if Some(*id) != root {
            dict.remove(b"Metadata");
        }
        if is_comment(dict) {
            dict.remove(b"T");
        }
    }
}

fn is_comment(dict: &Dictionary) -> bool {
    let subtype = dict.get(b"Subtype").and_then(Object::as_name).ok();
    subtype.is_some_and(|s| COMMENT_SUBTYPES.contains(&s))
        && !dict.has(b"FT")
        && matches!(dict.get(b"T"), Ok(Object::String(..)))
}

/// What personal info the PDF at `path` carries, as NUL-separated UTF-8 pairs.
/// A key can repeat. Keys: Author, Creator, Custom ("Name: value"),
/// XmpAuthor, XmpTool, History (steps), FilePath, CommentAuthor, Comments
/// (count), ObjectMetadata (count). Parses the whole file, so off the UI
/// thread. An encrypted file is STATUS_UNSUPPORTED.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn find_personal_info(path: *const c_char) -> ByteBuffer {
    if path.is_null() {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    let Ok(path) = unsafe { CStr::from_ptr(path) }.to_str().map(str::to_owned) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    panic::catch_unwind(move || {
        let Ok(doc) = Document::load(&path) else {
            return ByteBuffer::err(STATUS_UNSUPPORTED);
        };
        if doc.is_encrypted() || doc.encryption_state.is_some() {
            return ByteBuffer::err(STATUS_UNSUPPORTED);
        }
        let found = personal_info_of(&doc);
        bytes_buffer(join_nul(found.iter().flat_map(|(k, v)| [*k, v.as_str()])))
    })
    .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

pub(crate) fn personal_info_of(doc: &Document) -> Vec<(&'static str, String)> {
    let mut found: Vec<(&'static str, String)> = Vec::new();

    let info = match doc.trailer.get(b"Info") {
        Ok(Object::Reference(id)) => doc.get_object(*id).ok().and_then(|o| o.as_dict().ok()),
        Ok(Object::Dictionary(d)) => Some(d),
        _ => None,
    };
    if let Some(info) = info {
        for (key, value) in info.iter() {
            let text = object_text(doc, value);
            if text.trim().is_empty() {
                continue;
            }
            match key.as_slice() {
                b"Author" => found.push(("Author", text)),
                b"Creator" => found.push(("Creator", text)),
                k if INFO_KEPT_WHEN_REMOVING.contains(&k) => {}
                k => found.push(("Custom", format!("{}: {}", String::from_utf8_lossy(k), text))),
            }
        }
    }

    if let Some((_, stream)) = metadata_stream(doc) {
        let content = if stream.dict.has(b"Filter") {
            stream.decompressed_content().ok()
        } else {
            Some(stream.content.clone())
        };
        if let Some(xml) = content.and_then(|c| String::from_utf8(c).ok()) {
            for author in xmp_values(&xml, "dc:creator") {
                found.push(("XmpAuthor", author));
            }
            for tool in xmp_values(&xml, "xmp:CreatorTool") {
                found.push(("XmpTool", tool));
            }
            let steps = element_inner(&xml, "xmpMM:History").map(|h| h.matches("<rdf:li").count()).unwrap_or(0);
            if steps > 0 {
                found.push(("History", steps.to_string()));
            }
            let mut paths = BTreeSet::new();
            for path in xmp_values(&xml, "stRef:filePath") {
                if paths.insert(path.clone()) {
                    found.push(("FilePath", path));
                }
            }
            for name in names_with_prefix(&xml, "pdfx:") {
                for value in xmp_values(&xml, &name) {
                    found.push(("Custom", format!("{}: {}", &name["pdfx:".len()..], value)));
                }
            }
        }
    }

    let root = doc.trailer.get(b"Root").and_then(Object::as_reference).ok();
    let mut authors = BTreeSet::new();
    let mut comments = 0usize;
    let mut object_metadata = 0usize;
    for (id, object) in &doc.objects {
        let dict = match object {
            Object::Dictionary(d) => d,
            Object::Stream(s) => &s.dict,
            _ => continue,
        };
        if Some(*id) != root && dict.has(b"Metadata") {
            object_metadata += 1;
        }
        if is_comment(dict) {
            comments += 1;
            let name = dict.get(b"T").map(|t| object_text(doc, t)).unwrap_or_default();
            if !name.trim().is_empty() {
                authors.insert(name.trim().to_owned());
            }
        }
    }
    for author in authors {
        found.push(("CommentAuthor", author));
    }
    if comments > 0 {
        found.push(("Comments", comments.to_string()));
    }
    if object_metadata > 0 {
        found.push(("ObjectMetadata", object_metadata.to_string()));
    }

    found
}

/// An Info value as text, following a reference.
fn object_text(doc: &Document, value: &Object) -> String {
    match value {
        Object::Reference(id) => doc.get_object(*id).map(|o| object_text(doc, o)).unwrap_or_default(),
        Object::String(..) => lopdf::decode_text_string(value).unwrap_or_default(),
        Object::Name(n) => String::from_utf8_lossy(n).into_owned(),
        Object::Integer(i) => i.to_string(),
        Object::Real(r) => r.to_string(),
        Object::Boolean(b) => b.to_string(),
        _ => String::new(),
    }
}

/// Every name in the packet starting with `prefix`, as an element or attribute.
fn names_with_prefix(xml: &str, prefix: &str) -> BTreeSet<String> {
    let mut names = BTreeSet::new();
    let mut from = 0;
    while let Some(found) = xml[from..].find(prefix) {
        let at = from + found;
        from = at + prefix.len();
        let before = xml[..at].chars().next_back();
        if !matches!(before, Some(c) if c == '<' || c.is_whitespace()) {
            continue;
        }
        let end = xml[at..]
            .find(|c: char| c.is_whitespace() || c == '=' || c == '>' || c == '/')
            .map(|i| at + i)
            .unwrap_or(xml.len());
        if end > at + prefix.len() {
            names.insert(xml[at..end].to_owned());
        }
    }
    names
}

/// The text inside the first `<qname ...>...</qname>`, if there is one.
fn element_inner<'a>(xml: &'a str, qname: &str) -> Option<&'a str> {
    let open = format!("<{qname}");
    let close = format!("</{qname}>");
    let mut from = 0;
    while let Some(found) = xml[from..].find(&open) {
        let start = from + found;
        let after = start + open.len();
        from = after;
        if !matches!(xml[after..].chars().next(), Some(c) if c.is_whitespace() || c == '>') {
            continue;
        }
        let tag_end = start + xml[start..].find('>')?;
        if xml[..tag_end].ends_with('/') {
            continue;
        }
        let end = tag_end + xml[tag_end..].find(&close)?;
        return Some(&xml[tag_end + 1..end]);
    }
    None
}

/// The values of every `qname`: an element's text, each rdf:li of an array
/// inside it, or an attribute's value.
fn xmp_values(xml: &str, qname: &str) -> Vec<String> {
    let mut out = Vec::new();

    let open = format!("<{qname}");
    let close = format!("</{qname}>");
    let mut from = 0;
    while let Some(found) = xml[from..].find(&open) {
        let start = from + found;
        let after = start + open.len();
        from = after;
        if !matches!(xml[after..].chars().next(), Some(c) if c.is_whitespace() || c == '>') {
            continue;
        }
        let Some(tag_end) = xml[start..].find('>').map(|i| start + i) else { break };
        if xml[..tag_end].ends_with('/') {
            continue;
        }
        let Some(end) = xml[tag_end..].find(&close).map(|i| tag_end + i) else { break };
        let inner = &xml[tag_end + 1..end];
        if inner.contains("<rdf:li") {
            out.extend(li_values(inner));
        } else if !inner.contains('<') && !inner.trim().is_empty() {
            out.push(xml_unescape(inner.trim()));
        }
        from = end;
    }

    let mut from = 0;
    while let Some(found) = xml[from..].find(qname) {
        let at = from + found;
        from = at + qname.len();
        if !matches!(xml[..at].chars().next_back(), Some(c) if c.is_whitespace()) {
            continue;
        }
        let Some(rest) = xml[from..].trim_start().strip_prefix('=') else { continue };
        let rest = rest.trim_start();
        let Some(quote) = rest.chars().next().filter(|c| *c == '"' || *c == '\'') else { continue };
        if let Some(len) = rest[1..].find(quote) {
            let value = rest[1..1 + len].trim();
            if !value.is_empty() {
                out.push(xml_unescape(value));
            }
        }
    }

    out
}

fn li_values(block: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut from = 0;
    while let Some(found) = block[from..].find("<rdf:li") {
        let start = from + found;
        let Some(tag_end) = block[start..].find('>').map(|i| start + i) else { break };
        from = tag_end;
        if block[..tag_end].ends_with('/') {
            continue;
        }
        let Some(end) = block[tag_end..].find("</rdf:li>").map(|i| tag_end + i) else { break };
        let inner = block[tag_end + 1..end].trim();
        if !inner.is_empty() && !inner.contains('<') {
            out.push(xml_unescape(inner));
        }
        from = end;
    }
    out
}

fn xml_unescape(value: &str) -> String {
    value
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
        .replace("&amp;", "&")
}

fn xml_escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&apos;"),
            _ => out.push(c),
        }
    }
    out
}

/// The fonts the file at `path` uses, as NUL-separated UTF-8 fields, four per
/// font: name (subset prefix removed), type, embedded (0 or 1) and subset
/// (0 or 1). Sorted by name, one row per distinct name and state.
///
/// From the file rather than the open document because it has to see every
/// font dictionary, and PDFium only reaches a font through a LOADED page: on a
/// 3,000-page book that is every page loaded to answer one question. lopdf
/// reaches them all in one parse instead. Asked for on demand, off the UI
/// thread.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn list_document_fonts(path: *const c_char) -> ByteBuffer {
    if path.is_null() {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    }
    let Ok(path) = unsafe { CStr::from_ptr(path) }.to_str().map(str::to_owned) else {
        return ByteBuffer::err(STATUS_INVALID_INPUT);
    };

    panic::catch_unwind(move || {
        let Ok(doc) = Document::load(&path) else {
            return ByteBuffer::err(STATUS_UNSUPPORTED);
        };
        let mut fields: Vec<String> = Vec::new();
        for font in fonts_of(&doc) {
            fields.push(font.name);
            fields.push(font.kind);
            fields.push(if font.embedded { "1" } else { "0" }.to_owned());
            fields.push(if font.subset { "1" } else { "0" }.to_owned());
        }
        bytes_buffer(join_nul(fields.iter().map(String::as_str)))
    })
    .unwrap_or_else(|_| ByteBuffer::err(STATUS_PANIC))
}

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct FontEntry {
    pub name: String,
    pub kind: String,
    pub embedded: bool,
    pub subset: bool,
}

pub(crate) fn fonts_of(doc: &Document) -> Vec<FontEntry> {
    let mut found = BTreeSet::new();

    for object in doc.objects.values() {
        let Ok(dict) = object.as_dict() else {
            continue;
        };
        if dict.get(b"Type").and_then(Object::as_name).ok() != Some(b"Font".as_slice()) {
            continue;
        }
        let kind = dict
            .get(b"Subtype")
            .and_then(Object::as_name)
            .map(|n| String::from_utf8_lossy(n).into_owned())
            .unwrap_or_default();

        // A CID font is the descendant of a Type0 and is reported through it,
        // or every composite font would be listed twice.
        if kind.starts_with("CIDFontType") {
            continue;
        }

        let raw = dict
            .get(b"BaseFont")
            .and_then(Object::as_name)
            .map(|n| String::from_utf8_lossy(n).into_owned())
            .unwrap_or_else(|_| "(unnamed)".to_owned());
        let (name, subset) = strip_subset_prefix(&raw);

        let embedded = match kind.as_str() {
            // A Type 3 font's glyphs are content streams in the font itself.
            "Type3" => true,
            "Type0" => descendant(doc, dict).is_some_and(|d| has_font_file(doc, d)),
            _ => has_font_file(doc, dict),
        };

        found.insert(FontEntry { name, kind: pretty_kind(&kind).to_owned(), embedded, subset });
    }

    found.into_iter().collect()
}

fn resolve<'a>(doc: &'a Document, object: &'a Object) -> Option<&'a Object> {
    match object {
        Object::Reference(id) => doc.get_object(*id).ok(),
        other => Some(other),
    }
}

fn descendant<'a>(doc: &'a Document, font: &'a Dictionary) -> Option<&'a Dictionary> {
    let array = resolve(doc, font.get(b"DescendantFonts").ok()?)?.as_array().ok()?;
    resolve(doc, array.first()?)?.as_dict().ok()
}

fn has_font_file(doc: &Document, font: &Dictionary) -> bool {
    let Some(descriptor) = font
        .get(b"FontDescriptor")
        .ok()
        .and_then(|d| resolve(doc, d))
        .and_then(|d| d.as_dict().ok())
    else {
        return false;
    };
    [b"FontFile".as_slice(), b"FontFile2", b"FontFile3"].iter().any(|k| descriptor.has(k))
}

/// "ABCDEF+Nirmala" is a subset of Nirmala: six capitals and a plus.
fn strip_subset_prefix(name: &str) -> (String, bool) {
    let bytes = name.as_bytes();
    if bytes.len() > 7 && bytes[6] == b'+' && bytes[..6].iter().all(u8::is_ascii_uppercase) {
        (name[7..].to_owned(), true)
    } else {
        (name.to_owned(), false)
    }
}

fn pretty_kind(subtype: &str) -> &str {
    match subtype {
        "TrueType" => "TrueType",
        "Type1" => "Type 1",
        "MMType1" => "Type 1 (multiple master)",
        "Type3" => "Type 3",
        "Type0" => "Composite",
        other => other,
    }
}

// ---------------------------------------------------------------------------
// The NUL-separated pairs both directions use.
// ---------------------------------------------------------------------------

fn parse_pairs(data: &[u8]) -> Option<Vec<(String, String)>> {
    if data.is_empty() {
        return Some(Vec::new());
    }
    let text = std::str::from_utf8(data).ok()?;
    let fields: Vec<&str> = text.strip_suffix('\0').unwrap_or(text).split('\0').collect();
    if fields.len() % 2 != 0 {
        return None;
    }
    Some(fields.chunks(2).map(|kv| (kv[0].to_owned(), kv[1].to_owned())).collect())
}

fn join_nul<'a>(fields: impl Iterator<Item = &'a str>) -> Vec<u8> {
    let mut out = Vec::new();
    for field in fields {
        // A NUL inside a value would shift every field after it by one.
        out.extend(field.bytes().filter(|b| *b != 0));
        out.push(0);
    }
    out
}

fn buffer_of(pairs: &[(&str, String)]) -> ByteBuffer {
    bytes_buffer(join_nul(pairs.iter().flat_map(|(k, v)| [*k, v.as_str()])))
}

fn bytes_buffer(out: Vec<u8>) -> ByteBuffer {
    let mut boxed = out.into_boxed_slice();
    let buffer = ByteBuffer { data: boxed.as_mut_ptr(), len: boxed.len(), status: STATUS_OK_PDFIUM };
    std::mem::forget(boxed);
    buffer
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::ffi::CString;

    fn fixture(name: &str) -> Vec<u8> {
        std::fs::read(format!("tests/fixtures/{name}")).unwrap()
    }

    fn temp_copy(bytes: &[u8], name: &str) -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!("ayaan_docinfo_{name}_{}.pdf", std::process::id()));
        std::fs::write(&path, bytes).unwrap();
        path
    }

    fn pairs_of(items: &[(&str, &str)]) -> Vec<u8> {
        join_nul(items.iter().flat_map(|(k, v)| [*k, *v]))
    }

    fn write(path: &std::path::Path, items: &[(&str, &str)]) -> i32 {
        let data = pairs_of(items);
        let c_path = CString::new(path.to_str().unwrap()).unwrap();
        unsafe { write_document_info(c_path.as_ptr(), data.as_ptr(), data.len()) }
    }

    /// What PDFium, the OTHER library, reads out of the file.
    fn read(path: &std::path::Path) -> HashMap<String, String> {
        let c_path = CString::new(path.to_str().unwrap()).unwrap();
        let handle = crate::open_document(c_path.as_ptr());
        assert_ne!(handle, 0, "PDFium could not open {}", path.display());
        let buffer = get_document_properties(handle);
        assert_eq!(buffer.status, STATUS_OK_PDFIUM);
        let bytes = unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) }.to_vec();
        crate::free_byte_buffer(buffer);
        crate::close_document(handle);
        parse_pairs(&bytes).unwrap().into_iter().collect()
    }

    #[test]
    fn what_lopdf_writes_is_what_pdfium_reads_back() {
        // Two libraries have to agree: lopdf appends the update, PDFium reads
        // the file through its own parser. A test reading back with lopdf
        // would mostly prove lopdf can parse its own output.
        let path = temp_copy(&fixture("sample_20pages.pdf"), "roundtrip");
        let before = read(&path);
        assert_ne!(before.get("Title").map(String::as_str), Some("Chapter and verse"), "the control");

        let status = write(
            &path,
            &[
                ("Title", "Chapter and verse"),
                ("Author", "Aung Ko Ko"),
                ("Producer", "Ayaan PDF 9.9.9"),
                ("ModDate", "D:20260918120000+06'30'"),
                ("XmpDate", "2026-09-18T12:00:00+06:30"),
            ],
        );
        assert_eq!(status, STATUS_OK_PDFIUM);

        let after = read(&path);
        assert_eq!(after["Title"], "Chapter and verse");
        assert_eq!(after["Author"], "Aung Ko Ko");
        assert_eq!(after["Producer"], "Ayaan PDF 9.9.9");
        assert_eq!(after["ModDate"], "D:20260918120000+06'30'");
        assert_eq!(after["Pages"], before["Pages"], "the pages must be untouched");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_title_in_hindi_or_burmese_reads_back_exactly() {
        // PDFDocEncoding cannot hold either script; they go out as UTF-16BE.
        let path = temp_copy(&fixture("sample_20pages.pdf"), "scripts");
        assert_eq!(write(&path, &[("Title", "गीता दर्शन"), ("Author", "အောင်ကိုကို")]), STATUS_OK_PDFIUM);

        let after = read(&path);
        assert_eq!(after["Title"], "गीता दर्शन");
        assert_eq!(after["Author"], "အောင်ကိုကို");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn the_update_is_appended_rather_than_rewriting_the_file() {
        // What makes stamping every save affordable on a 200 MB book.
        let original = fixture("sample_300pages.pdf");
        let path = temp_copy(&original, "append");
        assert_eq!(write(&path, &[("Title", "Appended"), ("ModDate", "D:20260918120000Z")]), STATUS_OK_PDFIUM);

        let written = std::fs::read(&path).unwrap();
        assert!(written.starts_with(&original), "the original bytes must be kept as they were");
        assert!(written.len() - original.len() < 4096, "grew by {} bytes", written.len() - original.len());
        assert_eq!(read(&path)["Title"], "Appended");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_file_with_a_compressed_cross_reference_takes_the_update_too() {
        let path = temp_copy(&fixture("sample_justified.pdf"), "xrefstream");
        let pages = read(&path)["Pages"].clone();
        assert_eq!(write(&path, &[("Title", "Streamed"), ("Subject", "xref stream")]), STATUS_OK_PDFIUM);

        let after = read(&path);
        assert_eq!(after["Title"], "Streamed");
        assert_eq!(after["Subject"], "xref stream");
        assert_eq!(after["Pages"], pages);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn an_empty_value_removes_the_field_and_a_second_update_stacks() {
        let path = temp_copy(&fixture("sample_20pages.pdf"), "remove");
        assert_eq!(write(&path, &[("Subject", "to be removed"), ("Keywords", "kept")]), STATUS_OK_PDFIUM);
        assert_eq!(read(&path)["Subject"], "to be removed");

        assert_eq!(write(&path, &[("Subject", "")]), STATUS_OK_PDFIUM);
        let after = read(&path);
        assert!(!after.contains_key("Subject") || after["Subject"].is_empty(), "{after:?}");
        assert_eq!(after["Keywords"], "kept", "a field this write did not name is left alone");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_document_pdfium_created_says_ayaan_pdf_and_no_other_creator_is_touched() {
        let handle = crate::create_document();
        assert_ne!(handle, 0);
        assert_eq!(crate::insert_blank_page(handle, 0, 612.0, 792.0), STATUS_OK_PDFIUM);
        let path = std::env::temp_dir().join(format!("ayaan_docinfo_created_{}.pdf", std::process::id()));
        let c_path = CString::new(path.to_str().unwrap()).unwrap();
        assert_eq!(crate::save_document(handle, c_path.as_ptr()), STATUS_OK_PDFIUM);
        crate::close_document(handle);
        assert_eq!(read(&path).get("Creator").map(String::as_str), Some("PDFium"), "the control");

        assert_eq!(write(&path, &[("CreatorIfPdfium", "Ayaan PDF")]), STATUS_OK_PDFIUM);
        assert_eq!(read(&path)["Creator"], "Ayaan PDF");

        // Somebody else's creator is theirs to keep.
        assert_eq!(write(&path, &[("CreatorIfPdfium", "Something else")]), STATUS_OK_PDFIUM);
        assert_eq!(read(&path)["Creator"], "Ayaan PDF");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn an_encrypted_file_is_refused_and_left_as_it_was() {
        let original = fixture("sample_encrypted.pdf");
        let path = temp_copy(&original, "encrypted");
        assert_eq!(write(&path, &[("Title", "Nope")]), STATUS_UNSUPPORTED);
        assert_eq!(std::fs::read(&path).unwrap(), original);
        let _ = std::fs::remove_file(&path);
    }

    /// sample_20pages with an XMP packet of the shape Word writes: the title
    /// as an element, the producer as an attribute, and a PDF/A marker.
    fn with_xmp(xmp: &str) -> Vec<u8> {
        let mut doc = Document::load_mem(&fixture("sample_20pages.pdf")).unwrap();
        let mut dict = Dictionary::new();
        dict.set("Type", Object::Name(b"Metadata".to_vec()));
        dict.set("Subtype", Object::Name(b"XML".to_vec()));
        let id = doc.add_object(Stream::new(dict, xmp.as_bytes().to_vec()));
        let root = doc.trailer.get(b"Root").unwrap().as_reference().unwrap();
        doc.get_object_mut(root).unwrap().as_dict_mut().unwrap().set("Metadata", Object::Reference(id));
        let mut out = Vec::new();
        doc.save_to(&mut out).unwrap();
        out
    }

    const WORD_XMP: &str = concat!(
        "<?xpacket begin=\"\u{feff}\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>",
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">",
        "<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" pdf:Producer=\"Microsoft Word\"/>",
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">",
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Old title</rdf:li></rdf:Alt></dc:title>",
        "<dc:format>application/pdf</dc:format></rdf:Description>",
        "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"1\"/>",
        "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>",
    );

    #[test]
    fn the_xmp_copy_changes_with_the_info_and_nothing_else_in_it_does() {
        let path = temp_copy(&with_xmp(WORD_XMP), "xmp");
        assert_eq!(
            write(&path, &[("Title", "New title"), ("Producer", "Ayaan PDF 9.9.9"), ("XmpDate", "2026-09-18T12:00:00+06:30")]),
            STATUS_OK_PDFIUM
        );
        assert_eq!(read(&path)["Title"], "New title");

        let doc = Document::load(&path).unwrap();
        let (_, stream) = metadata_stream(&doc).expect("the XMP stream is still there");
        let xml = String::from_utf8(stream.content.clone()).unwrap();
        assert!(!xml.contains("Old title"), "{xml}");
        assert!(xml.contains("<rdf:li xml:lang=\"x-default\">New title</rdf:li>"), "{xml}");
        assert!(!xml.contains("Microsoft Word"), "the attribute form must go too: {xml}");
        assert!(xml.contains("<pdf:Producer>Ayaan PDF 9.9.9</pdf:Producer>"), "{xml}");
        assert!(xml.contains("<xmp:ModifyDate>2026-09-18T12:00:00+06:30</xmp:ModifyDate>"), "{xml}");
        assert!(xml.contains("pdfaid:part=\"1\""), "PDF/A identification must survive: {xml}");
        assert!(xml.contains("<dc:format>application/pdf</dc:format>"), "{xml}");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn xmp_edits_handle_every_shape_a_property_comes_in() {
        let xml = concat!(
            "<x:xmpmeta><rdf:RDF>",
            "<rdf:Description rdf:about=\"\" pdf:Keywords='a, b' xmp:ModifyDate=\"2020-01-01\">",
            "<dc:title/><dc:creator><rdf:Seq><rdf:li>Someone</rdf:li></rdf:Seq></dc:creator>",
            "<dc:titles>not a title</dc:titles></rdf:Description></rdf:RDF></x:xmpmeta>",
        );
        let out = edit_xmp(
            xml,
            &[
                (XmpProperty::Title, "A & B <c>"),
                (XmpProperty::Creator, ""),
                (XmpProperty::Keywords, "x"),
                (XmpProperty::ModifyDate, "2026-09-18T12:00:00Z"),
            ],
            &[],
        )
        .unwrap();

        assert!(out.contains("A &amp; B &lt;c&gt;"), "{out}");
        assert!(!out.contains("Someone"), "an empty value removes the property: {out}");
        assert!(!out.contains("<dc:creator>"), "{out}");
        assert!(!out.contains("'a, b'") && !out.contains("2020-01-01"), "{out}");
        assert!(out.contains("<dc:titles>not a title</dc:titles>"), "a longer name is a different property: {out}");
        assert_eq!(out.matches("<dc:title>").count(), 1, "{out}");
    }

    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn what_a_properties_write_costs_on_real_files() {
        let dir = std::path::Path::new(r"D:\Ayaan PDF Test file");
        let Ok(entries) = std::fs::read_dir(dir) else { return };
        let mut files: Vec<_> = entries
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("pdf")))
            .filter(|p| !p.file_name().unwrap().to_string_lossy().starts_with("ayaan-"))
            .collect();
        files.sort();

        println!("{:<46} {:>8} {:>8} {:>7}  result", "file", "MB", "ms", "grew");
        for file in files {
            let original = std::fs::read(&file).unwrap();
            let path = temp_copy(&original, "real");
            let before = std::panic::catch_unwind(|| read(&path)).ok();
            let started = std::time::Instant::now();
            let status = write(
                &path,
                &[
                    ("Title", "Round trip गीता"),
                    ("Producer", "Ayaan PDF 9.9.9"),
                    ("CreatorIfPdfium", "Ayaan PDF"),
                    ("ModDate", "D:20260918120000+06'30'"),
                    ("XmpDate", "2026-09-18T12:00:00+06:30"),
                ],
            );
            let ms = started.elapsed().as_millis();
            let grew = std::fs::metadata(&path).map(|m| m.len() as i64 - original.len() as i64).unwrap_or(0);
            let verdict = if status != STATUS_OK_PDFIUM {
                format!("status {status}")
            } else {
                match std::panic::catch_unwind(|| read(&path)) {
                    Ok(after) => {
                        let title_ok = after.get("Title").map(String::as_str) == Some("Round trip गीता");
                        let pages_ok = before.as_ref().map(|b| b.get("Pages") == after.get("Pages")).unwrap_or(true);
                        format!("title {} pages {}", if title_ok { "ok" } else { "WRONG" }, if pages_ok { "ok" } else { "CHANGED" })
                    }
                    Err(_) => "PDFium could not reopen it".to_owned(),
                }
            };
            println!(
                "{:<46} {:>8.1} {:>8} {:>7}  {}",
                file.file_name().unwrap().to_string_lossy().chars().take(46).collect::<String>(),
                original.len() as f64 / 1e6,
                ms,
                grew,
                verdict
            );
            let _ = std::fs::remove_file(&path);
        }
    }

    /// PDFium's reading of a page's text, as raw bytes, for comparison.
    fn text_of(path: &std::path::Path, page: i32) -> Vec<u8> {
        let c_path = CString::new(path.to_str().unwrap()).unwrap();
        let handle = crate::open_document(c_path.as_ptr());
        assert_ne!(handle, 0);
        let buffer = crate::get_page_text_runs(handle, page);
        let bytes = if buffer.data.is_null() {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) }.to_vec()
        };
        crate::free_byte_buffer(buffer);
        crate::close_document(handle);
        bytes
    }

    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn a_file_the_app_saved_reads_the_same_after_its_stamp() {
        // The path every save now takes: PDFium writes the file, then the
        // stamp is appended. What PDFium reads off the pages must not change,
        // Burmese and Hindi above all.
        let dir = std::path::Path::new(r"D:\Ayaan PDF Test file");
        let Ok(entries) = std::fs::read_dir(dir) else { return };
        let mut files: Vec<_> = entries
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("pdf")))
            .filter(|p| !p.file_name().unwrap().to_string_lossy().starts_with("ayaan-"))
            .filter(|p| std::fs::metadata(p).map(|m| m.len() < 20_000_000).unwrap_or(false))
            .collect();
        files.sort();

        let mut bad = Vec::new();
        for file in files {
            let c_src = CString::new(file.to_str().unwrap()).unwrap();
            let handle = crate::open_document(c_src.as_ptr());
            if handle == 0 {
                println!("{:<46} could not open", file.file_name().unwrap().to_string_lossy());
                continue;
            }
            let saved = std::env::temp_dir().join(format!("ayaan_docinfo_saved_{}.pdf", std::process::id()));
            let c_saved = CString::new(saved.to_str().unwrap()).unwrap();
            let status = crate::save_document(handle, c_saved.as_ptr());
            crate::close_document(handle);
            if status != STATUS_OK_PDFIUM {
                println!("{:<46} PDFium save {status}", file.file_name().unwrap().to_string_lossy());
                continue;
            }

            let pages: i32 = read(&saved)["Pages"].parse().unwrap();
            let checked = pages.min(3);
            let before: Vec<Vec<u8>> = (0..checked).map(|p| text_of(&saved, p)).collect();

            let stamp = write(
                &saved,
                &[
                    ("Title", "गीता ကဗျာ"),
                    ("Producer", "Ayaan PDF 9.9.9"),
                    ("CreatorIfPdfium", "Ayaan PDF"),
                    ("ModDate", "D:20260918120000+06'30'"),
                    ("XmpDate", "2026-09-18T12:00:00+06:30"),
                ],
            );
            let after_props = read(&saved);
            let after: Vec<Vec<u8>> = (0..checked).map(|p| text_of(&saved, p)).collect();

            let same_text = before == after;
            let ok = stamp == STATUS_OK_PDFIUM
                && same_text
                && after_props["Pages"] == pages.to_string()
                && after_props.get("Title").map(String::as_str) == Some("गीता ကဗျာ");
            println!(
                "{:<46} stamp {} pages {} text of first {} {}",
                file.file_name().unwrap().to_string_lossy().chars().take(46).collect::<String>(),
                stamp,
                pages,
                checked,
                if same_text { "identical" } else { "CHANGED" }
            );
            if !ok {
                bad.push(file.display().to_string());
            }
            let _ = std::fs::remove_file(&saved);
        }
        assert!(bad.is_empty(), "{bad:?}");
    }

    #[test]
    fn xmp_without_an_rdf_body_is_left_alone() {
        assert_eq!(edit_xmp("<x:xmpmeta/>", &[(XmpProperty::Title, "t")], &[]), None);
    }

    // ---------------- Removing personal info ----------------

    const PERSONAL_XMP_PACKET: &str = concat!(
        "<?xpacket begin=\"\u{feff}\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>",
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">",
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"",
        " xmlns:pdfx=\"http://ns.adobe.com/pdfx/1.3/\" xmp:CreatorTool=\"SecretWriter 1.0\" pdfx:Company=\"Acme Private\">",
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Kept title</rdf:li></rdf:Alt></dc:title>",
        "<dc:creator><rdf:Seq><rdf:li>Private Person Xq7</rdf:li></rdf:Seq></dc:creator></rdf:Description>",
        "<rdf:Description rdf:about=\"\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\" xmlns:stEvt=\"x\" xmlns:stRef=\"y\">",
        "<xmpMM:History><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><stEvt:action>created</stEvt:action></rdf:li>",
        "<rdf:li rdf:parseType=\"Resource\"><stEvt:action>saved</stEvt:action></rdf:li></rdf:Seq></xmpMM:History>",
        "<xmpMM:DerivedFrom rdf:parseType=\"Resource\"><stRef:filePath>C:\\Users\\xq7\\secret.docx</stRef:filePath></xmpMM:DerivedFrom>",
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>",
    );

    /// sample_20pages carrying personal info everywhere a real file does: the
    /// Info dictionary with a custom key, XMP with history and a source path,
    /// a comment with an author, XMP on a page, and a FORM FIELD whose /T is
    /// its name and has to survive.
    fn with_personal_info() -> Vec<u8> {
        let mut doc = Document::load_mem(&fixture("sample_20pages.pdf")).unwrap();

        let mut info = Dictionary::new();
        info.set("Title", pdf_text_string("Kept title"));
        info.set("Author", pdf_text_string("Private Person Xq7"));
        info.set("Creator", pdf_text_string("SecretWriter 1.0"));
        info.set("Company", pdf_text_string("Acme Private"));
        info.set("CreationDate", Object::String(b"D:20250101000000Z".to_vec(), StringFormat::Literal));
        let info_id = doc.add_object(info);
        doc.trailer.set("Info", Object::Reference(info_id));

        let mut meta = Dictionary::new();
        meta.set("Type", Object::Name(b"Metadata".to_vec()));
        meta.set("Subtype", Object::Name(b"XML".to_vec()));
        let meta_id = doc.add_object(Stream::new(meta.clone(), PERSONAL_XMP_PACKET.as_bytes().to_vec()));
        let root = doc.trailer.get(b"Root").unwrap().as_reference().unwrap();
        doc.get_object_mut(root).unwrap().as_dict_mut().unwrap().set("Metadata", Object::Reference(meta_id));

        let page_meta = doc.add_object(Stream::new(meta, b"<x:xmpmeta>PageXmpSecret</x:xmpmeta>".to_vec()));

        let mut comment = Dictionary::new();
        comment.set("Type", Object::Name(b"Annot".to_vec()));
        comment.set("Subtype", Object::Name(b"Text".to_vec()));
        comment.set("Rect", Object::Array(vec![50.into(), 50.into(), 70.into(), 70.into()]));
        comment.set("T", pdf_text_string("Private Person Xq7"));
        comment.set("Contents", pdf_text_string("A note"));
        let comment_id = doc.add_object(comment);

        let mut field = Dictionary::new();
        field.set("Type", Object::Name(b"Annot".to_vec()));
        field.set("Subtype", Object::Name(b"Widget".to_vec()));
        field.set("FT", Object::Name(b"Tx".to_vec()));
        field.set("T", pdf_text_string("FieldNameKeep"));
        field.set("Rect", Object::Array(vec![100.into(), 100.into(), 200.into(), 120.into()]));
        let field_id = doc.add_object(field);

        let first_page = *doc.get_pages().get(&1).unwrap();
        let page = doc.get_object_mut(first_page).unwrap().as_dict_mut().unwrap();
        page.set("Annots", Object::Array(vec![Object::Reference(comment_id), Object::Reference(field_id)]));
        page.set("Metadata", Object::Reference(page_meta));

        let mut out = Vec::new();
        doc.save_to(&mut out).unwrap();
        out
    }

    fn contains(haystack: &[u8], needle: &str) -> bool {
        haystack.windows(needle.len()).any(|w| w == needle.as_bytes())
    }

    const SECRETS: &[&str] = &["Private Person", "SecretWriter", "Acme Private", "secret.docx", "PageXmpSecret"];

    #[test]
    fn what_a_file_says_about_its_people_is_found() {
        let doc = Document::load_mem(&with_personal_info()).unwrap();
        let found = personal_info_of(&doc);
        let has = |k: &str, v: &str| found.iter().any(|(fk, fv)| *fk == k && fv == v);

        assert!(has("Author", "Private Person Xq7"), "{found:?}");
        assert!(has("Creator", "SecretWriter 1.0"), "{found:?}");
        assert!(has("Custom", "Company: Acme Private"), "the Info custom key: {found:?}");
        assert!(has("XmpAuthor", "Private Person Xq7"), "{found:?}");
        assert!(has("XmpTool", "SecretWriter 1.0"), "{found:?}");
        assert!(has("History", "2"), "{found:?}");
        assert!(has("FilePath", "C:\\Users\\xq7\\secret.docx"), "{found:?}");
        assert!(has("CommentAuthor", "Private Person Xq7"), "{found:?}");
        assert!(has("Comments", "1"), "{found:?}");
        assert!(has("ObjectMetadata", "1"), "{found:?}");
        assert!(!found.iter().any(|(_, v)| v.contains("FieldNameKeep")), "a field name is not an author: {found:?}");
        assert!(!found.iter().any(|(k, _)| *k == "Custom" && found.iter().any(|(_, v)| v.contains("Kept title"))));
    }

    #[test]
    fn removing_personal_info_leaves_none_of_it_anywhere_in_the_file() {
        let original = with_personal_info();

        // The control: the ordinary appended update clears the author as far
        // as any reader is concerned, and leaves it in the file's bytes.
        let appended = temp_copy(&original, "personal_control");
        assert_eq!(write(&appended, &[("Author", "")]), STATUS_OK_PDFIUM);
        assert!(!read(&appended).contains_key("Author"));
        assert!(contains(&std::fs::read(&appended).unwrap(), "Private Person Xq7"), "the control must still carry it");
        let _ = std::fs::remove_file(&appended);

        let path = temp_copy(&original, "personal");
        let text_before: Vec<Vec<u8>> = (0..3).map(|p| text_of(&path, p)).collect();
        let pages = read(&path)["Pages"].clone();

        assert_eq!(
            write(&path, &[("RemovePersonal", "1"), ("Author", ""), ("Producer", "Ayaan PDF 9.9.9"), ("ModDate", "D:20260918120000Z")]),
            STATUS_OK_PDFIUM
        );

        let bytes = std::fs::read(&path).unwrap();
        for secret in SECRETS {
            assert!(!contains(&bytes, secret), "{secret} is still in the file");
        }
        assert!(contains(&bytes, "FieldNameKeep"), "a form field's name must survive");
        assert!(contains(&bytes, "Kept title"), "the title is the document's, not personal");

        let after = read(&path);
        assert!(!after.contains_key("Author") && !after.contains_key("Creator"), "{after:?}");
        assert_eq!(after["Title"], "Kept title");
        assert_eq!(after["Producer"], "Ayaan PDF 9.9.9");
        assert_eq!(after["CreationDate"], "D:20250101000000Z", "dates are kept");
        assert_eq!(after["Pages"], pages);
        let text_after: Vec<Vec<u8>> = (0..3).map(|p| text_of(&path, p)).collect();
        assert_eq!(text_before, text_after, "the pages must read exactly as before");

        let doc = Document::load(&path).unwrap();
        assert!(personal_info_of(&doc).is_empty(), "{:?}", personal_info_of(&doc));
        let (_, stream) = metadata_stream(&doc).expect("the document's own XMP is edited, not dropped");
        let xml = String::from_utf8(stream.content.clone()).unwrap();
        assert!(xml.contains("Kept title"), "{xml}");
        assert!(!xml.contains("pdfx:Company"), "{xml}");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn removing_personal_info_from_an_encrypted_file_is_refused_and_leaves_it_alone() {
        let original = fixture("sample_encrypted.pdf");
        let path = temp_copy(&original, "personal_encrypted");
        assert_eq!(write(&path, &[("RemovePersonal", "1")]), STATUS_UNSUPPORTED);
        assert_eq!(std::fs::read(&path).unwrap(), original);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn what_removing_personal_info_does_to_real_files() {
        // The path a save with removal takes: PDFium writes, then the file is
        // rewritten whole. Pages must read the same, nothing personal may be
        // found afterwards, and the author must be gone from the raw bytes.
        let dir = std::path::Path::new(r"D:\Ayaan PDF Test file");
        let Ok(entries) = std::fs::read_dir(dir) else { return };
        let mut files: Vec<_> = entries
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("pdf")))
            .filter(|p| !p.file_name().unwrap().to_string_lossy().starts_with("ayaan-"))
            .collect();
        files.sort();

        let mut bad = Vec::new();
        println!("{:<40} {:>7} {:>7} {:>7}  found / result", "file", "MB", "after", "ms");
        for file in files {
            let c_src = CString::new(file.to_str().unwrap()).unwrap();
            let handle = crate::open_document(c_src.as_ptr());
            if handle == 0 {
                continue;
            }
            let saved = std::env::temp_dir().join(format!("ayaan_docinfo_rm_{}.pdf", std::process::id()));
            let c_saved = CString::new(saved.to_str().unwrap()).unwrap();
            let status = crate::save_document(handle, c_saved.as_ptr());
            crate::close_document(handle);
            if status != STATUS_OK_PDFIUM {
                continue;
            }

            let before_doc = Document::load(&saved).ok();
            let found = before_doc.as_ref().map(personal_info_of).unwrap_or_default();
            let author = found.iter().find(|(k, _)| *k == "Author").map(|(_, v)| v.clone());
            let pages = read(&saved)["Pages"].clone();
            let checked: i32 = pages.parse::<i32>().unwrap().min(3);
            let text_before: Vec<Vec<u8>> = (0..checked).map(|p| text_of(&saved, p)).collect();
            let size_before = std::fs::metadata(&saved).unwrap().len();

            let started = std::time::Instant::now();
            let stamp = write(&saved, &[("RemovePersonal", "1"), ("Producer", "Ayaan PDF 9.9.9"), ("ModDate", "D:20260918120000Z")]);
            let ms = started.elapsed().as_millis();

            let mut verdict = Vec::new();
            if stamp != STATUS_OK_PDFIUM {
                verdict.push(format!("status {stamp}"));
            } else {
                let after = read(&saved);
                if after["Pages"] != pages { verdict.push("PAGES CHANGED".into()); }
                let text_after: Vec<Vec<u8>> = (0..checked).map(|p| text_of(&saved, p)).collect();
                if text_after != text_before { verdict.push("TEXT CHANGED".into()); }
                let left = personal_info_of(&Document::load(&saved).unwrap());
                if !left.is_empty() { verdict.push(format!("LEFT {left:?}")); }
                if let Some(a) = author.filter(|a| a.is_ascii() && a.len() >= 4) {
                    if contains(&std::fs::read(&saved).unwrap(), &a) { verdict.push(format!("AUTHOR STILL IN BYTES {a}")); }
                }
            }
            let size_after = std::fs::metadata(&saved).map(|m| m.len()).unwrap_or(0);
            let kinds: BTreeSet<&str> = found.iter().map(|(k, _)| *k).collect();
            println!(
                "{:<40} {:>7.1} {:>7.1} {:>7}  {:?} {}",
                file.file_name().unwrap().to_string_lossy().chars().take(40).collect::<String>(),
                size_before as f64 / 1e6,
                size_after as f64 / 1e6,
                ms,
                kinds,
                if verdict.is_empty() { "ok".to_owned() } else { verdict.join("; ") }
            );
            if !verdict.is_empty() {
                bad.push(file.display().to_string());
            }
            let _ = std::fs::remove_file(&saved);
        }
        assert!(bad.is_empty(), "{bad:?}");
    }

    #[test]
    fn fonts_are_listed_once_with_whether_they_are_embedded() {
        let doc = Document::load("tests/fixtures/sample_complex_script.pdf").unwrap();
        let fonts = fonts_of(&doc);
        assert!(!fonts.is_empty());
        assert!(fonts.iter().all(|f| !f.name.is_empty() && !f.kind.is_empty()), "{fonts:?}");
        assert!(
            fonts.iter().all(|f| !(f.name.len() > 7 && f.name.as_bytes()[6] == b'+')),
            "subset prefixes stripped: {fonts:?}"
        );
        let mut names: Vec<_> = fonts.iter().map(|f| (&f.name, f.embedded)).collect();
        names.dedup();
        assert_eq!(names.len(), fonts.len(), "{fonts:?}");
    }
}
