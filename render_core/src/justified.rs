//! Editing one JUSTIFIED line by rewriting its content-stream operators.
//!
//! ⚠️ WHY THIS EXISTS AT ALL, AND WHY IT IS NOT DONE THROUGH PDFIUM. A Word
//! justified line is ONE text object whose `TJ` array carries the spacing:
//! ordinary kerning between glyphs, and one large negative adjustment after
//! each space that stretches the gaps until the last word lands on the right
//! margin. PDFium can read that perfectly, but `FPDFText_SetText` replaces an
//! object's whole string and writes it back as a plain run, so every one of
//! those numbers is discarded. Measured on the real document: replacing an
//! eight-letter word with a three-letter one moved the right edge 53pt, of
//! which only 25pt was the letters. The other 28pt was the justification
//! collapsing. There is no PDFium call that can write it back, because the
//! public API has no way to emit `TJ` adjustments, `Tw` or `Tc`.
//!
//! So the line is edited where it actually lives: in the content stream. lopdf
//! parses the operators, this module rewrites exactly one `TJ`, and the result
//! is a new document the caller reopens. The line stays ONE native text object
//! exactly as the producer wrote it, inside its own marked-content sequence,
//! in its own embedded font. Nothing else in the file is touched.

use std::collections::{BTreeMap, BTreeSet};

use lopdf::content::{Content, Operation};
use lopdf::{Document, Object};

use crate::{
    STATUS_DOC_NOT_REWRITABLE, STATUS_FONT_METRICS_UNAVAILABLE, STATUS_INVALID_INPUT,
    STATUS_LINE_NOT_REWRITABLE, STATUS_STALE_ANCHOR, STATUS_TOO_WIDE, STATUS_UNSUPPORTED,
};

/// How close a `Tm` has to be to the caller's baseline to be that line.
const BASELINE_TOLERANCE: f32 = 0.01;

/// WinAnsi's 0x80..0x9F block, the only stretch of it that is not Latin-1.
/// Zero marks a code WinAnsi leaves undefined: one of those must never be
/// produced, and a line containing one is not ours to edit.
const WINANSI_HIGH: [u16; 32] = [
    0x20AC, 0x0000, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
    0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x0000, 0x017D, 0x0000,
    0x0000, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
    0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x0000, 0x017E, 0x0178,
];

/// One Unicode character to one WinAnsi character code.
///
/// ⚠️ A PDF simple font's character codes are NOT UTF-8. A byte >= 0x80 in a
/// content stream is an ordinary single-byte code, and on the documents this
/// module edits it is a curly quote, a dash or an accented letter. Refusing
/// every such byte would refuse 14 of the 36 justified lines found across the
/// sampled library, most of them plain English text whose only sin is a
/// typographic apostrophe.
///
/// The map is one-to-one, so a character index and a code index are the same
/// index. That is what lets everything below stay byte-oriented.
fn winansi_encode(c: char) -> Option<u8> {
    let u = c as u32;
    if u < 0x80 {
        return Some(u as u8);
    }
    if let Some(i) = WINANSI_HIGH.iter().position(|w| *w != 0 && *w as u32 == u) {
        return Some(0x80 + i as u8);
    }
    match u {
        0xA0..=0xFF => Some(u as u8),
        _ => None,
    }
}

/// Every character of `text` as a character code, or nothing if any of them
/// has no WinAnsi code at all.
fn to_codes(text: &str) -> Option<Vec<u8>> {
    text.chars().map(winansi_encode).collect()
}

/// Whether the font's base encoding really is WinAnsi, and which codes its
/// `/Differences` array overrides.
///
/// ⚠️ Only the WRITING direction needs this. Reading is self-validating:
/// `locate` matches this module's encoding of the caller's text against the
/// bytes actually in the stream, so a font whose codes do not mean what
/// WinAnsi says simply fails to match and the edit is refused. Writing a
/// character the line does not already contain has no such check, so it is
/// allowed only on a genuine WinAnsi font, at a code the font did not remap.
/// Measured across the library: 337 simple fonts carry `/Differences`, and
/// what they overwhelmingly hold is a subset font's glyph indices (`g0`,
/// `f_t`, `uni1015`), which mean nothing in WinAnsi terms.
fn encoding_of(doc: &Document, font: &lopdf::Dictionary) -> (bool, BTreeSet<i64>) {
    let mut overridden = BTreeSet::new();
    let Ok(enc) = font.get(b"Encoding") else {
        return (false, overridden);
    };
    if let Object::Name(n) = enc {
        return (n.as_slice() == b"WinAnsiEncoding", overridden);
    }
    let dict = match enc {
        Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
        Object::Dictionary(d) => Some(d.clone()),
        _ => None,
    };
    let Some(dict) = dict else {
        return (false, overridden);
    };
    let base = matches!(dict.get(b"BaseEncoding"), Ok(Object::Name(n)) if n.as_slice() == b"WinAnsiEncoding");
    if let Ok(Object::Array(items)) = dict.get(b"Differences") {
        let mut code = 0i64;
        for item in items {
            match item {
                Object::Integer(i) => code = *i,
                Object::Real(r) => code = *r as i64,
                Object::Name(_) => {
                    overridden.insert(code);
                    code += 1;
                }
                _ => {}
            }
        }
    }
    (base, overridden)
}

/// The font's own statement of what its codes mean, inverted so a character can
/// be written back as the code that produces it.
///
/// ⚠️ AMBIGUITY IS REFUSED, NOT GUESSED. If two codes map to the same character
/// there is no basis to choose between them, so that character is dropped from
/// the inverse and the write is refused exactly as it was before. A code
/// mapping to SEVERAL characters is a ligature and cannot be produced from a
/// single character at all, so it is skipped.
///
/// Only single-byte source codes are read. A two-byte code belongs to a
/// composite font, whose pipeline is byte-per-character throughout and would
/// have to be generalised first.
fn to_unicode_inverse(doc: &Document, font: &lopdf::Dictionary) -> BTreeMap<char, u8> {
    let mut inverse: BTreeMap<char, u8> = BTreeMap::new();
    let Ok(entry) = font.get(b"ToUnicode") else {
        return inverse;
    };
    let stream = match entry {
        Object::Reference(id) => doc.get_object(*id).ok().and_then(|o| o.as_stream().ok()).cloned(),
        Object::Stream(st) => Some(st.clone()),
        _ => None,
    };
    let Some(stream) = stream else {
        return inverse;
    };
    let cmap = stream
        .decompressed_content()
        .unwrap_or_else(|_| stream.content.clone());

    let mut ambiguous: BTreeSet<char> = BTreeSet::new();
    for (code, ch) in parse_bf_sections(&cmap) {
        match inverse.get(&ch) {
            Some(existing) if *existing != code => {
                ambiguous.insert(ch);
            }
            Some(_) => {}
            None => {
                inverse.insert(ch, code);
            }
        }
    }
    for ch in ambiguous {
        inverse.remove(&ch);
    }
    inverse
}

/// One `<hex>` token, an array bracket, or a bare word.
pub(crate) enum CmapToken {
    Hex(Vec<u8>),
    Open,
    #[allow(dead_code)]
    Close,
    Word(String),
}

pub(crate) fn tokenize_cmap(cmap: &[u8]) -> Vec<CmapToken> {
    let mut out = Vec::new();
    let mut i = 0usize;
    while i < cmap.len() {
        let c = cmap[i];
        if c == b'<' {
            let start = i + 1;
            let mut j = start;
            while j < cmap.len() && cmap[j] != b'>' {
                j += 1;
            }
            let digits: Vec<u8> = cmap[start..j.min(cmap.len())]
                .iter()
                .copied()
                .filter(|d| d.is_ascii_hexdigit())
                .collect();
            let mut bytes = Vec::new();
            for pair in digits.chunks(2) {
                if pair.len() == 2 {
                    let hi = (pair[0] as char).to_digit(16).unwrap_or(0) as u8;
                    let lo = (pair[1] as char).to_digit(16).unwrap_or(0) as u8;
                    bytes.push((hi << 4) | lo);
                }
            }
            out.push(CmapToken::Hex(bytes));
            i = j + 1;
        } else if c == b'[' {
            out.push(CmapToken::Open);
            i += 1;
        } else if c == b']' {
            out.push(CmapToken::Close);
            i += 1;
        } else if c.is_ascii_alphabetic() {
            let start = i;
            while i < cmap.len() && (cmap[i].is_ascii_alphanumeric() || cmap[i] == b'_') {
                i += 1;
            }
            out.push(CmapToken::Word(
                String::from_utf8_lossy(&cmap[start..i]).to_string(),
            ));
        } else {
            i += 1;
        }
    }
    out
}

/// UTF-16BE, but only when it says exactly one character.
pub(crate) fn one_char_utf16be(bytes: &[u8]) -> Option<char> {
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|p| u16::from_be_bytes([p[0], p[1]]))
        .collect();
    let mut chars = char::decode_utf16(units);
    let first = chars.next()?.ok()?;
    if chars.next().is_some() {
        return None;
    }
    Some(first)
}

/// Every `code -> character` the CMap's bfchar and bfrange sections state.
fn parse_bf_sections(cmap: &[u8]) -> Vec<(u8, char)> {
    /// A range wider than one byte can address is not ours to read.
    const MAX_RANGE: u32 = 256;

    let tokens = tokenize_cmap(cmap);
    let mut out = Vec::new();
    let mut i = 0usize;

    while i < tokens.len() {
        let CmapToken::Word(word) = &tokens[i] else {
            i += 1;
            continue;
        };
        let is_char = word == "beginbfchar";
        let is_range = word == "beginbfrange";
        if !is_char && !is_range {
            i += 1;
            continue;
        }
        i += 1;

        while i < tokens.len() {
            if let CmapToken::Word(w) = &tokens[i] {
                if w.starts_with("end") {
                    i += 1;
                    break;
                }
            }
            if is_char {
                let (Some(CmapToken::Hex(src)), Some(CmapToken::Hex(dst))) =
                    (tokens.get(i), tokens.get(i + 1))
                else {
                    i += 1;
                    continue;
                };
                if src.len() == 1 {
                    if let Some(ch) = one_char_utf16be(dst) {
                        out.push((src[0], ch));
                    }
                }
                i += 2;
            } else {
                let (Some(CmapToken::Hex(lo)), Some(CmapToken::Hex(hi))) =
                    (tokens.get(i), tokens.get(i + 1))
                else {
                    i += 1;
                    continue;
                };
                i += 2;
                if lo.len() != 1 || hi.len() != 1 || hi[0] < lo[0] {
                    continue;
                }
                let span = hi[0] as u32 - lo[0] as u32;
                if span >= MAX_RANGE {
                    continue;
                }
                match tokens.get(i) {
                    Some(CmapToken::Hex(dst)) => {
                        // The destination advances with the code.
                        if let Some(base) = one_char_utf16be(dst).map(|c| c as u32) {
                            for k in 0..=span {
                                if let Some(ch) = char::from_u32(base + k) {
                                    out.push((lo[0] + k as u8, ch));
                                }
                            }
                        }
                        i += 1;
                    }
                    Some(CmapToken::Open) => {
                        i += 1;
                        let mut k = 0u32;
                        while let Some(tok) = tokens.get(i) {
                            match tok {
                                CmapToken::Close => {
                                    i += 1;
                                    break;
                                }
                                CmapToken::Hex(dst) => {
                                    if k <= span {
                                        if let Some(ch) = one_char_utf16be(dst) {
                                            out.push((lo[0] + k as u8, ch));
                                        }
                                    }
                                    k += 1;
                                    i += 1;
                                }
                                _ => i += 1,
                            }
                        }
                    }
                    _ => {}
                }
            }
        }
    }
    out
}

/// The one text object this edit is about, and the operators around it.
struct Located {
    page_id: (u32, u16),
    ops: Vec<Operation>,
    /// Index of the `BT` that opens the block, used to find the font in force.
    bt: usize,
    /// Index of the `TJ` operation, the only one this module ever replaces.
    tj_at: usize,
    array: Vec<Object>,
}

/// Everything an edit to one line needs, whichever path serves it.
///
/// Path A reads the located operators and the font in force. A second path that
/// generates its own glyphs needs the same, plus the `Tm` that places the line,
/// so a replacement can be written exactly where the original was. Gathering it
/// once, here, is the only reason this exists: the searches are the ones
/// `metrics` used to do, moved unchanged.
struct EditPlan {
    located: Located,
    /// The font resource name in force at the `TJ`.
    font_name: Vec<u8>,
    /// Its size, BEFORE the text matrix scales it.
    font_size: f64,
    /// The operands of the `Tm` that places the line, if the block has one.
    tm: Option<Vec<Object>>,
}

/// The font metrics the line is set in, read from the document itself.
pub(crate) struct Metrics {
    first_char: i64,
    widths: Vec<f64>,
    /// Type size times the text matrix's horizontal scale. It CANCELS out of
    /// the slot arithmetic (target and natural are both measured with it), but
    /// it is read properly rather than assumed so the fit check is in real
    /// points.
    size: f64,
    /// Whether a character code may be WRITTEN as WinAnsi says. See
    /// `encoding_of`.
    base_winansi: bool,
    /// Codes this font's `/Differences` gave a meaning of its own.
    differences: BTreeSet<i64>,
    /// The font's own `/ToUnicode`, inverted: character -> the code that
    /// writes it. Empty when the font has none, or when nothing in it was
    /// unambiguous.
    inverse: BTreeMap<char, u8>,
    /// The width for a code `/Widths` does not cover, from the descriptor's
    /// `/MissingWidth`.
    ///
    /// ⚠️ `None` MEANS UNANSWERABLE, NOT ZERO. A code outside the table used to
    /// contribute nothing, so a font that simply failed to declare a width
    /// moved everything after it and said nothing. Measured on the corpus: 282
    /// runs are set in a simple font with no width table at all.
    missing_width: Option<f64>,
    /// A Type0 font's CID widths. `None` for a simple font, whose codes are one
    /// byte; `Some` means codes are TWO bytes and widths come from the
    /// descendant's `/W` and `/DW`.
    cid: Option<crate::shaped::CidWidths>,
}

fn number(o: &Object) -> Option<f64> {
    match o {
        Object::Integer(i) => Some(*i as f64),
        Object::Real(r) => Some(*r as f64),
        _ => None,
    }
}

/// The bytes a `TJ` array actually spells out, ignoring its adjustments.
///
/// BYTES, NOT CHARACTERS. A simple font's codes are one byte each, and every
/// offset in this module is an offset into this sequence. Treating it as UTF-8
/// would put the offsets somewhere else the moment a byte above 127 appears.
fn flatten(array: &[Object]) -> Vec<u8> {
    let mut out = Vec::new();
    for o in array {
        if let Object::String(b, _) = o {
            out.extend_from_slice(b);
        }
    }
    out
}

/// Finds the text object on `baseline` that says `expected`.
///
/// ⚠️ BOTH CONDITIONS, AND AMBIGUITY REFUSES. A baseline alone does not
/// identify a line: two columns share one. Requiring the flattened text to
/// match as well makes the pair unique in practice, and finding two matches is
/// treated as not finding one, because guessing which is which would edit the
/// wrong column.
fn locate(doc: &Document, page_index: i32, baseline: f32, expected: &[u8]) -> Result<Located, i32> {
    let pages = doc.get_pages();
    let Some(&page_id) = pages.get(&(page_index as u32 + 1)) else {
        return Err(STATUS_INVALID_INPUT);
    };

    let raw = doc.get_page_content(page_id);
    let Ok(content) = Content::decode(&raw) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let ops = content.operations;

    let mut matches: Vec<(usize, usize, Vec<Object>)> = Vec::new();
    let (mut bt, mut tj, mut on_baseline) = (0usize, 0usize, false);

    // ⚠️ WHY THIS BOOKKEEPING EXISTS. Failing to find the line is not one
    // condition but two, and they call for opposite things to be said. Either
    // the page has moved on under the caller, or the page never wrote this
    // line in a shape that can be rewritten at all. Measured across the
    // sampled library, the second is nearly all of it, so reporting both as a
    // stale anchor would tell users their text had changed when nothing had.
    let mut runs_since_tm = 0usize;
    let mut moved_since_tm = false;
    // Some text on this page is drawn where `Td`/`TD`/`T*` left the cursor
    // rather than where a `Tm` put it, so a line need not have a `Tm` of its
    // own to find.
    let mut page_positions_text_relatively = false;
    let mut a_tm_sits_on_the_baseline = false;
    let mut the_block_there_is_not_one_run = false;

    for (i, op) in ops.iter().enumerate() {
        match op.operator.as_str() {
            "BT" => {
                bt = i;
                tj = 0;
                on_baseline = false;
                runs_since_tm = 0;
                moved_since_tm = false;
            }
            "Tm" => {
                if let Some(v) = op.operands.get(5).and_then(number) {
                    on_baseline = (v as f32 - baseline).abs() < BASELINE_TOLERANCE;
                    if on_baseline {
                        a_tm_sits_on_the_baseline = true;
                    }
                }
                runs_since_tm = 0;
                moved_since_tm = false;
            }
            "Td" | "TD" | "T*" => moved_since_tm = true,
            "TJ" | "Tj" | "'" | "\"" => {
                runs_since_tm += 1;
                if moved_since_tm {
                    page_positions_text_relatively = true;
                }
                // ⚠️ NOTED HERE RATHER THAN AT THE `ET`. A block drawn with
                // `Tj`, or one whose later `Tm` clears `on_baseline` before the
                // block closes, never reaches the `ET` arm below, and both are
                // ordinary in real files: judging the shape only at `ET` left
                // 15 structurally unsupported lines still calling themselves
                // stale.
                if on_baseline
                    && (runs_since_tm > 1 || moved_since_tm || op.operator != "TJ")
                {
                    the_block_there_is_not_one_run = true;
                }
                if on_baseline && op.operator == "TJ" {
                    tj = i;
                }
            }
            "ET" if on_baseline && tj > 0 => {
                if let Some(Object::Array(a)) = ops[tj].operands.first() {
                    if flatten(a) == expected {
                        matches.push((bt, tj, a.clone()));
                    } else if runs_since_tm > 1 || moved_since_tm {
                        // The `Tm` is where the caller said, but what hangs off
                        // it is several runs, so the text read back from it was
                        // never the whole line and cannot be compared.
                        the_block_there_is_not_one_run = true;
                    }
                }
                on_baseline = false;
            }
            _ => {}
        }
    }

    match matches.len() {
        1 => {
            let (bt, tj_at, array) = matches.remove(0);
            Ok(Located { page_id, ops, bt, tj_at, array })
        }
        // Not found, and the page itself says why: either the block at that
        // baseline is not a single run, or the line was never given a `Tm` to
        // be found by. Anything else, including two lines that read alike, is
        // reported as the anchor no longer holding.
        0 if the_block_there_is_not_one_run
            || (!a_tm_sits_on_the_baseline && page_positions_text_relatively) =>
        {
            Err(STATUS_LINE_NOT_REWRITABLE)
        }
        _ => Err(STATUS_STALE_ANCHOR),
    }
}

/// Locates the line and reads the text state in force at it.
///
/// ⚠️ THE FONT IS SEARCHED BACK FROM THE TJ, NOT FROM THE BT. PDF text state
/// carries across BT/ET, so the font in force may have been selected inside
/// this block or in an earlier one: the real document sets it two paragraphs
/// up, a Word fixture sets it inside the block. Searching back from the block
/// start finds one of those and reports the other as fontless.
fn plan(doc: &Document, page_index: i32, baseline: f32, expected: &[u8])
    -> Result<EditPlan, i32>
{
    let located = locate(doc, page_index, baseline, expected)?;

    let mut font_name: Option<Vec<u8>> = None;
    let mut font_size = 0.0f64;
    for op in located.ops[..located.tj_at].iter().rev() {
        if op.operator == "Tf" {
            if let Some(Object::Name(n)) = op.operands.first() {
                font_name = Some(n.clone());
                font_size = op.operands.get(1).and_then(number).unwrap_or(0.0);
                break;
            }
        }
    }
    let Some(font_name) = font_name else {
        return Err(STATUS_FONT_METRICS_UNAVAILABLE);
    };

    let tm = located.ops[located.bt..located.tj_at]
        .iter()
        .find(|o| o.operator == "Tm")
        .map(|o| o.operands.clone());

    Ok(EditPlan { located, font_name, font_size, tm })
}

fn metrics(doc: &Document, plan: &EditPlan) -> Result<Metrics, i32> {
    // The text matrix scales the type as well as placing it.
    let scale = plan
        .tm
        .as_ref()
        .and_then(|operands| operands.first().and_then(number))
        .unwrap_or(1.0);
    let m = font_metrics(doc, plan.located.page_id, &plan.font_name, plan.font_size * scale)?;
    // ⚠️ PATH A STILL REFUSES TYPE0, EXACTLY AS IT ALWAYS HAS. `font_metrics`
    // learned to read them so the measurement could; letting that widen what
    // Path A accepts would change its answer on lines it has refused since the
    // day it was written, and the 76-line byte-identical baseline is what says
    // it has not.
    if m.cid.is_some() {
        return Err(STATUS_FONT_METRICS_UNAVAILABLE);
    }
    Ok(m)
}

/// The same table, for any font resource named on any page, at any size.
///
/// ⚠️ THE SAME CODE PATH `metrics` ALWAYS USED, lifted out so it can be asked
/// about a font rather than about an anchored line. `metrics` is now a wrapper
/// that works out the size and calls this; nothing about what Path A resolves,
/// accepts or refuses changed, and the 76-line byte-identical baseline is what
/// says so.
///
/// `size` is the type size with the text matrix's horizontal scale already
/// folded in, exactly as `metrics` computed it.
pub(crate) fn font_metrics(
    doc: &Document,
    page_id: lopdf::ObjectId,
    font_name: &[u8],
    size: f64,
) -> Result<Metrics, i32> {
    if size <= 0.0 {
        return Err(STATUS_FONT_METRICS_UNAVAILABLE);
    }

    let deref = |o: &Object| -> Option<lopdf::Dictionary> {
        match o {
            Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
            Object::Dictionary(d) => Some(d.clone()),
            _ => None,
        }
    };
    let fail = |_| STATUS_FONT_METRICS_UNAVAILABLE;
    let page = doc.get_dictionary(page_id).map_err(fail)?;
    let resources = deref(page.get(b"Resources").map_err(fail)?)
        .ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;
    let fonts = deref(resources.get(b"Font").map_err(fail)?)
        .ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;
    let entry = fonts.get(font_name).map_err(fail)?;
    let font = deref(entry).ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;

    // ⚠️ TYPE0 KEEPS ITS WIDTHS SOMEWHERE ELSE ENTIRELY: on the descendant
    // font, as `/W` and `/DW`, and its codes are two bytes rather than one.
    // Read with the module this app already uses for exactly that, rather than
    // with a second idea of what a font is.
    let subtype = font.get(b"Subtype").ok().and_then(|o| match o {
        Object::Name(n) => Some(n.clone()),
        _ => None,
    });
    if subtype.as_deref() == Some(b"Type0".as_slice()) {
        // ⚠️ IDENTITY-H ONLY. Under any other CMap a code is not its own CID,
        // and guessing the mapping is exactly the silent drift the other half
        // of this change exists to stop.
        let identity = matches!(font.get(b"Encoding"), Ok(Object::Name(n)) if n == b"Identity-H");
        let Object::Reference(font_id) = entry else {
            return Err(STATUS_FONT_METRICS_UNAVAILABLE);
        };
        let Some(cid) = crate::shaped::cid_widths(doc, *font_id).filter(|_| identity) else {
            return Err(STATUS_FONT_METRICS_UNAVAILABLE);
        };
        let (base_winansi, differences) = encoding_of(doc, &font);
        return Ok(Metrics {
            first_char: 0,
            widths: Vec::new(),
            size,
            base_winansi,
            differences,
            inverse: to_unicode_inverse(doc, &font),
            missing_width: None,
            cid: Some(cid),
        });
    }

    // A simple font's own fallback for codes its `/Widths` does not reach.
    //
    // ⚠️ OPTIONAL AT EVERY STEP. Plenty of fonts carry no
    // `/FontDescriptor` at all, the base-14 among them, and demanding one here
    // turned "this font declares no fallback" into "this font cannot be
    // measured" for 23 tests' worth of perfectly ordinary type.
    let missing_width = font.get(b"FontDescriptor").ok()
        .and_then(deref)
        .and_then(|d| d.get(b"MissingWidth").ok().and_then(number));

    let first_char = font.get(b"FirstChar").map_err(fail)?.as_i64().map_err(fail)?;
    let widths: Vec<f64> = match font.get(b"Widths").map_err(fail)? {
        Object::Reference(id) => doc
            .get_object(*id)
            .map_err(fail)?
            .as_array()
            .map_err(fail)?
            .iter()
            .filter_map(number)
            .collect(),
        Object::Array(a) => a.iter().filter_map(number).collect(),
        _ => return Err(STATUS_FONT_METRICS_UNAVAILABLE),
    };
    if widths.is_empty() {
        return Err(STATUS_FONT_METRICS_UNAVAILABLE);
    }

    let (base_winansi, differences) = encoding_of(doc, &font);
    let inverse = to_unicode_inverse(doc, &font);

    Ok(Metrics {
        first_char, widths, size, base_winansi, differences, inverse,
        missing_width, cid: None,
    })
}

impl Metrics {
    /// The code that writes `c` in this font.
    ///
    /// ⚠️ THE ORDER IS THE COMPATIBILITY GUARANTEE. The declared-encoding
    /// rule is tried first and is unchanged, so every character that was
    /// accepted before still takes exactly the same code. `/ToUnicode` only
    /// ever ADDS characters that used to be refused; it never overrides a code
    /// the declared encoding already supplies.
    fn encode_char(&self, c: char) -> Option<u8> {
        if let Some(code) = winansi_encode(c) {
            // A high byte needs the font to actually declare WinAnsi; ASCII is
            // safe on anything that did not remap the code.
            let declared = (c as u32) < 0x80 || self.base_winansi;
            if declared && !self.differences.contains(&(code as i64)) {
                return Some(code);
            }
        }
        // The document's own statement of what its codes mean. It is written
        // in terms of the codes as they are actually used, so it already
        // accounts for `/Differences` and the override set does not apply.
        self.inverse.get(&c).copied()
    }

    /// The whole replacement as character codes, or nothing if any character
    /// has no code this font can be shown to produce.
    pub(crate) fn encode(&self, text: &str) -> Option<Vec<u8>> {
        text.chars().map(|c| self.encode_char(c)).collect()
    }

    /// Whether this font can measure every byte, which for a subset font is
    /// the same question as whether it can spell them.
    pub(crate) fn can_spell(&self, bytes: &[u8]) -> bool {
        bytes.iter().all(|b| {
            let i = *b as i64 - self.first_char;
            i >= 0 && (i as usize) < self.widths.len()
        })
    }

    /// Whether the font's `/Differences` gave this code a meaning of its own,
    /// so it can no longer be read as the base encoding says.
    pub(crate) fn remapped(&self, code: u8) -> bool {
        self.differences.contains(&(code as i64))
    }

    /// Whether this is a Type0 font, whose codes are two bytes.
    pub(crate) fn is_cid(&self) -> bool {
        self.cid.is_some()
    }

    /// What these codes advance by, or `None` if any of them has no width this
    /// font can be asked for.
    ///
    /// ⚠️ NEVER 0.0 FOR AN UNKNOWN CODE. A missing width used to contribute
    /// nothing, which is indistinguishable from a zero-width glyph and moves
    /// everything after it. Refusing turns a silent positional error into a
    /// refusal, which is what every other unanswerable question in this module
    /// already does.
    pub(crate) fn width_of(&self, bytes: &[u8]) -> Option<f64> {
        let mut sum = 0.0;
        if let Some(cid) = &self.cid {
            // Two-byte codes, and `/DW` covers everything `/W` does not, so a
            // CID is never unanswerable.
            if bytes.len() % 2 != 0 {
                return None;
            }
            for pair in bytes.chunks_exact(2) {
                sum += cid.of(u16::from_be_bytes([pair[0], pair[1]]));
            }
        } else {
            for b in bytes {
                let i = *b as i64 - self.first_char;
                let w = if i >= 0 && (i as usize) < self.widths.len() {
                    Some(self.widths[i as usize])
                } else {
                    self.missing_width
                };
                sum += w?;
            }
        }
        Some(sum / 1000.0 * self.size)
    }

    /// What the array advances by: glyph widths, less what the adjustments
    /// take back. A `TJ` number is SUBTRACTED from the position, so a negative
    /// one widens.
    pub(crate) fn advance(&self, array: &[Object]) -> Option<f64> {
        let mut sum = 0.0;
        for o in array {
            match o {
                Object::String(b, _) => sum += self.width_of(b)?,
                other => sum += number(other).map(|n| -n / 1000.0 * self.size).unwrap_or(0.0),
            }
        }
        Some(sum)
    }
}

/// Replaces bytes `at..at + len` of the flattened text with `new`.
///
/// ⚠️ INSERTED SPACES ARE CUT INTO THEIR OWN ELEMENTS. A justification slot is
/// an adjustment that follows a space (see `slots_of`), so text pushed in as
/// one string would give its spaces nowhere to stretch. Measured before this:
/// the two gaps either side of an inserted "and Trade" came out at 4.24 and
/// 4.48 points against 6.8 elsewhere on the same line.
///
/// An adjustment strictly inside the replaced range is kerning for characters
/// that no longer exist, so it goes with them. Nothing has to be done about the
/// width it was contributing, because `solve` recomputes from the array that
/// survives rather than tracking a delta.
fn splice(array: &[Object], at: usize, len: usize, new: &[u8]) -> Vec<Object> {
    let end = at + len;
    let mut out: Vec<Object> = Vec::with_capacity(array.len() + 4);
    let mut seen = 0usize;
    let mut written = false;

    for o in array {
        match o {
            Object::String(b, fmt) => {
                let start = seen;
                let stop = seen + b.len();
                seen = stop;

                if stop <= at || start >= end {
                    out.push(Object::String(b.clone(), *fmt));
                    continue;
                }

                let mut pending: Vec<u8> =
                    if start < at { b[..at - start].to_vec() } else { Vec::new() };

                if !written {
                    for part in new.split_inclusive(|c| *c == b' ') {
                        pending.extend_from_slice(part);
                        if part.last() == Some(&b' ') {
                            out.push(Object::String(std::mem::take(&mut pending), *fmt));
                            out.push(Object::Real(0.0));
                        }
                    }
                    written = true;
                }

                if stop > end {
                    pending.extend_from_slice(&b[end - start..]);
                }
                if !pending.is_empty() {
                    out.push(Object::String(pending, *fmt));
                }
            }
            other => {
                if !(seen > at && seen < end) {
                    out.push(other.clone());
                }
            }
        }
    }

    out
}

/// Which elements are justification slots.
///
/// ⚠️ STRUCTURAL, NEVER BY MAGNITUDE. A slot is the adjustment that FOLLOWS A
/// SPACE, whatever it currently says. Picking slots by size was measured to
/// fail outright: one edit widens the line, which drives the stretch towards
/// zero, and the next edit then finds no slots at all and compensates nothing,
/// leaving the line 38.5pt short of the right margin.
fn slots_of(array: &[Object]) -> Vec<bool> {
    let mut flags = vec![false; array.len()];
    let mut after_space = false;
    for (i, o) in array.iter().enumerate() {
        match o {
            Object::String(b, _) => after_space = b.last() == Some(&b' '),
            _ => {
                flags[i] = after_space;
                after_space = false;
            }
        }
    }
    flags
}

/// Sets every justification slot so the line advances by `target` again.
///
/// ⚠️ SOLVED ABSOLUTELY, NEVER ADJUSTED INCREMENTALLY. Spreading the width
/// difference over the slots was measured to leave newly inserted spaces
/// narrower than the rest of the line, because a new slot starts at zero
/// stretch while the producer's slots already carry theirs. Giving every slot
/// the SAME value makes the line uniform by construction, and makes the whole
/// operation idempotent: repeated edits cannot accumulate error, because each
/// one recomputes the answer instead of nudging the last one.
fn solve(array: &[Object], slots: &[bool], target: f64, m: &Metrics) -> Result<Vec<Object>, i32> {
    let count = slots.iter().filter(|f| **f).count();
    if count == 0 {
        return Err(STATUS_UNSUPPORTED);
    }

    // Everything the line advances by that is NOT justification.
    let natural: f64 = array
        .iter()
        .enumerate()
        .map(|(i, o)| match o {
            Object::String(b, _) => m.width_of(b),
            other if !slots[i] => Some(number(other).map(|n| -n / 1000.0 * m.size).unwrap_or(0.0)),
            _ => Some(0.0),
        })
        .sum::<Option<f64>>()
        .ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;

    let each = (target - natural) / count as f64;
    if each < 0.0 {
        // The words would have to be set tighter than the font's own spaces.
        return Err(STATUS_TOO_WIDE);
    }

    let value = ((-each / m.size * 1000.0) * 1000.0).round() / 1000.0;
    Ok(array
        .iter()
        .enumerate()
        .map(|(i, o)| if slots[i] { Object::Real(value as f32) } else { o.clone() })
        .collect())
}

/// Rewrites one justified line and returns the whole document's new bytes.
///
/// Pure: the caller's document is never touched, so a failure has nothing to
/// roll back. The result is a CANDIDATE with no relationship to any live
/// handle until the caller opens and validates it.
pub(crate) fn rewrite_bytes(
    bytes: &[u8],
    page_index: i32,
    baseline: f32,
    expected: &[u8],
    at: usize,
    len: usize,
    new: &[u8],
) -> Result<Vec<u8>, i32> {
    // No wrapping, ever. A line break is a paragraph edit, which this is not.
    if new.contains(&b'\n') || new.contains(&b'\r') {
        return Err(STATUS_INVALID_INPUT);
    }

    let Ok(doc) = Document::load_mem(bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };

    // The caller speaks Unicode, the content stream speaks character codes.
    // Convert once, here, and every offset below is a code offset.
    let (Some(expected_str), Some(new_str)) = (
        std::str::from_utf8(expected).ok(),
        std::str::from_utf8(new).ok(),
    ) else {
        return Err(STATUS_INVALID_INPUT);
    };
    if at + len > expected.len()
        || !expected_str.is_char_boundary(at)
        || !expected_str.is_char_boundary(at + len)
    {
        return Err(STATUS_INVALID_INPUT);
    }
    let len = expected_str[at..at + len].chars().count();
    let at = expected_str[..at].chars().count();
    // The anchor is self-validating: whatever this produces still has to match
    // bytes already in the stream, so a wrong guess simply fails to find the
    // line and nothing is written.
    let Some(expected) = to_codes(expected_str) else {
        return Err(STATUS_UNSUPPORTED);
    };

    let plan = plan(&doc, page_index, baseline, &expected)?;
    let m = metrics(&doc, &plan)?;

    // ⚠️ THE FONT DECIDES, so the replacement cannot be encoded before the
    // line has been found. Unlike the anchor there is no matching check to
    // catch a wrong code on the way in, so the code has to come from the font
    // itself.
    let Some(new) = m.encode(new_str) else {
        return Err(STATUS_UNSUPPORTED);
    };
    if !m.can_spell(&new) {
        return Err(STATUS_UNSUPPORTED);
    }

    // The width this line must still have when the edit is done. Taken BEFORE
    // the splice, from the line as it stands.
    let target = m.advance(&plan.located.array)
        .ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;

    let spliced = splice(&plan.located.array, at, len, &new);
    let slots = slots_of(&spliced);
    let rebuilt = solve(&spliced, &slots, target, &m)?;

    let tj_at = plan.located.tj_at;
    let page_id = plan.located.page_id;
    let mut ops = plan.located.ops;
    ops[tj_at] = Operation::new("TJ", vec![Object::Array(rebuilt)]);
    let Ok(encoded) = (Content { operations: ops }).encode() else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };

    // Reloaded rather than mutated in place: `locate` borrowed the parsed
    // document, and the write wants it fresh.
    let Ok(mut out) = Document::load_mem(bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    if out.change_page_content(page_id, encoded).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }

    let mut buf = Vec::new();
    if out.save_to(&mut buf).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }
    Ok(buf)
}
/// The resource name a Path B replacement is drawn under. Distinctive so it
/// cannot collide with a producer's own `/F1`, `/TT2` and the like, and shared
/// with the invisible run so both name the same embedded font.
const RESOURCE: &[u8] = b"AyaanPathB";

// ---------------------------------------------------------------- Path B
//
// ⚠️ A SIBLING, NOT A BRANCH. `rewrite_bytes` above is untouched: it neither
// gains a parameter nor learns that another path exists. What the two share is
// the READ half, `locate`, `plan` and `metrics`, which answers "where is this
// line and what is it set in". The emission halves stay apart, because Path A
// preserves the document's own font and Path B brings a new one.

/// The original array either side of the replaced bytes, with the replaced
/// bytes gone.
///
/// Path A's `splice` puts the replacement INTO the array because it is set in
/// the same font. Path B cannot: a `TJ` array speaks one font, so the
/// replacement has to become an array of its own between these two.
///
/// An adjustment strictly inside the replaced range is kerning for characters
/// that no longer exist and goes with them, exactly as in `splice`.
fn split_around(array: &[Object], at: usize, len: usize) -> (Vec<Object>, Vec<Object>) {
    let end = at + len;
    let (mut before, mut after) = (Vec::new(), Vec::new());
    let mut seen = 0usize;

    for o in array {
        match o {
            Object::String(b, fmt) => {
                let start = seen;
                let stop = seen + b.len();
                seen = stop;

                if stop <= at {
                    before.push(Object::String(b.clone(), *fmt));
                    continue;
                }
                if start >= end {
                    after.push(Object::String(b.clone(), *fmt));
                    continue;
                }
                // Straddles the range: keep whichever ends survive.
                if start < at {
                    before.push(Object::String(b[..at - start].to_vec(), *fmt));
                }
                if stop > end {
                    after.push(Object::String(b[end - start..].to_vec(), *fmt));
                }
            }
            other => {
                if seen <= at {
                    before.push(other.clone());
                } else if seen >= end {
                    after.push(other.clone());
                }
            }
        }
    }
    (before, after)
}

/// Sets every justification slot on BOTH sides of the replacement so the line
/// advances by `target` again.
///
/// The same rule as `solve`, which is left alone: every slot gets the SAME
/// value, so the line is uniform by construction and repeated edits cannot
/// accumulate error. The difference is only that the line is now two arrays
/// with a rigid block of known width between them.
fn solve_shaped(
    before: &[Object],
    after: &[Object],
    slots_before: &[bool],
    slots_after: &[bool],
    replacement: f64,
    replacement_slots: usize,
    target: f64,
    m: &Metrics,
) -> Result<(Vec<Object>, Vec<Object>, f64), i32> {
    // ⚠️ THE REPLACEMENT'S OWN SPACES COUNT. Without them a line whose every
    // slot fell inside the replacement had nothing to stretch and was refused,
    // which made replacing a WHOLE line impossible for no good reason: the user
    // selected that text deliberately.
    let count = slots_before.iter().chain(slots_after).filter(|f| **f).count()
        + replacement_slots;
    if count == 0 {
        return Err(STATUS_UNSUPPORTED);
    }

    let natural_of = |array: &[Object], slots: &[bool]| -> Option<f64> {
        array
            .iter()
            .enumerate()
            .map(|(i, o)| match o {
                Object::String(b, _) => m.width_of(b),
                other if !slots[i] => Some(number(other).map(|n| -n / 1000.0 * m.size).unwrap_or(0.0)),
                _ => Some(0.0),
            })
            .sum::<Option<f64>>()
    };
    // ⚠️ THE REPLACEMENT ARRIVES AS A NUMBER, NOT AN ARRAY. Its strings are
    // two-byte CID codes in a font this `Metrics` knows nothing about, so
    // measuring them here would read each byte as a code in the wrong font.
    let natural = natural_of(before, slots_before).ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?
        + natural_of(after, slots_after).ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?
        + replacement;

    let each = (target - natural) / count as f64;
    if each < 0.0 {
        return Err(STATUS_TOO_WIDE);
    }
    let written = -(each / m.size * 1000.0);

    let fill = |array: &[Object], slots: &[bool]| -> Vec<Object> {
        array
            .iter()
            .enumerate()
            .map(|(i, o)| {
                if slots[i] {
                    Object::Real(((written * 1000.0).round() / 1000.0) as f32)
                } else {
                    o.clone()
                }
            })
            .collect()
    };
    Ok((fill(before, slots_before), fill(after, slots_after), written))
}

/// Replaces bytes `at..at + len` of one justified line with text set in a font
/// the document did not have, returning the whole document's new bytes.
///
/// `bytes` must ALREADY carry the embedded font, and `font_id` must be the
/// Type0 font object in those same bytes that `provisioned` was shaped
/// against. Embedding is PDFium's job and rewriting is lopdf's, so the caller
/// does the first and hands the result here.
#[allow(clippy::too_many_arguments)]
pub(crate) fn rewrite_bytes_shaped(
    bytes: &[u8],
    page_index: i32,
    baseline: f32,
    expected: &[u8],
    at: usize,
    len: usize,
    provisioned: &crate::provision::Provisioned,
    new_text: &str,
    font_id: lopdf::ObjectId,
) -> Result<Vec<u8>, i32> {

    let Ok(doc) = Document::load_mem(bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    let Ok(expected_str) = std::str::from_utf8(expected) else {
        return Err(STATUS_INVALID_INPUT);
    };
    if at + len > expected.len()
        || !expected_str.is_char_boundary(at)
        || !expected_str.is_char_boundary(at + len)
    {
        return Err(STATUS_INVALID_INPUT);
    }
    let len = expected_str[at..at + len].chars().count();
    let at = expected_str[..at].chars().count();
    let Some(expected) = to_codes(expected_str) else {
        return Err(STATUS_UNSUPPORTED);
    };

    // The read half, shared with Path A.
    let plan = plan(&doc, page_index, baseline, &expected)?;
    let m = metrics(&doc, &plan)?;

    // Taken BEFORE anything changes, from the line as it stands.
    let target = m.advance(&plan.located.array)
        .ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;

    let Some(widths) = crate::shaped::cid_widths(&doc, font_id) else {
        return Err(STATUS_FONT_METRICS_UNAVAILABLE);
    };
    let mut run = crate::shaped::run(provisioned, new_text, &widths);
    if run.array.is_empty() {
        return Err(STATUS_INVALID_INPUT);
    }
    // Into the same points the rest of the arithmetic is in.
    let replacement_pt = run.advance / 1000.0 * m.size;

    let (before, after) = split_around(&plan.located.array, at, len);
    let slots_before = slots_of(&before);
    let slots_after = slots_of(&after);
    let (before, after, written) = solve_shaped(
        &before, &after, &slots_before, &slots_after,
        replacement_pt, run.slots.len(), target, &m,
    )?;

    // The replacement's own spaces stretch by exactly the same amount as the
    // line's, which is what makes the result uniform rather than merely the
    // right total width.
    let stretch = Object::Real(((written * 1000.0).round() / 1000.0) as f32);
    for at in &run.slots {
        run.array[*at] = stretch.clone();
    }
    // What the page will now advance by across the replacement, stretch and
    // all. The searchable run is sized against THIS, not against the unstretched
    // advance, or a highlight would come up short by one gap per space.
    let visible_1000 = run.advance + run.slots.len() as f64 * -written;
    let replacement = run.array;

    // Where the replacement will actually sit, measured from the SOLVED array
    // because the slots either side of it have just moved.
    let offset_pt = m.advance(&before).ok_or(STATUS_FONT_METRICS_UNAVAILABLE)?;

    // Built BEFORE the operator vector is taken out of the plan, because it
    // reads the line's own font and matrix off it.
    let searchable = searchable_run(
        provisioned, new_text, visible_1000, RESOURCE, &plan, offset_pt)?;

    // ⚠️ THE ORIGINAL Tf IS PUT BACK UNCONDITIONALLY. Tf persists across
    // BT/ET, so without this the new font re-set every later line on the page:
    // measured at 90,000 changed pixels over a 500-point band, against about
    // 10,000 confined to the edited line once it was restored.
    let size = Object::Real(plan.font_size as f32);
    let mut replacing: Vec<Operation> = Vec::with_capacity(7);
    if !before.is_empty() {
        replacing.push(Operation::new("TJ", vec![Object::Array(before)]));
    }
    replacing.push(Operation::new(
        "Tf",
        vec![Object::Name(RESOURCE.to_vec()), size.clone()],
    ));
    // ⚠️ THE SPAN WRAPS THE VISIBLE RUN AND NOTHING ELSE, both `Tf` operators
    // outside it, so the run's meaning travels with the run and neither font
    // switch sits inside a span a reader may treat as one unit.
    //
    // ⚠️ AND ONLY WHERE THE PER-GLYPH ORDER IS ACTUALLY WRONG. Text no shaper
    // reorders already extracts correctly one glyph at a time, and a span is
    // not free: PDFium re-derives the character positions inside one, which was
    // measured on a Latin replacement as a line whose word gaps went from
    // agreeing within 0.7pt to spreading over 1.05pt. A mechanism that buys
    // nothing here is not applied here.
    let span = crate::needs_shaping(new_text) && !reads_right_to_left(new_text);
    if span {
        replacing.push(actual_text(new_text));
    }
    replacing.push(Operation::new("TJ", vec![Object::Array(replacement)]));
    if span {
        replacing.push(Operation::new("EMC", vec![]));
    }
    replacing.push(Operation::new(
        "Tf",
        vec![Object::Name(plan.font_name.clone()), size],
    ));
    if !after.is_empty() {
        replacing.push(Operation::new("TJ", vec![Object::Array(after)]));
    }

    // ⚠️ EVERY OTHER OPERATOR IS RE-EMITTED IN PLACE. The enclosing BDC, its
    // property dictionary and MCID, the BT and the ET and the EMC are not
    // copied or rebuilt: only this one entry of the vector changes.
    let tj_at = plan.located.tj_at;
    let page_id = plan.located.page_id;
    let mut ops = plan.located.ops;
    let grew = replacing.len() - 1;
    ops.splice(tj_at..=tj_at, replacing);

    // ⚠️ ANY EARLIER RUN GOES FIRST. Removal is a lopdf operation here, which
    // is why Path B can have one at all: `FPDFPage_RemoveObject` followed by a
    // drop destroys the process, and the frozen text-box layer has to work
    // around that. Dropping operators from a vector has no such hazard.
    remove_path_b_runs(&mut ops, baseline);

    let at_end = closing_emc(&ops, tj_at + grew);
    for (k, op) in searchable.into_iter().enumerate() {
        ops.insert(at_end + k, op);
    }

    let Ok(encoded) = (Content { operations: ops }).encode() else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };

    // Reloaded rather than mutated in place: `locate` borrowed the parsed
    // document, and the write wants it fresh.
    let Ok(mut out) = Document::load_mem(bytes) else {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    };
    name_the_font(&mut out, page_id, RESOURCE, font_id)?;
    if out.change_page_content(page_id, encoded).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }

    let mut buf = Vec::new();
    if out.save_to(&mut buf).is_err() {
        return Err(STATUS_DOC_NOT_REWRITABLE);
    }
    Ok(buf)
}

/// Puts the embedded font into the page's `/Resources /Font` under `name`.
///
/// ⚠️ THIS IS WHAT KEEPS THE FONT. PDFium writes the font object out even when
/// nothing uses it (measured), but a content stream naming a resource the page
/// does not declare draws nothing at all. Both `/Resources` and `/Font` come in
/// two forms, inline or by reference, and a real producer uses either.
fn name_the_font(
    doc: &mut Document,
    page_id: lopdf::ObjectId,
    name: &[u8],
    font_id: lopdf::ObjectId,
) -> Result<(), i32> {
    let fail = |_| STATUS_DOC_NOT_REWRITABLE;
    let page = doc.get_dictionary(page_id).map_err(fail)?.clone();

    let (mut resources, resources_id) = match page.get(b"Resources") {
        Ok(Object::Reference(id)) => (doc.get_dictionary(*id).map_err(fail)?.clone(), Some(*id)),
        Ok(Object::Dictionary(d)) => (d.clone(), None),
        _ => (lopdf::Dictionary::new(), None),
    };

    let (mut fonts, fonts_id) = match resources.get(b"Font") {
        Ok(Object::Reference(id)) => (doc.get_dictionary(*id).map_err(fail)?.clone(), Some(*id)),
        Ok(Object::Dictionary(d)) => (d.clone(), None),
        _ => (lopdf::Dictionary::new(), None),
    };
    fonts.set(name.to_vec(), Object::Reference(font_id));

    match fonts_id {
        Some(id) => {
            doc.objects.insert(id, Object::Dictionary(fonts));
        }
        None => resources.set("Font", Object::Dictionary(fonts)),
    }
    match resources_id {
        Some(id) => {
            doc.objects.insert(id, Object::Dictionary(resources));
        }
        None => {
            let mut page = page;
            page.set("Resources", Object::Dictionary(resources));
            doc.objects.insert(page_id, Object::Dictionary(page));
        }
    }
    Ok(())
}

/// Where the marked-content sequence holding the edited line closes.
///
/// ⚠️ AFTER THE `EMC`, NEVER AFTER THE `ET`. Measured: going in after the `ET`
/// put the run INSIDE the original span, and its object then carried the
/// enclosing tag as well as ours. Tagged content must not gain a second copy
/// of the text it already holds.
///
/// A page with no marked content at all closes nowhere, and the run goes after
/// the line's own `ET` instead, which is still outside every span there is.
fn closing_emc(ops: &[Operation], from: usize) -> usize {
    if let Some(at) = ops[from..].iter().position(|o| o.operator == "EMC") {
        return (from + at + 1).min(ops.len());
    }
    match ops[from..].iter().position(|o| o.operator == "ET") {
        Some(at) => (from + at + 1).min(ops.len()),
        None => ops.len(),
    }
}

/// What the visible shaped run SAYS, as opposed to what it draws.
///
/// ⚠️ THIS IS THE ONLY THING THAT SURVIVES REORDERING. A shaper draws U+103C
/// before the consonant it was typed after, so the glyphs enter the content
/// stream in an order the characters are not in, and every per-glyph mechanism
/// reproduces the drawing order because that is the only order the stream has.
/// Measured: U+1019 U+103C written, drawn correctly, extracted as U+103C
/// U+1019. An explicit `/ToUnicode` built from the shaper's own clusters was
/// tried and is WORSE, because a CMap is keyed by glyph and one glyph serves
/// two clusters: the same file came back with a duplicated syllable, and with
/// junk characters where a glyph had no entry and fell through to the font
/// cmap. `/ActualText` is per-OCCURRENCE, which is the one thing a per-glyph
/// map can never be.
///
/// ⚠️ UTF-16BE WITH A BOM. A PDF text string is PDFDoc-encoded unless the BOM
/// says otherwise, and PDFDocEncoding cannot spell any of this. Hex, so no byte
/// needs an escaping rule to survive.
fn actual_text(text: &str) -> Operation {
    let mut utf16 = vec![0xFEu8, 0xFF];
    for unit in text.encode_utf16() {
        utf16.push((unit >> 8) as u8);
        utf16.push((unit & 0xFF) as u8);
    }
    let mut props = lopdf::Dictionary::new();
    props.set("ActualText", Object::String(utf16, lopdf::StringFormat::Hexadecimal));
    Operation::new("BDC", vec![Object::Name(b"Span".to_vec()), Object::Dictionary(props)])
}

/// Whether the run reads right to left, in which case NO span is written.
///
/// ⚠️ MEASURED: PDFIUM BIDI-REORDERS THE TEXT IT SUBSTITUTES. Arabic supplied
/// as `/ActualText` in correct logical order came back exactly reversed, while
/// the same edit WITHOUT a span already extracted in logical order: PDFium maps
/// the visual glyph stream back through the font's cmap and its own bidi pass
/// puts it right. So the span is for the scripts whose per-glyph order is
/// actually wrong, and RTL is left to the path that already works.
///
/// Any strong RTL character disqualifies the run, rather than the bidi
/// algorithm's first-strong rule. A replacement is one word in one script, and
/// the conservative answer costs nothing: not writing the span is exactly the
/// behaviour that was already correct.
fn reads_right_to_left(text: &str) -> bool {
    text.chars().any(|c| {
        let u = c as u32;
        (0x0590..=0x05FF).contains(&u)        // Hebrew
            || (0x0600..=0x06FF).contains(&u) // Arabic
            || (0x0700..=0x074F).contains(&u) // Syriac
            || (0x0750..=0x077F).contains(&u) // Arabic Supplement
            || (0x0780..=0x07BF).contains(&u) // Thaana
            || (0x07C0..=0x07FF).contains(&u) // NKo
            || (0x08A0..=0x08FF).contains(&u) // Arabic Extended-A
            || (0xFB1D..=0xFDFF).contains(&u) // Hebrew and Arabic presentation forms
            || (0xFE70..=0xFEFF).contains(&u)
    })
}

/// The invisible run that makes a shaped replacement searchable.
///
/// It draws nothing: render mode 3 is the same trick a scanned page's OCR layer
/// uses. What it contributes is the LOGICAL text, one glyph per character, so a
/// reader extracting the page gets what the user typed rather than what the
/// shaper drew.
///
/// ⚠️ THE FONT AND THE RENDER MODE ARE BOTH PUT BACK BEFORE THE `EMC`, so the
/// whole thing is one removable span AND nothing after it inherits either.
/// Measured without the font restore: the next line was drawn in the Identity-H
/// font and its own single bytes came back as two-byte codes, reading as CJK.
fn searchable_run(
    provisioned: &crate::provision::Provisioned,
    text: &str,
    advance_1000: f64,
    resource: &[u8],
    plan: &EditPlan,
    offset_pt: f64,
) -> Result<Vec<Operation>, i32> {
    let Some(codes) = crate::shaped::logical_codes(provisioned, text, advance_1000) else {
        return Err(STATUS_UNSUPPORTED);
    };

    // The line's own text matrix, shifted along by what precedes the
    // replacement. `offset_pt` is in the same points `Metrics::size` is, which
    // has the matrix's own scale folded into it already.
    let Some(tm) = plan.tm.clone() else {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    };
    if tm.len() != 6 {
        return Err(STATUS_LINE_NOT_REWRITABLE);
    }
    let e = number(&tm[4]).unwrap_or(0.0) + offset_pt;
    let mut placed = tm;
    placed[4] = Object::Real(e as f32);

    let size = Object::Real(plan.font_size as f32);
    let id = crate::mint_object_id();
    let mark = format!("{}{id}", crate::shaped::PATH_B_MARK_PREFIX);

    // ⚠️ A `BDC` WITH A PROPERTY DICTIONARY, NOT A BARE `BMC`, so the logical
    // text travels with the run instead of having to be decoded back out of
    // its glyphs. Written as a HEX string: these are raw UTF-8 bytes and hex
    // needs no escaping rules to survive them. Measured end to end, through a
    // real file: the mark NAME stays readable (Phase 5 and 6 both depend on
    // it) and the exact bytes come back.
    let mut recorded = lopdf::Dictionary::new();
    recorded.set(
        crate::shaped::PATH_B_TEXT_KEY,
        Object::String(text.as_bytes().to_vec(), lopdf::StringFormat::Hexadecimal),
    );

    Ok(vec![
        Operation::new("BDC", vec![
            Object::Name(mark.into_bytes()),
            Object::Dictionary(recorded),
        ]),
        Operation::new("BT", vec![]),
        Operation::new("Tf", vec![Object::Name(resource.to_vec()), size.clone()]),
        Operation::new("Tr", vec![Object::Integer(3)]),
        Operation::new("Tm", placed),
        Operation::new("TJ", vec![Object::Array(codes)]),
        Operation::new("Tr", vec![Object::Integer(0)]),
        Operation::new("Tf", vec![Object::Name(plan.font_name.clone()), size]),
        Operation::new("ET", vec![]),
        Operation::new("EMC", vec![]),
    ])
}

/// Drops the Path B invisible runs belonging to ONE line, and says how many.
///
/// ⚠️ REMOVED AS A SPAN, BY ITS MARK. Not by render mode, which OCR layers and
/// the text-box searchable layer also use. Everything from the `BDC` to its
/// matching `EMC` goes, which is exactly what `searchable_run` wrote, font and
/// render-mode restores included.
///
/// ⚠️ AND ONLY THIS LINE'S. It used to take every run on the page, so a second
/// shaped edit anywhere left the FIRST line's glyphs drawn with nothing left to
/// say what they spell: silently unsearchable, and unreadable by the block
/// model. A run is this line's when the `Tm` inside it sits on the same
/// baseline the caller is rewriting, which is the same test `locate` uses to
/// find the line in the first place.
///
/// ⚠️ A RUN WITH NO `Tm` IS LEFT ALONE. Everything `searchable_run` writes has
/// one, so a span without one is not ours in any recognisable form, and
/// destroying what cannot be identified is how searchable text goes missing.
pub(crate) fn remove_path_b_runs(ops: &mut Vec<Operation>, baseline: f32) -> usize {
    let mut removed = 0usize;
    let mut from = 0usize;
    loop {
        let Some(start) = ops.iter().skip(from).position(|o| {
            (o.operator == "BDC" || o.operator == "BMC")
                && matches!(o.operands.first(), Some(Object::Name(n))
                    if n.starts_with(crate::shaped::PATH_B_MARK_PREFIX.as_bytes()))
        }).map(|i| i + from) else {
            return removed;
        };
        // Balanced, so a span nested inside ours would not end it early.
        let mut depth = 0usize;
        let mut end = start;
        while end < ops.len() {
            match ops[end].operator.as_str() {
                "BMC" | "BDC" => depth += 1,
                "EMC" => {
                    depth -= 1;
                    if depth == 0 {
                        break;
                    }
                }
                _ => {}
            }
            end += 1;
        }
        let end = (end + 1).min(ops.len());

        // The baseline this span draws on, from the `Tm` inside it.
        let sits_here = ops[start..end].iter().any(|o| {
            o.operator == "Tm"
                && o.operands.get(5).and_then(number)
                    .is_some_and(|v| (v as f32 - baseline).abs() < BASELINE_TOLERANCE)
        });
        if !sits_here {
            from = start + 1;
            continue;
        }
        ops.drain(start..end);
        removed += 1;
        from = start;
    }
}
