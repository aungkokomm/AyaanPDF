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
//! ⚠️ NOTHING CALLS THIS YET. What is missing is not here: the core refuses
//! every shaped line before it gets this far, and a caret needs a position for
//! each CHARACTER, which a line's reading does not carry. Both are the next
//! step, and neither changes what this module answers.
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
fn installed(base_font: &str) -> Option<&'static str> {
    // "BCDEEE+MyanmarText" is one font, wearing a subset tag.
    let name = base_font.rsplit('+').next().unwrap_or(base_font);
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
    pub(crate) y: f64,
    pub(crate) x: f64,
    /// The font resource the line is drawn with, and the name behind it.
    pub(crate) resource: Vec<u8>,
    pub(crate) base_font: String,
    pub(crate) size: f64,
    pub(crate) glyphs: Vec<u16>,
    /// Everything the line's own `TJ` numbers add to its advance, in glyph
    /// space. A negative number opens space and a positive one closes it, and
    /// both count: the total is how far the pen really travelled.
    pub(crate) adjust: f64,
    /// Glyph indices with a word space in front of them.
    ///
    /// ⚠️ FOUND BY GEOMETRY, NOT BY GLYPH. A space between two separately
    /// placed runs is drawn by placing the second one further along, so there
    /// is no space glyph to read. The gaps INSIDE a run are a different thing
    /// and are not breaks: a justified line stretches the spaces it already
    /// has, so reading those as spaces splits words the author typed as one.
    pub(crate) breaks: Vec<usize>,
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
fn fonts_of(doc: &Document, page: ObjectId) -> BTreeMap<Vec<u8>, (String, Option<crate::shaped::CidWidths>)> {
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
    let mut leading = 0.0f64;
    let mut glyphs: Vec<u16> = Vec::new();
    let mut adjust = 0.0f64;

    macro_rules! finish {
        () => {
            if !glyphs.is_empty() {
                out.push(Line {
                    y: line_matrix.y(),
                    x: line_matrix.x(),
                    resource: resource.clone(),
                    base_font: names
                        .get(&resource)
                        .map(|(b, _)| b.clone())
                        .unwrap_or_default(),
                    size,
                    glyphs: std::mem::take(&mut glyphs),
                    adjust: std::mem::replace(&mut adjust, 0.0),
                    breaks: Vec::new(),
                });
            } else {
                adjust = 0.0;
            }
        };
    }

    for op in &content.operations {
        match op.operator.as_str() {
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
                // ⚠️ A HORIZONTAL Td IS THE SAME LINE. A producer uses one to
                // step along a line it is drawing in pieces, and treating each
                // piece as its own line cuts words, and syllables, in half.
                if ty.abs() > 0.01 {
                    finish!();
                }
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

    // Where a run ENDS: where it was placed, plus everything it advanced by.
    let ends_at = |line: &Line| -> Option<f64> {
        let widths = fonts.get(&line.resource).and_then(|(_, w)| w.as_ref())?;
        let drawn: f64 = line.glyphs.iter().map(|g| widths.of(*g)).sum();
        Some(line.x + (drawn + line.adjust) / 1000.0 * line.size)
    };

    let mut out: Vec<Line> = Vec::new();
    for line in lines {
        let joins = out.last().is_some_and(|prev| {
            (prev.y - line.y).abs() < SAME_BASELINE
                && installed(&prev.base_font) == installed(&line.base_font)
                && line.x >= prev.x
        });
        if !joins {
            out.push(line);
            continue;
        }
        let gap = ends_at(out.last().unwrap()).map(|end| line.x - end);
        let prev = out.last_mut().unwrap();
        let at = prev.glyphs.len();
        if gap.is_some_and(|g| g >= A_SPACE * line.size) {
            prev.breaks.push(at);
        }
        prev.glyphs.extend(&line.glyphs);
        prev.breaks.extend(line.breaks.iter().map(|i| i + at));
        prev.adjust += line.adjust;
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
    // ⚠️ AND IF NO SPLIT WORKS, THE LINE IS READ WHOLE. Losing a space is worth
    // far less than losing the line, and a reading with a word space missing is
    // still the author's text.
    split_at_the_spaces(index, face, line)
        .or_else(|| crate::reshape::prove(face, index, &line.glyphs))
}

fn split_at_the_spaces(index: &crate::reshape::Index, face: &rustybuzz::Face, line: &Line)
    -> Option<String>
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
        .copied()
        .chain(std::iter::once(line.glyphs.len()))
        .collect();

    let mut out = String::new();
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
            if let Some(piece) = crate::reshape::prove(face, index, &line.glyphs[from..cut]) {
                taken = Some((cut, piece));
                break;
            }
        }
        let (cut, piece) = taken?;
        if !out.is_empty() && !out.ends_with(' ') && !piece.starts_with(' ') {
            out.push(' ');
        }
        out.push_str(&piece);
        from = cut;
    }
    (!out.is_empty()).then_some(out)
}

/// Everything a page says that can be PROVEN, line by line.
///
/// A line reads as `None` when nothing reproduced its glyphs: an unknown font,
/// a character outside the enumeration, or a producer doing something the
/// index does not model. A refusal is the correct answer there.
pub(crate) fn read_page(doc: &Document, page: ObjectId) -> Vec<(f64, Option<String>)> {
    let lines = lines_of(doc, page);

    // One index per font, built over only the characters and glyphs that font
    // actually draws on this page.
    let mut wanted: BTreeMap<String, BTreeSet<u16>> = BTreeMap::new();
    for line in &lines {
        wanted.entry(line.base_font.clone()).or_default().extend(&line.glyphs);
    }

    let mut faces: BTreeMap<String, (Vec<u8>, crate::reshape::Index)> = BTreeMap::new();
    for (base_font, glyphs) in &wanted {
        let Some(path) = installed(base_font) else { continue };
        let Ok(bytes) = std::fs::read(path) else { continue };
        let Some(index) = crate::reshape::Index::build(&bytes, None, Some(glyphs)) else {
            continue;
        };
        faces.insert(base_font.clone(), (bytes, index));
    }

    lines
        .iter()
        .map(|line| {
            let said = faces.get(&line.base_font).and_then(|(bytes, index)| {
                let face = rustybuzz::Face::from_slice(bytes, 0)?;
                read_line(index, &face, line)
            });
            (line.y, said)
        })
        .collect()
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
        assert_eq!(lines[0].breaks, vec![3], "the gap was not read as a space");
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
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            glyphs: left.iter().copied().chain(right.iter().copied()).collect(),
            adjust: 0.0,
            breaks: vec![left.len()],
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
            resource: b"F1".to_vec(),
            base_font: "BCDEEE+MyanmarText".into(),
            size: 12.0,
            glyphs: drawn,
            adjust: 0.0,
            // Straight through the middle of the first cluster.
            breaks: vec![1],
        };
        assert_eq!(read_line(&index, &face, &line).as_deref(), Some(WORD),
            "an impossible break took the line down with it");
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
        let proven = read.iter().filter(|(_, said)| said.is_some()).count();
        println!("{proven} of {} lines, in {:?}", read.len(), started.elapsed());
        for (n, (y, said)) in read.iter().enumerate() {
            match said {
                Some(text) => println!("  {n:>2} y={y:7.1}  {text}"),
                None => println!("  {n:>2} y={y:7.1}  REFUSED"),
            }
        }
        assert!(proven * 2 > read.len(), "read {proven} of {} lines", read.len());
    }

    #[test]
    fn a_subset_tag_is_not_part_of_the_font_s_name() {
        assert_eq!(installed("BCDEEE+MyanmarText"), installed("MyanmarText"));
        assert!(installed("BCDEEE+MyanmarText").is_some());
        assert!(installed("MyanmarText-Bold").is_some());
        assert_ne!(installed("MyanmarText-Bold"), installed("MyanmarText"));
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
