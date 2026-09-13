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
    /// How far apart the paragraph sets its lines, when it has enough lines to
    /// say. A paragraph may only GAIN a line if this is known, because a line
    /// has to be put somewhere and there is nothing else to put it by.
    pub(crate) leading: Option<f64>,
}

/// How far apart a paragraph sets its lines.
///
/// ⚠️ THE MIDDLE GAP, NOT THE AVERAGE. A paragraph can carry one wider gap
/// where its producer nudged something, and an average quietly spreads that
/// over every line. The median is the spacing the paragraph actually uses.
fn leading_of(baselines: &[f64]) -> Option<f64> {
    let mut gaps: Vec<f64> =
        baselines.windows(2).map(|w| w[0] - w[1]).filter(|g| *g > 0.0).collect();
    if gaps.is_empty() {
        return None;
    }
    gaps.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    Some(gaps[gaps.len() / 2])
}

/// Everything the page draws below `under`, and how low the lowest of it sits.
///
/// ⚠️ THE WHOLE PAGE, NOT THE PARAGRAPH. A paragraph that gains a line takes
/// space the rest of the page was using, so everything under it goes down: the
/// next paragraph, the footer, the page number. Anything left where it was
/// would be written over.
fn below_the_paragraph(lines: &[Line], under: f64) -> (Vec<usize>, f64) {
    let mut showing: Vec<usize> = Vec::new();
    let mut lowest = f64::MAX;
    for other in lines.iter().filter(|l| l.page_y < under - BASELINE_TOLERANCE) {
        showing.extend(other.drawn_by.iter().copied());
        lowest = lowest.min(other.page_y);
    }
    showing.sort_unstable();
    showing.dedup();
    (showing, lowest)
}

/// The paragraph the reader is editing, as the app knows it.
pub(crate) struct Paragraph<'a> {
    /// Each line's baseline, the top of the paragraph first, in the page frame.
    pub(crate) baselines: &'a [f64],
    /// What each of those lines says now, and nothing where nothing read it.
    pub(crate) says: &'a [Option<String>],
    /// Which of them is being retyped.
    pub(crate) edited: usize,
    pub(crate) left: f64,
    pub(crate) column: f64,
    /// Whether `says` holds each line WHOLE, rather than one fragment of it.
    ///
    /// ⚠️ A READABLE FRAGMENT IS NOT A READABLE LINE, and the difference
    /// decides which mechanism the paragraph can use. On the Myanmar book a
    /// line is one placement, so what recovery reads of it is all of it. On the
    /// Hindi book a visual line is up to 52 placements and what reads is one
    /// word: rewrapping from that would rewrite every line of the paragraph
    /// with a single word of itself and throw the rest away.
    pub(crate) complete: bool,
    /// How many lines at the BOTTOM of it are copies the reflow just made.
    ///
    /// ⚠️ A COPY IS SOMEWHERE TO WRITE, NOT SOMETHING TO READ. A gained line
    /// is made by copying the paragraph's last one, so it SAYS what that line
    /// says and the writer can find it by that. Counting what it says as
    /// content as well would hand the rewrap the whole last line twice, and a
    /// paragraph can never be made to fit text it did not have.
    pub(crate) copied: usize,
}

impl Paragraph<'_> {
    /// Whether the words of this paragraph can be re-broken between its lines.
    ///
    /// ⚠️ WHICH IS A PROPERTY OF THE FILE, NOT OF THE SCRIPT. A rewrap moves
    /// whole words, so it needs every line from the edited one down to read
    /// whole. Measured on the reader's three books: the Myanmar body text gives
    /// one readable fragment a line with six to eight words in it, and the
    /// Hindi book gives eleven to twenty-two fragments a line and no whole line
    /// at all. So this is true for one and false for the other, and the two
    /// mechanisms divide exactly where the evidence does.
    fn can_be_rewrapped(&self) -> bool {
        self.complete && self.says[self.edited..].iter().all(Option::is_some)
    }
}

/// What has to happen to the lines of a paragraph to take a longer word.
enum Refill {
    /// It already fits. This is the ordinary single-line write.
    Fits,
    /// Each line's new text, the whole paragraph, top first.
    Rewrapped(Vec<String>),
    /// The words need a line the paragraph has not got.
    WontFit,
    /// Nothing here could be measured, so nothing here may decide anything.
    Unmeasurable,
}

/// How wide `text` is set in `font_bytes` at `size`, in PDF user space.
fn width_of(font_bytes: &[u8], text: &str, size: f64) -> Option<f64> {
    let glyphs = crate::shape_run(font_bytes, text, crate::shaped::SHAPING_SIZE)?;
    Some(crate::shaped_width(&glyphs) as f64 * size / crate::shaped::SHAPING_SIZE as f64)
}

/// What each line of the paragraph has to become once `new_text` is on the
/// edited one.
///
/// ⚠️ MEASURED IN THE FONT THE REPLACEMENT WILL BE SET IN, not the page's. A
/// line this rewrites is rewritten in the font being embedded, so its width is
/// that font's width. Measuring with the page's subset would answer a question
/// about text that is about to stop existing.
fn refill(
    doc: &Document,
    page: ObjectId,
    para: &Paragraph,
    new_text: &str,
    font_bytes: &[u8],
) -> Refill {
    let lines = crate::recover::lines_of(doc, page);
    let size_at = |y: f64| -> Option<f64> {
        lines.iter().find(|l| (l.page_y - y).abs() < BASELINE_TOLERANCE).map(|l| l.size)
    };

    let mut wanted: Vec<String> =
        para.says.iter().map(|s| s.clone().unwrap_or_default()).collect();
    for line in wanted.iter_mut().rev().take(para.copied) {
        line.clear();
    }
    wanted[para.edited] = new_text.to_string();

    let mut measurable = true;
    let fits = |i: usize, text: &str| -> bool {
        let Some(size) = size_at(para.baselines[i]) else { return false };
        let Some(width) = width_of(font_bytes, text, size) else { return false };
        para.left + width <= para.column + SETTLED
    };
    for (i, text) in wanted.iter().enumerate().skip(para.edited) {
        // ⚠️ AN EMPTY LINE IS MEASURABLE AND ITS WIDTH IS NOTHING. A line
        // the reflow has just made carries no words yet, and a shaper handed no
        // text answers nothing, which marked the whole paragraph unmeasurable
        // and quietly turned the rewrap back into a one-line write.
        if size_at(para.baselines[i]).is_none()
            || (!text.is_empty() && width_of(font_bytes, text, 1.0).is_none())
        {
            measurable = false;
        }
    }
    if !measurable {
        return Refill::Unmeasurable;
    }

    // ⚠️ AN EDIT THAT STILL FITS IS NOT REWRAPPED. A greedy refill breaks
    // lines where IT would break them, which is not always where the producer
    // did, so running it on a paragraph that did not need it would reflow the
    // whole thing under the reader for the sake of one letter.
    if fits(para.edited, &wanted[para.edited]) {
        return Refill::Fits;
    }

    match crate::block::rewrap_from(&wanted, para.edited, fits) {
        Some(out) => Refill::Rewrapped(out),
        None => Refill::WontFit,
    }
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

/// What a page declares for the fonts it draws with, keyed by resource name.
type Widths = BTreeMap<Vec<u8>, (String, Option<crate::shaped::CidWidths>)>;

/// What each of a page's fonts claims its codes say, keyed by resource name.
type Claims = BTreeMap<Vec<u8>, BTreeMap<u16, String>>;

/// Characters a line may not begin with, because they belong to the word
/// before them.
///
/// ⚠️ FOUND THE HARD WAY, ON THE READER'S OWN PARAGRAPH: a carry split a line
/// between a word and the comma after it, and the next line opened with
/// ", था नहीं!". The recovery reader could not see it, the comma read as
/// nothing, but the producer's `/ToUnicode` names it plainly: `000F=","` in the
/// fonts that draw the page's spaces and punctuation, and `0361="\u{964}"` in the
/// Devanagari ones.
const BELONGS_TO_THE_WORD_BEFORE: &str =
    ",.;:!?)]}\u{bb}\u{201d}\u{2019}\u{2026}\u{964}\u{965}\u{104a}\u{104b}";

/// Whether a line may begin with this placement.
fn may_open_a_line(p: &Placed) -> bool {
    !p.claims.trim_start().starts_with(|c: char| BELONGS_TO_THE_WORD_BEFORE.contains(c))
}

/// Operands as PDF reals.
fn reals(v: &[f64]) -> Vec<Object> {
    v.iter().map(|n| Object::Real(*n as f32)).collect()
}

/// A PDF number, whichever way the file wrote it.
fn number_in(o: &Object) -> Option<f64> {
    match o {
        Object::Real(r) => Some(*r as f64),
        Object::Integer(i) => Some(*i as f64),
        _ => None,
    }
}

/// A distance already measured ON THE PAGE, as a page displacement along this
/// line's baseline.
///
/// ⚠️ NOT [`Line::along_baseline`], WHICH TAKES A TEXT-SPACE ADVANCE. That one
/// scales as well as turns, and everything reflow measures from a `Placed` is
/// in page points already. Passing one to it scaled the page down a second
/// time: on the Geeta book, whose `cm` is 0.75, a line that had to move over by
/// 39.81 points moved 29.86, and the words carried onto it landed on top of the
/// ones already there.
fn along_the_page(line: &Line, distance: f64) -> (f64, f64) {
    let (x, y) = line.along_baseline(1.0);
    let unit = (x * x + y * y).sqrt();
    if unit <= f64::EPSILON {
        return (distance, 0.0);
    }
    (x / unit * distance, y / unit * distance)
}

/// One placement of a line, as reflow sees it: where it will be drawn once the
/// replacement has been written, and which operations draw it.
struct Placed {
    left: f64,
    right: f64,
    drawn_by: Vec<usize>,
    /// What the font claims these glyphs say. Empty where it claims nothing.
    ///
    /// ⚠️ A CLAIM, NOT A READING. It cannot spell a Devanagari word on this
    /// book and is never asked to: it is asked only whether a placement is a
    /// space or a mark of punctuation, which is where a line may be broken.
    claims: String,
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
    widths: &Widths,
    baseline: f64,
    retyping: Option<(&Line, f64, f64)>,
    claims: &Claims,
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
        let said: String = claims
            .get(&other.resource)
            .map(|map| other.glyphs.iter().filter_map(|g| map.get(g).cloned()).collect())
            .unwrap_or_default();
        out.push(Placed {
            left,
            right: left + width,
            drawn_by: other.drawn_by.clone(),
            claims: said,
        });
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

    // ⚠️ A LINE CAN ONLY BE BROKEN WHERE THE PAGE RE-PLACES ITS PEN. One `Tm`
    // can draw several placements, and moving one of them means rewriting that
    // `Tm`, which moves the others too. `move_placements` refuses such a set
    // rather than dragging a neighbour along, so a split chosen anywhere else
    // fails the whole edit: measured on the Geeta book, a four-line paragraph
    // whose carry landed mid-group refused with status 10.
    let Ok(content) = Content::decode(&doc.get_page_content(page)) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let pen = crate::shift::placements(&content);
    // ⚠️ A SET IS ONLY MOVABLE IF THE PENS THAT DRAW IT DRAW NOTHING ELSE.
    // This used to force the set closed instead, adding whatever else those
    // pens drew, and that quietly dragged text off other lines: the reader's
    // own page came back with a word from the line above sitting nine points
    // into the left margin, because it shared a `Tm` with a word being carried
    // down and went along with it. Whole placements or none, and the same
    // answer `move_placements` gives.
    let ops_of = |ps: &[Placed]| -> Vec<usize> {
        ps.iter().flat_map(|p| p.drawn_by.iter().copied()).collect()
    };
    let closed = |ops: &[usize]| -> bool {
        let tms: std::collections::BTreeSet<usize> =
            ops.iter().filter_map(|at| pen.get(at).map(|(tm, _)| *tm)).collect();
        let mine: std::collections::BTreeSet<usize> = ops.iter().copied().collect();
        // Every one of them placeable, and nothing else drawn by those pens.
        ops.iter().all(|at| pen.contains_key(at))
            && pen.iter().all(|(at, (tm, _))| !tms.contains(tm) || mine.contains(at))
    };
    // ⚠️ AND THE SPLIT MOVES UP THE LINE UNTIL THE TAIL IS ONE OF THOSE SETS.
    // A line can only be broken where the page re-places its pen, and the pen
    // it shares may belong to a placement on another line entirely, which no
    // amount of looking along this one would find.
    let breakable = |on_it: &[Placed], first: usize| -> usize {
        let mut at = first;
        while at > 0 && !closed(&ops_of(&on_it[at..])) {
            at -= 1;
        }
        at
    };

    let mut moves: Vec<(Vec<usize>, (f64, f64))> = Vec::new();
    let mut at = line.page_y;
    let claims = crate::recover::claims_of(doc, page);
    let mut on_it =
        placements_on(lines, &widths, at, Some((line, replacement.advance, slid)), &claims)
            .ok_or(STATUS_LINE_NOT_REWRITABLE)?;

    // ⚠️ COMPUTED ONCE, FROM THE PAGE AS IT STANDS. Every line the paragraph
    // gains pushes this same set down by one more leading, and the deltas add
    // up on the way through. Asking again after a push would ask about
    // positions that no longer exist and miss whatever had moved across the
    // boundary in the meantime.
    let (under, mut gained) = (*room.below.last().unwrap_or(&line.page_y), 0usize);
    let (pushed, lowest) = below_the_paragraph(lines, under);

    let mut queue = room.below.to_vec();
    loop {
        let Some(over) = on_it.iter().position(|p| p.right > room.column + SETTLED) else {
            return Ok(moves);
        };
        // ⚠️ TWO RULES FOR WHERE A LINE MAY BREAK, AND EITHER CAN MOVE THE
        // OTHER. The tail must be a set whose pens draw nothing else, and the
        // next line may not open with punctuation that belongs to the word
        // before it. Both only ever move the split earlier, so this settles.
        let mut first = over;
        loop {
            let mut next = breakable(&on_it, first);
            while next > 0 && !may_open_a_line(&on_it[next]) {
                next -= 1;
            }
            if next == first {
                break;
            }
            first = next;
        }
        // ⚠️ THE FIRST PLACEMENT CANNOT BE CARRIED ANYWHERE. Nothing is left
        // to hold the line, and the word is simply wider than the column. The
        // same answer covers a line the page draws with a single pen: there is
        // no point along it where its text may be parted.
        if first == 0 {
            return Err(STATUS_TOO_WIDE);
        }
        // The next line down, and one is made when the paragraph has run out.
        let (next, fresh) = match queue.is_empty() {
            false => (queue.remove(0), false),
            true => {
                let Some(leading) = room.leading else { return Err(STATUS_TOO_WIDE) };
                // ⚠️ AND NOTHING IS PUSHED OFF THE BOTTOM OF THE PAGE. There
                // is nowhere below the last line for the last line to go, and
                // moving text past the edge loses it as surely as deleting it.
                // Repaginating is a different feature.
                if lowest < f64::MAX && lowest - leading * (gained + 1) as f64 <= 0.0 {
                    return Err(STATUS_TOO_WIDE);
                }
                if !closed(&pushed) {
                    return Err(STATUS_LINE_NOT_REWRITABLE);
                }
                moves.push((pushed.clone(), (0.0, -leading)));
                gained += 1;
                (at - leading, true)
            }
        };

        let carried = &on_it[first..];
        // A space that leads a carried group hangs into the margin, so the
        // line it joins starts with its first word and not with an indent.
        // ⚠️ AND THE TYPE SIZE ON THE PAGE, for the same reason: `size` is
        // what the `Tf` says, and a placement's width here has been through the
        // page's transform already.
        let em = line.along_baseline(line.size).0.abs();
        // ⚠️ BY WHAT IT CLAIMS WHERE THE FONT SAYS, BY WIDTH ONLY WHERE IT
        // DOES NOT. A space is a placement of its own on this book and its
        // font names it `0003=" "`; the width rule guessed, and a comma is as
        // narrow as a space.
        let a_space = match carried[0].claims.as_str() {
            "" => carried[0].right - carried[0].left < em * A_SEPARATOR,
            said => said.trim().is_empty(),
        };
        let hang = if a_space {
            carried[0].right - carried[0].left
        } else {
            0.0
        };
        let dx = room.left - carried[0].left - hang;
        let dy = next - at;
        moves.push((ops_of(carried), (dx, dy)));

        // What was already on the line below is pushed along to make room for
        // what has just landed on it.
        // ⚠️ AND NO GAP IS ADDED BETWEEN THEM. The space that separated the
        // carried words from the one before them is itself a placement and is
        // carried with them, so the group already ends in one. Adding another
        // would open a double space at every line this cascades through.
        let taken = carried[carried.len() - 1].right - carried[0].left - hang;
        // ⚠️ A LINE THE PARAGRAPH JUST GAINED IS EMPTY BY CONSTRUCTION, and
        // asking the page about it would find whatever used to be drawn there
        // and has already been moved out of the way.
        let below = match fresh {
            true => Vec::new(),
            false => placements_on(lines, &widths, next, None, &claims)
                .ok_or(STATUS_LINE_NOT_REWRITABLE)?,
        };
        let push = taken;
        if !below.is_empty() {
            let shifting = ops_of(&below);
            if !closed(&shifting) {
                return Err(STATUS_LINE_NOT_REWRITABLE);
            }
            moves.push((shifting, along_the_page(line, push)));
        }

        // And the line below, as it now stands, is the one to check next.
        on_it = carried
            .iter()
            .map(|p| Placed {
                left: p.left + dx,
                right: p.right + dx,
                drawn_by: p.drawn_by.clone(),
                claims: p.claims.clone(),
            })
            .chain(below.into_iter().map(|p| Placed {
                left: p.left + push,
                right: p.right + push,
                drawn_by: p.drawn_by,
                claims: p.claims,
            }))
            .collect();
        at = next;
    }
}

/// Takes every placement that moved to another line out of the place it was
/// drawn in the stream and draws it again at the end, above everything else.
///
/// ⚠️ STREAM ORDER IS PAINT ORDER, AND A LINE'S BACKGROUND IS PAINTED FIRST.
/// The reader's Hindi book puts a full-width path under every line, drawn just
/// before that line's text. A word carried down to the next line kept its old
/// place in the stream, which is BEFORE the next line's path, so the path was
/// painted straight over it: its position was exactly right, the content
/// stream said so, and on the reader's screen it simply was not there. PDFium,
/// asked object by object, had the carried words at the margin with line two's
/// background path one object later.
///
/// ⚠️ EMPTIED IN PLACE AND DRAWN AGAIN AT THE END, so no index moves. The same
/// pattern the line being retyped already uses, and the reason the splice after
/// this can still trust every index it holds.
///
/// ⚠️ AND ONLY WHERE THE END OF THE STREAM IS UNDER THE SAME TRANSFORM. A copy
/// appended after the last operation is drawn with whatever `cm` is in force
/// there. So the copy applies again every `cm` a still-open scope had applied
/// where its text object began, and this refuses only when a top-level `cm`
/// comes later, which would move the copy somewhere else entirely.
fn drawn_on_top(content: &mut Content, moved: &[usize]) -> Result<(), i32> {
    let mine: std::collections::BTreeSet<usize> = moved.iter().copied().collect();

    // ⚠️ WHAT DECIDES THE TRANSFORM IS WHICH `cm`s ARE IN FORCE, NOT HOW
    // DEEP IN `q` AN OPERATION SITS. This first asked for depth zero and
    // refused the reader's own page: its invisible text layer is drawn one `q`
    // deep, under a `q` that sets no transform of its own, so a copy at the end
    // of the stream is drawn exactly where it was. What does move a copy is a
    // `cm` applied inside a scope that is still open, or a top-level one that
    // arrives later.
    //
    // ⚠️ AND THE COLOUR AND THE FONT IN FORCE GO WITH IT. Both are graphics
    // state, both can be set before the text object opens, and a copy that
    // left them behind would be drawn in whatever was set last on the page.
    //
    // ⚠️ AND A SCOPED `cm` IS NOT A REASON TO REFUSE, IT IS PART OF THE COPY.
    // The reader's page draws some of its words as
    // `q rg q Q cm BT … ET BT … ET Q`: a transform set once, inside a scope,
    // for several text objects after it. Walking back from a `BT` to find "its"
    // `q` found the first object's and refused the second. What a copy needs is
    // simply every `cm` still in force where its text object opened, applied
    // again, in order, inside the copy's own `q`.
    type InForce = (Vec<usize>, Option<Operation>, Option<Operation>);
    let mut scopes: Vec<InForce> = Vec::new();
    let mut top: InForce = (Vec::new(), None, None);
    let mut at_text: BTreeMap<usize, InForce> = BTreeMap::new();
    for (at, op) in content.operations.iter().enumerate() {
        match op.operator.as_str() {
            "q" => {
                let current = scopes.last().unwrap_or(&top);
                let (fill, font) = (current.1.clone(), current.2.clone());
                scopes.push((Vec::new(), fill, font));
            }
            "Q" => {
                scopes.pop();
            }
            "cm" => scopes.last_mut().unwrap_or(&mut top).0.push(at),
            "rg" | "g" | "k" | "sc" | "scn" => {
                scopes.last_mut().unwrap_or(&mut top).1 = Some(op.clone())
            }
            "Tf" => scopes.last_mut().unwrap_or(&mut top).2 = Some(op.clone()),
            "BT" => {
                let current = scopes.last().unwrap_or(&top);
                let (fill, font) = (current.1.clone(), current.2.clone());
                let cms = scopes.iter().flat_map(|s| s.0.iter().copied()).collect();
                at_text.insert(at, (cms, fill, font));
            }
            _ => {}
        }
    }
    let top_level_cm = top.0;

    let mut redraw: Vec<Operation> = Vec::new();
    let mut done: std::collections::BTreeSet<usize> = std::collections::BTreeSet::new();
    for &at in moved {
        if done.contains(&at) {
            continue;
        }
        if !matches!(content.operations[at].operator.as_str(), "TJ" | "Tj" | "'" | "\"") {
            continue;
        }
        let opens_text = (0..at)
            .rev()
            .find(|i| content.operations[*i].operator == "BT")
            .ok_or(STATUS_LINE_NOT_REWRITABLE)?;
        let closes_text = (at..content.operations.len())
            .find(|i| content.operations[*i].operator == "ET")
            .ok_or(STATUS_LINE_NOT_REWRITABLE)?;
        if top_level_cm.iter().any(|c| *c > at) {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        }
        let (cms, fill, font) = &at_text[&opens_text];

        redraw.push(Operation::new("q", vec![]));
        // What was in force when the text object opened; its own settings,
        // copied after these, still win.
        redraw.extend(cms.iter().map(|cm| content.operations[*cm].clone()));
        redraw.extend(fill.iter().cloned());
        redraw.extend(font.iter().cloned());
        for i in opens_text..=closes_text {
            let op = &content.operations[i];
            // Somebody else's text in the same object stays where it was.
            if matches!(op.operator.as_str(), "TJ" | "Tj" | "'" | "\"") {
                if !mine.contains(&i) {
                    continue;
                }
                done.insert(i);
            }
            redraw.push(op.clone());
        }
        redraw.push(Operation::new("Q", vec![]));
    }

    // Emptied rather than removed, so every other operation keeps its index.
    for &at in &done {
        content.operations[at] = Operation::new("TJ", vec![Object::Array(Vec::new())]);
    }
    content.operations.extend(redraw);
    Ok(())
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

    // ⚠️ AND WHAT WENT TO ANOTHER LINE IS DRAWN ABOVE THAT LINE'S BACKGROUND.
    // See `drawn_on_top`. Only vertical moves: sliding along its own line keeps
    // a word over its own line's background, where it always was.
    let vertical: Vec<usize> = reflow
        .moves
        .iter()
        .filter(|(_, (_, dy))| dy.abs() > SETTLED)
        .flat_map(|(showing, _)| showing.iter().copied())
        .filter(|at| !line.drawn_by.contains(at))
        .collect();
    if !vertical.is_empty() {
        drawn_on_top(&mut content, &vertical)?;
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
///
/// ⚠️ AND IT READS THE LINE THE WAY THE PAGE READER DID, which means it
/// needs the widths the page declares. See `recover::read_line`: without them a
/// line carrying an undrawable code is shown to the reader and then refused to
/// the writer.
///
/// ⚠️ AND A WORD THAT APPEARS TWICE ON ITS LINE NEEDS `at` TO BE EDITED AT
/// ALL. A baseline and a word do not name a placement on a book that draws one
/// word per placement: measured on the Geeta page, 103 of its 514 readable
/// placements have a twin on their own line, and one line of prose carries two
/// each of four different words. Refusing all of them is right when there is
/// nothing to choose by, and `at` is that something: where the reader clicked,
/// in PDF user space.
fn the_one_that_says<'a>(
    lines: &'a [Line],
    baseline: f64,
    expected: &str,
    indexes: &crate::recover::Indexes,
    face: &rustybuzz::Face,
    widths: &Widths,
    at: Option<f64>,
) -> Option<&'a Line> {
    let mut said: Vec<&Line> = Vec::new();
    for line in crate::recover::lines_at(lines, baseline) {
        let Some(index) = indexes.index_for(&line.base_font) else { continue };
        let mine = widths.get(&line.resource).and_then(|(_, w)| w.as_ref());
        if crate::recover::read_line(index, face, line, mine).as_deref() == Some(expected) {
            said.push(line);
        }
    }
    match (said.len(), at) {
        (1, _) => said.into_iter().next(),
        (0, _) | (_, None) => None,
        // ⚠️ NEAREST, AND ONLY IF IT IS REALLY NEAR. A hint that lands
        // nowhere close to any of them is a stale selection, and picking the
        // least distant of several wrong answers would edit a word the reader
        // is not looking at.
        (_, Some(at)) => {
            let near = said
                .into_iter()
                .min_by(|a, b| {
                    (a.page_x() - at).abs().total_cmp(&(b.page_x() - at).abs())
                })?;
            ((near.page_x() - at).abs() <= near.size * 2.0).then_some(near)
        }
    }
}

fn says_it(
    lines: &[Line],
    baseline: f64,
    expected: &str,
    indexes: &crate::recover::Indexes,
    face: &rustybuzz::Face,
    widths: &Widths,
    at: Option<f64>,
) -> bool {
    the_one_that_says(lines, baseline, expected, indexes, face, widths, at).is_some()
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
    retype_within(bytes, page_index, baseline, expected, new_text, font_path, lent, None, None)
}

/// Gives the paragraph one more line to write into, and moves the page below it
/// down to make the space.
///
/// ⚠️ THE NEW LINE IS A COPY OF THE LAST ONE, NOT SOMETHING DRAWN FROM
/// NOTHING. A text object carries a font, a size, a rendering mode and whatever
/// transform is in force over it, and inventing all of that again for a fresh
/// object is inventing four chances to differ from the paragraph it joins.
/// Copying the paragraph's own last line and moving the copy down by one
/// leading gets every one of them right by construction. The copy says what the
/// line it came from says, so the caller's rewrap simply writes over both.
fn with_another_line(
    doc: &Document,
    page: ObjectId,
    lines: &[Line],
    last: &Line,
    leading: f64,
) -> Result<Vec<u8>, i32> {
    let Ok(mut content) = Content::decode(&doc.get_page_content(page)) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let pen = crate::shift::placements(&content);

    // Everything under the paragraph goes down, so the strip under it is free.
    let (under, _) = below_the_paragraph(lines, last.page_y);
    let tms: std::collections::BTreeSet<usize> =
        under.iter().filter_map(|at| pen.get(at).map(|(tm, _)| *tm)).collect();
    let under: Vec<usize> = pen
        .iter()
        .filter(|(_, (tm, _))| tms.contains(tm))
        .map(|(at, _)| *at)
        .collect();
    crate::shift::move_placements(&mut content, &under, 0.0, -leading)?;

    // ⚠️ THE COPY IS BUILT, NOT SLICED OUT. Several lines commonly live in
    // one `BT`/`ET`, so copying that span would draw all of them a second time:
    // measured, the Myanmar book wraps a whole paragraph in one. What is copied
    // instead is the state the page had set by the time it drew this line, plus
    // this line's own placement and text, in the order they appear.
    let (Some(&first), Some(&end)) = (last.drawn_by.first(), last.drawn_by.last()) else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };
    let opens = (0..=first)
        .rev()
        .find(|at| content.operations[*at].operator == "BT")
        .ok_or(STATUS_LINE_NOT_REWRITABLE)?;
    let (_, ctm) = *pen.get(&first).ok_or(STATUS_LINE_NOT_REWRITABLE)?;
    let (de, df) = ctm.undo(0.0, -leading).ok_or(STATUS_LINE_NOT_REWRITABLE)?;
    let mine: std::collections::BTreeSet<usize> = last.drawn_by.iter().copied().collect();

    // ⚠️ EVERY PEN THAT PLACES OUR OWN TEXT, NOT JUST THE FIRST. A line the
    // reader sees as one placement is often several runs, and the GAPS between
    // where each was placed are what its spaces are made of. Keeping only the
    // first pen drew all the runs end to end: the copy came back saying the
    // line with every space missing, and the writer could no longer find it.
    let ours: std::collections::BTreeSet<usize> =
        mine.iter().filter_map(|at| pen.get(at).map(|(tm, _)| *tm)).collect();
    // And if one of those pens also places somebody else's text, this line
    // cannot be copied on its own at all.
    for (at, (tm, _)) in &pen {
        if ours.contains(tm) && !mine.contains(at) {
            return Err(STATUS_LINE_NOT_REWRITABLE);
        }
    }

    let mut copy: Vec<Operation> = vec![Operation::new("BT", vec![])];
    for at in opens..=end {
        let op = &content.operations[at];
        let m: Vec<f64> = op.operands.iter().filter_map(number_in).collect();
        match op.operator.as_str() {
            // Somebody else's text, and the pen that placed it.
            "TJ" | "Tj" if !mine.contains(&at) => continue,
            "Tm" | "Td" | "TD" if !ours.contains(&at) => continue,
            // The pens that place ours, each one line further down the page.
            "Tm" if m.len() == 6 => copy.push(Operation::new("Tm", reals(
                &[m[0], m[1], m[2], m[3], m[4] + de, m[5] + df]))),
            "Td" | "TD" if m.len() == 2 => copy.push(Operation::new(
                &op.operator.clone(), reals(&[m[0] + de, m[1] + df]))),
            // ⚠️ AND NOBODY'S LINE ADVANCE. `T*` and its relatives move the
            // pen relative to a text line this copy does not reproduce.
            "T*" | "'" | "\"" | "BT" | "ET" => continue,
            // Everything else is state the page had set by then: the font, the
            // size, the rendering mode, the colour.
            _ => copy.push(op.clone()),
        }
    }
    if !copy.iter().any(|op| matches!(op.operator.as_str(), "Tm" | "Td" | "TD")) {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    }
    copy.push(Operation::new("ET", vec![]));
    content.operations.extend(copy);

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

/// Retypes one line of a paragraph and reflows the paragraph to take it.
///
/// ⚠️ THE FILE CHOOSES THE MECHANISM, NOT THE SCRIPT. There are two ways to
/// make room for a longer word and each of the reader's books can use exactly
/// one of them:
///
/// - a paragraph whose lines READ WHOLE is rewrapped, moving words between
///   lines and rewriting the lines that changed. The Myanmar body text gives
///   one readable fragment a line with six to eight words in it;
/// - a paragraph whose lines are MANY PLACEMENTS has those placements carried
///   down instead, which reads nothing. The Hindi book gives eleven to
///   twenty-two fragments a line, most of them unreadable, and one word is one
///   placement.
///
/// The two conditions are near enough opposites, because a line drawn in one
/// placement is a line recovery reads whole. Nothing here asks what language
/// the text is in.
pub(crate) fn retype_in_paragraph(
    bytes: &[u8],
    page_index: i32,
    para: &Paragraph,
    new_text: &str,
    font_path: &str,
    lent: Option<&crate::recover::Indexes>,
    at: Option<f64>,
) -> Result<Vec<u8>, i32> {
    retype_in_paragraph_within(
        bytes, page_index, para, new_text, font_path, lent, para.baselines.len(), at)
}

/// The same, remembering how many lines the paragraph started with so a
/// paragraph that grows can be stopped growing.
fn retype_in_paragraph_within(
    bytes: &[u8],
    page_index: i32,
    para: &Paragraph,
    new_text: &str,
    font_path: &str,
    lent: Option<&crate::recover::Indexes>,
    growing: usize,
    at: Option<f64>,
) -> Result<Vec<u8>, i32> {
    if para.baselines.len() != para.says.len() || para.edited >= para.baselines.len() {
        return Err(STATUS_INVALID_INPUT);
    }
    let on = para.baselines[para.edited];
    let Some(was) = para.says[para.edited].as_deref() else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };

    // ⚠️ BUILT ONCE FOR ALL OF IT. Reshaping a font into an index is about
    // twenty seconds, and a rewrap writes several lines of the same document.
    // Letting each write build its own paid for one answer once per line.
    let held;
    let indexes = match lent {
        Some(ready) => ready,
        None => match Document::load_mem(bytes) {
            Ok(doc) => {
                held = crate::recover::indexes_for_document(&doc);
                &held
            }
            Err(_) => return Err(STATUS_DOC_NOT_REWRITABLE),
        },
    };

    let room = Room {
        left: para.left,
        column: para.column,
        below: &para.baselines[para.edited + 1..],
        leading: leading_of(para.baselines),
    };
    let carry_instead = || {
        retype_within(
            bytes, page_index, on, was, new_text, font_path, Some(indexes), Some(&room), at)
    };

    if !para.can_be_rewrapped() {
        return carry_instead();
    }

    let (Ok(font_bytes), Ok(doc)) = (std::fs::read(font_path), Document::load_mem(bytes)) else {
        return carry_instead();
    };
    let Some(&page) = doc.get_pages().values().nth(page_index as usize) else {
        return Err(STATUS_INVALID_INPUT);
    };

    let rewrapped = match refill(&doc, page, para, new_text, &font_bytes) {
        Refill::Rewrapped(lines) => lines,
        // Nothing to reflow, so this is the write it always was.
        Refill::Fits | Refill::Unmeasurable => {
            return retype_within(
                bytes, page_index, on, was, new_text, font_path, Some(indexes), None, at);
        }
        // ⚠️ OR THE PARAGRAPH TAKES ANOTHER LINE AND TRIES AGAIN. The copy
        // says what the line it was copied from says, so the paragraph handed
        // to the next attempt is the real one: one line longer, and every line
        // of it still findable by what it says.
        Refill::WontFit => {
            let Some(leading) = leading_of(para.baselines) else {
                return Err(STATUS_TOO_WIDE);
            };
            let at_last = *para.baselines.last().unwrap();
            let lines = crate::recover::lines_of(&doc, page);
            let Some(last) = crate::recover::lines_at(&lines, at_last).next() else {
                return Err(STATUS_TOO_WIDE);
            };
            let Some(said) = para.says.last().cloned().flatten() else {
                return Err(STATUS_TOO_WIDE);
            };
            let grown = with_another_line(&doc, page, &lines, last, leading)?;

            let mut baselines = para.baselines.to_vec();
            baselines.push(at_last - leading);
            let mut says = para.says.to_vec();
            says.push(Some(said));
            let bigger = Paragraph {
                baselines: &baselines,
                says: &says,
                edited: para.edited,
                left: para.left,
                column: para.column,
                complete: para.complete,
                copied: para.copied + 1,
            };
            // ⚠️ AND ONE LINE AT A TIME, so a paragraph that can never fit
            // stops instead of growing until the page is full. Four is more
            // lines than any single word can need.
            if para.baselines.len() >= growing.saturating_add(4) {
                return Err(STATUS_TOO_WIDE);
            }
            return retype_in_paragraph_within(
                &grown, page_index, &bigger, new_text, font_path, None, growing, at);
        }
    };

    // ⚠️ EVERY LINE IS THE SAME SINGLE-LINE WRITE, CHAINED. Nothing here knows
    // how to write a line, only which lines have to be written. A line the
    // refill left alone still says what it said, so its anchor still finds it
    // in the bytes the write before it produced.
    let mut carried = bytes.to_vec();
    for (n, text) in rewrapped.iter().enumerate() {
        let Some(before) = para.says[n].as_deref() else { continue };
        if text == before {
            continue;
        }
        carried = retype(
            &carried, page_index, para.baselines[n], before, text, font_path, Some(indexes))?;
    }
    Ok(carried)
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
    at: Option<f64>,
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
    if !says_it(
        &lines, baseline, expected, indexes, &face, &crate::recover::fonts_of(&doc, page), at)
    {
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
    let declared = crate::recover::fonts_of(&doc, page);
    let Some(line) =
        the_one_that_says(&lines, baseline, expected, indexes, &face, &declared, at)
    else {
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

        // A word on its first line that the WRITER can find, since the writer
        // finds the placement it is to replace by what that placement says.
        let lines = crate::recover::lines_of(&doc, page);
        let (target, was) =
            a_word_the_writer_can_find(&doc, page, &lines, &indexes, NIRMALA, baselines[0])
                .expect("the writer can find nothing on the first line of this paragraph");
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
            ("WITH THE PARAGRAPH'S ROOM", Some(Room { left, column, below: &baselines[1..], leading: leading_of(&baselines) })),
        ] {
            println!("---- {label}");
            let out = retype_within(
                &bytes, 0, target.page_y, &was, &now, NIRMALA, Some(&indexes), room.as_ref(),
                None);
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

    const MYANMAR_BOOK: &str = r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf";
    

    /// What each of a paragraph's baselines says, as recovery reads it.
    fn paragraph_says(bytes: &[u8], baselines: &[f64]) -> Vec<Option<String>> {
        let doc = Document::load_mem(bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);
        let lines = crate::recover::lines_of(&doc, page);
        let readings = crate::recover::read_page_with(&doc, page, &indexes);
        baselines
            .iter()
            .map(|&y| {
                lines
                    .iter()
                    .zip(&readings)
                    .find(|(l, r)| {
                        (l.page_y - y).abs() < BASELINE_TOLERANCE && r.text.is_some()
                    })
                    .and_then(|(_, r)| r.text.clone())
            })
            .collect()
    }

    /// ⚠️ THE WHOLE SCENARIO ON THE MYANMAR BOOK, THE OTHER WAY ROUND. Its
    /// lines read whole and are drawn in one placement each, so there is
    /// nothing to carry and everything to rewrap: the words move between the
    /// lines and every line that changed is written again.
    ///
    ///     cargo test --release a_longer_word_rewraps_a_myanmar_paragraph -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn a_longer_word_rewraps_a_myanmar_paragraph() {
        if !std::path::Path::new(MYANMAR_BOOK).exists()
            || !std::path::Path::new(MYANMAR_TEXT).exists()
        {
            println!("not on this machine");
            return;
        }
        let on_disk = std::fs::read(MYANMAR_BOOK).unwrap();
        let handle = crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
        let bytes = crate::document_bytes(handle).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);

        // ⚠️ THE PARAGRAPH WITH ROOM AT THE BOTTOM OF IT. A rewrap can only
        // put words where there is space for them, and on this page most
        // paragraphs are two full lines: growing a word there genuinely needs a
        // third line, which is a different step. `WontFit` is asserted on its
        // own below.
        let (blocks, _) = crate::page_blocks(handle, 0).expect("no blocks");
        let candidates: Vec<&crate::block::Block> =
            blocks.iter().filter(|b| b.lines.len() >= 2).collect();
        assert!(!candidates.is_empty(), "no paragraph of two lines on this page");

        // ⚠️ SLACK MEASURED THE WAY THE RESULT IS MEASURED. The block model's
        // own `right` disagreed with the placements by 411 points on this very
        // page, so choosing by it picked a paragraph with no room at all and
        // called it the roomiest. Both numbers here come from the placements.
        let every: Vec<f64> =
            candidates.iter().flat_map(|b| b.lines.iter().map(|l| l.baseline as f64)).collect();
        let reach = paragraph_geometry(&bytes, &every);
        let right_at = |y: f64| {
            reach.iter().find(|(at, ..)| (at - y).abs() < BASELINE_TOLERANCE).map(|(.., r)| *r)
        };
        let spare = |x: &crate::block::Block| {
            let column = x.lines.iter()
                .filter_map(|l| right_at(l.baseline as f64))
                .fold(f64::MIN, f64::max);
            column - right_at(x.lines.last().unwrap().baseline as f64).unwrap_or(column)
        };
        for (n, c) in candidates.iter().enumerate() {
            println!("   candidate {n}: {} lines, {:.1} points spare on the last",
                c.lines.len(), spare(c));
        }
        let block = candidates
            .iter()
            .max_by(|a, b| spare(a).partial_cmp(&spare(b)).unwrap())
            .unwrap();
        let baselines: Vec<f64> = block.lines.iter().map(|l| l.baseline as f64).collect();
        let left = block.lines.iter().map(|l| l.left).fold(f32::MAX, f32::min) as f64;
        let column = baselines.iter().filter_map(|y| right_at(*y)).fold(f64::MIN, f64::max);
        let says = paragraph_says(&bytes, &baselines);
        println!("a paragraph of {} lines, left {left:.2}, column {column:.2}",
            baselines.len());
        println!("the last line leaves {:.1} points spare", spare(block));
        for (y, s) in baselines.iter().zip(&says) {
            println!("   y {y:7.2}  {:2} words  {:?}",
                s.as_deref().unwrap_or("").split_whitespace().count(),
                s.as_deref().unwrap_or("<unreadable>").chars().take(30).collect::<String>());
        }

        // ⚠️ TWO ANSWERS TO "WHAT DOES THIS LINE SAY" IS ONE TOO MANY. The app
        // shows what the PAGE reader made of the line and the writer finds the
        // line by what the LINE reader makes of it, and the two do not go
        // through the same cleaning. Print both before asking for a write.
        {
            let page = *doc.get_pages().values().next().unwrap();
            let all = crate::recover::lines_of(&doc, page);
            let font_bytes = std::fs::read(MYANMAR_TEXT).unwrap();
            let face = rustybuzz::Face::from_slice(&font_bytes, 0).unwrap();
            for l in all.iter().filter(|l| (l.page_y - baselines[0]).abs() < BASELINE_TOLERANCE) {
                let by_line = indexes
                    .index_for(&l.base_font)
                    .and_then(|ix| crate::recover::read_line(ix, &face, l,
                        crate::recover::fonts_of(&doc, page).get(&l.resource)
                            .and_then(|(_, w)| w.as_ref())));
                println!("\n   at the baseline: {} in {}", l.glyphs.len(), l.base_font);
                println!("      the page reader: {:?}",
                    says[0].as_deref().unwrap_or("<none>").chars().take(24).collect::<String>());
                println!("      the line reader: {:?}",
                    by_line.as_deref().unwrap_or("<none>").chars().take(24).collect::<String>());
                println!("      they agree: {}", by_line.as_deref() == says[0].as_deref());
            }
        }

        // The reader replaces one word on the first line with a longer word.
        let was = says[0].clone().expect("the first line could not be read");
        let word = was
            .split_whitespace()
            .min_by_key(|w| w.chars().count())
            .expect("no word to lengthen")
            .to_string();
        let font_bytes = std::fs::read(MYANMAR_TEXT).unwrap();
        let size = block.lines[0].size as f64;
        // ⚠️ BIG ENOUGH TO BEAT THE SQUEEZE, AND FOUND BY WRITING IT. A
        // single-line write first tries to land on the old width by closing the
        // replacement's own word spaces, and on a line of seven words that
        // absorbs a doubled word completely. How much squeeze is left is not
        // worth predicting, so this grows the word until the page really does
        // carry ink past the column.
        let untouched = paragraph_geometry(&bytes, &baselines);
        let over_the_column = |produced: &[u8]| -> usize {
            paragraph_geometry(produced, &baselines)
                .iter()
                .zip(&untouched)
                .filter(|((_, _, _, r), (.., was_r))| *r > was_r.max(column) + SETTLED)
                .count()
        };
        let mut longer = word.clone();
        let (now, alone) = loop {
            longer.push_str(&word);
            let now = was.replacen(&word, &longer, 1);
            let alone = retype(&bytes, 0, baselines[0], &was, &now, MYANMAR_TEXT, Some(&indexes))
                .expect("the one-line write was refused");
            if over_the_column(&alone) > 0 {
                break (now, alone);
            }
            assert!(longer.chars().count() < word.chars().count() * 12,
                "nothing this test can type overflows the line");
        };
        println!("\nreplacing {:?} with {} copies of itself",
            word.chars().take(12).collect::<String>(),
            longer.chars().count() / word.chars().count().max(1));
        assert_ne!(now, was, "the replacement changed nothing");

        let para = Paragraph {
            baselines: &baselines,
            says: &says,
            edited: 0,
            left,
            column,
            complete: true,
            copied: 0,
        };
        assert!(para.can_be_rewrapped(), "this paragraph should take the rewrap path");

        // The old behaviour: one line, written past the end of its column.
        println!("\n---- BEFORE ANY OF THIS (one line, no paragraph)");
        for ((y, _, _, r), (.., was_r)) in
            paragraph_geometry(&alone, &baselines).iter().zip(&untouched)
        {
            let past = if r > &(was_r.max(column) + SETTLED) { "  PAST THE COLUMN" } else { "" };
            println!("   y {y:7.2}  reaches {r:7.2}{past}");
        }

        // ⚠️ AND WITH THE PARAGRAPH IT REWRAPS, GAINING A LINE TO DO IT.
        // Every paragraph on this page is two full lines: measured, 0.2, 2.6
        // and 10.5 points spare on the last line of the three of them. So there
        // is nowhere for a word to go until the paragraph makes somewhere.
        println!("\n---- WITH THE PARAGRAPH");
        let leading = leading_of(&baselines).expect("the paragraph has no leading");
        let floor = |b: &[u8]| every_baseline(b).iter()
            .map(|(y, _)| *y).fold(f64::MAX, f64::min);
        let was_floor = floor(&bytes);

        let grown = retype_in_paragraph(&bytes, 0, &para, &now, MYANMAR_TEXT, Some(&indexes), None)
            .expect("the paragraph refused to grow");

        let dropped = was_floor - floor(&grown);
        println!("   the lowest line on the page went down {dropped:.2}, \
                  which is {:.2} lines", dropped / leading);
        assert!((dropped - leading).abs() < BASELINE_TOLERANCE,
            "the page should have moved down one leading and moved {dropped:.2}");

        let every: Vec<f64> =
            baselines.iter().copied().chain([*baselines.last().unwrap() - leading]).collect();
        let said = paragraph_says(&grown, &every);
        for ((y, n, _, r), s) in paragraph_geometry(&grown, &every).iter().zip(&said) {
            println!("   y {y:7.2}  {n} placements  reaches {r:7.2}  {:2} words  {:?}",
                s.as_deref().unwrap_or("").split_whitespace().count(),
                s.as_deref().unwrap_or("<unreadable>").chars().take(26).collect::<String>());
            assert!(*n == 0 || *r <= column + SETTLED,
                "the line at {y:.2} reaches {r:.2}, past the column at {column:.2}");
        }
        assert!(said.last().is_some_and(|s| s.is_some()),
            "the line the paragraph gained says nothing");

        // ⚠️ AND NOT ONE WORD OF IT WAS LOST OR INVENTED. A rewrap moves
        // words between lines, so the only honest check is on the paragraph as
        // a whole.
        let before: Vec<&str> = says.iter().filter_map(|s| s.as_deref())
            .flat_map(str::split_whitespace).collect();
        let after: Vec<&str> = said.iter().filter_map(|s| s.as_deref())
            .flat_map(str::split_whitespace).collect();
        println!("   {} words before, {} after", before.len(), after.len());
        assert_eq!(before.len() + 0, after.len(),
            "the paragraph should still have every word it had:\nbefore {before:?}\nafter  {after:?}");

        crate::close_document(handle);
    }

    /// ⚠️ WHERE A REWRAP CAN BREAK A LINE, WHICH IS NOT WHERE A PLACEMENT CAN.
    /// `block::rewrap_from` moves whole words and finds them with
    /// `split_whitespace`, so a paragraph it can reflow is one whose lines
    /// carry spaces. Burmese does not always space its words, and a line that
    /// is one unbroken run is one word to a rewrap however wide it is.
    ///
    ///     cargo test --release how_many_words_a_real_paragraph_offers -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn how_many_words_a_real_paragraph_offers() {
        for (what, file) in [
            ("HINDI", GEETA),
            ("MYANMAR", r"D:\Ayaan PDF Test file\Pyidaungsu- text 3 Pages.pdf"),
            ("MYANMAR 2", r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf"),
        ] {
            println!("\n================ {what}");
            if !std::path::Path::new(file).exists() {
                println!("not on this machine");
                continue;
            }
            let on_disk = std::fs::read(file).unwrap();
            let handle = crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
            let bytes = crate::document_bytes(handle).unwrap();
            let doc = Document::load_mem(&bytes).unwrap();
            let page = *doc.get_pages().values().next().unwrap();
            let indexes = crate::recover::indexes_for_document(&doc);
            let lines = crate::recover::lines_of(&doc, page);
            let readings = crate::recover::read_page_with(&doc, page, &indexes);

            let (blocks, _) = crate::page_blocks(handle, 0).expect("no blocks");
            for block in blocks.iter().filter(|b| b.lines.len() >= 2) {
                let column =
                    block.lines.iter().map(|l| l.right).fold(f32::MIN, f32::max);
                println!("-- a paragraph of {} lines, column {column:.1}", block.lines.len());
                for bl in &block.lines {
                    // What the RECOVERY writer would be given for this line,
                    // which is what a rewrap would have to work with.
                    let said: Vec<&str> = lines
                        .iter()
                        .zip(&readings)
                        .filter(|(l, r)| {
                            (l.page_y - bl.baseline as f64).abs() < BASELINE_TOLERANCE
                                && r.text.is_some()
                        })
                        .map(|(_, r)| r.text.as_deref().unwrap())
                        .collect();
                    let whole = said.len() == 1;
                    let words = said.first().map_or(0, |t| t.split_whitespace().count());
                    println!(
                        "   y {:7.2}  {} readable fragment(s){}  {words:3} words  {:?}",
                        bl.baseline,
                        said.len(),
                        if whole { ", the whole line" } else { "" },
                        said.first().unwrap_or(&"").chars().take(28).collect::<String>(),
                    );
                }
            }
            crate::close_document(handle);
        }
    }

    /// The placement on `baseline` that the writer itself can find, and what it
    /// says.
    ///
    /// ⚠️ ASKED OF THE WRITER'S OWN FINDER, NOT OF THE PAGE READER. A test
    /// that picks a word the reader can see and the writer cannot is testing
    /// the pick, not the write. `the_one_that_says` refuses a tie, and which
    /// texts tie depends on how the line was cleaned.
    fn a_word_the_writer_can_find<'a>(
        doc: &Document,
        page: ObjectId,
        lines: &'a [Line],
        indexes: &crate::recover::Indexes,
        font: &str,
        baseline: f64,
    ) -> Option<(&'a Line, String)> {
        let font_bytes = std::fs::read(font).ok()?;
        let face = rustybuzz::Face::from_slice(&font_bytes, 0)?;
        let widths = crate::recover::fonts_of(doc, page);
        let readings = crate::recover::read_page_with(doc, page, indexes);
        for (line, reading) in lines.iter().zip(&readings) {
            if (line.page_y - baseline).abs() >= BASELINE_TOLERANCE {
                continue;
            }
            let Some(text) = reading.text.clone() else { continue };
            if text.chars().count() < 2 || text.trim() != text {
                continue;
            }
            if let Some(found) =
                the_one_that_says(lines, baseline, &text, indexes, &face, &widths, None)
            {
                return Some((found, text));
            }
        }
        None
    }

    /// ⚠️ WHAT THE PAGE DRAWS THAT REFLOW CANNOT MOVE. A placement is moved
    /// by rewriting the `Tm` that placed it, so a text object that never writes
    /// one cannot be moved at all. This says how much of a real page that is
    /// and what those objects look like.
    ///
    ///     cargo test --release what_cannot_be_moved -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs that are not in this repository"]
    fn what_cannot_be_moved() {
        for (what, file) in [
            ("HINDI", GEETA),
            ("MYANMAR", r"D:\Ayaan PDF Test file\Myanmar Unicode Text test file 2.pdf"),
        ] {
            println!("\n================ {what}");
            if !std::path::Path::new(file).exists() {
                println!("not on this machine");
                continue;
            }
            let bytes = std::fs::read(file).unwrap();
            let doc = Document::load_mem(&bytes).unwrap();
            let page = *doc.get_pages().values().next().unwrap();
            let content = Content::decode(&doc.get_page_content(page)).unwrap();
            let pen = crate::shift::placements(&content);

            let showing: Vec<usize> = content.operations.iter().enumerate()
                .filter(|(_, op)| matches!(op.operator.as_str(), "TJ" | "Tj"))
                .map(|(at, _)| at)
                .collect();
            let orphans: Vec<usize> =
                showing.iter().copied().filter(|at| !pen.contains_key(at)).collect();
            println!("{} showing operations, {} of them with no Tm to rewrite",
                showing.len(), orphans.len());

            // What the object around the first one is made of.
            if let Some(&first) = orphans.first() {
                let start = (0..first).rev()
                    .find(|at| content.operations[*at].operator == "BT")
                    .unwrap_or(0);
                let end = (first..content.operations.len())
                    .find(|at| content.operations[*at].operator == "ET")
                    .unwrap_or(content.operations.len() - 1);
                let ops: Vec<&str> = content.operations[start..=end]
                    .iter().map(|o| o.operator.as_str()).collect();
                println!("the object around the first of them: {ops:?}");
            }
        }
    }

    /// ⚠️ HOW MANY OF A PAGE'S WORDS THE WRITER CAN ACTUALLY FIND. It finds
    /// the placement to replace by what that placement SAYS, and refuses when
    /// two placements on one baseline say the same thing, because it cannot
    /// tell which the reader meant. On a book that draws one word per placement
    /// that tie is not rare at all: the same short word appears several times
    /// in one line of prose.
    ///
    ///     cargo test --release how_many_words_the_writer_can_find -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn how_many_words_the_writer_can_find() {
        for (what, file, font) in [
            ("HINDI", GEETA, NIRMALA),
            ("MYANMAR", MYANMAR_BOOK, MYANMAR_TEXT),
        ] {
            println!("\n================ {what}");
            if !std::path::Path::new(file).exists() || !std::path::Path::new(font).exists() {
                println!("not on this machine");
                continue;
            }
            let bytes = std::fs::read(file).unwrap();
            let doc = Document::load_mem(&bytes).unwrap();
            let page = *doc.get_pages().values().next().unwrap();
            let indexes = crate::recover::indexes_for_document(&doc);
            let lines = crate::recover::lines_of(&doc, page);
            let readings = crate::recover::read_page_with(&doc, page, &indexes);
            let widths = crate::recover::fonts_of(&doc, page);
            let font_bytes = std::fs::read(font).unwrap();
            let face = rustybuzz::Face::from_slice(&font_bytes, 0).unwrap();

            let (mut readable, mut findable, mut with_a_hint) = (0usize, 0usize, 0usize);
            let mut ties: std::collections::BTreeMap<String, usize> =
                std::collections::BTreeMap::new();
            for (line, reading) in lines.iter().zip(&readings) {
                let Some(text) = reading.text.as_deref() else { continue };
                readable += 1;
                if the_one_that_says(&lines, line.page_y, text, &indexes, &face, &widths, None)
                    .is_some()
                {
                    findable += 1;
                } else {
                    *ties.entry(text.to_string()).or_default() += 1;
                }
                // And again the way the app asks now, saying where it clicked.
                if the_one_that_says(
                    &lines, line.page_y, text, &indexes, &face, &widths, Some(line.page_x()))
                    .is_some()
                {
                    with_a_hint += 1;
                }
            }
            println!("{readable} readable placements, {findable} findable by text alone, \n                      {with_a_hint} findable when the app says where it clicked");
            println!("{} it cannot, because another placement on the same line says the same",
                readable - findable);
            let mut worst: Vec<(&String, &usize)> = ties.iter().collect();
            worst.sort_by(|a, b| b.1.cmp(a.1));
            for (text, n) in worst.iter().take(8) {
                println!("   {n:3} x {:?}", text.chars().take(18).collect::<String>());
            }
        }
    }

    /// ⚠️ THE WORD THE READER ACTUALLY LOST. On the Geeta page, line 297.48
    /// says four different words twice each, and every one of them was refused
    /// because a baseline and a word do not name a placement. The reader's own
    /// log: three `EditRecoveredLine p0 refused at baseline 297.48004150390625`
    /// in five minutes, and the app dropped the typing without a word.
    ///
    ///     cargo test --release a_repeated_word_is_edited_where_it_was_clicked -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn a_repeated_word_is_edited_where_it_was_clicked() {
        if !std::path::Path::new(GEETA).exists() || !std::path::Path::new(NIRMALA).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(GEETA).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);
        let lines = crate::recover::lines_of(&doc, page);
        let readings = crate::recover::read_page_with(&doc, page, &indexes);
        let widths = crate::recover::fonts_of(&doc, page);
        let font_bytes = std::fs::read(NIRMALA).unwrap();
        let face = rustybuzz::Face::from_slice(&font_bytes, 0).unwrap();

        // A word that really is said twice on one line of this book.
        let mut twice: Option<(f64, String, Vec<f64>)> = None;
        for (line, reading) in lines.iter().zip(&readings) {
            let Some(text) = reading.text.clone() else { continue };
            if text.chars().count() < 2 {
                continue;
            }
            let xs: Vec<f64> = lines.iter().zip(&readings)
                .filter(|(l, r)| (l.page_y - line.page_y).abs() < BASELINE_TOLERANCE
                    && r.text.as_ref() == Some(&text))
                .map(|(l, _)| l.page_x())
                .collect();
            if xs.len() >= 2 {
                twice = Some((line.page_y, text, xs));
                break;
            }
        }
        let Some((baseline, text, xs)) = twice else {
            println!("no word is said twice on any line of this page");
            return;
        };
        println!("{:?} is said {} times on the line at {baseline:.2}, at x {:?}",
            text.chars().take(14).collect::<String>(), xs.len(),
            xs.iter().map(|x| format!("{x:.1}")).collect::<Vec<_>>());

        // ⚠️ WITHOUT THE HINT IT IS REFUSED, and that is still right: there
        // is nothing to choose by.
        assert!(
            the_one_that_says(&lines, baseline, &text, &indexes, &face, &widths, None).is_none(),
            "a word said twice on its line should not be guessed at");

        // ⚠️ AND WITH IT, EACH ONE IS ITSELF.
        for want in &xs {
            let found = the_one_that_says(
                &lines, baseline, &text, &indexes, &face, &widths, Some(*want))
                .expect("the writer could not find the word the reader clicked");
            println!("   asked at {want:7.2}  found the one at {:7.2}", found.page_x());
            assert!((found.page_x() - want).abs() < 0.01,
                "asked for the word at {want:.2} and got the one at {:.2}", found.page_x());
        }

        // ⚠️ AND A HINT FROM NOWHERE NEAR IS STILL REFUSED, or a stale
        // selection would quietly edit whichever word happened to be closest.
        assert!(
            the_one_that_says(&lines, baseline, &text, &indexes, &face, &widths, Some(-500.0))
                .is_none(),
            "a hint that lands nowhere near should not pick a word anyway");

        // And the whole write goes through, on the SECOND of them.
        let last = xs.iter().cloned().fold(f64::MIN, f64::max);
        let now = format!("{text}{text}");
        let out = retype_within(
            &bytes, 0, baseline, &text, &now, NIRMALA, Some(&indexes), None, Some(last))
            .expect("the writer refused a word it had just found");

        let after = Document::load_mem(&out).unwrap();
        let p2 = *after.get_pages().values().next().unwrap();
        let ix = crate::recover::indexes_for_document(&after);
        let said: Vec<String> = crate::recover::lines_of(&after, p2)
            .iter()
            .zip(&crate::recover::read_page_with(&after, p2, &ix))
            .filter(|(l, _)| (l.page_y - baseline).abs() < BASELINE_TOLERANCE)
            .filter_map(|(_, r)| r.text.clone())
            .collect();
        println!("   after the write the line says {} x {:?} and {} x the doubled one",
            said.iter().filter(|t| **t == text).count(),
            text.chars().take(14).collect::<String>(),
            said.iter().filter(|t| **t == now).count());
        assert_eq!(said.iter().filter(|t| **t == now).count(), 1,
            "the doubled word is not on the line");
        assert_eq!(said.iter().filter(|t| **t == text).count(), xs.len() - 1,
            "the wrong copy of the word was changed");
    }

    /// ⚠️ WHERE A LINE MAY BE BROKEN IS A QUESTION ABOUT PUNCTUATION, and the
    /// recovery reader cannot answer it on this book: the comma that ended up
    /// starting the reader's third line reads as nothing. This prints what each
    /// font's `/ToUnicode` CLAIMS for the placements around that break, to see
    /// whether the producer's own table knows a comma from a word.
    ///
    ///     cargo test --release what_the_page_claims_around_a_break -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs a PDF that is not in this repository"]
    fn what_the_page_claims_around_a_break() {
        if !std::path::Path::new(GEETA).exists() {
            println!("not on this machine");
            return;
        }
        let bytes = std::fs::read(GEETA).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let claims = crate::recover::claims_of(&doc, page);
        println!("{} fonts on the page carry a /ToUnicode", claims.len());
        for (name, map) in &claims {
            let sample: Vec<String> = map.iter()
                .filter(|(_, t)| t.chars().all(|c| !c.is_alphanumeric()))
                .take(12)
                .map(|(c, t)| format!("{c:04X}={t:?}"))
                .collect();
            println!("   /{}  {} entries; not letters: {}",
                String::from_utf8_lossy(name), map.len(), sample.join(" "));
        }
        let lines = crate::recover::lines_of(&doc, page);
        for y in [297.48, 283.44, 269.52] {
            println!("\nthe end of the line at {y:.2}:");
            let mut on: Vec<&Line> = lines.iter()
                .filter(|l| (l.page_y - y).abs() < BASELINE_TOLERANCE && l.page_x() > 440.0)
                .collect();
            on.sort_by(|a, b| a.page_x().total_cmp(&b.page_x()));
            for l in on {
                let map = claims.get(&l.resource);
                let said: String = l.glyphs.iter()
                    .map(|g| map.and_then(|m| m.get(g)).cloned().unwrap_or_else(|| "?".into()))
                    .collect();
                println!("   x {:7.2}  /{:<4} codes {:?}  claims {said:?}",
                    l.page_x(), String::from_utf8_lossy(&l.resource),
                    l.glyphs.iter().map(|g| format!("{g:04X}")).collect::<Vec<_>>());
            }
        }
    }

    /// Every baseline the page draws on, top first, and how many placements.
    fn every_baseline(bytes: &[u8]) -> Vec<(f64, usize)> {
        let doc = Document::load_mem(bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let mut rows: Vec<(f64, usize)> = Vec::new();
        for line in crate::recover::lines_of(&doc, page) {
            match rows.iter_mut().find(|(y, _)| (y - line.page_y).abs() < BASELINE_TOLERANCE) {
                Some((_, n)) => *n += 1,
                None => rows.push((line.page_y, 1)),
            }
        }
        rows.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap());
        rows
    }

    /// ⚠️ THE PARAGRAPH GAINS A LINE AND THE PAGE UNDER IT MOVES DOWN.
    /// This is the case the carry used to refuse: a word so much longer that
    /// the words it displaces will not go into the lines the paragraph has.
    ///
    ///     cargo test --release a_paragraph_gains_a_line -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn a_paragraph_gains_a_line() {
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

        // ⚠️ THE PARAGRAPH WITH THE LEAST ROOM IN IT. A paragraph only needs
        // a new line once its own lines are full, and this page has one whose
        // short lines leave 848 points of slack: no replacement that fits in a
        // single placement can ever exhaust that, so it would prove nothing.
        let (blocks, _) = crate::page_blocks(handle, 0).expect("no blocks");
        let candidates: Vec<&crate::block::Block> =
            blocks.iter().filter(|b| b.lines.len() >= 2).collect();
        let all: Vec<f64> =
            candidates.iter().flat_map(|b| b.lines.iter().map(|l| l.baseline as f64)).collect();
        let reach = paragraph_geometry(&bytes, &all);
        let right_at = |y: f64| reach.iter()
            .find(|(at, ..)| (at - y).abs() < BASELINE_TOLERANCE).map(|(.., r)| *r);
        let slack = |b: &crate::block::Block| -> f64 {
            let column = b.lines.iter().filter_map(|l| right_at(l.baseline as f64))
                .fold(f64::MIN, f64::max);
            b.lines.iter()
                .filter_map(|l| right_at(l.baseline as f64))
                .map(|r| column - r)
                .sum()
        };
        // ⚠️ AND ONE WHOSE TEXT CAN BE MOVED AT ALL. Reflow moves a
        // placement by rewriting the `Tm` that placed it, and this book draws
        // some of its text with a `Td` instead, which has no `Tm` of its own to
        // rewrite. Those lines can be read and retyped but not shifted, and a
        // paragraph containing one refuses the whole edit.
        let placed_lines = crate::recover::lines_of(&doc, page);
        let content = Content::decode(&doc.get_page_content(page)).expect("no content");
        let pen = crate::shift::placements(&content);
        let movable = |b: &crate::block::Block| -> bool {
            placed_lines.iter()
                .filter(|l| b.lines.iter()
                    .any(|bl| (l.page_y - bl.baseline as f64).abs() < BASELINE_TOLERANCE))
                .all(|l| l.drawn_by.iter().all(|at| pen.contains_key(at)))
        };
        let can_move: Vec<&&crate::block::Block> =
            candidates.iter().filter(|b| movable(b)).collect();
        println!("{} of {} paragraphs on this page can be moved at all",
            can_move.len(), candidates.len());
        let block = can_move.iter()
            .min_by(|a, b| slack(a).partial_cmp(&slack(b)).unwrap())
            .expect("no paragraph on this page can be moved");
        let baselines: Vec<f64> = block.lines.iter().map(|l| l.baseline as f64).collect();
        let left = block.lines.iter().map(|l| l.left).fold(f32::MAX, f32::min) as f64;
        let column = baselines.iter().filter_map(|y| right_at(*y)).fold(f64::MIN, f64::max);
        let leading = leading_of(&baselines).expect("the paragraph has no leading");
        let last = *baselines.last().unwrap();
        println!("a paragraph of {} lines, column {column:.2}, leading {leading:.2}, \
                  last line at {last:.2}, {:.1} points of slack in it",
            baselines.len(), slack(block));

        let lines = crate::recover::lines_of(&doc, page);
        let (target, was) =
            a_word_the_writer_can_find(&doc, page, &lines, &indexes, NIRMALA, baselines[0])
                .expect("the writer can find nothing on the first line of this paragraph");

        // ⚠️ LONGER THAN ALL THE ROOM THE PARAGRAPH HAS. The replacement is
        // written as ONE placement, so it can never be wider than the column
        // itself: what it can do is fill every spare point the paragraph has
        // and leave it needing another line. Sized from the slack just measured.
        let one = {
            let w = crate::recover::fonts_of(&doc, page);
            let (_, Some(cw)) = w.get(&target.resource).expect("no widths for the target") else {
                panic!("the target placement has no widths")
            };
            target.along_baseline(crate::recover::advance_of(target, cw)).0
        };
        let copies = ((slack(block) / one).ceil() as usize + 2).max(2);
        println!("replacing {was:?}, one copy of it is {one:.1} points, with {copies} copies");
        assert!(one * copies as f64 + left < column,
            "{copies} copies will not fit in one placement, so this cannot be tested");
        let now = was.repeat(copies);

        let before = every_baseline(&bytes);
        let para = Paragraph {
            baselines: &baselines,
            // ⚠️ THE APP KNOWS WHICH FRAGMENT WAS CLICKED AND NOTHING ELSE.
            // On this book a visual line is up to 52 placements and most of
            // them read as nothing, so there is no whole-line text to give.
            // The clicked one is what the writer needs to find the placement,
            // and the rest being unknown is exactly what sends this paragraph
            // down the placement path.
            says: &{
                let mut says = vec![None; baselines.len()];
                says[0] = Some(was.clone());
                says
            },
            edited: 0,
            left,
            column,
            complete: false,
            copied: 0,
        };
        assert!(!para.can_be_rewrapped(),
            "the Hindi paragraph should take the placement path, not the rewrap");

        let produced =
            retype_in_paragraph(&bytes, 0, &para, &now, NIRMALA, Some(&indexes), None)
                .expect("the paragraph refused to grow");

        // ⚠️ RENDERED, BECAUSE A POSITION IS NOT A PIXEL. Everything under a
        // paragraph that gains a line moves DOWN, and on this book every line
        // has a white path under it painted just before its text; text that
        // keeps its old place in the stream is painted over by the path of the
        // line it moved onto.
        let dir = std::env::temp_dir().join("ayaan-gained-line");
        let _ = std::fs::create_dir_all(&dir);
        raster(&bytes, &dir.join("before.bgra").to_string_lossy());
        raster(&produced, &dir.join("after.bgra").to_string_lossy());
        println!("rendered to {} (paragraph last line at {:.2})", dir.display(),
            baselines.last().unwrap());
        let after = every_baseline(&produced);

        println!("\nthe paragraph, before and after:");
        for y in &baselines {
            let n = |rows: &[(f64, usize)]| rows.iter()
                .find(|(at, _)| (at - y).abs() < BASELINE_TOLERANCE).map_or(0, |(_, n)| *n);
            println!("   y {y:7.2}  {:3} -> {:3} placements", n(&before), n(&after));
        }


        println!("\nunder the paragraph, before -> after:");
        for (y, n) in before.iter().filter(|(y, _)| *y < last - BASELINE_TOLERANCE).take(6) {
            let at = after.iter().find(|(a, _)| (a - (y - leading)).abs() < BASELINE_TOLERANCE);
            println!("   y {y:7.2} {n:3}  ->  {}", match at {
                Some((a, m)) => format!("y {a:7.2} {m:3}"),
                None => "NOT THERE".into(),
            });
        }
        println!("every after-baseline from {:.2} down:", last);
        for (y, n) in after.iter().filter(|(y, _)| *y < last + BASELINE_TOLERANCE).take(8) {
            println!("   y {y:7.2}  {n:3} placements");
        }

        // ⚠️ THE LOWEST LINE ON THE PAGE IS THE ONE TO ASK. Every other line
        // has another line a leading below it, so "moved down by one leading"
        // cannot be told apart from "nothing moved": the first version of this
        // test compared each line against its NEIGHBOUR and reported a
        // page-wide shift that had not happened. The bottom line has nothing
        // under it to be confused with.
        let floor = |rows: &[(f64, usize)]| rows.iter().map(|(y, _)| *y).fold(f64::MAX, f64::min);
        let (was_floor, now_floor) = (floor(&before), floor(&after));
        let dropped = was_floor - now_floor;
        println!("the lowest line on the page went {was_floor:.2} to {now_floor:.2}, \
                  down {dropped:.2}, which is {:.2} lines", dropped / leading);
        assert!(dropped > SETTLED, "nothing under the paragraph moved down");
        let gained = (dropped / leading).round();
        assert!((dropped - gained * leading).abs() < BASELINE_TOLERANCE,
            "the page moved down {dropped:.2}, which is not a whole number of lines");
        assert!(gained >= 1.0, "the paragraph gained no line");

        // ⚠️ AND NOTHING ENDED UP PAST THE COLUMN. That is the whole point of
        // gaining the line, and the reason the carry used to refuse instead.
        let every: Vec<f64> = baselines.iter().copied()
            .chain((1..=gained as usize).map(|k| last - leading * k as f64))
            .collect();
        println!("the paragraph, {} lines now:", every.len());
        for (y, n, _, r) in paragraph_geometry(&produced, &every) {
            println!("   y {y:7.2}  {n:3} placements  reaches {r:7.2}");
            assert!(n == 0 || r <= column + SETTLED,
                "the line at {y:.2} reaches {r:.2}, past the column at {column:.2}");
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

        let room = Room { left, column, below: &baselines[1..], leading: leading_of(&baselines) };
        let out = retype_within(
            &bytes, 0, target.page_y, &was, &now, FONT, Some(&indexes), Some(&room), None);
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
            crate::recover::read_line(index, &face, &ours,
                crate::recover::fonts_of(&done, page_after).get(&ours.resource)
                    .and_then(|(_, w)| w.as_ref())).as_deref(),
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
                handle, 0, line.y as f32, -1.0,
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

    /// ⚠️ THE READER'S EDIT, RENDERED, BECAUSE POSITIONS ARE NOT PIXELS. The
    /// content stream said the carried words sat at the margin of the lines
    /// below, and on the reader's screen they were not there at all. This
    /// renders before and after with PDFium, which is what the app draws with,
    /// and prints what surrounds the carried text in the stream so anything
    /// that would hide it, a clip above all, can be seen.
    ///
    ///     cargo test --release what_the_readers_edit_looks_like -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs PDFs and fonts that are not in this repository"]
    fn what_the_readers_edit_looks_like() {
        if !std::path::Path::new(GEETA).exists() || !std::path::Path::new(NIRMALA).exists() {
            println!("not on this machine");
            return;
        }
        const AT: f64 = 297.48;
        const WORD: &str = "\u{92f}\u{941}\u{926}\u{94d}\u{927}";
        let on_disk = std::fs::read(GEETA).unwrap();
        let handle = crate::open_document_from_bytes(on_disk.as_ptr(), on_disk.len());
        let bytes = crate::document_bytes(handle).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page = *doc.get_pages().values().next().unwrap();
        let indexes = crate::recover::indexes_for_document(&doc);
        let lines = crate::recover::lines_of(&doc, page);
        let readings = crate::recover::read_page_with(&doc, page, &indexes);
        let (page_left, _, page_w) = crate::recover::page_box(&doc, page).unwrap();
        let target = lines.iter().zip(&readings)
            .find(|(l, r)| (l.page_y - AT).abs() < 0.5 && r.text.as_deref() == Some(WORD))
            .map(|(l, _)| l)
            .expect("the reader's word is not on that line");
        let at_left = ((target.page_x() - page_left) / page_w) as f32;

        let now = format!("{WORD} \u{914}\u{930} \u{927}\u{930}\u{94d}\u{92e}");
        let (want, text, font) = (WORD.as_bytes(), now.as_bytes(), NIRMALA.as_bytes());
        let buffer = crate::retype_recovered_line(
            handle, 0, AT as f32, at_left,
            want.as_ptr(), want.len(), text.as_ptr(), text.len(), font.as_ptr(), font.len());
        assert_eq!(buffer.status, crate::STATUS_OK_PDFIUM, "the edit was refused");
        let out = unsafe { std::slice::from_raw_parts(buffer.data, buffer.len) }.to_vec();
        crate::free_byte_buffer(buffer);
        crate::close_document(handle);

        let dir = std::env::temp_dir().join("ayaan-readers-edit");
        let _ = std::fs::create_dir_all(&dir);
        std::fs::write(dir.join("before.pdf"), &bytes).unwrap();
        std::fs::write(dir.join("after.pdf"), &out).unwrap();
        raster(&bytes, &dir.join("before.bgra").to_string_lossy());
        raster(&out, &dir.join("after.bgra").to_string_lossy());
        println!("wrote {}", dir.display());

        // What surrounds the text that moved. Every operation between the last
        // `q` before it and the `Q` that closes that, with clips called out.
        let after = Document::load_mem(&out).unwrap();
        let pg = *after.get_pages().values().next().unwrap();
        let content = Content::decode(&after.get_page_content(pg)).unwrap();
        let all = crate::recover::lines_of(&after, pg);
        let moved: Vec<&Line> = all.iter()
            .filter(|l| (l.page_y - 283.44).abs() < 0.5 && l.page_x() < 100.0)
            .collect();
        println!("\n{} placements at the start of the line at 283.44 after the edit", moved.len());
        for l in moved.iter().take(2) {
            let first = *l.drawn_by.first().unwrap();
            let from = first.saturating_sub(14);
            println!("\n  placement at x {:.2}, op {first}", l.page_x());
            for (i, op) in content.operations[from..(first + 3).min(content.operations.len())]
                .iter().enumerate()
            {
                let at = from + i;
                let flag = match op.operator.as_str() {
                    "W" | "W*" => "   <-- CLIP",
                    "re" => "   <-- rect",
                    "q" | "Q" => "   <-- state",
                    _ if at == first => "   <-- THE TEXT",
                    _ => "",
                };
                let args: Vec<String> = op.operands.iter().take(6).map(|o| match o {
                    Object::Real(r) => format!("{r:.2}"),
                    Object::Integer(i) => i.to_string(),
                    Object::Name(n) => format!("/{}", String::from_utf8_lossy(n)),
                    _ => "..".into(),
                }).collect();
                println!("    {at:6} {:<3} {}{flag}", op.operator, args.join(" "));
            }
        }

        // And how many clips the page sets at all.
        let clips = content.operations.iter().filter(|o| o.operator == "W" || o.operator == "W*")
            .count();
        println!("\nthe page sets {clips} clipping paths in {} operations",
            content.operations.len());
    }

    /// ⚠️ WHAT PDFIUM ITSELF HOLDS WHERE THE CARRIED WORDS SHOULD BE. The
    /// stream places them at the start of the lines below, lopdf reads them
    /// there, and PDFium's render shows nothing there. This asks PDFium, object
    /// by object, what it has in that corner of the paragraph, before and after.
    ///
    ///     cargo test --release what_pdfium_holds_where_the_words_went -- --ignored --nocapture
    #[test]
    #[ignore = "diagnostic, and needs files written by what_the_readers_edit_looks_like"]
    fn what_pdfium_holds_where_the_words_went() {
        let dir = std::env::temp_dir().join("ayaan-readers-edit");
        for name in ["before.pdf", "after.pdf"] {
            let path = dir.join(name);
            let Ok(pdf) = std::fs::read(&path) else {
                println!("run what_the_readers_edit_looks_like first");
                return;
            };
            println!("\n================ {name}");
            let handle = crate::open_document_from_bytes_inner(pdf.as_ptr(), pdf.len());
            assert_ne!(handle, 0);
            {
                use pdfium_render::prelude::*;
                let _guard = crate::call_guard();
                let doc = crate::lock(&crate::core().documents).get(&handle).cloned().unwrap();
                let g = crate::lock(&doc);
                let page = g.pages().get(0).unwrap();
                let text_page = page.text().ok();
                let objects = page.objects();
                let mut shown = 0usize;
                for index in 0..objects.len() {
                    let Ok(obj) = objects.get(index) else { continue };
                    let Ok(b) = obj.bounds() else { continue };
                    let (l, r, t, bt) =
                        (b.left().value, b.right().value, b.top().value, b.bottom().value);
                    // The start of the paragraph's second and third lines.
                    if r < 60.0 || l > 130.0 || t < 262.0 || bt > 300.0 {
                        continue;
                    }
                    let what = match &obj {
                        pdfium_render::prelude::PdfPageObject::Text(tx) => {
                            let said = text_page.as_ref().map(|tp| tp.for_object(tx))
                                .unwrap_or_default();
                            format!("text  {:?}", said.chars().take(12).collect::<String>())
                        }
                        other => format!("OTHER {:?}", other.object_type()),
                    };
                    println!("   #{index:5}  x {l:7.2}..{r:7.2}  y {bt:7.2}..{t:7.2}  {what}");
                    shown += 1;
                }
                println!("{shown} objects in that corner, of {} on the page", objects.len());
            }
            crate::close_document(handle);
        }
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
