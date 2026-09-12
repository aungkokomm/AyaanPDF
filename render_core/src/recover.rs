//! Recovering the text of a page whose own tables cannot be trusted.
//!
//! ⚠️ THIS IS THE CALLER [`crate::reshape`] WAS WRITTEN FOR. That module turns
//! a run of glyphs into the text that drew them, and deliberately answers
//! nothing about the page: where a line ends, and where the words are, are
//! questions about PDF structure rather than about a font. They are answered
//! here.
//!
//! ⚠️ A LINE IS FOUND BY THE TEXT MATRIX, NOT BY A GAP IN THE y VALUES. The
//! matrix is what the page itself uses to place a line, so it is the only
//! answer that cannot drift: `Td` and `T*` are RELATIVE, and a reader that
//! watches only `Tm` misses every line a producer moves to by leading.
//!
//! ⚠️ REACHED ONLY THROUGH `recover_page_text`, AND ON PURPOSE. Building the
//! index of what a font draws takes about 17 seconds for a page, so this is a
//! call a caller makes deliberately, for a page it already knows is refused.
//! `get_page_lines` still reads through the file's own tables and still refuses
//! the result, correctly: those tables really are wrong.
//!
//! ⚠️ WHAT IS STILL MISSING IS NOT HERE. An editor needs a position for each
//! CHARACTER, and a line's reading does not carry one: the glyphs a page draws
//! and the characters they spell are not in the same order and are not the same
//! count. That is the next problem, and it does not change what this answers.
#![allow(dead_code)]

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use lopdf::{Document, Object, ObjectId};

/// The fonts this can read, by the name a PDF gives them once the subset prefix
/// is stripped.
///
/// ⚠️ DELIBERATELY TWO FAMILIES. Recovering text needs the INSTALLED font, and
/// being wrong about which one silently produces glyphs that do not match, so
/// the answer is a short list of fonts whose behaviour has been measured rather
/// than a search of the system. Anything else is refused, which costs a reader
/// nothing they had before.
/// A base font's name without the subset tag a producer puts in front of it.
///
/// ⚠️ TWO SUBSETS OF ONE FACE ARE ONE FACE. Measured on a real page:
/// its body text alternates between `BCDEEE+MyanmarText` and
/// `BCDGEE+MyanmarText` line by line, so anything comparing the names as
/// written sees a different font every other line. That split one paragraph of
/// nine lines into six.
pub(crate) fn family_of(base_font: &str) -> &str {
    base_font.rsplit('+').next().unwrap_or(base_font)
}

fn installed(base_font: &str) -> Option<&'static str> {
    // "BCDEEE+MyanmarText" is one font, wearing a subset tag.
    let name = family_of(base_font);

    // ⚠️ A COMMA IS AS GOOD AS A HYPHEN, and measured on a real file it is what
    // the producer used: the page names its bold `ABCDEE+Pyidaungsu,Bold`.
    // Splitting on the hyphen alone took the whole of that as a family name,
    // matched nothing, and refused every bold line on the page before it began.
    let (family, bold) = match name.split_once(|c| c == '-' || c == ',') {
        Some((f, style)) => (f, style.eq_ignore_ascii_case("bold")),
        None => (name, false),
    };

    let (regular, heavy) = match family {
        "MyanmarText" => (
            r"C:\Windows\Fonts\mmrtext.ttf",
            r"C:\Windows\Fonts\mmrtextb.ttf",
        ),
        "Pyidaungsu" => (
            r"C:\Windows\Fonts\Pyidaungsu.ttf",
            r"C:\Windows\Fonts\Pyidaungsu-Bold.ttf",
        ),
        // The Devanagari faces answer for themselves, bold and all.
        _ => return crate::devanagari::face_named(family, bold),
    };
    if !bold {
        return Some(regular);
    }

    // ⚠️ AND THE FAMILY'S REGULAR FILE WILL DO WHEN THERE IS NO BOLD ONE.
    // Measured: on the machine this was written on there is no
    // `Pyidaungsu-Bold.ttf` at all, and the regular file proves 9 of the 10
    // bold lines of a real page, because the two weights of that family are
    // built from one source and number their glyphs alike.
    //
    // ⚠️ THIS CANNOT PRODUCE A WRONG READING, which is the whole reason it is
    // allowed. A line is proven by shaping candidate text through this font and
    // demanding the page's own glyph ids back, so a font that does not match
    // yields a REFUSAL and never a misreading. The worst case of guessing here
    // is the refusal we already had.
    Some(if std::path::Path::new(heavy).exists() { heavy } else { regular })
}

/// A 3x2 PDF matrix, as its six written numbers.
#[derive(Clone, Copy, Debug)]
struct Matrix([f64; 6]);

impl Matrix {
    const IDENTITY: Matrix = Matrix([1.0, 0.0, 0.0, 1.0, 0.0, 0.0]);

    fn translation(tx: f64, ty: f64) -> Matrix {
        Matrix([1.0, 0.0, 0.0, 1.0, tx, ty])
    }

    /// `self` applied first, then `then`.
    fn then(self, then: Matrix) -> Matrix {
        let [a1, b1, c1, d1, e1, f1] = self.0;
        let [a2, b2, c2, d2, e2, f2] = then.0;
        Matrix([
            a1 * a2 + b1 * c2,
            a1 * b2 + b1 * d2,
            c1 * a2 + d1 * c2,
            c1 * b2 + d1 * d2,
            e1 * a2 + f1 * c2 + e2,
            e1 * b2 + f1 * d2 + f2,
        ])
    }

    /// Where this matrix puts the origin.
    fn y(&self) -> f64 {
        self.0[5]
    }

    fn x(&self) -> f64 {
        self.0[4]
    }

    /// Where this matrix puts a point.
    fn on(&self, x: f64, y: f64) -> (f64, f64) {
        let [a, b, c, d, e, f] = self.0;
        (a * x + c * y + e, b * x + d * y + f)
    }

    /// Where this matrix sends a direction, with no translation.
    ///
    /// ⚠️ A LENGTH IS NOT A POINT. A reach above a baseline is a direction,
    /// and putting it through `on` would add the page's own offset to it.
    fn along(&self, x: f64, y: f64) -> (f64, f64) {
        let [a, b, c, d, _, _] = self.0;
        (a * x + c * y, b * x + d * y)
    }

    /// How much this matrix scales a vertical distance.
    ///
    /// A flip reports the same number as the lift it reverses: a height is a
    /// length, and a length has no sign.
    fn vertical_scale(&self) -> f64 {
        let [_, b, _, d, _, _] = self.0;
        (b * b + d * d).sqrt()
    }
}

/// One line of a page, as the glyphs it draws.
pub(crate) struct Line {
    /// Where the line sits, from the text matrix that placed it.
    ///
    /// ⚠️ IN THE TEXT OBJECT'S OWN SPACE, WHICH IS NOT THE PAGE'S. This is the
    /// `Tm` translation as the stream writes it, with nothing in force over it
    /// applied. Comparing lines of one page against each other is exactly what
    /// it is for; addressing a line from OUTSIDE the stream is what it is not.
    /// Use [`Line::page_y`] for that.
    pub(crate) y: f64,
    pub(crate) x: f64,

    /// Where the line sits on the PAGE: the same placement with whatever
    /// transform is in force where it is drawn applied to it.
    ///
    /// ⚠️ THIS IS THE ONLY ADDRESS AN OUTSIDE CALLER MAY USE, and it exists
    /// because the app addresses a line by its baseline and reads that baseline
    /// from PDFium, which reports the page. Measured: on a book whose text is
    /// drawn under `cm [0.72 0 0 -0.72 72 841.9]` the two frames disagree by
    /// three to ten points against a tolerance of half a point, so ZERO of that
    /// page's thirty-two lines could be found and every move of every line of
    /// that book was refused.
    ///
    /// Equal to `y` on any page that draws its text without a transform, which
    /// is why this went unnoticed: the Myanmar test file is one of those.
    pub(crate) page_y: f64,
    /// The transform in force where this line is drawn.
    ///
    /// ⚠️ KEPT WHOLE, because `page_y` alone is not enough. The same
    /// transform scales the line's WIDTH and its type size, and a book drawn
    /// under `cm [0.75 0 0 -0.75 0 841.92]` reported every box a third too wide
    /// as well as at a height that was off the page. Measured against PDFium on
    /// one word: left 0.3730 where the page has 0.2807, which is exactly 0.75.
    ctm: Matrix,
    /// The line's own matrix with the transform applied: text space straight
    /// onto the page.
    ///
    /// ⚠️ NEEDED SEPARATELY FROM `ctm` BECAUSE A REACH IS IN TEXT SPACE. The
    /// cluster edges are in user space and want `ctm`; how far the type climbs
    /// above its baseline comes from the FACE, in the text object's own space,
    /// and wants this. Using `ctm` for both put the ascender BELOW the baseline
    /// on a book whose text matrix flips to cancel the page's flip, so the
    /// caret was drawn in the gap under the line instead of on it.
    on_page: Matrix,
    /// The font resource the line is drawn with, and the name behind it.
    pub(crate) resource: Vec<u8>,
    pub(crate) base_font: String,
    pub(crate) size: f64,
    pub(crate) glyphs: Vec<u16>,
    /// Everything the line's own `TJ` numbers add to its advance, in glyph
    /// space. A negative number opens space and a positive one closes it, and
    /// both count: the total is how far the pen really travelled.
    pub(crate) adjust: f64,
    /// Every `TJ` number the line carries, and which glyph it comes before.
    ///
    /// ⚠️ THE TOTAL IS NOT ENOUGH TO PLACE A CARET. A justified line's
    /// stretch is spread along it, so knowing only that it adds 2 points
    /// somewhere puts everything after the first space up to 2 points wrong,
    /// and a caret that lands between the wrong letters is the whole failure
    /// this way of editing exists to avoid.
    pub(crate) nudges: Vec<(usize, f64)>,
    /// Where the line's word spaces are, and how wide.
    ///
    /// ⚠️ FOUND BY GEOMETRY, NOT BY GLYPH. A space between two separately
    /// placed runs is drawn by placing the second one further along, so there
    /// is no space glyph to read. The gaps INSIDE a run are a different thing
    /// and are not breaks: a justified line stretches the spaces it already
    /// has, so reading those as spaces splits words the author typed as one.
    ///
    /// ⚠️ AND THE WIDTH IS KEPT BECAUSE A REWRITER NEEDS IT. Collapsing a
    /// line's placements into one run loses every space that was drawn as a
    /// placement, unless the run puts them back as `TJ` numbers.
    pub(crate) breaks: Vec<Break>,
    /// Whether this line's gaps are LETTER SPACING rather than word spaces.
    ///
    /// ⚠️ A PRODUCER CAN SET A LINE WITH EXPANDED CHARACTER SPACING, drawing
    /// every syllable in a placement of its own with the same small gap after
    /// it. Measured on a real heading: seventeen placements, sixteen gaps, all
    /// between 0.284 and 0.296 of the type size against a space glyph 0.274
    /// wide, over text with no space in it anywhere. Read as word spaces, a
    /// department name came back with a space between every syllable of it,
    /// and an edit would have written every one of those into the page.
    ///
    /// ⚠️ THE GAPS ARE KEPT ALL THE SAME. They are on the page whatever they
    /// mean, so the geometry still has to cross them or every cluster after the
    /// first would be placed short. This says only what the TEXT does.
    pub(crate) tracked: bool,
    /// Which operations of the page's content stream drew this line, in order.
    ///
    /// ⚠️ WHAT A REWRITER NEEDS AND A READER DOES NOT. A line is drawn by
    /// several separately placed runs, so replacing its text means knowing
    /// every operation that contributed a glyph, not just where the line sits.
    pub(crate) drawn_by: Vec<usize>,
}

impl Line {
    /// Where the line STARTS on the page, in PDF user space.
    ///
    /// ⚠️ NOT [`Line::x`], WHICH IS THE STREAM'S OWN FRAME. Two lines drawn
    /// under different transforms cannot be ordered against each other by that
    /// one, and ordering them is the whole reason to ask.
    pub(crate) fn page_x(&self) -> f64 {
        self.on_page.x()
    }

    /// Where a distance measured ALONG this line's baseline lands on the page.
    ///
    /// ⚠️ AN ADVANCE IS IN UNSCALED TEXT SPACE, AND THE PAGE IS NOT. A width
    /// in points is what the pen travels before `Tm` and the transform in force
    /// over it have had their say. Handing that number straight to something
    /// that moves text across the PAGE moves the wrong distance on every book
    /// that draws its text under a `cm`, which is most of them.
    pub(crate) fn along_baseline(&self, distance: f64) -> (f64, f64) {
        self.on_page.along(distance, 0.0)
    }
}

/// A word space inside a line: which glyph it comes before, and how wide it is
/// in points.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Break {
    pub(crate) at: usize,
    pub(crate) points: f64,
}

fn number(o: &Object) -> Option<f64> {
    match o {
        Object::Real(r) => Some(*r as f64),
        Object::Integer(i) => Some(*i as f64),
        _ => None,
    }
}

fn dictionary(doc: &Document, o: &Object) -> Option<lopdf::Dictionary> {
    match o {
        Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
        Object::Dictionary(d) => Some(d.clone()),
        _ => None,
    }
}

/// The `/BaseFont` behind each font resource a page names, and the advances it
/// declares for its glyphs.
///
/// ⚠️ THE WIDTHS COME FROM THE FILE, NOT FROM THE INSTALLED FONT. They are what
/// the producer actually advanced by, which is what decides where one placed
/// run ends and whether a space stands between it and the next.
pub(crate) fn fonts_of(doc: &Document, page: ObjectId) -> BTreeMap<Vec<u8>, (String, Option<crate::shaped::CidWidths>)> {
    let mut out = BTreeMap::new();
    let Some(page) = doc.get_dictionary(page).ok() else { return out };
    let Some(resources) = page.get(b"Resources").ok().and_then(|o| dictionary(doc, o)) else {
        return out;
    };
    let Some(fonts) = resources.get(b"Font").ok().and_then(|o| dictionary(doc, o)) else {
        return out;
    };
    for (name, obj) in fonts.iter() {
        let Some(font) = dictionary(doc, obj) else { continue };
        let Ok(base) = font.get(b"BaseFont").and_then(|o| o.as_name()) else { continue };
        let widths = match obj {
            Object::Reference(id) => crate::shaped::cid_widths(doc, *id),
            _ => None,
        };
        out.insert(name.to_vec(), (String::from_utf8_lossy(base).to_string(), widths));
    }
    out
}

/// The page's CID fonts that say they draw Devanagari.
///
/// Both halves matter. A font arrives wearing its own glyph ids only where the
/// encoding is Identity-H, which is what declaring `/W` says; and whether it
/// draws Devanagari at all is a question only the font can answer, because the
/// glyph ids of a Latin subset and of a Devanagari face overlap almost exactly.
fn devanagari_fonts_of(doc: &Document, page: ObjectId) -> BTreeSet<String> {
    let mut out = BTreeSet::new();
    let Some(page_dict) = doc.get_dictionary(page).ok() else { return out };
    let Some(resources) = page_dict.get(b"Resources").ok().and_then(|o| dictionary(doc, o))
    else {
        return out;
    };
    let Some(fonts) = resources.get(b"Font").ok().and_then(|o| dictionary(doc, o)) else {
        return out;
    };
    for (_, obj) in fonts.iter() {
        let Object::Reference(id) = obj else { continue };
        if crate::shaped::cid_widths(doc, *id).is_none() {
            continue;
        }
        let Some(font) = dictionary(doc, obj) else { continue };
        let Ok(base) = font.get(b"BaseFont").and_then(|o| o.as_name()) else { continue };
        if crate::devanagari::font_claims_devanagari(doc, &font) {
            out.insert(String::from_utf8_lossy(base).to_string());
        }
    }
    out
}

/// Every line the page draws, in the order it draws them.
// The accumulator reset inside `finish!` is read by every expansion except the
// last one, which is the only place the compiler can see.
#[allow(unused_assignments)]
pub(crate) fn lines_of(doc: &Document, page: ObjectId) -> Vec<Line> {
    let names = fonts_of(doc, page);
    let Ok(content) = lopdf::content::Content::decode(&doc.get_page_content(page)) else {
        return Vec::new();
    };

    let mut out: Vec<Line> = Vec::new();
    let mut resource: Vec<u8> = Vec::new();
    let mut size = 0.0f64;
    // The text matrix and the LINE matrix. `Td` and `T*` move the line matrix,
    // and the text matrix is reset to it; only `TJ` moves the text matrix on
    // its own, which is why a line is identified by the line matrix.
    let mut line_matrix = Matrix::IDENTITY;
    // What is in force over the text, and the stack `q` and `Q` keep it on.
    //
    // ⚠️ TRACKED SO A LINE CAN BE FOUND FROM OUTSIDE THE STREAM. Nothing about
    // reading a line needs this: the glyphs, their order and their widths are
    // all in the text object's own space. It is here because an outside caller
    // hands back a baseline it read off the PAGE, and until this existed the
    // lookup compared that against a number in a different frame.
    let mut ctm = Matrix::IDENTITY;
    let mut ctm_stack: Vec<Matrix> = Vec::new();
    let mut leading = 0.0f64;
    let mut glyphs: Vec<u16> = Vec::new();
    let mut adjust = 0.0f64;
    let mut nudges: Vec<(usize, f64)> = Vec::new();
    let mut drawn_by: Vec<usize> = Vec::new();

    macro_rules! finish {
        () => {
            if !glyphs.is_empty() {
                let on_page = line_matrix.then(ctm);
                out.push(Line {
                    y: line_matrix.y(),
                    x: line_matrix.x(),
                    page_y: on_page.y(),
                    ctm,
                    on_page,
                    resource: resource.clone(),
                    base_font: names
                        .get(&resource)
                        .map(|(b, _)| b.clone())
                        .unwrap_or_default(),
                    size,
                    glyphs: std::mem::take(&mut glyphs),
                    adjust: std::mem::replace(&mut adjust, 0.0),
                    nudges: std::mem::take(&mut nudges),
                    breaks: Vec::new(),
                    tracked: false,
                    drawn_by: std::mem::take(&mut drawn_by),
                });
            } else {
                adjust = 0.0;
                nudges.clear();
                drawn_by.clear();
            }
        };
    }

    for (index, op) in content.operations.iter().enumerate() {
        match op.operator.as_str() {
            // ⚠️ THE TRANSFORM STACK, WHICH IS OUTSIDE THE TEXT OBJECTS. A
            // producer wraps a whole page of text in one `q`/`cm`/`Q`, so this
            // has to be tracked across every operation and not only inside a
            // `BT`. `Q` on an empty stack is a malformed stream, and the
            // identity is the only answer that keeps the rest readable.
            "q" => ctm_stack.push(ctm),
            "Q" => ctm = ctm_stack.pop().unwrap_or(Matrix::IDENTITY),
            "cm" => {
                let mut m = [0.0f64; 6];
                for (i, slot) in m.iter_mut().enumerate() {
                    *slot = op.operands.get(i).and_then(number).unwrap_or(0.0);
                }
                if op.operands.len() >= 6 {
                    ctm = Matrix(m).then(ctm);
                }
            }
            "BT" => {
                finish!();
                line_matrix = Matrix::IDENTITY;
            }
            "ET" => finish!(),
            "Tm" => {
                finish!();
                let mut m = [0.0f64; 6];
                for (i, slot) in m.iter_mut().enumerate() {
                    *slot = op.operands.get(i).and_then(number).unwrap_or(0.0);
                }
                line_matrix = Matrix(m);
            }
            "TL" => leading = op.operands.first().and_then(number).unwrap_or(0.0),
            "Td" | "TD" => {
                let tx = op.operands.first().and_then(number).unwrap_or(0.0);
                let ty = op.operands.get(1).and_then(number).unwrap_or(0.0);
                if op.operator == "TD" {
                    leading = -ty;
                }
                // ⚠️ A HORIZONTAL Td IS THE SAME LINE, BUT IT IS A NEW
                // PLACEMENT. A producer uses one to step along a line it is
                // drawing in pieces, and treating each piece as its own line
                // cuts words, and syllables, in half; `merge_placements` is
                // what puts them back, and it also records how far the pen
                // skipped. Swallowing the step here instead kept the glyphs
                // together but stamped the run with the x of its LAST
                // placement, so a real line's clusters began 175 points right
                // of where the page draws them.
                finish!();
                line_matrix = Matrix::translation(tx, ty).then(line_matrix);
            }
            "T*" => {
                finish!();
                line_matrix = Matrix::translation(0.0, -leading).then(line_matrix);
            }
            "Tf" => {
                // ⚠️ A NEW FONT RESOURCE IS NOT A NEW LINE. A producer splits
                // one line across several subsets of the SAME face: measured,
                // Word drew a single line of Burmese with three resources, all
                // of them MyanmarText. Ending the line at each switch cut
                // syllables in half and refused four lines that read perfectly
                // once joined. What matters is the face behind the resource.
                let next = match op.operands.first() {
                    Some(Object::Name(n)) => n.to_vec(),
                    _ => resource.clone(),
                };
                let was = names.get(&resource).map(|(b, _)| b.as_str()).unwrap_or_default();
                let now = names.get(&next).map(|(b, _)| b.as_str()).unwrap_or_default();
                if installed(was) != installed(now) {
                    finish!();
                }
                resource = next;
                size = op.operands.get(1).and_then(number).unwrap_or(0.0);
            }
            "TJ" | "Tj" => {
                let items: Vec<Object> = match op.operands.first() {
                    Some(Object::Array(a)) => a.clone(),
                    Some(o @ Object::String(..)) => vec![o.clone()],
                    _ => continue,
                };
                drawn_by.push(index);
                for item in items {
                    match item {
                        Object::String(bytes, _) => {
                            for pair in bytes.chunks(2) {
                                if pair.len() == 2 {
                                    glyphs.push(u16::from_be_bytes([pair[0], pair[1]]));
                                }
                            }
                        }
                        other => {
                            // A positive number moves the pen LEFT, so what it
                            // contributes to the advance is its negation.
                            if let Some(v) = number(&other) {
                                adjust -= v;
                                nudges.push((glyphs.len(), v));
                            }
                        }
                    }
                }
            }
            _ => {}
        }
    }
    finish!();
    merge_placements(out, &names)
}

/// Whether two font names are the same FACE, for the purpose of joining two
/// placements into one line.
///
/// ⚠️ TWO FONTS NOTHING IS KNOWN ABOUT ARE NOT THE SAME FONT. This used to
/// compare `installed()` of each, which answers None for anything outside the
/// two Burmese families, so ANY two unrecognised fonts compared equal and their
/// placements were merged into one line. Measured on a Devanagari book, whose
/// fonts are all unrecognised: a heading's Devanagari and the en dash beside it
/// are set in different fonts, they were joined, and the resulting run mixed
/// glyph ids from two fonts. It agreed with the page for three glyphs and then
/// diverged on the dash, which is not a reading problem at all.
///
/// ⚠️ AND THIS ONLY EVER REFUSES A JOIN. Two subsets of one readable family
/// still resolve to one file and still join, which is what the Burmese files
/// rely on; anything else must now be named alike. A join not made costs a
/// space at worst, where a join wrongly made corrupts the run.
fn one_face(a: &str, b: &str) -> bool {
    match (installed(a), installed(b)) {
        (Some(x), Some(y)) => x == y,
        _ => a == b,
    }
}

/// Joins the separately placed runs that make up one visual line.
///
/// ⚠️ A PRODUCER PLACES A LINE IN PIECES, EACH WITH ITS OWN `Tm`. Measured on a
/// Word file: the first visual line was four text blocks at y 709.1, 709.1,
/// 708.5 and 709.1, and reading them apart cut syllables across the joins and
/// refused three of the four. They are one line, and the page says so by
/// putting them on one baseline.
///
/// ⚠️ AND IF THE JOIN IS WRONG, THE LINE IS REFUSED, NOT MISREAD. Merging can
/// only change which glyph runs are offered to be proven.
fn merge_placements(
    lines: Vec<Line>,
    fonts: &BTreeMap<Vec<u8>, (String, Option<crate::shaped::CidWidths>)>,
) -> Vec<Line> {
    const SAME_BASELINE: f64 = 2.0;
    // A word space, as a fraction of the type size. Myanmar Text sets one at
    // about a quarter of an em and a producer stretches it when justifying, so
    // the test is generous downwards and unbounded upwards.
    const A_SPACE: f64 = 0.18;

    // How far off the baseline a run that draws only marks may be lifted and
    // still belong to the line, as a fraction of the type size. Measured: the
    // one on this page is lifted 3.24 points at 10.56 point type, and the page
    // sets its lines 19.56 points apart, so there is a wide gap between what
    // this admits and what it could ever reach.
    const A_MARK: f64 = 0.5;

    // Where a run ENDS: where it was placed, plus everything it advanced by.
    //
    // ⚠️ INCLUDING THE GAPS IT HAS ALREADY BEEN GIVEN. This is asked about a
    // line that may already be several placements joined, and a gap moves the
    // pen just as surely as a glyph does. Leaving them out made every gap after
    // the first come out inflated by the sum of the ones before it: one real
    // line reported three gaps worth 405 points across 496 points of text.
    let ends_at = |line: &Line| -> Option<f64> {
        let widths = fonts.get(&line.resource).and_then(|(_, w)| w.as_ref())?;
        let drawn: f64 = line.glyphs.iter().map(|g| widths.of(*g)).sum();
        let gaps: f64 = line.breaks.iter().map(|b| b.points).sum();
        Some(line.x + (drawn + line.adjust) / 1000.0 * line.size + gaps)
    };

    // Whether a run puts ink on the page without moving the pen at all.
    //
    // ⚠️ A RUN THAT ADVANCES BY NOTHING IS NOT A LINE. Word lifts the pen off
    // the baseline to place a below-base Myanmar mark and draws it in a
    // placement of its own; measured on a real page, one such glyph sat 3.24
    // points above the line, so the baseline test threw it out, and because it
    // fell BETWEEN the two halves of a line it stopped those from joining
    // either. One mark cost two lines, and it split a word down the middle.
    let draws_only_marks = |line: &Line| -> bool {
        let Some(widths) = fonts.get(&line.resource).and_then(|(_, w)| w.as_ref()) else {
            return false;
        };
        !line.glyphs.is_empty() && line.glyphs.iter().all(|g| widths.of(*g) == 0.0)
    };

    // How many times each line in `out` has been joined to another, so a line
    // gapped at EVERY one of its joins can be told from one gapped only
    // between its words.
    let mut joins: Vec<usize> = Vec::new();

    let mut out: Vec<Line> = Vec::new();
    for line in lines {
        let joined = out.last().is_some_and(|prev| {
            let apart = (prev.y - line.y).abs();
            one_face(&prev.base_font, &line.base_font)
                && line.x >= prev.x
                && (apart < SAME_BASELINE
                    || (apart < A_MARK * line.size && draws_only_marks(&line)))
        });
        if !joined {
            out.push(line);
            joins.push(0);
            continue;
        }
        *joins.last_mut().unwrap() += 1;
        let gap = ends_at(out.last().unwrap()).map(|end| line.x - end);
        let prev = out.last_mut().unwrap();
        let at = prev.glyphs.len();
        if let Some(points) = gap.filter(|g| *g >= A_SPACE * line.size) {
            prev.breaks.push(Break { at, points });
        }
        prev.glyphs.extend(&line.glyphs);
        prev.breaks.extend(line.breaks.iter().map(|b| Break { at: b.at + at, ..*b }));
        prev.adjust += line.adjust;
        prev.nudges.extend(line.nudges.iter().map(|(i, v)| (i + at, *v)));
        prev.drawn_by.extend(&line.drawn_by);
    }

    for (line, joins) in out.iter_mut().zip(joins) {
        line.tracked = is_letter_spaced(line, joins);
    }
    out
}

/// Whether a line's gaps are letter spacing rather than word spaces.
///
/// ⚠️ THREE THINGS TOGETHER, AND NONE OF THEM ALONE. A justified line's word
/// spaces are uniform too, so uniformity by itself would take the spaces out of
/// ordinary text: measured on one real page, a line of seven word gaps sat
/// between 0.463 and 0.490 of the type size. What letter spacing also does is
/// gap EVERY join, and stay about as wide as the space it is not. Measured over
/// two Burmese files, 44 lines: this says yes to the one line that is letter
/// spaced and no to all the rest, including every uniformly justified one.
fn is_letter_spaced(line: &Line, joins: usize) -> bool {
    // As a fraction of the type size. The space glyph in both Burmese faces
    // this reads is 0.274 of an em, so this admits one about half again as
    // wide and no more; a word gap on a justified line measured 1.7 times it.
    const AT_MOST: f64 = 0.40;
    // How much the gaps may differ from each other before they are not one
    // setting repeated. The letter-spaced line measured 0.012 across sixteen.
    const WITHIN: f64 = 0.08;
    // Two gaps are a coincidence. This is about a line SET that way.
    const AT_LEAST: usize = 3;

    if joins < AT_LEAST || line.breaks.len() != joins || line.size <= 0.0 {
        return false;
    }
    let ems: Vec<f64> = line.breaks.iter().map(|b| b.points / line.size).collect();
    let widest = ems.iter().cloned().fold(f64::MIN, f64::max);
    let narrowest = ems.iter().cloned().fold(f64::MAX, f64::min);
    widest <= AT_MOST && widest - narrowest <= WITHIN
}

/// What one line says, or nothing.
///
/// ⚠️ THE `TJ` GAPS ARE NOT WORD SPACES ON THIS KIND OF PAGE, and treating them
/// as such invents spaces the text does not have. A justified line stretches
/// the gaps it already has, so the spaces are drawn as real space GLYPHS and
/// come back from the index like any other character. Measured: reading gaps
/// above a threshold as spaces split `အရှေ့မိုးကုပ်` into two words that the
/// author had typed as one.
pub(crate) fn read_line(index: &crate::reshape::Index, face: &rustybuzz::Face, line: &Line)
    -> Option<String>
{
    Some(said_by(&pieces_of(index, face, line)?))
}

/// One stretch of a line that was proven on its own.
pub(crate) struct Piece {
    /// Which of the line's glyphs say it.
    pub(crate) glyphs: std::ops::Range<usize>,
    /// What they say.
    pub(crate) text: String,
    /// Whether the reading puts a space character here.
    ///
    /// ⚠️ A GAP AND A SPACE ARE NOT THE SAME THING. A producer can draw a
    /// space GLYPH and then also skip; the reading needs one space character
    /// for the two of them, while the geometry has to cross both widths. So
    /// this says only what the TEXT does, and [`clusters_of`] reads the skips
    /// off the line itself.
    pub(crate) space_before: bool,
}

/// What a line's pieces say together.
pub(crate) fn said_by(pieces: &[Piece]) -> String {
    let mut out = String::new();
    for piece in pieces {
        if piece.space_before {
            out.push(' ');
        }
        out.push_str(&piece.text);
    }
    out
}

/// The line, cut into the stretches that could each be proven.
///
/// ⚠️ THE READING AND THE GEOMETRY MUST COME FROM THE SAME CUTS, which is
/// why this is one function and not two. A caret placed from a different split
/// than the text it is being placed in would drift by a whole space wherever
/// the two disagreed.
pub(crate) fn pieces_of(index: &crate::reshape::Index, face: &rustybuzz::Face, line: &Line)
    -> Option<Vec<Piece>>
{
    // ⚠️ AND IF NO SPLIT WORKS, THE LINE IS READ WHOLE. Losing a space is
    // worth far less than losing the line, and a reading with a word space
    // missing is still the author's text.
    split_at_the_spaces(index, face, line).or_else(|| {
        Some(vec![Piece {
            glyphs: 0..line.glyphs.len(),
            text: crate::reshape::prove(face, index, &line.glyphs)?,
            space_before: false,
        }])
    })
}

fn split_at_the_spaces(index: &crate::reshape::Index, face: &rustybuzz::Face, line: &Line)
    -> Option<Vec<Piece>>
{
    if line.breaks.is_empty() {
        return None;
    }
    // ⚠️ A BREAK IS HONOURED ONLY IF WHAT IT CUTS OFF CAN BE PROVEN. A producer
    // is free to place a run that starts mid-syllable, and measured on a Word
    // file it does: splitting there leaves two halves of a cluster that nothing
    // spells. Rather than lose the line, the failed break is passed over and
    // folded into the next piece, which costs one space instead.
    let cuts: Vec<usize> = line
        .breaks
        .iter()
        .map(|b| b.at)
        .chain(std::iter::once(line.glyphs.len()))
        .collect();

    let mut out: Vec<Piece> = Vec::new();
    let mut from = 0usize;
    let mut next = 0usize;
    while from < line.glyphs.len() {
        let mut taken = None;
        while next < cuts.len() {
            let cut = cuts[next];
            next += 1;
            if cut <= from || cut > line.glyphs.len() {
                continue;
            }
            if let Some(text) = crate::reshape::prove(face, index, &line.glyphs[from..cut]) {
                taken = Some((cut, text));
                break;
            }
        }
        let (cut, text) = taken?;

        // The page's own skip before this piece, whatever it means.
        let gap_before = line
            .breaks
            .iter()
            .find(|b| b.at == from)
            .map(|b| b.points)
            .unwrap_or(0.0);

        // ⚠️ BUT NOT A SPACE IF THE PAGE ALREADY DREW ONE. A break only says
        // the pieces were placed apart, and a placement can begin right after a
        // space glyph, in which case adding another would put two where the
        // author typed one.
        let ends_open = out.last().is_some_and(|p: &Piece| p.text.ends_with(' '));
        // ⚠️ AND NOT ON A LINE THAT IS MERELY LETTER SPACED. See
        // `Line::tracked`: every join of such a line carries the same small
        // gap, and none of them is a space the author typed.
        let space_before = !out.is_empty()
            && !line.tracked
            && !ends_open
            && !text.starts_with(' ')
            && gap_before > 0.0;

        out.push(Piece { glyphs: from..cut, text, space_before });
        from = cut;
    }
    (!out.is_empty()).then_some(out)
}

/// Where one cluster of a line's reading is drawn.
///
/// ⚠️ A CLUSTER, NOT A CHARACTER, AND THE SCRIPT FORCES IT. `မြ` is drawn
/// as one unit with the medial BEFORE the consonant it follows, so there is no
/// position on the page between the two of them for a caret to stand at. A
/// reader moves through Burmese a cluster at a time and so does this. Asking
/// for a per-character box would mean inventing positions the page does not
/// have, and putting a caret at an invented position is exactly the failure
/// editing in place exists to avoid.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Cluster {
    /// The bytes of the line's reading this covers.
    pub(crate) from: usize,
    pub(crate) to: usize,
    /// Where it is drawn, in PDF user space.
    pub(crate) left: f64,
    pub(crate) right: f64,
}

/// Where each cluster of a line's reading sits on the page.
///
/// ⚠️ MEASURED WITH THE PAGE'S OWN WIDTHS AND THE PAGE'S OWN NUDGES. The
/// advance a glyph really made is what the file declares for it plus whatever
/// `TJ` number was written in front of it, and on a justified line those
/// numbers are where the stretch lives. Laying the text out from the font's
/// natural advances instead would drift further along the line, which is the
/// end a reader is most likely to click.
/// How far the page's pen travels drawing this placement, in PDF user space.
///
/// ⚠️ NO READING REQUIRED, AND THAT IS THE WHOLE VALUE OF IT. This is the
/// file's own arithmetic: the widths it declares for the glyphs it draws, less
/// the numbers it wrote between them, plus the gaps it skipped. `clusters_of`
/// can only measure a placement it has READ, because it re-shapes the reading
/// to find the cluster edges. On the reader's Hindi book a placement is one
/// word and only about seven in ten can be read, so a measurement that needs
/// the reading cannot say where three in ten of them end.
///
/// ⚠️ AND IT MIRRORS `clusters_of` OPERATION FOR OPERATION, because the two
/// must not disagree about where a line ends. Same scale, same nudges with the
/// same sign, same skips. Measured on both real books by
/// `the_two_ways_of_measuring_a_placement`: 0.011 points apart at worst.
///
/// ⚠️ THE SKIPS COUNT, AND THEY ARE NOT IN THE NUDGES. A producer draws one
/// line in several placements and the space between two of them is made by
/// starting the next one further along, not by any number inside a run.
/// Leaving them out measured the reader's own line 46.7 points shorter than it
/// is: the replacement was told it was already too wide, no stretch was shared
/// into it, and the line came back with its right edge 46.7 points in from the
/// margin.
///
/// ⚠️ AND IT IS IN TEXT SPACE, NOT ON THE PAGE. Put it through
/// [`Line::along_baseline`] before moving anything by it.
pub(crate) fn advance_of(line: &Line, widths: &crate::shaped::CidWidths) -> f64 {
    let scale = line.size / 1000.0;
    let glyphs: f64 = line.glyphs.iter().map(|g| widths.of(*g)).sum();
    // A positive number moves the pen LEFT, so it takes width away.
    let nudges: f64 = line.nudges.iter().map(|(_, v)| *v).sum();
    let skips: f64 = line.breaks.iter().map(|b| b.points).sum();
    (glyphs - nudges) * scale + skips
}

pub(crate) fn clusters_of(
    line: &Line,
    pieces: &[Piece],
    face: &rustybuzz::Face,
    widths: &crate::shaped::CidWidths,
) -> Vec<Cluster> {
    let scale = line.size / 1000.0;
    // How far the page skips before drawing the glyph at this index.
    let skip_at = |g: usize| -> f64 {
        line.breaks.iter().filter(|b| b.at == g).map(|b| b.points).sum()
    };
    let mut out: Vec<Cluster> = Vec::new();
    let mut x = line.x;
    let mut at = 0usize;

    for piece in pieces {
        // ⚠️ THE SKIP IS ALWAYS CROSSED, whether or not a character stands
        // in it. A space the reading invented gets a box of its own, because a
        // caret has to be able to stand in it. A skip the page left AFTER a
        // space it had already drawn belongs to that space, so it widens the
        // cluster before it. Either way the pen moves, and forgetting that put
        // one line 14 points left of where it is drawn.
        let gap = skip_at(piece.glyphs.start);
        if gap > 0.0 {
            if piece.space_before {
                out.push(Cluster { from: at, to: at + 1, left: x, right: x + gap });
                at += 1;
            } else if let Some(last) = out.last_mut() {
                last.right += gap;
            }
            x += gap;
        }

        let mut buffer = rustybuzz::UnicodeBuffer::new();
        buffer.push_str(&piece.text);
        let shaped = rustybuzz::shape(face, &[], buffer);
        let infos = shaped.glyph_infos();

        let mut i = 0usize;
        while i < infos.len() {
            let cluster = infos[i].cluster as usize;
            let left = x;
            while i < infos.len() && infos[i].cluster as usize == cluster {
                let g = piece.glyphs.start + i;
                // ⚠️ AND A SKIP INSIDE A PIECE IS CROSSED TOO. A piece is as
                // long as the reading could be proven, which has nothing to do
                // with where the page chose to re-place its pen: measured, one
                // real line skipped 37 points at seven places that all fell
                // inside a piece, and every cluster after them sat short.
                if i > 0 {
                    x += skip_at(g);
                }
                // The number written in front of this glyph, if any. Positive
                // moves the pen LEFT, so its contribution is its negation.
                for (_, v) in line.nudges.iter().filter(|(at, _)| *at == g) {
                    x -= v * scale;
                }
                x += widths.of(infos[i].glyph_id as u16) * scale;
                i += 1;
            }
            let ends = infos
                .get(i)
                .map(|g| g.cluster as usize)
                .unwrap_or(piece.text.len());
            out.push(Cluster { from: at + cluster, to: at + ends, left, right: x });
        }
        at += piece.text.len();
    }
    out
}

/// How far above and below the baseline a face reaches at this size. The
/// second number is negative, the way a font declares its descender.
fn reach_of(face: &rustybuzz::Face, size: f64) -> (f64, f64) {
    let upem = face.units_per_em() as f64;
    if upem <= 0.0 {
        return (0.0, 0.0);
    }
    let scale = size / upem;
    (face.ascender() as f64 * scale, face.descender() as f64 * scale)
}

/// The page's crop box, as (left, top, width) in PDF user space.
///
/// ⚠️ INHERITED, AND THE CROP BOX BEFORE THE MEDIA BOX, because that is what
/// the rest of the app normalizes against: `page_origin` asks PDFium the same
/// two questions in the same order. A page that answered differently here would
/// put every recovered line at an offset from every other line on the page.
pub(crate) fn page_box(doc: &Document, page: ObjectId) -> Option<(f64, f64, f64)> {
    let mut at = page;
    for _ in 0..32 {
        let Ok(dict) = doc.get_dictionary(at) else { break };
        for key in [b"CropBox".as_slice(), b"MediaBox".as_slice()] {
            if let Some(found) = dict.get(key).ok().and_then(|o| rect_of(doc, o)) {
                return Some(found);
            }
        }
        match dict.get(b"Parent").ok().and_then(|o| o.as_reference().ok()) {
            Some(parent) => at = parent,
            None => break,
        }
    }
    None
}

/// A PDF rectangle as (left, top, width), whichever corners it names.
fn rect_of(doc: &Document, object: &Object) -> Option<(f64, f64, f64)> {
    let Ok((_, Object::Array(a))) = doc.dereference(object) else { return None };
    if a.len() != 4 {
        return None;
    }
    let mut v = [0.0f64; 4];
    for (slot, o) in v.iter_mut().zip(a) {
        *slot = doc.dereference(o).ok().and_then(|(_, r)| number(r))?;
    }
    // A rectangle may be written from any corner to any corner.
    let (x0, x1) = (v[0].min(v[2]), v[0].max(v[2]));
    let (_, y1) = (v[1].min(v[3]), v[1].max(v[3]));
    let width = x1 - x0;
    (width > 0.0).then_some((x0, y1, width))
}

/// The line again with any code the font has no glyph for turned into a SKIP,
/// or nothing when it has none.
///
/// ⚠️ A CODE THE FACE CANNOT DRAW IS NOT A LETTER. Measured on a real Word
/// page: one line ended with code 8224 in a face that has 1028 glyphs, so the
/// page draws nothing there and nothing ever could. Every one of that line's
/// other 83 glyphs proved on its own, and the single phantom refused the whole
/// line of the author's text.
///
/// ⚠️ BUT IT STILL MOVES THE PEN, BY WHATEVER THE FILE DECLARES FOR IT. That
/// one was declared a full em wide, so simply dropping it left the line's
/// clusters 10.56 points short of where the page's pen finishes. Trailing, that
/// costs nothing; in the middle of a line it would drag every cluster after it
/// a whole em to the left, which is a caret on the wrong letter. Turning it
/// into a skip keeps the geometry exactly as it was and takes only the letter
/// away.
///
/// ⚠️ AND ONLY OUT OF RANGE, NEVER MERELY BLANK. A space has a glyph, an
/// outline of nothing and a real advance, and treating one as a phantom would
/// be reading the page's own spaces out of the text. The test here is whether
/// the face has the glyph at all, which is the one case where no renderer can
/// put ink down.
fn without_phantoms(
    line: &Line,
    face: &rustybuzz::Face,
    widths: Option<&crate::shaped::CidWidths>,
) -> Option<Line> {
    let glyphs = face.number_of_glyphs();
    if !line.glyphs.iter().any(|g| *g >= glyphs) {
        return None;
    }
    let scale = line.size / 1000.0;

    // Where each surviving glyph moves to, and what the dropped ones skipped.
    //
    // ⚠️ A NUMBER OR A SKIP AT THE DROPPED INDEX STAYS PUT. It was written
    // BEFORE the phantom, so it moves the pen before whatever now stands there;
    // only what came after the phantom shifts back by one.
    let mut moved_to = Vec::with_capacity(line.glyphs.len() + 1);
    let mut kept: Vec<u16> = Vec::with_capacity(line.glyphs.len());
    let mut skipped: Vec<Break> = Vec::new();
    for g in &line.glyphs {
        moved_to.push(kept.len());
        if *g < glyphs {
            kept.push(*g);
        } else {
            let points = widths.map(|w| w.of(*g) * scale).unwrap_or(0.0);
            if points > 0.0 {
                skipped.push(Break { at: kept.len(), points });
            }
        }
    }
    moved_to.push(kept.len());

    let end = kept.len();
    let at = |i: usize| moved_to.get(i).copied().unwrap_or(end);

    let mut breaks: Vec<Break> =
        line.breaks.iter().map(|b| Break { at: at(b.at), ..*b }).collect();
    breaks.extend(skipped);
    breaks.sort_by_key(|b| b.at);

    Some(Line {
        glyphs: kept,
        nudges: line.nudges.iter().map(|(i, v)| (at(*i), *v)).collect(),
        breaks,
        resource: line.resource.clone(),
        base_font: line.base_font.clone(),
        drawn_by: line.drawn_by.clone(),
        ..*line
    })
}

/// One line of a page, and what it says.
pub(crate) struct Reading {
    /// Where the line sits, in PDF user space.
    pub(crate) y: f64,
    pub(crate) x: f64,
    /// How tall the type is, and how far it reaches above and below the
    /// baseline, all in PDF user space.
    ///
    /// ⚠️ FROM THE FACE, NOT FROM THE GLYPHS THAT HAPPEN TO BE ON THE LINE.
    /// The app draws a frame round a line and covers it while it is retyped, and
    /// a box that tracked the tallest letter present would jump as soon as the
    /// reader typed a taller one.
    pub(crate) size: f64,
    pub(crate) top: f64,
    pub(crate) bottom: f64,
    /// The line's font, subset tag and all, for the app to match on screen.
    pub(crate) font: String,
    /// What it says, or nothing when it could not be proven.
    pub(crate) text: Option<String>,
    /// Where each cluster of that reading is drawn. Empty when nothing was
    /// proven, because there is nothing to place.
    pub(crate) clusters: Vec<Cluster>,
}

/// What each of a page's fonts draws, ready to read that page with.
///
/// ⚠️ THIS IS THE EXPENSIVE PART, AND THE ONLY EXPENSIVE PART. Building it
/// takes about 17 seconds; reading a page with one already built takes
/// milliseconds. It is separate from the reading so a caller can pay for it
/// once, in advance, and off the thread the reader is waiting on.
#[derive(Default, Clone)]
pub(crate) struct Indexes {
    by_font: BTreeMap<String, Arc<(Vec<u8>, crate::reshape::Index)>>,
    /// The installed file each font resolved to.
    ///
    /// WARN THE CALLER CANNOT WORK THIS OUT FROM THE NAME. A real Hindi book
    /// names its fonts `CIDFont+F1`..`F7`, which the app looked up, failed to
    /// find, and reported as "CIDFont+F2 is not installed on this machine".
    /// The face was resolved HERE, by evidence, so the answer belongs here and
    /// travels with the reading.
    paths: BTreeMap<String, &'static str>,

    /// The glyphs each face's index was built over.
    ///
    /// ⚠️ THIS IS WHAT AN INDEX ACTUALLY IS. `Index::build` keeps a syllable
    /// only when every glyph it draws is in the set it was given, so an index
    /// reads exactly those clusters whose glyphs are a subset of this and
    /// nothing else. Recording it is what lets a later page ask "is my page
    /// already covered" instead of rebuilding or, worse, reading against an
    /// index that cannot spell its syllables and calling the page unreadable.
    covered: BTreeMap<&'static str, Covers>,

    /// Bumped whenever a font resolves to a face or a face's coverage grows.
    ///
    /// ⚠️ EVERY CHANGE TO THIS VALUE IS AN IMPROVEMENT, which is what makes
    /// one counter enough. Coverage only ever grows, and a font's face is
    /// decided once and never revisited, so a reading taken at an earlier
    /// generation can only be poorer than one taken now, never wrong. A page
    /// records the generation it was read at and is eligible again the moment
    /// this moves past it.
    generation: u64,
}

/// What a face's index can read.
///
/// ⚠️ THE TWO SCRIPTS INDEX DIFFERENTLY, and pretending otherwise costs a
/// rebuild per page. A Burmese index is filled by enumerating the syllables a
/// GIVEN glyph set can spell, so it reads that set and no more. A Devanagari
/// one is filled from the font's own tables and is never given a set at all,
/// so it already reads everything the face can draw. Recording a Devanagari
/// face as covering only the glyphs of the page that happened to build it
/// would rebuild it, and bump the generation, on every page after the first.
#[derive(Clone)]
enum Covers {
    Everything,
    Only(BTreeSet<u16>),
}

impl Covers {
    fn all_of(&self, glyphs: &BTreeSet<u16>) -> bool {
        match self {
            Covers::Everything => true,
            Covers::Only(had) => glyphs.iter().all(|g| had.contains(g)),
        }
    }
}

impl Indexes {
    /// Whether anything on the page can be read at all.
    pub(crate) fn is_empty(&self) -> bool {
        self.by_font.is_empty()
    }

    /// What this font draws, if it is one that can be read.
    pub(crate) fn index_for(&self, base_font: &str) -> Option<&crate::reshape::Index> {
        self.by_font.get(base_font).map(|face| &face.1)
    }

    /// The installed file this font was read with, if any.
    pub(crate) fn path_for(&self, base_font: &str) -> Option<&'static str> {
        self.paths.get(base_font).copied()
    }

    /// Which improvement of the recovery resources this is.
    pub(crate) fn generation(&self) -> u64 {
        self.generation
    }

    /// Takes in everything `wanted` asks for, and says whether anything about
    /// this changed and so has to be kept.
    ///
    /// ⚠️ FONT IDENTITY IS DECIDED ONCE AND IS STICKY. `devanagari::face_of`
    /// picks the face that names the largest SHARE of a glyph set, and a single
    /// page is a much smaller sample than a document: two faces can both clear
    /// the bar on one page and the winner would be whichever the candidate list
    /// reaches first. Re-deciding per page would let two pages of one book read
    /// the same font as two different faces. Once `paths` has an answer it is
    /// never asked again.
    ///
    /// ⚠️ AND WHERE A PAGE'S OWN EVIDENCE IS NOT ENOUGH, THE SEARCH WIDENS
    /// FOR THAT FONT ALONE. A font first met on a page that draws four of its
    /// glyphs may resolve to nothing, and refusing it there would make the page
    /// unreadable for a reason that is an accident of where the reader started.
    /// `wanted_for_font` collects that one font across the document, which is
    /// the whole-document walk this exists to avoid, paid once per font that
    /// needs it rather than once per page.
    ///
    /// ⚠️ COVERAGE, BY CONTRAST, IS SAFELY PER PAGE, for every font that
    /// declares its widths. A larger glyph set only ever ADDS entries to an
    /// index: shaping does not depend on the set, and the keep test is that a
    /// syllable's glyphs are contained by it. So a face rebuilt over a union is
    /// a superset of what it was, and no reading that was possible before
    /// becomes impossible. The fonts that declare nothing are named in
    /// [`Wanted::unbounded`] and widened where the build is paid for.
    pub(crate) fn grow(&mut self, doc: &Document, wanted: &Wanted) -> bool {
        let mut changed = false;

        // 1. Identity, for fonts that do not have one yet.
        for (base_font, glyphs) in &wanted.by_font {
            if self.paths.contains_key(base_font) {
                continue;
            }
            let path = match installed(base_font) {
                Some(path) => Some(path),
                None => match crate::devanagari::face_of(glyphs) {
                    Some(path) => Some(path),
                    // The targeted widen, and only here.
                    None => crate::devanagari::face_of(&wanted_for_font(doc, base_font)),
                },
            };
            if let Some(path) = path {
                self.paths.insert(base_font.clone(), path);
                changed = true;
            }
        }

        // 2. What each face is now asked to cover.
        let mut asked: BTreeMap<&'static str, BTreeSet<u16>> = BTreeMap::new();
        for (base_font, glyphs) in &wanted.by_font {
            let Some(&path) = self.paths.get(base_font) else { continue };
            asked.entry(path).or_default().extend(glyphs.iter().copied());
        }

        // 3. Rebuild only the faces that are asked for something they have not
        //    got, over the UNION so the set grows monotonically and a face is
        //    rebuilt fewer and fewer times as a document is read.
        let mut grew = false;
        for (path, glyphs) in &asked {
            let have = self.covered.get(path);
            if have.is_some_and(|had| had.all_of(glyphs)) {
                continue;
            }
            let mut union = match have {
                Some(Covers::Only(had)) => had.clone(),
                _ => BTreeSet::new(),
            };
            union.extend(glyphs.iter().copied());

            // ⚠️ AND A FONT THIS PAGE CANNOT BOUND IS WIDENED BEFORE PAYING.
            // Asked for a font that declares no widths, the page can only say
            // what it draws itself, which the next page exceeds; scoping the
            // build to that would rebuild the face, at twenty seconds a time,
            // on page after page. The document is walked for those fonts only,
            // and only here, where a build is about to be paid for anyway.
            for base_font in &wanted.unbounded {
                if self.paths.get(base_font) == Some(path) {
                    union.extend(wanted_for_font(doc, base_font));
                }
            }

            let script = if crate::devanagari::owns(path) {
                Script::Devanagari
            } else {
                Script::Burmese
            };
            let Some((face, covers)) = build_face(path, &union, script) else { continue };

            for (base_font, at) in &self.paths {
                if at == path {
                    self.by_font.insert(base_font.clone(), Arc::clone(&face));
                }
            }
            self.covered.insert(path, covers);
            grew = true;
        }

        // 4. And a name that resolved to a face somebody else already built
        //    still needs to be handed that face.
        //
        // ⚠️ THE COVERAGE TEST ANSWERS FOR THE FACE, NOT FOR THE NAME.
        // `BCDEEE+MyanmarText` and `BCDGEE+MyanmarText` are one file, so the
        // second name met is asked for glyphs the face already covers and step
        // 3 rightly builds nothing. Without this it would be left with a
        // resolved path and no index, which reads as a font that cannot be
        // read: two subsets of one face sharing one index is the whole reason
        // this is keyed by face.
        let adopt: Vec<(String, Arc<(Vec<u8>, crate::reshape::Index)>)> = self
            .paths
            .iter()
            .filter(|(base_font, _)| !self.by_font.contains_key(*base_font))
            .filter_map(|(base_font, &path)| {
                Some((base_font.clone(), self.face_at(path)?))
            })
            .collect();
        for (base_font, face) in adopt {
            self.by_font.insert(base_font, face);
            grew = true;
        }

        // ⚠️ A BUMP MEANS A BETTER READING IS AVAILABLE, and nothing else.
        // Resolving a name to a face that then failed to build changes this and
        // must be kept, so that the widen is not paid again on the next page,
        // but it makes no page readable that was not readable before and a page
        // re-read on the strength of it would find exactly what it found last
        // time.
        if grew {
            self.generation += 1;
        }
        changed || grew
    }

    /// A face already built, found by the file it was built from.
    fn face_at(&self, path: &'static str) -> Option<Arc<(Vec<u8>, crate::reshape::Index)>> {
        self.paths
            .iter()
            .find(|(_, at)| **at == path)
            .and_then(|(base_font, _)| self.by_font.get(base_font).cloned())
    }
}

/// Resources that claim to be a given improvement, for asking a caller what it
/// does when one page's reading has been overtaken by another's.
#[cfg(test)]
pub(crate) fn tests_only_at_generation(generation: u64) -> Indexes {
    Indexes { generation, ..Default::default() }
}

/// Everything one PAGE draws, as the glyphs each of its fonts declares.
///
/// ⚠️ THE PAGE, NOT THE DOCUMENT. `indexes_for_document` walks every page of
/// the file to answer a question about one of them, which on a book of tens of
/// thousands of pages is the whole cost of preparing anything. A page can only
/// draw the glyphs it contains, so its own fonts are all that its own reading
/// can need.
pub(crate) fn wanted_for_page(doc: &Document, page: ObjectId) -> Wanted {
    let mut by_font: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
    for (_, (base_font, widths)) in fonts_of(doc, page) {
        if let Some(w) = widths {
            by_font.entry(base_font).or_default().extend(w.declared());
        }
    }

    // ⚠️ AND A FONT THAT DECLARES NOTHING STILL HAS TO BE READABLE, exactly
    // as the whole-document walk handles it: a subset without a `/W` array
    // leaves nothing to scope by, so what the page DRAWS is asked instead.
    let undeclared: Vec<String> = fonts_of(doc, page)
        .into_values()
        .map(|(f, _)| f)
        .filter(|f| !by_font.contains_key(f))
        .collect();
    if !undeclared.is_empty() {
        for line in lines_of(doc, page) {
            if undeclared.contains(&line.base_font) {
                by_font.entry(line.base_font).or_default().extend(line.glyphs);
            }
        }
    }
    Wanted { by_font, unbounded: undeclared.into_iter().collect() }
}

/// What one page needs, and how far the page can be trusted to know it.
pub(crate) struct Wanted {
    /// Every glyph each of the page's fonts is known to draw.
    pub(crate) by_font: BTreeMap<String, BTreeSet<u16>>,

    /// The fonts among them whose extent this page CANNOT bound.
    ///
    /// ⚠️ A SUBSET'S `/W` IS ALREADY DOCUMENT-WIDE, and that is the whole
    /// reason a page can be prepared on its own: the array declares a width for
    /// every CID the WHOLE DOCUMENT uses of that font, so one page's
    /// declaration is every page's and the second page of a book finds itself
    /// covered. A font without one leaves only what is drawn HERE, which the
    /// next page will exceed, and a Burmese index rebuilt per page is twenty
    /// seconds per page. Naming them is what lets the build widen just those.
    unbounded: BTreeSet<String>,
}

/// Every glyph ONE font declares, or is seen to draw, anywhere in the document.
///
/// ⚠️ THE ONLY WHOLE-DOCUMENT WALK LEFT, and it is asked for one font at a
/// time, only from inside [`Indexes::grow`], and only for the two things a
/// single page genuinely cannot answer: which face a font is, and how far a
/// font that declares no widths extends. The alternative to the first is that
/// where a reader happens to open a book decides which of its fonts can ever be
/// read; the alternative to the second is rebuilding a face on page after
/// page.
fn wanted_for_font(doc: &Document, base_font: &str) -> BTreeSet<u16> {
    let mut glyphs = BTreeSet::new();
    for &page in doc.get_pages().values() {
        for (_, (name, widths)) in fonts_of(doc, page) {
            if name != base_font {
                continue;
            }
            match widths {
                Some(w) => glyphs.extend(w.declared()),
                None => {
                    for line in lines_of(doc, page) {
                        if line.base_font == base_font {
                            glyphs.extend(line.glyphs.iter().copied());
                        }
                    }
                }
            }
        }
    }
    glyphs
}

/// Builds what is needed to read this page: one index per font, over the
/// glyphs that font actually draws here.
///
/// ⚠️ AN INDEX BUILT FOR AN OLDER VERSION OF A PAGE STAYS SAFE TO USE, so
/// nothing here needs invalidating when the page changes. The narrowing decides
/// which syllables are in the index, never what one means, and every reading is
/// shaped again against the glyphs actually on the page. A stale index can only
/// fail to read something; it cannot read it wrongly.
pub(crate) fn indexes_for(doc: &Document, page: ObjectId) -> Indexes {
    let mut wanted: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
    for line in lines_of(doc, page) {
        wanted.entry(line.base_font).or_default().extend(line.glyphs);
    }
    build_indexes(&wanted)
}

/// The same, for the WHOLE DOCUMENT, so every page shares one index.
///
/// ⚠️ MEASURED BEFORE THIS WAS WRITTEN, on a real three-page Burmese file:
///
/// ```text
///                       page 0   page 1   page 2   total    time
///     one per page      16/30    26/30    21/21    63/81    50.1s
///     one per document  20/30    30/30    21/21    71/81    22.6s
/// ```
///
/// It is not a trade. One index is two and a half times faster on three pages
/// and the saving GROWS with page count, because it no longer depends on it.
/// And it reads MORE, not less: a page-scoped index was too narrow to read its
/// own page.
///
/// ⚠️ THE SCOPE COMES FROM THE FONT DICTIONARY, NOT FROM THE PAGES. A
/// producer's embedded subset declares a width for every CID the document uses,
/// so the document-wide glyph set is there to be read without parsing a single
/// content stream. That is why this costs no more than one page's index did.
pub(crate) fn indexes_for_document(doc: &Document) -> Indexes {
    let mut wanted: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
    let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

    for &page in &pages {
        for (_, (base_font, widths)) in fonts_of(doc, page) {
            if let Some(w) = widths {
                wanted.entry(base_font).or_default().extend(w.declared());
            }
        }
    }

    // ⚠️ AND A FONT THAT DECLARES NOTHING STILL HAS TO BE READABLE. A subset
    // without a `/W` array leaves nothing to scope by, and an unscoped index is
    // the 55-second whole-of-Burmese case. Falling back to what the pages
    // actually draw keeps such a font exactly as readable as it was before this
    // existed, and costs a content-stream walk only when one turns up.
    let undeclared: Vec<String> = pages
        .iter()
        .flat_map(|&page| fonts_of(doc, page).into_values().map(|(f, _)| f))
        .filter(|f| !wanted.contains_key(f))
        .collect();
    if !undeclared.is_empty() {
        for &page in &pages {
            for line in lines_of(doc, page) {
                if undeclared.contains(&line.base_font) {
                    wanted.entry(line.base_font).or_default().extend(line.glyphs);
                }
            }
        }
    }

    build_indexes(&wanted)
}

/// The smallest document that opens: one blank page.
///
/// For tests elsewhere in the crate that need a handle to hang something off
/// rather than a page to read.
#[cfg(test)]
pub(crate) fn tests_only_blank_page() -> Document {
    let mut doc = Document::with_version("1.7");
    let pages_id = doc.new_object_id();

    let mut page_dict = lopdf::Dictionary::new();
    page_dict.set("Type", Object::Name(b"Page".to_vec()));
    page_dict.set("Parent", Object::Reference(pages_id));
    page_dict.set("MediaBox", Object::Array(vec![
        0.into(), 0.into(), 612.into(), 792.into(),
    ]));
    let page = doc.add_object(page_dict);

    let mut pages = lopdf::Dictionary::new();
    pages.set("Type", Object::Name(b"Pages".to_vec()));
    pages.set("Kids", Object::Array(vec![Object::Reference(page)]));
    pages.set("Count", Object::Integer(1));
    doc.objects.insert(pages_id, Object::Dictionary(pages));

    let mut catalog = lopdf::Dictionary::new();
    catalog.set("Type", Object::Name(b"Catalog".to_vec()));
    catalog.set("Pages", Object::Reference(pages_id));
    let catalog = doc.add_object(catalog);

    doc.trailer.set("Root", catalog);
    doc
}

/// How far the current preparation has got, for anything that wants to say so.
///
/// ⚠️ IT USED TO SAY NOTHING FOR TWENTY SECONDS, and a reader can only read
/// that as the app having hung. Global rather than threaded through, because
/// exactly one preparation runs at a time by construction: the slot each is
/// built under is held for the whole of it, so a second arrival waits rather
/// than starting its own.
pub(crate) mod progress {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Mutex;

    pub(crate) static DONE: AtomicUsize = AtomicUsize::new(0);
    pub(crate) static TOTAL: AtomicUsize = AtomicUsize::new(0);

    /// ⚠️ THE INVARIANT THE GLOBAL RESTS ON, ENFORCED RATHER THAN ASSUMED.
    ///
    /// One set of counters can only describe one preparation, and the comment
    /// above says exactly one runs at a time "by construction". That is true of
    /// the app, which has one document open, and false of anything that
    /// prepares two at once: the second `began` resets the first's total and
    /// the first bar jumps backwards or never arrives.
    ///
    /// The test suite is the thing that prepares several at once, and it caught
    /// this by failing: a bar driven to 100 was read back at 79 because another
    /// build had restarted the counters underneath it. Holding this for the
    /// length of a build makes the sentence above true instead of hopeful.
    pub(crate) static ONE_AT_A_TIME: Mutex<()> = Mutex::new(());

    /// How far along, 0 to 100, or None when nothing is being prepared.
    pub(crate) fn percent() -> Option<i32> {
        let total = TOTAL.load(Ordering::Relaxed);
        if total == 0 {
            return None;
        }
        let done = DONE.load(Ordering::Relaxed).min(total);
        Some(((done as f64 / total as f64) * 100.0).round() as i32)
    }

    pub(crate) fn began(total: usize) {
        DONE.store(0, Ordering::Relaxed);
        TOTAL.store(total, Ordering::Relaxed);
    }

    pub(crate) fn reached(done: usize) {
        DONE.store(done, Ordering::Relaxed);
    }

    /// ⚠️ ALWAYS, INCLUDING WHEN THE BUILD FAILED OR FOUND NOTHING. A bar left
    /// on screen because an index came back empty is a worse lie than no bar.
    pub(crate) fn finished() {
        TOTAL.store(0, Ordering::Relaxed);
        DONE.store(0, Ordering::Relaxed);
    }
}

/// Builds one index per font, over the glyphs each is scoped to.
fn build_indexes(wanted: &BTreeMap<String, BTreeSet<u16>>) -> Indexes {
    // ⚠️ ONE INDEX PER FACE, NOT PER NAME. A producer splits a document across
    // several subsets of one font, and a page names each of them separately:
    // `BCDEEE+MyanmarText` and `BCDGEE+MyanmarText` are one face, and so are
    // `ABCDEE+Pyidaungsu` and `ABCDEE+Pyidaungsu,Bold` once the bold falls back
    // to its family's file. Indexing each name apart pays the seconds twice
    // over for the same font, and the two halves cannot even read each other's
    // syllables. Measured on a real three-page file: 40.5s for two names
    // against the one face they share.
    // ⚠️ AND WHICH SCRIPT'S CLUSTERS FILL EACH FACE'S INDEX. The walk that
    // reads a line is not Burmese, but the ENUMERATION that fills its index is,
    // and pouring Burmese syllables through a Devanagari font produces an index
    // that reads nothing. Measured on a real Hindi book: 14.3 seconds to build
    // an index that located 0 of its 1,386 lines.
    let mut per_face: BTreeMap<&'static str, (BTreeSet<u16>, Script)> = BTreeMap::new();
    let mut face_of: BTreeMap<&String, &'static str> = BTreeMap::new();
    for (base_font, glyphs) in wanted {
        // ⚠️ AND WHEN THE NAME SAYS NOTHING, ASK THE GLYPHS. `installed` reads
        // a family out of the font's name, which works for the producers that
        // write one. A real Hindi book names all seven of its fonts
        // `CIDFont+F1`..`CIDFont+F7`, so every line on every page was refused
        // here before the reading even began.
        let Some(path) = installed(base_font).or_else(|| crate::devanagari::face_of(glyphs))
        else {
            continue;
        };
        // ⚠️ FROM THE FACE, NOT FROM WHICHEVER ROUTE FOUND IT. Our own
        // replacement font is named `NirmalaUI` and resolves by NAME, and
        // deciding the script by the route would then fill its index with
        // Burmese and leave the app unable to read what it had just written.
        let script = if crate::devanagari::owns(path) {
            Script::Devanagari
        } else {
            Script::Burmese
        };
        let entry = per_face.entry(path).or_insert_with(|| (BTreeSet::new(), script));
        entry.0.extend(glyphs.iter().copied());
        face_of.insert(base_font, path);
    }

    let mut built: BTreeMap<&'static str, (Arc<(Vec<u8>, crate::reshape::Index)>, Covers)> =
        BTreeMap::new();
    for (path, (glyphs, script)) in &per_face {
        if let Some(face) = build_face(path, glyphs, *script) {
            built.insert(path, face);
        }
    }

    let mut by_font = BTreeMap::new();
    let mut paths = BTreeMap::new();
    for (base_font, path) in face_of {
        if let Some((face, _)) = built.get(path) {
            by_font.insert(base_font.clone(), Arc::clone(face));
            paths.insert(base_font.clone(), path);
        }
    }
    let covered = built.into_iter().map(|(path, (_, covers))| (path, covers)).collect();
    Indexes { by_font, paths, covered, generation: 1 }
}

/// One face's index, built over exactly the glyphs it is given.
///
/// ⚠️ ONE PREPARATION AT A TIME, which the progress counters have always
/// assumed and nothing has ever enforced. See `progress::ONE_AT_A_TIME`.
/// Poisoning is not a reason to refuse to read a document: the counters are two
/// integers and a panicking build leaves them stale at worst.
///
/// ⚠️ AND THE LOCK IS HELD ACROSS THE WHOLE BUILD, which is what lets a
/// caller record a face's coverage the moment this returns: nobody can be
/// half-way through building the same face and about to record a different set.
fn build_face(
    path: &'static str,
    glyphs: &BTreeSet<u16>,
    script: Script,
) -> Option<(Arc<(Vec<u8>, crate::reshape::Index)>, Covers)> {
    let _only_one = progress::ONE_AT_A_TIME
        .lock()
        .unwrap_or_else(|held| held.into_inner());

    let bytes = std::fs::read(path).ok()?;

    // ⚠️ NO PROGRESS CARD FOR A BUILD THIS SHORT. The Devanagari enumeration
    // is bounded to a few thousand clusters and shapes in about fifty
    // milliseconds, against twenty-odd seconds for Burmese, so a reader would
    // see the card appear and vanish for no reason.
    //
    // ⚠️ AND IT IGNORES THE GLYPH SET ENTIRELY, which is why it reports
    // `Everything`: `reading_index` is asked only for a path, so its coverage
    // is total by construction and no later page can ask it for anything it
    // has not already got.
    if script == Script::Devanagari {
        let index = crate::devanagari::reading_index(path)?;
        return Some((Arc::new((bytes, index)), Covers::Everything));
    }

    // ⚠️ REPORTED PER FACE, because that is where the total is known. In
    // practice there is one: the whole reason this groups by face is that every
    // subset of a font shares its index. A document that really did use two
    // unrelated readable faces would fill the bar twice.
    let report = |done: usize, total: usize| {
        if done == 0 {
            progress::began(total);
        } else {
            progress::reached(done);
        }
    };
    let index = crate::reshape::Index::build_reporting(&bytes, None, Some(glyphs), Some(&report));
    progress::finished();

    Some((Arc::new((bytes, index?)), Covers::Only(glyphs.clone())))
}

/// Which script's clusters have to be enumerated to fill a face's index.
///
/// ⚠️ NOT WHAT THE FACE CONTAINS, but what this document is using it FOR. The
/// walk that reads a line is script-agnostic; only the list of candidate
/// clusters is not, and pouring one script's list through another's font
/// produces an index that reads nothing at all.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Script {
    Burmese,
    Devanagari,
}

/// Whether a font by this name is one that can be read.
///
/// ⚠️ ASKED OF A NAME AND NOTHING ELSE, so a caller can decide whether a
/// document is worth reading before it has gone to the trouble of serialising
/// it. Measured: serialising every opened document just to find out cost 26
/// seconds across the test suite, for an answer PDFium already has.
pub(crate) fn can_read(base_font: &str) -> bool {
    installed(base_font).is_some()
}

/// Whether this page draws anything in a font that can be read, which is what
/// says whether building an index for it is worth 17 seconds.
pub(crate) fn worth_reading(doc: &Document, page: ObjectId) -> bool {
    let lines = lines_of(doc, page);
    if lines.iter().any(|l| can_read(&l.base_font)) {
        return true;
    }

    // ⚠️ AND A FONT WHOSE NAME SAYS NOTHING IS STILL WORTH READING. This
    // asked `can_read`, which asks a NAME, and a real Hindi book names all
    // seven of its fonts `CIDFont+F1`..`F7`. Every page of every such book
    // answered no here and settled with nothing, so the app dutifully asked for
    // a recovery that was never prepared and offered the reader no way in.
    //
    // ⚠️ AND ONLY OF THE CID FONTS. Asking a face which glyphs it can name
    // means building its tables, and doing that for every candidate takes over
    // a second the first time. A page of ordinary Latin has nothing to gain
    // from the question: text arrives wearing its own glyph ids only where the
    // font is CID and Identity-H, which is what declaring `/W` says. Measured
    // by a test that timed out waiting for a Latin page to settle.
    let cid: BTreeSet<String> = devanagari_fonts_of(doc, page);
    if cid.is_empty() {
        return false;
    }

    let mut by_font: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
    for line in lines.iter().filter(|l| cid.contains(&l.base_font)) {
        by_font
            .entry(line.base_font.clone())
            .or_default()
            .extend(line.glyphs.iter().copied());
    }
    by_font
        .values()
        .any(|glyphs| crate::devanagari::face_of(glyphs).is_some())
}

/// Everything a page says that can be PROVEN, line by line.
///
/// A line reads as `None` when nothing reproduced its glyphs: an unknown font,
/// a character outside the enumeration, or a producer doing something the
/// index does not model. A refusal is the correct answer there.
pub(crate) fn read_page_with(doc: &Document, page: ObjectId, indexes: &Indexes)
    -> Vec<Reading>
{
    // ⚠️ ONE FACE PER FONT, NOT ONE PER LINE. Parsing Myanmar Text is a few
    // megabytes of work, and building it inside the loop charged that to every
    // line on the page. Measured on one fixture: three seconds a click.
    let faces: BTreeMap<&str, (rustybuzz::Face, &crate::reshape::Index)> = indexes
        .by_font
        .iter()
        .filter_map(|(name, face)| {
            let (bytes, index) = face.as_ref();
            Some((name.as_str(), (rustybuzz::Face::from_slice(bytes, 0)?, index)))
        })
        .collect();
    let widths = fonts_of(doc, page);
    lines_of(doc, page)
        .iter()
        .map(|line| {
            let read = faces.get(line.base_font.as_str()).and_then(|(face, index)| {
                // ⚠️ THE LINE THE PAGE CAN ACTUALLY DRAW, which is not always
                // the line the file wrote down. See `without_phantoms`.
                let mine = widths.get(&line.resource).and_then(|(_, w)| w.as_ref());
                let cleaned = without_phantoms(line, face, mine);
                let line = cleaned.as_ref().unwrap_or(line);
                let pieces = pieces_of(index, face, line)?;
                let clusters = match mine {
                    Some(w) => clusters_of(line, &pieces, face, w),
                    None => Vec::new(),
                };
                Some((said_by(&pieces), clusters))
            });
            let (text, clusters) = match read {
                Some((text, clusters)) => (Some(text), clusters),
                None => (None, Vec::new()),
            };
            let (top, bottom) = faces
                .get(line.base_font.as_str())
                .map(|(face, _)| reach_of(face, line.size))
                .unwrap_or((0.0, 0.0));
            // ⚠️ EVERY NUMBER HERE IS IN THE PAGE'S FRAME, and none of
            // them used to be. `Line::y` is the stream's own placement, and the
            // app compares what it gets back against a baseline PDFium read off
            // the PAGE. On a book drawn under `cm [0.75 0 0 -0.75 0 841.92]`
            // that put every baseline off the page and every box a third too
            // wide: measured, ALL 1,386 of its lines, and the app's merge then
            // left 36 of 41 of PDFium's own fragments lying on top of the
            // lines that were supposed to replace them.
            //
            // A flip swaps top and bottom, so they are sorted after mapping
            // rather than assumed. In PDF space the visual TOP is the larger
            // number, which is the way round the caller's `down` expects.
            // ⚠️ THE REACH IS A DIRECTION IN TEXT SPACE, so it goes
            // through the line's own matrix as well as the page's. Measured on
            // a book whose text matrix flips to cancel the page's flip: putting
            // it through the page transform alone sent the ascender DOWNWARDS,
            // and the caret was drawn in the gap below the line rather than on
            // the type. PDFium had the box at 0.1265..0.1399 and this reported
            // 0.1354..0.1590, which is almost entirely under the baseline.
            let reach_up = line.page_y + line.on_page.along(0.0, top).1;
            let reach_down = line.page_y + line.on_page.along(0.0, bottom).1;
            let scale = line.on_page.vertical_scale();
            Reading {
                y: line.page_y,
                x: line.ctm.on(line.x, line.y).0,
                size: line.size * scale,
                top: reach_up.max(reach_down),
                bottom: reach_up.min(reach_down),
                font: line.base_font.clone(),
                text,
                clusters: clusters
                    .into_iter()
                    .map(|c| Cluster {
                        left: line.ctm.on(c.left, line.y).0,
                        right: line.ctm.on(c.right, line.y).0,
                        ..c
                    })
                    .collect(),
            }
        })
        .collect()
}

/// Every line sitting on this baseline.
///
/// ⚠️ A BASELINE DOES NOT IDENTIFY A LINE, and this does not pretend it
/// does. Two columns share one, and so do a shaped run and the invisible
/// searchable run Ayaan writes beside it: measured, taking the first match on a
/// Path B page picked the invisible one. It is the caller's expected TEXT that
/// settles which was meant, and this only narrows the field.
pub(crate) fn lines_at(lines: &[Line], baseline: f64) -> impl Iterator<Item = &Line> {
    const NEAR: f64 = 0.5;
    // ⚠️ THE PAGE'S FRAME, which is the one the caller was handed and the
    // only one it can read a baseline off PDFium in. See `Line::page_y`.
    lines.iter().filter(move |l| (l.page_y - baseline).abs() < NEAR)
}

/// The same, building the indexes first. For a caller with nothing prepared.
pub(crate) fn read_page(doc: &Document, page: ObjectId) -> Vec<Reading> {
    read_page_with(doc, page, &indexes_for(doc, page))
}

#[cfg(test)]
mod tests {
    use super::*;

    use lopdf::content::{Content, Operation};
    use lopdf::dictionary;

    const MYANMAR_TEXT: &str = r"C:\Windows\Fonts\mmrtext.ttf";

    /// A one-page document whose text is the operations given, drawn with a
    /// Type0 font that declares one width for every glyph.
    ///
    /// Enough of a PDF for the reader under test: a page, a font resource with
    /// a `/BaseFont` and a `/W`, and a content stream.
    fn a_page(operations: Vec<Operation>, width: f64) -> (Document, ObjectId) {
        let mut doc = Document::with_version("1.7");
        let descendant = doc.add_object(dictionary! {
            "Type" => "Font",
            "Subtype" => "CIDFontType2",
            "BaseFont" => "BCDEEE+MyanmarText",
            "W" => vec![0.into(), vec![width.into()].into()],
            "DW" => width,
        });
        let font = doc.add_object(dictionary! {
            "Type" => "Font",
            "Subtype" => "Type0",
            "BaseFont" => "BCDEEE+MyanmarText",
            "Encoding" => "Identity-H",
            "DescendantFonts" => vec![descendant.into()],
        });
        let content = Content { operations };
        let stream = doc.add_object(lopdf::Stream::new(
            dictionary! {},
            content.encode().unwrap(),
        ));
        let pages_id = doc.new_object_id();
        let page = doc.add_object(dictionary! {
            "Type" => "Page",
            "Parent" => pages_id,
            "Contents" => stream,
            "Resources" => dictionary! { "Font" => dictionary! { "F1" => font } },
        });
        doc.objects.insert(pages_id, lopdf::Object::Dictionary(dictionary! {
            "Type" => "Pages",
            "Kids" => vec![page.into()],
            "Count" => 1,
        }));
        let catalog = doc.add_object(dictionary! {
            "Type" => "Catalog",
            "Pages" => pages_id,
        });
        doc.trailer.set("Root", catalog);
        (doc, page)
    }

    fn show(glyphs: &[u16]) -> Operation {
        let bytes: Vec<u8> = glyphs.iter().flat_map(|g| g.to_be_bytes()).collect();
        Operation::new("TJ", vec![vec![lopdf::Object::String(
            bytes,
            lopdf::StringFormat::Hexadecimal,
        )].into()])
    }

    fn place(x: f64, y: f64) -> Operation {
        Operation::new("Tm", vec![
            1.into(), 0.into(), 0.into(), 1.into(), x.into(), y.into(),
        ])
    }

    fn pick(x: f64) -> Operation {
        Operation::new("Tf", vec!["F1".into(), x.into()])
    }

    /// ⚠️ ONE VISUAL LINE, HOWEVER MANY TIMES IT IS PLACED. A producer draws a
    /// line in pieces, each with its own `Tm`, and reading them apart cuts
    /// syllables across the joins.
    #[test]
    fn separately_placed_pieces_of_one_line_are_one_line() {
        // Two placements on the same baseline, the second starting exactly
        // where the first ends: 72 + 3 glyphs of 500/1000 em at 12pt = 90.
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&[10, 11, 12]),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            pick(12.0),
            place(90.0, 700.0),
            show(&[13, 14]),
            Operation::new("ET", vec![]),
        ], 500.0);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1, "two placements on one baseline read as two lines");
        assert_eq!(lines[0].glyphs, vec![10, 11, 12, 13, 14]);
        assert!(lines[0].breaks.is_empty(),
            "a join with no gap invented a space: {:?}", lines[0].breaks);
    }

    /// ⚠️ AND A GAP BETWEEN THE PIECES IS A WORD SPACE. It is drawn by placing
    /// the next piece further along, so there is no space glyph to find.
    #[test]
    fn a_gap_between_two_placements_is_a_word_space() {
        // The first piece ends at 90; the second starts at 96, six points on.
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&[10, 11, 12]),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            pick(12.0),
            place(96.0, 700.0),
            show(&[13, 14]),
            Operation::new("ET", vec![]),
        ], 500.0);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1);
        let found: Vec<usize> = lines[0].breaks.iter().map(|b| b.at).collect();
        assert_eq!(found, vec![3], "the gap was not read as a space");
        assert!((lines[0].breaks[0].points - 6.0).abs() < 0.01,
            "the space came back {:?} wide, not the 6 points that were left",
            lines[0].breaks[0].points);
    }

    /// Placements of one glyph each, `gap` points apart, all on one baseline.
    ///
    /// Every glyph is 500/1000 of an em and the type is 12 point, so a piece is
    /// 6 points wide and the next one starts 6 + `gap` further along.
    fn a_line_placed_a_piece_at_a_time(gaps: &[f64]) -> (Document, ObjectId) {
        let mut ops = Vec::new();
        let mut x = 72.0f64;
        for (i, gap) in std::iter::once(&0.0).chain(gaps).enumerate() {
            x += if i == 0 { 0.0 } else { 6.0 + gap };
            ops.push(Operation::new("BT", vec![]));
            ops.push(pick(12.0));
            ops.push(place(x, 700.0));
            ops.push(show(&[10 + i as u16]));
            ops.push(Operation::new("ET", vec![]));
        }
        a_page(ops, 500.0)
    }

    /// ⚠️ A LINE GAPPED THE SAME SMALL AMOUNT AT EVERY JOIN IS LETTER SPACED.
    /// Measured on a real heading: seventeen placements, sixteen gaps, all
    /// between 0.284 and 0.296 of the type size, over text with no space in it
    /// anywhere. The reader was calling every one of them a word space and
    /// offering `\u{1006}\u{100A}\u{103A} \u{1019}\u{103C}\u{1031}\u{102C}\u{1004}\u{103A}\u{1038}` for a name that has no space in it.
    #[test]
    fn a_line_gapped_the_same_way_at_every_join_is_letter_spaced() {
        // 0.29 of 12 point type, four times over.
        let (doc, page) = a_line_placed_a_piece_at_a_time(&[3.48, 3.48, 3.5, 3.46]);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1, "the pieces did not join");
        assert!(lines[0].tracked, "a letter-spaced line was read as worded");

        // ⚠️ AND THE GAPS ARE STILL THERE. They are on the page whatever they
        // mean, and the geometry has to cross them or every cluster after the
        // first is placed short.
        assert_eq!(lines[0].breaks.len(), 4,
            "the gaps were thrown away instead of being reinterpreted");
    }

    /// ⚠️ AND A JUSTIFIED LINE IS NOT, however even its spaces are. Measured
    /// on a real page, one line's seven word gaps sat between 0.463 and 0.490
    /// of the type size: uniformity alone would have taken the spaces out of
    /// ordinary text. What tells them apart is width, and that letter spacing
    /// gaps EVERY join.
    #[test]
    fn a_line_whose_even_gaps_are_word_spaces_keeps_them() {
        // The same evenness, but half again as wide as a space.
        let (doc, page) = a_line_placed_a_piece_at_a_time(&[5.7, 5.8, 5.75, 5.72]);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1);
        assert!(!lines[0].tracked,
            "a line of word spaces was mistaken for letter spacing");
    }

    /// ⚠️ AND A LINE GAPPED ONLY BETWEEN ITS WORDS IS NOT, however narrow the
    /// gaps are. Letter spacing is a setting applied to the whole line, so it
    /// shows up at every join; a page that draws most of its syllables together
    /// and breaks only at the spaces is doing the ordinary thing.
    #[test]
    fn a_line_gapped_at_only_some_of_its_joins_keeps_its_spaces() {
        // Five joins, but only three of them gapped: the other two are drawn
        // hard against each other.
        let (doc, page) = a_line_placed_a_piece_at_a_time(&[3.48, 0.0, 3.48, 0.0, 3.48]);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1);
        assert!(!lines[0].tracked,
            "a line gapped only at its spaces was read as letter spaced");
    }

    /// ⚠️ TWO GAPS ARE A COINCIDENCE. This is about a line SET that way, and
    /// a two-piece line has too little of it to tell.
    #[test]
    fn a_line_of_two_pieces_is_never_called_letter_spaced() {
        let (doc, page) = a_line_placed_a_piece_at_a_time(&[3.48]);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1);
        assert!(!lines[0].tracked);
    }

    /// ⚠️ AND WHAT IT COMES TO IN THE READING: no space, where before there
    /// was one at every join.
    #[test]
    fn a_letter_spaced_line_reads_as_the_word_it_is() {
        let Some(bytes) = std::fs::read(MYANMAR_TEXT).ok() else { return };
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();

        // Three syllables of one word, each drawn in a placement of its own.
        const A: &str = "\u{1006}\u{100A}\u{103A}";
        const B: &str = "\u{1019}\u{103C}\u{1031}\u{102C}\u{1004}\u{103A}\u{1038}";
        // ⚠️ SPELLED THE WAY THE INDEX PROVES IT. Two orderings of the
        // same two marks draw identically and `Index::build` keeps one of
        // them, so asking for the other asks about the index rather than
        // about spaces.
        const C: &str = "\u{1014}\u{103E}\u{1004}\u{1037}\u{103A}";
        let pieces = [A, B, C].map(|s| crate::reshape::draws(&face, s));

        let mut chars: BTreeSet<char> = BTreeSet::new();
        for s in [A, B, C] {
            chars.extend(s.chars());
        }
        let index = crate::reshape::Index::build(&bytes, Some(&chars), None).unwrap();

        let mut glyphs: Vec<u16> = Vec::new();
        let mut breaks: Vec<Break> = Vec::new();
        for piece in &pieces {
            if !glyphs.is_empty() {
                breaks.push(Break { at: glyphs.len(), points: 3.48 });
            }
            glyphs.extend(piece);
        }

        let mut line = Line {
            y: 700.0,
            x: 72.0,
            page_y: 700.0,
            ctm: Matrix::IDENTITY,
            on_page: Matrix::translation(0.0, 700.0),
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            nudges: Vec::new(),
            glyphs,
            adjust: 0.0,
            tracked: false,
            breaks,
            drawn_by: Vec::new(),
        };

        assert_eq!(read_line(&index, &face, &line).as_deref(),
            Some(format!("{A} {B} {C}").as_str()),
            "the control is wrong: gaps should read as spaces when they mean spaces");

        line.tracked = true;
        assert_eq!(read_line(&index, &face, &line).as_deref(),
            Some(format!("{A}{B}{C}").as_str()),
            "a letter-spaced line still came back with spaces in it");
    }

    /// A different baseline is a different line, however close it is drawn.
    #[test]
    fn a_new_baseline_is_a_new_line() {
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&[10, 11]),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 686.0),
            show(&[12, 13]),
            Operation::new("ET", vec![]),
        ], 500.0);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0].glyphs, vec![10, 11]);
        assert_eq!(lines[1].glyphs, vec![12, 13]);
    }

    /// ⚠️ `Td` AND `T*` ARE RELATIVE, and a reader watching only `Tm` misses
    /// every line a producer moves to by leading.
    #[test]
    fn a_line_reached_by_leading_is_found() {
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            Operation::new("TL", vec![14.into()]),
            show(&[10]),
            Operation::new("T*", vec![]),
            show(&[11]),
            Operation::new("Td", vec![0.into(), (-14).into()]),
            show(&[12]),
            Operation::new("ET", vec![]),
        ], 500.0);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 3, "leading did not start new lines: {:?}",
            lines.iter().map(|l| l.y).collect::<Vec<_>>());
        assert!((lines[0].y - 700.0).abs() < 0.01);
        assert!((lines[1].y - 686.0).abs() < 0.01);
        assert!((lines[2].y - 672.0).abs() < 0.01);
    }

    /// A horizontal `Td` steps ALONG a line, and is not a new one.
    #[test]
    fn a_horizontal_step_stays_on_the_line() {
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&[10]),
            Operation::new("Td", vec![20.into(), 0.into()]),
            show(&[11]),
            Operation::new("ET", vec![]),
        ], 500.0);

        let lines = lines_of(&doc, page);
        assert_eq!(lines.len(), 1, "a step along the line started a new one");
    }

    /// The reading of a real line, end to end: glyphs the font actually draws,
    /// a space found by geometry, and the author's text back out.
    #[test]
    fn a_line_reads_back_with_its_word_space() {
        let Some(bytes) = std::fs::read(MYANMAR_TEXT).ok() else { return };
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();

        const LEFT: &str = "အရုဏ်ဦး";
        const RIGHT: &str = "ရောင်နီသည်";
        let left = crate::reshape::draws(&face, LEFT);
        let right = crate::reshape::draws(&face, RIGHT);

        let mut chars: BTreeSet<char> = LEFT.chars().collect();
        chars.extend(RIGHT.chars());
        let mut glyphs: BTreeSet<u16> = left.iter().copied().collect();
        glyphs.extend(&right);
        let index = crate::reshape::Index::build(&bytes, Some(&chars), Some(&glyphs)).unwrap();

        let line = Line {
            y: 700.0,
            x: 72.0,
            page_y: 700.0,
            ctm: Matrix::IDENTITY,
            on_page: Matrix::translation(0.0, 700.0),
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            nudges: Vec::new(),
            glyphs: left.iter().copied().chain(right.iter().copied()).collect(),
            adjust: 0.0,
            tracked: false,
            breaks: vec![Break { at: left.len(), points: 3.0 }],
            drawn_by: Vec::new(),
        };
        assert_eq!(read_line(&index, &face, &line).as_deref(),
            Some(format!("{LEFT} {RIGHT}").as_str()));
    }

    /// ⚠️ A BREAK THAT CANNOT BE PROVEN IS PASSED OVER, NOT OBEYED. A producer
    /// may start a placement mid-syllable, and cutting there leaves two halves
    /// of a cluster that nothing spells. Losing the space is worth far less
    /// than losing the line.
    #[test]
    fn a_break_inside_a_syllable_costs_a_space_and_not_the_line() {
        let Some(bytes) = std::fs::read(MYANMAR_TEXT).ok() else { return };
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();

        // "မြန်မာ", whose first cluster draws two glyphs.
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let drawn = crate::reshape::draws(&face, WORD);
        let chars: BTreeSet<char> = WORD.chars().collect();
        let index = crate::reshape::Index::build(&bytes, Some(&chars), None).unwrap();

        let line = Line {
            y: 700.0,
            x: 72.0,
            page_y: 700.0,
            ctm: Matrix::IDENTITY,
            on_page: Matrix::translation(0.0, 700.0),
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            nudges: Vec::new(),
            glyphs: drawn,
            adjust: 0.0,
            tracked: false,
            // Straight through the middle of the first cluster.
            breaks: vec![Break { at: 1, points: 3.0 }],
            drawn_by: Vec::new(),
        };
        assert_eq!(read_line(&index, &face, &line).as_deref(), Some(WORD),
            "an impossible break took the line down with it");
    }


    /// Phase 2 on the real thing: does the geometry land where the page draws?
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn the_clusters_of_a_real_word_page_land_where_the_text_is() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = indexes_for(&doc, page);
        let lines = lines_of(&doc, page);
        let read = read_page_with(&doc, page, &indexes);
        let fonts = fonts_of(&doc, page);

        let mut placed = 0usize;
        for (line, reading) in lines.iter().zip(read.iter()) {
            let Some(text) = reading.text.as_deref() else { continue };
            placed += 1;

            // Contiguous, in order, on character boundaries, covering it all.
            assert_eq!(reading.clusters.first().map(|c| c.from), Some(0));
            assert_eq!(reading.clusters.last().map(|c| c.to), Some(text.len()));
            for pair in reading.clusters.windows(2) {
                assert_eq!(pair[0].to, pair[1].from, "a gap in {text:?}");
            }
            for c in &reading.clusters {
                assert!(text.is_char_boundary(c.from) && text.is_char_boundary(c.to),
                    "cluster {c:?} cuts a character of {text:?}");
            }

            // And the line ends where the page says it ends.
            let Some((_, Some(w))) = fonts.get(&line.resource) else { continue };
            // ⚠️ AND NONE OF IT IS OFF THE PAGE. Independent of the
            // arithmetic below, which shares its inputs with the code it is
            // checking: a bug that stamped a line with the x of its LAST
            // placement put clusters at 752 points on a 595 point page.
            for c in &reading.clusters {
                assert!(c.left >= 0.0 && c.right <= 595.5,
                    "cluster {c:?} is off the page");
            }

            // Where the pen finishes: every glyph it drew, every number
            // written between them, and every skip the page asked for.
            let drawn: f64 = line.glyphs.iter().map(|g| w.of(*g)).sum();
            let gaps: f64 = line.breaks.iter().map(|b| b.points).sum();

            // ⚠️ MINUS ANYTHING THE PEN CROSSED AFTER THE LAST INK. A code the
            // face cannot draw still advances by whatever the file declares for
            // it, and this page ends one line with a full em of exactly that.
            // Clusters bound INK: there is nothing out there for one to hold
            // and no caret position past the end of the text, so the reading
            // stops at the last thing anybody can see and so does this.
            let face = indexes
                .by_font
                .get(&line.base_font)
                .and_then(|face| rustybuzz::Face::from_slice(&face.0, 0));
            let phantom = face.map(|f| f.number_of_glyphs()).unwrap_or(u16::MAX);
            let trailing: f64 = line
                .glyphs
                .iter()
                .rev()
                .take_while(|g| **g >= phantom)
                .map(|g| w.of(*g))
                .sum();

            let ends = line.x
                + (drawn - trailing + line.adjust) / 1000.0 * line.size
                + gaps;
            let ours = reading.clusters.last().unwrap().right;
            println!("line at y={:7.1}: {:2} clusters, {} skips worth {:6.2}, ends at {:8.2} against {:8.2}, out by {:5.2}",
                line.y, reading.clusters.len(), line.breaks.len(), gaps,
                ours, ends, (ours - ends).abs());
            assert!((ours - ends).abs() < 0.5,
                "the clusters end {:.2}pt from where the line does", (ours - ends).abs());
        }
        println!("{placed} lines placed");
        assert!(placed > 0);
    }

    /// The measurement, against a real Word-produced page that is not in this
    /// repository. Ignored because it needs that file; kept because it is the
    /// only end-to-end number this work has, and the next change to any of it
    /// should be checked against the same page.
    ///
    /// Last run: 18 lines, 16 read, in about 18 seconds, and the readings are
    /// the author's text. The two refusals are one stray run placed 3 points
    /// off its neighbours' baseline, which also stops the line it interrupts
    /// from joining up, and one line not yet diagnosed.
    ///
    /// ⚠️ THE REMAINING SPACE ERRORS ARE BOTH KINDS, and neither is verifiable:
    /// a space between two placed runs draws no glyph, so nothing can be shaped
    /// to check it. One line runs `မှ` into `အရုဏ်` and another splits `နေ့သစ်`.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn the_measurement_on_a_real_word_page() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("the file is not here");
            return;
        }
        let started = std::time::Instant::now();
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        let read = read_page(&doc, page);
        let proven = read.iter().filter(|r| r.text.is_some()).count();
        println!("{proven} of {} lines, in {:?}", read.len(), started.elapsed());
        for (n, r) in read.iter().enumerate() {
            match &r.text {
                Some(text) => println!("  {n:>2} y={:7.1}  {text}", r.y),
                None => println!("  {n:>2} y={:7.1}  REFUSED", r.y),
            }
        }
        // ⚠️ ALL OF THEM, ON THIS FILE. It was 16 of 18 before a phantom code
        // and a lifted mark were understood; leaving the bar at half would let
        // either of them come back unnoticed.
        assert_eq!(proven, read.len(), "read {proven} of {} lines", read.len());
    }

    /// What the lines recovery DECLINED are made of, and what the lines around
    /// them are made of, so the two can be compared. Diagnostic: it asserts
    /// nothing, because the numbers are what this phase is here to change.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn what_the_refused_lines_are_made_of() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = indexes_for(&doc, page);
        let lines = lines_of(&doc, page);
        let read = read_page_with(&doc, page, &indexes);

        for (n, (line, r)) in lines.iter().zip(read.iter()).enumerate() {
            let mark = if r.text.is_some() { " " } else { "*" };
            println!(
                "{mark}{n:>2} y={:8.2} x={:7.2} size={:5.2} glyphs={:>3} skips={:>2} font={} ops={:?}",
                line.y, line.x, line.size, line.glyphs.len(),
                line.breaks.len(), line.base_font, line.drawn_by);
            if r.text.is_none() {
                println!("      glyph ids: {:?}", line.glyphs);
                // What does each single glyph spell on its own?
                if let Some(entry) = indexes.by_font.get(&line.base_font) {
                    let (bytes, index) = entry.as_ref();
                    let face = rustybuzz::Face::from_slice(bytes, 0).unwrap();
                    let one: Vec<String> = line.glyphs.iter()
                        .map(|g| crate::reshape::prove(&face, index, &[*g])
                            .unwrap_or_else(|| "?".into()))
                        .collect();
                    println!("      one at a time: {one:?}");
                    // And the longest prefix that CAN be proven.
                    let mut best = 0;
                    for cut in 1..=line.glyphs.len() {
                        if crate::reshape::prove(&face, index, &line.glyphs[..cut]).is_some() {
                            best = cut;
                        }
                    }
                    println!("      longest provable prefix: {best} of {}", line.glyphs.len());
                }
            }
        }
    }

    /// A page whose declared widths are the FONT'S OWN, so a mark really does
    /// advance by nothing and a letter really does advance by its own width.
    ///
    /// `phantom` is a code the face has no glyph for, declared a full em wide
    /// the way a producer's default width declares everything it did not list.
    fn a_page_measured_by(face: &rustybuzz::Face, operations: Vec<Operation>)
        -> (Document, ObjectId)
    {
        use rustybuzz::ttf_parser::GlyphId;

        let upem = face.units_per_em() as f64;
        let mut widths: Vec<lopdf::Object> = Vec::new();
        for id in 0..face.number_of_glyphs() {
            let advance = face.glyph_hor_advance(GlyphId(id)).unwrap_or(0) as f64;
            widths.push((id as i64).into());
            widths.push(vec![(advance / upem * 1000.0).into()].into());
        }

        let mut doc = Document::with_version("1.7");
        let descendant = doc.add_object(dictionary! {
            "Type" => "Font",
            "Subtype" => "CIDFontType2",
            "BaseFont" => "BCDEEE+MyanmarText",
            "W" => widths,
            "DW" => 1000.0,
        });
        let font = doc.add_object(dictionary! {
            "Type" => "Font",
            "Subtype" => "Type0",
            "BaseFont" => "BCDEEE+MyanmarText",
            "Encoding" => "Identity-H",
            "DescendantFonts" => vec![descendant.into()],
        });
        let stream = doc.add_object(lopdf::Stream::new(
            dictionary! {},
            Content { operations }.encode().unwrap(),
        ));
        let pages_id = doc.new_object_id();
        let page = doc.add_object(dictionary! {
            "Type" => "Page",
            "Parent" => pages_id,
            "MediaBox" => vec![0.into(), 0.into(), 595.into(), 842.into()],
            "Contents" => stream,
            "Resources" => dictionary! { "Font" => dictionary! { "F1" => font } },
        });
        doc.objects.insert(pages_id, lopdf::Object::Dictionary(dictionary! {
            "Type" => "Pages",
            "Kids" => vec![page.into()],
            "Count" => 1,
        }));
        let catalog = doc.add_object(dictionary! {
            "Type" => "Catalog",
            "Pages" => pages_id,
        });
        doc.trailer.set("Root", catalog);
        (doc, page)
    }

    /// ⚠️ WHICH FRAME THE READING COMES BACK IN. `Reading.y` is `Line::y`,
    /// the stream's own placement, and the app compares it against a baseline
    /// PDFium read off the PAGE. `Line::page_y` is the same placement with the
    /// transform applied, and its doc comment says it is the only address an
    /// outside caller may use.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn which_frame_a_hindi_reading_comes_back_in() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let (page_left, page_top, page_w) = page_box(&doc, page).unwrap();
        println!("page box: left {page_left}, top {page_top}, width {page_w}");

        let lines = lines_of(&doc, page);
        let mut differ = 0;
        for line in &lines {
            if (line.y - line.page_y).abs() > 0.01 {
                differ += 1;
            }
        }
        println!("{} lines, {differ} whose stream placement and page placement differ",
            lines.len());

        for line in lines.iter().take(6) {
            println!("   y {:8.2} -> normalized {:7.4}   |   page_y {:8.2} -> normalized {:7.4}",
                line.y, (page_top - line.y) / page_w,
                line.page_y, (page_top - line.page_y) / page_w);
        }
    }

    /// The transform a real Hindi book is drawn under, and what it does.
    const FLIPPED: Matrix = Matrix([0.75, 0.0, 0.0, -0.75, 0.0, 841.92]);

    /// ⚠️ A HEIGHT IS A LENGTH, AND A FLIP DOES NOT MAKE IT NEGATIVE, and the
    /// same flip reverses which way a line reaches. Both were got wrong once:
    /// sorting top and bottom the obvious way round put every box upside down.
    #[test]
    fn a_flipped_and_scaled_page_maps_a_lines_geometry_onto_it() {
        // A baseline the stream puts at 111.04 is at 758.64 on the page.
        assert!((FLIPPED.on(0.0, 111.04).1 - 758.64).abs() < 0.01);

        // ⚠️ AND x IS SCALED, NOT MERELY SHIFTED. Reporting the stream's own
        // x made every caret box a third too wide.
        assert!((FLIPPED.on(100.0, 0.0).0 - 75.0).abs() < 1e-9);

        assert!((FLIPPED.vertical_scale() - 0.75).abs() < 1e-9);

        let reaches_up = FLIPPED.on(0.0, 111.04 + 8.0).1;
        let reaches_down = FLIPPED.on(0.0, 111.04 - 2.0).1;
        assert!(reaches_up < reaches_down,
            "the flip did not reverse the reach, so top and bottom need no sorting \
             and this test is not testing anything");
    }

    /// ⚠️ A REACH IS IN TEXT SPACE, AND TEXT SPACE IS NOT USER SPACE. A page
    /// that flips is usually drawn with a text matrix that flips back, so the
    /// type reads the right way up; the page transform ALONE therefore has the
    /// wrong sign for it. Putting the ascender through the page transform on
    /// its own sent it below the baseline, and the caret was drawn in the gap
    /// under the line instead of on the type. The user saw that before a test
    /// did, which is why this one is here.
    #[test]
    fn a_reach_above_the_baseline_stays_above_it_on_the_page() {
        // The real book: the page flips and scales, the text matrix flips back.
        let text = Matrix([1.0, 0.0, 0.0, -1.0, 96.0, 111.04]);
        let on_page = text.then(FLIPPED);

        assert!(on_page.along(0.0, 10.0).1 > 0.0,
            "an ascender ended up below the baseline");
        assert!(on_page.along(0.0, -3.0).1 < 0.0,
            "a descender ended up above the baseline");

        // ⚠️ AND THE PAGE TRANSFORM ALONE GETS IT WRONG, which is the whole
        // point: without this the test passes on the broken version too.
        assert!(FLIPPED.along(0.0, 10.0).1 < 0.0,
            "the page transform does not flip, so this proves nothing");

        // The composed scale is what a type size has to be measured in.
        assert!((on_page.vertical_scale() - 0.75).abs() < 1e-9);
    }

    /// ⚠️ AND THE LINE HAS TO CARRY THAT TRANSFORM OUT OF THE STREAM. This
    /// wraps a fixture's content in the transform above and asks where its
    /// lines say they are. Without the `cm` being tracked, `page_y` is just `y`
    /// and every one of them is off the page.
    #[test]
    fn a_line_drawn_under_a_transform_reports_where_the_page_puts_it() {
        let mut doc = lopdf::Document::load("tests/fixtures/sample_lines.pdf").unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let plain = lines_of(&doc, page);
        assert!(!plain.is_empty(), "the fixture draws no lines");

        let content = doc.get_page_content(page);
        let mut wrapped = b"q 0.75 0 0 -0.75 0 841.92 cm".to_vec();
        wrapped.push(b'\n');
        wrapped.extend_from_slice(&content);
        wrapped.extend_from_slice(b"\nQ");
        doc.change_page_content(page, wrapped).unwrap();

        let under = lines_of(&doc, page);
        assert_eq!(under.len(), plain.len(), "the transform changed which lines there are");
        for (before, after) in plain.iter().zip(&under) {
            assert!((after.y - before.y).abs() < 1e-9,
                "the stream's own placement should not have moved");
            // The added transform composes ON TOP of whatever the fixture
            // already does, so the line's new place is its old place put
            // through it. This one has no shear, so y does not depend on x.
            let want = FLIPPED.on(0.0, before.page_y).1;
            assert!((after.page_y - want).abs() < 0.001,
                "line at {} reports {} on the page, and the transform puts it at {want}",
                before.page_y, after.page_y);
        }
    }

    /// What the matrices actually are on the real book, before guessing at how
    /// to compose them.
    #[test]
    #[ignore = "probe, and needs a PDF that is not in this repository"]
    fn what_matrices_a_hindi_line_is_drawn_under() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        for line in lines_of(&doc, page).iter().take(4) {
            println!("size {:.3}  y {:.2} -> page_y {:.2}  x {:.2}",
                line.size, line.y, line.page_y, line.x);
            println!("   ctm {:?}", line.ctm.0);
        }
    }

    /// ⚠️ WHAT THE LINES THAT DO NOT READ ACTUALLY ARE. 3,114 of 8,499 is
    /// the honest figure for the book, and on its own it reads as a third. But
    /// a "line" here is a PLACEMENT, and this book places its page numbers, its
    /// punctuation and its Latin in fonts no Devanagari face has any business
    /// naming. This asks how many of the unread ones were ever candidates.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_the_lines_that_do_not_read_are() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let indexes = indexes_for_document(&doc);

        let mut no_index = 0usize;
        let mut refused = 0usize;
        let mut read = 0usize;
        let mut one_glyph = 0usize;
        let mut by_font: BTreeMap<String, (usize, usize)> = BTreeMap::new();

        for (_, &page) in doc.get_pages().iter() {
            let readings = read_page_with(&doc, page, &indexes);
            for (line, reading) in lines_of(&doc, page).iter().zip(&readings) {
                let entry = by_font.entry(line.base_font.clone()).or_default();
                entry.1 += 1;
                if reading.text.is_some() {
                    read += 1;
                    entry.0 += 1;
                } else if indexes.index_for(&line.base_font).is_none() {
                    no_index += 1;
                } else if line.glyphs.len() <= 1 {
                    one_glyph += 1;
                } else {
                    refused += 1;
                }
            }
        }
        println!("{read} read");
        println!("{no_index} in a font no Devanagari face names at all");
        println!("{one_glyph} a single glyph in a font that IS named");
        println!("{refused} refused though the font is named and there is more than one glyph");
        println!("\nper font, read of total:");
        for (font, (ok, all)) in &by_font {
            println!("   {font:16} {ok:5} of {all:5}   index: {}",
                if indexes.index_for(font).is_some() { "yes" } else { "no" });
        }
    }

    fn an_index_of(bytes: &[u8], text: &str) -> Indexes {
        let chars: BTreeSet<char> = text.chars().collect();
        let index = crate::reshape::Index::build(bytes, Some(&chars), None).unwrap();
        Indexes {
            by_font: [("BCDEEE+MyanmarText".to_string(), std::sync::Arc::new((bytes.to_vec(), index)))]
                .into_iter()
                .collect(),
            paths: BTreeMap::new(),
            ..Default::default()
        }
    }

    /// ⚠️ A CODE THE FACE CANNOT DRAW MUST NOT COST THE LINE. Measured on a
    /// real Word page: one line ended with code 8224 in a face that has 1028
    /// glyphs. Nothing can put ink down for it, every one of that line's other
    /// 83 glyphs proved on its own, and the single phantom refused the whole
    /// line of the author's text.
    #[test]
    fn a_code_the_font_cannot_draw_does_not_refuse_the_line() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();

        let mut glyphs = crate::reshape::draws(&face, WORD);
        let phantom = face.number_of_glyphs() + 7;
        glyphs.push(phantom);

        let (doc, page) = a_page_measured_by(&face, vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&glyphs),
            Operation::new("ET", vec![]),
        ]);

        let read = read_page_with(&doc, page, &an_index_of(&bytes, WORD));
        let one = read.into_iter().next().expect("no line");
        assert_eq!(one.text.as_deref(), Some(WORD),
            "one code nothing can draw refused the whole line");
    }

    /// ⚠️ AND IT STILL MOVES THE PEN. That phantom was declared a full em
    /// wide, so simply dropping it left everything after it an em to the left,
    /// which is a caret on the wrong letter.
    #[test]
    fn a_code_the_font_cannot_draw_still_takes_up_its_declared_room() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        let index = an_index_of(&bytes, WORD);

        let drawn = crate::reshape::draws(&face, WORD);
        let phantom = face.number_of_glyphs() + 7;

        // The same word twice, and again with a phantom between the halves.
        let mut interrupted = drawn.clone();
        interrupted.insert(2, phantom);

        let clean = {
            let (doc, page) = a_page_measured_by(&face, vec![
                Operation::new("BT", vec![]), pick(12.0), place(72.0, 700.0),
                show(&drawn), Operation::new("ET", vec![]),
            ]);
            read_page_with(&doc, page, &index).into_iter().next().unwrap()
        };
        let broken = {
            let (doc, page) = a_page_measured_by(&face, vec![
                Operation::new("BT", vec![]), pick(12.0), place(72.0, 700.0),
                show(&interrupted), Operation::new("ET", vec![]),
            ]);
            read_page_with(&doc, page, &index).into_iter().next().unwrap()
        };

        // A full em at 12 point, exactly what the default width declares.
        let room = broken.clusters.last().unwrap().right
            - clean.clusters.last().unwrap().right;
        assert!((room - 12.0).abs() < 0.01,
            "the phantom took up {room:.2} points, not the 12 it declared");
    }

    /// ⚠️ A RUN THAT ADVANCES BY NOTHING IS NOT A LINE OF ITS OWN. Word lifts
    /// the pen off the baseline to place a below-base Myanmar mark and draws it
    /// in a placement by itself. Measured on a real page: one such glyph sat
    /// 3.24 points above its line, so the baseline test threw it out, and
    /// because it fell BETWEEN the two halves of a line it stopped those from
    /// joining either. One mark cost two lines and split a word down the middle.
    #[test]
    fn a_lifted_mark_belongs_to_the_line_it_interrupts() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        // "ကုန်": the second glyph is a below-base vowel that advances by
        // nothing, which is what lets it be drawn anywhere.
        const WORD: &str = "\u{1000}\u{102F}\u{1014}\u{103A}";
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        let glyphs = crate::reshape::draws(&face, WORD);
        assert!(glyphs.len() >= 3, "expected several glyphs for {WORD:?}");

        let mark = glyphs[1];
        assert_eq!(face.glyph_hor_advance(rustybuzz::ttf_parser::GlyphId(mark)), Some(0),
            "this test needs a mark that advances by nothing");

        // Drawn the way Word draws it: the head of the word, then the mark
        // lifted onto its own line, then the tail, all at the same x the pen
        // had reached.
        let head = &glyphs[..1];
        let tail = &glyphs[2..];
        let upem = face.units_per_em() as f64;
        let after_head: f64 = head
            .iter()
            .map(|g| face.glyph_hor_advance(rustybuzz::ttf_parser::GlyphId(*g)).unwrap_or(0) as f64)
            .sum::<f64>() / upem * 12.0;

        let (doc, page) = a_page_measured_by(&face, vec![
            Operation::new("BT", vec![]), pick(12.0), place(72.0, 700.0),
            show(head), Operation::new("ET", vec![]),
            Operation::new("BT", vec![]), pick(12.0),
            place(72.0 + after_head, 703.24),
            show(&[mark]), Operation::new("ET", vec![]),
            Operation::new("BT", vec![]), pick(12.0),
            place(72.0 + after_head, 700.0),
            show(tail), Operation::new("ET", vec![]),
        ]);

        let read = read_page_with(&doc, page, &an_index_of(&bytes, WORD));
        assert_eq!(read.len(), 1, "the lifted mark was read as a line of its own");
        assert_eq!(read[0].text.as_deref(), Some(WORD),
            "the mark was dropped, so the word lost a letter");
    }

    /// Where each line's word spaces come from: a space GLYPH the page draws,
    /// or a SKIP between two placements that draws nothing. Diagnostic.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn where_the_word_spaces_come_from() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = indexes_for(&doc, page);
        let lines = lines_of(&doc, page);
        let read = read_page_with(&doc, page, &indexes);

        for (n, (line, r)) in lines.iter().zip(read.iter()).enumerate() {
            let Some(text) = r.text.as_deref() else { continue };
            let Some(entry) = indexes.by_font.get(&line.base_font) else { continue };
            let (bytes, index) = entry.as_ref();
            let face = rustybuzz::Face::from_slice(bytes, 0).unwrap();

            // The glyph this font draws for an ordinary space.
            let space = crate::reshape::draws(&face, " ");
            let drew = line.glyphs.iter().filter(|g| space.contains(g)).count();
            let says = text.chars().filter(|c| *c == ' ').count();

            println!(
                "{n:>2} y={:7.1}  spaces in the reading {says:>2}, \
                 space glyphs drawn {drew:>2}, skips {:>2} worth {:6.2}",
                line.y, line.breaks.len(),
                line.breaks.iter().map(|b| b.points).sum::<f64>());
        }
    }

    /// What the Myanmar files in the user's test folder are actually made of,
    /// page by page. Diagnostic: the corpus this work has to cover.
    /// How wide the gaps a producer leaves between its placements really are,
    /// against the width of the space glyph the same font draws.
    ///
    /// The reader turns a gap into a word space when it is at least
    /// `A_SPACE` of the type size. On one file that reads every syllable apart
    /// (`\u{1026}\u{1038} \u{1005}\u{102E}\u{1038}` for text with no spaces in it at all), this says whether those
    /// gaps are genuinely space-sized or whether the threshold is simply too
    /// low for the way that producer sets type.
    /// ⚠️ TWO FONTS NOTHING IS KNOWN ABOUT ARE NOT THE SAME FONT.
    ///
    /// The join used to ask `installed()` of each name and compare the answers.
    /// That is None for anything outside the two Burmese families, so ANY two
    /// unrecognised fonts compared equal and their placements were merged into
    /// one line, mixing glyph ids from two fonts into one run. Measured on a
    /// Devanagari book: a heading's Devanagari and the en dash beside it are set
    /// in different fonts, they were joined, and the run diverged from the page
    /// at the dash. Splitting them took that page from 289 runs to 1641, and
    /// took the runs that reproduce exactly from 5 to 73.
    /// A page whose two font resources are named differently, both of them
    /// names nothing is known about, with one placement in each.
    fn a_page_in_two_unknown_fonts(operations: Vec<Operation>) -> (Document, ObjectId) {
        let mut doc = Document::with_version("1.7");
        let mut font_res = lopdf::Dictionary::new();

        for (res, base) in [("F1", "CIDFont+F2"), ("F2", "CIDFont+F7")] {
            let descendant = doc.add_object(lopdf::dictionary! {
                "Type" => "Font",
                "Subtype" => "CIDFontType2",
                "BaseFont" => base,
                "W" => vec![0.into(), vec![500.0.into()].into()],
                "DW" => 500.0,
            });
            let font = doc.add_object(lopdf::dictionary! {
                "Type" => "Font",
                "Subtype" => "Type0",
                "BaseFont" => base,
                "Encoding" => "Identity-H",
                "DescendantFonts" => vec![descendant.into()],
            });
            font_res.set(res, font);
        }

        let content = Content { operations };
        let stream = doc.add_object(lopdf::Stream::new(
            lopdf::dictionary! {},
            content.encode().unwrap(),
        ));
        let pages_id = doc.new_object_id();
        let page = doc.add_object(lopdf::dictionary! {
            "Type" => "Page",
            "Parent" => pages_id,
            "Contents" => stream,
            "Resources" => lopdf::dictionary! { "Font" => font_res },
        });
        doc.objects.insert(pages_id, Object::Dictionary(lopdf::dictionary! {
            "Type" => "Pages",
            "Kids" => vec![page.into()],
            "Count" => 1,
        }));
        let catalog = doc.add_object(lopdf::dictionary! {
            "Type" => "Catalog",
            "Pages" => pages_id,
        });
        doc.trailer.set("Root", catalog);
        (doc, page)
    }

    /// ⚠️ AND THE JOIN ITSELF MUST HONOUR IT, not merely the helper.
    ///
    /// Two placements on one baseline, set in two different fonts, must come
    /// back as TWO lines. Merged, the run mixes glyph ids from two fonts and
    /// means nothing: measured on a Devanagari book, a heading's Devanagari and
    /// the en dash beside it were joined and the run diverged from the page at
    /// the dash.
    #[test]
    fn two_placements_in_different_unknown_fonts_are_two_lines() {
        let (doc, page) = a_page_in_two_unknown_fonts(vec![
            Operation::new("BT", vec![]),
            Operation::new("Tf", vec!["F1".into(), 12.0.into()]),
            place(72.0, 700.0),
            show(&[10, 11, 12]),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            Operation::new("Tf", vec!["F2".into(), 12.0.into()]),
            place(90.0, 700.0),
            show(&[13, 14]),
            Operation::new("ET", vec![]),
        ]);

        let lines = lines_of(&doc, page);

        assert_eq!(lines.len(), 2,
            "two fonts on one baseline were merged into one run: {:?}",
            lines.iter().map(|l| (&l.base_font, &l.glyphs)).collect::<Vec<_>>());
        assert_eq!(lines[0].glyphs, vec![10, 11, 12]);
        assert_eq!(lines[1].glyphs, vec![13, 14]);
    }

    #[test]
    fn two_fonts_nothing_is_known_about_are_not_one_face() {
        // The bug, said plainly: these are different fonts.
        assert!(!one_face("CIDFont+F2", "CIDFont+F7"),
            "two unrecognised fonts were treated as one, so their runs would merge");

        // One font is still itself.
        assert!(one_face("CIDFont+F2", "CIDFont+F2"));

        // ⚠️ AND THE BURMESE FILES MUST BE UNAFFECTED. Their pages alternate
        // between subsets of one family line by line, and joining those is what
        // makes a line readable at all.
        assert!(one_face("BCDEEE+MyanmarText", "BCDGEE+MyanmarText"),
            "two subsets of one readable family stopped being one face");

        // And a readable family is never the same as an unreadable font.
        assert!(!one_face("BCDEEE+MyanmarText", "CIDFont+F2"));
    }

    /// ⚠️ THE REPAIR, END TO END, GATED BY THE SAME PROOF AS MYANMAR.
    ///
    /// Step 1 paired every run with the line the file says it belongs to. This
    /// puts the pieces back together and asks the only question that settles
    /// anything: substitute the wrong codepoints for what the page's own glyphs
    /// say they should be, shape the line again, and demand the page's glyphs.
    ///
    /// Three things had to be measured before the loop could even run, and each
    /// of them changed it:
    ///
    /// ⚠️ A RUN IS A FRAGMENT, NOT A LINE. This producer draws one page's
    /// text as sixteen hundred placements. Sorted by where they sit across the
    /// page they are the line, and the line is what has to be reproduced.
    ///
    /// ⚠️ A LINE IS SET IN MORE THAN ONE FACE. Measured: 240 of this page's
    /// 246 lines draw out of more than one font resource, and those resources
    /// are not all one face. Two of them number their glyphs the way the
    /// resolved Devanagari face does; five draw the spaces, digits and dashes
    /// out of something else, where the en dash is glyph 177 against the
    /// Devanagari face's 168. Judging those runs would repair a difference that
    /// is not an error, so the walk steps over them.
    ///
    /// ⚠️ AND A CLUSTER IS NOT ALWAYS ONE GLYPH. `र्श` draws as the consonant
    /// plus a reph above it. Indexing only the clusters that draw as a single
    /// glyph made every one of those invisible, so the index is keyed by the
    /// SEQUENCE a cluster draws, and the repair takes the longest sequence that
    /// the page's own glyphs begin with.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn how_much_of_a_page_a_codepoint_repair_proves() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();
        let devanagari_char =
            |c: char| ('\u{0900}'..='\u{097F}').contains(&c);

        // ⚠️ THE SPACE IS NOT A GLYPH ON THIS PAGE. A run of the page's own
        // glyphs crosses a space without drawing anything for it, while the
        // face shapes one, so a run that is really there stops matching in the
        // middle. Measured: this alone refused most of the lines that carried
        // no error at all. Both sides drop it, and the position reported for a
        // repair is still the position in the real shaping.
        let blank: BTreeSet<u16> = crate::reshape::draws(&face, " ")
            .into_iter().collect();
        println!("the face draws a space as {blank:?}");

        // ⚠️ THE REPH IS WRITTEN AFTER ITS BASE, WHICH NO SUBSTITUTION CAN
        // FIX. `दर्शन` reaches the file as `दशŊन`: the `र्` is a mark drawn
        // above `श` and the producer emitted it in the order it is DRAWN, one
        // place after the consonant it belongs to. Unicode has no character for
        // a standalone reph, so the repair is not a substitution at all, it is
        // a move. The glyph is named by shaping a reph and the same consonant
        // without one, and taking what was added.
        let reph = {
            let with = crate::reshape::draws(&face, "र्क");
            let without = crate::reshape::draws(&face, "क");
            with.iter().copied().find(|g| !without.contains(g))
        };
        println!("the face draws a reph as {reph:?}");

        // ⚠️ AND A MATRA IS NOT ONE GLYPH EITHER. The `ि` reaches over the
        // cluster that follows it, so the face carries a width variant of it
        // per cluster and the shaper picks one. Measured: the page draws 302
        // where this shaper picks 228 for the same `ि` in the same word, which
        // is not a wrong codepoint and cannot be repaired as one. Every variant
        // a character can produce, gathered from the face itself.
        let mut variants: BTreeMap<char, BTreeSet<u16>> = BTreeMap::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.contains(&0) {
                continue;
            }
            let mut buffer = rustybuzz::UnicodeBuffer::new();
            buffer.push_str(&text);
            let shaped = rustybuzz::shape(&face, &[], buffer);
            for (info, &id) in shaped.glyph_infos().iter().zip(&g) {
                if let Some(c) = text[info.cluster as usize..].chars().next() {
                    variants.entry(c).or_default().insert(id);
                }
            }
        }
        let alternates = |c: char, a: u16, b: u16| {
            variants.get(&c).is_some_and(|s| s.contains(&a) && s.contains(&b))
        };
        for c in ['ि', 'ा', 'र'] {
            println!("the face draws {c:?} as any of {:?}",
                variants.get(&c).map(|s| s.len()));
        }

        // The glyph SEQUENCE a cluster draws -> the cluster, where exactly one
        // cluster draws it. Anything two clusters can draw is dropped.
        let mut inverse: BTreeMap<Vec<u16>, String> = BTreeMap::new();
        let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.is_empty() || g.contains(&0) || g.len() > 3 {
                continue;
            }
            if let Some(had) = inverse.insert(g.clone(), text.clone()) {
                if had != text {
                    clash.insert(g);
                }
            }
        }
        for g in &clash {
            inverse.remove(g);
        }
        let known: BTreeSet<u16> = inverse.keys().flatten().copied().collect();
        println!("{} glyph sequences have exactly one spelling, over {} glyphs",
            inverse.len(), known.len());

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        // ⚠️ WHICH RESOURCES ARE EVEN THE SAME FACE, asked of their own
        // glyphs. The two groups come out at 61% and 68% against five
        // resources at 0%, so nothing in between has to be judged.
        let mut seen: BTreeMap<String, (BTreeSet<u16>, usize)> = BTreeMap::new();
        for &page in &pages {
            for line in lines_of(&doc, page) {
                let e = seen.entry(line.base_font.clone()).or_default();
                e.1 += line.glyphs.len();
                e.0.extend(line.glyphs.iter().copied());
            }
        }
        println!("\nper font resource, of its distinct glyphs, how many the \
            resolved face draws:");
        let mut devanagari: BTreeSet<String> = BTreeSet::new();
        for (name, (ids, drawn)) in &seen {
            let hit = ids.iter().filter(|g| known.contains(g)).count();
            let share = hit as f64 / ids.len().max(1) as f64;
            if share > 0.25 {
                devanagari.insert(name.clone());
            }
            println!("   {name:<12} {drawn:>5} drawn, {:>4} distinct, {hit:>4} \
                known ({:>3.0}%), highest id {}{}",
                ids.len(), share * 100.0, ids.iter().max().copied().unwrap_or(0),
                if share > 0.25 { "   <- devanagari" } else { "" });
        }

        let mut lines_judged = 0usize;
        let mut already = 0usize;
        let mut proven = 0usize;
        let mut gave_up = 0usize;
        let mut runs_judged = 0usize;
        let mut runs_proven = 0usize;
        let mut swaps_made: BTreeMap<char, BTreeMap<String, usize>> = BTreeMap::new();
        let mut stalls: BTreeMap<&str, usize> = BTreeMap::new();
        let mut shown = 0usize;
        let mut stalled = 0usize;

        for (n, &page) in pages.iter().enumerate() {
            let said = crate::tests::decoded_lines_for(&bytes, n as i32);
            if said.is_empty() {
                continue;
            }
            let Some((left, top, width)) = page_box(&doc, page) else { continue };
            let across = |v: f64| ((v - left) / width) as f32;
            let down = |v: f64| ((top - v) / width) as f32;

            // Every run, gathered under the line it was paired with.
            let mut gathered: BTreeMap<usize, Vec<Line>> = BTreeMap::new();
            for line in lines_of(&doc, page) {
                if line.glyphs.is_empty() {
                    continue;
                }
                let my_base = down(line.page_y);
                let my_x = across(line.x);
                let near = (line.size / width * 0.5).max(0.002) as f32;
                let best = said
                    .iter()
                    .enumerate()
                    .filter(|(_, s)| (s.4 - my_base).abs() < near)
                    .min_by(|(_, a), (_, b)| {
                        (a.0 - my_x).abs().total_cmp(&(b.0 - my_x).abs())
                    });
                if let Some((i, _)) = best {
                    gathered.entry(i).or_default().push(line);
                }
            }

            for (i, mut runs) in gathered {
                runs.sort_by(|a, b| a.x.total_cmp(&b.x));
                runs.retain(|r| devanagari.contains(&r.base_font));
                let drawn: usize = runs.iter().map(|r| r.glyphs.len()).sum();
                if drawn < 4 {
                    continue;
                }
                let text = said[i].5.clone();
                if !text.chars().any(devanagari_char) {
                    continue;
                }
                lines_judged += 1;
                runs_judged += runs.len();

                // ⚠️ EACH DEVANAGARI RUN IS FOUND, NOT COUNTED TO. The runs
                // between them are drawn by another face and cannot be shaped
                // here, so their length is not something to rely on: measured,
                // a line that was otherwise entirely repaired was refused
                // because the page ends with a space its text does not carry.
                // Each run is sought at or after where the last one ended, and
                // the walk fails only when a run is nowhere ahead of it.
                let walk = |now: &str| -> Result<usize, (usize, Vec<u16>, usize)> {
                    // The shaping with the blanks taken out, each glyph still
                    // carrying where it really sits and which character it came
                    // from, because a character's width variants all count.
                    let mut buffer = rustybuzz::UnicodeBuffer::new();
                    buffer.push_str(now);
                    let out = rustybuzz::shape(&face, &[], buffer);
                    let ink: Vec<(u16, usize, char)> = out.glyph_infos().iter()
                        .enumerate()
                        .map(|(i, g)| (g.glyph_id as u16, i,
                            now[g.cluster as usize..].chars().next().unwrap_or(' ')))
                        .filter(|(g, _, _)| !blank.contains(g))
                        .collect();
                    let mut at = 0usize;
                    for (r, run) in runs.iter().enumerate() {
                        let want: Vec<u16> = run.glyphs.iter().copied()
                            .filter(|g| !blank.contains(g)).collect();
                        if want.is_empty() {
                            continue;
                        }
                        let mut best = (0usize, at);
                        for q in at..=ink.len() {
                            let m = want.iter().zip(&ink[q..])
                                .take_while(|(a, (b, _, c))| **a == *b
                                    || alternates(*c, **a, *b))
                                .count();
                            if m > best.0 {
                                best = (m, q);
                            }
                            if m == want.len() {
                                break;
                            }
                        }
                        if best.0 == want.len() {
                            at = best.1 + want.len();
                        } else {
                            let here = ink.get(best.1 + best.0)
                                .map(|(_, i, _)| *i)
                                .unwrap_or(out.len());
                            return Err((here, want[best.0..].to_vec(), r));
                        }
                    }
                    Ok(runs.len())
                };

                if walk(&text).is_ok() {
                    already += 1;
                    runs_proven += runs.len();
                    continue;
                }

                // Repair: one wrong codepoint at a time, reshaping each round.
                let mut now = text.clone();
                let mut swaps: Vec<(char, String)> = Vec::new();
                let mut why = "";
                for _ in 0..40 {
                    let Err((at, tail, _)) = walk(&now) else { break };
                    let Some((offset, bad)) = char_and_offset(&face, &now, at) else {
                        why = "the divergence is past the end of the text";
                        break;
                    };
                    let after = offset + bad.len_utf8();
                    let mine = !devanagari_char(bad) && !bad.is_ascii();

                    // The longest run of the page's own glyphs this face can
                    // spell. Longest, because `र्श` and `श` both begin here.
                    let spelling = (1..=tail.len().min(3)).rev()
                        .find_map(|k| inverse.get(&tail[..k]));

                    if let Some(spelling) = spelling.filter(|_| mine) {
                        // ⚠️ A WRONG CODEPOINT STANDING FOR A WHOLE CLUSTER,
                        // which is the case this whole investigation began from.
                        swaps.push((bad, spelling.clone()));
                        now = format!("{}{spelling}{}", &now[..offset], &now[after..]);
                    } else if reph == Some(tail[0]) && mine {
                        // A reph, written where it is drawn. Take the character
                        // out and put a real `र्` in front of the syllable it
                        // belongs to.
                        let ins = syllable_start(&now[..offset]);
                        swaps.push((bad, "र् moved back".into()));
                        now = format!("{}र्{}{}", &now[..ins], &now[ins..offset],
                            &now[after..]);
                    } else if devanagari_char(bad)
                        && crate::reshape::draws(&face, &bad.to_string())
                            .first() == Some(&tail[0])
                        && now[after..].starts_with('\u{094D}')
                        && !now[after + '\u{094D}'.len_utf8()..]
                            .starts_with('\u{200C}')
                    {
                        // ⚠️ THE PAGE DID NOT FORM THE CONJUNCT THE FACE DOES.
                        // Measured: `अध्याय` is drawn as `ध` and `य` either side
                        // of a visible virama where the face makes one `ध्य`.
                        // The text is not wrong, it is under-specified: what the
                        // page shows is the half form refused, which Unicode
                        // spells with a zero-width non-joiner after the virama.
                        let cut = after + '\u{094D}'.len_utf8();
                        swaps.push((bad, "conjunct broken".into()));
                        now = format!("{}{}{}", &now[..cut], '\u{200C}', &now[cut..]);
                    } else if spelling.is_none() {
                        why = "no spelling for the glyphs the page draws";
                        break;
                    } else {
                        why = "the character that diverged is already devanagari";
                        break;
                    }
                }

                match walk(&now) {
                    Ok(_) => {
                        proven += 1;
                        runs_proven += runs.len();
                        for (bad, spelling) in &swaps {
                            *swaps_made.entry(*bad).or_default()
                                .entry(spelling.clone()).or_default() += 1;
                        }
                        if shown < 6 && !swaps.is_empty() {
                            shown += 1;
                            println!("\n  PROVEN after {} swaps, {drawn} glyphs in {} runs",
                                swaps.len(), runs.len());
                            println!("     file said “{}”",
                                text.chars().take(56).collect::<String>());
                            println!("     really   “{}”",
                                now.chars().take(56).collect::<String>());
                        }
                    }
                    Err((at, tail, r)) => {
                        gave_up += 1;
                        runs_proven += r;
                        *stalls.entry(if why.is_empty() {
                            "still not proven after 40 rounds"
                        } else { why }).or_default() += 1;
                        if stalled < 6 {
                            stalled += 1;
                            let shaped = crate::reshape::draws(&face, &now);
                            println!("\n  STOPPED in run {r} of {}, at glyph {at} \
                                of {} ({} swaps in): {why}",
                                runs.len(), shaped.len(), swaps.len());
                            println!("     page draws  {:?}",
                                &tail[..tail.len().min(5)]);
                            println!("     text shapes {:?}",
                                &shaped[at.min(shaped.len())
                                    ..(at + 5).min(shaped.len())]);
                            println!("     the character there {:?}",
                                char_and_offset(&face, &now, at).map(|(_, c)| c));
                            println!("     glyph {} is {}, one of it spells {:?}, \
                                two {:?}",
                                tail[0],
                                if known.contains(&tail[0]) { "drawn by this face" }
                                else { "NOT drawn by this face" },
                                inverse.get(&tail[..1]),
                                tail.get(..2).and_then(|k| inverse.get(k)));
                            println!("     text “{}”",
                                now.chars().take(60).collect::<String>());
                        }
                    }
                }
            }
        }

        println!("\n{lines_judged} lines carrying devanagari:");
        println!("   {already} already read correctly");
        println!("   {proven} PROVEN after repair");
        println!("   {gave_up} not proven");
        for (why, n) in &stalls {
            println!("      {why} x{n}");
        }
        if lines_judged > 0 {
            println!("   {:.0}% of lines, {:.0}% of runs ({runs_proven} of \
                {runs_judged})",
                (already + proven) as f64 / lines_judged as f64 * 100.0,
                runs_proven as f64 / runs_judged.max(1) as f64 * 100.0);
        }

        println!("\nthe substitutions that proved:");
        for (bad, spellings) in &swaps_made {
            let mut best: Vec<(&String, &usize)> = spellings.iter().collect();
            best.sort_by(|a, b| b.1.cmp(a.1));
            let shown: Vec<String> = best.iter().take(2)
                .map(|(s, n)| format!("{s:?} x{n}")).collect();
            println!("   {bad:?} (U+{:04X}) -> {}", *bad as u32, shown.join(", "));
        }
    }

    /// ⚠️ A WRONG CODEPOINT IS NOT WRONG, IT IS THE GLYPH ID.
    ///
    /// The producer's `/ToUnicode` does not cover the glyphs that come out
    /// wrong: measured, resource F2 declares 62 entries and F7 declares 78,
    /// and NONE of the odd characters is among them. So there is nothing to
    /// invert. What PDFium does with a CID it has no entry for is emit the CID,
    /// and under Identity-H the CID is the glyph id, so the character arrives
    /// with the glyph's own number in it.
    ///
    /// That is worth checking rather than admiring, because if it holds the
    /// whole alignment problem disappears: the text already names the glyph it
    /// should have drawn, character by character, with no line matching, no run
    /// pairing and no geometry anywhere in it.
    ///
    /// Three that were solved the long way and agree: `Ŋ` is U+014A = 330,
    /// which is the reph; `ʼ` is U+02BC = 700, which spells `ष्ट`; `Ů` is
    /// U+016E = 366, which is what the run under `प्रवचन` starts with.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn whether_a_wrong_codepoint_is_simply_the_glyph_id() {
        for file in [GEETA_SMALL, CHAL_HANSA] {
            if !std::path::Path::new(file).exists() {
                println!("\n== {} is not on this machine ==",
                    file.rsplit('\\').next().unwrap());
                continue;
            }
            println!("\n================ {} ================",
                file.rsplit('\\').next().unwrap());
            how_far_the_glyph_id_assumption_holds(file);
        }
    }

    fn how_far_the_glyph_id_assumption_holds(file: &str) {
        if !std::path::Path::new(file).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(file).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        let mut suspects = 0usize;
        let mut is_a_glyph_the_page_draws = 0usize;
        let mut not = 0usize;
        let mut which: BTreeMap<char, usize> = BTreeMap::new();
        let mut strangers: BTreeMap<char, usize> = BTreeMap::new();

        for (n, &page) in pages.iter().enumerate() {
            // Every glyph this page draws, whatever resource drew it.
            let drawn: BTreeSet<u16> = lines_of(&doc, page)
                .iter()
                .flat_map(|l| l.glyphs.clone())
                .collect();

            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                for c in line.5.chars() {
                    let odd = !('\u{0900}'..='\u{097F}').contains(&c)
                        && !c.is_ascii()
                        && !c.is_whitespace()
                        && !"–—‘’“”…•·".contains(c);
                    if !odd {
                        continue;
                    }
                    suspects += 1;
                    if u32::from(c) <= u32::from(u16::MAX)
                        && drawn.contains(&(u32::from(c) as u16))
                    {
                        is_a_glyph_the_page_draws += 1;
                        *which.entry(c).or_default() += 1;
                    } else {
                        not += 1;
                        *strangers.entry(c).or_default() += 1;
                    }
                }
            }
        }

        println!("{suspects} characters that are neither devanagari nor plain text");
        println!("   {is_a_glyph_the_page_draws} are the id of a glyph the page draws");
        println!("   {not} are not");
        println!("   {} distinct, over {} pages", which.len(), pages.len());

        if !strangers.is_empty() {
            println!("\nthe ones that are NOT a glyph the page draws:");
            for (c, n) in strangers.iter().take(20) {
                println!("   {c:?} U+{:04X} = {} x{n}", u32::from(c.to_owned()),
                    u32::from(*c));
            }
        }

        // And what they spell, read straight out of the face with the id the
        // character carries. No page, no line, no geometry.
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        let Ok(font) = std::fs::read(NIRMALA) else { return };
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();
        let mut inverse: BTreeMap<Vec<u16>, String> = BTreeMap::new();
        let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.is_empty() || g.contains(&0) || g.len() > 3 {
                continue;
            }
            if let Some(had) = inverse.insert(g.clone(), text.clone()) {
                if had != text {
                    clash.insert(g);
                }
            }
        }
        for g in &clash {
            inverse.remove(g);
        }
        let reph = {
            let with = crate::reshape::draws(&face, "र्क");
            let without = crate::reshape::draws(&face, "क");
            with.iter().copied().find(|g| !without.contains(g))
        };

        let mut spelled = 0usize;
        let mut rephs = 0usize;
        let mut blank = 0usize;
        println!("\nwhat each of them spells, by its own id:");
        for (c, count) in &which {
            let id = u32::from(*c) as u16;
            let said = inverse.get(&vec![id]);
            if Some(id) == reph {
                rephs += count;
                println!("   {c:?} U+{:04X} = {id:<5} the reph, x{count}", u32::from(*c));
            } else if let Some(said) = said {
                spelled += count;
                println!("   {c:?} U+{:04X} = {id:<5} {said:?} x{count}", u32::from(*c));
            } else {
                blank += count;
                println!("   {c:?} U+{:04X} = {id:<5} NOTHING SPELLS IT x{count}",
                    u32::from(*c));
            }
        }
        println!("\n{spelled} occurrences spell a cluster, {rephs} are the reph, \
            {blank} have no spelling");
    }

    /// The end of the orthographic syllable that `tail` begins with: past the
    /// consonant, and past every consonant a virama joins to it. A matra
    /// belongs after the whole of that, so `कि` and `क्षि` put it in different
    /// places. None when `tail` does not begin with a syllable at all.
    fn syllable_end(tail: &str) -> Option<usize> {
        let consonant = |c: char| ('\u{0915}'..='\u{0939}').contains(&c)
            || ('\u{0958}'..='\u{095F}').contains(&c);
        let chars: Vec<(usize, char)> = tail.char_indices().collect();
        let mut k = 0usize;
        loop {
            let (_, c) = *chars.get(k)?;
            if consonant(c) {
                break;
            }
            // Only another Devanagari mark may stand in front of the base.
            if !('\u{0900}'..='\u{097F}').contains(&c) {
                return None;
            }
            k += 1;
        }
        while k + 2 < chars.len() && chars[k + 1].1 == '\u{094D}'
            && consonant(chars[k + 2].1)
        {
            k += 2;
        }
        Some(chars[k].0 + chars[k].1.len_utf8())
    }

    /// A glyph that is not a letter on its own: the text it carries on each
    /// side of the syllable it hangs off, and which side of that syllable the
    /// face draws it.
    ///
    /// ⚠️ ONE GLYPH CAN CARRY TEXT ON BOTH SIDES. `को` draws as [248, 316]
    /// and `र्को` as [248, 342]: the reph and the `ो` are ONE glyph, and its
    /// text is a `र्` that Unicode writes in front of the syllable and a `ो`
    /// it writes after it. Treating a form as a single string on a single side
    /// cannot express that, and glyphs 333, 334, 336, 337, 339 and 342 are all
    /// of them, 253 characters on one file.
    ///
    /// ⚠️ AND `drawn_before` IS ABOUT THE STREAM, NOT THE TEXT. It says
    /// whether the face puts this glyph in front of the base it belongs to,
    /// which is what decides where the syllable IS when the repair meets the
    /// glyph: to the right of it if the face drew it first, to the left if not.
    #[derive(Clone, PartialEq, Eq, PartialOrd, Ord)]
    struct Form {
        before: String,
        after: String,
        drawn_before: bool,
    }

    /// Every glyph that is a dependent form, learned from the face by putting
    /// each sign on each base and seeing what one glyph the face adds.
    fn dependent_forms(face: &rustybuzz::Face) -> BTreeMap<u16, Form> {
        const VIRAMA: char = '\u{094D}';
        let consonants: Vec<char> = ('\u{0915}'..='\u{0939}')
            .chain('\u{0958}'..='\u{095F}')
            .collect();
        let reph = format!("र{VIRAMA}");

        // ⚠️ MARKS FUSE IN COMBINATION, NOT ONE AT A TIME. `में` draws as `म`
        // and ONE more glyph carrying both the `े` and the `ं`; so does `हैं`,
        // and so does the `र्` with `ों` under it in `वर्षों`. Pairing the reph
        // with a single matra found none of them: measured, glyphs 336 and 339
        // alone were 221 of the 230 characters the repair could not name, and
        // neither is drawn by any single-mark cluster that can be built.
        //
        // A sign is therefore an optional reph, an optional matra and an
        // optional nasal, and what it gives the repair is text in front of the
        // syllable and text after it.
        let matras: Vec<String> = std::iter::once(String::new())
            .chain(('\u{093E}'..='\u{094C}').map(|c| c.to_string()))
            .collect();
        let nasals = ["", "\u{0902}", "\u{0903}", "\u{0901}"];
        let mut signs: Vec<(String, String)> = Vec::new();
        for wear_reph in [false, true] {
            for m in &matras {
                for n in nasals {
                    if !wear_reph && m.is_empty() && n.is_empty() {
                        continue;
                    }
                    signs.push((
                        if wear_reph { reph.clone() } else { String::new() },
                        format!("{m}{n}"),
                    ));
                }
            }
        }
        signs.push((String::new(), format!("{VIRAMA}र")));

        // ⚠️ AND THE BASES NEED ONLY BE WHAT A MARK SITS ON. Every cluster
        // that is itself a consonant carrying a sign is now reachable as a base
        // plus one of the signs above, so enumerating those as bases as well
        // multiplies the work by eighteen and learns nothing new.
        let bases: Vec<String> = consonants.iter().map(|c| c.to_string())
            .chain(consonants.iter().flat_map(|c| {
                consonants.iter().map(move |d| format!("{c}{VIRAMA}{d}"))
            }))
            .collect();
        let mut found: BTreeMap<u16, BTreeMap<Form, usize>> = BTreeMap::new();
        for base in &bases {
            let plain = crate::reshape::draws(face, base);
            if plain.is_empty() || plain.contains(&0) {
                continue;
            }
            for (before, after) in &signs {
                let with = crate::reshape::draws(
                    face, &format!("{before}{base}{after}"));
                // Exactly one glyph more than the base, with the base's own
                // glyphs either side of it. Anything else means the sign
                // changed the base as well, and there is nothing to learn.
                if with.len() != plain.len() + 1 || with.contains(&0) {
                    continue;
                }
                let head = with.iter().zip(&plain)
                    .take_while(|(a, b)| a == b).count();
                let tail = with.iter().rev().zip(plain.iter().rev())
                    .take_while(|(a, b)| a == b).count();
                if head + tail < plain.len() {
                    continue;
                }
                *found.entry(with[head]).or_default()
                    .entry(Form {
                        before: before.clone(),
                        after: after.clone(),
                        drawn_before: head == 0,
                    })
                    .or_default() += 1;
            }
        }

        // ⚠️ A HALF FORM IS A GLYPH TOO, and it is not a dependent form at
        // all: it is the consonant itself, drawn as the half it becomes when a
        // virama joins it to what follows. It carries nothing on either side.
        for &c in &consonants {
            let mut first: Option<u16> = None;
            let mut steady = true;
            let mut seen = 0usize;
            for &d in &consonants {
                let g = crate::reshape::draws(face, &format!("{c}{VIRAMA}{d}"));
                if g.len() < 2 || g.contains(&0) {
                    continue;
                }
                seen += 1;
                match first {
                    None => first = Some(g[0]),
                    Some(f) if f != g[0] => {
                        steady = false;
                        break;
                    }
                    _ => {}
                }
            }
            if let Some(f) = first.filter(|_| steady && seen >= 8) {
                *found.entry(f).or_default()
                    .entry(Form {
                        before: String::new(),
                        after: format!("{c}{VIRAMA}"),
                        drawn_before: false,
                    })
                    .or_default() += seen;
            }
        }

        // ⚠️ WHAT IT MEANS IS WHAT MOST OF ITS BASES SAY IT MEANS, not the
        // only thing any base ever said. Refusing every glyph two bases
        // disagree about lost the reph itself: `र्र` puts glyph 330 under the
        // rakar as well, one base against thirty-four, and the whole reph
        // repair went with it. A clear majority decides, and a tie refuses.
        // ⚠️ WHAT A GLYPH MEANS IS THE TEXT, NOT THE SIDE IT SAT ON. The
        // same `ि` comes out drawn-before over some bases and drawn-after over
        // others, and counting those as two different answers split the vote:
        // the `ि` for `र` scored ten against five and was thrown out as a
        // disagreement, 29 characters on one file, when both were saying the
        // same thing. The text decides, and the side is a majority within it.
        //
        // ⚠️ AND UNANIMOUS IS ENOUGH HOWEVER FEW SAID IT. A matra has a width
        // variant per base it sits on, so the widest are produced by one or two
        // bases and no others.
        found.into_iter()
            .filter_map(|(g, what)| {
                let mut by_text: BTreeMap<(String, String), (usize, usize)> =
                    BTreeMap::new();
                for (form, n) in what {
                    let e = by_text.entry((form.before, form.after)).or_default();
                    e.0 += n;
                    if form.drawn_before {
                        e.1 += n;
                    }
                }
                let mut ranked: Vec<((String, String), (usize, usize))> =
                    by_text.into_iter().collect();
                ranked.sort_by(|a, b| b.1.0.cmp(&a.1.0));
                let ((before, after), (n, before_side)) = ranked.first()?.clone();
                let runner_up = ranked.get(1).map_or(0, |(_, (m, _))| *m);
                (runner_up == 0 || n > runner_up * 2).then_some((g, Form {
                    before,
                    after,
                    drawn_before: before_side * 2 > n,
                }))
            })
            .collect()
    }

    /// Puts back the pre-base matras the producer wrote where they are DRAWN.
    ///
    /// ⚠️ NOT EVERY MISORDERED CHARACTER ARRIVED AS A GLYPH ID. Measured on
    /// this file: `है कि` reaches the text as `हैिक`, a real U+093F sitting in
    /// front of the consonant it belongs to, because the producer emitted its
    /// characters in the order it drew them and this one is drawn first. No
    /// glyph id is involved and nothing about it is wrong except the order.
    ///
    /// ⚠️ AND IT IS DECIDABLE, NOT A GUESS. A pre-base matra is only ever
    /// written after a consonant. One that is not preceded by a consonant
    /// cannot be where it belongs, wherever it came from, so this moves exactly
    /// those and leaves every correctly written one alone, including the ones
    /// the glyph-id repair has just placed.
    fn reorder_prebase(text: &str, prebase: &BTreeSet<char>) -> String {
        let consonant = |c: char| ('\u{0915}'..='\u{0939}').contains(&c)
            || ('\u{0958}'..='\u{095F}').contains(&c) || c == '\u{093C}';
        let mut out = String::with_capacity(text.len());
        let mut rest = text;
        loop {
            let Some((offset, matra)) = rest.char_indices()
                .find(|(_, c)| prebase.contains(c))
            else {
                break;
            };
            let after = offset + matra.len_utf8();
            match syllable_end(&rest[after..]) {
                Some(end) => {
                    out.push_str(&rest[..offset]);
                    out.push_str(&rest[after..after + end]);
                    out.push(matra);
                    rest = &rest[after + end..];
                }
                None => {
                    out.push_str(&rest[..after]);
                    rest = &rest[after..];
                }
            }
        }
        out.push_str(rest);
        out
    }

    /// Rewrites a line the producer emitted as glyph ids back into Devanagari.
    ///
    /// Every character that is neither Devanagari nor plain text carries the id
    /// of the glyph that should have been drawn. `spells` says what a glyph
    /// spells outright; `forms` says which glyphs are dependent forms and which
    /// side of their syllable they belong on.
    fn repair_devanagari(
        text: &str,
        spells: &BTreeMap<Vec<u16>, String>,
        forms: &BTreeMap<u16, Form>,
    ) -> String {
        let mut out = String::with_capacity(text.len());
        let mut rest = text;
        while let Some((offset, bad)) = rest.char_indices().find(|(_, c)| {
            !('\u{0900}'..='\u{097F}').contains(c) && !c.is_ascii()
                && !c.is_whitespace() && !"–—‘’“”…•·".contains(*c)
        }) {
            let id = u32::from(bad);
            let id = if id <= u32::from(u16::MAX) { id as u16 } else { 0 };
            let after = offset + bad.len_utf8();

            if let Some(said) = spells.get(&vec![id]) {
                // A whole cluster, in place.
                out.push_str(&rest[..offset]);
                out.push_str(said);
                rest = &rest[after..];
            } else if let Some(form) = forms.get(&id) {
                if form.drawn_before {
                    // The face drew this in FRONT of its base, so the syllable
                    // it belongs to is still to the right and has not been
                    // repaired yet.
                    //
                    // ⚠️ WHICH IS WHY THE `after` TEXT IS ONLY NAMED HERE, NOT
                    // MOVED. Measured: moving it now needs the syllable to the
                    // right, and a `ि` in front of a conjunct still spelled as
                    // a glyph id found no syllable at all and was left as a raw
                    // id, 77 times on one file. `reorder_prebase` moves it once
                    // the whole line reads.
                    out.push_str(&rest[..offset]);
                    out.push_str(&form.before);
                    out.push_str(&form.after);
                    rest = &rest[after..];
                } else {
                    // The face drew it after its base, so the syllable is to
                    // the left and this pass has already repaired it.
                    let head = &rest[..offset];
                    let ins = syllable_start(head);
                    out.push_str(&head[..ins]);
                    out.push_str(&form.before);
                    out.push_str(&head[ins..]);
                    out.push_str(&form.after);
                    rest = &rest[after..];
                }
            } else {
                out.push_str(&rest[..after]);
                rest = &rest[after..];
            }
        }
        out.push_str(rest);
        out
    }

    /// ⚠️ STEP 3: REPAIR THE TEXT ALONE, THEN PROVE IT AGAINST THE PAGE.
    ///
    /// Step 2 repaired a line by walking it against the glyphs the page draws,
    /// which needed the runs paired to the line, sorted across the page and
    /// filtered to the face. That is a lot of geometry to be right about, and
    /// it is what limited the rate: a line stalls on the first thing it cannot
    /// explain, so one matra variant refuses a line whose every codepoint is
    /// known.
    ///
    /// ⚠️ NONE OF THAT IS NEEDED. Measured: every one of the 1268 odd
    /// characters on this file is the id of a glyph the page draws, with no
    /// exceptions, because PDFium emits the CID for a glyph `/ToUnicode` does
    /// not cover and Identity-H makes the CID the glyph id. The text already
    /// names the glyph it should have drawn. So the repair reads the text and
    /// nothing else, and the page is kept for what it is good for: saying
    /// whether the answer is right.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_a_text_only_repair_reads_off_a_devanagari_page() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();

        let wide = devanagari_clusters_wide();
        println!("{} clusters enumerated", wide.len());
        let mut spells: BTreeMap<Vec<u16>, String> = BTreeMap::new();
        let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
        for text in &wide {
            let g = crate::reshape::draws(&face, text);
            if g.is_empty() || g.contains(&0) || g.len() > 3 {
                continue;
            }
            if let Some(had) = spells.insert(g.clone(), text.clone()) {
                if had != *text {
                    clash.insert(g);
                }
            }
        }
        for g in &clash {
            spells.remove(g);
        }
        let forms = dependent_forms(&face);
        let prebase: BTreeSet<char> = forms.values()
            .filter(|f| f.drawn_before)
            .filter_map(|f| {
                let mut c = f.after.chars();
                c.next().filter(|_| c.next().is_none())
            })
            .collect();
        println!("{} glyph sequences spell a cluster, {} glyphs are a \
            dependent form, {prebase:?} are drawn before their base",
            spells.len(), forms.len());
        for (g, form) in forms.iter().take(6) {
            println!("   {g:<5} {:?} .. {:?}, drawn {} its base", form.before,
                form.after, if form.drawn_before { "before" } else { "after" });
        }

        let blank: BTreeSet<u16> = crate::reshape::draws(&face, " ")
            .into_iter().collect();
        let mut variants: BTreeMap<char, BTreeSet<u16>> = BTreeMap::new();
        for text in devanagari_clusters_wide() {
            let mut buffer = rustybuzz::UnicodeBuffer::new();
            buffer.push_str(&text);
            let out = rustybuzz::shape(&face, &[], buffer);
            for info in out.glyph_infos() {
                if info.glyph_id == 0 {
                    continue;
                }
                if let Some(c) = text[info.cluster as usize..].chars().next() {
                    variants.entry(c).or_default().insert(info.glyph_id as u16);
                }
            }
        }
        let alternates = |c: char, a: u16, b: u16| {
            variants.get(&c).is_some_and(|s| s.contains(&a) && s.contains(&b))
        };

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        // Which resources are the face, asked of their own glyphs.
        let known: BTreeSet<u16> = spells.keys().flatten().copied()
            .chain(forms.keys().copied()).collect();
        let mut devanagari: BTreeSet<String> = BTreeSet::new();
        {
            let mut seen: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
            for &page in &pages {
                for line in lines_of(&doc, page) {
                    seen.entry(line.base_font.clone()).or_default()
                        .extend(line.glyphs.iter().copied());
                }
            }
            for (name, ids) in seen {
                let hit = ids.iter().filter(|g| known.contains(g)).count();
                if hit as f64 / ids.len().max(1) as f64 > 0.25 {
                    devanagari.insert(name);
                }
            }
        }

        let mut lines = 0usize;
        let mut untouched = 0usize;
        let mut changed = 0usize;
        let mut before_proves = 0usize;
        let mut after_proves = 0usize;
        let mut broke = 0usize;
        let mut shown = 0usize;
        let mut refused = 0usize;
        let mut still_carries_an_id = 0usize;
        let mut looks_finished_but_refused = 0usize;
        let mut leftovers: BTreeMap<char, usize> = BTreeMap::new();
        let mut told = 0usize;
        let mut why_refused: BTreeMap<&str, usize> = BTreeMap::new();

        for (n, &page) in pages.iter().enumerate() {
            let said = crate::tests::decoded_lines_for(&bytes, n as i32);
            if said.is_empty() {
                continue;
            }
            let Some((left, top, width)) = page_box(&doc, page) else { continue };
            let across = |v: f64| ((v - left) / width) as f32;
            let down = |v: f64| ((top - v) / width) as f32;

            let mut gathered: BTreeMap<usize, Vec<Line>> = BTreeMap::new();
            for line in lines_of(&doc, page) {
                if line.glyphs.is_empty() {
                    continue;
                }
                let my_base = down(line.page_y);
                let my_x = across(line.x);
                let near = (line.size / width * 0.5).max(0.002) as f32;
                let best = said.iter().enumerate()
                    .filter(|(_, s)| (s.4 - my_base).abs() < near)
                    .min_by(|(_, a), (_, b)| {
                        (a.0 - my_x).abs().total_cmp(&(b.0 - my_x).abs())
                    });
                if let Some((i, _)) = best {
                    gathered.entry(i).or_default().push(line);
                }
            }

            for (i, mut runs) in gathered {
                runs.sort_by(|a, b| a.x.total_cmp(&b.x));
                runs.retain(|r| devanagari.contains(&r.base_font));
                let drawn: usize = runs.iter().map(|r| r.glyphs.len()).sum();
                let text = said[i].5.clone();
                if drawn < 4 || !text.chars().any(|c| ('\u{0900}'..='\u{097F}')
                    .contains(&c))
                {
                    continue;
                }
                lines += 1;

                // The proof, unchanged from step 2: every run of the page's own
                // glyphs found in order in the shaping of the text.
                let proves = |now: &str|
                    -> Result<(), (usize, Vec<u16>, Vec<u16>, char)> {
                    let mut buffer = rustybuzz::UnicodeBuffer::new();
                    buffer.push_str(now);
                    let out = rustybuzz::shape(&face, &[], buffer);
                    let ink: Vec<(u16, char)> = out.glyph_infos().iter()
                        .map(|g| (g.glyph_id as u16,
                            now[g.cluster as usize..].chars().next().unwrap_or(' ')))
                        .filter(|(g, _)| !blank.contains(g))
                        .collect();
                    let mut at = 0usize;
                    for run in &runs {
                        let want: Vec<u16> = run.glyphs.iter().copied()
                            .filter(|g| !blank.contains(g)).collect();
                        if want.is_empty() {
                            continue;
                        }
                        let mut found = None;
                        for q in at..=ink.len().saturating_sub(want.len()) {
                            if want.iter().zip(&ink[q..]).all(|(a, (b, c))|
                                a == b || alternates(*c, *a, *b))
                            {
                                found = Some(q);
                                break;
                            }
                        }
                        match found {
                            Some(q) => at = q + want.len(),
                            None => return Err((
                                at,
                                want,
                                ink[at.min(ink.len())..(at + 6).min(ink.len())]
                                    .iter().map(|(g, _)| *g).collect(),
                                ink.get(at).map_or(' ', |(_, c)| *c),
                            )),
                        }
                    }
                    Ok(())
                };

                let was = proves(&text).is_ok();
                if was {
                    before_proves += 1;
                }
                // ⚠️ THE PRODUCTION REPAIR, NOT A COPY OF IT. This diagnostic
                // is the only thing that measures the reading against a real
                // page, so it has to be measuring what ships. The page's whole
                // set of lines goes in together because the drawn-order
                // question is asked of the page, not of one line.
                let mut whole: Vec<String> = said.iter().map(|s| s.5.clone()).collect();
                crate::devanagari::repair_page(&mut whole);
                let now = whole[i].clone();
                if now == text {
                    untouched += 1;
                } else {
                    changed += 1;
                }
                let outcome = proves(&now);
                if outcome.is_ok() {
                    after_proves += 1;
                    if !was && shown < 8 {
                        shown += 1;
                        println!("\n  {}", text.chars().take(58).collect::<String>());
                        println!("  {}", now.chars().take(58).collect::<String>());
                    }
                } else {
                    refused += 1;
                    let left: Vec<char> = now.chars().filter(|c|
                        !('\u{0900}'..='\u{097F}').contains(c) && !c.is_ascii()
                            && !c.is_whitespace() && !"–—‘’“”…•·".contains(*c))
                        .collect();
                    if left.is_empty() {
                        looks_finished_but_refused += 1;
                        // ⚠️ THE PROOF IS STRICTER THAN THE GOAL. A line can
                        // carry exactly the right text and still not reproduce
                        // the page, because the page declined a ligature the
                        // face forms: `अध्याय` is drawn as `ध`, a visible
                        // virama and `य` where the face makes one `ध्य`, and
                        // the text is identical either way. Counting those
                        // separately is the difference between "not proven"
                        // and "read wrongly".
                        let (_, want, _, c) = outcome.clone().unwrap_err();
                        let alone = crate::reshape::draws(&face, &c.to_string());
                        *why_refused.entry(
                            if alone.first() == want.first() {
                                "the page declined a conjunct the face forms"
                            } else if want.first().zip(alone.first())
                                .is_some_and(|(a, b)| alternates(c, *a, *b))
                            {
                                "the face and the page chose different variants"
                            } else {
                                "something else"
                            }).or_default() += 1;
                        if told < 6 {
                            told += 1;
                            let (at, want, got, _) = outcome.clone().unwrap_err();
                            println!("\n  REFUSED though nothing is left to repair, \
                                at glyph {at}:");
                            println!("     page draws  {:?}",
                                &want[..want.len().min(6)]);
                            println!("     text shapes {got:?}");
                            println!("     {}", now.chars().take(58)
                                .collect::<String>());
                        }
                    } else {
                        still_carries_an_id += 1;
                        for c in left {
                            *leftovers.entry(c).or_default() += 1;
                        }
                    }
                    if was {
                        broke += 1;
                        println!("\n  ⚠️ THE REPAIR BROKE A LINE THAT ALREADY READ");
                        println!("     was “{}”", text.chars().take(58)
                            .collect::<String>());
                        println!("     now “{}”", now.chars().take(58)
                            .collect::<String>());
                    }
                }
            }
        }

        println!("\n{lines} lines carrying devanagari");
        println!("   {changed} rewritten, {untouched} left alone");
        // ⚠️ "BEFORE" IS NO LONGER BEFORE. The reader itself repairs now, so
        // the text this diagnostic is handed has already been through it and
        // both counts read the same. The unrepaired numbers are in the commits
        // that measured them; what this proves today is the shipped path.
        println!("   {before_proves} reproduced the page as the reader hands it over");
        println!("   {after_proves} reproduce it after ({:.0}%)",
            after_proves as f64 / lines.max(1) as f64 * 100.0);
        println!("   {refused} still refused, {broke} of them broken BY the repair");
        println!("      {still_carries_an_id} still carry a glyph id nothing spells");
        println!("      {looks_finished_but_refused} carry none and are refused anyway");
        for (why, n) in &why_refused {
            println!("         {why} x{n}");
        }
        println!("\n   the ids left over, by how much they cost:");
        let mut worst: Vec<(&char, &usize)> = leftovers.iter().collect();
        worst.sort_by(|a, b| b.1.cmp(a.1));
        // ⚠️ AND WHAT ARE THEY? Asked of the face: every cluster whose
        // shaping contains the glyph, and where in it. A glyph that only ever
        // turns up in the middle of a cluster is a dependent form of something,
        // and the something is what the cluster has that the others do not.
        let mut inside: BTreeMap<u16, Vec<(String, usize, usize)>> = BTreeMap::new();
        for text in &wide {
            let g = crate::reshape::draws(&face, text);
            for (i, &id) in g.iter().enumerate() {
                let e = inside.entry(id).or_default();
                if e.len() < 4 {
                    e.push((text.clone(), i, g.len()));
                }
            }
        }
        for (c, n) in worst.iter().take(8) {
            let id = u32::from(**c) as u16;
            println!("      {c:?} = glyph {id} x{n}, forms says {:?}, spells says {:?}",
                forms.get(&id).map(|f| (&f.before, &f.after, f.drawn_before)),
                spells.get(&vec![id]));
            match inside.get(&id) {
                Some(seen) => for (text, i, len) in seen {
                    println!("           {i} of {len} in {text:?}");
                },
                None => println!("           the face never draws it"),
            }
        }
    }

    /// ⚠️ WHICH FACE IS THE PAGE'S FACE, ASKED OF THE CHARACTERS IT FAILED.
    ///
    /// The repair names a character by looking its id up in a face. Nirmala
    /// names all but two of them, and those two are 221 of the 230 it cannot
    /// do: glyphs 336 and 339, which Nirmala does not draw from ANY Devanagari
    /// cluster that can be built, checked over every consonant, conjunct and
    /// pairing of signs. A glyph the page draws 156 times and the face never
    /// draws is not a gap in the enumeration, it is the wrong face.
    ///
    /// So the face is chosen the same way everything else here is decided: by
    /// which one accounts for what the page actually draws.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn which_installed_face_names_the_most_of_a_devanagari_page() {
        for file in [GEETA_SMALL, CHAL_HANSA] {
            if !std::path::Path::new(file).exists() {
                println!("\n== {} is not on this machine ==",
                    file.rsplit('\\').next().unwrap());
                continue;
            }
            println!("\n================ {} ================",
                file.rsplit('\\').next().unwrap());
            which_face_names_the_most(file);
        }
    }

    fn which_face_names_the_most(file: &str) {
        const CANDIDATES: [(&str, &str); 7] = [
            ("Nirmala UI", r"C:\Windows\Fonts\NIRMALA.TTF"),
            ("Nirmala UI Bold", r"C:\Windows\Fonts\NIRMALAB.TTF"),
            ("Mangal", r"C:\Windows\Fonts\mangal.ttf"),
            ("Mangal Bold", r"C:\Windows\Fonts\mangalb.ttf"),
            ("Aparajita", r"C:\Windows\Fonts\APARAJ.TTF"),
            ("Kokila", r"C:\Windows\Fonts\KOKILA.TTF"),
            ("Utsaah", r"C:\Windows\Fonts\UTSAAH.TTF"),
        ];
        if !std::path::Path::new(file).exists() {
            println!("not on this machine");
            return;
        }

        // Every character the file emitted as a glyph id, and how often.
        let bytes = std::fs::read(file).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let mut wanted: BTreeMap<u16, usize> = BTreeMap::new();
        for n in 0..pages.len() {
            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                for c in line.5.chars() {
                    if !('\u{0900}'..='\u{097F}').contains(&c) && !c.is_ascii()
                        && !c.is_whitespace() && !"–—‘’“”…•·".contains(c)
                        && u32::from(c) <= u32::from(u16::MAX)
                    {
                        *wanted.entry(u32::from(c) as u16).or_default() += 1;
                    }
                }
            }
        }
        let total: usize = wanted.values().sum();
        println!("{} distinct ids, {total} occurrences\n", wanted.len());

        for (name, path) in CANDIDATES {
            let Ok(font) = std::fs::read(path) else {
                println!("   {name:<18} not installed");
                continue;
            };
            let Some(face) = rustybuzz::Face::from_slice(&font, 0) else { continue };

            let mut spells: BTreeMap<Vec<u16>, String> = BTreeMap::new();
            let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
            for text in devanagari_clusters_wide() {
                let g = crate::reshape::draws(&face, &text);
                if g.is_empty() || g.contains(&0) || g.len() > 3 {
                    continue;
                }
                if let Some(had) = spells.insert(g.clone(), text.clone()) {
                    if had != text {
                        clash.insert(g);
                    }
                }
            }
            for g in &clash {
                spells.remove(g);
            }
            let forms = dependent_forms(&face);

            let named: usize = wanted.iter()
                .filter(|(g, _)| spells.contains_key(&vec![**g])
                    || forms.contains_key(g))
                .map(|(_, n)| n)
                .sum();
            let distinct = wanted.keys()
                .filter(|g| spells.contains_key(&vec![**g]) || forms.contains_key(g))
                .count();
            println!("   {name:<18} names {distinct:>3} of {} ids, \
                {named:>5} of {total} occurrences ({:>3.0}%)",
                wanted.len(), named as f64 / total.max(1) as f64 * 100.0);
        }
    }

    /// ⚠️ WHAT KIND OF FILE IS THIS, BEFORE ASKING WHAT IS WRONG WITH IT.
    ///
    /// The Geeta repair rests on one measured fact: PDFium emits the CID for a
    /// glyph `/ToUnicode` does not cover, and Identity-H makes the CID the
    /// glyph id. Both halves of that are properties of the FILE, not of the
    /// script, so a second Devanagari book can fail either half and still be
    /// perfectly ordinary Devanagari. This asks each file which it is.
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn what_shape_each_devanagari_file_is() {
        for file in [GEETA_SMALL, CHAL_HANSA] {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            println!("\n================ {} ================",
                file.rsplit('\\').next().unwrap());
            let bytes = std::fs::read(file).unwrap();
            let Ok(doc) = Document::load_mem(&bytes) else { continue };
            let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

            // How the text comes out.
            let mut devanagari = 0usize;
            let mut ascii = 0usize;
            let mut odd = 0usize;
            let mut lines = 0usize;
            let look = pages.len().min(8);
            for n in 0..look {
                for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                    lines += 1;
                    for c in line.5.chars() {
                        if ('\u{0900}'..='\u{097F}').contains(&c) {
                            devanagari += 1;
                        } else if c.is_ascii() || c.is_whitespace() {
                            ascii += 1;
                        } else {
                            odd += 1;
                        }
                    }
                }
            }
            println!("{} pages, first {look} carry {lines} lines: \
                {devanagari} devanagari characters, {ascii} plain, {odd} odd",
                pages.len());

            // What the content stream gives up.
            let mut runs = 0usize;
            let mut glyphs = 0usize;
            for &page in pages.iter().take(look) {
                for line in lines_of(&doc, page) {
                    runs += 1;
                    glyphs += line.glyphs.len();
                }
            }
            println!("the stream reader finds {runs} runs, {glyphs} glyphs");

            // And what the fonts are.
            for (n, &page) in pages.iter().take(2).enumerate() {
                let Some(fonts) = doc.get_dictionary(page).ok()
                    .and_then(|p| p.get(b"Resources").ok()
                        .and_then(|o| dictionary(&doc, o)))
                    .and_then(|r| r.get(b"Font").ok()
                        .and_then(|o| dictionary(&doc, o)))
                else {
                    continue;
                };
                for (name, obj) in fonts.iter() {
                    let Some(f) = dictionary(&doc, obj) else { continue };
                    let say = |k: &[u8]| f.get(k).ok()
                        .map(|v| format!("{v:?}"))
                        .unwrap_or_else(|| "-".into());
                    println!("   page {n} {:<4} {:<16} {:<14} encoding {} \
                        tounicode {}",
                        String::from_utf8_lossy(name),
                        say(b"Subtype").trim_matches('"').to_string(),
                        say(b"BaseFont").trim_matches('"').to_string(),
                        say(b"Encoding"),
                        if f.has(b"ToUnicode") { "yes" } else { "NO" });
                }
            }

            // A few lines, as they come out.
            println!("   ---- as extracted ----");
            for (i, line) in crate::tests::decoded_lines_for(&bytes, 1)
                .iter().enumerate().take(4)
            {
                let _ = i;
                println!("   {}", line.5.chars().take(64).collect::<String>());
            }
        }
    }

    /// ⚠️ AND IS MANGAL REALLY NAMING THEM, OR JUST HITTING SOMETHING?
    ///
    /// The face test says Mangal names 21 of Chal Hansa's 22 odd characters.
    /// It cannot be taken at face value: that test asks whether the character's
    /// code point, READ AS A GLYPH ID, is in the face's tables, and 22 small
    /// numbers will hit a table of ninety thousand by luck alone. The file's
    /// fonts are simple TrueType with `/WinAnsiEncoding`, where a byte is a
    /// character code and not a glyph id at all, so the mechanism the Geeta
    /// repair rests on is not even present.
    ///
    /// The only test that settles it is whether the answers read as Hindi.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_the_second_producers_odd_characters_are() {
        const MANGAL: &str = r"C:\Windows\Fonts\mangal.ttf";
        if !std::path::Path::new(CHAL_HANSA).exists()
            || !std::path::Path::new(MANGAL).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(MANGAL).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();
        let wide = devanagari_clusters_wide();
        let mut spells: BTreeMap<Vec<u16>, String> = BTreeMap::new();
        let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
        for text in &wide {
            let g = crate::reshape::draws(&face, text);
            if g.is_empty() || g.contains(&0) || g.len() > 3 {
                continue;
            }
            if let Some(had) = spells.insert(g.clone(), text.clone()) {
                if had != *text {
                    clash.insert(g);
                }
            }
        }
        for g in &clash {
            spells.remove(g);
        }
        let forms = dependent_forms(&face);

        let bytes = std::fs::read(CHAL_HANSA).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages = doc.get_pages().len();

        let mut seen: BTreeMap<char, (usize, Vec<String>)> = BTreeMap::new();
        for n in 0..pages.min(40) {
            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                for (i, c) in line.5.char_indices() {
                    if ('\u{0900}'..='\u{097F}').contains(&c) || c.is_ascii()
                        || c.is_whitespace() || "–—‘’“”…•·".contains(c)
                    {
                        continue;
                    }
                    let e = seen.entry(c).or_default();
                    e.0 += 1;
                    if e.1.len() < 2 {
                        let from = line.5[..i].char_indices().rev().nth(8)
                            .map_or(0, |(j, _)| j);
                        let to = line.5[i..].char_indices().nth(9)
                            .map_or(line.5.len(), |(j, _)| i + j);
                        e.1.push(line.5[from..to].to_string());
                    }
                }
            }
        }

        println!("{} distinct odd characters over the first 40 pages", seen.len());
        for (c, (n, contexts)) in &seen {
            let id = u32::from(*c);
            let said = (id <= u32::from(u16::MAX)).then(|| id as u16).and_then(|g| {
                spells.get(&vec![g]).cloned()
                    .or_else(|| forms.get(&g).map(|f| format!("{}..{}",
                        f.before, f.after)))
            });
            println!("\n   {c:?} U+{id:04X} x{n}  mangal-as-id says {said:?}");
            for ctx in contexts {
                println!("        ...{ctx}...");
            }
        }
    }

    /// ⚠️ IS THE INFORMATION EVEN THERE? That is the question that decides
    /// whether a file is repairable at all, and it is not the same question as
    /// whether the text looks wrong.
    ///
    /// The Geeta is repairable because nothing was lost: every character it got
    /// wrong still carries the id of the glyph that should have been drawn. A
    /// file that DROPPED characters cannot be repaired from its text however
    /// clever the tables are, because there is nothing left to repair.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn whether_the_second_producer_lost_anything() {
        if !std::path::Path::new(CHAL_HANSA).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(CHAL_HANSA).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages = doc.get_pages().len();

        let mut shown = 0usize;
        for n in 4..pages.min(30) {
            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                let has_c1 = line.5.chars().any(|c| ('\u{0080}'..='\u{009F}')
                    .contains(&c));
                if !has_c1 || line.5.chars().count() < 20 || shown >= 4 {
                    continue;
                }
                shown += 1;
                println!("\n  page {n}: {}", line.5);
                print!("    ");
                for c in line.5.chars().take(46) {
                    if ('\u{0900}'..='\u{097F}').contains(&c) {
                        print!("{c}");
                    } else {
                        print!("[{:04X}]", u32::from(c));
                    }
                }
                println!();
            }
        }

        // And the counts, over the whole book: what is Devanagari, what is
        // Latin standing where Devanagari belongs, what is a control byte.
        let mut deva = 0usize;
        let mut latin = 0usize;
        let mut control = 0usize;
        let mut digit = 0usize;
        let mut space = 0usize;
        let mut other = 0usize;
        for n in 0..pages.min(40) {
            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                for c in line.5.chars() {
                    if ('\u{0900}'..='\u{097F}').contains(&c) {
                        deva += 1;
                    } else if ('\u{0080}'..='\u{009F}').contains(&c) {
                        control += 1;
                    } else if c.is_ascii_alphabetic() {
                        latin += 1;
                    } else if c.is_ascii_digit() {
                        digit += 1;
                    } else if c.is_whitespace() {
                        space += 1;
                    } else {
                        other += 1;
                    }
                }
            }
        }
        let all = deva + latin + control + digit + space + other;
        println!("\n  over 40 pages, {all} characters:");
        for (what, n) in [("devanagari", deva), ("latin letters", latin),
            ("control bytes", control), ("digits", digit), ("spaces", space),
            ("punctuation and the rest", other)]
        {
            println!("     {what:<26} {n:>6} ({:>4.1}%)",
                n as f64 / all.max(1) as f64 * 100.0);
        }
    }

    /// ⚠️ CAN THE SECOND PRODUCER'S BYTES BE TURNED INTO GLYPH IDS?
    ///
    /// This is the one question that decides whether the Geeta approach extends
    /// or was a trick. Nothing is lost in either file: `राष्ट्रपति` reaches
    /// Chal Hansa's text as `रा`, U+0080, `प`, U+0011, `त`, so U+0080 stands
    /// for `ष्ट्र` and U+0011 for `ि` and both are still there to be named.
    ///
    /// What differs is the bridge from the wrong character to a glyph. The
    /// Geeta needs none: its fonts are Identity-H, so the CID PDFium falls back
    /// to IS the glyph id. Chal Hansa's are simple TrueType with
    /// `/WinAnsiEncoding`, where the byte is a character code and the font's
    /// own cmap is what turns it into a glyph. If that cmap survived
    /// subsetting, the same tables finish the job. If it did not, this file
    /// needs something the Geeta never did.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn whether_the_second_producers_bytes_reach_a_glyph() {
        const MANGAL: &str = r"C:\Windows\Fonts\mangal.ttf";
        if !std::path::Path::new(CHAL_HANSA).exists()
            || !std::path::Path::new(MANGAL).exists()
        {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(CHAL_HANSA).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        // The characters that need a bridge, and how often.
        let mut wanted: BTreeMap<char, usize> = BTreeMap::new();
        for n in 0..pages.len().min(40) {
            for line in crate::tests::decoded_lines_for(&bytes, n as i32) {
                for c in line.5.chars() {
                    if !('\u{0900}'..='\u{097F}').contains(&c)
                        && !c.is_whitespace()
                        && !c.is_ascii_digit()
                        && !"–—‘’“”…•·।॥.,;:!?()[]{}\"'/\\-_=+*&%$#@~`|<>".contains(c)
                    {
                        *wanted.entry(c).or_default() += 1;
                    }
                }
            }
        }
        let total: usize = wanted.values().sum();
        println!("{} distinct characters need a bridge, {total} occurrences",
            wanted.len());

        // Mangal's own tables, to say what a glyph id means.
        let font = std::fs::read(MANGAL).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();
        let mut spells: BTreeMap<Vec<u16>, String> = BTreeMap::new();
        let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
        for text in devanagari_clusters_wide() {
            let g = crate::reshape::draws(&face, &text);
            if g.is_empty() || g.contains(&0) || g.len() > 3 {
                continue;
            }
            if let Some(had) = spells.insert(g.clone(), text.clone()) {
                if had != text {
                    clash.insert(g);
                }
            }
        }
        for g in &clash {
            spells.remove(g);
        }
        let forms = dependent_forms(&face);
        let name = |g: u16| spells.get(&vec![g]).cloned()
            .or_else(|| forms.get(&g).map(|f| format!("{}..{}", f.before, f.after)));

        // The embedded programs, and what their cmaps do with those bytes.
        for &page in pages.iter().skip(9).take(1) {
            for (res, program) in embedded_programs(&doc, page) {
                let Some(sub) = rustybuzz::Face::from_slice(&program, 0) else {
                    println!("\n   {} : {} bytes, NOT PARSEABLE",
                        String::from_utf8_lossy(&res), program.len());
                    continue;
                };
                println!("\n   {} : {} bytes, {} glyphs in the subset",
                    String::from_utf8_lossy(&res), program.len(),
                    sub.number_of_glyphs());

                let mut reached = 0usize;
                let mut named = 0usize;
                let mut shown = 0usize;
                for (c, n) in &wanted {
                    // What a viewer does with a byte in a symbolic font: the
                    // font's own cmap, tried plain and at the F000 offset.
                    // The font's OWN cmap, the Macintosh subtable a viewer
                    // uses for a symbolic font, not the Unicode one that
                    // rustybuzz picks by default. That default is why the
                    // first attempt reached nothing at all.
                    let id = sub.tables().cmap.and_then(|cm| cm.subtables
                        .into_iter()
                        .find(|s| s.platform_id
                            == rustybuzz::ttf_parser::PlatformId::Macintosh)
                        .and_then(|s| s.glyph_index(u32::from(*c))));
                    let Some(id) = id else { continue };
                    reached += n;
                    let said = name(id.0);
                    if said.is_some() {
                        named += n;
                    }
                    if shown < 12 {
                        shown += 1;
                        println!("      {c:?} U+{:04X} x{n} -> glyph {} -> {:?}",
                            u32::from(*c), id.0, said);
                    }
                }
                println!("      {reached} of {total} occurrences reach a glyph, \
                    {named} of those are named");
            }
        }
    }

    const HINDI: [&str; 4] = [
        r"D:\Ayaan PDF Test file\Geeta Darshan Complete 18 Chapters.pdf",
        r"D:\Ayaan PDF Test file\003_Agyat_Ki_Aur.pdf",
        r"D:\Ayaan PDF Test file\024_Bharat_Ki_Khoj.pdf",
        r"D:\Ayaan PDF Test file\Chal Hansa Us Des.pdf",
    ];

    /// Every name a page's fonts go by, so it can be settled whether the
    /// descriptor's name is a safe thing to prefer.
    ///
    /// A Devanagari book was measured naming its fonts `CIDFont+F2`, which says
    /// nothing at all: the table that maps a family to an installed file has
    /// nothing to match on. The descriptor beside it says `GAGHKL+NirmalaUI`.
    /// Before preferring one over the other everywhere, the Burmese files have
    /// to say whether that would change anything they already do.
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn what_a_pages_fonts_are_called_by_each_name_they_have() {
        let files: Vec<&str> = HINDI
            .iter()
            .copied()
            .chain([
                r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf",
                r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf",
            ])
            .collect();

        for file in files {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            let Ok(doc) = Document::load(file) else { continue };
            let Some((_, &page)) = doc.get_pages().iter().next() else { continue };
            println!("\n=== {} ===", file.rsplit('\\').next().unwrap());
            println!("{:<10} {:<24} {:<24} {}",
                "resource", "/BaseFont", "descriptor /FontName", "the program says");

            let Some(fonts) = doc
                .get_dictionary(page)
                .ok()
                .and_then(|p| p.get(b"Resources").ok().and_then(|o| dictionary(&doc, o)))
                .and_then(|r| r.get(b"Font").ok().and_then(|o| dictionary(&doc, o)))
            else {
                println!("no fonts");
                continue;
            };

            for (name, obj) in fonts.iter() {
                let Some(font) = dictionary(&doc, obj) else { continue };
                let base = font
                    .get(b"BaseFont")
                    .ok()
                    .and_then(|o| o.as_name().ok())
                    .map(|n| String::from_utf8_lossy(n).into_owned())
                    .unwrap_or_else(|| "-".into());
                let descriptor = descriptor_name(&doc, &font).unwrap_or_else(|| "-".into());
                let program = program_family(&doc, &font).unwrap_or_else(|| "-".into());
                println!("{:<10} {:<24} {:<24} {}",
                    String::from_utf8_lossy(name), base, descriptor, program);
            }
        }
    }

    /// The family the embedded font PROGRAM calls itself.
    ///
    /// ⚠️ THE ONLY PLACE THE TRUTH IS, ON SOME FILES. A Devanagari book was
    /// measured naming its fonts `CIDFont+F2` in the BaseFont AND in the
    /// descriptor beside it. The subset it embeds knows perfectly well what it
    /// is; nothing had ever asked it.
    fn program_family(doc: &Document, font: &lopdf::Dictionary) -> Option<String> {
        let holder = match font.get(b"DescendantFonts").ok() {
            Some(o) => {
                let arr = match o {
                    Object::Array(a) => a.clone(),
                    Object::Reference(id) => doc.get_object(*id).ok()?.as_array().ok()?.clone(),
                    _ => return None,
                };
                dictionary(doc, arr.first()?)?
            }
            None => font.clone(),
        };
        let d = holder.get(b"FontDescriptor").ok().and_then(|o| dictionary(doc, o))?;
        let id = [&b"FontFile2"[..], b"FontFile3", b"FontFile"]
            .iter()
            .find_map(|k| d.get(k).ok().and_then(|v| v.as_reference().ok()))?;
        let bytes = doc.get_object(id).ok()?.as_stream().ok()?.decompressed_content().ok()?;
        let face = rustybuzz::ttf_parser::Face::parse(&bytes, 0).ok()?;
        face.names()
            .into_iter()
            .find(|n| n.name_id == 1)
            .and_then(|n| n.to_string())
    }

    /// The name the font DESCRIPTOR gives, following a Type0 down to the
    /// descendant that carries it.
    fn descriptor_name(doc: &Document, font: &lopdf::Dictionary) -> Option<String> {
        let holder = match font.get(b"DescendantFonts").ok() {
            Some(o) => {
                let arr = match o {
                    Object::Array(a) => a.clone(),
                    Object::Reference(id) => doc.get_object(*id).ok()?.as_array().ok()?.clone(),
                    _ => return None,
                };
                dictionary(doc, arr.first()?)?
            }
            None => font.clone(),
        };
        let d = holder.get(b"FontDescriptor").ok().and_then(|o| dictionary(doc, o))?;
        d.get(b"FontName")
            .ok()
            .and_then(|o| o.as_name().ok())
            .map(|n| String::from_utf8_lossy(n).into_owned())
    }

    /// ⚠️ CAN THE FILE'S OWN TEXT BE PROVEN? Devanagari pages, unlike the
    /// Burmese ones, DO carry a reading: measured, 41 of 42 text objects on one
    /// page came back as Devanagari and not one character as U+0000. What is
    /// unknown is whether that reading is RIGHT, and the same test that proves
    /// a recovered line answers it: shape the text the file gives and demand
    /// the glyphs the page draws.
    ///
    /// This needs no index and no enumeration. It is `draws` and a comparison.
    /// A page, anywhere in these books, whose Devanagari is set in a face THIS
    /// MACHINE HAS. Without one there is nothing to shape against and no
    /// measurement to take.
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn where_a_devanagari_page_uses_a_face_we_have() {
        const LOOK: usize = 60;
        for file in HINDI {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            let Ok(doc) = Document::load(file) else { continue };
            let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
            println!("\n=== {} ({} pages, looking at the first {LOOK}) ===",
                file.rsplit('\\').next().unwrap(), pages.len());

            let mut seen: BTreeMap<String, (usize, bool)> = BTreeMap::new();
            for (n, &page) in pages.iter().take(LOOK).enumerate() {
                for name in fonts_named(&doc, page).into_values() {
                    let have = devanagari_file(&name)
                        .is_some_and(|f| std::path::Path::new(f).exists());
                    seen.entry(name).or_insert((n, have));
                }
            }
            for (name, (first, have)) in &seen {
                println!("   {name:<40} first on page {first:<4} installed here: {have}");
            }
        }
    }

    /// ⚠️ DOES THE REPAIR ACTUALLY PROVE? The lookup answers "what should
    /// have been here". This asks the only question that matters after that:
    /// substitute, shape the whole line again, and demand the page's glyphs.
    ///
    /// Nothing is accepted on the strength of the lookup alone. A line either
    /// comes back as the glyphs the page draws or it is not repaired.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn how_many_lines_a_codepoint_repair_can_prove() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();

        // glyph -> the one cluster that draws it.
        let mut inverse: BTreeMap<u16, String> = BTreeMap::new();
        let mut clash: BTreeSet<u16> = BTreeSet::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.len() != 1 || g[0] == 0 {
                continue;
            }
            if let Some(had) = inverse.insert(g[0], text.clone()) {
                if had != text {
                    clash.insert(g[0]);
                }
            }
        }
        for g in &clash {
            inverse.remove(g);
        }
        println!("{} glyphs have exactly one spelling ({} clashed and were dropped)",
            inverse.len(), clash.len());

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        // ⚠️ LEARNED PER FONT RESOURCE, NOT PER DOCUMENT. Pooling them gave
        // one wrong codepoint three different answers: two fonts of a document
        // number their glyphs differently, so the same stand-in character means
        // different things in each.
        let mut learned: BTreeMap<(usize, Vec<u8>), BTreeMap<char, BTreeMap<String, usize>>> =
            BTreeMap::new();

        let mut total = 0usize;
        let mut clean = 0usize;
        let mut repaired = 0usize;
        let mut partial = 0usize;
        let mut examples = 0usize;

        for (n, &page) in pages.iter().enumerate() {
            let said = page_text(&bytes, n);
            let said: Vec<&String> = said.iter().filter(|s| !s.trim().is_empty()).collect();
            if said.is_empty() {
                continue;
            }

            for line in lines_of(&doc, page) {
                if line.glyphs.len() < 4 {
                    continue;
                }
                total += 1;

                let Some(start) = said.iter().max_by_key(|s| {
                    crate::reshape::draws(&face, s)
                        .iter()
                        .zip(&line.glyphs)
                        .take_while(|(a, b)| a == b)
                        .count()
                }) else { continue };

                if crate::reshape::draws(&face, start) == line.glyphs {
                    clean += 1;
                    continue;
                }

                // Repair, one wrong codepoint at a time, re-shaping each round.
                let mut text = (*start).clone();
                let mut swaps: Vec<(char, String)> = Vec::new();
                for _ in 0..24 {
                    let drawn = crate::reshape::draws(&face, &text);
                    if drawn == line.glyphs {
                        break;
                    }
                    let at = drawn
                        .iter()
                        .zip(&line.glyphs)
                        .take_while(|(a, b)| a == b)
                        .count();
                    if drawn.get(at) != Some(&0) {
                        break;
                    }
                    let Some(&wanted) = line.glyphs.get(at) else { break };
                    let Some(spelling) = inverse.get(&wanted) else { break };
                    let Some((offset, bad)) = char_and_offset(&face, &text, at) else { break };

                    swaps.push((bad, spelling.clone()));
                    let mut next = String::with_capacity(text.len() + spelling.len());
                    next.push_str(&text[..offset]);
                    next.push_str(spelling);
                    next.push_str(&text[offset + bad.len_utf8()..]);
                    text = next;
                }

                let done = crate::reshape::draws(&face, &text) == line.glyphs;
                if done {
                    repaired += 1;
                    let entry = learned
                        .entry((n, line.resource.clone()))
                        .or_default();
                    for (bad, spelling) in &swaps {
                        *entry.entry(*bad).or_default().entry(spelling.clone()).or_default() += 1;
                    }
                    if examples < 4 && !swaps.is_empty() {
                        examples += 1;
                        println!("\n  repaired a line of {} glyphs with {} swaps",
                            line.glyphs.len(), swaps.len());
                        println!("     was “{}”", start.chars().take(46).collect::<String>());
                        println!("     now “{}”", text.chars().take(46).collect::<String>());
                    }
                } else if !swaps.is_empty() {
                    partial += 1;
                }
            }
        }

        println!("\n{total} lines: {clean} already exact, {repaired} repaired and PROVEN, \
            {partial} changed but still unproven, {} untouched",
            total - clean - repaired - partial);

        println!("\nthe table each font resource learned:");
        for ((page, resource), table) in &learned {
            let mut ambiguous = 0usize;
            for spellings in table.values() {
                if spellings.len() > 1 {
                    ambiguous += 1;
                }
            }
            println!("   page {page} {:<6} {} characters, {ambiguous} of them ambiguous",
                String::from_utf8_lossy(resource), table.len());
        }
    }

    /// Where the orthographic syllable that ends `head` begins: back to its
    /// last consonant, and on back over every consonant a virama joins to it.
    /// A reph belongs in front of the whole of that, so `कर्ष` and `र्क्ष` put
    /// it in different places.
    fn syllable_start(head: &str) -> usize {
        let consonant = |c: char| ('\u{0915}'..='\u{0939}').contains(&c)
            || ('\u{0958}'..='\u{095F}').contains(&c);
        let chars: Vec<(usize, char)> = head.char_indices().collect();
        let Some(mut k) = chars.iter().rposition(|(_, c)| consonant(*c)) else {
            return head.len();
        };
        while k >= 2 && chars[k - 1].1 == '\u{094D}' && consonant(chars[k - 2].1) {
            k -= 2;
        }
        chars[k].0
    }

    /// The character of `text` that produced the glyph at `at`, and where it
    /// starts.
    fn char_and_offset(face: &rustybuzz::Face, text: &str, at: usize) -> Option<(usize, char)> {
        let mut buffer = rustybuzz::UnicodeBuffer::new();
        buffer.push_str(text);
        let shaped = rustybuzz::shape(face, &[], buffer);
        let cluster = shaped.glyph_infos().get(at)?.cluster as usize;
        text[cluster..].chars().next().map(|c| (cluster, c))
    }

    /// ⚠️ THE REPAIR TABLE COMES OUT OF THE FILE, NOT OUT OF MATCHING LINES.
    ///
    /// The producer's `/ToUnicode` already says which character it thinks each
    /// glyph means. That is exactly the wrong answer we are trying to correct,
    /// and it is a TABLE: glyph -> the character it wrongly claims. Inverting a
    /// bounded cluster enumeration gives glyph -> the text that really draws it.
    /// Composing the two gives, per font, the substitution to make, with no
    /// line matching anywhere in it.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_the_repair_table_looks_like_composed_from_the_file() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();

        let mut inverse: BTreeMap<u16, String> = BTreeMap::new();
        let mut clash: BTreeSet<u16> = BTreeSet::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.len() != 1 || g[0] == 0 {
                continue;
            }
            if let Some(had) = inverse.insert(g[0], text.clone()) {
                if had != text {
                    clash.insert(g[0]);
                }
            }
        }
        for g in &clash {
            inverse.remove(g);
        }

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let Some(&page) = pages.first() else { return };

        let Some(fonts) = doc
            .get_dictionary(page)
            .ok()
            .and_then(|p| p.get(b"Resources").ok().and_then(|o| dictionary(&doc, o)))
            .and_then(|r| r.get(b"Font").ok().and_then(|o| dictionary(&doc, o)))
        else {
            println!("no fonts");
            return;
        };

        for (name, obj) in fonts.iter() {
            let Some(font_dict) = dictionary(&doc, obj) else { continue };
            let Some(map) = to_unicode(&doc, &font_dict) else { continue };

            let mut suspects = 0usize;
            let mut solved = 0usize;
            let mut unsolved: Vec<u16> = Vec::new();
            let mut shown = 0usize;

            println!("\n--- resource {} : /ToUnicode has {} entries ---",
                String::from_utf8_lossy(name), map.len());

            for (&cid, claimed) in &map {
                // A claim is suspect when it is not Devanagari and not plain
                // ASCII: that is what a mis-mapped conjunct looks like.
                let odd = claimed.chars().any(|c| {
                    !('\u{0900}'..='\u{097F}').contains(&c) && !c.is_ascii() && !c.is_whitespace()
                });
                if !odd {
                    continue;
                }
                suspects += 1;
                match inverse.get(&cid) {
                    Some(real) => {
                        solved += 1;
                        if shown < 12 {
                            shown += 1;
                            println!("   glyph {cid:<5} claims {claimed:?}  really {real:?}");
                        }
                    }
                    None => unsolved.push(cid),
                }
            }
            println!("   {solved} of {suspects} wrong claims resolved to a real cluster");
            if !unsolved.is_empty() {
                println!("   unresolved glyphs: {:?}",
                    &unsolved[..unsolved.len().min(12)]);
            }
        }
    }

    /// The `/ToUnicode` CMap of a font, as glyph code -> the text it claims.
    ///
    /// Only what this measurement needs: `beginbfchar` and `beginbfrange` with
    /// hex operands, which is what every producer writes.
    fn to_unicode(doc: &Document, font: &lopdf::Dictionary) -> Option<BTreeMap<u16, String>> {
        let id = font.get(b"ToUnicode").ok()?.as_reference().ok()?;
        let raw = doc.get_object(id).ok()?.as_stream().ok()?.decompressed_content().ok()?;
        let text = String::from_utf8_lossy(&raw).into_owned();

        // Every <hex> token of a section, in order. Producers put many entries
        // on a line, so pairing has to be done on the tokens themselves.
        fn tokens(section: &str) -> Vec<Vec<u16>> {
            let mut out = Vec::new();
            let mut rest = section;
            while let Some(a) = rest.find('<') {
                let after = &rest[a + 1..];
                let Some(b) = after.find('>') else { break };
                let body = &after[..b];
                rest = &after[b + 1..];
                if body.is_empty() || body.len() % 4 != 0 {
                    out.push(Vec::new());
                    continue;
                }
                let parsed: Option<Vec<u16>> = (0..body.len() / 4)
                    .map(|i| u16::from_str_radix(&body[i * 4..i * 4 + 4], 16).ok())
                    .collect();
                out.push(parsed.unwrap_or_default());
            }
            out
        }

        let say = |v: &[u16]| String::from_utf16_lossy(v);
        let mut out: BTreeMap<u16, String> = BTreeMap::new();

        let mut rest = text.as_str();
        while let Some(s) = rest.find("beginbfchar") {
            let after = &rest[s + "beginbfchar".len()..];
            let e = after.find("endbfchar").unwrap_or(after.len());
            let items = tokens(&after[..e]);
            for pair in items.chunks(2) {
                if let [from, to] = pair {
                    if from.len() == 1 && !to.is_empty() {
                        out.insert(from[0], say(to));
                    }
                }
            }
            rest = &after[e.min(after.len())..];
        }

        let mut rest = text.as_str();
        while let Some(s) = rest.find("beginbfrange") {
            let after = &rest[s + "beginbfrange".len()..];
            let e = after.find("endbfrange").unwrap_or(after.len());
            let section = &after[..e];
            // Array form is skipped: it maps one code to several strings and
            // this measurement does not need it.
            if !section.contains('[') {
                let items = tokens(section);
                for three in items.chunks(3) {
                    if let [a, b, c] = three {
                        if a.len() == 1 && b.len() == 1 && !c.is_empty() && a[0] <= b[0] {
                            for (step, code) in (a[0]..=b[0]).enumerate() {
                                let mut to = c.clone();
                                let last = to.len() - 1;
                                to[last] = to[last].saturating_add(step as u16);
                                out.insert(code, say(&to));
                            }
                        }
                    }
                }
            }
            rest = &after[e.min(after.len())..];
        }
        Some(out)
    }

    /// ⚠️ THE WRONG CODEPOINTS ARE NOT IN THE FILE. THE EXTRACTOR INVENTS THEM.
    ///
    /// Measured: these fonts' `/ToUnicode` maps carry 3 to 78 entries while the
    /// fonts draw hundreds of glyphs, and none of the wrong claims are in them.
    /// So `Ŋ` and `Ƿ` do not come from the producer's table at all. They come
    /// from the EMBEDDED SUBSET'S OWN `cmap`, which a subsetter rebuilds by
    /// handing each retained glyph whatever code was free.
    ///
    /// That cmap is readable, and it makes the repair table derivable with no
    /// line matching and no `/ToUnicode`:
    ///
    ///   subset cmap:  wrong codepoint -> glyph
    ///   inverse index: glyph          -> the cluster that really draws it
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn the_repair_table_read_out_of_the_subsets_own_cmap() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();

        let mut inverse: BTreeMap<u16, String> = BTreeMap::new();
        let mut clash: BTreeSet<u16> = BTreeSet::new();
        for text in devanagari_clusters() {
            let g = crate::reshape::draws(&face, &text);
            if g.len() != 1 || g[0] == 0 {
                continue;
            }
            if let Some(had) = inverse.insert(g[0], text.clone()) {
                if had != text {
                    clash.insert(g[0]);
                }
            }
        }
        for g in &clash {
            inverse.remove(g);
        }
        println!("{} glyphs of Nirmala have exactly one spelling", inverse.len());

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let Some(&page) = pages.first() else { return };

        for (resource, program) in embedded_programs(&doc, page) {
            let Ok(subset) = rustybuzz::ttf_parser::Face::parse(&program, 0) else { continue };

            // Which codes the subset's cmap answers to, over the range a
            // producer's stand-ins land in, plus plain Latin.
            let mut table: Vec<(char, u16, Option<String>)> = Vec::new();
            for code in 0x0041u32..0x0500 {
                let Some(c) = char::from_u32(code) else { continue };
                if ('\u{0900}'..='\u{097F}').contains(&c) {
                    continue;
                }
                let Some(gid) = subset.glyph_index(c) else { continue };
                if gid.0 == 0 {
                    continue;
                }
                table.push((c, gid.0, inverse.get(&gid.0).cloned()));
            }
            if table.is_empty() {
                continue;
            }
            let solved = table.iter().filter(|(_, _, r)| r.is_some()).count();
            println!("\n--- {} : {} non-Devanagari codes in its cmap, {solved} \
                resolve to a Devanagari cluster ---",
                String::from_utf8_lossy(&resource), table.len());

            let mut shown = 0usize;
            for (c, gid, real) in &table {
                if let Some(real) = real {
                    if shown < 14 {
                        shown += 1;
                        println!("   {c:?} (U+{:04X}) -> glyph {gid} -> {real:?}",
                            *c as u32);
                    }
                }
            }
        }
    }

    /// ⚠️ PAIRING A CONTENT-STREAM LINE WITH THE TEXT PDFIUM READ FOR IT.
    ///
    /// The repair needs to know what the file SAYS a given line says. Matching
    /// by string similarity was measured useless: a 94-glyph line agreed with
    /// its "closest" text for 2 glyphs, because the two sides segment a page
    /// differently and the closest string was simply another line.
    ///
    /// Geometry settles it. Both sides know where a line sits, so pair them by
    /// baseline and by where they start across the page, and never by what they
    /// say.
    ///
    /// ⚠️ AND THE LATIN LINES ARE THE GROUND TRUTH. A Latin line has no
    /// mis-mapped conjuncts, so a correct pairing must reproduce its glyphs
    /// EXACTLY. That number measures the pairing itself, with nothing about
    /// Devanagari in it.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn pairing_a_line_with_what_the_file_says_it_says() {
        const FACES: [(&str, &str); 6] = [
            ("nirmala", r"C:\Windows\Fonts\NIRMALA.TTF"),
            ("arial", r"C:\Windows\Fonts\arial.ttf"),
            ("arialbd", r"C:\Windows\Fonts\arialbd.ttf"),
            ("times", r"C:\Windows\Fonts\times.ttf"),
            ("timesbd", r"C:\Windows\Fonts\timesbd.ttf"),
            ("calibri", r"C:\Windows\Fonts\calibri.ttf"),
        ];
        if !std::path::Path::new(GEETA_SMALL).exists() {
            println!("not on this machine");
            return;
        }
        let loaded: Vec<(&str, Vec<u8>)> = FACES
            .iter()
            .filter_map(|(n, path)| std::fs::read(path).ok().map(|b| (*n, b)))
            .collect();

        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

        let mut paired = 0usize;
        let mut unpaired = 0usize;
        let mut latin_seen = 0usize;
        let mut latin_exact = 0usize;
        let mut indic_seen = 0usize;
        let mut indic_prefix: Vec<(usize, usize)> = Vec::new();
        let mut shown = 0usize;

        for (n, &page) in pages.iter().enumerate() {
            let said = crate::tests::decoded_lines_for(&bytes, n as i32);
            if said.is_empty() {
                continue;
            }
            let Some((left, top, width)) = page_box(&doc, page) else { continue };
            let lines = lines_of(&doc, page);

            // ⚠️ NORMALIZED THE WAY THE OTHER SIDE ALREADY IS: top-left
            // origin, both axes over the page WIDTH. See `recover_page_text`.
            let across = |v: f64| ((v - left) / width) as f32;
            let down = |v: f64| ((top - v) / width) as f32;

            for line in &lines {
                if line.glyphs.len() < 4 {
                    continue;
                }
                let my_base = down(line.page_y);
                let my_x = across(line.x);

                // The nearest baseline, and among those the nearest start.
                // A line's own height is the scale: half of it is generous
                // enough for a superscript and far short of the next line.
                let near = (line.size / width * 0.5).max(0.002) as f32;
                let best = said
                    .iter()
                    .filter(|s| (s.4 - my_base).abs() < near)
                    .min_by(|a, b| {
                        (a.0 - my_x).abs().total_cmp(&(b.0 - my_x).abs())
                    });

                let Some(said_line) = best else {
                    unpaired += 1;
                    continue;
                };
                paired += 1;

                // ⚠️ A RUN IS A PIECE OF A LINE, NOT A LINE. This producer
                // places nearly every cluster separately: measured, one page's
                // forty lines of text are drawn as sixteen hundred runs. So the
                // question is not whether the line's text SHAPES INTO this run,
                // it is whether this run APPEARS IN the shaping of the line.
                // That needs no substring alignment and it survives a line that
                // changes font partway, which these headings do.
                let mut top_face: Option<(&str, usize, bool)> = None;
                for (name, face_bytes) in &loaded {
                    let Some(face) = rustybuzz::Face::from_slice(face_bytes, 0) else {
                        continue;
                    };
                    let g = crate::reshape::draws(&face, &said_line.5);
                    let found = g.len() >= line.glyphs.len()
                        && g.windows(line.glyphs.len()).any(|w| w == line.glyphs);
                    let agree = if found {
                        line.glyphs.len()
                    } else {
                        g.windows(line.glyphs.len().min(g.len().max(1)))
                            .map(|w| {
                                w.iter()
                                    .zip(&line.glyphs)
                                    .take_while(|(a, b)| a == b)
                                    .count()
                            })
                            .max()
                            .unwrap_or(0)
                    };
                    if top_face.is_none_or(|(_, a, _)| agree > a) {
                        top_face = Some((name, agree, found));
                    }
                }
                let Some((face_name, agree, exact)) = top_face else { continue };

                let indic = said_line.5.chars().any(|c| ('\u{0900}'..='\u{097F}').contains(&c));
                if indic {
                    indic_seen += 1;
                    indic_prefix.push((agree, line.glyphs.len()));
                    if shown < 4 && !exact && agree > 0 {
                        shown += 1;
                        println!("  page {n}: run of {} glyphs, {agree} accounted for \
                            ({face_name})", line.glyphs.len());
                        println!("     “{}”", said_line.5.chars().take(52).collect::<String>());
                    }
                } else {
                    latin_seen += 1;
                    if exact {
                        latin_exact += 1;
                    }
                }
            }
        }

        println!("\npairing: {paired} lines paired by geometry, {unpaired} with no \
            line on their baseline");
        println!("LATIN GROUND TRUTH: {latin_exact} of {latin_seen} paired Latin lines \
            reproduce their glyphs EXACTLY");

        let total: usize = indic_prefix.iter().map(|(a, _)| a).sum();
        let glyphs: usize = indic_prefix.iter().map(|(_, l)| l).sum();
        let full = indic_prefix.iter().filter(|(a, l)| a == l).count();
        println!("DEVANAGARI: {indic_seen} runs, {full} found whole in the line's \
            own text ({:.0}%), {total} of {glyphs} glyphs accounted for ({:.0}%)",
            if indic_seen == 0 { 0.0 } else { full as f64 / indic_seen as f64 * 100.0 },
            if glyphs == 0 { 0.0 } else { total as f64 / glyphs as f64 * 100.0 });
    }

    /// A second Devanagari book, from a producer that is not the Geeta's.
    const CHAL_HANSA: &str = r"D:\Ayaan PDF Test file\Chal Hansa Us Des.pdf";

    const GEETA_SMALL: &str =
        r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";

    /// ⚠️ CAN A WRONG CODEPOINT BE SOLVED RATHER THAN GUESSED?
    ///
    /// Measured: a Geeta line shapes glyph for glyph with the page until it
    /// reaches a character the producer's `/ToUnicode` got wrong, where it
    /// shapes `.notdef` and the page draws a real glyph. The question this
    /// answers is whether that real glyph names its own text: whether a bounded
    /// enumeration of Devanagari CLUSTERS, shaped through the resolved face,
    /// contains exactly one sequence that draws it.
    ///
    /// ⚠️ BOUNDED, AND NOTHING LIKE THE BURMESE ENUMERATION. This is not the
    /// language: it is consonant, consonant plus virama, virama plus consonant,
    /// and consonant plus virama plus consonant, over the Devanagari block.
    /// A few thousand candidates, built in milliseconds, against Burmese's
    /// millions and eighteen seconds. Nothing here is believed without being
    /// shaped again.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn whether_a_wrong_codepoint_can_be_looked_up_from_the_glyph_it_should_draw() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(GEETA_SMALL).exists()
            || !std::path::Path::new(NIRMALA).exists()
        {
            println!("not on this machine");
            return;
        }
        let font = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font, 0).unwrap();

        // The inverse: every glyph run a bounded set of clusters draws, and
        // what drew it.
        let mut inverse: BTreeMap<Vec<u16>, Vec<String>> = BTreeMap::new();
        let mut built = 0usize;
        for text in devanagari_clusters() {
            built += 1;
            let g = crate::reshape::draws(&face, &text);
            if g.is_empty() || g.contains(&0) {
                continue;
            }
            inverse.entry(g).or_default().push(text);
        }
        println!("{built} clusters shaped, {} distinct glyph runs", inverse.len());
        let single: usize = inverse.keys().filter(|k| k.len() == 1).count();
        println!("{single} of them draw as ONE glyph, which is what a wrong \
            codepoint stands in for");
        let ambiguous = inverse
            .iter()
            .filter(|(k, v)| k.len() == 1 && v.len() > 1)
            .count();
        println!("{ambiguous} of those one-glyph runs have more than one spelling");

        // Now the document: every non-Devanagari, non-ASCII character in its
        // text is a suspect, and the page says which glyph belongs there.
        let bytes = std::fs::read(GEETA_SMALL).unwrap();
        let Ok(doc) = Document::load_mem(&bytes) else { return };
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        println!("\n{} pages", pages.len());

        let mut suspects: BTreeMap<char, usize> = BTreeMap::new();
        let mut solved: BTreeMap<char, BTreeMap<String, usize>> = BTreeMap::new();
        let mut lines_seen = 0usize;
        let mut lines_clean = 0usize;

        for (n, &page) in pages.iter().enumerate().take(4) {
            let said = page_text(&bytes, n);
            for s in &said {
                for c in s.chars() {
                    let devanagari = ('\u{0900}'..='\u{097F}').contains(&c);
                    if !devanagari && !c.is_ascii() && !c.is_whitespace() {
                        *suspects.entry(c).or_default() += 1;
                    }
                }
            }

            for line in lines_of(&doc, page) {
                if line.glyphs.len() < 4 {
                    continue;
                }
                lines_seen += 1;

                // The text that best explains this line.
                let Some(text) = said.iter().max_by_key(|s| {
                    crate::reshape::draws(&face, s)
                        .iter()
                        .zip(&line.glyphs)
                        .take_while(|(a, b)| a == b)
                        .count()
                }) else { continue };

                let drawn = crate::reshape::draws(&face, text);
                let agree = drawn
                    .iter()
                    .zip(&line.glyphs)
                    .take_while(|(a, b)| a == b)
                    .count();
                if agree == line.glyphs.len() && drawn.len() == line.glyphs.len() {
                    lines_clean += 1;
                    continue;
                }
                if agree == 0 {
                    continue;
                }

                // At the divergence: the page draws one thing, the text draws
                // `.notdef`. Ask the inverse what draws the page's glyph.
                let Some(&wanted) = line.glyphs.get(agree) else { continue };
                if drawn.get(agree) != Some(&0) {
                    continue;
                }
                // Which character of the text produced that .notdef.
                let Some(bad) = char_at_glyph(&face, text, agree) else { continue };
                if let Some(spellings) = inverse.get(&vec![wanted]) {
                    let entry = solved.entry(bad).or_default();
                    for s in spellings {
                        *entry.entry(s.clone()).or_default() += 1;
                    }
                }
            }
        }

        println!("{lines_clean} of {lines_seen} lines already shape exactly");
        println!("\nsuspect characters in the text:");
        for (c, n) in &suspects {
            println!("   U+{:04X} {c:?}  {n} times", *c as u32);
        }

        println!("\nwhat the page says each suspect should have been:");
        for (bad, spellings) in &solved {
            let mut best: Vec<(&String, &usize)> = spellings.iter().collect();
            best.sort_by(|a, b| b.1.cmp(a.1));
            let shown: Vec<String> = best
                .iter()
                .take(3)
                .map(|(s, n)| format!("{s:?} x{n}"))
                .collect();
            println!("   U+{:04X} {bad:?} -> {}", *bad as u32, shown.join(", "));
        }
    }

    /// Which character of `text` produced the glyph at `at`.
    fn char_at_glyph(face: &rustybuzz::Face, text: &str, at: usize) -> Option<char> {
        let mut buffer = rustybuzz::UnicodeBuffer::new();
        buffer.push_str(text);
        let shaped = rustybuzz::shape(face, &[], buffer);
        let cluster = shaped.glyph_infos().get(at)?.cluster as usize;
        text[cluster..].chars().next()
    }

    /// A bounded set of Devanagari clusters: the consonants, each with a
    /// virama before and after, and each pair joined by one.
    fn devanagari_clusters() -> Vec<String> {
        const VIRAMA: char = '\u{094D}';
        let consonants: Vec<char> = ('\u{0915}'..='\u{0939}')
            .chain('\u{0958}'..='\u{095F}')
            .collect();
        let signs: Vec<char> = ('\u{093E}'..='\u{094C}').chain(['\u{0902}', '\u{0903}', '\u{0901}']).collect();

        let mut out: Vec<String> = Vec::new();
        for &c in &consonants {
            out.push(c.to_string());
            out.push(format!("{c}{VIRAMA}"));
            out.push(format!("{VIRAMA}{c}"));
            for &s in &signs {
                out.push(format!("{c}{s}"));
            }
            for &d in &consonants {
                out.push(format!("{c}{VIRAMA}{d}"));
            }
        }
        out
    }

    /// The clusters, plus a matra on every CONJUNCT as well as on every single
    /// consonant.
    ///
    /// ⚠️ A MATRA ON A CONJUNCT IS NOT THE SAME GLYPH. The `ि` reaches over
    /// the cluster that follows it, so the face carries a width variant of it
    /// per cluster, and `क्षि` needs a wider one than `कि` does. Enumerating
    /// only the single consonants named three of those variants and left the
    /// rest unspellable: measured, glyphs 336, 339 and 342 alone were 248 of
    /// the 350 characters the repair could not name.
    fn devanagari_clusters_wide() -> Vec<String> {
        const VIRAMA: char = '\u{094D}';
        let consonants: Vec<char> = ('\u{0915}'..='\u{0939}')
            .chain('\u{0958}'..='\u{095F}')
            .collect();
        let signs: Vec<char> = ('\u{093E}'..='\u{094C}')
            .chain(['\u{0902}', '\u{0903}', '\u{0901}'])
            .collect();

        let mut out = devanagari_clusters();
        for &c in &consonants {
            for &d in &consonants {
                for &s in &signs {
                    out.push(format!("{c}{VIRAMA}{d}{s}"));
                }
                // ⚠️ AND A CONJUNCT IS NOT ALWAYS TWO CONSONANTS. `शस्त्र`
                // draws its `स्त्र` as a single glyph, and no two-consonant
                // cluster produces it. The third consonant is not free though:
                // it is the one a virama can carry under a conjunct, which in
                // practice is the ra, ya and va forms.
                for tail in ['र', 'य', 'व'] {
                    out.push(format!("{c}{VIRAMA}{d}{VIRAMA}{tail}"));
                    for &s in &signs {
                        out.push(format!("{c}{VIRAMA}{d}{VIRAMA}{tail}{s}"));
                    }
                }
            }
        }
        out
    }

    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn how_much_of_a_devanagari_page_proves_from_its_own_text() {
        // Every Devanagari face this machine carries. A page whose font names
        // itself is matched by name; one that does not is matched by WHICH FACE
        // PROVES IT, which is the only identification these files allow.
        const CANDIDATES: [(&str, &str); 7] = [
            ("Mangal", r"C:\Windows\Fonts\mangal.ttf"),
            ("Mangal Bold", r"C:\Windows\Fonts\mangalb.ttf"),
            ("Nirmala UI", r"C:\Windows\Fonts\NIRMALA.TTF"),
            ("Nirmala UI Bold", r"C:\Windows\Fonts\NIRMALAB.TTF"),
            ("Aparajita", r"C:\Windows\Fonts\APARAJ.TTF"),
            ("Kokila", r"C:\Windows\Fonts\KOKILA.TTF"),
            ("Utsaah", r"C:\Windows\Fonts\UTSAAH.TTF"),
        ];
        const PAGES: usize = 3;

        let loaded: Vec<(&str, Vec<u8>)> = CANDIDATES
            .iter()
            .filter_map(|(name, path)| std::fs::read(path).ok().map(|b| (*name, b)))
            .collect();
        println!("{} candidate faces installed", loaded.len());

        for file in HINDI {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            println!("\n================ {} ================",
                file.rsplit('\\').next().unwrap());

            let bytes = std::fs::read(file).unwrap();
            let Ok(doc) = Document::load_mem(&bytes) else { continue };
            let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();

            for (n, &page) in pages.iter().take(PAGES).enumerate() {
                // What the FILE says this page says, per text object.
                let said = page_text(&bytes, n);
                let said: Vec<&String> = said.iter().filter(|s| !s.trim().is_empty()).collect();

                let lines = lines_of(&doc, page);
                if lines.is_empty() || said.is_empty() {
                    println!("\n-- page {n}: {} lines, {} text objects, nothing to compare",
                        lines.len(), said.len());
                    continue;
                }

                // Group the page's lines by the font resource that drew them.
                let mut by_resource: BTreeMap<Vec<u8>, Vec<&Line>> = BTreeMap::new();
                for line in &lines {
                    by_resource.entry(line.resource.clone()).or_default().push(line);
                }
                let named = fonts_named(&doc, page);

                println!("\n-- page {n}: {} lines, {} text objects",
                    lines.len(), said.len());

                for (resource, mine) in &by_resource {
                    // Only the runs long enough to mean something.
                    let worth: Vec<&&Line> = mine.iter().filter(|l| l.glyphs.len() >= 3).collect();
                    if worth.is_empty() {
                        continue;
                    }
                    let name = named
                        .get(resource)
                        .cloned()
                        .unwrap_or_else(|| "(unnamed)".into());

                    // Try every face, and let the winner identify the font.
                    let mut best: Option<(&str, usize, usize)> = None;
                    for (face_name, face_bytes) in &loaded {
                        let Some(face) = rustybuzz::Face::from_slice(face_bytes, 0) else {
                            continue;
                        };
                        let shaped: Vec<Vec<u16>> = said
                            .iter()
                            .map(|s| crate::reshape::draws(&face, s))
                            .collect();

                        let mut proven = 0usize;
                        let mut close = 0usize;
                        for line in &worth {
                            if shaped.iter().any(|g| *g == line.glyphs) {
                                proven += 1;
                                continue;
                            }
                            // How far the best candidate string agrees before
                            // it diverges: the difference between "wrong face"
                            // and "right face, damaged text".
                            let agree = shaped
                                .iter()
                                .map(|g| {
                                    g.iter()
                                        .zip(&line.glyphs)
                                        .take_while(|(a, b)| a == b)
                                        .count()
                                })
                                .max()
                                .unwrap_or(0);
                            if agree * 2 >= line.glyphs.len() {
                                close += 1;
                            }
                        }
                        if best.is_none_or(|(_, p, c)| (proven, close) > (p, c)) {
                            best = Some((face_name, proven, close));
                        }
                    }

                    let Some((face_name, proven, close)) = best else { continue };
                    println!("   {:<20} {:>3} lines: {proven} proven, {close} half-agreeing \
                        (best face: {face_name})",
                        name, worth.len());

                    // ⚠️ AND WHAT A FAILURE LOOKS LIKE, on the winning face.
                    if proven < worth.len() {
                        let Some((_, face_bytes)) =
                            loaded.iter().find(|(nm, _)| nm == &face_name) else { continue };
                        let Some(face) = rustybuzz::Face::from_slice(face_bytes, 0) else {
                            continue;
                        };
                        for line in worth.iter().take(2) {
                            if said.iter().any(|s| crate::reshape::draws(&face, s) == line.glyphs) {
                                continue;
                            }
                            let best_s = said.iter().max_by_key(|s| {
                                crate::reshape::draws(&face, s)
                                    .iter()
                                    .zip(&line.glyphs)
                                    .take_while(|(a, b)| a == b)
                                    .count()
                            });
                            if let Some(s) = best_s {
                                let g = crate::reshape::draws(&face, s);
                                let agree = g
                                    .iter()
                                    .zip(&line.glyphs)
                                    .take_while(|(a, b)| a == b)
                                    .count();
                                println!("      line of {} glyphs, closest text agrees for {agree}",
                                    line.glyphs.len());
                                println!("         “{}”", s.chars().take(44).collect::<String>());
                                println!("         page draws {:?}",
                                    &line.glyphs[..line.glyphs.len().min(10)]);
                                println!("         text shapes {:?}",
                                    &g[..g.len().min(10)]);
                            }
                        }
                    }
                }
            }
        }
    }

    /// What PDFium reads off one page of a document held in memory.
    fn page_text(bytes: &[u8], page_index: usize) -> Vec<String> {
        let handle = crate::open_document_from_bytes(bytes.as_ptr(), bytes.len());
        if handle == 0 {
            return Vec::new();
        }
        let out = crate::tests::texts_of_page(handle, page_index as i32);
        crate::close_document(handle);
        out
    }

    /// Nirmala UI, which is what these books are set in, by whichever name the
    /// page gives. Local to this measurement on purpose: putting it in
    /// `installed` would make every Hindi page look worth RECOVERING, and
    /// recovery means enumerating Burmese.
    /// ⚠️ WHAT STOPS THE EXISTING WRITER FROM TOUCHING A HINDI LINE. `retype`
    /// is not Myanmar-specific: it takes a font path and two plain strings. But
    /// it finds the line by reading its glyphs back through `recover::Indexes`,
    /// and that index is keyed on the page's font NAME. This asks what those
    /// names are on a real Hindi file and what the index makes of them.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_the_writer_makes_of_a_hindi_page() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = lopdf::Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();

        let lines = lines_of(&doc, page);
        let mut by_font: BTreeMap<String, usize> = BTreeMap::new();
        for line in &lines {
            *by_font.entry(line.base_font.clone()).or_default() += 1;
        }
        println!("{} lines on page 0, drawn by:", lines.len());
        for (font, n) in &by_font {
            println!("   {n:5} lines  {font:24}  installed() -> {:?}", installed(font));
        }

        let clock = std::time::Instant::now();
        let indexes = indexes_for_document(&doc);
        println!("\nindexes_for_document took {:?}, empty: {}",
            clock.elapsed(), indexes.is_empty());
        for font in by_font.keys() {
            println!("   index_for({font:24}) -> {}",
                if indexes.index_for(font).is_some() { "an index" } else { "NOTHING" });
        }
    }

    /// ⚠️ THE RISKY END OF EDITING HINDI, ASKED FIRST. The writer finds a line
    /// by reading its glyphs back through `recover::read_line` and demanding
    /// the caller's text. That walk is not Burmese; only the enumeration that
    /// fills its index is. So: fill one with DEVANAGARI clusters and ask the
    /// same walk to read a real Hindi page.
    ///
    /// If it reads, editing Hindi is mostly wiring the existing writer up. If
    /// it does not, the writer needs a route of its own and this is a much
    /// larger piece of work.
    #[test]
    #[ignore = "diagnostic, and needs a PDF and fonts that are not in this repository"]
    fn whether_the_writers_walk_can_read_a_hindi_line() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        const NIRMALA: &str = r"C:\Windows\Fonts\Nirmala.ttf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(NIRMALA).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = lopdf::Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let font_bytes = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font_bytes, 0).unwrap();

        let clock = std::time::Instant::now();
        let index = crate::devanagari::reading_index(r"C:\Windows\Fonts\NIRMALA.TTF")
            .expect("no tables for Nirmala");
        println!("built a devanagari index in {:?}", clock.elapsed());

        let lines = lines_of(&doc, page);
        let mut read = 0;
        let mut devanagari_lines = 0;
        let mut shown = 0;
        for line in &lines {
            // Only the runs long enough to be words; a one-glyph line proves
            // nothing either way.
            if line.glyphs.len() < 4 {
                continue;
            }
            devanagari_lines += 1;
            match read_line(&index, &face, line) {
                Some(text) if text.chars().any(crate::devanagari::is_devanagari) => {
                    read += 1;
                    if shown < 8 {
                        shown += 1;
                        println!("   READ {:?}", text.chars().take(48).collect::<String>());
                    }
                }
                _ => {}
            }
        }
        println!("\n{read} of {devanagari_lines} runs read by the writer's own walk");
    }

    /// ⚠️ THE WRITER'S OWN CHECK, ON A REAL HINDI PAGE. `retype` refuses unless
    /// it can find the line the caller means, and it finds it by reading the
    /// line's glyphs back through this index and demanding the caller's text.
    /// On a book naming its fonts `CIDFont+F1`..`F7` that index was EMPTY, so
    /// every Hindi line was refused before the writer looked at it.
    ///
    /// This asks for the whole chain: the fonts resolve by evidence, the index
    /// fills with Devanagari, and a line reads back as the words on the page.
    #[test]
    #[ignore = "diagnostic, and needs a PDF and fonts that are not in this repository"]
    fn a_hindi_line_can_be_found_the_way_the_writer_finds_one() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = lopdf::Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();

        let clock = std::time::Instant::now();
        let indexes = indexes_for_document(&doc);
        let built = clock.elapsed();
        assert!(!indexes.is_empty(), "the fonts still resolve to nothing");
        println!("indexes_for_document took {built:?}");

        // Every line the writer could now locate, and what it would call it.
        let lines = lines_of(&doc, page);
        let mut found = 0;
        let mut shown = 0;
        for line in &lines {
            let Some(entry) = indexes.by_font.get(&line.base_font) else { continue };
            let Some(face) = rustybuzz::Face::from_slice(&entry.0, 0) else { continue };
            let Some(text) = read_line(&entry.1, &face, line) else { continue };
            if !text.chars().any(crate::devanagari::is_devanagari) {
                continue;
            }
            found += 1;
            if shown < 6 {
                shown += 1;
                println!("   at y={:.1}: {:?}", line.y,
                    text.chars().take(40).collect::<String>());
            }
        }
        println!("\n{found} of {} lines can now be located by the writer", lines.len());
        assert!(found > 0, "no line reads, so the writer still cannot find one");
    }

    fn devanagari_file(base_font: &str) -> Option<&'static str> {
        let name = base_font.to_lowercase();
        let bold = name.contains("bold");
        if name.contains("nirmala") {
            return Some(if bold {
                r"C:\Windows\Fonts\NIRMALAB.TTF"
            } else {
                r"C:\Windows\Fonts\NIRMALA.TTF"
            });
        }
        if name.contains("mangal") {
            return Some(r"C:\Windows\Fonts\mangal.ttf");
        }
        None
    }

    /// The font program each of a page's resources embeds, by resource name.
    fn embedded_programs(doc: &Document, page: ObjectId) -> BTreeMap<Vec<u8>, Vec<u8>> {
        let mut out = BTreeMap::new();
        let Some(fonts) = doc
            .get_dictionary(page)
            .ok()
            .and_then(|p| p.get(b"Resources").ok().and_then(|o| dictionary(doc, o)))
            .and_then(|r| r.get(b"Font").ok().and_then(|o| dictionary(doc, o)))
        else {
            return out;
        };
        for (name, obj) in fonts.iter() {
            let Some(font) = dictionary(doc, obj) else { continue };
            let holder = match font.get(b"DescendantFonts").ok() {
                Some(o) => {
                    let arr = match o {
                        Object::Array(a) => a.clone(),
                        Object::Reference(id) => match doc.get_object(*id).ok()
                            .and_then(|o| o.as_array().ok()) {
                            Some(a) => a.clone(),
                            None => continue,
                        },
                        _ => continue,
                    };
                    match arr.first().and_then(|f| dictionary(doc, f)) {
                        Some(d) => d,
                        None => continue,
                    }
                }
                None => font.clone(),
            };
            let Some(d) = holder.get(b"FontDescriptor").ok().and_then(|o| dictionary(doc, o))
            else { continue };
            let Some(id) = [&b"FontFile2"[..], b"FontFile3", b"FontFile"]
                .iter()
                .find_map(|k| d.get(k).ok().and_then(|v| v.as_reference().ok()))
            else { continue };
            let Some(bytes) = doc
                .get_object(id)
                .ok()
                .and_then(|o| o.as_stream().ok())
                .and_then(|s| s.decompressed_content().ok())
            else { continue };
            if rustybuzz::Face::from_slice(&bytes, 0).is_some() {
                out.insert(name.to_vec(), bytes);
            }
        }
        out
    }

    /// Every font resource of a page, by the best name available for it: the
    /// program's own, falling back to the BaseFont.
    fn fonts_named(doc: &Document, page: ObjectId) -> BTreeMap<Vec<u8>, String> {
        let mut out = BTreeMap::new();
        let Some(fonts) = doc
            .get_dictionary(page)
            .ok()
            .and_then(|p| p.get(b"Resources").ok().and_then(|o| dictionary(doc, o)))
            .and_then(|r| r.get(b"Font").ok().and_then(|o| dictionary(doc, o)))
        else {
            return out;
        };
        for (name, obj) in fonts.iter() {
            let Some(font) = dictionary(doc, obj) else { continue };
            let best = program_family(doc, &font).or_else(|| {
                font.get(b"BaseFont")
                    .ok()
                    .and_then(|o| o.as_name().ok())
                    .map(|n| String::from_utf8_lossy(n).into_owned())
            });
            if let Some(best) = best {
                out.insert(name.to_vec(), best);
            }
        }
        out
    }

    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn how_wide_a_producers_gaps_are_against_a_real_space() {
        const FILES: [&str; 2] = [
            r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf",
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf",
        ];

        for file in FILES {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            let doc = Document::load(file).unwrap();
            let (_, &page) = doc.get_pages().iter().next().unwrap();
            let indexes = indexes_for_document(&doc);
            let read = read_page_with(&doc, page, &indexes);
            let lines = lines_of(&doc, page);

            println!("\n=== {} ===", file.rsplit('\\').next().unwrap());
            for (i, line) in lines.iter().enumerate() {
                if line.breaks.is_empty() {
                    continue;
                }
                // How wide the font's own space glyph is at this size, so the
                // gaps can be compared against the thing they claim to be.
                let space = installed(&line.base_font)
                    .and_then(|p| std::fs::read(p).ok())
                    .and_then(|b| {
                        let face = rustybuzz::Face::from_slice(&b, 0)?;
                        let upem = face.units_per_em() as f64;
                        let id = face.glyph_index(' ')?;
                        let w = face.glyph_hor_advance(id)? as f64;
                        Some(w / upem)
                    })
                    .unwrap_or(f64::NAN);

                // How many of the line's own glyphs are the font's SPACE, and
                // how many placements it is made of. A producer that draws its
                // spaces as glyphs is not making them out of placement gaps.
                let space_glyphs = installed(&line.base_font)
                    .and_then(|p| std::fs::read(p).ok())
                    .and_then(|b| {
                        let face = rustybuzz::Face::from_slice(&b, 0)?;
                        let id = face.glyph_index(' ')?.0;
                        Some(line.glyphs.iter().filter(|g| **g == id).count())
                    })
                    .unwrap_or(usize::MAX);

                let widths: Vec<String> = line
                    .breaks
                    .iter()
                    .map(|b| format!("{:.3}", b.points / line.size))
                    .collect();
                let says = read
                    .iter()
                    .find(|r| (r.y - line.y).abs() < 1e-9)
                    .and_then(|r| r.text.clone())
                    .unwrap_or_else(|| "-".into());
                println!(
                    "{i:>3} size={:.2} ops={:>3} tracked={} \
                     gaps(em)=[{}]\n     “{says}”",
                    line.size, line.drawn_by.len(), line.tracked, widths.join(" ")
                );
            }
        }
    }

    #[test]
    #[ignore = "needs Myanmar PDFs that are not in this repository"]
    fn what_the_myanmar_corpus_looks_like() {
        const FOLDER: &str = r"D:\Ayaan PDF Test file";
        if !std::path::Path::new(FOLDER).exists() {
            return;
        }
        let mut files: Vec<std::path::PathBuf> = std::fs::read_dir(FOLDER)
            .unwrap()
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| {
                // ⚠️ BY FONT NAME AS WELL AS BY SCRIPT NAME. This matched only
                // "myanmar" and so never once looked at the Pyidaungsu file the
                // user supplied for exactly this measurement: the corpus table
                // reported no Pyidaungsu because it had not read the file that
                // had it, not because the file was missing.
                p.file_name()
                    .and_then(|n| n.to_str())
                    .map(|n| n.to_lowercase())
                    .is_some_and(|n| n.contains("myanmar") || n.contains("pyidaungsu"))
            })
            .collect();
        files.sort();

        for file in &files {
            let Ok(doc) = Document::load(file) else {
                println!("{:?}: would not load", file.file_name().unwrap());
                continue;
            };
            let pages = doc.get_pages();
            println!("\n{:?}: {} page(s)", file.file_name().unwrap(), pages.len());

            for (n, (_, &page)) in pages.iter().enumerate() {
                let started = std::time::Instant::now();
                let lines = lines_of(&doc, page);

                // Every font the page's text is set in, and how many lines each.
                let mut by_font: BTreeMap<String, usize> = BTreeMap::new();
                for line in &lines {
                    *by_font.entry(line.base_font.clone()).or_default() += 1;
                }
                let known: Vec<String> = by_font
                    .keys()
                    .map(|f| format!("{f} -> {:?}", installed(f).map(|p| {
                        std::path::Path::new(p)
                            .file_name().unwrap().to_str().unwrap().to_string()
                    })))
                    .collect();

                let indexes = indexes_for(&doc, page);
                let built = started.elapsed();
                let read = read_page_with(&doc, page, &indexes);
                let proven = read.iter().filter(|r| r.text.is_some()).count();

                println!("  page {n}: {} runs, {} lines, {proven} read, \
                          index {built:.1?}, whole page {:.1?}",
                    lines.len(), read.len(), started.elapsed());

                // ⚠️ PER FONT, because "16 of 30" on a page set in two weights
                // does not say whether the half that failed is a font that
                // resolved to nothing or a font that resolved to the wrong
                // build. Those are different bugs with different fixes.
                let mut said: BTreeMap<String, (usize, usize)> = BTreeMap::new();
                for r in &read {
                    let e = said.entry(r.font.clone()).or_default();
                    e.1 += 1;
                    if r.text.is_some() { e.0 += 1; }
                }
                for f in &known {
                    let family = f.split(" ->").next().unwrap_or_default();
                    let (ok, all) = said.get(family).copied().unwrap_or((0, 0));
                    println!("      {f}   read {ok} of {all}");
                }
            }
        }
    }

    /// MEASUREMENT: what a retype costs once the reading has been paid for.
    ///
    /// ⚠️ IT USED TO BUILD ITS OWN INDEX TWICE, once to check the line still
    /// says what the caller thinks and once more after embedding the font, so
    /// one keystroke cost about forty seconds on top of the twenty the reader
    /// had already waited. The app has an index in hand by then, because it
    /// read the line in order to offer it, so this asks what changes when it is
    /// lent rather than rebuilt.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_a_retype_costs_with_and_without_the_index_it_could_borrow() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not here");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        let started = std::time::Instant::now();
        let indexes = indexes_for_document(&doc);
        println!("preparing the document: {:.1?}", started.elapsed());

        let read = read_page_with(&doc, page, &indexes);
        let Some(line) = read.iter().find(|r| r.text.is_some()) else {
            println!("nothing read");
            return;
        };
        let was = line.text.clone().unwrap();
        let now = format!("{was}\u{1000}");

        let font = installed(&line.font).expect("no font for the line");

        let lent = std::time::Instant::now();
        let with = crate::retype::retype(&bytes, 0, line.y, &was, &now, font, Some(&indexes));
        let lent = lent.elapsed();

        let alone = std::time::Instant::now();
        let without = crate::retype::retype(&bytes, 0, line.y, &was, &now, font, None);
        let alone = alone.elapsed();

        println!("retype with the index lent:  {:.1?}  ({})",
            lent, if with.is_ok() { "wrote" } else { "refused" });
        println!("retype building its own:     {:.1?}  ({})",
            alone, if without.is_ok() { "wrote" } else { "refused" });

        // ⚠️ AND THE SAME ANSWER EITHER WAY. A borrowed index that changed the
        // verdict would be a saving bought with correctness.
        assert_eq!(with.is_ok(), without.is_ok(), "lending the index changed the answer");
    }

    /// MEASUREMENT: is there anywhere inside a paragraph where a click lands on
    /// no line at all?
    ///
    /// ⚠️ THE APP FINDS A LINE BY ASKING WHICH BOX A POINT IS IN, and a
    /// recovered line's box is the FACE's height at that size, not the leading
    /// the page was set with. A generously leaded paragraph therefore has
    /// STRIPES OF NOTHING between its lines, and a click landing in one falls
    /// through to PDFium's word reader, which on a Burmese page returns
    /// scrambled fragments. That is what "This text cannot be edited in place
    /// yet" on a paragraph that reads perfectly actually means.
    ///
    /// This prints the gaps so the size of them is a number rather than a
    /// guess. The app's tolerance is 0.004 of the page width.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn where_a_click_inside_a_paragraph_lands_on_no_line() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not here");
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let indexes = indexes_for_document(&doc);

        // The app's own tolerance, from ViewportViewModel::LineAt.
        const TOLERANCE: f64 = 0.004;

        for (n, &page) in pages.iter().enumerate() {
            let (_, page_top, page_w) = page_box(&doc, page).unwrap();
            let mut read: Vec<&Reading> = Vec::new();
            let all = read_page_with(&doc, page, &indexes);
            read.extend(all.iter().filter(|r| r.text.is_some()));
            if read.len() < 2 {
                continue;
            }

            // Normalized the way the app draws: down from the top of the page,
            // both axes over its WIDTH.
            let mut boxes: Vec<(f64, f64)> = read
                .iter()
                .map(|r| ((page_top - r.top) / page_w, (page_top - r.bottom) / page_w))
                .collect();
            boxes.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());

            let mut gaps: Vec<f64> = Vec::new();
            for pair in boxes.windows(2) {
                let gap = pair[1].0 - pair[0].1;
                if gap > 0.0 {
                    gaps.push(gap);
                }
            }
            let dead = gaps.iter().filter(|g| **g > TOLERANCE * 2.0).count();
            let worst = gaps.iter().cloned().fold(0.0f64, f64::max);

            println!(
                "page {n}: {} lines read, {} of {} gaps are wider than the tolerance can \
                 close, worst {:.4} of the page width ({:.1} pt on this page)",
                read.len(), dead, gaps.len(), worst, worst * page_w);
        }
    }

    /// MEASUREMENT: can one index serve a whole document, or must every page
    /// pay for its own?
    ///
    /// ⚠️ THE NUMBER PHASE 5 TURNS ON. An index is built per page today, keyed
    /// by (document, page) and scoped to the glyph ids THAT page draws, and it
    /// costs about 17 seconds. A document of N Burmese pages therefore costs
    /// N x 17s, and every index is thrown away when an edit rewrites the
    /// document behind a new handle, so the wait comes back after each edit.
    ///
    /// Two ways out, both measured here against the per-page baseline:
    ///
    ///   UNION      one index scoped to every glyph every page draws. Needs all
    ///              the pages parsed first, which is cheap, but enumerates a
    ///              larger set.
    ///   DECLARED   one index scoped to the CIDs the embedded SUBSET declares a
    ///              width for, which is the whole document's glyph set and is
    ///              available from the font dictionary WITHOUT parsing a single
    ///              page.
    ///
    /// ⚠️ AND COVERAGE IS MEASURED WITH THE COST, because an index that is
    /// faster and reads fewer lines is not a saving. Spellings grow
    /// COMBINATORIALLY with the character mix, so a wider scope can cost more
    /// than the pages it replaces; that is exactly what this is for.
    #[test]
    #[ignore = "diagnostic, and needs a multi-page Burmese PDF that is not in this repository"]
    fn whether_one_index_can_serve_a_whole_document() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("no multi-page Burmese file here");
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        println!("{} pages", pages.len());

        // ---- the baseline: one index per page, as it works today ----
        let started = std::time::Instant::now();
        let mut per_page_read = 0usize;
        let mut per_page_lines = 0usize;
        for (n, &page) in pages.iter().enumerate() {
            let at = std::time::Instant::now();
            let indexes = indexes_for(&doc, page);
            let read = read_page_with(&doc, page, &indexes);
            let proven = read.iter().filter(|r| r.text.is_some()).count();
            per_page_read += proven;
            per_page_lines += read.len();
            println!("  page {n}: {proven} of {} in {:.1?}", read.len(), at.elapsed());
        }
        let per_page = started.elapsed();
        println!("PER PAGE: {per_page_read} of {per_page_lines} lines, {per_page:.1?}");

        // ---- what each scope would cover ----
        let mut union: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
        let mut declared: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
        for &page in &pages {
            for line in lines_of(&doc, page) {
                union.entry(line.base_font.clone()).or_default().extend(line.glyphs);
            }
            for (_, (base_font, widths)) in fonts_of(&doc, page) {
                if let Some(w) = widths {
                    declared.entry(base_font).or_default().extend(w.declared());
                }
            }
        }
        for (font, glyphs) in &union {
            let d = declared.get(font).map(|s| s.len()).unwrap_or(0);
            println!("  {font}: pages draw {} glyphs, the subset declares {d}",
                glyphs.len());
        }

        // ---- one index for the document ----
        //
        // ⚠️ THE SHIPPED FUNCTION, not a copy of it. This measurement
        // decided that the shipped one should exist, so it has to keep asking
        // the real thing or it stops being evidence about the real thing.
        for label in ["DOCUMENT"] {
            let started = std::time::Instant::now();
            let shared = indexes_for_document(&doc);
            let built = started.elapsed();

            let mut read_total = 0usize;
            let mut lines_total = 0usize;
            let mut each: Vec<String> = Vec::new();
            for &page in &pages {
                let read = read_page_with(&doc, page, &shared);
                let proven = read.iter().filter(|r| r.text.is_some()).count();
                each.push(format!("{proven}/{}", read.len()));
                read_total += proven;
                lines_total += read.len();
            }
            println!("  {label} per page: {}", each.join("  "));
            println!("{label}: {read_total} of {lines_total} lines, \
                      built in {built:.1?}, all pages in {:.1?}",
                started.elapsed());
        }
    }

    /// MEASUREMENT: which Pyidaungsu on this machine actually reads the page.
    ///
    /// ⚠️ THERE ARE THREE OF THEM AND THEY ARE DIFFERENT FILES. `installed`
    /// hardcodes `C:\Windows\Fonts\Pyidaungsu.ttf`, which is an older build
    /// than the 2.5.3 the user installed PER-USER, and a third is registered by
    /// an unrelated application. Proving a line means shaping candidate text
    /// and demanding the page's own glyph ids back, so the build matters: a
    /// font that renames or renumbers a glyph proves nothing.
    ///
    /// The question this answers is whether resolving fonts through the
    /// registry is worth building, or whether the file already on the system
    /// path reads the page just as well.
    #[test]
    #[ignore = "diagnostic, and needs fonts and a PDF that are not in this repository"]
    fn which_pyidaungsu_reads_the_users_page() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text test pdf.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("no Pyidaungsu file here");
            return;
        }
        let local = std::env::var("LOCALAPPDATA").unwrap_or_default();
        const SUPPLIED: &str = r"D:\Ayaan PDF Test file";
        let candidates: Vec<(&str, String)> = vec![
            ("system Pyidaungsu.ttf", r"C:\Windows\Fonts\Pyidaungsu.ttf".to_string()),
            ("per-user 2.5.3 Regular",
             format!(r"{local}\Microsoft\Windows\Fonts\Pyidaungsu-2.5.3_Regular.ttf")),
            ("per-user 2.5.3 Bold",
             format!(r"{local}\Microsoft\Windows\Fonts\Pyidaungsu-2.5.3_Bold.ttf")),
            ("supplied 2.5.3 Numbers",
             format!(r"{SUPPLIED}\Pyidaungsu-2.5.3_Numbers.ttf")),
            ("BMSTU pyidaungsu-1.2",
             r"C:\Program Files (x86)\BMSTU\MMUDictionary\pyidaungsu-1.2.ttf".to_string()),
        ];

        let doc = Document::load(FILE).unwrap();
        let pages = doc.get_pages();
        let (_, &page) = pages.iter().next().unwrap();
        let lines = lines_of(&doc, page);

        // Every font the page names, and the glyphs each draws.
        let mut wanted: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
        for line in &lines {
            wanted.entry(line.base_font.clone()).or_default().extend(line.glyphs.iter().copied());
        }
        for (font, glyphs) in &wanted {
            println!("{font}: {} lines, {} distinct glyphs",
                lines.iter().filter(|l| &l.base_font == font).count(), glyphs.len());
        }

        // ⚠️ WHAT THE PAGE ACTUALLY ASKS FOR, so a font that reads none of it
        // can be told apart from a font that is simply absent. A subset numbers
        // its glyphs from the font it was cut out of, so if a candidate orders
        // its glyphs differently the same id means a different letter and NOTHING
        // can ever be proven with it. That is not a bug to fix: it is the wrong
        // font, and a refusal is the only honest answer.
        let highest = wanted.values().flatten().max().copied().unwrap_or(0);
        println!("the page's highest glyph id is {highest}");


        for (label, path) in &candidates {
            if !std::path::Path::new(path).exists() {
                println!("\n{label}: not on this machine");
                continue;
            }
            let bytes = std::fs::read(path).unwrap();
            println!("\n{label} ({} bytes)", bytes.len());

            // Where this font puts a few plain Burmese letters. Two builds that
            // agree here share a glyph order and can read each other's subsets;
            // two that disagree cannot, whatever else is true of them.
            if let Some(face) = rustybuzz::ttf_parser::Face::parse(&bytes, 0).ok() {
                let where_are: Vec<String> = ['\u{1000}', '\u{1005}', '\u{1010}', '\u{1019}']
                    .iter()
                    .map(|c| match face.glyph_index(*c) {
                        Some(g) => format!("{c}={}", g.0),
                        None => format!("{c}=none"),
                    })
                    .collect();
                println!("   {} glyphs; {}", face.number_of_glyphs(), where_are.join(" "));
            }

            // ⚠️ AND WHERE IT PUTS THE JOINED FORMS, which is what actually
            // decides this. A Burmese line is drawn almost entirely in COMPOSED
            // glyphs, and those are numbered above the plain letters, exactly
            // where two builds of one family differ when one carries more
            // glyphs than the other. Two fonts can agree on every plain letter
            // and still share not one shaped word.
            if let Some(face) = rustybuzz::Face::from_slice(&bytes, 0) {
                let words = [
                    "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}",
                    "\u{1000}\u{103C}\u{1014}\u{103A}",
                    "\u{1005}\u{102C}",
                ];
                let shaped: Vec<String> = words
                    .iter()
                    .map(|w| format!("{w}={:?}", crate::reshape::draws(&face, w)))
                    .collect();
                println!("   shaped {}", shaped.join("  "));
            }

            for (font, glyphs) in &wanted {
                let started = std::time::Instant::now();
                let Some(index) = crate::reshape::Index::build(&bytes, None, Some(glyphs)) else {
                    println!("   {font}: no index (the font draws none of these glyphs)");
                    continue;
                };
                let mut indexes = Indexes::default();
                indexes.by_font.insert(font.clone(), Arc::new((bytes.clone(), index)));

                let read = read_page_with(&doc, page, &indexes);
                let mine: Vec<&Reading> = read.iter().filter(|r| &r.font == font).collect();
                let proven = mine.iter().filter(|r| r.text.is_some()).count();
                println!("   {font}: read {proven} of {} in {:.1?}",
                    mine.len(), started.elapsed());
            }
        }
    }

    /// What an index COSTS, against how many distinct characters it is asked to
    /// cover. Diagnostic, and the number that decides whether a document of
    /// many pages can share one index or must pay for each.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn what_an_index_costs_against_the_characters_it_covers() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();

        // The characters this page actually uses, found the way indexes_for
        // finds them: by reading the page and keeping what proved.
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = indexes_for(&doc, page);
        let read = read_page_with(&doc, page, &indexes);
        let mut used: BTreeSet<char> = BTreeSet::new();
        for r in &read {
            if let Some(text) = &r.text {
                used.extend(text.chars().filter(|c| !c.is_whitespace()));
            }
        }
        println!("the page uses {} distinct characters", used.len());

        // ⚠️ HOW IT SCALES IS THE WHOLE QUESTION. If the cost is roughly flat
        // in the character count, a whole document can share ONE index and a
        // reader pays once instead of once a page. If it climbs steeply, each
        // page has to be narrowed to its own and there is nothing to share.
        let whole_block: BTreeSet<char> = ('\u{1000}'..='\u{109F}').collect();
        let half: BTreeSet<char> = used.iter().take(used.len() / 2).copied().collect();

        for (what, chars) in [
            ("half the page's characters", &half),
            ("the page's own characters", &used),
            ("the whole Myanmar block", &whole_block),
        ] {
            let started = std::time::Instant::now();
            let index = crate::reshape::Index::build(&bytes, Some(chars), None);
            println!("{:>28}: {:>3} chars, {:>8.1?}, {} spellings",
                what, chars.len(), started.elapsed(),
                index.map(|i| i.len()).unwrap_or(0));
        }
    }

    /// How many glyphs a PAGE draws, against how many the DOCUMENT's embedded
    /// subset declares. Diagnostic: the gap between them is what a
    /// document-wide index would have to cover, and therefore pay for.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn how_much_wider_the_subset_is_than_one_page() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() {
            return;
        }
        let doc = Document::load(FILE).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        let mut drawn: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
        for line in lines_of(&doc, page) {
            drawn.entry(line.base_font).or_default().extend(line.glyphs);
        }

        // What each font resource DECLARES, from the widths the file lists.
        let fonts = fonts_of(&doc, page);
        for (resource, (base_font, widths)) in &fonts {
            let Some(w) = widths.as_ref() else { continue };
            let declared = w.declared().count();
            println!(
                "{:>16} {base_font:<22} page draws {:>3}, file declares {declared}",
                String::from_utf8_lossy(resource),
                drawn.get(base_font).map(|g| g.len()).unwrap_or(0));
        }
    }

    /// HOW a page positions the text it draws, which is what decides how a
    /// line could be moved. Diagnostic.
    ///
    /// Three things are asked of every text object on the page: whether it
    /// starts with an absolute `Tm`, whether anything inside it is placed
    /// relatively afterwards, and whether the page has put a transform under it
    /// with `cm`. A line whose placement is absolute can be moved by changing
    /// six numbers; one placed relative to the line before it cannot, because
    /// moving it would move everything that follows.
    #[test]
    #[ignore = "needs PDFs that are not in this repository"]
    fn how_the_real_files_position_their_text() {
        const FILES: [(&str, usize); 3] = [
            (r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf", 0),
            (r"D:\Ayaan PDF Test file\Pyidaungsu- text test pdf.pdf", 0),
            (r"D:\Ayaan PDF Test file\21_Lessons_for_the_21st_Century_-_Yuval_Noah_Harari.pdf", 2),
        ];

        for (file, page_index) in FILES {
            if !std::path::Path::new(file).exists() {
                println!("{file:?}: not here");
                continue;
            }
            let doc = Document::load(file).unwrap();
            let pages = doc.get_pages();
            let Some((_, &page)) = pages.iter().nth(page_index) else { continue };
            let Ok(content) = lopdf::content::Content::decode(&doc.get_page_content(page))
            else {
                println!("{file:?}: content would not decode");
                continue;
            };

            let (mut objects, mut absolute, mut relative_only, mut empty) = (0, 0, 0, 0);
            let mut moved_under = 0usize;
            let mut depth = 0i32;

            // What the page has put under the text, if anything.
            let mut transforms = 0usize;

            let mut in_text = false;
            let mut first_placement: Option<&str> = None;
            let mut showed = false;

            for op in &content.operations {
                match op.operator.as_str() {
                    "q" => depth += 1,
                    "Q" => depth -= 1,
                    "cm" => {
                        transforms += 1;
                        if depth > 0 { moved_under += 1; }
                    }
                    "BT" => {
                        in_text = true;
                        first_placement = None;
                        showed = false;
                    }
                    "ET" => {
                        if in_text {
                            objects += 1;
                            match (showed, first_placement) {
                                (false, _) => empty += 1,
                                (true, Some("Tm")) => absolute += 1,
                                (true, _) => relative_only += 1,
                            }
                        }
                        in_text = false;
                    }
                    "Tm" | "Td" | "TD" | "T*" if in_text => {
                        if first_placement.is_none() && !showed {
                            first_placement = Some(match op.operator.as_str() {
                                "Tm" => "Tm",
                                other => Box::leak(other.to_string().into_boxed_str()),
                            });
                        }
                    }
                    "TJ" | "Tj" if in_text => showed = true,
                    _ => {}
                }
            }

            let name = std::path::Path::new(file).file_name().unwrap();
            println!(
                "\n{name:?} page {page_index}: {} operations, {objects} text objects",
                content.operations.len());
            println!("   placed absolutely by Tm : {absolute}");
            println!("   placed relatively only  : {relative_only}");
            println!("   drew nothing            : {empty}");
            println!("   cm transforms on the page: {transforms} ({moved_under} inside q/Q)");
        }
    }

    /// Whether a transform is in force when the page draws its text, which
    /// decides whether a move measured on screen is a move in the numbers the
    /// `Tm` operators carry. Diagnostic.
    #[test]
    #[ignore = "needs PDFs that are not in this repository"]
    fn whether_a_transform_is_in_force_over_the_text() {
        const FILES: [(&str, usize); 3] = [
            (r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf", 0),
            (r"D:\Ayaan PDF Test file\Pyidaungsu- text test pdf.pdf", 0),
            (r"D:\Ayaan PDF Test file\21_Lessons_for_the_21st_Century_-_Yuval_Noah_Harari.pdf", 2),
        ];

        for (file, page_index) in FILES {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            let doc = Document::load(file).unwrap();
            let pages = doc.get_pages();
            let Some((_, &page)) = pages.iter().nth(page_index) else { continue };
            let content = lopdf::content::Content::decode(&doc.get_page_content(page)).unwrap();

            let name = std::path::Path::new(file).file_name().unwrap();
            let mut said = false;
            let mut text_after = 0usize;
            let mut seen_cm = false;

            for op in &content.operations {
                match op.operator.as_str() {
                    "cm" => {
                        let m: Vec<f64> = op.operands.iter().filter_map(number).collect();
                        let identity = m.len() == 6
                            && (m[0] - 1.0).abs() < 1e-9 && m[1].abs() < 1e-9
                            && m[2].abs() < 1e-9 && (m[3] - 1.0).abs() < 1e-9
                            && m[4].abs() < 1e-9 && m[5].abs() < 1e-9;
                        println!("{name:?}: cm {m:?} {}",
                            if identity { "(identity)" } else { "(NOT identity)" });
                        said = true;
                        seen_cm = true;
                    }
                    "BT" if seen_cm => text_after += 1,
                    _ => {}
                }
            }
            if !said {
                println!("{name:?}: no cm at all");
            } else {
                println!("{name:?}: {text_after} text objects drawn after it");
            }
        }
    }

    /// ⚠️ THIS HAD NO `#[test]` AND SO HAD NEVER ONCE RUN. It was found while
    /// changing the very function it covers. A test that silently does nothing
    /// is worse than no test, because the file reads as though the rule is held
    /// down when nothing is holding it.
    #[test]
    fn a_subset_tag_is_not_part_of_the_font_s_name() {
        assert_eq!(installed("BCDEEE+MyanmarText"), installed("MyanmarText"));
        assert!(installed("BCDEEE+MyanmarText").is_some());
        assert!(installed("MyanmarText-Bold").is_some());

        // ⚠️ NOT `assert_ne!` ANY MORE, and that is not a weakening. A bold
        // name resolves to the bold FILE when there is one and to the family's
        // regular file when there is not, so demanding the two differ would be
        // demanding a particular font be installed on whatever machine runs
        // this. Which file it lands on is asserted below, against what is
        // actually on disk.
        let bold = installed("MyanmarText-Bold").unwrap();
        assert_eq!(
            bold,
            if std::path::Path::new(r"C:\Windows\Fonts\mmrtextb.ttf").exists() {
                r"C:\Windows\Fonts\mmrtextb.ttf"
            } else {
                r"C:\Windows\Fonts\mmrtext.ttf"
            });
    }

    /// ⚠️ THE WAIT HAS TO BE VISIBLE. Preparing a document is about twenty
    /// seconds during which the app said nothing at all, and a reader can only
    /// read that as a hang. This checks the three things a bar on screen needs:
    /// that it is nothing before, that it moves during, and that it is nothing
    /// again afterwards.
    #[test]
    fn preparing_says_how_far_along_it_is() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }

        // ⚠️ THIS TEST DRIVES THE COUNTERS BY HAND, so it has to hold what a
        // real preparation holds. Without it another test's build reset the
        // total underneath this one and a bar driven to 100 read back as 79.
        let _only_one = progress::ONE_AT_A_TIME
            .lock()
            .unwrap_or_else(|held| held.into_inner());

        assert_eq!(progress::percent(), None, "something was already preparing");

        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let glyphs: BTreeSet<u16> = [3u16, 4, 5].into_iter().collect();

        let seen = std::sync::Mutex::new(Vec::<i32>::new());
        let report = |done: usize, total: usize| {
            if done == 0 { progress::began(total); } else { progress::reached(done); }
            if let Some(p) = progress::percent() {
                seen.lock().unwrap().push(p);
            }
        };
        let _ = crate::reshape::Index::build_reporting(
            &bytes, None, Some(&glyphs), Some(&report));
        progress::finished();

        let seen = seen.into_inner().unwrap();
        assert!(seen.len() > 2, "the bar would have jumped straight to the end");
        assert_eq!(seen[0], 0, "it did not start at nothing");
        assert_eq!(seen[seen.len() - 1], 100, "it never reached the end");

        // ⚠️ AND IT ONLY EVER GOES FORWARDS. A bar that goes backwards reads as
        // the work having been lost and started again.
        for pair in seen.windows(2) {
            assert!(pair[1] >= pair[0], "the bar went backwards: {pair:?}");
        }

        assert_eq!(progress::percent(), None, "the bar was left on screen");
    }

    /// ⚠️ TWO SUBSETS OF ONE FACE MUST SHARE ONE INDEX. A producer splits a
    /// document across several subsets and a page names each separately, so
    /// indexing by NAME paid the seconds twice over for the same font file, and
    /// neither half could read the other's syllables. Measured on a real
    /// three-page file: 53s and 71 of 80 lines by name, 22s and 80 of 80 by
    /// face.
    #[test]
    fn two_subsets_of_one_face_get_one_index_between_them() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        // A tiny scope, because this is about the sharing and not the contents.
        let glyphs: BTreeSet<u16> = [3u16, 4, 5].into_iter().collect();
        let wanted: BTreeMap<String, BTreeSet<u16>> = [
            ("BCDEEE+MyanmarText".to_string(), glyphs.clone()),
            ("BCDGEE+MyanmarText".to_string(), glyphs.clone()),
            // And the bold name, which falls back to the same family.
            ("BCDGEE+MyanmarText,Bold".to_string(), glyphs),
        ]
        .into_iter()
        .collect();

        let built = build_indexes(&wanted);
        let one = built.by_font.get("BCDEEE+MyanmarText").expect("no index");
        let two = built.by_font.get("BCDGEE+MyanmarText").expect("no index");
        assert!(Arc::ptr_eq(one, two), "two subsets of one face were indexed apart");

        // The bold name shares whatever file it resolved to, whichever that is.
        let bold = built.by_font.get("BCDGEE+MyanmarText,Bold").expect("no index");
        let shared = Arc::ptr_eq(bold, one);
        assert_eq!(
            shared,
            installed("MyanmarText-Bold") == installed("MyanmarText"),
            "the bold name did not follow the file it resolves to");
    }

    /// ⚠️ A COMMA IS AS GOOD AS A HYPHEN, and on a real file it is what the
    /// producer used: the user's page names its bold `ABCDEE+Pyidaungsu,Bold`.
    /// Splitting on the hyphen alone took that whole string as a family name,
    /// matched nothing, and refused all ten of the page's bold lines before it
    /// began. Measured after the fix: 9 of those 10 read.
    #[test]
    fn a_style_written_with_a_comma_is_still_a_style() {
        assert_eq!(installed("ABCDEE+Pyidaungsu,Bold"), installed("Pyidaungsu-Bold"));
        assert!(installed("ABCDEE+Pyidaungsu,Bold").is_some());

        // The family is what is left of the name, whichever way it was written.
        assert_eq!(installed("Pyidaungsu,Bold"), installed("Pyidaungsu-Bold"));

        // And a comma in a name that is not a style still does not match.
        assert_eq!(installed("Helvetica,Bold"), None);
    }

    /// ⚠️ THE FAMILY'S REGULAR FILE IS THE HONEST FALLBACK FOR A MISSING BOLD.
    /// A line is proven by shaping candidate text through the installed font
    /// and demanding the page's own glyph ids back, so a font that does not
    /// match yields a REFUSAL, never a misreading. The worst case of falling
    /// back is the refusal there already was; the measured case is 9 of 10 bold
    /// lines read on a machine with no bold Pyidaungsu at all.
    #[test]
    fn a_missing_bold_falls_back_to_the_family_it_belongs_to() {
        let regular = installed("Pyidaungsu").unwrap();
        let bold = installed("Pyidaungsu,Bold").unwrap();

        if std::path::Path::new(r"C:\Windows\Fonts\Pyidaungsu-Bold.ttf").exists() {
            assert_ne!(bold, regular, "a bold file is installed and was not used");
        } else {
            assert_eq!(bold, regular, "no bold file, so the regular one had to answer");
        }
    }

    // ---- what one page has to pay for ----

    /// A document of two pages, each drawing in a font of its own, each font
    /// declaring a width for exactly the CIDs it is given.
    fn two_pages_in_their_own_fonts(
        first: (&str, &[u16]), second: (&str, &[u16]),
    ) -> (Document, Vec<ObjectId>) {
        let mut doc = Document::with_version("1.7");
        let pages_id = doc.new_object_id();
        let mut pages = Vec::new();

        for (base, cids) in [first, second] {
            let widths: Vec<Object> = cids
                .iter()
                .flat_map(|c| [Object::Integer(*c as i64), vec![Object::Real(500.0)].into()])
                .collect();
            let descendant = doc.add_object(lopdf::dictionary! {
                "Type" => "Font",
                "Subtype" => "CIDFontType2",
                "BaseFont" => base,
                "W" => widths,
                "DW" => 500.0,
            });
            let font = doc.add_object(lopdf::dictionary! {
                "Type" => "Font",
                "Subtype" => "Type0",
                "BaseFont" => base,
                "Encoding" => "Identity-H",
                "DescendantFonts" => vec![descendant.into()],
            });
            let content = Content { operations: vec![
                Operation::new("BT", vec![]),
                Operation::new("Tf", vec!["F1".into(), 12.0.into()]),
                place(72.0, 700.0),
                show(cids),
                Operation::new("ET", vec![]),
            ] };
            let stream = doc.add_object(lopdf::Stream::new(
                lopdf::dictionary! {}, content.encode().unwrap()));
            pages.push(doc.add_object(lopdf::dictionary! {
                "Type" => "Page",
                "Parent" => pages_id,
                "Contents" => stream,
                "Resources" => lopdf::dictionary! {
                    "Font" => lopdf::dictionary! { "F1" => font } },
            }));
        }

        doc.objects.insert(pages_id, Object::Dictionary(lopdf::dictionary! {
            "Type" => "Pages",
            "Kids" => pages.iter().map(|p| Object::Reference(*p)).collect::<Vec<_>>(),
            "Count" => pages.len() as i64,
        }));
        let catalog = doc.add_object(lopdf::dictionary! {
            "Type" => "Catalog",
            "Pages" => pages_id,
        });
        doc.trailer.set("Root", catalog);
        (doc, pages)
    }

    /// ⚠️ THE WHOLE POINT OF THE PAGE-SCOPED PREPARATION. Preparing one page
    /// used to walk every page of the document, so opening a book of tens of
    /// thousands of pages paid for all of them to read the first. A page can
    /// only draw the glyphs it contains.
    #[test]
    fn a_page_asks_only_for_the_fonts_it_draws_with() {
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("AAAAAA+First", &[3, 4, 5]), ("BBBBBB+Second", &[7, 8]));

        let first = wanted_for_page(&doc, pages[0]).by_font;
        assert_eq!(first.keys().collect::<Vec<_>>(), vec!["AAAAAA+First"],
            "preparing one page asked for another page's font");
        assert_eq!(first["AAAAAA+First"], [3u16, 4, 5].into_iter().collect::<BTreeSet<_>>());

        let second = wanted_for_page(&doc, pages[1]).by_font;
        assert_eq!(second.keys().collect::<Vec<_>>(), vec!["BBBBBB+Second"]);
    }

    /// ⚠️ AND THE DOCUMENT-WIDE ANSWER IS STILL AVAILABLE, unchanged, for
    /// the one thing that needs it: a font whose own page could not identify
    /// it. Both pages' fonts must be in it or the widen would be narrower than
    /// the thing it is widening.
    #[test]
    fn one_font_can_still_be_asked_for_across_the_whole_document() {
        let (doc, _) = two_pages_in_their_own_fonts(
            ("AAAAAA+Same", &[3, 4, 5]), ("AAAAAA+Same", &[9]));

        assert_eq!(
            wanted_for_font(&doc, "AAAAAA+Same"),
            [3u16, 4, 5, 9].into_iter().collect::<BTreeSet<_>>(),
            "the widen missed a page");
    }

    /// ⚠️ AND IT CANNOT DEPEND ON WHICH PAGE ASKED. `wanted_for_font` is a
    /// union over the document, so the evidence a font is identified from is
    /// the same evidence wherever a reader happens to open the book. Page order
    /// deciding which face a font resolves to would let two pages of one
    /// document read the same font as two different faces.
    #[test]
    fn the_widened_evidence_is_the_same_whichever_page_asks() {
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("AAAAAA+Same", &[3, 4, 5]), ("AAAAAA+Same", &[9]));

        let across = wanted_for_font(&doc, "AAAAAA+Same");

        // Genuinely wider than either page on its own, which is the only
        // reason it is worth asking for.
        for &page in &pages {
            let own = wanted_for_page(&doc, page).by_font;
            assert!(own["AAAAAA+Same"].len() < across.len(),
                "a page's own evidence was not narrower than the document's");
            assert!(own["AAAAAA+Same"].iter().all(|g| across.contains(g)));
        }
    }

    /// One page's reading, already paid for.
    ///
    /// ⚠️ SCOPED BY THE CHARACTERS, NOT BY THE GLYPHS, and that is the only
    /// reason these tests are affordable. Enumerating what a Burmese glyph set
    /// can spell is six minutes of a debug build, and what the bookkeeping does
    /// with an index it already has does not depend on how wide that index is:
    /// these ask which faces are BUILT and which are REUSED, so an index built
    /// over one character answers exactly as well as one built over a font.
    fn already_paid_for(covers: &[u16]) -> Indexes {
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let chars: BTreeSet<char> = "\u{1019}".chars().collect();
        let index = crate::reshape::Index::build(&bytes, Some(&chars), None).unwrap();
        Indexes {
            by_font: [("BCDEEE+MyanmarText".to_string(), Arc::new((bytes, index)))]
                .into_iter()
                .collect(),
            paths: [("BCDEEE+MyanmarText".to_string(), MYANMAR_TEXT)].into_iter().collect(),
            covered: [(MYANMAR_TEXT, Covers::Only(covers.iter().copied().collect()))]
                .into_iter()
                .collect(),
            generation: 1,
        }
    }

    /// Leaves a document's fonts with nothing for a page to scope them by.
    ///
    /// ⚠️ NOT BY EMPTYING `/W`, WHICH IS NOT THE SAME THING. A Type0 font
    /// with no width array still answers `cid_widths`, out of `/DW`, and so
    /// still counts as having declared its extent. What produces a font nothing
    /// can bound is one the reader cannot read a CID width out of at all.
    fn nothing_to_scope_the_fonts_by(doc: &mut Document) {
        let ids: Vec<ObjectId> = doc.objects.keys().copied().collect();
        for id in ids {
            if let Some(d) = doc.get_object_mut(id).ok().and_then(|o| o.as_dict_mut().ok()) {
                d.remove(b"DescendantFonts");
            }
        }
    }

    /// ⚠️ AND A FONT THAT DECLARES NOTHING IS NOT BOUNDED BY ONE PAGE. Page
    /// scoping is free because a subset's `/W` is already document-wide: it
    /// lists every CID the whole file uses, so the second page finds itself
    /// covered. A font without one leaves only what is drawn HERE, which the
    /// next page exceeds, and a face rebuilt per page is twenty seconds per
    /// page. Such fonts are named so the build can widen exactly those.
    #[test]
    fn a_font_that_declares_no_widths_is_not_taken_at_one_pages_word() {
        let (mut doc, pages) = two_pages_in_their_own_fonts(
            ("AAAAAA+Same", &[3, 4]), ("AAAAAA+Same", &[9]));
        nothing_to_scope_the_fonts_by(&mut doc);

        let first = wanted_for_page(&doc, pages[0]);
        assert_eq!(first.by_font["AAAAAA+Same"], [3u16, 4].into_iter().collect::<BTreeSet<_>>(),
            "the page did not fall back to what it draws");
        assert!(first.unbounded.contains("AAAAAA+Same"),
            "a font with nothing to scope by was taken at one page's word");

        // And what the build widens such a font to is the whole document.
        assert_eq!(wanted_for_font(&doc, "AAAAAA+Same"),
            [3u16, 4, 9].into_iter().collect::<BTreeSet<_>>());

        // A font that DOES declare its widths is not widened, because it does
        // not need to be: its own declaration already covers every page.
        let (declared, pages) = two_pages_in_their_own_fonts(
            ("AAAAAA+Same", &[3, 4]), ("AAAAAA+Same", &[9]));
        assert!(wanted_for_page(&declared, pages[0]).unbounded.is_empty(),
            "a font that declares its widths was walked for anyway");
    }

    /// ⚠️ AND ONCE A FONT HAS A FACE IT IS NEVER ASKED AGAIN. Identity is
    /// statistical: `face_of` picks whichever face names the largest share of a
    /// glyph set, and a page is a much smaller sample than a document. Deciding
    /// it afresh on each page is how one font becomes two faces in one book.
    #[test]
    fn a_font_that_has_a_face_keeps_it_however_many_pages_ask() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("BCDEEE+MyanmarText", &[3]), ("BCDEEE+MyanmarText", &[4]));

        // Resolved to something no name lookup would have chosen, so that a
        // second opinion would be visible if one were taken.
        let mut indexes = already_paid_for(&[3, 4]);
        indexes.paths.insert("BCDEEE+MyanmarText".to_string(), r"C:\Windows\Fonts\NIRMALA.TTF");

        indexes.grow(&doc, &wanted_for_page(&doc, pages[1]));
        assert_eq!(indexes.path_for("BCDEEE+MyanmarText"), Some(r"C:\Windows\Fonts\NIRMALA.TTF"),
            "a second page re-decided which face a font is");
    }

    /// ⚠️ A FACE ALREADY WIDE ENOUGH IS NOT BUILT AGAIN, which is what makes
    /// the second page of a book free. Seventeen seconds is paid by whichever
    /// page needs it first and by nobody after.
    #[test]
    fn a_page_covered_by_what_is_already_built_costs_nothing() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("BCDEEE+MyanmarText", &[3, 4, 5]), ("BCDEEE+MyanmarText", &[3, 4]));

        let mut indexes = already_paid_for(&[3, 4, 5]);
        let built = indexes.generation();
        let face = Arc::clone(&indexes.by_font["BCDEEE+MyanmarText"]);

        // The second page draws a SUBSET of the first, which is the ordinary
        // case: a subset's /W declares what the whole document uses of it.
        assert!(!indexes.grow(&doc, &wanted_for_page(&doc, pages[1])),
            "a page already covered rebuilt the index");
        assert_eq!(indexes.generation(), built,
            "a reading that changed nothing was announced as an improvement");
        assert!(Arc::ptr_eq(&face, &indexes.by_font["BCDEEE+MyanmarText"]),
            "the face was replaced by an identical one");

        // And the page that paid for it is covered too, which is what stops the
        // document rebuilding the same face for ever.
        assert!(!indexes.grow(&doc, &wanted_for_page(&doc, pages[0])),
            "the page that built the face was not covered by it");
    }

    /// ⚠️ AND A PAGE THAT NEEDS MORE GETS MORE, AND SAYS SO. The generation
    /// is what tells a page read earlier that a better reading is available.
    ///
    /// Asked of a Devanagari face, whose index is built from the font's own
    /// tables in about fifty milliseconds.
    #[test]
    fn a_page_that_needs_more_grows_the_face_and_the_reading() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(NIRMALA).exists() {
            return;
        }
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("NirmalaUI", &[3, 4]), ("NirmalaUI", &[3, 4, 5, 6]));

        // As if the first page had been read for and nothing else had.
        let mut indexes = Indexes {
            paths: [("NirmalaUI".to_string(), NIRMALA)].into_iter().collect(),
            covered: [(NIRMALA, Covers::Only([3u16, 4].into_iter().collect()))]
                .into_iter()
                .collect(),
            generation: 1,
            ..Default::default()
        };
        let built = indexes.generation();

        assert!(indexes.grow(&doc, &wanted_for_page(&doc, pages[1])),
            "a page asking for glyphs nothing covered was refused");
        assert!(indexes.generation() > built, "the better reading was not announced");
        assert!(indexes.by_font.contains_key("NirmalaUI"), "the face was not built");
    }

    /// ⚠️ AND A SECOND NAME FOR A FACE ALREADY BUILT MUST BE HANDED IT. Two
    /// subsets of one font are one file and share one index, so the second name
    /// met asks for glyphs the face already covers and rightly builds nothing.
    /// Left there it would have a resolved face and no index, which reads as a
    /// font that cannot be read at all.
    #[test]
    fn a_new_name_for_a_face_already_built_shares_its_index() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("BCDEEE+MyanmarText", &[3, 4, 5]), ("BCDGEE+MyanmarText", &[3, 4]));

        let mut indexes = already_paid_for(&[3, 4, 5]);
        assert!(indexes.grow(&doc, &wanted_for_page(&doc, pages[1])),
            "a name with no index was left without one");

        let one = indexes.by_font.get("BCDEEE+MyanmarText").expect("no index");
        let two = indexes.by_font.get("BCDGEE+MyanmarText")
            .expect("the second subset was left with no index");
        assert!(Arc::ptr_eq(one, two), "two subsets of one face were indexed apart");
    }

    /// ⚠️ AND A DEVANAGARI FACE IS BUILT ONCE, FULL STOP. Its index is
    /// filled from the font's own tables and is never given a glyph set, so
    /// recording it as covering only the page that built it would rebuild it,
    /// and announce a new reading, on every page of a Hindi book after the
    /// first.
    #[test]
    fn a_devanagari_face_is_never_rebuilt_for_a_wider_page() {
        const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
        if !std::path::Path::new(NIRMALA).exists() {
            return;
        }
        let (doc, pages) = two_pages_in_their_own_fonts(
            ("NirmalaUI", &[3, 4]), ("NirmalaUI", &[500, 501]));

        let mut indexes = Indexes::default();
        assert!(indexes.grow(&doc, &wanted_for_page(&doc, pages[0])),
            "the Devanagari face did not build");
        let built = indexes.generation();

        assert!(!indexes.grow(&doc, &wanted_for_page(&doc, pages[1])),
            "a Devanagari face was rebuilt for glyphs its index already reads");
        assert_eq!(indexes.generation(), built);
    }

    /// A page drawing exactly `text`, and what recovery makes of it.
    ///
    /// Every glyph is declared half an em wide, so at 12 point each advances
    /// exactly 6 points and the arithmetic in these tests is checkable by hand.
    fn a_page_saying(text: &str) -> (String, Vec<Cluster>) {
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        let glyphs = crate::reshape::draws(&face, text);

        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&glyphs),
            Operation::new("ET", vec![]),
        ], 500.0);

        let chars: BTreeSet<char> = text.chars().collect();
        let index = crate::reshape::Index::build(&bytes, Some(&chars), None).unwrap();
        let indexes = Indexes {
            by_font: [("BCDEEE+MyanmarText".to_string(), std::sync::Arc::new((bytes, index)))]
                .into_iter()
                .collect(),
            paths: BTreeMap::new(),
        ..Default::default()
        };
        let read = read_page_with(&doc, page, &indexes);
        let one = read.into_iter().next().expect("no line");
        (one.text.expect("nothing read"), one.clusters)
    }

    /// ⚠️ THE DECISION THIS WHOLE PHASE RESTS ON. `မြ` is ONE cluster covering
    /// BOTH characters, because the medial is drawn before the consonant it
    /// follows and there is no position on the page between them. A caret that
    /// could stand there would be standing somewhere the page does not have.
    #[test]
    fn a_reordered_pair_is_one_cluster_and_not_two() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        // "မြန်မာ": ma + medial ra, na + asat, ma + aa. Three characters pairs,
        // three clusters, six glyphs, and every character is three bytes.
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let (text, clusters) = a_page_saying(WORD);

        assert_eq!(text, WORD);
        assert_eq!(clusters.len(), 3, "{clusters:?}");

        assert_eq!(clusters[0].from, 0);
        assert_eq!(clusters[0].to, 6, "the medial was split off its consonant");
        assert_eq!(clusters[1].from, 6);
        assert_eq!(clusters[1].to, 12);
        assert_eq!(clusters[2].from, 12);
        assert_eq!(clusters[2].to, 18);
    }

    /// ⚠️ THE TWO MEASUREMENTS MUST NOT DISAGREE ABOUT WHERE A PLACEMENT ENDS.
    /// `clusters_of` re-shapes a placement's reading to find its cluster edges,
    /// so it can only measure what it could read. `advance_of` adds up the
    /// file's own declared widths and reads nothing. Reflow slides placements by
    /// the second while the app frames them from the first, so a page where they
    /// differ is a page where a word lands somewhere other than its box.
    ///
    ///     cargo test --release the_two_ways_of_measuring_a_placement -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn the_two_ways_of_measuring_a_placement() {
        for (what, file) in [
            ("HINDI", r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf"),
            ("MYANMAR", r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf"),
        ] {
            println!("\n================ {what}");
            if !std::path::Path::new(file).exists() {
                println!("not on this machine");
                continue;
            }
            let doc = Document::load(file).unwrap();
            let page = *doc.get_pages().values().next().unwrap();
            let indexes = indexes_for_document(&doc);
            let lines = lines_of(&doc, page);
            let readings = read_page_with(&doc, page, &indexes);
            let fonts = fonts_of(&doc, page);

            let (mut both, mut worst, mut only_mine, mut unmeasurable) = (0usize, 0.0f64, 0usize, 0usize);
            for (line, reading) in lines.iter().zip(&readings) {
                let Some((_, Some(widths))) = fonts.get(&line.resource) else {
                    unmeasurable += 1;
                    continue;
                };
                // ⚠️ AN ADVANCE IS IN TEXT SPACE AND A CLUSTER IS ON THE PAGE.
                // Comparing them raw disagreed by up to 320 points on the Hindi
                // book, all of it the `cm [0.75 0 0 -0.75 0 841.92]` it draws
                // under. `advance_of` is in the frame `along_baseline` expects,
                // which is the frame reflow already moves things in.
                let mine = advance_of(line, widths);
                assert!(mine.is_finite(), "a placement could not be measured");
                let Some(last) = reading.clusters.last() else {
                    // The whole point: an unread placement still has an extent.
                    only_mine += 1;
                    continue;
                };
                let off = (line.ctm.along(mine, 0.0).0 - (last.right - reading.x)).abs();
                if off > worst {
                    worst = off;
                    println!("   worst so far {off:.3} pt on {:?}",
                        reading.text.as_deref().unwrap_or("").chars().take(20).collect::<String>());
                }
                both += 1;
            }
            println!("{both} placements measured both ways, worst disagreement {worst:.3} pt");
            println!("{only_mine} placements nothing could read, all still measured");
            println!("{unmeasurable} placements in a font with no declared widths");
            assert!(worst < 0.5,
                "the two measurements disagree by {worst:.3} points, so a placement \
                 moved by one will not land where the other draws its box");
        }
    }

    /// ⚠️ WHERE A LINE CAN BE BROKEN WITHOUT READING IT. Carrying a word to
    /// the next line means moving whole placements, so a line can only reflow
    /// at the boundaries between the placements it is drawn in. This says how
    /// many boundaries each real line actually offers.
    ///
    ///     cargo test --release where_a_real_line_can_be_broken -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn where_a_real_line_can_be_broken() {
        for (what, file) in [
            ("HINDI", r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf"),
            ("MYANMAR", r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf"),
        ] {
            println!("\n================ {what}");
            if !std::path::Path::new(file).exists() {
                println!("not on this machine");
                continue;
            }
            let doc = Document::load(file).unwrap();
            let page = *doc.get_pages().values().next().unwrap();
            let lines = lines_of(&doc, page);
            let fonts = fonts_of(&doc, page);
            let indexes = indexes_for_document(&doc);
            let readings = read_page_with(&doc, page, &indexes);

            // Every placement, gathered onto the baseline it is drawn on.
            let mut rows: Vec<(f64, Vec<(f64, f64, String, String)>)> = Vec::new();
            for (line, reading) in lines.iter().zip(&readings) {
                let Some((_, Some(w))) = fonts.get(&line.resource) else { continue };
                let left = line.page_x();
                let span = (
                    left,
                    left + line.ctm.along(advance_of(line, w), 0.0).0,
                    line.base_font.clone(),
                    reading.text.clone().unwrap_or_else(|| "?".into()),
                );
                match rows.iter_mut().find(|(y, _)| (y - line.page_y).abs() < 0.5) {
                    Some((_, on_it)) => on_it.push(span),
                    None => rows.push((line.page_y, vec![span])),
                }
            }
            for (_, on_it) in rows.iter_mut() {
                on_it.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap());
            }

            let alone = rows.iter().filter(|(_, on_it)| on_it.len() == 1).count();
            println!("{} visual lines, {alone} of them drawn as a single placement",
                rows.len());
            let widest = rows.iter().max_by_key(|(_, on_it)| on_it.len()).unwrap();
            println!("the most divided line is at y {:.2}, {} placements:",
                widest.0, widest.1.len());
            for (n, (left, right, font, text)) in widest.1.iter().enumerate() {
                println!("   {n:3}  x {left:7.2} .. {right:7.2}  {:>6.2} wide  {font:14} {:?}",
                    right - left, text.chars().take(16).collect::<String>());
            }
        }
    }

    /// Two glyphs at half an em each, at 12 point, from x=72.
    #[test]
    fn a_cluster_is_as_wide_as_the_glyphs_that_draw_it() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let (_, clusters) = a_page_saying(WORD);

        for (n, want) in [(0usize, (72.0, 84.0)), (1, (84.0, 96.0)), (2, (96.0, 108.0))] {
            assert!((clusters[n].left - want.0).abs() < 0.01
                && (clusters[n].right - want.1).abs() < 0.01,
                "cluster {n} is {:?}, expected {want:?}", clusters[n]);
        }
    }

    /// ⚠️ NO GAPS AND NO OVERLAPS, or a caret offset would fall into a crack
    /// between two clusters and there would be no answer for where it goes.
    #[test]
    fn the_clusters_cover_the_whole_reading_in_order() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const LINE: &str = "အရုဏ်ဦး ရောင်နီသည်";
        let (text, clusters) = a_page_saying(LINE);

        assert!(!clusters.is_empty());
        assert_eq!(clusters[0].from, 0, "the reading does not start at the first cluster");
        assert_eq!(clusters.last().unwrap().to, text.len(),
            "the reading is longer than the clusters that place it");

        for pair in clusters.windows(2) {
            assert_eq!(pair[0].to, pair[1].from, "a gap or an overlap: {pair:?}");
            assert!(pair[1].left >= pair[0].left - 0.01,
                "the clusters are not laid left to right: {pair:?}");
        }
        // And every cluster names bytes that really are a character boundary.
        for c in &clusters {
            assert!(text.is_char_boundary(c.from) && text.is_char_boundary(c.to),
                "cluster {c:?} cuts a character in half of {text:?}");
        }
    }

    /// A space that no glyph draws still has to be somewhere, or the caret
    /// cannot be put between the two words it separates.
    #[test]
    fn a_space_between_placements_gets_a_box_of_its_own() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        const LEFT: &str = "မြန်မာ";
        const RIGHT: &str = "မြန်မာ";
        let left = crate::reshape::draws(&face, LEFT);
        let right = crate::reshape::draws(&face, RIGHT);

        // The first piece ends at 72 + 6 glyphs * 6pt = 108; the second starts
        // six points further on.
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            pick(12.0),
            place(72.0, 700.0),
            show(&left),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            pick(12.0),
            place(114.0, 700.0),
            show(&right),
            Operation::new("ET", vec![]),
        ], 500.0);

        let chars: BTreeSet<char> = LEFT.chars().collect();
        let index = crate::reshape::Index::build(&bytes, Some(&chars), None).unwrap();
        let indexes = Indexes {
            by_font: [("BCDEEE+MyanmarText".to_string(), std::sync::Arc::new((bytes, index)))]
                .into_iter()
                .collect(),
            paths: BTreeMap::new(),
        ..Default::default()
        };
        let one = read_page_with(&doc, page, &indexes).into_iter().next().unwrap();
        let text = one.text.expect("nothing read");
        assert_eq!(text, format!("{LEFT} {RIGHT}"));

        let space = one.clusters.iter()
            .find(|c| &text[c.from..c.to] == " ")
            .expect("the space has no box");
        assert!((space.left - 108.0).abs() < 0.01 && (space.right - 114.0).abs() < 0.01,
            "the space is at {space:?}, not the six points that were left for it");
    }

    /// ⚠️ AN UNKNOWN FONT IS REFUSED, NOT SUBSTITUTED. Reading a page with the
    /// wrong font produces glyphs that do not match, so the reading fails; but
    /// a family that merely LOOKS similar could match often enough to be
    /// believed, which is worse than refusing.
    #[test]
    fn a_font_that_was_not_measured_is_refused() {
        assert_eq!(installed("ABCDEF+Zawgyi-One"), None);
        assert_eq!(installed("Arial"), None);
        assert_eq!(installed(""), None);
    }

    #[test]
    fn a_matrix_composes_the_way_a_page_moves_a_line() {
        // Down 12 from 700 is 688, whether the page says so with Tm or by
        // stepping with Td.
        let placed = Matrix([1.0, 0.0, 0.0, 1.0, 72.0, 700.0]);
        let stepped = Matrix::translation(0.0, -12.0).then(placed);
        assert!((stepped.y() - 688.0).abs() < 0.001, "{:?}", stepped);
        assert!((stepped.x() - 72.0).abs() < 0.001, "{:?}", stepped);

        // And a horizontal step does not move the line.
        let along = Matrix::translation(40.0, 0.0).then(placed);
        assert!((along.y() - 700.0).abs() < 0.001);
        assert!((along.x() - 112.0).abs() < 0.001);
    }
}
