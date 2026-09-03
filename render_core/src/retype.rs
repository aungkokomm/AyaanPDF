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
/// natural width rather than having a gap invented inside a word.
fn stretched(breaks: &[Break], by: f64) -> Vec<Break> {
    if breaks.is_empty() || by == 0.0 {
        return breaks.to_vec();
    }
    let each = by / breaks.len() as f64;
    breaks
        .iter()
        .map(|b| Break { points: (b.points + each).max(0.0), ..*b })
        .collect()
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
    let kept: Vec<Break> = line
        .breaks
        .iter()
        .copied()
        .filter(|b| b.at < glyphs.len())
        .collect();

    // Laid out once to find out how wide it comes, then again with the spaces
    // adjusted so it ends where the old line ended.
    let natural = lay_out(&glyphs, &kept, line.size, &widths);
    let replacement = match advance_of(&doc, page, line) {
        Some(target) => {
            let kept = stretched(&kept, target - natural.advance);
            lay_out(&glyphs, &kept, line.size, &widths)
        }
        None => natural,
    };
    write_over(&doc, page, line, replacement, font_id, new_text)
}
