//! Setting the state of a form field: checkbox, radio, combo box, list box.
//!
//! THE THIRD THING PDFIUM CANNOT DO, and the reason is smaller and sharper than
//! it looks. A checkbox's state lives in two places, the field's `/V` and the
//! widget's `/AS`, and BOTH are PDF **name** objects. PDFium's whole
//! annotation-writing surface is `FPDFAnnot_SetStringValue`, which writes a
//! **string**. There is no call anywhere in the C API that writes a name into an
//! annotation dictionary.
//!
//! Measured, not assumed. Writing `/AS` and `/V` as strings does not merely fail
//! to take: it destroys the widget's appearance, because the name lookup into
//! `/AP /N` no longer matches anything. Dark pixels inside the widget rect, on
//! the fixture, rendered the way the app renders:
//!
//! | | before | after PDFium wrote strings |
//! |---|---|---|
//! | checkbox | 101 | 75, the box itself gone |
//! | radio, unselected | 134 | 81 |
//! | radio, selected | 187 | 83 |
//!
//! and it survived a save and reopen in that state.
//!
//! The vendored crate's own setters cannot be used either, for reasons visible
//! in its source and then confirmed:
//!
//! * `checkbox.set_checked()` hard-codes `"/Yes"`. Correct only when the form
//!   happens to name its on state `Yes`; plenty use `On`, `1`, or a word in
//!   another language. This module never assumes, and the fixture's checkbox is
//!   deliberately `/On` so that assuming would fail a test.
//! * `radio.set_checked()` reads the widget's CURRENT `/AS` and writes that as
//!   the group value, so selecting an unselected radio sets the group to `Off`.
//!   Reproduced exactly: it printed `ap: Some("Off")` and nothing changed.
//! * combo and list have no value setter at all.
//!
//! So this is lopdf, exactly as `outline.rs` writes bookmarks and `gradient.rs`
//! writes shadings, and for exactly the same reason.
//!
//! ⚠️ THE ON-STATE NAME IS IN THE FILE ALREADY. It is the key of the widget's
//! own `/AP /N` dictionary that is not `Off`. Nothing has to be generated, and
//! no appearance has to be drawn: both states are already there and `/AS` simply
//! picks one. That is why checkbox and radio are cheap.
//!
//! A choice field is different: it has ONE appearance stream showing the current
//! label, so changing the value leaves the old text drawn. `/NeedAppearances`
//! tells the reader to rebuild it, and the PDFium we ship was measured to honour
//! that (a combo reading "United Kingdom" came back "Myanmar").
//!
//! Nothing here is addressed by page or object index. A field is found by its
//! fully qualified name and a widget by its position among its field's `/Kids`,
//! which is the same pair PDFium reports and neither of which drifts when the
//! document is written.

use lopdf::{Dictionary, Document, Object, ObjectId};

use crate::{STATUS_INVALID_INPUT, STATUS_OK_PDFIUM, STATUS_UNSUPPORTED};

/// The kinds this module writes. Mirrors the FIELD_* constants in lib.rs.
pub const KIND_CHECKBOX: i32 = 2;
pub const KIND_RADIO: i32 = 3;
pub const KIND_COMBO: i32 = 4;
pub const KIND_LISTBOX: i32 = 5;

/// Rewrites one field's state, returning the new document bytes.
///
/// `index` is a WIDGET's position among its field's `/Kids` for a checkbox or
/// radio, and an OPTION's position in `/Opt` for a choice field. `on` applies
/// only to a checkbox; a radio is always a selection and a choice field always
/// has one.
pub fn apply(
    bytes: &[u8],
    field_name: &str,
    kind: i32,
    index: i32,
    on: bool,
) -> Result<Vec<u8>, i32> {
    let mut doc = Document::load_mem(bytes).map_err(|_| STATUS_UNSUPPORTED)?;

    let field_id = find_field(&doc, field_name).ok_or(STATUS_INVALID_INPUT)?;
    let widgets = widgets_of(&doc, field_id);
    if widgets.is_empty() {
        return Err(STATUS_INVALID_INPUT);
    }

    match kind {
        KIND_CHECKBOX => set_checkbox(&mut doc, field_id, &widgets, index, on)?,
        KIND_RADIO => set_radio(&mut doc, field_id, &widgets, index)?,
        KIND_COMBO | KIND_LISTBOX => set_choice(&mut doc, field_id, index)?,
        _ => return Err(STATUS_INVALID_INPUT),
    }

    let mut out = Vec::with_capacity(bytes.len() + 1024);
    doc.save_to(&mut out).map_err(|_| STATUS_UNSUPPORTED)?;
    Ok(out)
}

/// The object carrying the field whose fully qualified name is `wanted`.
///
/// Qualified, not partial: a field's name is its own `/T` with each ancestor's
/// `/T` in front, joined by dots, which is what PDFium reports and therefore
/// what the app asks with. A form with two `Name` fields under different parents
/// is the case that makes the difference.
fn find_field(doc: &Document, wanted: &str) -> Option<ObjectId> {
    doc.objects
        .keys()
        .copied()
        .find(|&id| qualified_name(doc, id).as_deref() == Some(wanted))
}

fn qualified_name(doc: &Document, id: ObjectId) -> Option<String> {
    let mut parts: Vec<String> = Vec::new();
    let mut at = Some(id);
    let mut depth = 0;

    // Bounded: a malformed file can point a /Parent chain back at itself, and
    // walking one forever inside a save is not a failure anyone can diagnose.
    while let Some(current) = at {
        if depth > 32 {
            return None;
        }
        depth += 1;

        let dict = doc.get_dictionary(current).ok()?;
        if let Ok(t) = dict.get(b"T").and_then(|o| o.as_str()) {
            parts.push(String::from_utf8_lossy(t).into_owned());
        }
        at = dict.get(b"Parent").and_then(|o| o.as_reference()).ok();
    }

    if parts.is_empty() {
        return None;
    }
    parts.reverse();
    Some(parts.join("."))
}

/// A field's widgets in `/Kids` order, or the field itself when it IS the
/// widget. The order is what PDFium's control index counts, so the two agree.
fn widgets_of(doc: &Document, field_id: ObjectId) -> Vec<ObjectId> {
    let Ok(dict) = doc.get_dictionary(field_id) else {
        return Vec::new();
    };

    match dict.get(b"Kids").and_then(|o| o.as_array()) {
        Ok(kids) => kids
            .iter()
            .filter_map(|k| k.as_reference().ok())
            .filter(|&id| {
                doc.get_dictionary(id)
                    .map(|d| d.has(b"Subtype") || d.has(b"Rect"))
                    .unwrap_or(false)
            })
            .collect(),
        Err(_) => vec![field_id],
    }
}

/// The name a widget's `/AP /N` uses for its ON state.
///
/// ⚠️ NEVER `Yes`. It is whichever key of that dictionary is not `Off`, and it
/// differs per widget inside one radio group, which is exactly how a group knows
/// which button is selected.
fn on_state(doc: &Document, widget: ObjectId) -> Option<String> {
    doc.get_dictionary(widget)
        .ok()?
        .get(b"AP")
        .and_then(|o| o.as_dict())
        .ok()?
        .get(b"N")
        .and_then(|o| o.as_dict())
        .ok()?
        .iter()
        .map(|(k, _)| String::from_utf8_lossy(k).into_owned())
        .find(|k| k != "Off")
}

fn set_name(doc: &mut Document, id: ObjectId, key: &str, value: &str) {
    if let Ok(dict) = doc.get_dictionary_mut(id) {
        dict.set(key, Object::Name(value.as_bytes().to_vec()));
    }
}

fn set_checkbox(
    doc: &mut Document,
    field_id: ObjectId,
    widgets: &[ObjectId],
    index: i32,
    on: bool,
) -> Result<(), i32> {
    let widget = *widgets
        .get(usize::try_from(index).map_err(|_| STATUS_INVALID_INPUT)?)
        .ok_or(STATUS_INVALID_INPUT)?;

    let state = if on {
        on_state(doc, widget).ok_or(STATUS_UNSUPPORTED)?
    } else {
        "Off".to_owned()
    };

    set_name(doc, widget, "AS", &state);
    set_name(doc, field_id, "V", &state);
    Ok(())
}

fn set_radio(
    doc: &mut Document,
    field_id: ObjectId,
    widgets: &[ObjectId],
    index: i32,
) -> Result<(), i32> {
    let wanted = usize::try_from(index).map_err(|_| STATUS_INVALID_INPUT)?;
    if wanted >= widgets.len() {
        return Err(STATUS_INVALID_INPUT);
    }

    // Read every widget's own on state BEFORE writing any of them: lopdf hands
    // out a borrow of the whole document, and the chosen widget's state is the
    // group's new value, so both have to be known first.
    let states: Vec<Option<String>> = widgets.iter().map(|&w| on_state(doc, w)).collect();
    let chosen = states[wanted].clone().ok_or(STATUS_UNSUPPORTED)?;

    // ⚠️ EVERY widget moves, not just the one clicked. A radio group shows the
    // selection by each button's own /AS, so leaving the others alone would draw
    // two selected buttons even though the group value named one.
    for (i, &widget) in widgets.iter().enumerate() {
        let state = if i == wanted { chosen.as_str() } else { "Off" };
        set_name(doc, widget, "AS", state);
    }

    set_name(doc, field_id, "V", &chosen);
    Ok(())
}

/// The export value of one `/Opt` entry, AS THE STRING OBJECT IT ALREADY IS.
///
/// Two shapes are legal and both appear in the wild: a bare string, where the
/// export value and the label are the same, and a two-element array
/// `[export display]`, where they differ. Writing the display text as the value
/// is the mistake that makes a form submit "United Kingdom" where the server
/// expects "UK".
///
/// ⚠️ THE OBJECT IS COPIED, NOT RE-ENCODED, and that is the whole fix for the
/// real-world failure. A PDF text string can be PDFDocEncoded or UTF-16BE with
/// a byte order mark, and any producer writes the second one the moment a form
/// is authored outside plain ASCII. Decoding to a Rust `String` and writing that
/// back turned `FE FF 00 57 ...` ("Woman") into
/// `EF BF BD EF BF BD 00 57 ...`: the BOM became two replacement characters,
/// the string stopped being valid UTF-16, PDFium matched no option, and the
/// field was silently BLANKED while the write reported success.
///
/// Handing back the very bytes and the very string format that are already in
/// `/Opt` cannot get the encoding wrong, because it never has an opinion about
/// it. Anything that is not a string is refused rather than guessed at.
fn export_object(entry: &Object) -> Option<Object> {
    let candidate = match entry {
        Object::Array(pair) => pair.first()?,
        other => other,
    };

    match candidate {
        Object::String(bytes, format) => Some(Object::String(bytes.clone(), *format)),
        _ => None,
    }
}

/// A PDF text string as text, for reporting and for tests.
///
/// Two encodings, told apart the way the specification says: a leading
/// `FE FF` means UTF-16BE, anything else is PDFDocEncoded, which agrees with
/// Latin-1 across everything a form field is likely to hold.
///
/// Reading only. Nothing writes through this, so a lossy corner cannot reach
/// the document.
pub fn decode_pdf_string(bytes: &[u8]) -> String {
    if bytes.len() >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF {
        let units: Vec<u16> = bytes[2..]
            .chunks_exact(2)
            .map(|pair| u16::from_be_bytes([pair[0], pair[1]]))
            .collect();
        return String::from_utf16_lossy(&units);
    }

    bytes.iter().map(|&b| b as char).collect()
}

/// Choice-field flag bit 22 (1-based): more than one option may be selected.
const FF_MULTISELECT: i64 = 1 << 21;

fn set_choice(doc: &mut Document, field_id: ObjectId, index: i32) -> Result<(), i32> {
    let wanted = usize::try_from(index).map_err(|_| STATUS_INVALID_INPUT)?;

    // ⚠️ REFUSED, not quietly narrowed. A multi-select list holds a LIST of
    // values, and writing one over it would throw away every other selection
    // the reader had made. Single selection is what this milestone supports, so
    // the honest answer is to decline rather than to lose their work.
    let flags = doc
        .get_dictionary(field_id)
        .ok()
        .and_then(|d| d.get(b"Ff").and_then(|o| o.as_i64()).ok())
        .unwrap_or(0);
    if flags & FF_MULTISELECT != 0 {
        return Err(STATUS_UNSUPPORTED);
    }

    let value = doc
        .get_dictionary(field_id)
        .ok()
        .and_then(|d| d.get(b"Opt").and_then(|o| o.as_array()).ok())
        .and_then(|opts| opts.get(wanted))
        .and_then(export_object)
        .ok_or(STATUS_INVALID_INPUT)?;

    if let Ok(dict) = doc.get_dictionary_mut(field_id) {
        // The option's own string object, byte for byte, so whatever encoding
        // the document was written in is the encoding it keeps.
        dict.set("V", value);
        // The selected INDEX as well as the value. A reader uses /I to know
        // which row to highlight, and two options can share a display label.
        dict.set("I", Object::Array(vec![Object::Integer(wanted as i64)]));
    }

    need_appearances(doc);
    Ok(())
}

/// Asks any reader to rebuild the appearances it can no longer trust.
///
/// Only choice fields need it. A checkbox and a radio already carry a stream for
/// every state and `/AS` just picks one, but a combo or list has a SINGLE stream
/// with the old label drawn into it, so without this the value changes and the
/// field goes on showing what it showed before. Measured on the PDFium we ship:
/// with the flag set, a combo reading "United Kingdom" came back "Myanmar".
fn need_appearances(doc: &mut Document) {
    let Some(acro) = doc
        .trailer
        .get(b"Root")
        .and_then(|o| o.as_reference())
        .ok()
        .and_then(|root| doc.get_dictionary(root).ok())
        .and_then(|catalog| catalog.get(b"AcroForm").ok())
        .cloned()
    else {
        return;
    };

    match acro {
        Object::Reference(id) => {
            if let Ok(dict) = doc.get_dictionary_mut(id) {
                dict.set("NeedAppearances", Object::Boolean(true));
            }
        }
        // An inline /AcroForm dictionary has to be put back through the catalog.
        Object::Dictionary(mut dict) => {
            dict.set("NeedAppearances", Object::Boolean(true));
            if let Ok(root) = doc.trailer.get(b"Root").and_then(|o| o.as_reference()) {
                if let Ok(catalog) = doc.get_dictionary_mut(root) {
                    catalog.set("AcroForm", Object::Dictionary(dict));
                }
            }
        }
        _ => {}
    }
}

/// The state a field is in, read back out of the bytes. Used by the read-back
/// check the writer performs on itself, and by the tests.
pub fn read_state(bytes: &[u8], field_name: &str) -> Option<(String, Vec<String>)> {
    let doc = Document::load_mem(bytes).ok()?;
    let field_id = find_field(&doc, field_name)?;
    let dict = doc.get_dictionary(field_id).ok()?;

    let value = match dict.get(b"V") {
        // A name is never text: it is an identifier, always ASCII here.
        Ok(Object::Name(n)) => String::from_utf8_lossy(n).into_owned(),
        Ok(Object::String(s, _)) => decode_pdf_string(s),
        _ => String::new(),
    };

    let states = widgets_of(&doc, field_id)
        .into_iter()
        .map(|w| {
            doc.get_dictionary(w)
                .ok()
                .and_then(|d| d.get(b"AS").ok())
                .and_then(|o| o.as_name().ok())
                .map(|n| String::from_utf8_lossy(n).into_owned())
                .unwrap_or_default()
        })
        .collect();

    Some((value, states))
}

/// Whether a dictionary key exists, for the widget filter above.
trait HasKey {
    fn has(&self, key: &[u8]) -> bool;
}

impl HasKey for Dictionary {
    fn has(&self, key: &[u8]) -> bool {
        self.get(key).is_ok()
    }
}

/// Turns this module's `Result` into the status codes the FFI speaks.
pub fn status_of(result: &Result<Vec<u8>, i32>) -> i32 {
    match result {
        Ok(_) => STATUS_OK_PDFIUM,
        Err(code) => *code,
    }
}
