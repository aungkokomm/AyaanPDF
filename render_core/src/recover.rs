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
    let (family, bold) = match name.split_once('-') {
        Some((f, style)) => (f, style.eq_ignore_ascii_case("bold")),
        None => (name, false),
    };
    match (family, bold) {
        ("MyanmarText", false) => Some(r"C:\Windows\Fonts\mmrtext.ttf"),
        ("MyanmarText", true) => Some(r"C:\Windows\Fonts\mmrtextb.ttf"),
        ("Pyidaungsu", false) => Some(r"C:\Windows\Fonts\Pyidaungsu.ttf"),
        ("Pyidaungsu", true) => Some(r"C:\Windows\Fonts\Pyidaungsu-Bold.ttf"),
        _ => None,
    }
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
    /// Which operations of the page's content stream drew this line, in order.
    ///
    /// ⚠️ WHAT A REWRITER NEEDS AND A READER DOES NOT. A line is drawn by
    /// several separately placed runs, so replacing its text means knowing
    /// every operation that contributed a glyph, not just where the line sits.
    pub(crate) drawn_by: Vec<usize>,
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

    let mut out: Vec<Line> = Vec::new();
    for line in lines {
        let joins = out.last().is_some_and(|prev| {
            let apart = (prev.y - line.y).abs();
            installed(&prev.base_font) == installed(&line.base_font)
                && line.x >= prev.x
                && (apart < SAME_BASELINE
                    || (apart < A_MARK * line.size && draws_only_marks(&line)))
        });
        if !joins {
            out.push(line);
            continue;
        }
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
    out
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
        let space_before = !out.is_empty()
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
pub(crate) struct Indexes {
    by_font: BTreeMap<String, (Vec<u8>, crate::reshape::Index)>,
}

impl Indexes {
    /// Whether anything on the page can be read at all.
    pub(crate) fn is_empty(&self) -> bool {
        self.by_font.is_empty()
    }

    /// What this font draws, if it is one that can be read.
    pub(crate) fn index_for(&self, base_font: &str) -> Option<&crate::reshape::Index> {
        self.by_font.get(base_font).map(|(_, index)| index)
    }
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

    let mut by_font = BTreeMap::new();
    for (base_font, glyphs) in &wanted {
        let Some(path) = installed(base_font) else { continue };
        let Ok(bytes) = std::fs::read(path) else { continue };
        let Some(index) = crate::reshape::Index::build(&bytes, None, Some(glyphs)) else {
            continue;
        };
        by_font.insert(base_font.clone(), (bytes, index));
    }
    Indexes { by_font }
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
    lines_of(doc, page).iter().any(|l| can_read(&l.base_font))
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
        .filter_map(|(name, (bytes, index))| {
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
            Reading {
                y: line.y,
                x: line.x,
                size: line.size,
                top: line.y + top,
                bottom: line.y + bottom,
                font: line.base_font.clone(),
                text,
                clusters,
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
    lines.iter().filter(move |l| (l.y - baseline).abs() < NEAR)
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
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            nudges: Vec::new(),
            glyphs: left.iter().copied().chain(right.iter().copied()).collect(),
            adjust: 0.0,
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
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            nudges: Vec::new(),
            glyphs: drawn,
            adjust: 0.0,
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
                .and_then(|(bytes, _)| rustybuzz::Face::from_slice(bytes, 0));
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

    #[test]
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
                if let Some((bytes, index)) = indexes.by_font.get(&line.base_font) {
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

    fn an_index_of(bytes: &[u8], text: &str) -> Indexes {
        let chars: BTreeSet<char> = text.chars().collect();
        let index = crate::reshape::Index::build(bytes, Some(&chars), None).unwrap();
        Indexes {
            by_font: [("BCDEEE+MyanmarText".to_string(), (bytes.to_vec(), index))]
                .into_iter()
                .collect(),
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
            let Some((bytes, index)) = indexes.by_font.get(&line.base_font) else { continue };
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
        let candidates: Vec<(&str, String)> = vec![
            ("system Pyidaungsu.ttf", r"C:\Windows\Fonts\Pyidaungsu.ttf".to_string()),
            ("per-user 2.5.3 Regular",
             format!(r"{local}\Microsoft\Windows\Fonts\Pyidaungsu-2.5.3_Regular.ttf")),
            ("per-user 2.5.3 Bold",
             format!(r"{local}\Microsoft\Windows\Fonts\Pyidaungsu-2.5.3_Bold.ttf")),
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

        for (label, path) in &candidates {
            if !std::path::Path::new(path).exists() {
                println!("\n{label}: not on this machine");
                continue;
            }
            let bytes = std::fs::read(path).unwrap();
            println!("\n{label} ({} bytes)", bytes.len());

            for (font, glyphs) in &wanted {
                let started = std::time::Instant::now();
                let Some(index) = crate::reshape::Index::build(&bytes, None, Some(glyphs)) else {
                    println!("   {font}: no index (the font draws none of these glyphs)");
                    continue;
                };
                let mut indexes = Indexes { by_font: BTreeMap::new() };
                indexes.by_font.insert(font.clone(), (bytes.clone(), index));

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

    fn a_subset_tag_is_not_part_of_the_font_s_name() {
        assert_eq!(installed("BCDEEE+MyanmarText"), installed("MyanmarText"));
        assert!(installed("BCDEEE+MyanmarText").is_some());
        assert!(installed("MyanmarText-Bold").is_some());
        assert_ne!(installed("MyanmarText-Bold"), installed("MyanmarText"));
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
            by_font: [("BCDEEE+MyanmarText".to_string(), (bytes, index))]
                .into_iter()
                .collect(),
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
            by_font: [("BCDEEE+MyanmarText".to_string(), (bytes, index))]
                .into_iter()
                .collect(),
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
