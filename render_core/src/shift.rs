//! Moving text where it stands, without changing a word of it.
//!
//! ⚠️ MEASURED BEFORE ANY OF THIS WAS WRITTEN. Across three real files, a
//! Word-produced Burmese page, a Pyidaungsu page and a page of a commercially
//! produced book, EVERY text object begins with an absolute `Tm`: 151 of 151,
//! 333 of 333 and 32 of 32, with not one placed only relative to the object
//! before it. That is what makes moving text a matter of changing two numbers
//! rather than of wrapping operations in a transform, which the PDF spec does
//! not allow inside a text object anyway.
//!
//! ⚠️ AND A TRANSFORM CAN BE IN FORCE OVER THE TEXT. The book's page draws all
//! 32 of its text objects under `cm [0.72 0 0 -0.72 72 841.9]`, which is a
//! scale AND A FLIP OF THE Y AXIS. A move of ten points down the page is not a
//! move of ten points in the numbers a `Tm` carries, and on that page it is not
//! even in the same direction. Every displacement here is therefore turned
//! through the inverse of whatever is in force where the text is drawn.

use std::collections::BTreeMap;

use lopdf::content::{Content, Operation};
use lopdf::{Document, Object, ObjectId};

use crate::{STATUS_DOC_NOT_REWRITABLE, STATUS_INVALID_INPUT, STATUS_LINE_NOT_REWRITABLE};

/// The linear part of a PDF matrix: the four numbers that scale, rotate and
/// skew, without the two that translate.
///
/// A displacement is a direction and a distance, not a place, so the
/// translation must not be applied to it. Turning (0, 10) through a matrix
/// including its translation would move the text to the corner of the page.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct Linear {
    pub(crate) a: f64,
    pub(crate) b: f64,
    pub(crate) c: f64,
    pub(crate) d: f64,
}

impl Linear {
    pub(crate) const IDENTITY: Linear = Linear { a: 1.0, b: 0.0, c: 0.0, d: 1.0 };

    /// `self` applied first, then `then`.
    fn then(self, then: Linear) -> Linear {
        Linear {
            a: self.a * then.a + self.b * then.c,
            b: self.a * then.b + self.b * then.d,
            c: self.c * then.a + self.d * then.c,
            d: self.c * then.b + self.d * then.d,
        }
    }

    /// Where a displacement in the space this maps INTO has to be, to come out
    /// of it as `(x, y)`. None when the matrix flattens everything onto a line
    /// and there is no answer.
    pub(crate) fn undo(self, x: f64, y: f64) -> Option<(f64, f64)> {
        let det = self.a * self.d - self.b * self.c;
        if det.abs() < 1e-12 {
            return None;
        }
        Some((
            (x * self.d - y * self.c) / det,
            (y * self.a - x * self.b) / det,
        ))
    }
}

/// Which `Tm` places each showing operation, and what is in force over it.
///
/// ⚠️ THE LAST `Tm` SINCE THE ENCLOSING `BT`, because `Td` and `T*` are
/// relative to the line matrix that `Tm` set. Moving the `Tm` carries every
/// relative step taken after it, which is exactly what is wanted, and it is
/// also why one `Tm` can govern more than one showing operation.
fn placements(content: &Content) -> BTreeMap<usize, (usize, Linear)> {
    let mut out = BTreeMap::new();
    let mut stack: Vec<Linear> = Vec::new();
    let mut ctm = Linear::IDENTITY;
    let mut placed: Option<usize> = None;

    for (at, op) in content.operations.iter().enumerate() {
        match op.operator.as_str() {
            "q" => stack.push(ctm),
            "Q" => ctm = stack.pop().unwrap_or(Linear::IDENTITY),
            "cm" => {
                let m: Vec<f64> = op.operands.iter().filter_map(number).collect();
                if m.len() == 6 {
                    ctm = Linear { a: m[0], b: m[1], c: m[2], d: m[3] }.then(ctm);
                }
            }
            "BT" => placed = None,
            "ET" => placed = None,
            "Tm" => placed = Some(at),
            "TJ" | "Tj" => {
                if let Some(tm) = placed {
                    out.insert(at, (tm, ctm));
                }
            }
            _ => {}
        }
    }
    out
}

fn number(o: &Object) -> Option<f64> {
    match o {
        Object::Real(r) => Some(*r as f64),
        Object::Integer(i) => Some(*i as f64),
        _ => None,
    }
}

/// Moves the text drawn by `showing` across the page by `(dx, dy)` in PDF user
/// space, returning the document's new bytes.
///
/// ⚠️ ALL OF A PLACEMENT OR NONE OF IT. One `Tm` can place several showing
/// operations, and moving it moves all of them. If any operation it governs was
/// not asked for, this refuses rather than dragging a neighbour along: text
/// moving that the reader did not touch is worse than a move that declines.
pub(crate) fn shift(
    doc: &Document,
    page: ObjectId,
    showing: &[usize],
    dx: f64,
    dy: f64,
) -> Result<Vec<u8>, i32> {
    if showing.is_empty() {
        return Err(STATUS_INVALID_INPUT);
    }
    let Ok(mut content) = Content::decode(&doc.get_page_content(page)) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };

    let places = placements(&content);
    let mut moving: BTreeMap<usize, Linear> = BTreeMap::new();
    for at in showing {
        let Some((tm, ctm)) = places.get(at).copied() else {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        };
        moving.insert(tm, ctm);
    }

    // ⚠️ NOTHING ELSE MAY BE CARRIED ALONG. Asked once per placement being
    // moved: does it also draw something that was not asked for?
    for (at, (tm, _)) in &places {
        if moving.contains_key(tm) && !showing.contains(at) {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        }
    }

    for (tm, ctm) in &moving {
        let Some((de, df)) = ctm.undo(dx, dy) else {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        };
        let op = &mut content.operations[*tm];
        let m: Vec<f64> = op.operands.iter().filter_map(number).collect();
        if m.len() != 6 {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        }
        *op = Operation::new(
            "Tm",
            vec![
                Object::Real(m[0] as f32),
                Object::Real(m[1] as f32),
                Object::Real(m[2] as f32),
                Object::Real(m[3] as f32),
                Object::Real((m[4] + de) as f32),
                Object::Real((m[5] + df) as f32),
            ],
        );
    }

    let mut out = doc.clone();
    let Ok(encoded) = content.encode() else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    if out.change_page_content(page, encoded).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }
    let mut bytes = Vec::new();
    if out.save_to(&mut bytes).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use lopdf::dictionary;

    /// A page of exactly these operations, with a media box so it is a real
    /// page and not a fragment.
    fn a_page(operations: Vec<Operation>) -> (Document, ObjectId) {
        let mut doc = Document::with_version("1.7");
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
            "Resources" => dictionary! {},
        });
        doc.objects.insert(pages_id, lopdf::Object::Dictionary(dictionary! {
            "Type" => "Pages",
            "Kids" => vec![page.into()],
            "Count" => 1,
        }));
        let catalog = doc.add_object(dictionary! { "Type" => "Catalog", "Pages" => pages_id });
        doc.trailer.set("Root", catalog);
        (doc, page)
    }

    fn tm(a: f64, b: f64, c: f64, d: f64, e: f64, f: f64) -> Operation {
        Operation::new("Tm", vec![
            a.into(), b.into(), c.into(), d.into(), e.into(), f.into(),
        ])
    }

    fn show(text: &str) -> Operation {
        Operation::new("Tj", vec![Object::String(
            text.as_bytes().to_vec(), lopdf::StringFormat::Literal)])
    }

    /// Every `Tm` on the first page, as its six numbers.
    fn matrices(bytes: &[u8]) -> Vec<Vec<f64>> {
        matrices_of(bytes, 0)
    }

    /// Every `Tm` on one page, as its six numbers.
    fn matrices_of(bytes: &[u8], page_index: usize) -> Vec<Vec<f64>> {
        let doc = Document::load_mem(bytes).unwrap();
        let pages = doc.get_pages();
        let (_, &page) = pages.iter().nth(page_index).unwrap();
        Content::decode(&doc.get_page_content(page))
            .unwrap()
            .operations
            .iter()
            .filter(|op| op.operator == "Tm")
            .map(|op| op.operands.iter().filter_map(number).collect())
            .collect()
    }

    /// Two lines, each its own text object placed absolutely, which is how
    /// every real file measured draws its text.
    fn two_lines() -> (Document, ObjectId) {
        a_page(vec![
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 72.0, 700.0),
            show("one"),
            Operation::new("ET", vec![]),
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 72.0, 680.0),
            show("two"),
            Operation::new("ET", vec![]),
        ])
    }

    #[test]
    fn a_line_moves_by_exactly_what_it_was_asked_to() {
        let (doc, page) = two_lines();
        // Operation 2 is the first `Tj`.
        let out = shift(&doc, page, &[2], 12.0, -5.0).expect("refused");
        let after = matrices(&out);

        assert_eq!(after.len(), 2);
        assert!((after[0][4] - 84.0).abs() < 0.01, "{:?}", after[0]);
        assert!((after[0][5] - 695.0).abs() < 0.01, "{:?}", after[0]);
    }

    /// ⚠️ AND NOTHING ELSE MOVES. The whole point of a move is that the reader
    /// picked one thing up; anything else shifting is damage they did not ask
    /// for and may not even be looking at.
    #[test]
    fn the_line_that_was_not_asked_for_stays_exactly_where_it_was() {
        let (doc, page) = two_lines();
        let before = matrices(&{
            let mut b = Vec::new();
            doc.clone().save_to(&mut b).unwrap();
            b
        });
        let out = shift(&doc, page, &[2], 12.0, -5.0).expect("refused");
        let after = matrices(&out);

        assert_eq!(before[1], after[1], "the other line moved");
    }

    /// ⚠️ THE BOOK'S PAGE, WHICH WOULD HAVE MOVED THE WRONG WAY. Its 32 text
    /// objects are all drawn under `cm [0.72 0 0 -0.72 72 841.9]`: a scale, and
    /// a FLIP OF THE Y AXIS. Ten points down the page is not ten points in the
    /// numbers a `Tm` carries, and down is up.
    #[test]
    fn a_move_is_turned_through_the_transform_in_force_over_the_text() {
        const S: f64 = 0.7197380065917969;
        const T: f64 = -0.72017902135849;

        let (doc, page) = a_page(vec![
            Operation::new("cm", vec![
                S.into(), 0.into(), 0.into(), T.into(), 72.into(), 841.89.into(),
            ]),
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 100.0, 200.0),
            show("under a transform"),
            Operation::new("ET", vec![]),
        ]);

        // Twenty points right and ten DOWN the page, in user space.
        let out = shift(&doc, page, &[3], 20.0, -10.0).expect("refused");
        let after = matrices(&out);

        // Which is 20/0.7197 across and -10/-0.7202 along, in the text's own
        // space: the flip turns a move down the page into a move UP in `Tm`.
        assert!((after[0][4] - (100.0 + 20.0 / S)).abs() < 0.01, "{:?}", after[0]);
        assert!((after[0][5] - (200.0 + -10.0 / T)).abs() < 0.01, "{:?}", after[0]);
        assert!(after[0][5] > 200.0,
            "the flip was not honoured: moving down the page moved f down too");
    }

    /// A transform that flattens the page onto a line has no inverse, and there
    /// is no honest answer for where the text should go.
    #[test]
    fn a_transform_that_cannot_be_undone_is_refused() {
        let (doc, page) = a_page(vec![
            Operation::new("cm", vec![0.into(), 0.into(), 0.into(), 0.into(),
                                      0.into(), 0.into()]),
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 100.0, 200.0),
            show("flattened"),
            Operation::new("ET", vec![]),
        ]);
        assert_eq!(shift(&doc, page, &[3], 5.0, 5.0), Err(STATUS_LINE_NOT_REWRITABLE));
    }

    /// ⚠️ ALL OF A PLACEMENT OR NONE OF IT. One `Tm` can place several showing
    /// operations, and moving it moves all of them. Dragging a neighbour along
    /// is worse than declining.
    #[test]
    fn a_placement_that_also_draws_something_else_is_refused() {
        let (doc, page) = a_page(vec![
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 72.0, 700.0),
            show("mine"),
            Operation::new("Td", vec![50.into(), 0.into()]),
            show("someone else's"),
            Operation::new("ET", vec![]),
        ]);
        assert_eq!(shift(&doc, page, &[2], 10.0, 0.0), Err(STATUS_LINE_NOT_REWRITABLE));

        // Asked for both, it moves them, because then nothing is carried along
        // that the caller did not want.
        assert!(shift(&doc, page, &[2, 4], 10.0, 0.0).is_ok());
    }

    /// A `q`/`Q` pair puts the transform back, and text after it is not under
    /// the one inside.
    #[test]
    fn a_transform_that_was_put_back_does_not_apply_to_what_follows() {
        let (doc, page) = a_page(vec![
            Operation::new("q", vec![]),
            Operation::new("cm", vec![2.into(), 0.into(), 0.into(), 2.into(),
                                      0.into(), 0.into()]),
            Operation::new("Q", vec![]),
            Operation::new("BT", vec![]),
            tm(1.0, 0.0, 0.0, 1.0, 72.0, 700.0),
            show("plain"),
            Operation::new("ET", vec![]),
        ]);
        let out = shift(&doc, page, &[5], 10.0, 0.0).expect("refused");

        // Not five, which is what the doubled transform would have given.
        assert!((matrices(&out)[0][4] - 82.0).abs() < 0.01, "{:?}", matrices(&out));
    }

    #[test]
    fn moving_nothing_is_refused_rather_than_rewriting_the_file() {
        let (doc, page) = two_lines();
        assert_eq!(shift(&doc, page, &[], 5.0, 5.0), Err(STATUS_INVALID_INPUT));
        assert_eq!(shift(&doc, page, &[999], 5.0, 5.0), Err(STATUS_LINE_NOT_REWRITABLE));
    }

    /// The real thing, on files that are not in this repository: a line of each
    /// is picked up and put down twenty points to the right and ten down, and
    /// nothing else on the page is allowed to have moved.
    ///
    /// ⚠️ THE BOOK IS THE ONE THAT MATTERS. Its text is drawn under a scale and
    /// a flip, so it is the page where getting the arithmetic wrong shows up as
    /// a line landing somewhere else entirely.
    #[test]
    #[ignore = "needs PDFs that are not in this repository"]
    fn a_line_of_a_real_page_moves_and_takes_nothing_with_it() {
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
            let name = std::path::Path::new(file).file_name().unwrap().to_owned();
            let doc = Document::load(file).unwrap();
            let pages = doc.get_pages();
            let (_, &page) = pages.iter().nth(page_index).unwrap();

            // A line with something on it, and the operations that draw it.
            let lines = crate::recover::lines_of(&doc, page);
            let line = lines
                .iter()
                .find(|l| l.glyphs.len() > 4 && !l.drawn_by.is_empty())
                .expect("no line to move");
            let mine = line.drawn_by.clone();

            let mut before = Vec::new();
            doc.clone().save_to(&mut before).unwrap();
            let was = matrices_of(&before, page_index);

            let out = match shift(&doc, page, &mine, 20.0, -10.0) {
                Ok(out) => out,
                Err(status) => {
                    println!("{name:?}: refused with {status}, {} operations",
                        mine.len());
                    continue;
                }
            };
            let now = matrices_of(&out, page_index);
            assert_eq!(was.len(), now.len(), "{name:?}: a Tm appeared or vanished");

            let moved: Vec<usize> = (0..was.len()).filter(|i| was[*i] != now[*i]).collect();
            println!("{name:?}: {} operations drew the line, {} of {} matrices moved",
                mine.len(), moved.len(), was.len());

            assert!(!moved.is_empty(), "{name:?}: nothing moved at all");

            // ⚠️ AND EVERY ONE THAT MOVED WENT THE SAME WAY BY THE SAME AMOUNT.
            // A page where some of a line's placements are under one transform
            // and some under another would come apart, and this is what would
            // say so.
            let first = (now[moved[0]][4] - was[moved[0]][4],
                         now[moved[0]][5] - was[moved[0]][5]);
            for i in &moved {
                let d = (now[*i][4] - was[*i][4], now[*i][5] - was[*i][5]);
                assert!((d.0 - first.0).abs() < 0.01 && (d.1 - first.1).abs() < 0.01,
                    "{name:?}: one placement moved by {d:?} and another by {first:?}");
            }

            // And the file is still a file.
            let reopened = Document::load_mem(&out).unwrap();
            assert_eq!(reopened.get_pages().len(), pages.len());
        }
    }

    #[test]
    fn undoing_a_displacement_gives_back_what_went_in() {
        let m = Linear { a: 0.72, b: 0.0, c: 0.0, d: -0.72 };
        let (x, y) = m.undo(20.0, -10.0).unwrap();
        assert!((x * m.a + y * m.c - 20.0).abs() < 1e-9);
        assert!((x * m.b + y * m.d - -10.0).abs() < 1e-9);
    }
}
