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
use std::collections::BTreeMap;

use crate::{
    STATUS_DOC_NOT_REWRITABLE, STATUS_FONT_UNUSABLE, STATUS_INVALID_INPUT,
    STATUS_LINE_NOT_REWRITABLE, STATUS_TOO_WIDE,
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

/// How close two baselines must be to be the same one, in PDF points. Read
/// back out of a stream that wrote them as decimals, so never exact.
const BASELINE_TOLERANCE: f64 = 0.5;

/// Below this many points, nothing on the page has visibly moved. A justified
/// line absorbs its whole difference into its own spaces and lands here.
const SETTLED: f64 = 0.05;

/// What has to move out of a replacement's way, and how far.
///
/// ⚠️ SEVERAL MOVES, NOT ONE, since the carry. Sliding the rest of a line
/// right is one move; carrying its tail down to the next line and pushing that
/// line along is two more, and a paragraph that cascades is two more again per
/// line. They are all applied to the same stream before anything is spliced
/// into it, and moving a placement rewrites its `Tm` operands and changes no
/// index, so the order between them does not matter.
#[derive(Default)]
pub(crate) struct Reflow {
    /// Each group of operations to move, as indices into the stream the
    /// replacement is about to be written into, and how far in PDF user space.
    moves: Vec<(Vec<usize>, (f64, f64))>,
}

/// The room the app says this paragraph has, on the page.
///
/// ⚠️ THE APP KNOWS THIS AND THE CORE DOES NOT. A paragraph is what the
/// reader clicked into, and the block model the app frames it with already has
/// its edges and its baselines. Two attempts to work them out down here failed
/// on the reader's own books: the Myanmar letterhead joined into one fake
/// paragraph, and the Hindi page gave no stacked paragraph at all in eight
/// pages, because down here a "line" is a placement and one visual line is 52
/// of them.
pub(crate) struct Room<'a> {
    /// Where the paragraph starts and how far right it may reach.
    pub(crate) left: f64,
    pub(crate) column: f64,
    /// The baselines UNDER the line being retyped, nearest first.
    pub(crate) below: &'a [f64],
}

/// Everything drawn to the RIGHT of `line` on the same baseline.
///
/// ⚠️ A RECOVERED LINE IS OFTEN ONE WORD. On the reader's Hindi book a
/// "line" is a single word, so what comes after it on the page is its own
/// neighbours, and they are what has to move when the replacement is a
/// different width. On the Burmese file the same call finds nothing, because
/// there a line really is the whole line. Correct both times, for one reason.
///
/// ⚠️ AND THE PAGE'S FRAME, NOT THE STREAM'S. `Line::x` is the `Tm`
/// translation as written, so two lines drawn under different transforms sort
/// against each other by nothing at all.
fn after_on_the_baseline(lines: &[Line], line: &Line) -> Vec<usize> {
    let mine = line.page_x();
    let mut showing: Vec<usize> = Vec::new();
    for other in lines {
        if std::ptr::eq(other, line) {
            continue;
        }
        if (other.page_y - line.page_y).abs() > BASELINE_TOLERANCE {
            continue;
        }
        if other.page_x() <= mine {
            continue;
        }
        showing.extend(other.drawn_by.iter().copied());
    }
    showing.sort_unstable();
    showing.dedup();
    showing
}

/// What the rest of the line has to do to make room for `replacement`.
///
/// ⚠️ ONLY WHAT THE REPLACEMENT COULD NOT ABSORB. `share` opens the new
/// text's own word spaces so the line ends where it ended, and when that works
/// there is nothing left over and nothing moves. It cannot work on a line that
/// has no spaces to open, which is every line of a book that draws one word per
/// line, and that is the case this exists for: without it a longer word is
/// simply drawn over the top of the next one.
fn reflow_for(lines: &[Line], line: &Line, replacement: &Replacement, was: Option<f64>) -> Reflow {
    let slack = was.map_or(0.0, |target| replacement.advance - target);
    if slack.abs() <= SETTLED {
        return Reflow::default();
    }
    Reflow {
        moves: vec![(after_on_the_baseline(lines, line), line.along_baseline(slack))],
    }
}

/// One placement of a line, as reflow sees it: where it will be drawn once the
/// replacement has been written, and which operations draw it.
struct Placed {
    left: f64,
    right: f64,
    drawn_by: Vec<usize>,
}

/// A separator this narrow is hung into the margin rather than indenting the
/// line it is carried to, as a fraction of the type size.
///
/// ⚠️ A WORD SPACE IS A PLACEMENT OF ITS OWN ON A REAL BOOK, and it reads as
/// nothing, so it cannot be told from a word by reading it. Measured on the
/// Geeta book: a line of 52 placements is 26 words and 26 spaces, every space
/// 2.93 points at 10.56 point type (0.277 em) and the narrowest word 5.49
/// points (0.52 em). Anything under this is a separator on that evidence.
const A_SEPARATOR: f64 = 0.4;

/// Every placement drawn on `baseline`, left to right, after `slid` has been
/// applied to everything right of the line being retyped.
fn placements_on(
    lines: &[Line],
    widths: &BTreeMap<Vec<u8>, (String, Option<crate::shaped::CidWidths>)>,
    baseline: f64,
    retyping: Option<(&Line, f64, f64)>,
) -> Option<Vec<Placed>> {
    let mut out: Vec<Placed> = Vec::new();
    for other in lines {
        if (other.page_y - baseline).abs() > BASELINE_TOLERANCE {
            continue;
        }
        let (_, Some(w)) = widths.get(&other.resource)? else { return None };
        let mut left = other.page_x();
        // The line being retyped keeps its place and takes its new width; what
        // is drawn to the right of it has already been slid out of the way.
        let mut width = other.along_baseline(crate::recover::advance_of(other, w)).0;
        if let Some((line, replacing, slid)) = retyping {
            if std::ptr::eq(other, line) {
                width = other.along_baseline(replacing).0;
            } else if other.page_x() > line.page_x() {
                left += slid;
            }
        }
        out.push(Placed { left, right: left + width, drawn_by: other.drawn_by.clone() });
    }
    out.sort_by(|a, b| a.left.partial_cmp(&b.left).unwrap_or(std::cmp::Ordering::Equal));
    Some(out)
}

/// Carries whatever no longer fits down through the paragraph, line by line.
///
/// ⚠️ WHOLE PLACEMENTS, AND NO TEXT IS READ TO DO IT. This is the whole
/// reason placement reflow exists: on the reader's Hindi book only 514 of 1,386
/// placements can be read at all, and every one of the other 872 still has a
/// width the file declares and a `Tm` that can be rewritten. A word is a
/// placement there, so moving placements moves words.
///
/// ⚠️ AND IT REFUSES RATHER THAN OVERFLOWING THE PARAGRAPH. When the last
/// baseline the app gave still cannot take what reached it, the paragraph needs
/// a line it does not have, and making one is not this. Writing anyway would
/// draw the tail of the paragraph over whatever is under it.
fn carry(
    doc: &Document,
    page: ObjectId,
    lines: &[Line],
    line: &Line,
    replacement: &Replacement,
    slid: f64,
    room: &Room,
) -> Result<Vec<(Vec<usize>, (f64, f64))>, i32> {
    let widths = crate::recover::fonts_of(doc, page);
    let mut moves: Vec<(Vec<usize>, (f64, f64))> = Vec::new();
    let mut at = line.page_y;
    let mut on_it = placements_on(lines, &widths, at, Some((line, replacement.advance, slid)))
        .ok_or(STATUS_LINE_NOT_REWRITABLE)?;

    for &next in room.below {
        let Some(first) = on_it.iter().position(|p| p.right > room.column + SETTLED) else {
            return Ok(moves);
        };
        // ⚠️ THE FIRST PLACEMENT CANNOT BE CARRIED ANYWHERE. Nothing is left
        // to hold the line, and the word is simply wider than the column.
        if first == 0 {
            return Err(STATUS_TOO_WIDE);
        }
        let carried = &on_it[first..];
        // A space that leads a carried group hangs into the margin, so the
        // line it joins starts with its first word and not with an indent.
        let hang = if carried[0].right - carried[0].left < line.size * A_SEPARATOR {
            carried[0].right - carried[0].left
        } else {
            0.0
        };
        let dx = room.left - carried[0].left - hang;
        let dy = next - at;
        moves.push((
            carried.iter().flat_map(|p| p.drawn_by.iter().copied()).collect(),
            (dx, dy),
        ));

        // What was already on the line below is pushed along to make room for
        // what has just landed on it.
        // ⚠️ AND NO GAP IS ADDED BETWEEN THEM. The space that separated the
        // carried words from the one before them is itself a placement and is
        // carried with them, so the group already ends in one. Adding another
        // would open a double space at every line this cascades through.
        let taken = carried[carried.len() - 1].right - carried[0].left - hang;
        let below = placements_on(lines, &widths, next, None)
            .ok_or(STATUS_LINE_NOT_REWRITABLE)?;
        let push = taken;
        if !below.is_empty() {
            moves.push((
                below.iter().flat_map(|p| p.drawn_by.iter().copied()).collect(),
                line.along_baseline(push),
            ));
        }

        // And the line below, as it now stands, is the one to check next.
        on_it = carried
            .iter()
            .map(|p| Placed {
                left: p.left + dx,
                right: p.right + dx,
                drawn_by: p.drawn_by.clone(),
            })
            .chain(below.into_iter().map(|p| Placed {
                left: p.left + push,
                right: p.right + push,
                drawn_by: p.drawn_by,
            }))
            .collect();
        at = next;
    }

    if on_it.iter().any(|p| p.right > room.column + SETTLED) {
        return Err(STATUS_TOO_WIDE);
    }
    Ok(moves)
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
    reflow: Reflow,
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

    // ⚠️ THE REST OF THE LINE MOVES FIRST, BEFORE THE SPLICE BELOW. Moving
    // rewrites `Tm` operands and changes no index; the splice inserts five
    // operations where one was, so every index past it means something else
    // afterwards. The reflow was collected against the stream as it is here.
    //
    // ⚠️ AND A REFLOW THAT CANNOT BE DONE EXACTLY REFUSES THE WHOLE EDIT. It
    // is only ever asked for when the replacement is a visibly different width,
    // and going ahead without it draws the new text over the top of the word
    // after it. Text the reader can see is wrong is worse than an edit that
    // declines.
    for (showing, (dx, dy)) in &reflow.moves {
        crate::shift::move_placements(&mut content, showing, *dx, *dy)?;
    }

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
    Some(crate::recover::advance_of(line, widths.as_ref()?))
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
    retype_within(bytes, page_index, baseline, expected, new_text, font_path, lent, None)
}

/// The same, told what room the paragraph has, so a replacement too long for
/// its line can be carried down through the lines under it.
///
/// ⚠️ `room` OF NONE IS EXACTLY WHAT [`retype`] ALWAYS DID: the rest of the
/// line slides and nothing else moves. Only a caller that knows the paragraph
/// can ask for more, because only it knows where the paragraph ends.
pub(crate) fn retype_within(
    bytes: &[u8],
    page_index: i32,
    baseline: f64,
    expected: &str,
    new_text: &str,
    font_path: &str,
    lent: Option<&crate::recover::Indexes>,
    room: Option<&Room>,
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
    let was = advance_of(&doc, page, line);
    let replacement = match was {
        Some(target) => {
            let gaps = share(&spaces, target - natural.advance);
            lay_out(glyphs, &gaps, line.size, &widths)
        }
        None => natural,
    };

    // And whatever the new text could not absorb, the rest of the line absorbs
    // by moving.
    let mut reflow = reflow_for(&lines, line, &replacement, was);
    if let Some(room) = room {
        let slid = reflow.moves.first().map_or(0.0, |(_, by)| by.0);
        reflow.moves.extend(carry(&doc, page, &lines, line, &replacement, slid, room)?);
    }
    write_over(&doc, page, line, replacement, reflow, font_id, new_text)
}

#[cfg(test)]
mod tests {
    use super::*;

    const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";
    const GEETA: &str =
        r"D:\Ayaan PDF Test file\Pages from Geeta Darshan Complete 18 Chapters.pdf";

    /// Every placement on each of a paragraph's baselines, left to right.
    fn paragraph_geometry(bytes: &[u8], baselines: &[f64]) -> Vec<(f64, usize, f64, f64)> {
        let doc = Document::load_mem(bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let widths = crate::recover::fonts_of(&doc, page);
        let lines = crate::recover::lines_of(&doc, page);
        baselines
            .iter()
            .map(|&y| {
                let mut n = 0usize;
                let (mut left, mut right) = (f64::MAX, f64::MIN);
                for l in lines.iter().filter(|l| (l.page_y - y).abs() < BASELINE_TOLERANCE) {
                    let Some((_, Some(w))) = widths.get(&l.resource) else { continue };
                    n += 1;
                    left = left.min(l.page_x());
                    right = right
                        .max(l.page_x() + l.along_baseline(crate::recover::advance_of(l, w)).0);
                }
                (y, n, left, right)
            })
            .collect()
    }

    /// ⚠️ THE WHOLE SCENARIO ON THE READER'S OWN BOOK: a word is replaced with
    /// a longer one, the words it displaces move down through the paragraph,
    /// and nothing ends up past the column.
    ///
    /// The control matters as much as the result. `room` of None is what the
    /// writer did before this existed, and it is run first so the overflow it
    /// leaves is on the record next to the reflow that fixes it.
    ///
    ///     cargo test --release a_longer_word_reflows_a_real_paragraph -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn a_longer_word_reflows_a_real_paragraph() {
        if !std::path::Path::new(GEETA).exists() || !std::path::Path::new(NIRMALA).exists() {
            println!("not on this machine");
            return;
        }
        let on_disk = std::fs::read(GEETA).unwrap();
        let handle = crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
        let bytes = crate::document_bytes(handle).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);

        // The paragraph as the APP frames it, which is where a paragraph comes
        // from. Nothing below reconstructs one from placements.
        let (blocks, _) = crate::page_blocks(handle, 0).expect("no blocks");
        let block = blocks
            .iter()
            .filter(|b| b.lines.len() >= 3)
            .max_by_key(|b| b.lines.len())
            .expect("no paragraph of three lines on this page");
        let baselines: Vec<f64> = block.lines.iter().map(|l| l.baseline as f64).collect();
        let left = block.lines.iter().map(|l| l.left).fold(f32::MAX, f32::min) as f64;
        let column = block.lines.iter().map(|l| l.right).fold(f32::MIN, f32::max) as f64;
        println!("a paragraph of {} lines, left {left:.2}, column {column:.2}",
            baselines.len());

        // A word on its first line that recovery can read, and that no other
        // placement on that line says: the writer finds the placement it is to
        // replace by what that placement says, and refuses a tie.
        let lines = crate::recover::lines_of(&doc, page);
        let readings = crate::recover::read_page_with(&doc, page, &indexes);
        let on_line: Vec<_> = lines
            .iter()
            .zip(&readings)
            .filter(|(l, _)| (l.page_y - baselines[0]).abs() < BASELINE_TOLERANCE)
            .collect();
        let (target, was) = on_line
            .iter()
            .find_map(|(l, r)| {
                let t = r.text.clone()?;
                let said = on_line.iter().filter(|(_, o)| o.text.as_ref() == Some(&t)).count();
                (said == 1 && t.chars().count() >= 2 && t.trim() == t).then_some((*l, t))
            })
            .expect("nothing on the paragraph's first line is readable and unambiguous");
        let now = format!("{was}{was}{was}");
        println!("replacing {was:?} with {now:?} at y {:.2}\n", target.page_y);

        // ⚠️ THE UNTOUCHED PAGE IS THE REFERENCE, NOT THE COLUMN. The block
        // model's right edge is PDFium's measurement of the ink and this
        // measures the advances the file declares, and on this page one line
        // reaches 2.34 points past the column before anything is edited at all.
        // Asking for "inside the column" would fail on a line the edit never
        // touched. What the reader can see is whether a line got WORSE.
        let untouched = paragraph_geometry(&bytes, &baselines);
        for (y, n, l, r) in &untouched {
            println!("   y {y:7.2}  {n:3} placements  {l:7.2} .. {r:7.2}");
        }
        println!();

        for (label, room) in [
            ("BEFORE ANY OF THIS (room: None)", None),
            ("WITH THE PARAGRAPH'S ROOM", Some(Room { left, column, below: &baselines[1..] })),
        ] {
            println!("---- {label}");
            let out = retype_within(
                &bytes, 0, target.page_y, &was, &now, NIRMALA, Some(&indexes), room.as_ref());
            let produced = out.unwrap_or_else(|status| {
                panic!("{label} refused with status {status}");
            });
            let mut worse = 0usize;
            for ((y, n, l, r), (_, was_n, _, was_r)) in
                paragraph_geometry(&produced, &baselines).iter().zip(&untouched)
            {
                let note = if r > &(was_r.max(column) + SETTLED) {
                    worse += 1;
                    "  FURTHER RIGHT THAN IT STARTED"
                } else {
                    ""
                };
                println!("   y {y:7.2}  {n:3} placements ({:+})  {l:7.2} .. {r:7.2}{note}",
                    *n as i64 - *was_n as i64);
            }
            match room {
                // The old behaviour is the control: without the paragraph's
                // room the longer word runs off the end of its line, and if it
                // did not there would be nothing here to fix.
                None => assert!(worse > 0, "the control did not overflow, so this proves nothing"),
                Some(_) => assert_eq!(worse, 0, "{worse} lines ended further right than they began"),
            }
            println!();
        }
        crate::close_document(handle);
    }

    /// ⚠️ AND PLACEMENT REFLOW CANNOT SERVE THE MYANMAR BOOK AT ALL. Its
    /// producer draws a whole line in ONE placement: measured on page one,
    /// 29 lines and 29 placements, the widest 461 points of text in a single
    /// `TJ`. There is no boundary inside it to break at, so there is nothing to
    /// carry and the line can only be refused.
    ///
    /// ⚠️ WHICH IS THE OPPOSITE OF THE HINDI BOOK, AND FOR THE SAME REASON.
    /// One placement per line is also a line recovery reads whole: 29 of 29
    /// there against 514 of 1,386 on the Hindi page. A book that cannot be
    /// reflowed by moving placements can be reflowed by rewriting its text, and
    /// that is the path Myanmar has to take.
    ///
    ///     cargo test --release a_myanmar_line_is_one_placement -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF and fonts that are not in this repository"]
    fn a_myanmar_line_is_one_placement() {
        const FILE: &str = r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf";
        const FONT: &str = r"C:\Windows\Fonts\Pyidaungsu.ttf";
        if !std::path::Path::new(FILE).exists() || !std::path::Path::new(FONT).exists() {
            println!("not on this machine");
            return;
        }
        let on_disk = std::fs::read(FILE).unwrap();
        let handle = crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
        let bytes = crate::document_bytes(handle).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);

        let (blocks, _) = crate::page_blocks(handle, 0).expect("no blocks");
        let block = blocks
            .iter()
            .filter(|b| b.lines.len() >= 2)
            .max_by_key(|b| b.lines.len())
            .expect("no paragraph of two lines on this page");
        let baselines: Vec<f64> = block.lines.iter().map(|l| l.baseline as f64).collect();
        let left = block.lines.iter().map(|l| l.left).fold(f32::MAX, f32::min) as f64;
        let column = block.lines.iter().map(|l| l.right).fold(f32::MIN, f32::max) as f64;
        println!("a paragraph of {} lines, left {left:.2}, column {column:.2}",
            baselines.len());
        for (y, n, l, r) in paragraph_geometry(&bytes, &baselines) {
            println!("   y {y:7.2}  {n:3} placements  {l:7.2} .. {r:7.2}");
        }

        let lines = crate::recover::lines_of(&doc, page);
        let readings = crate::recover::read_page_with(&doc, page, &indexes);
        let (target, was) = lines
            .iter()
            .zip(&readings)
            .filter(|(l, _)| (l.page_y - baselines[0]).abs() < BASELINE_TOLERANCE)
            .find_map(|(l, r)| Some((l, r.text.clone()?)))
            .expect("nothing readable on the paragraph's first line");
        let now = format!("{was}{was}");
        println!("\ndoubling the first line, {} characters to {}",
            was.chars().count(), now.chars().count());

        let room = Room { left, column, below: &baselines[1..] };
        let out = retype_within(
            &bytes, 0, target.page_y, &was, &now, FONT, Some(&indexes), Some(&room));
        match out {
            Ok(_) => panic!("the line was reflowed, so this book is no longer one placement a line"),
            Err(status) => {
                println!("refused with status {status}, which is the honest answer here");
                assert_eq!(status, STATUS_TOO_WIDE,
                    "a line with nothing to carry should say it is too wide");
            }
        }
        crate::close_document(handle);
    }

    /// ⚠️ REWRITING A REAL HINDI LINE, END TO END. Phase 1 proved the writer
    /// could FIND one; this changes one and asks the file what it says.
    ///
    /// Four things have to hold, and the last two are the ones that bite:
    ///
    /// - the file says the new text where PDFium reads it, which is what the
    ///   app shows;
    /// - there is one FEWER of the old word than there was. Its mere absence
    ///   cannot be asked for: this page carries four of the word being changed,
    ///   and a check for "the old text is gone" fails on a perfect edit;
    /// - the walker reads OUR OWN RUN back. The writer finds a line by reading
    ///   it, so a replacement it cannot read is a line that can be edited once
    ///   and never again. It could not, until the index was given the
    ///   dependent forms: this face draws `नमस्ते` as four glyphs and the last
    ///   is the `े` alone, which a table of whole clusters cannot spell;
    /// - and nothing else on the page moved.
    ///
    /// ⚠️ AND THE REST OF THE LINE MAKES ROOM. `retype` first tries to land a
    /// replacement on the old width by opening the line's own spaces, and on
    /// this book a "line" is a WORD, with no space in it to open: measured,
    /// 27.5pt replaced by 32.6pt. Every run on the page is positioned
    /// absolutely, so nothing used to shift and the longer word simply ran into
    /// its neighbour. What is asked here is the thing the reader can see: the
    /// GAP between this word and the next one along is the gap it was.
    #[test]
    #[ignore = "needs a PDF and fonts that are not in this repository"]
    fn a_real_hindi_line_can_be_rewritten() {
        if !std::path::Path::new(GEETA).exists() || !std::path::Path::new(NIRMALA).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(GEETA).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let (_, &page) = doc.get_pages().iter().next().unwrap();

        let indexes = crate::recover::indexes_for_document(&doc);
        let read = crate::recover::read_page_with(&doc, page, &indexes);

        // A line the page will let us read, and one that says something no
        // other line at its baseline says, so the writer can tell them apart.
        let mut counts: std::collections::BTreeMap<(String, String), usize> =
            std::collections::BTreeMap::new();
        for r in &read {
            if let Some(text) = &r.text {
                *counts.entry((format!("{:.1}", r.y), text.clone())).or_default() += 1;
            }
        }
        let line = read
            .iter()
            .find(|r| {
                r.text.as_ref().is_some_and(|t| {
                    t.chars().any(crate::devanagari::is_devanagari)
                        && t.chars().count() >= 3
                        && counts[&(format!("{:.1}", r.y), t.clone())] == 1
                })
            })
            .expect("no line on this page is both readable and unambiguous");
        let was = line.text.clone().unwrap();
        println!("rewriting {was:?} at y={:.1}", line.y);

        // Deliberately a different length, so nothing can pass by the old
        // positions happening to fit.
        const NOW: &str = "नमस\u{94D}ते";
        let out = retype(&bytes, 0, line.y, &was, NOW, NIRMALA, Some(&indexes))
            .expect("the writer refused a line it had just read");

        let path = std::env::temp_dir()
            .join(format!("ayaan-hindi-retyped-{}.pdf", std::process::id()));
        std::fs::write(&path, &out).unwrap();

        // What PDFium extracts, which is what the app shows.
        let (before, after) = (extracted_lines(GEETA), extracted_lines(path.to_str().unwrap()));
        assert!(after.contains(NOW), "the file does not say the new text");
        assert_eq!(
            after.matches(&was).count() + 1,
            before.matches(&was).count(),
            "exactly one of the old word should have gone"
        );

        // And the writer can find its own work, which is what lets a line be
        // edited a second time.
        let done = Document::load_mem(&out).unwrap();
        let (_, &page_after) = done.get_pages().iter().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&done);
        let ours = crate::recover::lines_of(&done, page_after)
            .into_iter()
            .find(|l| l.base_font == "NirmalaUI")
            .expect("the replacement is not on the page");
        let index = indexes.index_for(&ours.base_font).expect("our own font resolved to nothing");
        let face_bytes = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&face_bytes, 0).unwrap();
        assert_eq!(
            crate::recover::read_line(index, &face, &ours).as_deref(),
            Some(NOW),
            "the writer cannot read its own replacement"
        );

        // ⚠️ AND THE WORD AFTER IT IS STILL THE SAME DISTANCE AWAY. Asked as
        // a GAP rather than as a position, because a gap is the same number in
        // both files however wide the replacement came out, and it is what the
        // reader is looking at when they say the words ran together.
        // ⚠️ A `Reading` IS IN THE PAGE'S FRAME AND A `Line` IS IN THE
        // STREAM'S. This book draws its text under a `cm`, so the two disagree,
        // and looking the line up by `Line::y`/`Line::x` finds nothing at all.
        // It went unnoticed because the only thing that used to ask was a
        // `println!` that printed `None` and said nothing about it.
        let was_lines = crate::recover::lines_of(&doc, page);
        let mine = was_lines
            .iter()
            .find(|l| {
                (l.page_y - line.y).abs() < 0.01 && (l.page_x() - line.x).abs() < 0.01
            })
            .expect("the line being edited is not on the page");
        let now_lines = crate::recover::lines_of(&done, page_after);

        println!("   width: {:?} before, {:?} after",
            advance_of(&doc, page, mine), advance_of(&done, page_after, &ours));

        // ⚠️ PAIRED BY POSITION ALONG THE BASELINE, NOT BY WHAT THEY SAY. A
        // page repeats words, and pairing them by the glyphs they draw matched
        // one neighbour against a copy of itself 58 points away and reported
        // that as a move.
        let along = |lines: &[Line], y: f64| {
            let mut on: Vec<&Line> = lines
                .iter()
                .filter(|l| (l.page_y - y).abs() < BASELINE_TOLERANCE)
                .collect();
            on.sort_by(|a, b| a.page_x().total_cmp(&b.page_x()));
            on.into_iter().map(|l| l.page_x()).collect::<Vec<f64>>()
        };
        let before_xs = along(&was_lines, mine.page_y);
        let after_xs = along(&now_lines, ours.page_y);
        assert_eq!(before_xs.len(), after_xs.len(),
            "the baseline is drawn by a different number of runs now");

        // How far the replacement pushes, on the PAGE. The widths are in the
        // text object's own space, and this book draws under a `cm`.
        let old_width = advance_of(&doc, page, mine).expect("the old line has no width");
        let new_width = advance_of(&done, page_after, &ours).expect("the new line has no width");
        let pushed = mine.along_baseline(new_width - old_width).0;
        println!("   the replacement pushes {pushed:.2} points along the page");

        let mut moved = 0;
        for (i, (&was_x, &now_x)) in before_xs.iter().zip(&after_xs).enumerate() {
            let want = if was_x > mine.page_x() { pushed } else { 0.0 };
            println!("   run {i} at {was_x:.2} is now at {now_x:.2} (asked to move {want:.2})");
            assert!((now_x - was_x - want).abs() < 0.05,
                "the run at {was_x:.2} moved {:.2}, not {want:.2}", now_x - was_x);
            if want != 0.0 {
                moved += 1;
            }
        }
        println!("   {moved} run(s) made room, {} stayed put", before_xs.len() - moved);

        let _ = std::fs::remove_file(&path);
    }

    /// Everything PDFium reads off a file's first page, as one string.
    fn extracted_lines(path: &str) -> String {
        let c_path = std::ffi::CString::new(path).unwrap();
        let handle = crate::open_document(c_path.as_ptr());
        assert_ne!(handle, 0, "{path} will not open");
        let buffer = crate::get_page_lines(handle, 0);
        let mut bytes = vec![0u8; buffer.len];
        unsafe { std::ptr::copy_nonoverlapping(buffer.data, bytes.as_mut_ptr(), buffer.len) };
        crate::free_byte_buffer(buffer);
        crate::close_document(handle);
        String::from_utf8_lossy(&bytes).into_owned()
    }

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
            let page = *doc.get_pages().values().next().expect("no page");
            let _ = crate::indexes_for_doc(handle, &doc, page);
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
