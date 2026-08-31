//! THE PRODUCER'S OWN TEXT PIECES, read and written back in place.
//!
//! ⚠️ A LINE IS NOT ONE RUN. Real documents draw a line in several text-showing
//! operators, and the boundaries between them are exactly where the producer
//! changes colour, character spacing or render mode. Folding a line into one
//! array redraws every later piece in the FIRST one's state: measured on the
//! corpus, doing that cost 115 of the 129 blocks that would not come back.
//!
//! So an edit here rewrites ONE piece's array and leaves every other operator
//! in the stream exactly as the producer wrote it. Nothing is re-anchored: a
//! piece the producer placed keeps its own `Td`, and a piece the producer left
//! to follow on keeps following the renderer's own accumulated advance, which
//! is more exact than any width table we can read.

use crate::justified;
use lopdf::content::{Content, Operation};
use lopdf::{Document, Object};
use std::collections::BTreeMap;

/// A 3x2 PDF matrix, `[a b c d e f]`.
#[derive(Clone, Copy, Debug)]
pub(crate) struct M(pub [f64; 6]);

impl M {
    pub(crate) const ID: M = M([1.0, 0.0, 0.0, 1.0, 0.0, 0.0]);

    /// `self` applied first, then `n`.
    pub(crate) fn then(self, n: M) -> M {
        let (s, t) = (self.0, n.0);
        M([
            s[0] * t[0] + s[1] * t[2],
            s[0] * t[1] + s[1] * t[3],
            s[2] * t[0] + s[3] * t[2],
            s[2] * t[1] + s[3] * t[3],
            s[4] * t[0] + s[5] * t[2] + t[4],
            s[4] * t[1] + s[5] * t[3] + t[5],
        ])
    }

    /// Where this matrix puts the text-space origin, in y.
    pub(crate) fn origin_y(self) -> f64 {
        self.0[5]
    }

    pub(crate) fn translated(self, tx: f64, ty: f64) -> M {
        M([1.0, 0.0, 0.0, 1.0, tx, ty]).then(self)
    }
}

/// One text-showing operator, and everything needed to redraw it.
pub(crate) struct Piece {
    /// Its index in the page's operator list.
    pub at: usize,
    /// The baseline in USER space, which is what the block model reports.
    pub y: f64,
    /// The text matrix in force.
    pub tm: M,
    /// The transform the text matrix sits under.
    pub ctm: M,
    /// The text LINE matrix as this operator begins, and as it leaves it.
    /// They differ only for `'` and `"`, which move down a line before drawing.
    pub tlm_in: M,
    pub tlm_out: M,
    /// Whether the producer set a position for this piece, rather than letting
    /// it follow on from the piece before.
    pub repositioned: bool,
    /// The operator itself, because `'` and `"` also move to the next line and
    /// so cannot simply be swapped for a `TJ`.
    pub operator: String,
    pub font: Vec<u8>,
    pub size: f64,
    /// The character codes drawn, with the positioning numbers dropped.
    pub codes: Vec<u8>,
    /// All three change the advance and all three are graphics state: character
    /// spacing, word spacing, and horizontal scaling as a fraction.
    pub tc: f64,
    pub tw: f64,
    pub th: f64,
}

/// Every text-showing operator of a page, located through the full CTM.
///
/// ⚠️ THROUGH THE FULL CTM, NOT JUST `Tm`. Measured: 58.7% of the corpus's
/// pages carry a `cm`, and a `cm` can scale as well as move. The block model's
/// baselines come from PDFium with the transform already applied, so the stream
/// has to be read the same way or nothing lines up.
pub(crate) fn read(content: &Content) -> Vec<Piece> {
    let mut out = Vec::new();
    let mut ctm = M::ID;
    let mut stack: Vec<M> = Vec::new();
    let (mut tm, mut tlm) = (M::ID, M::ID);
    let mut leading = 0.0f64;
    let mut font: Vec<u8> = Vec::new();
    let mut size = 0.0f64;
    let (mut tc, mut tw, mut th) = (0.0f64, 0.0f64, 1.0f64);
    let mut font_stack: Vec<(Vec<u8>, f64)> = Vec::new();
    let mut placed = true;

    for (i, op) in content.operations.iter().enumerate() {
        // Taken before the operator runs, because `'` and `"` move the line
        // matrix themselves.
        let tlm_in = tlm;
        let num = |k: usize| {
            op.operands.get(k).and_then(|o| match o {
                Object::Integer(n) => Some(*n as f64),
                Object::Real(r) => Some(*r as f64),
                _ => None,
            })
        };
        let six = || -> Option<M> {
            let mut v = [0.0f64; 6];
            for (k, slot) in v.iter_mut().enumerate() {
                *slot = num(k)?;
            }
            Some(M(v))
        };
        match op.operator.as_str() {
            "q" => {
                stack.push(ctm);
                font_stack.push((font.clone(), size));
            }
            "Q" => {
                ctm = stack.pop().unwrap_or(M::ID);
                if let Some((f, sz)) = font_stack.pop() {
                    font = f;
                    size = sz;
                }
            }
            "cm" => {
                if let Some(m) = six() {
                    ctm = m.then(ctm);
                }
            }
            "BT" => {
                tm = M::ID;
                tlm = M::ID;
            }
            "Tf" => {
                if let Some(Object::Name(n)) = op.operands.first() {
                    font = n.clone();
                }
                size = num(1).unwrap_or(size);
            }
            "Tm" => {
                if let Some(m) = six() {
                    tm = m;
                    tlm = m;
                }
            }
            "Tc" => tc = num(0).unwrap_or(tc),
            "Tw" => tw = num(0).unwrap_or(tw),
            "Tz" => th = num(0).unwrap_or(th * 100.0) / 100.0,
            "TL" => leading = num(0).unwrap_or(leading),
            "Td" => {
                tlm = tlm.translated(num(0).unwrap_or(0.0), num(1).unwrap_or(0.0));
                tm = tlm;
            }
            "TD" => {
                leading = -num(1).unwrap_or(0.0);
                tlm = tlm.translated(num(0).unwrap_or(0.0), num(1).unwrap_or(0.0));
                tm = tlm;
            }
            "T*" => {
                tlm = tlm.translated(0.0, -leading);
                tm = tlm;
            }
            _ => {}
        }
        if op.operator == "'" || op.operator == "\"" {
            tlm = tlm.translated(0.0, -leading);
            tm = tlm;
        }
        if matches!(
            op.operator.as_str(),
            "BT" | "Tm" | "Td" | "TD" | "T*" | "'" | "\""
        ) {
            placed = true;
        }
        if !matches!(op.operator.as_str(), "TJ" | "Tj" | "'" | "\"") {
            continue;
        }

        let mut codes = Vec::new();
        match op.operands.first() {
            Some(Object::Array(a)) => {
                for o in a {
                    if let Object::String(b, _) = o {
                        codes.extend_from_slice(b);
                    }
                }
            }
            Some(Object::String(b, _)) => codes.extend_from_slice(b),
            _ => {}
        }
        // `"` carries word and char spacing before the string.
        if op.operator == "\"" {
            if let Some(Object::String(b, _)) = op.operands.get(2) {
                codes.clear();
                codes.extend_from_slice(b);
            }
        }

        out.push(Piece {
            at: i,
            y: tm.then(ctm).origin_y(),
            tm,
            ctm,
            tlm_in,
            tlm_out: tlm,
            repositioned: placed,
            operator: op.operator.clone(),
            font: font.clone(),
            size,
            codes,
            tc,
            tw,
            th,
        });
        placed = false;
    }
    out
}

/// Both directions of a font's own `/ToUnicode`, and how wide its codes are.
///
/// ⚠️ THE FONT'S OWN TABLE, NOT A GUESS. Editing means writing codes the
/// document's renderer will read back as the characters we meant, and the only
/// authority for that is the font's own map. A character it does not list is
/// refused rather than approximated.
pub(crate) struct Coding {
    /// Bytes per code: two for Identity-H, one for a simple font.
    width: usize,
    to_text: BTreeMap<u16, String>,
    to_code: BTreeMap<char, u16>,
}

impl Coding {
    /// `None` when the font's codes cannot be read at all: a Type0 under any
    /// CMap but Identity-H, where a code is not its own CID.
    pub(crate) fn of(doc: &Document, page_id: lopdf::ObjectId, font_name: &[u8]) -> Option<Coding> {
        let deref = |o: &Object| -> Option<lopdf::Dictionary> {
            match o {
                Object::Dictionary(d) => Some(d.clone()),
                Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
                _ => None,
            }
        };
        let page = doc.get_dictionary(page_id).ok()?;
        let resources = deref(page.get(b"Resources").ok()?)?;
        let fonts = deref(resources.get(b"Font").ok()?)?;
        let font = deref(fonts.get(font_name).ok()?)?;

        let subtype = font.get(b"Subtype").ok().and_then(|o| match o {
            Object::Name(n) => Some(n.clone()),
            _ => None,
        });
        let width = if subtype.as_deref() == Some(b"Type0".as_slice()) {
            if !matches!(font.get(b"Encoding"), Ok(Object::Name(n)) if n == b"Identity-H") {
                return None;
            }
            2
        } else {
            1
        };

        let mut to_text: BTreeMap<u16, String> = BTreeMap::new();
        let mut to_code: BTreeMap<char, u16> = BTreeMap::new();
        let mut ambiguous: Vec<char> = Vec::new();
        for (code, ch) in bf_pairs(doc, &font, width) {
            to_text.entry(code).or_insert_with(|| ch.to_string());
            match to_code.get(&ch) {
                Some(existing) if *existing != code => ambiguous.push(ch),
                Some(_) => {}
                None => {
                    to_code.insert(ch, code);
                }
            }
        }
        // A character two codes can write is one we cannot choose between.
        for ch in ambiguous {
            to_code.remove(&ch);
        }
        Some(Coding {
            width,
            to_text,
            to_code,
        })
    }

    /// Each code turned into the text it draws, kept separate so a character
    /// offset can be walked back to the byte that produced it.
    pub(crate) fn decode(&self, codes: &[u8]) -> Option<Vec<String>> {
        if codes.len() % self.width != 0 {
            return None;
        }
        let mut out = Vec::with_capacity(codes.len() / self.width);
        for chunk in codes.chunks_exact(self.width) {
            let code = if self.width == 2 {
                u16::from_be_bytes([chunk[0], chunk[1]])
            } else {
                chunk[0] as u16
            };
            out.push(self.to_text.get(&code)?.clone());
        }
        Some(out)
    }

    /// `None` if the font cannot write any one of these characters.
    pub(crate) fn encode(&self, text: &str) -> Option<Vec<u8>> {
        let mut out = Vec::with_capacity(text.chars().count() * self.width);
        for ch in text.chars() {
            let code = *self.to_code.get(&ch)?;
            if self.width == 2 {
                out.extend_from_slice(&code.to_be_bytes());
            } else {
                out.push(u8::try_from(code).ok()?);
            }
        }
        Some(out)
    }
}

/// The `bfchar` and `bfrange` pairs of a font's `/ToUnicode`, at the code width
/// the font actually uses.
///
/// ⚠️ WIDTH-AWARE, unlike the reader Path A uses, which takes one-byte codes
/// only because that is all Path A ever accepts.
fn bf_pairs(doc: &Document, font: &lopdf::Dictionary, width: usize) -> Vec<(u16, char)> {
    use justified::CmapToken;

    let Ok(entry) = font.get(b"ToUnicode") else {
        return Vec::new();
    };
    let stream = match entry {
        Object::Reference(id) => doc
            .get_object(*id)
            .ok()
            .and_then(|o| o.as_stream().ok())
            .cloned(),
        Object::Stream(st) => Some(st.clone()),
        _ => None,
    };
    let Some(stream) = stream else {
        return Vec::new();
    };
    let cmap = stream
        .decompressed_content()
        .unwrap_or_else(|_| stream.content.clone());

    let value = |bytes: &[u8]| -> Option<u16> {
        if bytes.len() != width {
            return None;
        }
        Some(if width == 2 {
            u16::from_be_bytes([bytes[0], bytes[1]])
        } else {
            bytes[0] as u16
        })
    };

    // A range wider than this is a mapping of a whole script, not of a line.
    const MAX_RANGE: u32 = 65_536;
    let tokens = justified::tokenize_cmap(&cmap);
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
                if let (Some(code), Some(ch)) = (value(src), justified::one_char_utf16be(dst)) {
                    out.push((code, ch));
                }
                i += 2;
            } else {
                let (Some(CmapToken::Hex(lo)), Some(CmapToken::Hex(hi))) =
                    (tokens.get(i), tokens.get(i + 1))
                else {
                    i += 1;
                    continue;
                };
                let (Some(lo), Some(hi)) = (value(lo), value(hi)) else {
                    i += 2;
                    continue;
                };
                if hi < lo || (hi as u32 - lo as u32) >= MAX_RANGE {
                    i += 2;
                    continue;
                }
                match tokens.get(i + 2) {
                    Some(CmapToken::Hex(dst)) => {
                        if let Some(first) = justified::one_char_utf16be(dst) {
                            for step in 0..=(hi - lo) {
                                let Some(ch) = char::from_u32(first as u32 + step as u32) else {
                                    break;
                                };
                                out.push((lo + step, ch));
                            }
                        }
                        i += 3;
                    }
                    Some(CmapToken::Open) => {
                        let mut j = i + 3;
                        let mut code = lo;
                        while let Some(tok) = tokens.get(j) {
                            match tok {
                                CmapToken::Hex(dst) => {
                                    if let Some(ch) = justified::one_char_utf16be(dst) {
                                        out.push((code, ch));
                                    }
                                    code = code.saturating_add(1);
                                    j += 1;
                                }
                                _ => break,
                            }
                        }
                        i = j + 1;
                    }
                    _ => i += 2,
                }
            }
        }
    }
    out
}

/// Replace `len` bytes of codes, starting at `at` counted across the array's
/// strings, keeping every positioning number that falls outside that span.
///
/// ⚠️ THE NUMBERS OUTSIDE THE SPAN ARE THE PRODUCER'S KERNING and they are
/// still correct for the text that is staying. The ones inside belong to the
/// characters being replaced and go with them.
pub(crate) fn splice(array: &[Object], at: usize, len: usize, new: &[u8]) -> Option<Vec<Object>> {
    if len == 0 {
        return None;
    }
    let end = at + len;
    let mut out: Vec<Object> = Vec::with_capacity(array.len() + 2);
    let mut cursor = 0usize;
    let mut written = false;
    for o in array {
        match o {
            Object::String(bytes, format) => {
                let start = cursor;
                let stop = cursor + bytes.len();
                cursor = stop;
                if stop <= at || start >= end {
                    out.push(Object::String(bytes.clone(), *format));
                    continue;
                }
                let head = at.saturating_sub(start).min(bytes.len());
                if head > 0 {
                    out.push(Object::String(bytes[..head].to_vec(), *format));
                }
                if !written {
                    if !new.is_empty() {
                        out.push(Object::String(new.to_vec(), lopdf::StringFormat::Hexadecimal));
                    }
                    written = true;
                }
                let tail = end.saturating_sub(start).min(bytes.len());
                if tail < bytes.len() {
                    out.push(Object::String(bytes[tail..].to_vec(), *format));
                }
            }
            other => {
                // A number sitting strictly inside the replaced span adjusted
                // characters that are going away.
                if cursor <= at || cursor >= end {
                    out.push(other.clone());
                }
            }
        }
    }
    if !written {
        return None;
    }
    Some(out)
}

/// What to do with one piece.
pub(crate) struct Emission {
    /// Its new array, or `None` to keep the producer's own verbatim.
    pub array: Option<Vec<Object>>,
    /// How far to slide it along the baseline, in text space.
    pub shift: f64,
}

/// The page's operators with the planned pieces rewritten, and every other
/// operator left exactly as the producer wrote it.
pub(crate) fn write(content: &Content, plan: &BTreeMap<usize, Emission>) -> Content {
    let mut ops = Vec::with_capacity(content.operations.len() + plan.len() * 2);
    for (i, op) in content.operations.iter().enumerate() {
        let Some(e) = plan.get(&i) else {
            ops.push(op.clone());
            continue;
        };
        let array = match &e.array {
            Some(a) => a.clone(),
            None => match op.operands.first() {
                Some(Object::Array(a)) => a.clone(),
                Some(Object::String(b, f)) => vec![Object::String(b.clone(), *f)],
                _ => Vec::new(),
            },
        };
        // ⚠️ A ZERO MOVE IS NOT FREE. `Td` sets the text matrix FROM the line
        // matrix, so emitting one to mean "stays put" throws away the
        // accumulated advance the next piece needs.
        let slide = |d: f64| {
            Operation::new(
                "Td",
                vec![Object::Real(d as f32), Object::Real(0.0)],
            )
        };
        if e.shift != 0.0 {
            ops.push(slide(e.shift));
        }
        ops.push(Operation::new("TJ", vec![Object::Array(array)]));
        if e.shift != 0.0 {
            ops.push(slide(-e.shift));
        }
    }
    Content {
        operations: ops,
    }
}

/// How far these codes advance the text matrix, in text space.
///
/// Per the spec: `((w0 - Tj/1000) * Tfs + Tc + Tw) * Th`, summed. Glyphs are
/// counted as GLYPHS: a Type0 code is two bytes, and word spacing applies to
/// the single-byte code 32, which an Identity-H font does not have.
fn advance_of(m: &justified::Metrics, piece: &Piece, codes: &[u8]) -> Option<f64> {
    let (glyphs, spaces) = if m.is_cid() {
        ((codes.len() / 2) as f64, 0.0)
    } else {
        (
            codes.len() as f64,
            codes.iter().filter(|b| **b == b' ').count() as f64,
        )
    };
    Some((m.width_of(codes)? + glyphs * piece.tc + spaces * piece.tw) * piece.th)
}

/// Which piece owns a span, where inside its codes it sits, and the codes to
/// put there. Both locators answer in exactly these terms.
type Span = (usize, usize, usize, Vec<u8>);

/// The span, found by reading the codes back through the font's own map.
///
/// Exact, and the only route that can address a two-byte font, because a
/// `/ToUnicode` states outright what every code means. It also proves it is
/// looking at the right line by decoding the whole of it and comparing.
#[allow(clippy::too_many_arguments)]
fn locate_by_coding(
    doc: &Document,
    page_id: lopdf::ObjectId,
    mine: &[&Piece],
    expected: &str,
    at: usize,
    len: usize,
    text: &str,
) -> Result<Span, i32> {
    // Decode the line from the stream and require it to be the line the model
    // says it is. This is what keeps two coordinate systems honest: the model
    // counts characters of its own text, the stream counts bytes of codes.
    let mut codings: BTreeMap<Vec<u8>, Coding> = BTreeMap::new();
    let mut drawn = String::new();
    // Where each piece's text starts in `drawn`, and its decoded chunks.
    let mut spans: Vec<(usize, Vec<String>)> = Vec::with_capacity(mine.len());
    for r in mine.iter() {
        if !codings.contains_key(&r.font) {
            let Some(c) = Coding::of(doc, page_id, &r.font) else {
                return Err(crate::STATUS_UNSUPPORTED);
            };
            codings.insert(r.font.clone(), c);
        }
        let coding = &codings[&r.font];
        let Some(chunks) = coding.decode(&r.codes) else {
            return Err(crate::STATUS_UNSUPPORTED);
        };
        let start = drawn.len();
        for chunk in &chunks {
            drawn.push_str(chunk);
        }
        spans.push((start, chunks));
    }
    if drawn != expected {
        return Err(crate::STATUS_STALE_ANCHOR);
    }

    // Which piece owns the span, and where inside its codes it sits.
    let end = at + len;
    let mut target: Option<(usize, usize, usize)> = None;
    for (k, (start, chunks)) in spans.iter().enumerate() {
        let mut here = *start;
        let (mut from, mut to) = (None, None);
        for (index, chunk) in chunks.iter().enumerate() {
            if here == at {
                from = Some(index);
            }
            here += chunk.len();
            if here == end {
                to = Some(index + 1);
            }
        }
        if let (Some(from), Some(to)) = (from, to) {
            let unit = codings[&mine[k].font].width;
            target = Some((k, from * unit, (to - from) * unit));
            break;
        }
    }
    // Not found means the span starts or ends inside a character, or crosses
    // from one piece into the next. Both are a selection this step cannot
    // address on its own.
    let Some((k, code_at, code_len)) = target else {
        return Err(crate::STATUS_SELECTION_NOT_ADDRESSABLE);
    };
    let Some(new_codes) = codings[&mine[k].font].encode(text) else {
        // ⚠️ EXACTLY AND ONLY THIS STATUS FALLS THROUGH TO PATH B upstream: the
        // document's own font cannot spell what was typed.
        return Err(crate::STATUS_UNSUPPORTED);
    };
    Ok((k, code_at, code_len, new_codes))
}

/// The span, found without asking the font what its codes mean.
///
/// ⚠️ THE TWO COUNTS WILL NOT AGREE, AND THEY DO NOT HAVE TO. Measured over
/// the corpus: every difference between the codes a producer draws and the
/// characters PDFium reports is whitespace. The producer writes two spaces
/// after a full stop and PDFium reports one; it writes none between two pieces
/// and PDFium generates one to stand for the gap; it writes one at the head of
/// a piece and PDFium drops it altogether. So this walk matches the characters
/// that are NOT whitespace one for one, and lets the whitespace between them
/// stretch. Against every line whose fonts WILL answer, it placed 388 of 388
/// spans on exactly the right text.
///
/// ⚠️ LATIN ONLY, AND THAT IS NOT A SIMPLIFICATION. PDFium reports Myanmar in
/// reading order while the stream draws it in visual order, so for a shaped
/// script the counts can agree while the mapping is a permutation: the line
/// reads `ဖြ` and the codes spell `ြဖ`. Every span this walk ever placed
/// wrongly was shaped, and not one was Latin. Reordering is a separate problem
/// and guessing at it here would corrupt the text silently.
#[allow(clippy::too_many_arguments)]
fn locate_by_model(
    doc: &Document,
    page_id: lopdf::ObjectId,
    mine: &[&Piece],
    expected: &str,
    at: usize,
    len: usize,
    text: &str,
) -> Result<Span, i32> {
    if expected.chars().any(|c| (c as u32) >= 0x0300) {
        return Err(crate::STATUS_UNSUPPORTED);
    }

    // Every code drawn on the baseline, end to end, and which piece drew each.
    let mut codes: Vec<u8> = Vec::new();
    let mut owner: Vec<usize> = Vec::new();
    let mut metrics: BTreeMap<Vec<u8>, justified::Metrics> = BTreeMap::new();
    for (k, r) in mine.iter().enumerate() {
        if !metrics.contains_key(&r.font) {
            // The size is not one of the questions asked of these metrics:
            // encoding, spelling and remapping are all size-independent. It is
            // floored only because `font_metrics` refuses a size of zero, and
            // an invisible piece should not cost the line its edit.
            let m = justified::font_metrics(doc, page_id, &r.font, r.size.max(0.001))?;
            // This route writes one byte per character, which a Type0's
            // two-byte codes are not; and it reads a space out of the stream by
            // its code, which a font that remapped 32 no longer writes.
            if m.is_cid() || m.remapped(b' ') {
                return Err(crate::STATUS_UNSUPPORTED);
            }
            metrics.insert(r.font.clone(), m);
        }
        codes.extend(r.codes.iter().copied());
        owner.extend(std::iter::repeat(k).take(r.codes.len()));
    }

    // ⚠️ WALKED AGAINST THE LINE, WHICH IS ALREADY CUT. `expected` is the
    // model's drawn text sliced by the line's `prefix` and `suffix`, so the
    // baseline can carry codes on either side of it. Leading space codes are
    // stepped over and the tail is required to be spaces too.
    //
    // ⚠️ WHICH IS WHY A LINE WHOSE CUT IS NOT WHITESPACE IS REFUSED, NOT
    // REPAIRED. On the 531 lines that do align the cut is whitespace or nothing
    // every time, but that number is measured on the survivors: a line cut
    // through real letters simply falls out of step here and is refused. The
    // model does know where the cut is, in the first run's `obj_at`, and
    // threading it through is the next widening rather than a guess made here.
    let mut marks: Vec<(usize, usize)> = Vec::with_capacity(expected.len() + 1);
    let mut i = 0usize;
    for (byte, ch) in expected.char_indices() {
        if !ch.is_whitespace() {
            while i < codes.len() && codes[i] == b' ' {
                i += 1;
            }
        }
        marks.push((byte, i));
        if ch.is_whitespace() {
            while i < codes.len() && codes[i] == b' ' {
                i += 1;
            }
        } else {
            if i >= codes.len() || codes[i] == b' ' {
                return Err(crate::STATUS_STALE_ANCHOR);
            }
            i += 1;
        }
    }
    if codes[i..].iter().any(|c| *c != b' ') {
        return Err(crate::STATUS_STALE_ANCHOR);
    }
    marks.push((expected.len(), i));

    let code_of = |byte: usize| marks.iter().find(|(b, _)| *b == byte).map(|(_, c)| *c);
    let (Some(code_at), Some(code_end)) = (code_of(at), code_of(at + len)) else {
        return Err(crate::STATUS_SELECTION_NOT_ADDRESSABLE);
    };
    // A span of pure whitespace can be a character PDFium generated, which no
    // code drew and so nothing can be written over.
    if code_end <= code_at {
        return Err(crate::STATUS_SELECTION_NOT_ADDRESSABLE);
    }

    // ⚠️ ONE PIECE, OR NOTHING. A span reaching over a piece boundary would
    // have to be divided between two arrays the producer positioned
    // independently, and nothing in the model says where to divide it. Measured
    // at 66 of 1034 spans, refused rather than guessed at.
    let k = owner[code_at];
    if owner[code_at..code_end].iter().any(|o| *o != k) {
        return Err(crate::STATUS_SELECTION_NOT_ADDRESSABLE);
    }
    let start = owner.iter().position(|o| *o == k).unwrap_or(0);
    let m = &metrics[&mine[k].font];

    // ⚠️ THE ANCHOR, AND THE ONLY ONE THIS ROUTE HAS. The other route proves
    // it is looking at the right line by decoding the whole of it; with no
    // `/ToUnicode` there is nothing to decode. So the codes about to be DELETED
    // are encoded back out of the model's own text and required to be the very
    // bytes already sitting there. Measured: the encoder answers a DIFFERENT
    // code on 5 lines of the corpus, every one a subset font whose
    // `/Differences` renumber the glyphs from 1, where WinAnsi's answer for an
    // ASCII letter is a glyph the font does not have.
    let Some(was) = m.encode(&expected[at..at + len]) else {
        return Err(crate::STATUS_UNSUPPORTED);
    };
    if was != codes[code_at..code_end] {
        return Err(crate::STATUS_STALE_ANCHOR);
    }

    let Some(new_codes) = m.encode(text) else {
        return Err(crate::STATUS_UNSUPPORTED);
    };
    // A code outside the width table is a code the font cannot be shown to
    // have, which for a subset font is the same question as whether it can
    // spell it.
    if !m.can_spell(&new_codes) {
        return Err(crate::STATUS_UNSUPPORTED);
    }
    Ok((k, code_at - start, code_end - code_at, new_codes))
}

/// Replace `len` bytes of one line's own text, at UTF-8 byte offset `at` within
/// it, with `text`.
///
/// ⚠️ THE LINE IS NOT RE-LAID-OUT. Every piece stays where the producer put it
/// and only one piece's array changes, so the line grows or shrinks to the
/// right and nothing else on the page moves. Anything that would need the rest
/// of the line to move is refused here rather than guessed at; that is reflow,
/// and reflow is a later step.
#[allow(clippy::too_many_arguments)]
pub(crate) fn replace_in_line(
    doc: &Document,
    page_id: lopdf::ObjectId,
    content: &Content,
    baseline: f32,
    expected: &str,
    at: usize,
    len: usize,
    text: &str,
    right_limit: Option<f64>,
) -> Result<Content, i32> {
    if content.operations.iter().any(|o| o.operator == "BI") {
        return Err(crate::STATUS_INLINE_IMAGE_PAGE);
    }
    if len == 0 || at + len > expected.len() {
        return Err(crate::STATUS_INVALID_INPUT);
    }

    let pieces = read(content);
    let mine: Vec<&Piece> = pieces
        .iter()
        .filter(|r| (r.y - baseline as f64).abs() < 0.05)
        .collect();
    if mine.is_empty() {
        return Err(crate::STATUS_LINE_NOT_REWRITABLE);
    }
    // `'` and `"` move to the next line as well as drawing, so swapping one for
    // a `TJ` would silently drop that move.
    if mine.iter().any(|r| r.operator == "'" || r.operator == "\"") {
        return Err(crate::STATUS_LINE_NOT_REWRITABLE);
    }

    // ⚠️ TWO WAYS OF ASKING, AND THE ORDER IS THE COMPATIBILITY GUARANTEE.
    // Reading the codes back through the font's own `/ToUnicode` is exact and
    // handles a two-byte font, so it keeps every line it already handled. It
    // needs a `/ToUnicode`, and 186 of the corpus blocks this writer refuses
    // have none; those go to the model-driven walk instead.
    let (k, code_at, code_len, new_codes) =
        match locate_by_coding(doc, page_id, &mine, expected, at, len, text) {
            Ok(found) => found,
            Err(coding) => locate_by_model(doc, page_id, &mine, expected, at, len, text)
                // The first route's refusal is the more specific one whenever
                // the second simply does not take this kind of line.
                .map_err(|model| if model == crate::STATUS_UNSUPPORTED { coding } else { model })?,
        };
    let piece = mine[k];
    let old_codes = piece.codes[code_at..code_at + code_len].to_vec();

    let array: Vec<Object> = match content.operations[piece.at].operands.first() {
        Some(Object::Array(a)) => a.clone(),
        Some(Object::String(b, f)) => vec![Object::String(b.clone(), *f)],
        _ => return Err(crate::STATUS_LINE_NOT_REWRITABLE),
    };
    let Some(spliced) = splice(&array, code_at, code_len, &new_codes) else {
        return Err(crate::STATUS_LINE_NOT_REWRITABLE);
    };
    // ⚠️ NOWHERE TO PUT A GLYPH THAT WAS NOT THERE BEFORE. A positioning number
    // sitting immediately after the span is the producer adjusting the NEXT
    // glyph, which is what a line positioned glyph by glyph looks like. Adding
    // a glyph there leaves it without an adjustment of its own, and the gap the
    // producer had closed reopens. Deleting is safe: both neighbours keep their
    // adjustments and the line closes up.
    if new_codes.len() > old_codes.len() && adjusted_at(&array, code_at + code_len) {
        return Err(crate::STATUS_PRODUCER_POSITIONS_EACH_GLYPH);
    }

    // How much wider the line just became, and whether anything has to move
    // because of it.
    let metrics = justified::font_metrics(doc, page_id, &piece.font, piece.size)?;
    let (Some(was), Some(now)) = (
        advance_of(&metrics, piece, &old_codes),
        advance_of(&metrics, piece, &new_codes),
    ) else {
        return Err(crate::STATUS_FONT_METRICS_UNAVAILABLE);
    };
    let grew = now - was;

    // ⚠️ ONLY THE LAST PIECE OF A LINE MAY CHANGE WIDTH. Anything after it that
    // the producer positioned would stay put and be written over, and anything
    // that follows on would move by the RENDERER's advance while a piece after
    // that moved by ours. Sliding them is reflow's job, with reflow's gates.
    if grew.abs() > 1e-9 && k + 1 < mine.len() {
        return Err(crate::STATUS_BLOCK_NEEDS_REFLOW);
    }

    // Only a line that GREW can run out of room. Checking a line that did not
    // move compares a piece's origin against an ink edge, which are not the
    // same measurement, and refused 5 blocks that had not changed at all.
    if let (Some(limit), true) = (right_limit, grew > 0.0) {
        // Text space to user space, for the horizontal direction.
        let scale = piece.tm.then(piece.ctm).0[0];
        let right = mine
            .iter()
            .map(|r| r.tm.then(r.ctm).0[4])
            .fold(f64::MIN, f64::max);
        if right + grew * scale > limit + 0.5 {
            return Err(crate::STATUS_TOO_WIDE);
        }
    }

    let mut plan: BTreeMap<usize, Emission> = BTreeMap::new();
    plan.insert(
        piece.at,
        Emission {
            array: Some(spliced),
            shift: 0.0,
        },
    );
    Ok(write(content, &plan))
}

/// `replace_in_line` over a whole document's bytes, which is the shape the
/// block emitter already works in.
#[allow(clippy::too_many_arguments)]
pub(crate) fn rewrite_bytes(
    bytes: &[u8],
    page_index: i32,
    baseline: f32,
    expected: &str,
    at: usize,
    len: usize,
    text: &str,
    right_limit: Option<f64>,
) -> Result<Vec<u8>, i32> {
    let mut doc = Document::load_mem(bytes).map_err(|_| crate::STATUS_DOC_NOT_REWRITABLE)?;
    let page_id = *doc
        .get_pages()
        .get(&(page_index as u32 + 1))
        .ok_or(crate::STATUS_INVALID_INPUT)?;
    let content = Content::decode(&doc.get_page_content(page_id)).map_err(|_| crate::STATUS_DOC_NOT_REWRITABLE)?;
    let rebuilt = replace_in_line(
        &doc, page_id, &content, baseline, expected, at, len, text, right_limit,
    )?;
    let encoded = rebuilt.encode().map_err(|_| crate::STATUS_DOC_NOT_REWRITABLE)?;
    doc.change_page_content(page_id, encoded).map_err(|_| crate::STATUS_DOC_NOT_REWRITABLE)?;
    let mut out = Vec::new();
    doc.save_to(&mut out).map_err(|_| crate::STATUS_DOC_NOT_REWRITABLE)?;
    Ok(out)
}

/// Whether a positioning number sits exactly at this byte offset into the
/// array's codes.
fn adjusted_at(array: &[Object], offset: usize) -> bool {
    let mut cursor = 0usize;
    for o in array {
        match o {
            Object::String(bytes, _) => cursor += bytes.len(),
            _ if cursor == offset => return true,
            _ => {}
        }
    }
    false
}
