//! Writing new text over a line whose old text was RECOVERED rather than read.
//!
//! ⚠️ THE ORDINARY WRITER CANNOT FIND THESE LINES AT ALL. `justified` locates a
//! line by encoding the text it expects through WinAnsi and matching those
//! single bytes against the stream. WinAnsi has no code for U+1019, so for a
//! line of Burmese it refuses before it starts. That writer was built to edit a
//! LATIN line and drop shaped text into it; this one edits a line that is
//! already shaped.
//!
//! ⚠️ SO THE LINE IS FOUND BY ITS GLYPHS, and that is self-validating in the
//! same way. [`crate::recover`] reads the line, the reading is shaped again,
//! and the glyphs it produces must be the ones already on the page. A caller
//! whose idea of the line has gone stale gets a refusal, not someone else's
//! text overwritten.

use lopdf::content::{Content, Operation};
use lopdf::{Document, Object, ObjectId};

use crate::recover::{Break, Line};
use crate::{
    STATUS_DOC_NOT_REWRITABLE, STATUS_FONT_UNUSABLE, STATUS_INVALID_INPUT,
    STATUS_LINE_NOT_REWRITABLE,
};

/// The resource the replacement's font is named by on the page.
///
/// ⚠️ ITS OWN, NOT THE ONE THE LINE WAS DRAWN WITH. The page's font is a
/// SUBSET, holding only the glyphs the producer happened to use, and its layout
/// tables are pruned: measured, shaping one known word through the subset gave
/// 2 `.notdef` and 7 wrong glyphs out of 24. A reader typing a letter the page
/// does not already contain would be drawing nothing at all.
const RESOURCE: &[u8] = b"AyaanRetype";

/// What a replacement will draw, before it is written.
pub(crate) struct Replacement {
    /// The `TJ` operands, with the line's word spaces put back as numbers.
    run: Vec<Object>,
    /// What the whole line will advance by, in points.
    pub(crate) advance: f64,
}

/// The glyphs, as one `TJ` string operand.
fn codes(ids: &[u16]) -> Object {
    Object::String(
        ids.iter().flat_map(|g| g.to_be_bytes()).collect(),
        lopdf::StringFormat::Hexadecimal,
    )
}

/// Lays `glyphs` out as one run, putting the line's word spaces back.
///
/// ⚠️ A SPACE BETWEEN PLACEMENTS HAS NO GLYPH, so collapsing a line's placements
/// into one run loses every one of them unless they are re-emitted as `TJ`
/// numbers. Measured on a real page: one line's three spaces were 169, 36 and
/// 200 thousandths wide, none of which any glyph would have drawn.
pub(crate) fn lay_out(
    glyphs: &[u16],
    breaks: &[Break],
    size: f64,
    widths: &crate::shaped::CidWidths,
) -> Replacement {
    let mut run: Vec<Object> = Vec::new();
    let mut advance = 0.0f64;
    let mut from = 0usize;

    for gap in breaks {
        if gap.at <= from || gap.at > glyphs.len() {
            continue;
        }
        run.push(codes(&glyphs[from..gap.at]));
        // A positive `TJ` number moves the pen LEFT, so opening a space is a
        // negative one, in thousandths of the type size.
        run.push(Object::Real((-(gap.points / size * 1000.0)) as f32));
        advance += gap.points;
        from = gap.at;
    }
    run.push(codes(&glyphs[from..]));

    let drawn: f64 = glyphs.iter().map(|g| widths.of(*g)).sum();
    advance += drawn / 1000.0 * size;
    Replacement { run, advance }
}

/// Puts `replacement` where `line` was drawn, in a font of our own.
///
/// ⚠️ EVERY OPERATION THE LINE WAS DRAWN BY IS ACCOUNTED FOR. A real page draws
/// one line in many pieces: measured, 63 operations for 86 glyphs. The first
/// becomes the whole replacement and the rest are emptied, because leaving one
/// behind would draw the old text on top of the new.
pub(crate) fn write_over(
    doc: &Document,
    page: ObjectId,
    line: &Line,
    replacement: Replacement,
    font: ObjectId,
    text: &str,
) -> Result<Vec<u8>, i32> {
    let Some(&first) = line.drawn_by.first() else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };
    let Ok(mut content) = Content::decode(&doc.get_page_content(page)) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    if line.drawn_by.iter().any(|at| *at >= content.operations.len()) {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    }

    let size = Object::Real(line.size as f32);
    let replacing = vec![
        Operation::new("Tf", vec![Object::Name(RESOURCE.to_vec()), size.clone()]),
        crate::justified::actual_text(text),
        Operation::new("TJ", vec![Object::Array(replacement.run)]),
        Operation::new("EMC", vec![]),
        // ⚠️ THE LINE'S OWN FONT GOES BACK. `Tf` outlives `BT`/`ET`, so without
        // this every later line on the page would be set in ours.
        Operation::new("Tf", vec![Object::Name(line.resource.clone()), size]),
    ];

    // Everything else the line was drawn by draws nothing now. Emptied rather
    // than removed, so every other operation keeps the index it had.
    for &at in &line.drawn_by {
        content.operations[at] = Operation::new("TJ", vec![Object::Array(Vec::new())]);
    }
    content.operations.splice(first..=first, replacing);

    let mut out = doc.clone();
    crate::justified::name_the_font(&mut out, page, RESOURCE, font)?;
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

/// What the line advances by now, in points, using the widths the PAGE
/// declares for the font it was drawn with.
///
/// ⚠️ THE PAGE'S WIDTHS, NOT OURS. The replacement is set in a font of our
/// own embedding, whose glyphs advance by their own amounts, so measuring the
/// old line with the new font's numbers would compare nothing to nothing.
fn advance_of(doc: &Document, page: ObjectId, line: &Line) -> Option<f64> {
    let fonts = crate::recover::fonts_of(doc, page);
    let (_, widths) = fonts.get(&line.resource)?;
    let widths = widths.as_ref()?;
    let drawn: f64 = line.glyphs.iter().map(|g| widths.of(*g)).sum();
    Some((drawn + line.adjust) / 1000.0 * line.size)
}

/// Widens or narrows the line's word spaces so the replacement ends where the
/// old line ended.
///
/// ⚠️ OR THE RIGHT MARGIN MOVES. A justified paragraph is justified by
/// stretching the spaces it already has, so a replacement that ignores the
/// width it is replacing leaves one line of the block short or long, which is
/// the most visible thing an editor can do to a page.
///
/// ⚠️ AND A LINE WITH NO SPACES CANNOT BE STRETCHED, so it is left at its
/// Where a replacement's word spaces are, as indices into its OWN glyphs.
///
/// ⚠️ THE NEW TEXT'S SPACES, NEVER THE OLD LINE'S. This used to reuse the
/// breaks the old line was read with, which are glyph indices into text that no
/// longer exists: the replacement has a different number of glyphs in different
/// places, so every one of those gaps landed somewhere arbitrary in it, and in
/// Burmese that is usually between a consonant and the vowel sign that belongs
/// to it. Measured in the running app on a real page: `အရှေ့` came out as
/// `အရေ့` with its `ှ` sitting alone a space to the right, and gaps opened
/// inside `မိုးကုပ်စက်ဝိုင်းမှ`. The justification slack was then added to those
/// same wrong positions, which widened them.
///
/// ⚠️ AND THE GAP GOES AFTER THE SPACE, not in place of it. The replacement is
/// set in a font embedded whole, so its spaces are real space glyphs with real
/// advances and the words are already correctly apart. What is left to add is
/// only the stretch the old line had.
fn spaces_in(glyphs: &[crate::ShapedGlyph], text: &str) -> Vec<usize> {
    let mut out: Vec<usize> = Vec::new();
    for (i, g) in glyphs.iter().enumerate() {
        let at = g.cluster as usize;
        if text[at..].starts_with(' ') && out.last() != Some(&(i + 1)) {
            out.push(i + 1);
        }
    }
    out
}

/// The justification the old line had, shared out across those spaces.
///
/// ⚠️ NOTHING TO SHARE IT ACROSS MEANS NOTHING IS ADDED. A line of one word has
/// no spaces to stretch, and a replacement wider than the room it is going into
/// cannot be squeezed: its own glyphs are as wide as they are. Either way the
/// line simply ends where its letters end, which is honest, where spreading the
/// difference between the letters would take a word apart.
fn share(spaces: &[usize], by: f64) -> Vec<Break> {
    if spaces.is_empty() || by <= 0.0 {
        return Vec::new();
    }
    let each = by / spaces.len() as f64;
    spaces.iter().map(|at| Break { at: *at, points: each }).collect()
}

/// The line on this baseline that reads as `expected`, if exactly one does.
///
/// ⚠️ EXACTLY ONE. Two lines on a baseline saying the same thing is not a
/// line this can safely edit, because there is nothing to choose between them
/// and choosing wrongly overwrites the other.
fn the_one_that_says<'a>(
    lines: &'a [Line],
    baseline: f64,
    expected: &str,
    indexes: &crate::recover::Indexes,
    face: &rustybuzz::Face,
) -> Option<&'a Line> {
    let mut found = None;
    for line in crate::recover::lines_at(lines, baseline) {
        let Some(index) = indexes.index_for(&line.base_font) else { continue };
        if crate::recover::read_line(index, face, line).as_deref() != Some(expected) {
            continue;
        }
        if found.is_some() {
            return None;
        }
        found = Some(line);
    }
    found
}

fn says_it(
    lines: &[Line],
    baseline: f64,
    expected: &str,
    indexes: &crate::recover::Indexes,
    face: &rustybuzz::Face,
) -> bool {
    the_one_that_says(lines, baseline, expected, indexes, face).is_some()
}

/// Replaces the text of one recovered line, returning the document's new bytes.
///
/// `expected` is what the caller believes the line says. The line is recovered
/// again here and must still say it, so a stale selection cannot overwrite
/// whatever has taken its place.
pub(crate) fn retype(
    bytes: &[u8],
    page_index: i32,
    baseline: f64,
    expected: &str,
    new_text: &str,
    font_path: &str,
) -> Result<Vec<u8>, i32> {
    if new_text.is_empty() || expected.is_empty() {
        return Err(STATUS_INVALID_INPUT);
    }
    let Ok(doc) = Document::load_mem(bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let pages = doc.get_pages();
    let Some((_, &page)) = pages.iter().nth(page_index as usize) else {
        return Err(STATUS_INVALID_INPUT);
    };

    let Some(font_bytes) = std::fs::read(font_path).ok() else {
        return Err(STATUS_FONT_UNUSABLE);
    };
    let Some(face) = rustybuzz::Face::from_slice(&font_bytes, 0) else {
        return Err(STATUS_FONT_UNUSABLE);
    };

    // ⚠️ THE LINE IS THE ONE THAT SAYS WHAT THE CALLER THINKS IT SAYS. A
    // baseline only narrows the field, and a caller whose idea of the line has
    // gone stale finds nothing here rather than overwriting whatever took its
    // place.
    let indexes = crate::recover::indexes_for(&doc, page);
    let lines = crate::recover::lines_of(&doc, page);
    if !says_it(&lines, baseline, expected, &indexes, &face) {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    }

    // ⚠️ AND THE FONT IS EMBEDDED WHOLE, not borrowed from the page. See
    // `RESOURCE`: the page's copy is a subset and cannot spell a letter the
    // producer never used.
    let provisioned =
        crate::provision::provision(Some(font_path), new_text, crate::shaped::SHAPING_SIZE)
            .map_err(crate::status_of)?;
    let Some((bytes, font_id)) = crate::embed_for_shaping(bytes, &provisioned) else {
        return Err(STATUS_FONT_UNUSABLE);
    };
    let Ok(doc) = Document::load_mem(&bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let Some(widths) = crate::shaped::cid_widths(&doc, font_id) else {
        return Err(STATUS_FONT_UNUSABLE);
    };

    // The line has to be found again: embedding rewrote the document, and the
    // operation indices it carries are indices into that document's stream.
    let indexes = crate::recover::indexes_for(&doc, page);
    let lines = crate::recover::lines_of(&doc, page);
    let Some(line) = the_one_that_says(&lines, baseline, expected, &indexes, &face) else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };

    let glyphs: Vec<u16> = provisioned.glyphs.iter().map(|g| g.id as u16).collect();
    let spaces = spaces_in(&provisioned.glyphs, new_text);

    // Laid out once as the shaper set it, to find out how wide it comes, then
    // again with its own spaces opened so it ends where the old line ended.
    let natural = lay_out(&glyphs, &[], line.size, &widths);
    let replacement = match advance_of(&doc, page, line) {
        Some(target) => {
            let gaps = share(&spaces, target - natural.advance);
            lay_out(&glyphs, &gaps, line.size, &widths)
        }
        None => natural,
    };
    write_over(&doc, page, line, replacement, font_id, new_text)
}

#[cfg(test)]
mod tests {
    use super::*;

    const MYANMAR_TEXT: &str = r"C:\Windows\Fonts\mmrtext.ttf";

    /// Every glyph of `text` as the writer will actually set it: shaped by the
    /// installed Myanmar Text, which is the font it embeds.
    fn shaped(text: &str) -> Vec<crate::ShapedGlyph> {
        crate::provision::provision(Some(MYANMAR_TEXT), text, crate::shaped::SHAPING_SIZE)
            .unwrap()
            .glyphs
    }

    /// The index of every glyph that STARTS a drawn unit, and the end.
    ///
    /// A gap anywhere else is a gap inside a mark's own letter.
    fn cluster_edges(glyphs: &[crate::ShapedGlyph]) -> Vec<usize> {
        let mut edges = vec![0usize];
        for (i, g) in glyphs.iter().enumerate().skip(1) {
            if g.cluster != glyphs[i - 1].cluster {
                edges.push(i);
            }
        }
        edges.push(glyphs.len());
        edges
    }

    /// ⚠️ THE BUG THIS FUNCTION EXISTS FOR. The replacement used to be
    /// justified on the OLD line's break positions, which are glyph indices
    /// into text that no longer exists. Seen in the running app on a real page:
    /// `အရှေ့` came out as `အရေ့` with its `ှ` alone a space to the right, and
    /// gaps opened inside `မိုးကုပ်စက်ဝိုင်းမှ`.
    #[test]
    fn a_replacement_opens_its_gaps_only_at_its_own_spaces() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        // Two words, one space: exactly one gap, and it follows the space.
        const TEXT: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C} \u{1005}\u{102C}";
        let glyphs = shaped(TEXT);
        let spaces = spaces_in(&glyphs, TEXT);

        assert_eq!(spaces.len(), 1, "{spaces:?} for the one space in {TEXT:?}");

        let edges = cluster_edges(&glyphs);
        assert!(edges.contains(&spaces[0]),
            "a gap at glyph {} is inside a drawn unit; the edges are {edges:?}",
            spaces[0]);
    }

    /// ⚠️ AND NEVER INSIDE A DRAWN UNIT, whichever way the text is written. A
    /// gap between a consonant and its own vowel sign is the visible damage:
    /// the mark ends up drawn a space away from the letter it belongs to.
    #[test]
    fn no_gap_ever_falls_inside_a_drawn_unit() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        for text in [
            "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C} \u{1005}\u{102C}",
            "\u{1021}\u{101B}\u{103E}\u{1031}\u{1037} \u{1019}\u{102D}\u{102F}\u{1038}",
            "\u{1000} \u{1001} \u{1002} \u{1003}",
            "one two three",
        ] {
            let glyphs = shaped(text);
            let edges = cluster_edges(&glyphs);
            for at in spaces_in(&glyphs, text) {
                assert!(edges.contains(&at),
                    "in {text:?} a gap at glyph {at} is inside a unit: {edges:?}");
            }
        }
    }

    /// One space means one gap, however many glyphs the words either side of it
    /// happen to need.
    #[test]
    fn a_gap_for_every_space_and_no_others() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const THREE: &str = "\u{1000} \u{1001} \u{1002}";
        let glyphs = shaped(THREE);
        assert_eq!(spaces_in(&glyphs, THREE).len(), 2);
    }

    /// A line of one word has nothing to stretch, and stretching it anyway
    /// would take the word apart.
    #[test]
    fn a_line_with_no_spaces_is_not_stretched_at_all() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        const ONE: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let spaces = spaces_in(&shaped(ONE), ONE);

        assert!(spaces.is_empty());
        assert!(share(&spaces, 40.0).is_empty(),
            "a word was pulled apart to fill the line");
    }

    /// A replacement wider than the room it is going into cannot be squeezed:
    /// its own glyphs are as wide as they are.
    /// What the writer actually PUT IN THE FILE, read back out of it.
    ///
    /// ⚠️ THE ONLY TEST HERE THAT WOULD HAVE CAUGHT THE BUG. The two above it
    /// check functions that did not exist while it was live, so they pin the
    /// fix but cannot fail on the code that had it. This walks the replacement
    /// the writer wrote and demands every gap in it fall where the shaper says
    /// one drawn unit ends. Reverting `spaces_in`/`share` to the old
    /// `line.breaks` makes it fail.
    ///
    /// ⚠️ AND READING THE PAGE BACK IS NOT ENOUGH, which is why this looks at
    /// the stream. Recovery only turns a gap into a space where it can prove
    /// both sides, so a gap dropped inside a cluster is passed over and the
    /// text reads back perfectly while the page is visibly damaged.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn the_gaps_the_writer_leaves_in_the_file_are_all_between_drawn_units() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();

        // The first line the page will let us read, and a replacement of a
        // quite different length so the old positions cannot accidentally fit.
        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = crate::recover::indexes_for(&doc, page);
        let read = crate::recover::read_page_with(&doc, page, &indexes);
        let line = read.iter().find(|r| r.text.is_some()).expect("nothing read");
        let was = line.text.clone().unwrap();

        // ⚠️ LONG ENOUGH THAT THE OLD LINE'S POSITIONS FALL INSIDE IT. A short
        // replacement sends every stale index off the end, where it is filtered
        // away, and that hides the damage instead of showing it.
        const NOW: &str = "\u{1021}\u{101B}\u{103E}\u{1031}\u{1037}\u{1019}\u{102D}\u{102F}\u{1038}\
\u{1000}\u{102F}\u{1015}\u{103A}\u{1005}\u{1000}\u{103A}\u{101D}\u{102D}\u{102F}\u{1004}\u{103A}\u{1038}\u{1019}\u{103E} \
\u{1021}\u{101B}\u{102F}\u{1023}\u{103A}\u{1026}\u{1038} \
\u{101B}\u{1031}\u{102C}\u{1004}\u{103A}\u{1014}\u{102E}\u{101E}\u{100A}\u{103A}";
        let out = retype(&bytes, 0, line.y, &was, NOW, MYANMAR_TEXT)
            .expect("the retype was refused");

        // The replacement is the run set in our own font resource.
        let after = Document::load_mem(&out).unwrap();
        let (_, &page) = after.get_pages().iter().next().unwrap();
        let content = Content::decode(&after.get_page_content(page)).unwrap();

        let mut ours = false;
        let mut run: Option<Vec<Object>> = None;
        for op in &content.operations {
            match op.operator.as_str() {
                "Tf" => {
                    ours = matches!(op.operands.first(), Some(Object::Name(n)) if n == RESOURCE);
                }
                "TJ" if ours => {
                    if let Some(Object::Array(a)) = op.operands.first() {
                        run = Some(a.clone());
                        break;
                    }
                }
                _ => {}
            }
        }
        let run = run.expect("the replacement is not in the file");

        // Walk it: strings are glyphs, numbers are gaps between them.
        let mut at = 0usize;
        let mut gaps: Vec<usize> = Vec::new();
        for item in &run {
            match item {
                Object::String(b, _) => at += b.len() / 2,
                Object::Real(_) | Object::Integer(_) => gaps.push(at),
                _ => {}
            }
        }

        let glyphs = shaped(NOW);
        assert_eq!(at, glyphs.len(), "the run does not draw the new text");

        // ⚠️ AT THE SPACES THEMSELVES, NOT MERELY SOMEWHERE HARMLESS. Asking
        // only that each gap fall on a unit boundary was measured PASSING on
        // the broken code for one replacement out of two: stale positions can
        // land on a boundary by luck, and one that does is still a gap in the
        // wrong place. The positions the writer should have chosen are known
        // exactly, so demand exactly those.
        assert_eq!(gaps, spaces_in(&glyphs, NOW),
            "the writer put its gaps at {gaps:?}, not at the new text's spaces");

        // Said again the way the damage shows on the page, so a failure names
        // it: a gap inside a unit draws a mark a space away from its letter.
        let edges = cluster_edges(&glyphs);
        for gap in &gaps {
            assert!(edges.contains(gap),
                "the writer left a gap at glyph {gap}, which is inside a drawn \
                 unit; the units start at {edges:?}");
        }

        // ⚠️ AND THE LINE DID NOT MOVE. The replacement is spliced at the first
        // operation the line was drawn by, so it begins wherever that
        // operation's pen already was; anything else and the paragraph's left
        // edge would step in or out by however far the writer was off.
        let before = crate::recover::lines_of(&doc, page);
        let after_lines = crate::recover::lines_of(&after, page);
        let was_at = crate::recover::lines_at(&before, line.y)
            .map(|l| l.x)
            .next()
            .expect("the line was not there to start with");
        let now_at = crate::recover::lines_at(&after_lines, line.y)
            .map(|l| l.x)
            .next()
            .expect("the line is gone");
        assert!((now_at - was_at).abs() < 0.01,
            "the line started at {was_at:.2} and now starts at {now_at:.2}");
    }

    #[test]
    fn nothing_is_taken_away_when_the_replacement_is_too_wide() {
        assert!(share(&[3, 7], -20.0).is_empty());
        assert!(share(&[3, 7], 0.0).is_empty());
    }

    #[test]
    fn the_stretch_is_shared_equally_between_the_spaces() {
        let gaps = share(&[3, 7, 11], 30.0);

        assert_eq!(gaps.iter().map(|g| g.at).collect::<Vec<_>>(), vec![3, 7, 11]);
        for g in &gaps {
            assert!((g.points - 10.0).abs() < 1e-9, "{g:?}");
        }
    }
}
