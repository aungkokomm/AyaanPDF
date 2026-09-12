//! Several visual lines read as ONE piece of text, and a map from every
//! logical character back to the PDF object that draws it.
//!
//! Two positions live here and they are not the same thing:
//!
//!   LOGICAL CHARACTER POSITION  an offset into the block's Unicode text. What
//!                               a reader selects. Reading order. Includes the
//!                               line breaks the block introduces itself.
//!
//!   PDF OBJECT POSITION         (object index, character index inside THAT
//!                               object's own decoded string). What an emitter
//!                               has to splice. Page order, not reading order,
//!                               and for a shaped script not logical order.
//!
//! ⚠️ NOTHING HERE TOUCHES PDFium. It takes lines and objects already read and
//! returns a model, which is what makes the whole thing testable without a
//! document. The gathering lives in `lib.rs` beside the reader it borrows from.
//!
//! ⚠️ AND THE LOGICAL TEXT COMES FROM THE OBJECTS, never from PDFium's
//! character stream. The stream synthesizes separators no object holds, so an
//! offset into it would point at characters the page cannot be asked about.

use std::collections::BTreeMap;

// ---------------------------------------------------------------------------
// why a block will not be edited
// ---------------------------------------------------------------------------

/// Another line's objects sit inside this block's range, so rewriting the range
/// would rewrite text belonging to something else. 283 blocks of the measured
/// corpus.
pub(crate) const BLOCK_INTERLEAVED: u32 = 1;
/// A member line is drawn by objects some other line also draws.
pub(crate) const BLOCK_FOREIGN: u32 = 2;
/// Two member lines claim the same object, so one PDF position stands for two
/// logical characters and the backward map has no single answer. Measured: 19
/// blocks, and Step A saw the same thing from outside as the 0.7% of blocks
/// whose object ranges do not ascend.
pub(crate) const BLOCK_SHARED_OBJECTS: u32 = 3;
/// The page's own text does not decode to Unicode.
pub(crate) const BLOCK_UNDECODABLE: u32 = 4;
/// A member line has no objects offering any text.
pub(crate) const BLOCK_NO_RUNS: u32 = 5;
/// This page carries one of our own invisible logical runs whose visible
/// partner could not be identified, so some run on it may be reading back as
/// shaped glyphs without the model knowing which.
pub(crate) const BLOCK_PATH_B_UNPAIRED: u32 = 6;
/// An invisible logical run sits INSIDE a line's object range. The reader's own
/// concatenation would splice its characters into the middle of the line, so
/// every offset past it means something else.
pub(crate) const BLOCK_LOGICAL_RUN_INSIDE_LINE: u32 = 7;

// ---------------------------------------------------------------------------
// what the model is built from
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(crate) enum ObjectKind {
    /// The page's own text.
    Ordinary,
    /// One of ours: the invisible run carrying a Path B replacement's LOGICAL
    /// characters, marked `AyaanPathB:<id>`.
    LogicalRun,
    /// The visible glyphs a Path B edit drew, identified by pairing rather than
    /// by a mark of its own. Its decoded text is what the shaper produced, so
    /// it is not what the page says.
    ShapedRun,
    /// The invisible run that makes an Ayaan text box searchable.
    SearchRun,
}

/// One text object of a page, exactly as read.
#[derive(Clone)]
pub(crate) struct RawObject {
    /// What PDFium decodes from the glyphs. ⚠️ THE READER'S CONCATENATION IS
    /// BUILT FROM THIS AND NOTHING ELSE, because `prefix` and `suffix` are
    /// offsets into precisely that string.
    pub drawn: String,
    /// What the page actually says, where that differs: a shaped run's logical
    /// characters, recovered from its invisible partner.
    pub logical: Option<String>,
    pub kind: ObjectKind,
    pub font: String,
    pub size: f32,
    pub color: u32,
}

/// One visual line, as `page_lines` already produces it.
#[derive(Clone)]
pub(crate) struct RawLine {
    pub first: usize,
    pub last: usize,
    /// Characters of the concatenation before the line starts, and after it
    /// ends. ⚠️ BOTH. `get_page_lines` publishes only the first of the pair, so
    /// anything reading the model through that FFI has to guess the second;
    /// here they are both simply present.
    pub prefix: usize,
    pub suffix: usize,
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
    /// In PDF points, which is what an emitter's anchor is matched against.
    pub baseline: f32,
    pub size: f32,
    pub refusal: u32,
    pub font: String,
}

impl RawLine {
    fn height(&self) -> f32 {
        (self.bottom - self.top).abs().max(1e-6)
    }

    fn stacked(&self) -> Stacked<'_> {
        Stacked {
            font: &self.font,
            size: self.size,
            left: self.left,
            baseline: self.baseline,
            height: self.height(),
        }
    }
}

// ---------------------------------------------------------------------------
// the model
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(crate) struct ObjPos {
    pub object: usize,
    pub at: usize,
}

/// Where one LOGICAL character came from.
#[derive(Clone, Copy, Debug)]
pub(crate) enum Source {
    /// A boundary the block introduced. Belongs to no object, which is the
    /// point: an edit landing here is a line break, not a character.
    Break,
    Char {
        line: u32,
        run: u32,
        pos: ObjPos,
        /// The same character's offset inside its line's DRAWN string, which is
        /// what an emitter's `at` counts.
        drawn_at: u32,
        /// The block wrote a plain space where the page held something else: a
        /// tab, a non-breaking space, or a run of several. `pos` is the first
        /// character of that run, so it is still a real place on the page.
        synthesized: bool,
    },
    /// Inside a run whose logical characters do not correspond one for one to
    /// anything drawn. A Path B replacement is four codepoints and two glyphs;
    /// there is no third character to point at. The run is the unit.
    Atomic { line: u32, run: u32 },
}

/// One maximal stretch of a line drawn by a single text object.
///
/// ⚠️ `logical_len` AND `drawn_len` ARE DIFFERENT NUMBERS. The first counts what
/// a reader sees, the second counts what an emitter must cover: whitespace the
/// normalisation dropped is in the second and not the first, and a shaped run's
/// two counts have nothing to do with each other at all.
#[derive(Clone)]
pub(crate) struct Run {
    pub object: usize,
    pub obj_at: usize,
    pub obj_span: usize,
    pub logical_len: usize,
    /// Where this run sits inside its line's drawn string.
    pub drawn_at: usize,
    pub drawn_len: usize,
    pub atomic: bool,
    pub font: String,
    pub size: f32,
    pub color: u32,
}

#[derive(Clone)]
pub(crate) struct BlockLine {
    pub runs: Vec<Run>,
    pub first_object: usize,
    pub last_object: usize,
    /// The concatenation of this line's objects, sliced by `prefix` and
    /// `suffix`. ⚠️ WHAT AN EMITTER COMPARES ITS ANCHOR AGAINST, so it is kept
    /// exactly as drawn: unsqueezed, unsubstituted.
    pub drawn: String,
    pub left: f32,
    pub right: f32,
    pub baseline: f32,
    pub height: f32,
    pub size: f32,
    pub font: String,
    pub refusal: u32,
    /// The line's own stretch of the block's logical text, the `\n` excluded.
    pub start: usize,
    pub end: usize,
}

#[derive(Clone)]
pub(crate) struct Block {
    pub text: String,
    pub map: Vec<Source>,
    pub lines: Vec<BlockLine>,
    /// In line heights. Positive means the first line starts further right.
    pub first_line_indent: f32,
    pub refusals: Vec<u32>,
}

impl Block {
    pub fn editable(&self) -> bool {
        self.refusals.is_empty()
    }
}

// ---------------------------------------------------------------------------
// grouping
// ---------------------------------------------------------------------------

// ⚠️ STEP A'S MEASURED RULE, AND THE NUMBERS BELONG TO IT. Across 154 documents
// and 3971 lines it produced 2854 blocks with 20 false merges and about 337
// false splits, which is deliberately the conservative direction: leaving a
// paragraph in two pieces is recoverable, joining two that are not one is not.
// Changing any of these without re-running that corpus makes every figure in
// the record a claim about a rule that no longer exists.
const SAME_SIZE: f32 = 0.01; // points
const SAME_LEFT: f32 = 0.25; // line heights
const LEADING_TOLERANCE: f32 = 0.25;
const MAX_LEADING: f32 = 2.5;
const INDENT_MIN: f32 = 0.5;
const INDENT_MAX: f32 = 4.0;

/// How far `b`'s baseline sits BELOW `a`'s, in `a`'s line heights.
///
/// ⚠️ SIGNED, AND NEVER `abs()`. The sort is by the ink top, which is not the
/// baseline: two lines can be ordered one way by their tops and the other way
/// by their baselines, and Step A's rule refuses that pair as "the same line or
/// above". Taking the magnitude instead joined 8 blocks the measured corpus
/// keeps apart, which is how this was caught.
fn leading_below(a: &RawLine, b: &RawLine) -> f32 {
    leading(&a.stacked(), &b.stacked())
}

fn joins(a: &RawLine, b: &RawLine, established: Option<f32>, a_is_block_start: bool) -> bool {
    stacks(&a.stacked(), &b.stacked(), established, a_is_block_start)
}

/// The only things that decide whether one line stacks under another.
///
/// ⚠️ HERE SO THERE IS EXACTLY ONE COPY OF THE RULE. Two readers find lines on
/// a page: this one, through PDFium's objects, and `recover::lines_of`, through
/// the content stream. Both need to know where a paragraph ends, and a second
/// copy of the rule would drift from this one the first time either was tuned.
pub(crate) struct Stacked<'a> {
    pub font: &'a str,
    pub size: f32,
    pub left: f32,
    pub baseline: f32,
    /// How tall the line's ink is. Never zero: it is what everything else is
    /// measured in.
    pub height: f32,
}

/// How far `b`'s baseline sits BELOW `a`'s, in `a`'s line heights.
///
/// ⚠️ SIGNED, AND NEVER `abs()`. The sort is by the ink top, which is not the
/// same order as the baseline for lines of different sizes.
pub(crate) fn leading(a: &Stacked, b: &Stacked) -> f32 {
    (a.baseline - b.baseline) / a.height.abs().max(1e-6)
}

/// Whether `b` is the next line of the same paragraph as `a`.
pub(crate) fn stacks(
    a: &Stacked,
    b: &Stacked,
    established: Option<f32>,
    a_is_block_start: bool,
) -> bool {
    let h = a.height.abs().max(1e-6);
    let leading = leading(a, b);
    if b.font != a.font {
        return false;
    }
    if (b.size - a.size).abs() >= SAME_SIZE {
        return false;
    }
    if leading <= 0.05 || leading > MAX_LEADING {
        return false;
    }
    if let Some(e) = established {
        if (leading - e).abs() > LEADING_TOLERANCE {
            return false;
        }
    }
    let dl = (b.left - a.left) / h;
    if dl.abs() <= SAME_LEFT {
        return true;
    }
    // A first-line indent: the FIRST line of a block sits further right.
    a_is_block_start && -dl >= INDENT_MIN && -dl <= INDENT_MAX
}

/// Groups lines already sorted into reading order into paragraphs, as indices.
pub(crate) fn stack_up(lines: &[Stacked]) -> Vec<Vec<usize>> {
    let mut out = Vec::new();
    let mut i = 0usize;
    while i < lines.len() {
        let mut members = vec![i];
        let mut established: Option<f32> = None;
        let mut j = i;
        while j + 1 < lines.len()
            && stacks(&lines[j], &lines[j + 1], established, members.len() == 1)
        {
            if established.is_none() {
                established = Some(leading(&lines[j], &lines[j + 1]));
            }
            members.push(j + 1);
            j += 1;
        }
        out.push(members);
        i = j + 1;
    }
    out
}

fn group(lines: &[RawLine]) -> Vec<Vec<usize>> {
    let stacked: Vec<Stacked> = lines.iter().map(RawLine::stacked).collect();
    stack_up(&stacked)
}

// ---------------------------------------------------------------------------
// one line
// ---------------------------------------------------------------------------

/// Every character of `first..=last`, in page order, each carrying where it came
/// from.
///
/// ⚠️ THE READER'S OWN CONCATENATION RULE, AND IT HAS TO BE. `prefix` and
/// `suffix` are offsets into exactly this string, so leaving anything out, a
/// whitespace-only object included, moves every offset after it.
fn drawn_chars(objects: &BTreeMap<usize, RawObject>, first: usize, last: usize) -> Vec<(char, ObjPos)> {
    let mut out = Vec::new();
    for i in first..=last {
        let Some(o) = objects.get(&i) else { continue };
        for (at, c) in o.drawn.chars().enumerate() {
            out.push((c, ObjPos { object: i, at }));
        }
    }
    out
}

/// `split_whitespace().join(" ")` with every character's origin kept.
///
/// ⚠️ THE READER DOES EXACTLY THIS, DESTRUCTIVELY, AND ONLY FOR MULTI-OBJECT
/// LINES. That is the single reason a block cannot be built on `LineCluster`'s
/// own text: the same string comes out, but nothing in it can be traced back.
/// Producers put the separator in BOTH neighbours, so joining two objects
/// otherwise gives a space the page does not draw.
fn normalise(raw: &[(char, ObjPos)], multi_object: bool) -> Vec<(usize, bool)> {
    let mut out: Vec<(usize, bool)> = Vec::new();
    if !multi_object {
        // A line living in ONE object is left exactly as it is: that string is
        // what a write verifies its span against.
        out.extend((0..raw.len()).map(|i| (i, false)));
        return out;
    }
    let mut i = 0usize;
    let mut wrote_word = false;
    while i < raw.len() {
        if raw[i].0.is_whitespace() {
            let start = i;
            while i < raw.len() && raw[i].0.is_whitespace() {
                i += 1;
            }
            if wrote_word && i < raw.len() {
                out.push((start, !(i - start == 1 && raw[start].0 == ' ')));
            }
            continue;
        }
        out.push((i, false));
        wrote_word = true;
        i += 1;
    }
    out
}

struct Built {
    line: BlockLine,
    text: String,
    sources: Vec<Source>,
    /// An invisible logical run was spliced into the middle of this line.
    logical_run_inside: bool,
}

fn build_line(l: &RawLine, objects: &BTreeMap<usize, RawObject>, index: u32) -> Built {
    let raw = drawn_chars(objects, l.first, l.last);
    let multi = l.first != l.last;

    let lo = l.prefix.min(raw.len());
    let hi = raw.len().saturating_sub(l.suffix).max(lo);
    let slice = &raw[lo..hi];

    let drawn: String = slice.iter().map(|(c, _)| *c).collect();
    let logical_run_inside = (l.first..=l.last)
        .filter_map(|i| objects.get(&i))
        .any(|o| o.kind == ObjectKind::LogicalRun);

    let emitted = normalise(slice, multi);

    // Where each character of `slice` starts inside `drawn`, in characters.
    // They are the same sequence, so the index is the offset.
    let mut runs: Vec<Run> = Vec::new();
    let mut sources: Vec<Source> = Vec::new();
    let mut text = String::new();

    for (raw_i, synthesized) in emitted {
        let (c, pos) = slice[raw_i];
        let object = objects.get(&pos.object);
        let atomic = object.is_some_and(|o| o.logical.is_some());

        let extend = runs.last().is_some_and(|r| r.object == pos.object);
        if !extend {
            runs.push(Run {
                object: pos.object,
                obj_at: pos.at,
                obj_span: 0,
                logical_len: 0,
                drawn_at: raw_i,
                drawn_len: 0,
                atomic,
                font: object.map(|o| o.font.clone()).unwrap_or_default(),
                size: object.map(|o| o.size).unwrap_or(l.size),
                color: object.map(|o| o.color).unwrap_or(0),
            });
        }
        let run_index = (runs.len() - 1) as u32;
        let run = runs.last_mut().unwrap();
        run.obj_span = pos.at + 1 - run.obj_at;
        run.drawn_len = raw_i + 1 - run.drawn_at;

        run.logical_len += 1;
        text.push(if synthesized { ' ' } else { c });
        sources.push(Source::Char {
            line: index,
            run: run_index,
            pos,
            drawn_at: raw_i as u32,
            synthesized,
        });
    }

    // ⚠️ SUBSTITUTED AFTER THE WALK, NEVER DURING IT. The logical text is a
    // different length from what the run draws, so putting it in earlier would
    // move every drawn offset after it. Here the drawn side is already settled
    // and only the logical side changes.
    //
    // ⚠️ AND ONLY THE RUN'S NON-BLANK CORE IS REPLACED. A visible replacement
    // often draws a trailing separator that its invisible partner does not
    // carry, so swapping the run whole would run the replacement into the next
    // word. The whitespace at either end keeps its own place on the page and
    // stays addressable; only the glyphs nothing corresponds to become atomic.
    if runs.iter().any(|r| r.atomic) {
        let mut chars: Vec<char> = text.chars().collect();
        let mut starts: Vec<usize> = Vec::with_capacity(runs.len());
        let mut acc = 0usize;
        for r in &runs {
            starts.push(acc);
            acc += r.logical_len;
        }
        // Backwards, so a replacement never moves a range not yet reached.
        for k in (0..runs.len()).rev() {
            if !runs[k].atomic {
                continue;
            }
            let from = starts[k];
            let to = from + runs[k].logical_len;
            let lead = chars[from..to].iter().take_while(|c| c.is_whitespace()).count();
            let trail = chars[from..to]
                .iter()
                .rev()
                .take_while(|c| c.is_whitespace())
                .count();
            let (core_from, core_to) = (from + lead, to - trail);
            if core_from >= core_to {
                continue; // draws nothing but space; there is no shaping to hide
            }
            let logical: Vec<char> = objects[&runs[k].object]
                .logical
                .clone()
                .unwrap_or_default()
                .chars()
                .collect();
            let n = logical.len();
            chars.splice(core_from..core_to, logical);
            sources.splice(
                core_from..core_to,
                std::iter::repeat(Source::Atomic {
                    line: index,
                    run: k as u32,
                })
                .take(n),
            );
            runs[k].logical_len = lead + n + trail;
        }
        text = chars.into_iter().collect();
    }

    Built {
        line: BlockLine {
            runs,
            first_object: l.first,
            last_object: l.last,
            drawn,
            left: l.left,
            right: l.right,
            baseline: l.baseline,
            height: l.height(),
            size: l.size,
            font: l.font.clone(),
            refusal: l.refusal,
            start: 0,
            end: 0,
        },
        text,
        sources,
        logical_run_inside,
    }
}

// ---------------------------------------------------------------------------
// assembling
// ---------------------------------------------------------------------------

/// The refusal code the reader gives a line whose range some other line also
/// draws from. Kept as a number rather than an import so this module stays free
/// of the reader.
const LINE_FOREIGN_OBJECT: u32 = 9;

/// Blocks, in reading order, from lines and objects already read.
pub(crate) fn assemble(
    mut lines: Vec<RawLine>,
    objects: &BTreeMap<usize, RawObject>,
    unpaired_shaped_runs: bool,
) -> Vec<Block> {
    // ⚠️ READING ORDER, NOT THE ORDER THE READER RETURNS. Measured in Step A:
    // sorting geometrically is what makes "the next line down the page" mean
    // that. Lines sharing a baseline are side-by-side content the reader has
    // already separated, and refusing to stack them is correct.
    //
    // ⚠️ AND THESE ARE PDF COORDINATES, so down the page is DECREASING y. The
    // reader hands out page space here and only flips to the app's top-left
    // when it serializes; sorting ascending would read every page upwards.
    lines.sort_by(|a, b| {
        b.top
            .partial_cmp(&a.top)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then(a.left.partial_cmp(&b.left).unwrap_or(std::cmp::Ordering::Equal))
    });

    let mut out = Vec::new();
    for members in group(&lines) {
        let mut text = String::new();
        let mut map: Vec<Source> = Vec::new();
        let mut block_lines: Vec<BlockLine> = Vec::new();
        let mut refusals: Vec<u32> = Vec::new();

        for (n, &li) in members.iter().enumerate() {
            let mut built = build_line(&lines[li], objects, n as u32);
            if built.logical_run_inside && !refusals.contains(&BLOCK_LOGICAL_RUN_INSIDE_LINE) {
                refusals.push(BLOCK_LOGICAL_RUN_INSIDE_LINE);
            }
            if n > 0 {
                text.push('\n');
                map.push(Source::Break);
            }
            built.line.start = map.len();
            text.push_str(&built.text);
            map.append(&mut built.sources);
            built.line.end = map.len();
            block_lines.push(built.line);
        }

        let lo = block_lines.iter().map(|l| l.first_object).min().unwrap_or(0);
        let hi = block_lines.iter().map(|l| l.last_object).max().unwrap_or(0);
        if lines
            .iter()
            .enumerate()
            .any(|(i, o)| !members.contains(&i) && o.first >= lo && o.last <= hi)
        {
            refusals.push(BLOCK_INTERLEAVED);
        }
        if block_lines.iter().any(|l| l.refusal == LINE_FOREIGN_OBJECT) {
            refusals.push(BLOCK_FOREIGN);
        }
        if block_lines
            .windows(2)
            .any(|w| w[1].first_object <= w[0].last_object)
        {
            refusals.push(BLOCK_SHARED_OBJECTS);
        }
        if text.contains('\u{fffd}') {
            refusals.push(BLOCK_UNDECODABLE);
        }
        if block_lines.iter().any(|l| l.runs.is_empty()) {
            refusals.push(BLOCK_NO_RUNS);
        }
        if unpaired_shaped_runs {
            refusals.push(BLOCK_PATH_B_UNPAIRED);
        }

        let first_line_indent = if block_lines.len() > 1 {
            let rest = block_lines[1..]
                .iter()
                .map(|l| l.left)
                .fold(f32::MAX, f32::min);
            (block_lines[0].left - rest) / block_lines[0].height
        } else {
            0.0
        };

        out.push(Block {
            text,
            map,
            lines: block_lines,
            first_line_indent,
            refusals,
        });
    }
    out
}

// ---------------------------------------------------------------------------
// the map, both ways
// ---------------------------------------------------------------------------

/// A contiguous stretch of ONE object, which is what an emitter can splice.
#[derive(Debug, PartialEq, Eq)]
pub(crate) struct Span {
    pub object: usize,
    pub at: usize,
    pub len: usize,
}

/// LOGICAL to PDF. The lines touched, the object stretches under them, and how
/// many line breaks the selection swallowed.
pub(crate) fn resolve(b: &Block, start: usize, len: usize) -> (Vec<u32>, Vec<Span>, usize) {
    let mut touched: Vec<u32> = Vec::new();
    let mut spans: Vec<Span> = Vec::new();
    let mut breaks = 0usize;
    for i in start..(start + len).min(b.map.len()) {
        match b.map[i] {
            Source::Break => breaks += 1,
            Source::Atomic { line, run } => {
                if !touched.contains(&line) {
                    touched.push(line);
                }
                let r = &b.lines[line as usize].runs[run as usize];
                let span = Span {
                    object: r.object,
                    at: r.obj_at,
                    len: r.obj_span,
                };
                if spans.last() != Some(&span) {
                    spans.push(span);
                }
            }
            Source::Char { line, pos, .. } => {
                if !touched.contains(&line) {
                    touched.push(line);
                }
                let extend = spans
                    .last()
                    .is_some_and(|s| s.object == pos.object && s.at + s.len == pos.at);
                if extend {
                    spans.last_mut().unwrap().len += 1;
                } else {
                    spans.push(Span {
                        object: pos.object,
                        at: pos.at,
                        len: 1,
                    });
                }
            }
        }
    }
    (touched, spans, breaks)
}

/// PDF to LOGICAL. `None` when the position is not part of the block: a
/// character the normalisation dropped, one inside an atomic run, or an object
/// outside it.
pub(crate) fn offset_of(b: &Block, want: ObjPos) -> Option<usize> {
    b.map
        .iter()
        .position(|s| matches!(s, Source::Char { pos, .. } if *pos == want))
}

// ---------------------------------------------------------------------------
// turning a selection into an edit
// ---------------------------------------------------------------------------

/// What an emitter needs to rewrite a selection, in the terms it already
/// speaks: an anchor, the line it expects to find there, and a span of BYTES
/// inside it.
pub(crate) struct LineEdit {
    pub line: usize,
    pub baseline: f32,
    pub expected: String,
    pub at: usize,
    pub len: usize,
}

/// The block's own text, `start` and `len` in logical characters, translated
/// into the one line and byte span it turns out to be.
///
/// ⚠️ ONE LINE. A selection crossing a break is a paragraph edit, which needs
/// reflow to say where the text after it goes, and reflow is not built. It is
/// refused here rather than half-done.
pub(crate) fn plan(
    b: &Block,
    start: usize,
    len: usize,
    spans_lines: i32,
    not_addressable: i32,
    not_editable: i32,
    invalid: i32,
) -> Result<LineEdit, i32> {
    if !b.editable() {
        return Err(not_editable);
    }
    if len == 0 || start + len > b.map.len() {
        return Err(invalid);
    }

    let mut line: Option<u32> = None;
    let mut lo = usize::MAX;
    let mut hi = 0usize;
    for entry in &b.map[start..start + len] {
        match *entry {
            Source::Break => return Err(spans_lines),
            // A shaped replacement's codepoints have no character-by-character
            // place on the page: four of them are drawn by two glyphs, and
            // which glyph is the third is not a question with an answer.
            Source::Atomic { .. } => return Err(not_addressable),
            Source::Char {
                line: l, drawn_at, ..
            } => {
                if *line.get_or_insert(l) != l {
                    return Err(spans_lines);
                }
                lo = lo.min(drawn_at as usize);
                hi = hi.max(drawn_at as usize);
            }
        }
    }
    let Some(line) = line else {
        return Err(invalid);
    };
    let l = &b.lines[line as usize];

    // A run the model calls atomic anywhere in this line makes the line's drawn
    // string something other than what it says; refuse rather than splice into
    // it by offset.
    if l.runs.iter().any(|r| r.atomic) {
        return Err(not_addressable);
    }

    // ⚠️ CHARACTERS IN, BYTES OUT. An emitter's `at` counts bytes of the line
    // as drawn, and the two agree only while the line is ASCII. Converting here
    // rather than hoping means a line that is not ASCII fails on its anchor,
    // which is the existing behaviour, instead of silently splicing at the
    // wrong place.
    //
    // The span runs from the first selected character THROUGH the last, so any
    // whitespace the normalisation dropped between them is inside it. That is
    // deliberate: it is being replaced, and the replacement carries its own.
    let chars: Vec<(usize, char)> = l.drawn.char_indices().collect();
    if hi >= chars.len() {
        return Err(invalid);
    }
    let at = chars[lo].0;
    let end = chars[hi].0 + chars[hi].1.len_utf8();

    Ok(LineEdit {
        line: line as usize,
        baseline: l.baseline,
        expected: l.drawn.clone(),
        at,
        len: end - at,
    })
}


// ---------------------------------------------------------------------------
// laying an edited block back out over its lines
// ---------------------------------------------------------------------------

/// One line's worth of change, in the block's own logical characters.
pub(crate) struct LineChange {
    pub line: usize,
    /// Where the change starts, as an offset into the BLOCK's logical text.
    pub at: usize,
    /// How many logical characters it replaces. Never zero: see `narrow`.
    pub len: usize,
    pub text: String,
}

/// The smallest replacement turning `old` into `new`, or `None` if they agree.
///
/// ⚠️ NEVER ZERO-LENGTH, AND THAT IS DELIBERATE. Path A's `splice` walks the
/// `TJ` array looking for a span to cut, and an empty span that falls exactly
/// between two string operands matches neither of them: the insertion is
/// written nowhere and the call still reports success. Widening a pure
/// insertion by one character on either side turns every edit into a
/// replacement, which is the case the emitters are proven on, and the page ends
/// up saying the same thing.
fn narrow(old: &[char], new: &[char]) -> Option<(usize, usize, String)> {
    if old == new {
        return None;
    }
    let mut p = 0usize;
    while p < old.len() && p < new.len() && old[p] == new[p] {
        p += 1;
    }
    let mut s = 0usize;
    while s < old.len() - p && s < new.len() - p && old[old.len() - 1 - s] == new[new.len() - 1 - s]
    {
        s += 1;
    }

    let (mut a, mut b) = (p, old.len() - s);
    let (mut c, mut d) = (p, new.len() - s);
    if a == b {
        if a > 0 {
            a -= 1;
            c -= 1;
        } else if b < old.len() {
            b += 1;
            d += 1;
        } else {
            // Nothing on the page to widen against: the line draws nothing.
            return None;
        }
    }
    Some((a, b - a, new[c..d].iter().collect()))
}

/// The edited block's text, laid back over the lines it came from.
///
/// ⚠️ ASSIGNMENT, NOT REFLOW. Every line keeps its own place on the page and
/// takes whatever text now sits between the same two breaks. Changing how many
/// lines there are, or moving a word from one line to the next, decides where
/// everything after it goes, and that is reflow: it is refused here rather than
/// half-done. Nothing else about the block moves, so a line that grew or shrank
/// is the emitter's problem to accept or refuse on width, which it already
/// knows how to do.
pub(crate) fn lay_out(b: &Block, edited: &str, needs_reflow: i32) -> Result<Vec<LineChange>, i32> {
    let wanted: Vec<&str> = edited.split('\n').collect();
    if wanted.len() != b.lines.len() {
        return Err(needs_reflow);
    }

    let text: Vec<char> = b.text.chars().collect();
    let mut out = Vec::new();
    for (i, line) in b.lines.iter().enumerate() {
        let old = &text[line.start..line.end];
        let new: Vec<char> = wanted[i].chars().collect();
        let Some((at, len, replacement)) = narrow(old, &new) else {
            continue;
        };
        out.push(LineChange {
            line: i,
            at: line.start + at,
            len,
            text: replacement,
        });
    }
    Ok(out)
}

/// The paragraph's lines refilled from `first` onwards, so no line from there
/// is wider than the room it has.
///
/// ⚠️ LINES BEFORE `first` ARE NOT TOUCHED. An edit on the fifth line has no
/// business moving the second, and refilling the whole paragraph would put
/// every line at risk of a refusal for the sake of one. `emit_block` is all or
/// nothing, so every line this moves is a line that can refuse the edit.
///
/// ⚠️ AND THE WORDS ARE THE ONLY THING THAT MOVES. No line changes its
/// place, its font or its size: this decides which words sit on which of the
/// lines the paragraph already has, and nothing else. A paragraph that needs a
/// line it has not got comes back as `None`, because drawing a line that was
/// never there is a different piece of work.
///
/// `fits` is asked whether a candidate line's text sits in line `i`'s room.
pub(crate) fn rewrap_from(
    lines: &[String],
    first: usize,
    fits: impl Fn(usize, &str) -> bool,
) -> Option<Vec<String>> {
    if first >= lines.len() {
        return None;
    }

    // Every word from the edited line to the end of the paragraph, which is
    // exactly the text that is allowed to move.
    let mut words: std::collections::VecDeque<&str> =
        lines[first..].iter().flat_map(|l| l.split_whitespace()).collect();

    let mut out: Vec<String> = lines[..first].to_vec();
    for i in first..lines.len() {
        let mut line = String::new();
        while let Some(word) = words.front() {
            let candidate = if line.is_empty() {
                (*word).to_string()
            } else {
                format!("{line} {word}")
            };
            // ⚠️ A WORD TOO WIDE FOR ANY LINE STILL HAS TO GO SOMEWHERE. A
            // single word longer than the measure is a real thing in a narrow
            // column, and breaking out of an empty line would drop it and spin.
            if !line.is_empty() && !fits(i, &candidate) {
                break;
            }
            line = candidate;
            words.pop_front();
        }
        out.push(line);
    }

    // Words left over means the paragraph has to GAIN a line.
    if words.is_empty() { Some(out) } else { None }
}

// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    fn object(drawn: &str) -> RawObject {
        RawObject {
            drawn: drawn.to_string(),
            logical: None,
            kind: ObjectKind::Ordinary,
            font: "F".into(),
            size: 12.0,
            color: 0,
        }
    }

    fn shaped(drawn: &str, logical: &str) -> RawObject {
        RawObject {
            logical: Some(logical.to_string()),
            kind: ObjectKind::ShapedRun,
            ..object(drawn)
        }
    }

    fn objects(items: &[(usize, RawObject)]) -> BTreeMap<usize, RawObject> {
        items.iter().cloned().collect()
    }

    /// A line at `baseline`, drawn by `first..=last`, nothing sliced off.
    fn line(first: usize, last: usize, baseline: f32) -> RawLine {
        RawLine {
            first,
            last,
            prefix: 0,
            suffix: 0,
            left: 100.0,
            top: baseline + 9.0,
            right: 300.0,
            bottom: baseline - 3.0,
            baseline,
            size: 12.0,
            refusal: 0,
            font: "F".into(),
        }
    }

    fn one(lines: Vec<RawLine>, objs: BTreeMap<usize, RawObject>) -> Block {
        let mut blocks = assemble(lines, &objs, false);
        assert_eq!(blocks.len(), 1, "expected exactly one block");
        blocks.remove(0)
    }

    /// Every character resolves to the place it came from, and nothing is
    /// claimed twice. The property the whole model rests on.
    fn assert_lossless(b: &Block) {
        let chars: Vec<char> = b.text.chars().collect();
        assert_eq!(chars.len(), b.map.len(), "text and map are different lengths");
        let mut claimed: Vec<ObjPos> = Vec::new();
        for (i, s) in b.map.iter().enumerate() {
            let Source::Char { pos, .. } = *s else { continue };
            assert_eq!(offset_of(b, pos), Some(i), "{pos:?} did not come back as {i}");
            assert!(!claimed.contains(&pos), "{pos:?} is claimed twice");
            claimed.push(pos);
        }
    }

    #[test]
    fn a_single_object_line_is_kept_exactly_as_drawn() {
        let b = one(
            vec![line(0, 0, 700.0)],
            objects(&[(0, object("The  quick  fox"))]),
        );
        // NOT SQUEEZED. That string is what a write verifies its span against,
        // so a doubled space in it is real.
        assert_eq!(b.text, "The  quick  fox");
        assert_eq!(b.lines[0].drawn, "The  quick  fox");
        assert_lossless(&b);
    }

    #[test]
    fn a_multi_object_line_maps_across_the_join() {
        let objs = objects(&[
            (0, object("The quick ")),
            (1, object("brown")),
            (2, object(" fox")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        assert_eq!(b.text, "The quick brown fox");
        assert_eq!(b.lines[0].runs.len(), 3);
        assert_lossless(&b);

        // The 'b' of "brown" is object 1's first character, and asking the
        // other way round gets back to the same offset.
        let at = b.text.find("brown").unwrap();
        let Source::Char { pos, .. } = b.map[at] else { panic!("not a character") };
        assert_eq!(pos, ObjPos { object: 1, at: 0 });
        assert_eq!(offset_of(&b, ObjPos { object: 1, at: 0 }), Some(at));
    }

    #[test]
    fn a_separator_both_neighbours_hold_becomes_one_space_with_a_place() {
        // Producers put the separator in BOTH objects, so the concatenation has
        // a space the page does not draw.
        let objs = objects(&[(0, object("A ")), (1, object(" ragged"))]);
        let b = one(vec![line(0, 1, 700.0)], objs);
        assert_eq!(b.text, "A ragged");
        assert_lossless(&b);

        let Source::Char { pos, synthesized, .. } = b.map[1] else { panic!() };
        assert!(synthesized, "a two-character run collapsed to one is synthesized");
        assert_eq!(pos, ObjPos { object: 0, at: 1 }, "it points at the first of them");
        // And the drawn string still has both.
        assert_eq!(b.lines[0].drawn, "A  ragged");
    }

    #[test]
    fn a_blank_object_between_two_words_keeps_the_offsets_right() {
        // THE ONE `get_page_text_objects` DROPS. `prefix` and `suffix` index a
        // concatenation that contains it, so leaving it out moves everything
        // after it.
        let objs = objects(&[
            (0, object("Hello")),
            (1, object("   ")),
            (2, object("world")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        assert_eq!(b.text, "Hello world");
        assert_eq!(b.lines[0].drawn, "Hello   world");
        assert_lossless(&b);
        // 'w' is object 2's first character, not object 1's.
        let at = b.text.find('w').unwrap();
        let Source::Char { pos, .. } = b.map[at] else { panic!() };
        assert_eq!(pos, ObjPos { object: 2, at: 0 });
    }

    #[test]
    fn prefix_and_suffix_slice_the_line_out_of_its_objects() {
        let mut l = line(0, 0, 700.0);
        l.prefix = 2;
        l.suffix = 3;
        let b = one(vec![l], objects(&[(0, object("xxHello worldyyy"))]));
        assert_eq!(b.text, "Hello world");
        assert_eq!(b.lines[0].drawn, "Hello world");
        // The first character is object 0's THIRD, which is what having both
        // halves of the pair is for.
        let Source::Char { pos, .. } = b.map[0] else { panic!() };
        assert_eq!(pos, ObjPos { object: 0, at: 2 });
        assert_lossless(&b);
    }

    #[test]
    fn a_shaped_run_reads_logically_and_keeps_its_separators() {
        // What Path B leaves behind: glyphs that decode to nothing anyone
        // typed, and an invisible partner holding what the page really says.
        let objs = objects(&[
            (0, object("The quick ")),
            (1, shaped("\u{194}\u{93e} ", "\u{915}\u{94d}\u{937}\u{93e}")),
            (2, object(" fox")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        assert_eq!(b.text, "The quick \u{915}\u{94d}\u{937}\u{93e} fox");
        // The glyphs are still what an emitter would have to cover.
        assert_eq!(b.lines[0].drawn, "The quick \u{194}\u{93e}  fox");

        let at = b.text.find('\u{915}').unwrap();
        // Four codepoints, and not one of them has a place of its own.
        for i in at..at + 4 {
            assert!(matches!(b.map[i], Source::Atomic { .. }), "offset {i}");
        }
        // The separator after it is an ordinary character with a real place.
        assert!(matches!(b.map[at + 4], Source::Char { .. }));
    }

    #[test]
    fn a_selection_inside_a_shaped_run_is_refused_rather_than_spliced() {
        let objs = objects(&[
            (0, object("The quick ")),
            (1, shaped("\u{194}\u{93e} ", "\u{915}\u{94d}\u{937}\u{93e}")),
            (2, object(" fox")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        let at = b.text.find('\u{915}').unwrap();
        assert_eq!(plan(&b, at, 2, 14, 16, 15, 1).err(), Some(16));
        // AND SO IS THE REST OF THE LINE. The line's drawn string is not what
        // it says, so an offset into it means nothing anywhere.
        assert_eq!(plan(&b, 0, 3, 14, 16, 15, 1).err(), Some(16));
    }

    #[test]
    fn two_lines_become_one_block_with_the_break_belonging_to_no_object() {
        let objs = objects(&[(0, object("first line")), (1, object("second line"))]);
        let b = one(vec![line(0, 0, 700.0), line(1, 1, 686.0)], objs);
        assert_eq!(b.text, "first line\nsecond line");
        assert_eq!(b.lines.len(), 2);
        assert!(matches!(b.map[10], Source::Break));
        assert_lossless(&b);

        // Resolving across the break reports both lines and neither claims the
        // break as a character.
        let (touched, spans, breaks) = resolve(&b, 8, 5);
        assert_eq!(touched, vec![0, 1]);
        assert_eq!(breaks, 1);
        assert_eq!(spans.len(), 2);
    }

    #[test]
    fn a_selection_crossing_a_break_is_refused() {
        let objs = objects(&[(0, object("first line")), (1, object("second line"))]);
        let b = one(vec![line(0, 0, 700.0), line(1, 1, 686.0)], objs);
        assert_eq!(plan(&b, 8, 5, 14, 16, 15, 1).err(), Some(14));
    }

    #[test]
    fn a_plan_gives_byte_offsets_into_the_line_as_drawn() {
        let objs = objects(&[
            (0, object("The quick ")),
            (1, object("brown")),
            (2, object(" fox")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        let at = b.text.find("brown").unwrap();
        let p = plan(&b, at, 5, 14, 16, 15, 1).expect("refused");
        assert_eq!(p.line, 0);
        assert_eq!(p.baseline, 700.0);
        assert_eq!(p.expected, "The quick brown fox");
        assert_eq!(&p.expected[p.at..p.at + p.len], "brown");
    }

    #[test]
    fn a_plan_covers_the_whitespace_the_normalisation_dropped() {
        // Selecting across the join must hand an emitter a span that includes
        // the second copy of the separator, or the replacement lands next to a
        // space the page still draws.
        let objs = objects(&[(0, object("A ")), (1, object(" ragged"))]);
        let b = one(vec![line(0, 1, 700.0)], objs);
        assert_eq!(b.text, "A ragged");
        let p = plan(&b, 0, 8, 14, 16, 15, 1).expect("refused");
        assert_eq!(p.expected, "A  ragged");
        assert_eq!(&p.expected[p.at..p.at + p.len], "A  ragged");
    }

    #[test]
    fn a_block_whose_lines_share_an_object_is_refused() {
        // One PDF position would stand for two logical characters, and the
        // backward map would have no single answer.
        let objs = objects(&[
            (0, object("first")),
            (1, object("shared")),
            (2, object("second")),
        ]);
        let b = one(vec![line(0, 1, 700.0), line(1, 2, 686.0)], objs);
        assert!(!b.editable());
        assert!(b.refusals.contains(&BLOCK_SHARED_OBJECTS));
        assert_eq!(plan(&b, 0, 3, 14, 16, 15, 1).err(), Some(15));
    }

    #[test]
    fn an_unpaired_logical_run_refuses_every_block_on_the_page() {
        // Some run on this page may be reading back as shaped glyphs and the
        // model cannot say which, so it declines to say any of them are safe.
        let objs = objects(&[(0, object("ordinary text"))]);
        let blocks = assemble(vec![line(0, 0, 700.0)], &objs, true);
        assert!(blocks[0].refusals.contains(&BLOCK_PATH_B_UNPAIRED));
    }

    // ---------------- laying an edited block back out ----------------

    fn two_line_block() -> Block {
        let objs = objects(&[(0, object("first line here")), (1, object("second line here"))]);
        one(vec![line(0, 0, 700.0), line(1, 1, 686.0)], objs)
    }

    #[test]
    fn an_unedited_block_asks_for_no_changes() {
        let b = two_line_block();
        let text = b.text.clone();
        assert!(lay_out(&b, &text, 17).unwrap().is_empty());
    }

    #[test]
    fn one_edited_line_becomes_one_change_addressed_in_block_offsets() {
        let b = two_line_block();
        let changes = lay_out(&b, "first LINE here\nsecond line here", 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].line, 0);
        assert_eq!(changes[0].text, "LINE");
        // The offset is into the BLOCK, which for line 0 is also into the line.
        assert_eq!(changes[0].at, 6);
        assert_eq!(changes[0].len, 4);
    }

    #[test]
    fn an_edit_on_the_second_line_is_offset_past_the_break() {
        let b = two_line_block();
        let changes = lay_out(&b, "first line here\nsecond LINE here", 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].line, 1);
        // 15 characters of line one, one break, then seven of line two.
        assert_eq!(changes[0].at, 15 + 1 + 7);
        assert_eq!(&b.text[changes[0].at..changes[0].at + changes[0].len], "line");
    }

    #[test]
    fn both_lines_edited_become_two_changes() {
        let b = two_line_block();
        let changes = lay_out(&b, "FIRST line here\nsecond line THERE", 17).unwrap();
        assert_eq!(changes.len(), 2);
        assert_eq!((changes[0].line, changes[1].line), (0, 1));
        assert_eq!(changes[0].text, "FIRST");
        assert_eq!(changes[1].text, "THERE");
    }

    #[test]
    fn a_different_number_of_lines_is_reflow_and_is_refused() {
        let b = two_line_block();
        assert_eq!(lay_out(&b, "first line here", 17).err(), Some(17));
        assert_eq!(lay_out(&b, "first\nline\nhere", 17).err(), Some(17));
        // ⚠️ INCLUDING WHEN THE TEXT IS OTHERWISE IDENTICAL. Deleting the break
        // is a real edit and needs reflow to say where the words go.
        assert_eq!(lay_out(&b, &b.text.replace('\n', " "), 17).err(), Some(17));
    }

    #[test]
    fn a_typed_character_is_widened_into_a_replacement() {
        // ⚠️ NEVER A ZERO-LENGTH SPAN. Path A's splice looks for a span to cut,
        // and an empty one falling between two string operands matches neither:
        // it writes nothing and reports success. Widening by one character
        // turns typing into the replacement case the emitters are proven on.
        let b = two_line_block();
        let changes = lay_out(&b, "firXst line here\nsecond line here", 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].len, 1, "a pure insertion was left zero-length");
        assert_eq!(changes[0].at, 2);
        assert_eq!(changes[0].text, "rX");
        // And it says the same thing: the character before, then the new one.
        assert_eq!(&b.text[changes[0].at..changes[0].at + changes[0].len], "r");
    }

    #[test]
    fn typing_at_the_very_start_of_a_line_widens_forwards_instead() {
        let b = two_line_block();
        let changes = lay_out(&b, "Xfirst line here\nsecond line here", 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].at, 0);
        assert_eq!(changes[0].len, 1);
        assert_eq!(changes[0].text, "Xf");
    }

    #[test]
    fn deleting_a_word_is_a_replacement_by_nothing() {
        let b = two_line_block();
        let changes = lay_out(&b, "first here\nsecond line here", 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].text, "");
        assert_eq!(&b.text[changes[0].at..changes[0].at + changes[0].len], "line ");
    }

    #[test]
    fn a_change_the_map_cannot_address_is_refused_by_the_plan() {
        // Editing the shaped run of a Path B line: the layout finds the change
        // happily, and the map is what says it cannot be placed.
        let objs = objects(&[
            (0, object("The quick ")),
            (1, shaped("\u{194}\u{93e} ", "\u{915}\u{94d}\u{937}\u{93e}")),
            (2, object(" fox")),
        ]);
        let b = one(vec![line(0, 2, 700.0)], objs);
        let edited = b.text.replace('\u{915}', "x");
        let changes = lay_out(&b, &edited, 17).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(
            plan(&b, changes[0].at, changes[0].len, 14, 16, 15, 1).err(),
            Some(16));
    }

    /// Everything up to and including `room` characters fits, which is a
    /// measure a test can do arithmetic in.
    fn room(room: usize) -> impl Fn(usize, &str) -> bool {
        move |_, text: &str| text.chars().count() <= room
    }

    /// ⚠️ THE OVERFLOW GOES TO THE NEXT LINE RATHER THAN PAST THE MARGIN.
    /// This is the whole point: a justified line that grew used to run out over
    /// the right margin or refuse outright, because nothing was allowed to
    /// carry the excess.
    #[test]
    fn a_word_that_no_longer_fits_moves_to_the_line_below() {
        let lines = vec!["aaa bbb ccc".to_string(), "ddd".to_string()];

        let out = rewrap_from(&lines, 0, room(11)).expect("it fits in two lines");
        assert_eq!(out, vec!["aaa bbb ccc", "ddd"], "nothing needed to move");

        // One character wider, and the last word has to go down.
        let lines = vec!["aaa bbbb ccc".to_string(), "ddd".to_string()];
        let out = rewrap_from(&lines, 0, room(11)).expect("it fits in two lines");
        assert_eq!(out, vec!["aaa bbbb", "ccc ddd"]);
    }

    /// ⚠️ AND IT CASCADES. Pushing a word onto the next line can push that
    /// line's last word onto the one after it, all the way down the paragraph.
    #[test]
    fn the_push_carries_on_down_the_paragraph() {
        // One character too long on the first line, and every line below it
        // has to take a word from the one above.
        let lines = vec![
            "aaaaa bbbb".to_string(),
            "cccc dddd".to_string(),
            "eeee".to_string(),
        ];
        let out = rewrap_from(&lines, 0, room(9)).expect("five words, three lines");
        assert_eq!(out, vec!["aaaaa", "bbbb cccc", "dddd eeee"],
            "the push stopped before the end of the paragraph");
    }

    /// ⚠️ LINES ABOVE THE EDIT ARE LEFT ALONE, byte for byte. Every line
    /// this moves is a line that can refuse, and `emit_block` is all or
    /// nothing, so moving one that did not need to move risks the whole edit.
    #[test]
    fn the_lines_above_the_edit_are_not_touched() {
        let lines = vec![
            "keep me exactly".to_string(),
            "aaa bbbb".to_string(),
            "ccc".to_string(),
        ];
        let out = rewrap_from(&lines, 1, room(8)).expect("it fits");
        assert_eq!(out[0], "keep me exactly",
            "a line above the edit was re-filled and could now refuse");
    }

    /// ⚠️ A PARAGRAPH THAT NEEDS A LINE IT HAS NOT GOT IS REFUSED, not
    /// half-done. Drawing a line that was never there is a different piece of
    /// work, and silently dropping the words would lose the reader's text.
    #[test]
    fn words_that_do_not_go_into_the_lines_there_are_refuse() {
        let lines = vec!["aaa bbb ccc ddd".to_string()];
        assert!(rewrap_from(&lines, 0, room(7)).is_none(),
            "four words were squeezed into one line's room");
    }

    /// ⚠️ A WORD WIDER THAN THE MEASURE STILL HAS TO GO SOMEWHERE. A single
    /// long word in a narrow column is ordinary, and an empty line that refuses
    /// it would drop it and spin.
    #[test]
    fn a_word_wider_than_the_line_is_still_placed() {
        let lines = vec!["antidisestablishmentarianism".to_string()];
        let out = rewrap_from(&lines, 0, room(5)).expect("it has nowhere else to go");
        assert_eq!(out, vec!["antidisestablishmentarianism"]);
    }

    #[test]
    fn lines_are_read_down_the_page_not_up_it() {
        // PDF COORDINATES. The reader hands out page space, where down the page
        // is DECREASING y; sorting the other way reads every paragraph
        // backwards and was caught by exactly this.
        let objs = objects(&[(0, object("second line")), (1, object("first line"))]);
        let b = one(vec![line(0, 0, 686.0), line(1, 1, 700.0)], objs);
        assert_eq!(b.text, "first line\nsecond line");
    }
}
