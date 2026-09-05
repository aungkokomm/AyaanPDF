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

/// Lays `glyphs` out as one run, drawn where the SHAPER put them, and with the
/// line's word spaces put back.
///
/// ⚠️ A SPACE BETWEEN PLACEMENTS HAS NO GLYPH, so collapsing a line's placements
/// into one run loses every one of them unless they are re-emitted as `TJ`
/// numbers. Measured on a real page: one line's three spaces were 169, 36 and
/// 200 thousandths wide, none of which any glyph would have drawn.
///
/// ⚠️ AND A PDF RUN CARRIES NO SHAPING. A viewer advances the pen by the width
/// the FILE declares for each glyph and draws the next one there; it does not
/// know that a Burmese mark belongs under the letter before it. The font here
/// is embedded by PDFium, and measured on the reader's own line PDFium gives
/// six of its marks the DEFAULT width of a full em where the shaper advances
/// them by nothing at all. Every one of those opened an em of blank page after
/// the mark: `\u{1021}\u{101B}\u{103E}\u{1031}\u{1037}` came out as `\u{1021}\u{101B}\u{1031}\u{1037}` with the medial stranded to the right,
/// and the line was 79 thousandths of an em too wide for every mark in it.
///
/// So every glyph the file would advance differently carries a `TJ` number
/// correcting the pen to the shaper's advance, and a glyph the shaper nudged
/// sideways is moved there and back around its own placement.
///
/// ⚠️ A MARK RAISED OR LOWERED CANNOT BE EXPRESSED HERE. A `TJ` number moves
/// the pen along the line and nothing else, so a `y_offset` is dropped. Neither
/// of the two faces this writer is used with asks for one: measured over the
/// reader's page, none of 43 glyphs was raised or lowered by any amount.
pub(crate) fn lay_out(
    glyphs: &[crate::ShapedGlyph],
    breaks: &[Break],
    size: f64,
    widths: &crate::shaped::CidWidths,
) -> Replacement {
    // The shaper works at `SHAPING_SIZE`, so its advances are already in
    // thousandths of an em when that size is 1000. Written as the ratio so it
    // stays right if the size ever changes.
    let per_mille = 1000.0 / crate::shaped::SHAPING_SIZE as f64;

    let mut run: Vec<Object> = Vec::new();
    let mut pending: Vec<u16> = Vec::new();
    // Everything the run advances by, in thousandths of the type size.
    let mut advance = 0.0f64;
    // A correction too small to be worth a number of its own, kept until it is.
    let mut owed = 0.0f64;

    // A number can only follow the glyphs drawn so far, so anything held back
    // is written out first.
    //
    // ⚠️ A CORRECTION TOO SMALL TO WRITE IS OWED, NOT DROPPED. Most glyphs
    // differ from the file's declared width only by the rounding in its own
    // tables, a thousandth of an em or less, and a number for each would treble
    // the length of the run. But dropping them lets the error walk: measured on
    // one real line, fifteen glyphs of rounding had already moved the pen 1.2
    // thousandths, and a long line drifts further with every one. Carried
    // forward, the run stays short and the pen stays exact.
    fn put(run: &mut Vec<Object>, pending: &mut Vec<u16>, owed: &mut f64, by: f64) {
        *owed += by;
        if owed.abs() < 0.5 {
            return;
        }
        if !pending.is_empty() {
            run.push(codes(pending));
            pending.clear();
        }
        run.push(Object::Real(*owed as f32));
        *owed = 0.0;
    }

    for (i, g) in glyphs.iter().enumerate() {
        // The word space asked for before this glyph. A positive `TJ` number
        // moves the pen LEFT, so opening a space is a negative one.
        for gap in breaks.iter().filter(|b| b.at == i) {
            let wide = gap.points / size * 1000.0;
            put(&mut run, &mut pending, &mut owed, -wide);
            advance += wide;
        }

        let asked = g.x_advance as f64 * per_mille;
        let sideways = g.x_offset as f64 * per_mille;

        put(&mut run, &mut pending, &mut owed, -sideways);
        pending.push(g.id as u16);
        // Back to where the pen should be: undo the nudge, and take off
        // whatever the file's own width added over the shaper's advance.
        put(&mut run, &mut pending, &mut owed, sideways + widths.of(g.id as u16) - asked);
        advance += asked;
    }

    // A space asked for after the last glyph, which is what a line recovered
    // with a trailing space leaves behind.
    for gap in breaks.iter().filter(|b| b.at >= glyphs.len()) {
        let wide = gap.points / size * 1000.0;
        put(&mut run, &mut pending, &mut owed, -wide);
        advance += wide;
    }

    if !pending.is_empty() {
        run.push(codes(&pending));
    }
    Replacement { run, advance: advance / 1000.0 * size }
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
    // ⚠️ AND THE GAPS BETWEEN ITS PLACEMENTS, WHICH ARE NOT IN `adjust`. A
    // producer draws one line in several placements and the space between two
    // of them is made by starting the next one further along, not by any
    // number inside a run. `merge_placements` keeps those as `breaks` for
    // exactly this reason, and leaving them out measured the reader's own line
    // 46.7 points shorter than it is: the replacement was told it was already
    // too wide, no stretch was shared into it at all, and the line came back
    // with its right edge pulled 46.7 points in from the margin.
    let gaps: f64 = line.breaks.iter().map(|b| b.points).sum();
    Some((drawn + line.adjust) / 1000.0 * line.size + gaps)
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
/// ⚠️ AND THE INDEX IS LENT, NOT BUILT AGAIN. Reshaping a font into an index
/// is about twenty seconds, and this used to do it TWICE for one keystroke:
/// once to check the line still says what the caller thinks, and once more
/// after embedding the font, on a document that had changed underneath it. The
/// app has already built one to READ this line with, so a retype that built its
/// own was paying forty seconds for an answer it was being handed.
///
/// ⚠️ ONE INDEX SERVES BOTH READS, before and after embedding. Embedding adds
/// a font object; it does not renumber the subsets already there, and it is
/// those the line is drawn with. `lent` is None only when nothing has prepared
/// the document, and then one is built here as before.
pub(crate) fn retype(
    bytes: &[u8],
    page_index: i32,
    baseline: f64,
    expected: &str,
    new_text: &str,
    font_path: &str,
    lent: Option<&crate::recover::Indexes>,
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
    //
    // ⚠️ AND THE SAME INDEX THE READER USED, WHICH IS THE DOCUMENT'S. This was
    // scoped to one PAGE while the app read with a document-wide one, so the
    // writer's idea of what could be read was NARROWER than the reader's: a
    // line the reader had shown and offered came back here as one that says
    // nothing, and the retype was refused with the text plainly on screen.
    // Two answers to "what does this line say" is one too many.
    let built;
    let indexes: &crate::recover::Indexes = match lent {
        Some(ready) => ready,
        None => {
            built = crate::recover::indexes_for_document(&doc);
            &built
        }
    };
    let lines = crate::recover::lines_of(&doc, page);
    if !says_it(&lines, baseline, expected, indexes, &face) {
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
    //
    // ⚠️ THE SAME INDEX AGAIN, NOT A SECOND ONE. Embedding added a font
    // object and renumbered nothing: the line is still drawn with the subsets
    // that were already there, under the same names, so the index built for
    // them still reads it. Building another here was twenty seconds spent
    // arriving at the answer already in hand.
    let lines = crate::recover::lines_of(&doc, page);
    let Some(line) = the_one_that_says(&lines, baseline, expected, indexes, &face) else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };

    let glyphs = &provisioned.glyphs;
    let spaces = spaces_in(glyphs, new_text);

    // Laid out once as the shaper set it, to find out how wide it comes, then
    // again with its own spaces opened so it ends where the old line ended.
    let natural = lay_out(glyphs, &[], line.size, &widths);
    let replacement = match advance_of(&doc, page, line) {
        Some(target) => {
            let gaps = share(&spaces, target - natural.advance);
            lay_out(glyphs, &gaps, line.size, &widths)
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
        let out = retype(&bytes, 0, line.y, &was, NOW, MYANMAR_TEXT, None)
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

        // Walk the run the way a viewer does: a string draws its glyphs at the
        // pen, advancing it by the width the FILE declares, and a number moves
        // the pen along the line. Every glyph should be drawn exactly where the
        // shaper put it, plus whatever stretch has been opened before it.
        let glyphs = shaped(NOW);
        // ⚠️ THE WRITER'S FONT, FOUND BY THE NAME THE WRITER GAVE IT. Asking
        // for one whose BaseFont ends in "MyanmarText" finds the PAGE's own
        // subset first, and measuring the replacement with the widths of the
        // font it is not set in reports every mark 363 thousandths out.
        let named = crate::recover::fonts_of(&after, page);
        let embedded = named
            .get(RESOURCE)
            .and_then(|(_, w)| w.as_ref())
            .expect("the replacement's font is not named on the page");

        let mut pen = 0.0f64;
        let mut shaper = 0.0f64;
        let mut stretch = 0.0f64;
        let mut opened: Vec<usize> = Vec::new();
        let mut at = 0usize;

        for item in &run {
            match item {
                Object::String(b, _) => {
                    for pair in b.chunks(2) {
                        assert!(at < glyphs.len(), "the run draws more than the text has");
                        let id = u16::from_be_bytes([pair[0], pair[1]]);
                        assert_eq!(id, glyphs[at].id as u16,
                            "glyph {at} of the run is not the glyph the shaper made");

                        // Where this glyph SHOULD be drawn.
                        let want = shaper + stretch + glyphs[at].x_offset as f64;
                        let off = pen - want;

                        // A jump forward here is a word space being opened, and
                        // it is only allowed where the new text has one.
                        if off > 1.0 {
                            opened.push(at);
                            stretch += off;
                        } else {
                            assert!(off.abs() <= 1.0,
                                "glyph {at} is drawn {off:.1} thousandths from \
                                 where the shaper put it");
                        }

                        pen += embedded.of(id);
                        shaper += glyphs[at].x_advance as f64;
                        at += 1;
                    }
                }
                Object::Real(v) => pen -= *v as f64,
                Object::Integer(v) => pen -= *v as f64,
                _ => {}
            }
        }
        assert_eq!(at, glyphs.len(), "the run does not draw the whole text");

        // ⚠️ AT THE SPACES THEMSELVES, NOT MERELY SOMEWHERE HARMLESS. Asking
        // only that each gap fall on a unit boundary was measured PASSING on
        // the broken code for one replacement out of two: stale positions can
        // land on a boundary by luck, and one that does is still a gap in the
        // wrong place. The positions the writer should have chosen are known
        // exactly, so demand exactly those.
        //
        // ⚠️ AND THE STRETCH GOES BEFORE THE GLYPH THAT FOLLOWS THE SPACE,
        // which is the index `spaces_in` names.
        assert_eq!(opened, spaces_in(&glyphs, NOW),
            "the writer opened space at {opened:?}, not at the new text's spaces");

        // Said again the way the damage shows on the page, so a failure names
        // it: a gap inside a unit draws a mark a space away from its letter.
        let edges = cluster_edges(&glyphs);
        for gap in &opened {
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

    /// What ONE retype costs, stage by stage, and how much of each stage is
    /// work on the whole document rather than on the paragraph being edited.
    ///
    /// Run with
    ///   cargo test --release --lib what_one_edit_costs -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_one_edit_costs() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(MYANMAR_TEXT).exists() {
            println!("not on this machine");
            return;
        }
        let on_disk = std::fs::read(FILE).unwrap();

        // The document as the app holds it: open in PDFium, with one index
        // already built, which is the state a reader is in when they type.
        let handle = crate::open_document_from_bytes_inner(on_disk.as_ptr(), on_disk.len());
        assert_ne!(handle, 0);

        let timed = |what: &str, f: &mut dyn FnMut()| {
            let started = std::time::Instant::now();
            f();
            println!("{what:<44} {:>8.1} ms", started.elapsed().as_secs_f64() * 1000.0);
        };

        // 1. What the FFI does before it calls anything: serialise the whole
        //    document out of PDFium.
        let mut bytes = Vec::new();
        timed("snapshot the whole document out of PDFium",
            &mut || bytes = crate::document_bytes(handle).unwrap());
        println!("{:<44} {:>8} KB", "  (the document is)", bytes.len() / 1024);

        // 2. Parsing it with lopdf.
        let mut doc = None;
        timed("parse the whole document with lopdf",
            &mut || doc = Document::load_mem(&bytes).ok());
        let doc = doc.unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        // 3. The index, which the app has already paid for once.
        let mut indexes = None;
        timed("build the whole-document index (once)",
            &mut || indexes = Some(crate::recover::indexes_for_document(&doc)));
        let indexes = indexes.unwrap();

        // The line to edit, and a replacement one word different.
        let read = crate::recover::read_page_with(&doc, page, &indexes);
        let line = read.iter().find(|r| r.text.is_some()).expect("nothing read");
        let was = line.text.clone().unwrap();
        let now = format!("{} X", was.trim());

        // 4. Reading the page to check the line still says what it said.
        timed("read the page's lines", &mut || {
            let _ = crate::recover::lines_of(&doc, page);
        });

        // 5. Shaping the replacement.
        let mut provisioned = None;
        timed("shape the replacement", &mut || {
            provisioned = crate::provision::provision(
                Some(MYANMAR_TEXT), &now, crate::shaped::SHAPING_SIZE).ok()
        });
        let provisioned = provisioned.unwrap();

        // 6. Embedding the font, which opens the document in PDFium a second
        //    time and serialises it TWICE more.
        let mut embedded = None;
        timed("embed the font (opens + saves the doc twice)",
            &mut || embedded = crate::embed_for_shaping(&bytes, &provisioned));
        let (after_bytes, _font_id) = embedded.unwrap();
        println!("{:<44} {:>8} KB", "  (and the document is now)", after_bytes.len() / 1024);

        // 7. Parsing it again.
        timed("parse the whole document a second time", &mut || {
            let _ = Document::load_mem(&after_bytes);
        });

        // 8. The write itself: a deep clone of the object graph and a full save.
        let after = Document::load_mem(&after_bytes).unwrap();
        timed("clone the whole object graph", &mut || {
            let _ = after.clone();
        });
        timed("serialise the whole document back out", &mut || {
            let mut out = Vec::new();
            let _ = after.clone().save_to(&mut out);
        });

        // 9. The whole thing, as the app actually calls it.
        let mut produced = Vec::new();
        timed("== retype(), end to end ==", &mut || {
            produced = retype(&bytes, 0, line.y, &was, &now, MYANMAR_TEXT, Some(&indexes))
                .expect("refused");
        });

        // 10. And what the app does with the answer: a new document, and a new
        //     handle, which is a new cache key for the index.
        let mut fresh = 0u64;
        timed("open the produced bytes as a new document",
            &mut || fresh = crate::open_document_from_bytes_inner(
                produced.as_ptr(), produced.len()));
        assert_ne!(fresh, 0);

        // 11. ⚠️ THE ONE THAT REPEATS. `prepare_recovery` is called on the
        //     new handle after every edit, and the index cache is keyed by
        //     handle, so the index built in step 3 is thrown away and this is
        //     paid again for every single edit.
        let produced_doc = Document::load_mem(&produced).unwrap();
        timed("REBUILD the index for the new handle",
            &mut || { let _ = crate::recover::indexes_for_document(&produced_doc); });

        // 12. And what that rebuild BUYS. If the line the retype wrote can be
        //     read with the old index too, the rebuild is paying sixteen
        //     seconds for nothing and the index can simply be carried over.
        let rebuilt = crate::recover::indexes_for_document(&produced_doc);
        let with_old = crate::recover::read_page_with(&produced_doc, page, &indexes);
        let with_new = crate::recover::read_page_with(&produced_doc, page, &rebuilt);
        let says = |rs: &[crate::recover::Reading]| -> String {
            rs.iter()
                .find(|r| (r.y - line.y).abs() < 0.01)
                .and_then(|r| r.text.clone())
                .unwrap_or_else(|| "(unreadable)".into())
        };
        println!("\nthe retyped line, with the index carried over: {}", says(&with_old));
        println!("the retyped line, with the index rebuilt:      {}", says(&with_new));

        let readable = |rs: &[crate::recover::Reading]| rs.iter().filter(|r| r.text.is_some()).count();
        println!("lines readable, carried over {} / rebuilt {} / of {}",
            readable(&with_old), readable(&with_new), with_old.len());

        crate::close_document(fresh);
        crate::close_document(handle);
    }

    /// What THREE edits in a row do to the file, and whether the line an edit
    /// wrote can be read back without rebuilding anything.
    ///
    /// Run with
    ///   cargo test --release --lib what_repeated_edits_cost -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_repeated_edits_cost() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(MYANMAR_TEXT).exists() {
            println!("not on this machine");
            return;
        }
        let mut bytes = std::fs::read(FILE).unwrap();
        println!("the file starts at {} KB", bytes.len() / 1024);

        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);

        let read = crate::recover::read_page_with(&doc, page, &indexes);
        let first = read.iter().find(|r| r.text.is_some()).expect("nothing read");
        let baseline = first.y;
        let mut says = first.text.clone().unwrap();

        for round in 1..=3 {
            let now = format!("{} {round}", says.trim());
            let started = std::time::Instant::now();
            let Ok(out) = retype(&bytes, 0, baseline, &says, &now, MYANMAR_TEXT, Some(&indexes))
            else {
                println!("edit {round}: REFUSED");
                break;
            };
            let took = started.elapsed().as_secs_f64() * 1000.0;

            // How many fonts the file now carries, and how big it is.
            let after = Document::load_mem(&out).unwrap();
            let fonts = after
                .objects
                .values()
                .filter(|o| matches!(o, Object::Dictionary(d)
                    if d.get(b"Subtype").ok().and_then(|x| x.as_name().ok()) == Some(b"Type0")))
                .count();
            println!("edit {round}: {took:>6.1} ms, file {} KB, {fonts} Type0 fonts",
                out.len() / 1024);

            // ⚠️ AND WHAT PDFIUM MAKES OF THE LINE JUST WRITTEN. Ayaan writes
            // its runs with `/ActualText`, so a line it wrote may be readable
            // by the ordinary route and need no index at all.
            let handle = crate::open_document_from_bytes_inner(out.as_ptr(), out.len());
            if handle != 0 {
                let seen = crate::tests::texts_of(handle);
                let mine = seen.iter().find(|s| s.contains('\u{1021}'));
                println!("   PDFium reads: {:?}", mine.map(|s| s.chars().take(40).collect::<String>()));
                crate::close_document(handle);
            }

            // And what recovery makes of it with the index already in hand.
            let with_old = crate::recover::read_page_with(&after, page, &indexes);
            let back = with_old
                .iter()
                .find(|r| (r.y - baseline).abs() < 0.01)
                .and_then(|r| r.text.clone());
            println!("   recovery reads: {:?}",
                back.as_ref().map(|s| s.chars().take(40).collect::<String>()));

            match back {
                Some(text) => {
                    says = text;
                    bytes = out;
                }
                None => {
                    println!("   the line it just wrote cannot be read again, so a \
                        second edit of it is refused from here");
                    break;
                }
            }
        }
    }

    /// ⚠️ AN EDIT TOUCHES ONE PAGE AND ONE LINE OF IT. The writer hands back
    /// a whole new document because it changes the file's structure, and the
    /// question that matters is not whether the BYTES differ but whether
    /// anything else on the paper does. Every other page's drawing must come
    /// back operation for operation, and every other line of the edited page
    /// must still say what it said.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn an_edit_leaves_every_other_page_and_line_alone() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let pages: Vec<ObjectId> = doc.get_pages().values().copied().collect();
        let indexes = crate::recover::indexes_for_document(&doc);

        let edited = pages[0];
        let read = crate::recover::read_page_with(&doc, edited, &indexes);
        let line = read.iter().find(|r| r.text.is_some()).expect("nothing read");
        let was = line.text.clone().unwrap();
        let now = format!("{} X", was.trim());

        let out = retype(&bytes, 0, line.y, &was, &now, MYANMAR_TEXT, Some(&indexes))
            .expect("refused");
        let after = Document::load_mem(&out).unwrap();
        let after_pages: Vec<ObjectId> = after.get_pages().values().copied().collect();
        assert_eq!(after_pages.len(), pages.len(), "the edit changed the page count");

        // Every page but the edited one draws exactly what it drew.
        for (i, (&before, &now_page)) in pages.iter().zip(&after_pages).enumerate().skip(1) {
            let a = Content::decode(&doc.get_page_content(before)).unwrap();
            let b = Content::decode(&after.get_page_content(now_page)).unwrap();
            assert_eq!(a.operations.len(), b.operations.len(),
                "page {i} is drawn by a different number of operations now");
            for (n, (x, y)) in a.operations.iter().zip(&b.operations).enumerate() {
                assert_eq!(x.operator, y.operator,
                    "page {i} operation {n} changed");
                assert_eq!(x.operands, y.operands,
                    "page {i} operation {n} draws something else now");
            }
        }

        // And every other line of the edited page still says what it said.
        let now_read = crate::recover::read_page_with(&after, after_pages[0], &indexes);
        for r in &read {
            if (r.y - line.y).abs() < 0.01 {
                continue;
            }
            let then = now_read.iter().find(|n| (n.y - r.y).abs() < 0.01);
            assert_eq!(then.and_then(|n| n.text.clone()), r.text,
                "the line at {:.2} changed, and it was not the one being edited", r.y);
        }
    }

    /// ⚠️ AND THE EDIT SURVIVES BEING SAVED AND OPENED AGAIN. The writer works
    /// on lopdf's object graph and the app hands the result to PDFium, so the
    /// two have to agree about what was written.
    #[test]
    #[ignore = "needs a Myanmar PDF that is not in this repository"]
    fn what_an_edit_wrote_is_still_there_after_a_save_and_a_reopen() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(MYANMAR_TEXT).exists() {
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);

        let read = crate::recover::read_page_with(&doc, page, &indexes);
        let line = read.iter().find(|r| r.text.is_some()).expect("nothing read");
        let was = line.text.clone().unwrap();
        let now = format!("{} X", was.trim());

        let out = retype(&bytes, 0, line.y, &was, &now, MYANMAR_TEXT, Some(&indexes))
            .expect("refused");

        // Through PDFium and back to disk, the way the app saves.
        let handle = crate::open_document_from_bytes_inner(out.as_ptr(), out.len());
        assert_ne!(handle, 0, "the produced file would not open");
        let saved = crate::document_bytes(handle).expect("it would not save");
        crate::close_document(handle);

        let reopened = crate::open_document_from_bytes_inner(saved.as_ptr(), saved.len());
        assert_ne!(reopened, 0, "the saved file would not open");

        // ⚠️ READ BACK THROUGH PDFIUM, NOT THROUGH RECOVERY. Ayaan writes its
        // runs with `/ActualText`, so the line it wrote is ordinary readable
        // text: that is what makes a SECOND edit of it cheap, and it is the
        // thing a save has to preserve.
        let seen = crate::tests::texts_of(reopened);
        crate::close_document(reopened);

        let wanted: String = now.chars().filter(|c| !c.is_whitespace()).collect();
        let found = seen.iter().any(|s| {
            let bare: String = s.chars().filter(|c| !c.is_whitespace()).collect();
            bare.contains(&wanted[..wanted.len().min(30)])
        });
        assert!(found, "the edited line is not in the saved file: {seen:?}");
    }

    /// The app's own loop, three edits deep: prepare once, then retype, open the
    /// result, hand the reading over, close the old one, and ask whether the
    /// next edit still has an index to borrow.
    ///
    /// Run with
    ///   cargo test --release --lib the_apps_own_edit_loop -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn the_apps_own_edit_loop() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        const FONT: &str = r"C:\Windows\Fonts\Pyidaungsu.ttf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(FONT).exists() {
            println!("not on this machine");
            return;
        }
        let on_disk = std::fs::read(FILE).unwrap();

        let mut handle =
            crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
        assert_ne!(handle, 0);

        // What the app does when it first sees a Myanmar page. Synchronous here
        // so the timing is visible; the app does it on a thread.
        let started = std::time::Instant::now();
        {
            let bytes = crate::document_bytes(handle).unwrap();
            let doc = Document::load_mem(&bytes).unwrap();
            let _ = crate::indexes_for_doc(handle, &doc);
        }
        println!("prepared in {:.1} s", started.elapsed().as_secs_f64());
        println!("ready: {}", crate::recovery_is_ready(handle, 0));

        for round in 1..=3 {
            // Find a line to edit, the way the app does.
            let bytes = crate::document_bytes(handle).unwrap();
            let doc = Document::load_mem(&bytes).unwrap();
            let (_, &page) = doc.get_pages().iter().next().unwrap();
            let Some(indexes) = crate::cached_indexes(handle) else {
                println!("edit {round}: NOTHING CACHED, the retype will build its own");
                break;
            };
            let read = crate::recover::read_page_with(&doc, page, &indexes);
            let Some(line) = read
                .iter()
                .filter(|r| r.text.is_some())
                .nth(round - 1)
            else {
                println!("edit {round}: no line to edit");
                break;
            };
            let was = line.text.clone().unwrap();
            let now = format!("{} {round}", was.trim());

            // The FFI the app calls, timed: this is what the reader waits for.
            let started = std::time::Instant::now();
            let buffer = crate::retype_recovered_line(
                handle, 0, line.y as f32,
                was.as_ptr(), was.len(),
                now.as_ptr(), now.len(),
                FONT.as_ptr(), FONT.len(),
            );
            let took = started.elapsed().as_secs_f64();
            if buffer.status != crate::STATUS_OK_PDFIUM {
                println!("edit {round}: refused with {} after {took:.1} s",
                    buffer.status);
                crate::free_byte_buffer(buffer);
                break;
            }
            let produced =
                unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) }.to_vec();
            crate::free_byte_buffer(buffer);
            println!("edit {round}: retype took {took:.1} s");

            // ⚠️ AND WHAT RestoreDocumentBytes DOES, IN ITS ORDER.
            let restored =
                crate::open_document_from_bytes(produced.as_ptr(), produced.len());
            assert_ne!(restored, 0);
            crate::adopt_recovery(handle, restored);
            crate::close_document(handle);
            handle = restored;

            println!("   after handing over, ready: {}",
                crate::recovery_is_ready(handle, 0));

            // And what the app asks for next, which must cost nothing.
            let started = std::time::Instant::now();
            crate::prepare_recovery(handle, 0);
            std::thread::sleep(std::time::Duration::from_millis(300));
            println!("   prepare_recovery returned in {:.3} s, still ready: {}",
                started.elapsed().as_secs_f64(),
                crate::recovery_is_ready(handle, 0));
        }
        crate::close_document(handle);
    }

    /// Whether the bold font the reader has installed PER USER can actually set
    /// the bold lines of their page.
    ///
    /// ⚠️ WRITING AND READING WANT DIFFERENT THINGS FROM A FONT. Reading needs
    /// one whose glyphs are NUMBERED like the page's subset, and this one is
    /// not: measured, Pyidaungsu 2.5.3 proves zero lines of that page. Writing
    /// needs only correct outlines for the text being typed, because the writer
    /// embeds the face whole and shapes through it. So the question here is
    /// narrow: does it spell these words at all.
    #[test]
    #[ignore = "diagnostic, and needs fonts that are not in this repository"]
    fn whether_the_users_installed_bold_can_set_their_bold_lines() {
        let user_fonts = std::env::var("LOCALAPPDATA")
            .map(|p| std::path::PathBuf::from(p).join(r"Microsoft\Windows\Fonts"))
            .unwrap_or_default();
        let Ok(entries) = std::fs::read_dir(&user_fonts) else {
            println!("no per-user font folder");
            return;
        };
        let mut bolds: Vec<std::path::PathBuf> = entries
            .filter_map(|e| e.ok().map(|e| e.path()))
            .filter(|p| {
                p.file_name()
                    .and_then(|n| n.to_str())
                    .map(|n| n.to_lowercase())
                    .is_some_and(|n| n.contains("pyidaungsu") && n.contains("bold"))
            })
            .collect();
        bolds.sort();
        if bolds.is_empty() {
            println!("no per-user Pyidaungsu bold installed");
            return;
        }

        // The bold lines of the reader's own page, as recovery reads them.
        const LINES: [&str; 3] = [
            "\u{1026}\u{1038}\u{1005}\u{102E}\u{1038}\u{1021}\u{101B}\u{102C}\u{101B}\u{103E}\u{102D} \u{1019}\u{103C}\u{102D}\u{102F}\u{1037}\u{1015}\u{103C} \u{101B}\u{102F}\u{1036}\u{1038}",
            "\u{1006}\u{100A}\u{103A}\u{1019}\u{103C}\u{1031}\u{102C}\u{1004}\u{103A}\u{1038}\u{1014}\u{103E}\u{1004}\u{103A}\u{1037}\u{101B}\u{1031}\u{1021}\u{101E}\u{102F}\u{1036}\u{1038}\u{1001}\u{103B}\u{1019}\u{103E}\u{102F}\u{1005}\u{102E}\u{1019}\u{1036}\u{1001}\u{1014}\u{103A}\u{1037}\u{1001}\u{103D}\u{1032}\u{101B}\u{1031}\u{1038}\u{1026}\u{1038}\u{1005}\u{102E}\u{1038}\u{1013}\u{102C}\u{1014}",
            "\u{101B}\u{103E}\u{1019}\u{103A}\u{1038}\u{1015}\u{103C}\u{100A}\u{103A}\u{1014}\u{101A}\u{103A}",
        ];

        for font in &bolds {
            let path = font.to_string_lossy().into_owned();
            println!("\n{}", font.file_name().unwrap().to_string_lossy());
            for (i, line) in LINES.iter().enumerate() {
                match crate::provision::provision(
                    Some(&path), line, crate::shaped::SHAPING_SIZE)
                {
                    Ok(p) => println!("  line {i}: {} glyphs, spells it", p.glyphs.len()),
                    Err(e) => println!("  line {i}: REFUSED ({e:?})"),
                }
            }
        }

        // And the control: the regular file the writer uses today, so a failure
        // above can be told apart from these words being unspellable.
        const REGULAR: &str = r"C:\Windows\Fonts\Pyidaungsu.ttf";
        if std::path::Path::new(REGULAR).exists() {
            println!("\ncontrol, the regular file the writer uses today:");
            for (i, line) in LINES.iter().enumerate() {
                match crate::provision::provision(
                    Some(REGULAR), line, crate::shaped::SHAPING_SIZE)
                {
                    Ok(p) => println!("  line {i}: {} glyphs, spells it", p.glyphs.len()),
                    Err(e) => println!("  line {i}: REFUSED ({e:?})"),
                }
            }
        }
    }

    /// Whether a real Devanagari page needs RECOVERY at all, or whether the
    /// file already says what it says.
    ///
    /// Recovery exists because those Burmese pages carry no usable answer: the
    /// producer wrote no `/ToUnicode`, so the text had to be reconstructed from
    /// the glyphs by enumerating a language. That is the expensive, the
    /// script-specific and the fragile part of the whole pipeline. Before any
    /// of it is generalised, the question worth answering is whether Devanagari
    /// pages are in the same position or a different one.
    ///
    /// Run with
    ///   cargo test --release --lib whether_devanagari_needs_recovering -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn whether_devanagari_needs_recovering() {
        const FILES: [&str; 4] = [
            r"D:\Ayaan PDF Test file\Geeta Darshan Complete 18 Chapters.pdf",
            r"D:\Ayaan PDF Test file\003_Agyat_Ki_Aur.pdf",
            r"D:\Ayaan PDF Test file\024_Bharat_Ki_Khoj.pdf",
            r"D:\Ayaan PDF Test file\Chal Hansa Us Des.pdf",
        ];

        for file in FILES {
            if !std::path::Path::new(file).exists() {
                continue;
            }
            println!("\n=== {} ===", file.rsplit('\\').next().unwrap());

            // What PDFium extracts, which is what the ordinary writer works from.
            let bytes = std::fs::read(file).unwrap();
            let handle = crate::open_document_from_bytes(bytes.as_ptr(), bytes.len());
            if handle == 0 {
                println!("would not open");
                continue;
            }
            let pages = crate::get_page_count(handle);
            println!("{pages} pages");

            let seen = crate::tests::texts_of(handle);
            let devanagari = |s: &str| s.chars().any(|c| ('\u{0900}'..='\u{097F}').contains(&c));
            let indic = seen.iter().filter(|s| devanagari(s)).count();
            println!("page 1: {} text objects, {indic} of them carrying Devanagari",
                seen.len());
            for s in seen.iter().filter(|s| devanagari(s)).take(3) {
                println!("   “{}”", s.chars().take(48).collect::<String>());
            }
            // ⚠️ A ZERO IS WHAT AN UNREADABLE PAGE LOOKS LIKE. A producer
            // with no `/ToUnicode` hands back U+0000 per glyph, which is
            // exactly the case recovery was built for.
            let nulls: usize = seen.iter()
                .map(|s| s.chars().filter(|c| *c == '\u{0}').count())
                .sum();
            println!("page 1: {nulls} characters read back as U+0000");
            crate::close_document(handle);

            // What the page DRAWS, and in what.
            let Ok(doc) = Document::load_mem(&bytes) else {
                println!("lopdf would not load it");
                continue;
            };
            let Some((_, &page)) = doc.get_pages().iter().next() else { continue };
            let lines = crate::recover::lines_of(&doc, page);
            let mut by_font: std::collections::BTreeMap<String, usize> =
                std::collections::BTreeMap::new();
            for l in &lines {
                *by_font.entry(l.base_font.clone()).or_default() += l.glyphs.len();
            }
            println!("page 1 draws {} lines in:", lines.len());
            for (font, glyphs) in &by_font {
                println!("   {font:<16} {glyphs:>5} glyphs  readable now: {}",
                    crate::recover::can_read(font));
            }

            // ⚠️ AND WHAT THE FILE ITSELF SAYS THE FACE IS. `CIDFont+F2`
            // names no family, so the table that maps a BaseFont to an
            // installed file has nothing to match on. The embedded subset is
            // right there in the document though, and a font program carries
            // its own name.
            println!("what the embedded subsets call themselves:");
            for (_, id) in doc.objects.iter().filter_map(|(id, o)| match o {
                Object::Dictionary(d)
                    if d.get(b"Type").ok().and_then(|x| x.as_name().ok())
                        == Some(b"FontDescriptor") => Some((d.clone(), *id)),
                _ => None,
            }) {
                let Ok(Object::Dictionary(d)) = doc.get_object(id) else { continue };
                let named = |k: &[u8]| d.get(k).ok()
                    .and_then(|v| v.as_name().ok())
                    .map(|n| String::from_utf8_lossy(n).into_owned());
                let program = [&b"FontFile2"[..], b"FontFile3", b"FontFile"]
                    .iter()
                    .find_map(|k| d.get(k).ok().and_then(|v| v.as_reference().ok()));

                let mut real = String::from("-");
                if let Some(pid) = program {
                    if let Ok(stream) = doc.get_object(pid).and_then(|o| o.as_stream()) {
                        if let Ok(bytes) = stream.decompressed_content() {
                            if let Ok(face) = rustybuzz::ttf_parser::Face::parse(&bytes, 0) {
                                let mut got: Vec<String> = Vec::new();
                                for name in face.names() {
                                    if name.name_id == 1 || name.name_id == 6 {
                                        if let Some(s) = name.to_string() {
                                            if !got.contains(&s) { got.push(s); }
                                        }
                                    }
                                }
                                if !got.is_empty() { real = got.join(" / "); }
                            } else {
                                real = "(unparseable)".into();
                            }
                        }
                    }
                }
                println!("   /FontName {:<20} /FontFamily {:<14} program says: {real}",
                    named(b"FontName").unwrap_or_else(|| "-".into()),
                    named(b"FontFamily").unwrap_or_else(|| "-".into()));
            }
        }
    }

    /// Page one of a file, rendered, so a claim about how it is set can be
    /// checked by looking at it.
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_a_page_actually_looks_like() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        if !std::path::Path::new(FILE).exists() {
            println!("not on this machine");
            return;
        }
        let out_dir = std::env::temp_dir().join("ayaan-retype-probe");
        let _ = std::fs::create_dir_all(&out_dir);
        let to = out_dir.join("page.bgra").to_string_lossy().into_owned();
        raster(&std::fs::read(FILE).unwrap(), &to);
        println!("wrote {to}");
    }

    /// Page one of `pdf`, rendered, written as width, height and BGRA bytes so
    /// the result can be looked at instead of described.
    fn raster(pdf: &[u8], to: &str) {
        // The open takes the call lock itself, so taking it here first is a
        // deadlock: hold it only over the render.
        let handle = crate::open_document_from_bytes_inner(pdf.as_ptr(), pdf.len());
        assert_ne!(handle, 0, "the produced file would not open");
        let _guard = crate::call_guard();
        let doc = crate::lock(&crate::core().documents).get(&handle).cloned().unwrap();
        let g = crate::lock(&doc);
        let page = g.pages().get(0).unwrap();
        let (w, h, bgra) = crate::render_page_via_pdfium_page(&page, 1400).unwrap();
        drop(page);
        drop(g);
        drop(doc);
        let mut out = format!("{w} {h}\n").into_bytes();
        out.extend_from_slice(&bgra);
        std::fs::write(to, out).unwrap();
        drop(_guard);
        crate::close_document(handle);
    }

    /// ⚠️ NOT A REGRESSION TEST. What the writer actually leaves in the file
    /// when it retypes a line of the reader's OWN page, printed operand by
    /// operand, because reading the page back cannot see a gap dropped inside a
    /// cluster and the damage the reader reported is exactly that.
    ///
    /// Run with
    ///   cargo test --lib what_the_writer_puts_on_the_users_page -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF and fonts that are not in this repository"]
    fn what_the_writer_puts_on_the_users_page() {
        const FILE: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        const FONT: &str = MYANMAR_TEXT;
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(FONT).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(FILE).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        let indexes = crate::recover::indexes_for_document(&doc);
        let read = crate::recover::read_page_with(&doc, page, &indexes);

        // Every line the page draws, and how it is drawn, so a line made of
        // more than one text object shows up as one.
        let lines = crate::recover::lines_of(&doc, page);
        println!("\n=== the page's lines ===");
        for (i, l) in lines.iter().enumerate() {
            let says = read
                .iter()
                .find(|r| (r.y - l.y).abs() < 1e-9)
                .and_then(|r| r.text.clone());
            println!(
                "{i:>3}  y={:9.3} x={:8.3} size={:5.2} font={:<24} ops={:>3} glyphs={:>4} {}",
                l.y, l.x, l.size, l.base_font, l.drawn_by.len(), l.glyphs.len(),
                match &says { Some(t) => format!("“{t}”"), None => "-".into() }
            );
        }

        // The line the reader was editing when the damage appeared.
        let Some(target) = read.iter().find(|r| {
            r.text.as_deref().is_some_and(|t| t.starts_with("\u{1021}\u{101B}\u{103E}\u{1031}"))
        }) else {
            println!("that line is not on this page");
            return;
        };
        let was = target.text.clone().unwrap();
        println!("\n=== the line ===\ny        {:.4}", target.y);
        println!("expected “{was}”  ({} chars, ends in a space: {})",
            was.chars().count(), was.ends_with(' '));

        // ⚠️ HOW MANY LINES SIT ON THAT BASELINE. `write_over` empties the
        // operations of ONE of them; anything else on the same baseline goes on
        // drawing its old glyphs underneath the replacement.
        let sharing: Vec<&Line> = crate::recover::lines_at(&lines, target.y).collect();
        println!("lines on this baseline: {}", sharing.len());
        for l in &sharing {
            println!("   x={:8.3} ops={:>3} glyphs={:>4} adjust={:.1} drawn_by={:?}",
                l.x, l.drawn_by.len(), l.glyphs.len(), l.adjust, l.drawn_by);
            println!("   breaks {:?}",
                l.breaks.iter().map(|b| (b.at, (b.points * 10.0).round() / 10.0))
                    .collect::<Vec<_>>());
        }

        // ⚠️ WHAT THE APP SENDS, WHICH IS TRIMMED. `EditSelectedLine` trims
        // before it hands the text over, so a line recovered with a trailing
        // space is replaced by one without it.
        let now = format!("{} X", was.trim());
        println!("new      “{now}”");

        let out = retype(&bytes, 0, target.y, &was, &now, FONT, Some(&indexes))
            .expect("the retype was refused");

        // Both files on disk, so the result can be LOOKED AT rather than
        // reasoned about.
        let out_dir = std::env::temp_dir().join("ayaan-retype-probe");
        let _ = std::fs::create_dir_all(&out_dir);
        let at = |name: &str| out_dir.join(name).to_string_lossy().into_owned();
        std::fs::write(at("before.pdf"), &bytes).unwrap();
        std::fs::write(at("after.pdf"), &out).unwrap();
        println!("wrote {}", out_dir.display());
        raster(&bytes, &at("before.bgra"));
        raster(&out, &at("after.bgra"));

        let after = Document::load_mem(&out).unwrap();
        let (_, &page_after) = after.get_pages().iter().next().unwrap();
        let content = Content::decode(&after.get_page_content(page_after)).unwrap();

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

        let mut at = 0usize;
        let mut gaps: Vec<(usize, f64)> = Vec::new();
        print!("\n=== the run ===\n");
        for item in &run {
            match item {
                Object::String(b, _) => {
                    print!("[{} glyphs]", b.len() / 2);
                    at += b.len() / 2;
                }
                Object::Real(v) => {
                    print!(" {v:.1} ");
                    gaps.push((at, *v as f64));
                }
                Object::Integer(v) => {
                    print!(" {v} ");
                    gaps.push((at, *v as f64));
                }
                _ => print!("?"),
            }
        }
        println!("\ntotal {at} glyphs, {} gaps", gaps.len());

        let glyphs = crate::provision::provision(Some(FONT), &now, crate::shaped::SHAPING_SIZE)
            .unwrap()
            .glyphs;
        println!("the shaper makes {} glyphs of that text", glyphs.len());
        println!("gaps at        {:?}", gaps.iter().map(|g| g.0).collect::<Vec<_>>());
        println!("its spaces at  {:?}", spaces_in(&glyphs, &now));

        let mut edges = vec![0usize];
        for (i, g) in glyphs.iter().enumerate().skip(1) {
            if g.cluster != glyphs[i - 1].cluster {
                edges.push(i);
            }
        }
        edges.push(glyphs.len());
        for (at, _) in &gaps {
            if !edges.contains(at) {
                println!("{}  a gap at glyph {at} is INSIDE a drawn unit", "\u{26a0}");
            }
        }

        // ⚠️ AND WHAT IS LEFT DRAWING. Every operation the line was drawn by
        // should now draw nothing but the one that carries the replacement.
        println!("\n=== what the page says now ===");
        for r in crate::recover::read_page_with(&after, page_after, &indexes) {
            println!("y={:9.3} {}", r.y,
                match &r.text { Some(t) => format!("“{t}”"), None => "-".into() });
        }
    }

    /// How far the writer's flat run is from the shaping it is meant to draw.
    ///
    /// A PDF text run has no shaping in it: the viewer advances the pen by the
    /// font's own width for each glyph and draws it there. So a mark the shaper
    /// placed under its consonant is drawn at the pen instead, and the pen then
    /// moves on by the mark's nominal width. This counts, for one real line,
    /// how many glyphs that is wrong for.
    #[test]
    #[ignore = "diagnostic, and needs a font that is not in this repository"]
    fn how_much_of_the_shaping_a_flat_run_throws_away() {
        if !std::path::Path::new(MYANMAR_TEXT).exists() {
            println!("not on this machine");
            return;
        }
        const LINE: &str = "\u{1021}\u{101B}\u{103E}\u{1031}\u{1037}\u{1019}\u{102D}\u{102F}\u{1038}\u{1000}\u{102F}\u{1015}\u{103A}\u{1005}\u{1000}\u{103A}\u{101D}\u{102D}\u{102F}\u{1004}\u{103A}\u{1038}\u{1019}\u{103E} \u{1021}\u{101B}\u{102F}\u{1023}\u{103A}\u{1026}\u{1038} \u{101B}\u{1031}\u{102C}\u{1004}\u{103A}\u{1014}\u{102E}\u{101E}\u{100A}\u{103A}";

        let bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        let upem = face.units_per_em() as f64;
        let glyphs = shaped(LINE);

        let mut moved = 0usize;
        let mut raised = 0usize;
        let mut narrowed = 0usize;
        println!("\n idx   gid   shaped_adv  font_adv   x_off   y_off");
        for (i, g) in glyphs.iter().enumerate() {
            // Everything in thousandths of an em, which is what a PDF run
            // measures in.
            let k = 1000.0 / crate::shaped::SHAPING_SIZE as f64;
            let adv = g.x_advance as f64 * k;
            let xo = g.x_offset as f64 * k;
            let yo = g.y_offset as f64 * k;
            let own = face
                .glyph_hor_advance(rustybuzz::ttf_parser::GlyphId(g.id as u16))
                .map(|w| w as f64 * 1000.0 / upem)
                .unwrap_or(0.0);

            if xo.abs() > 0.5 { moved += 1; }
            if yo.abs() > 0.5 { raised += 1; }
            if (adv - own).abs() > 0.5 { narrowed += 1; }

            if xo.abs() > 0.5 || yo.abs() > 0.5 || (adv - own).abs() > 0.5 {
                println!("{i:>4} {:>5}  {adv:>10.1} {own:>9.1} {xo:>7.1} {yo:>7.1}",
                    g.id);
            }
        }
        println!("\n{} glyphs: {moved} moved sideways, {raised} raised or \
            lowered, {narrowed} drawn at a width the font does not declare",
            glyphs.len());

        // And the widths the writer actually EMBEDS, which are what a viewer
        // advances the pen by. Anything but the shaper's advance is a gap or an
        // overlap on the page.
        const HOST: &str =
            r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
        if !std::path::Path::new(HOST).exists() {
            return;
        }
        let host = std::fs::read(HOST).unwrap();
        let provisioned = crate::provision::provision(
            Some(MYANMAR_TEXT), LINE, crate::shaped::SHAPING_SIZE).unwrap();
        let (embedded, font_id) = crate::embed_for_shaping(&host, &provisioned).unwrap();
        let doc = Document::load_mem(&embedded).unwrap();
        let widths = crate::shaped::cid_widths(&doc, font_id).unwrap();

        let mut wrong = 0usize;
        let mut total_off = 0.0f64;
        println!("\n idx   gid   shaped_adv   embedded_W    difference");
        for (i, g) in glyphs.iter().enumerate() {
            let k = 1000.0 / crate::shaped::SHAPING_SIZE as f64;
            let adv = g.x_advance as f64 * k;
            let w = widths.of(g.id as u16);
            if (adv - w).abs() > 0.5 {
                wrong += 1;
                total_off += w - adv;
                println!("{i:>4} {:>5}  {adv:>10.1} {w:>12.1} {:>13.1}",
                    g.id, w - adv);
            }
        }
        println!("\n{wrong} of {} glyphs are advanced by a width the shaper did \
            not ask for, {total_off:.1} thousandths in total, which is \
            {:.2} points at 10.56 point type",
            glyphs.len(), total_off / 1000.0 * 10.56);
    }

    /// A glyph, as the shaper hands it over.
    fn glyph(id: u32, advance: f32, offset: f32) -> crate::ShapedGlyph {
        crate::ShapedGlyph {
            id,
            x_advance: advance,
            x_offset: offset,
            y_offset: 0.0,
            cluster: 0,
        }
    }

    /// Where the run's numbers are, and what they say: the glyph index each one
    /// falls before, and its value.
    fn numbers(run: &[Object]) -> Vec<(usize, f64)> {
        let mut at = 0usize;
        let mut out = Vec::new();
        for item in run {
            match item {
                Object::String(b, _) => at += b.len() / 2,
                Object::Real(v) => out.push((at, *v as f64)),
                Object::Integer(v) => out.push((at, *v as f64)),
                _ => {}
            }
        }
        out
    }

    /// ⚠️ THE BUG THE READER PHOTOGRAPHED. A PDF run carries no shaping: the
    /// viewer advances the pen by the width the FILE declares for each glyph
    /// and draws the next one there. PDFium embeds the font this writer uses,
    /// and measured on the reader's own line it gives six of the Burmese marks
    /// the default width of a FULL EM where the shaper advances them by
    /// nothing at all. Every one of those opened an em of blank page after the
    /// mark and pushed it off the letter it belongs under: `\u{1021}\u{101B}\u{103E}\u{1031}\u{1037}` came out as
    /// `\u{1021}\u{101B}\u{1031}\u{1037}` with the medial stranded to the right.
    ///
    /// So the run has to say what the shaper decided, glyph by glyph.
    #[test]
    fn a_mark_the_font_calls_an_em_wide_advances_by_the_nothing_the_shaper_asked_for() {
        // A letter of half an em, then a mark the shaper does not advance at
        // all, then another letter. The file calls the mark a full em, which is
        // what a viewer would use.
        let glyphs = [glyph(10, 500.0, 0.0), glyph(11, 0.0, 0.0), glyph(12, 500.0, 0.0)];
        let widths = crate::shaped::CidWidths::of_these(1000.0, &[(10, 500.0), (12, 500.0)]);

        let out = lay_out(&glyphs, &[], 10.0, &widths);

        // The em the file would have added after the mark is taken straight
        // back off, so the next letter is drawn against it.
        assert_eq!(numbers(&out.run), vec![(2, 1000.0)],
            "the mark was left advancing by the width the file declares");

        // And the line is as wide as the shaper said, not an em wider.
        assert!((out.advance - 10.0).abs() < 1e-9,
            "the run advances by {} points, not the 10 the shaper asked for",
            out.advance);
    }

    /// ⚠️ AND A GLYPH THE SHAPER MOVED SIDEWAYS IS DRAWN WHERE IT PUT IT.
    /// Mark attachment is a horizontal nudge as well as a vertical one, and on
    /// the reader's line fifteen of forty-three glyphs carry one.
    #[test]
    fn a_glyph_the_shaper_moved_sideways_is_drawn_where_it_put_it() {
        // The mark is nudged 40 thousandths to the LEFT of the pen.
        let glyphs = [glyph(10, 500.0, 0.0), glyph(11, 0.0, -40.0), glyph(12, 500.0, 0.0)];
        let widths = crate::shaped::CidWidths::of_these(0.0, &[(10, 500.0), (12, 500.0)]);

        let out = lay_out(&glyphs, &[], 10.0, &widths);

        // Moved there before the mark is drawn, and taken back after it, so
        // nothing downstream of the mark is shifted.
        assert_eq!(numbers(&out.run), vec![(1, 40.0), (2, -40.0)],
            "the nudge was dropped, or it was not undone");
        assert!((out.advance - 10.0).abs() < 1e-9, "{}", out.advance);
    }

    /// ⚠️ AND THE WORD SPACES STILL GO IN, on top of all of that.
    #[test]
    fn the_line_s_word_spaces_are_still_opened_where_they_were_asked_for() {
        let glyphs = [glyph(10, 500.0, 0.0), glyph(11, 500.0, 0.0)];
        let widths = crate::shaped::CidWidths::of_these(0.0, &[(10, 500.0), (11, 500.0)]);

        let out = lay_out(&glyphs, &[Break { at: 1, points: 2.0 }], 10.0, &widths);

        // 2 points at 10 point type is 200 thousandths, opened rather than
        // closed, so the number is negative.
        assert_eq!(numbers(&out.run), vec![(1, -200.0)]);
        assert!((out.advance - 12.0).abs() < 1e-9,
            "the space was not counted into the width: {}", out.advance);
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
