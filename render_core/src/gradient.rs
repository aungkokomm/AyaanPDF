//! Writing a shape's gradient fill into the file, as real PDF vector paint.
//!
//! THE SECOND THING PDFIUM CANNOT DO. Its whole object-creation surface is
//! `FPDFPageObj_CreateNewPath`, `CreateNewRect`, `CreateTextObj` and
//! `NewImageObj`; there is no call anywhere that makes a shading or a pattern,
//! and the vendored `PdfPageShadingObject` has getters and no constructor. So a
//! gradient is written as PDF objects directly, with lopdf, exactly as
//! `outline.rs` writes bookmarks and for exactly the same reason.
//!
//! The representation is the one the spike measured against the PDFium we ship:
//! an axial `ShadingType 2` in `DeviceRGB` with a `FunctionType 2` exponential
//! interpolation, wrapped in a `PatternType 2` pattern, placed in the
//! appearance stream's own `/Pattern` resources and selected with
//! `/Pattern cs`, `/P0 scn` before an `f`.
//!
//! THE TAG IS THE AUTHORITY. Nothing here reads a pixel, and nothing here knows
//! the live renderer exists. The shape's stored gradient says which colours and
//! which two endpoints, and the appearance stream PDFium already wrote says
//! where the shape is. Between them there is no third opinion.
//!
//! THE STROKE IS NOT TOUCHED. The gradient is drawn as a new fill PREPENDED to
//! the appearance stream rather than by rewriting what is in it, so the shape
//! PDFium drew comes out byte for byte the same and lands on top of the paint,
//! which is the order PDF's own combined operator would have used.
//!
//! The file is never edited in place. The caller passes a separate destination
//! and swaps the files itself, the same discipline the outline writer and the
//! annotation save path already follow, and for the same reason: writing over
//! the file you loaded from is how a document gets destroyed.

use std::ffi::{c_char, CStr};
use std::panic;

use lopdf::content::{Content, Operation};
use lopdf::{dictionary, Dictionary, Document, Object, ObjectId};

use crate::{
    effect_fields, parse_shape_tag, STATUS_INVALID_INPUT, STATUS_OK_PDFIUM, STATUS_PANIC,
    STATUS_UNSUPPORTED,
};

/// The name the pattern is registered under in an appearance's resources.
///
/// One per appearance stream, and each shape has an appearance of its own, so
/// there is nothing for it to collide with. A stream that somehow already has a
/// `/P0` is one we did not write, and it is left alone: this only ever adds a
/// pattern to an appearance whose annotation carries one of our shape tags.
const PATTERN_NAME: &str = "P0";

/// A gradient exactly as the shape's tag stores it: two colours and two
/// endpoints, the endpoints being fractions of the shape's own upright box with
/// y running DOWN, which is the model's convention everywhere.
#[derive(Clone, Copy, Debug, PartialEq)]
struct Gradient {
    from: [f32; 3],
    to: [f32; 3],
    x0: f32,
    y0: f32,
    x1: f32,
    y1: f32,
}

/// One shape's upright box in the appearance stream's own coordinates, in
/// points, y running UP as PDF does.
#[derive(Clone, Copy, Debug)]
struct Box2 {
    left: f32,
    bottom: f32,
    right: f32,
    top: f32,
}

/// Writes every gradient-filled shape's paint into the document.
///
/// Reads `src_path`, writes the result to `dst_path`, and leaves the source
/// untouched. `out_written` receives how many gradients were written; when that
/// is zero NOTHING IS WRITTEN AT ALL and `dst_path` is not created, so the
/// caller can skip the file swap and an ordinary document pays only for the
/// read.
///
/// Returns STATUS_OK_PDFIUM, STATUS_INVALID_INPUT (bad arguments),
/// STATUS_UNSUPPORTED (the file could not be parsed or written, which includes
/// encrypted documents), or STATUS_PANIC.
///
/// # Safety
/// `src_path` and `dst_path` must be valid NUL-terminated C strings, and
/// `out_written` must be null or point to a writable `i32`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn write_gradients(
    src_path: *const c_char,
    dst_path: *const c_char,
    out_written: *mut i32,
) -> i32 {
    if src_path.is_null() || dst_path.is_null() {
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

    let mut written = 0i32;
    let status = panic::catch_unwind(|| write_gradients_inner(&src, &dst))
        .unwrap_or(Err(STATUS_PANIC));

    let status = match status {
        Ok(count) => {
            written = count;
            STATUS_OK_PDFIUM
        }
        Err(code) => code,
    };

    if !out_written.is_null() {
        unsafe { *out_written = written };
    }

    status
}

fn write_gradients_inner(src: &str, dst: &str) -> Result<i32, i32> {
    let mut doc = Document::load(src).map_err(|_| STATUS_UNSUPPORTED)?;

    // Read first, edit second. An appearance stream is a top-level object and
    // the annotation dictionaries that point at it are often INLINE in the
    // page's /Annots array, so walking and mutating at once would mean holding
    // a borrow of the page while writing the stream.
    let work = gradients_to_write(&doc);
    if work.is_empty() {
        return Ok(0);
    }

    let mut written = 0;
    for (stream_id, gradient) in work {
        if paint_one(&mut doc, stream_id, gradient) {
            written += 1;
        }
    }

    if written == 0 {
        return Ok(0);
    }

    doc.save(dst).map_err(|_| STATUS_UNSUPPORTED)?;

    Ok(written)
}

/// Every appearance stream that needs a gradient painting into it, with the
/// gradient its annotation asked for.
fn gradients_to_write(doc: &Document) -> Vec<(ObjectId, Gradient)> {
    let mut work = Vec::new();

    for (_, page_id) in doc.get_pages() {
        let Ok(page) = doc.get_dictionary(page_id) else {
            continue;
        };
        let Some(annots) = array_of(doc, page.get(b"Annots").ok()) else {
            continue;
        };

        for annot in annots {
            let Some(dict) = dict_of(doc, Some(&annot)) else {
                continue;
            };
            let Some(gradient) = gradient_of(&dict) else {
                continue;
            };
            let Some(stream_id) = appearance_of(&dict) else {
                continue;
            };

            work.push((stream_id, gradient));
        }
    }

    work
}

/// The gradient one of our shapes carries, or None for anything else: another
/// editor's annotation, one of ours with no gradient, or a gradient this build
/// cannot faithfully write.
fn gradient_of(annot: &Dictionary) -> Option<Gradient> {
    // The tag lives under a private key so that readers do not show it as a
    // comment. Older files kept it in /Contents, and those are still editable,
    // so both are read here exactly as the rest of the core reads them.
    let tag = string_of(annot, b"AyaanTag").or_else(|| string_of(annot, b"Contents"))?;

    let effects = parse_shape_tag(&tag)?.13;

    effect_fields(&effects).find_map(parse_gradient_field)
}

/// Reads one `f(c=,c2=,x0=,y0=,x1=,y1=)` field, the Rust half of
/// `ShapeFillTag.Read`.
///
/// ONLY OPAQUE STOPS, for now, and a translucent one is refused rather than
/// flattened. A PDF shading carries no alpha of its own: expressing one needs a
/// constant `/ca` in an ExtGState when both stops share it, and a luminosity
/// soft mask when they do not. Neither is written yet, and writing an opaque
/// gradient where a translucent one was asked for would be exactly the silent
/// simplification this whole feature is built to avoid. The tag keeps its
/// definition either way; only the appearance goes unpainted.
fn parse_gradient_field(field: &str) -> Option<Gradient> {
    let inner = field.strip_prefix("f(")?.strip_suffix(')')?;

    let (mut from, mut to) = (None, None);
    let (mut x0, mut y0, mut x1, mut y1) = (None, None, None, None);

    for pair in inner.split(',') {
        let (key, value) = pair.split_once('=')?;
        match key {
            "c" => from = Some(opaque_rgb(value)?),
            "c2" => to = Some(opaque_rgb(value)?),
            "x0" => x0 = Some(finite(value)?),
            "y0" => y0 = Some(finite(value)?),
            "x1" => x1 = Some(finite(value)?),
            "y1" => y1 = Some(finite(value)?),
            // A key from a later build is skipped, not fatal, exactly as an
            // effect field's is.
            _ => {}
        }
    }

    let gradient = Gradient {
        from: from?,
        to: to?,
        x0: x0?,
        y0: y0?,
        x1: x1?,
        y1: y1?,
    };

    // Two endpoints in the same place have no direction, and a shading would
    // divide by that length.
    if gradient.x0 == gradient.x1 && gradient.y0 == gradient.y1 {
        return None;
    }

    Some(gradient)
}

/// An "AARRGGBB" stop as three components in 0..1, or None when it is not fully
/// opaque. See `parse_gradient_field`.
fn opaque_rgb(value: &str) -> Option<[f32; 3]> {
    if value.len() != 8 {
        return None;
    }

    let packed = u32::from_str_radix(value, 16).ok()?;
    if packed >> 24 != 0xFF {
        return None;
    }

    Some([
        ((packed >> 16) & 0xFF) as f32 / 255.0,
        ((packed >> 8) & 0xFF) as f32 / 255.0,
        (packed & 0xFF) as f32 / 255.0,
    ])
}

fn finite(value: &str) -> Option<f32> {
    value.parse::<f32>().ok().filter(|v| v.is_finite())
}

/// Paints one gradient into one appearance stream. False when the stream turned
/// out not to hold a fillable path after all, which leaves it untouched.
fn paint_one(doc: &mut Document, stream_id: ObjectId, gradient: Gradient) -> bool {
    let Ok(stream) = doc.get_object(stream_id).and_then(|o| o.as_stream()) else {
        return false;
    };
    let Ok(content) = stream.get_plain_content() else {
        return false;
    };
    let Ok(decoded) = Content::decode(&content) else {
        return false;
    };

    let Some(shape) = shape_drawing(&decoded.operations) else {
        return false;
    };

    let Some(box2) = path_box(&decoded.operations[shape.clone()]) else {
        return false;
    };

    let matrix = current_matrix(&decoded.operations[shape.clone()]);

    // The paint goes FIRST and the original drawing follows it untouched, so
    // the stroke PDFium wrote lands on top of the fill. That is the order PDF's
    // own fill-and-stroke operator uses, and it is why the shape does not come
    // out half a stroke thinner than it is.
    let mut operations = Vec::with_capacity(decoded.operations.len() + 6);
    operations.extend_from_slice(&decoded.operations[shape.clone()]);
    operations.push(Operation::new("cs", vec!["Pattern".into()]));
    operations.push(Operation::new("scn", vec![PATTERN_NAME.into()]));
    operations.push(Operation::new("f", vec![]));
    operations.push(Operation::new("Q", vec![]));
    operations.extend_from_slice(&decoded.operations);

    let Ok(encoded) = (Content { operations }).encode() else {
        return false;
    };

    let pattern = shading_pattern(gradient, box2, matrix);

    let Ok(stream) = doc
        .get_object_mut(stream_id)
        .and_then(|o| o.as_stream_mut())
    else {
        return false;
    };

    add_pattern(&mut stream.dict, pattern);
    stream.set_plain_content(encoded);

    true
}

/// Where in the operations the shape's own drawing is: from its `q` up to but
/// not including the operator that paints it.
///
/// THE FIRST PATH-PAINTING OPERATOR is the shape's, because the shape is the
/// only path in one of our appearances. An image drawn beside it, which is what
/// a soft shadow is, paints with `Do` and is not one of these.
///
/// Taking the whole `q` block rather than only the path operators means the
/// colour, the width and above all the `cm` that turns a rotated shape come
/// along with it, so the copy is positioned exactly as the original is without
/// any of that being understood here.
fn shape_drawing(ops: &[Operation]) -> Option<std::ops::Range<usize>> {
    let paint = ops.iter().position(|op| {
        matches!(
            op.operator.as_str(),
            "S" | "s" | "f" | "F" | "f*" | "B" | "B*" | "b" | "b*" | "n"
        )
    })?;

    let start = ops[..paint]
        .iter()
        .rposition(|op| op.operator == "q")
        .unwrap_or(0);

    Some(start..paint)
}

/// The box the path's own coordinates span, in the stream's units.
///
/// EVERY COORDINATE COUNTS, control points included. For the three shapes that
/// can be filled that is exact: a rectangle is its corners, and the quarter
/// arcs an ellipse and a rounded rectangle are built from put their control
/// points ON the box's edges, never outside it. A general Bezier could reach
/// past its own hull, and if a kind ever arrives that does, this is where it
/// would have to start measuring the curve instead.
fn path_box(ops: &[Operation]) -> Option<Box2> {
    let (mut left, mut bottom) = (f32::MAX, f32::MAX);
    let (mut right, mut top) = (f32::MIN, f32::MIN);
    let mut any = false;

    for op in ops {
        let coords: &[Object] = match op.operator.as_str() {
            "m" | "l" | "c" | "v" | "y" => &op.operands,
            "re" => {
                // x y w h, which is a corner and a size rather than two points.
                let n = numbers(&op.operands);
                if n.len() != 4 {
                    continue;
                }
                for (x, y) in [(n[0], n[1]), (n[0] + n[2], n[1] + n[3])] {
                    left = left.min(x);
                    right = right.max(x);
                    bottom = bottom.min(y);
                    top = top.max(y);
                    any = true;
                }
                continue;
            }
            _ => continue,
        };

        let n = numbers(coords);
        for pair in n.chunks_exact(2) {
            left = left.min(pair[0]);
            right = right.max(pair[0]);
            bottom = bottom.min(pair[1]);
            top = top.max(pair[1]);
            any = true;
        }
    }

    if !any || right <= left || top <= bottom {
        return None;
    }

    Some(Box2 { left, bottom, right, top })
}

/// The `cm` in the shape's block, if it has one.
///
/// A PATTERN IS NOT AFFECTED BY THE CURRENT TRANSFORM. Its own matrix maps
/// pattern space to the form's default space, so a rotated shape, whose path is
/// written upright and turned by a `cm`, needs that same matrix on the pattern
/// or the gradient would sit upright inside a turned shape. Giving the pattern
/// the stream's own `cm` puts pattern space exactly where the path's
/// coordinates are, whatever they are, without the rotation being re-derived
/// from anything.
fn current_matrix(ops: &[Operation]) -> Option<[f32; 6]> {
    for op in ops {
        if op.operator != "cm" {
            continue;
        }

        let n = numbers(&op.operands);
        if n.len() == 6 {
            return Some([n[0], n[1], n[2], n[3], n[4], n[5]]);
        }
    }

    None
}

fn numbers(operands: &[Object]) -> Vec<f32> {
    operands
        .iter()
        .filter_map(|o| match o {
            Object::Integer(i) => Some(*i as f32),
            Object::Real(r) => Some(*r),
            _ => None,
        })
        .collect()
}

/// The shading pattern a gradient becomes, with its endpoints resolved against
/// the shape's box.
///
/// THE ONE COORDINATE CONVERSION, and the only place the model's fractions meet
/// the file. The tag stores 0..1 across the shape's upright box with y running
/// DOWN, because that is what survives a move and a resize with nothing written
/// for either; a PDF runs y UP, so the vertical fraction is measured from the
/// top down rather than from the origin up. Nothing is stored in page
/// coordinates and nothing about the tag changes when the shape is moved,
/// resized or turned.
fn shading_pattern(gradient: Gradient, box2: Box2, matrix: Option<[f32; 6]>) -> Object {
    let w = box2.right - box2.left;
    let h = box2.top - box2.bottom;

    let at = |gx: f32, gy: f32| (box2.left + (gx * w), box2.top - (gy * h));

    let (x0, y0) = at(gradient.x0, gradient.y0);
    let (x1, y1) = at(gradient.x1, gradient.y1);

    let mut pattern = dictionary! {
        "Type" => "Pattern",
        "PatternType" => 2,
        "Shading" => Object::Dictionary(dictionary! {
            "ShadingType" => 2,
            "ColorSpace" => "DeviceRGB",
            "Coords" => Object::Array(vec![
                x0.into(), y0.into(), x1.into(), y1.into(),
            ]),
            // Past either end the paint is that end's own colour rather than
            // nothing, which is what the live renderer's clamped shader does.
            "Extend" => Object::Array(vec![true.into(), true.into()]),
            "Function" => Object::Dictionary(dictionary! {
                "FunctionType" => 2,
                "Domain" => Object::Array(vec![0.0.into(), 1.0.into()]),
                "C0" => Object::Array(
                    gradient.from.iter().map(|c| Object::Real(*c)).collect::<Vec<_>>()),
                "C1" => Object::Array(
                    gradient.to.iter().map(|c| Object::Real(*c)).collect::<Vec<_>>()),
                "N" => 1,
            }),
        }),
    };

    if let Some(m) = matrix {
        pattern.set(
            "Matrix",
            Object::Array(m.iter().map(|v| Object::Real(*v)).collect::<Vec<_>>()),
        );
    }

    Object::Dictionary(pattern)
}

/// Puts the pattern in the appearance's own resources, leaving whatever else is
/// in there alone.
///
/// The appearance's, not the page's. An annotation's form XObject resolves
/// names against its own /Resources, which is the case the spike proved and the
/// one the whole architecture rests on.
fn add_pattern(dict: &mut Dictionary, pattern: Object) {
    let mut resources = dict
        .get(b"Resources")
        .and_then(|o| o.as_dict())
        .cloned()
        .unwrap_or_default();

    let mut patterns = resources
        .get(b"Pattern")
        .and_then(|o| o.as_dict())
        .cloned()
        .unwrap_or_default();

    patterns.set(PATTERN_NAME, pattern);
    resources.set("Pattern", Object::Dictionary(patterns));
    dict.set("Resources", Object::Dictionary(resources));
}

/// The object id of an annotation's normal appearance stream.
///
/// A stream is always indirect in PDF, so this is a reference or it is not an
/// appearance we can paint into. `/N` may also be a dictionary of named states,
/// which none of ours is: those belong to widgets and are left alone.
fn appearance_of(annot: &Dictionary) -> Option<ObjectId> {
    match annot.get(b"AP").ok()?.as_dict().ok()?.get(b"N").ok()? {
        Object::Reference(id) => Some(*id),
        _ => None,
    }
}

/// A dictionary that may be written inline or referenced. PDFium writes our
/// annotations inline, which is legal and which every reader below has to cope
/// with.
fn dict_of(doc: &Document, object: Option<&Object>) -> Option<Dictionary> {
    match object? {
        Object::Dictionary(d) => Some(d.clone()),
        Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
        _ => None,
    }
}

/// An array that may be written inline or referenced, on the same terms.
fn array_of(doc: &Document, object: Option<&Object>) -> Option<Vec<Object>> {
    match object? {
        Object::Array(a) => Some(a.clone()),
        Object::Reference(id) => doc.get_object(*id).ok()?.as_array().ok().cloned(),
        _ => None,
    }
}

/// A dictionary's string entry as UTF-8, or None when it is absent or is not
/// text. Our tags are written through PDFium as UTF-16 and come back out as
/// plain bytes whenever every character fits, which is always: a shape tag is
/// ASCII by construction.
fn string_of(dict: &Dictionary, key: &[u8]) -> Option<String> {
    match dict.get(key).ok()? {
        Object::String(bytes, _) => String::from_utf8(bytes.clone()).ok(),
        _ => None,
    }
}
