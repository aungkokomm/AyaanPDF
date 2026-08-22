//! SPIKE. Not production, and not called by anything.
//!
//! THE QUESTION: can a gradient reach the file as real vector paint and come
//! back rendered correctly by the PDFium we actually ship?
//!
//! PDFium cannot create one. Its whole object-creation surface is
//! `FPDFPageObj_CreateNewPath`, `CreateNewRect`, `CreateTextObj` and
//! `NewImageObj`; the vendored `PdfPageShadingObject` has getters and no
//! constructor. So a gradient has to be written as PDF objects directly, with
//! lopdf, exactly as `outline.rs` writes bookmarks and for exactly the same
//! reason.
//!
//! That only helps if PDFium can READ what lopdf writes. Everything downstream
//! of that assumption, on screen and in print and in the thumbnail pane, is
//! PDFium rendering the saved file, so if it cannot the whole architecture
//! changes. These tests write the PDF by hand, save it, reopen it through
//! PDFium and MEASURE THE PIXELS.
//!
//! Three cases, in the order they matter:
//!
//! 1. A rectangle in page content. The simplest thing that can work.
//! 2. An ellipse and a rounded rectangle. Our shapes are not all rectangles,
//!    and a pattern that only fills axis-aligned boxes would be no use.
//! 3. The same gradient inside an ANNOTATION'S APPEARANCE STREAM, which is
//!    where our shapes actually live. This is the case the architecture rests
//!    on; the first two only tell us whether the format is right at all.
//!
//! Delete this file once the production path exists and is tested on its own.

#![cfg(test)]

use std::path::PathBuf;

use lopdf::content::{Content, Operation};
use lopdf::{dictionary, Document, Object, Stream};

use pdfium_render::prelude::*;

use crate::{lock, pdfium, CALL_LOCK};

/// The page is a square this many points on a side, so a horizontal gradient
/// runs the whole width and the sampling arithmetic stays obvious.
const SIDE: f32 = 200.0;

/// Red on the left, blue on the right. Chosen because the two are as far apart
/// as channels get, so "which end am I looking at" is never a judgement call.
const LEFT: [f32; 3] = [1.0, 0.0, 0.0];
const RIGHT: [f32; 3] = [0.0, 0.0, 1.0];

fn scratch(name: &str) -> PathBuf {
    let mut path = std::env::temp_dir();
    path.push(format!("ayaan-gradient-spike-{name}-{}.pdf", std::process::id()));
    path
}

/// The shading dictionary itself: an axial (type 2) shading from one colour to
/// another across the given x range, with a type 2 exponential function doing
/// the interpolation.
///
/// This is the whole of the PDF gradient vocabulary we need. Nothing here is
/// PDFium-specific; it is the plain spec representation.
fn axial_shading(x0: f32, x1: f32) -> Object {
    Object::Dictionary(dictionary! {
        "ShadingType" => 2,
        "ColorSpace" => "DeviceRGB",
        // Two points in the pattern's own space. The gradient runs between
        // them and Extend paints the ends flat beyond.
        "Coords" => Object::Array(vec![
            x0.into(), 0.0.into(), x1.into(), 0.0.into(),
        ]),
        "Extend" => Object::Array(vec![true.into(), true.into()]),
        "Function" => Object::Dictionary(dictionary! {
            "FunctionType" => 2,
            "Domain" => Object::Array(vec![0.0.into(), 1.0.into()]),
            "C0" => Object::Array(LEFT.iter().map(|c| Object::Real(*c)).collect::<Vec<_>>()),
            "C1" => Object::Array(RIGHT.iter().map(|c| Object::Real(*c)).collect::<Vec<_>>()),
            "N" => 1,
        }),
    })
}

/// A shading PATTERN wrapping that shading, which is what a fill operator can
/// name. PatternType 2 is a shading pattern; PatternType 1 would be a tiling
/// one, which is not what a gradient is.
fn shading_pattern(x0: f32, x1: f32) -> Object {
    Object::Dictionary(dictionary! {
        "Type" => "Pattern",
        "PatternType" => 2,
        "Shading" => axial_shading(x0, x1),
    })
}

/// The operators that fill the current path with the pattern.
///
/// `/Pattern cs` switches the fill colour space, `/P0 scn` selects the pattern
/// by name, and the ordinary fill operator does the rest. The PATH is what
/// clips the paint, which is the reason this works for any shape at all rather
/// than only for rectangles.
fn fill_with_pattern(ops: &mut Vec<Operation>) {
    ops.push(Operation::new("cs", vec!["Pattern".into()]));
    ops.push(Operation::new("scn", vec!["P0".into()]));
    ops.push(Operation::new("f", vec![]));
}

fn rectangle(x: f32, y: f32, w: f32, h: f32) -> Vec<Operation> {
    let mut ops = vec![Operation::new(
        "re",
        vec![x.into(), y.into(), w.into(), h.into()],
    )];
    fill_with_pattern(&mut ops);
    ops
}

/// Four Beziers, the way every drawing tool approximates one.
const KAPPA: f32 = 0.552_284_75;

fn ellipse(cx: f32, cy: f32, rx: f32, ry: f32) -> Vec<Operation> {
    let (kx, ky) = (rx * KAPPA, ry * KAPPA);
    let mut ops = vec![
        Operation::new("m", vec![(cx - rx).into(), cy.into()]),
        Operation::new("c", vec![
            (cx - rx).into(), (cy + ky).into(),
            (cx - kx).into(), (cy + ry).into(),
            cx.into(), (cy + ry).into()]),
        Operation::new("c", vec![
            (cx + kx).into(), (cy + ry).into(),
            (cx + rx).into(), (cy + ky).into(),
            (cx + rx).into(), cy.into()]),
        Operation::new("c", vec![
            (cx + rx).into(), (cy - ky).into(),
            (cx + kx).into(), (cy - ry).into(),
            cx.into(), (cy - ry).into()]),
        Operation::new("c", vec![
            (cx - kx).into(), (cy - ry).into(),
            (cx - rx).into(), (cy - ky).into(),
            (cx - rx).into(), cy.into()]),
        Operation::new("h", vec![]),
    ];
    fill_with_pattern(&mut ops);
    ops
}

fn rounded_rect(x: f32, y: f32, w: f32, h: f32, r: f32) -> Vec<Operation> {
    let k = r * KAPPA;
    let (l, b, rt, t) = (x, y, x + w, y + h);
    let mut ops = vec![
        Operation::new("m", vec![(l + r).into(), b.into()]),
        Operation::new("l", vec![(rt - r).into(), b.into()]),
        Operation::new("c", vec![
            (rt - r + k).into(), b.into(), rt.into(), (b + r - k).into(),
            rt.into(), (b + r).into()]),
        Operation::new("l", vec![rt.into(), (t - r).into()]),
        Operation::new("c", vec![
            rt.into(), (t - r + k).into(), (rt - r + k).into(), t.into(),
            (rt - r).into(), t.into()]),
        Operation::new("l", vec![(l + r).into(), t.into()]),
        Operation::new("c", vec![
            (l + r - k).into(), t.into(), l.into(), (t - r + k).into(),
            l.into(), (t - r).into()]),
        Operation::new("l", vec![l.into(), (b + r).into()]),
        Operation::new("c", vec![
            l.into(), (b + r - k).into(), (l + r - k).into(), b.into(),
            (l + r).into(), b.into()]),
        Operation::new("h", vec![]),
    ];
    fill_with_pattern(&mut ops);
    ops
}

/// Writes a one-page document whose CONTENT draws the given operators, with the
/// pattern in the page's own resources.
fn write_page_content(path: &PathBuf, ops: Vec<Operation>, x0: f32, x1: f32) {
    let mut doc = Document::with_version("1.7");

    let content = Content { operations: ops };
    let stream = doc.add_object(Stream::new(dictionary! {}, content.encode().unwrap()));

    let pages_id = doc.new_object_id();
    let resources = doc.add_object(dictionary! {
        "Pattern" => dictionary! { "P0" => shading_pattern(x0, x1) },
    });

    let page = doc.add_object(dictionary! {
        "Type" => "Page",
        "Parent" => pages_id,
        "Contents" => stream,
        "Resources" => resources,
        "MediaBox" => Object::Array(vec![
            0.into(), 0.into(), SIDE.into(), SIDE.into()]),
    });

    doc.objects.insert(pages_id, Object::Dictionary(dictionary! {
        "Type" => "Pages",
        "Kids" => Object::Array(vec![page.into()]),
        "Count" => 1,
    }));

    let catalog = doc.add_object(dictionary! {
        "Type" => "Catalog",
        "Pages" => pages_id,
    });
    doc.trailer.set("Root", catalog);

    doc.save(path).expect("the spike could not write its PDF");
}

/// The same, but the drawing lives in an ANNOTATION'S APPEARANCE STREAM, which
/// is where every shape this app writes actually lives.
///
/// The pattern goes in the form XObject's OWN resources, not the page's, which
/// is the part worth proving: an appearance stream is a self-contained thing
/// and PDFium has to resolve the pattern from inside it.
fn write_annotation_appearance(path: &PathBuf, ops: Vec<Operation>, x0: f32, x1: f32) {
    let mut doc = Document::with_version("1.7");

    let pages_id = doc.new_object_id();

    let ap_resources = doc.add_object(dictionary! {
        "Pattern" => dictionary! { "P0" => shading_pattern(x0, x1) },
    });

    let content = Content { operations: ops };
    let appearance = doc.add_object(Stream::new(
        dictionary! {
            "Type" => "XObject",
            "Subtype" => "Form",
            "FormType" => 1,
            "BBox" => Object::Array(vec![
                0.into(), 0.into(), SIDE.into(), SIDE.into()]),
            "Resources" => ap_resources,
        },
        content.encode().unwrap(),
    ));

    let annotation = doc.add_object(dictionary! {
        "Type" => "Annot",
        "Subtype" => "Square",
        "Rect" => Object::Array(vec![
            0.into(), 0.into(), SIDE.into(), SIDE.into()]),
        // 4 is the PRINT flag. Without it some renderers skip the annotation.
        "F" => 4,
        "AP" => dictionary! { "N" => appearance },
    });

    let empty = doc.add_object(Stream::new(dictionary! {}, Vec::new()));

    let page = doc.add_object(dictionary! {
        "Type" => "Page",
        "Parent" => pages_id,
        "Contents" => empty,
        "Resources" => dictionary! {},
        "Annots" => Object::Array(vec![annotation.into()]),
        "MediaBox" => Object::Array(vec![
            0.into(), 0.into(), SIDE.into(), SIDE.into()]),
    });

    doc.objects.insert(pages_id, Object::Dictionary(dictionary! {
        "Type" => "Pages",
        "Kids" => Object::Array(vec![page.into()]),
        "Count" => 1,
    }));

    let catalog = doc.add_object(dictionary! {
        "Type" => "Catalog",
        "Pages" => pages_id,
    });
    doc.trailer.set("Root", catalog);

    doc.save(path).expect("the spike could not write its PDF");
}

/// Reopens with PDFium and renders, returning (width, height, BGRA bytes).
///
/// THE POINT OF THE WHOLE SPIKE. Not "lopdf can read its own output", which
/// would prove nothing: the renderer that has to understand this is the one the
/// app ships.
///
/// THE CALLER HOLDS CALL_LOCK. PDFium is not thread safe and the whole crate
/// serialises on that mutex; these tests bypassed it at first and survived
/// until a sixth concurrent user, at which point the process died with an
/// illegal instruction and no stack. Taking it here instead would deadlock the
/// test that needs to save between two renders.
fn render(path: &PathBuf, width: i32) -> (i32, i32, Vec<u8>) {
    let pdfium = pdfium().expect("pdfium is not available");
    let doc = pdfium
        .load_pdf_from_file(path.to_str().unwrap(), None)
        .expect("PDFium refused to reopen the spike's own file");
    let page = doc.pages().get(0).unwrap();

    let config = PdfRenderConfig::new()
        .set_target_width(width)
        .set_reverse_byte_order(false)
        .rotate_if_landscape(PdfPageRenderRotation::None, false);

    let bitmap = page.render_with_config(&config).unwrap();

    (bitmap.width() as i32, bitmap.height() as i32, bitmap.as_raw_bytes().to_vec())
}

/// One pixel as (blue, green, red).
fn at(px: &[u8], w: i32, x: i32, y: i32) -> (u8, u8, u8) {
    let i = ((y * w + x) * 4) as usize;
    (px[i], px[i + 1], px[i + 2])
}

/// Decisively the first colour: red HIGH and blue LOW.
///
/// Both halves matter. "Red is above 180" is true of WHITE PAPER, so a test
/// written that way passes on a page where nothing was drawn at all, which is
/// how three of these first passed with the appearance stream removed.
fn is_left_colour(p: (u8, u8, u8)) -> bool {
    let (b, _, r) = p;
    r > 180 && b < 80
}

/// Decisively the second colour: blue HIGH and red LOW. Same reasoning.
fn is_right_colour(p: (u8, u8, u8)) -> bool {
    let (b, _, r) = p;
    b > 180 && r < 80
}

fn is_paper(p: (u8, u8, u8)) -> bool {
    let (b, g, r) = p;
    b > 240 && g > 240 && r > 240
}

// ---------------------------------------------------------------------------

#[test]
fn spike_1_pdfium_renders_a_gradient_lopdf_wrote() {
    let _guard = lock(&CALL_LOCK);
    let path = scratch("rect");
    write_page_content(&path, rectangle(0.0, 0.0, SIDE, SIDE), 0.0, SIDE);

    let (w, h, px) = render(&path, 200);
    let mid = h / 2;

    let (lb, lg, lr) = at(&px, w, 4, mid);
    let (cb, cg, cr) = at(&px, w, w / 2, mid);
    let (rb, rg, rr) = at(&px, w, w - 5, mid);

    // The left end is the first colour, the right end the second, and the
    // middle is genuinely between them rather than one or the other.
    assert!(lr > 200 && lb < 60, "the left end is not red: {lr},{lg},{lb}");
    assert!(rb > 200 && rr < 60, "the right end is not blue: {rr},{rg},{rb}");
    assert!(
        cr > 60 && cr < 200 && cb > 60 && cb < 200,
        "the middle is not a blend, so this is two flat fills and not a gradient: {cr},{cg},{cb}");

    // And it really is a RAMP: red falls and blue rises across the page.
    assert!(lr > cr && cr > rr, "red does not fall: {lr} {cr} {rr}");
    assert!(lb < cb && cb < rb, "blue does not rise: {lb} {cb} {rb}");

    let _ = std::fs::remove_file(&path);
}

#[test]
fn spike_2_the_gradient_is_clipped_by_an_arbitrary_path() {
    let _guard = lock(&CALL_LOCK);
    // Our shapes are not all rectangles. The fill operator paints inside the
    // CURRENT PATH whatever that path is, so an ellipse should be filled and
    // its corners should be left alone.
    let path = scratch("ellipse");
    write_page_content(
        &path,
        ellipse(SIDE / 2.0, SIDE / 2.0, SIDE / 2.0 - 2.0, SIDE / 2.0 - 2.0),
        0.0,
        SIDE,
    );

    let (w, h, px) = render(&path, 200);
    let mid = h / 2;

    let left = at(&px, w, 6, mid);
    let right = at(&px, w, w - 7, mid);
    let corner = at(&px, w, 3, 3);

    assert!(is_left_colour(left), "the ellipse's left is not red: {left:?}");
    assert!(is_right_colour(right), "the ellipse's right is not blue: {right:?}");
    assert!(
        is_paper(corner),
        "the corner outside the ellipse was painted, so the path did not clip: {corner:?}");

    let _ = std::fs::remove_file(&path);
}

#[test]
fn spike_2b_a_rounded_rectangle_is_filled_too() {
    let _guard = lock(&CALL_LOCK);
    let path = scratch("rounded");
    write_page_content(&path, rounded_rect(2.0, 2.0, SIDE - 4.0, SIDE - 4.0, 40.0), 0.0, SIDE);

    let (w, h, px) = render(&path, 200);
    let mid = h / 2;

    let left = at(&px, w, 6, mid);
    let right = at(&px, w, w - 7, mid);
    let corner = at(&px, w, 2, 2);

    assert!(is_left_colour(left), "the rounded rect's left is not red: {left:?}");
    assert!(is_right_colour(right), "the rounded rect's right is not blue: {right:?}");
    assert!(is_paper(corner), "the rounded corner was painted square: {corner:?}");

    let _ = std::fs::remove_file(&path);
}

#[test]
fn spike_3_the_gradient_works_inside_an_annotation_appearance_stream() {
    let _guard = lock(&CALL_LOCK);
    // THE ONE THE ARCHITECTURE RESTS ON. Every shape this app writes is an
    // annotation, and its appearance stream is a form XObject with resources of
    // its own. If PDFium cannot resolve a pattern from in there, the whole
    // save-time plan is wrong and the answer has to be something else.
    let path = scratch("annot");
    write_annotation_appearance(&path, rectangle(20.0, 20.0, SIDE - 40.0, SIDE - 40.0), 20.0, SIDE - 20.0);

    let (w, h, px) = render(&path, 200);
    let mid = h / 2;

    let (lb, lg, lr) = at(&px, w, w / 10 + 2, mid);
    let (cb, cg, cr) = at(&px, w, w / 2, mid);
    let (rb, rg, rr) = at(&px, w, w - w / 10 - 3, mid);

    assert!(lr > 200 && lb < 60, "the annotation's left end is not red: {lr},{lg},{lb}");
    assert!(rb > 200 && rr < 60, "the annotation's right end is not blue: {rr},{rg},{rb}");
    assert!(
        cr > 60 && cr < 200 && cb > 60 && cb < 200,
        "the annotation's middle is not a blend: {cr},{cg},{cb}");

    let _ = std::fs::remove_file(&path);
}

#[test]
fn spike_4_an_ellipse_inside_an_annotation_appearance_stream() {
    let _guard = lock(&CALL_LOCK);
    // The two hard parts together, which is what production actually needs.
    let path = scratch("annot-ellipse");
    write_annotation_appearance(
        &path,
        ellipse(SIDE / 2.0, SIDE / 2.0, SIDE / 2.0 - 10.0, SIDE / 2.0 - 10.0),
        10.0,
        SIDE - 10.0,
    );

    let (w, h, px) = render(&path, 200);
    let mid = h / 2;

    let left = at(&px, w, 14, mid);
    let right = at(&px, w, w - 15, mid);
    let corner = at(&px, w, 3, 3);

    assert!(is_left_colour(left), "the annotated ellipse's left is not red: {left:?}");
    assert!(is_right_colour(right), "the annotated ellipse's right is not blue: {right:?}");
    assert!(
        is_paper(corner),
        "the corner outside the annotated ellipse was painted: {corner:?}");

    let _ = std::fs::remove_file(&path);
}

#[test]
fn spike_5_the_pattern_survives_a_save_by_pdfium_itself() {
    let _guard = lock(&CALL_LOCK);
    // THE PRODUCTION ROUND TRIP. The app saves through PDFium, so a document
    // that already carries a gradient will be opened, edited and saved again by
    // it. If PDFium drops or flattens somebody else's shading on the way
    // through, the writing pass has to be the LAST thing that touches a save
    // rather than something that can run once and be relied on.
    //
    // The tag stays authoritative either way, so this is survivable news; it is
    // still better known now than found later.
    let written = scratch("survives-src");
    write_annotation_appearance(
        &written, rectangle(20.0, 20.0, SIDE - 40.0, SIDE - 40.0), 20.0, SIDE - 20.0);

    let resaved = scratch("survives-dst");
    {
        let pdfium = pdfium().expect("pdfium is not available");
        let doc = pdfium
            .load_pdf_from_file(written.to_str().unwrap(), None)
            .expect("PDFium refused to open the spike's file");
        doc.save_to_file(&resaved).expect("PDFium refused to save");
    }

    let (w, h, px) = render(&resaved, 200);
    let mid = h / 2;

    let left = at(&px, w, w / 10 + 2, mid);
    let centre = at(&px, w, w / 2, mid);
    let right = at(&px, w, w - w / 10 - 3, mid);

    assert!(is_left_colour(left), "PDFium's own save lost the left end: {left:?}");
    assert!(is_right_colour(right), "PDFium's own save lost the right end: {right:?}");

    let (cb, _, cr) = centre;
    assert!(
        cr > 60 && cr < 200 && cb > 60 && cb < 200,
        "PDFium's own save flattened the gradient: {centre:?}");

    let _ = std::fs::remove_file(&written);
    let _ = std::fs::remove_file(&resaved);
}
