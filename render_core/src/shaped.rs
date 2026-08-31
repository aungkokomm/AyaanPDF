//! Turning shaped glyphs into the `TJ` array a PDF page draws them with.
//!
//! ⚠️ NOTHING HERE LOCATES ANYTHING. It is given glyphs and the font's own
//! widths and produces operands. Finding the line stays in `justified`, which
//! Path A and Path B share, and the two emissions stay apart.
//!
//! ⚠️ AND PDFIUM MUST NEVER REGENERATE THE PAGE AFTERWARDS.
//! `CPDF_PageContentGenerator::ProcessText` emits a single hex `Tj` and throws
//! per-glyph positions away, which is exactly what this builds. Read in the
//! source, and it is the ceiling on every PDFium-based editor.

use std::collections::BTreeMap;

use lopdf::{Document, Object, StringFormat};

use crate::provision::Provisioned;
use crate::ShapedGlyph;

/// The default width of a CID with no `/W` entry. The PDF default when the
/// descendant font omits `/DW`, which the font PDFium writes does: measured,
/// `/DW` absent and `/W` present.
const DEFAULT_WIDTH: f64 = 1000.0;

/// A `TJ` correction below this is not worth an array element. In glyph space,
/// so a thousandth of an em.
const CORRECTION_EPSILON: f64 = 1.0;

/// The size a replacement is shaped at.
///
/// ⚠️ GLYPH SPACE, NOT POINTS. `run` works in thousandths of an em, where the
/// type size cancels out of every comparison with `/W`. Shaping at 1000 makes
/// that conversion exact rather than merely close, because the advance comes
/// back as the glyph-space number directly. It also means the emitter never has
/// to be told what size the line is set in, so it cannot be told a wrong one,
/// and the line does not have to be located twice to find out.
pub(crate) const SHAPING_SIZE: f32 = 1000.0;

/// What the renderer will advance by for each CID, which is not what the
/// shaper decided.
///
/// ⚠️ THE TWO GENUINELY DISAGREE. PDFium builds `/W` by walking the font's
/// cmap, so every glyph GSUB produced (a ligature, a conjunct, a reordered
/// form) is missing from it and falls back to `/DW`. Measured: Devanagari
/// glyph 407 had no `/W` entry and drew 1000 wide where the shaper wanted 717.
pub(crate) struct CidWidths {
    default: f64,
    by_cid: BTreeMap<u16, f64>,
}

impl CidWidths {
    pub(crate) fn of(&self, cid: u16) -> f64 {
        self.by_cid.get(&cid).copied().unwrap_or(self.default)
    }
}

fn number(o: &Object) -> Option<f64> {
    match o {
        Object::Integer(i) => Some(*i as f64),
        Object::Real(r) => Some(*r as f64),
        _ => None,
    }
}

fn resolve<'a>(doc: &'a Document, o: &'a Object) -> Option<lopdf::Dictionary> {
    match o {
        Object::Reference(id) => doc.get_dictionary(*id).ok().cloned(),
        Object::Dictionary(d) => Some(d.clone()),
        _ => None,
    }
}

/// Reads `/W` and `/DW` off a Type0 font's descendant.
///
/// Both forms of `/W` are read: `c [w1 w2 ...]`, which lists consecutive CIDs,
/// and `c_first c_last w`, which gives a whole span one width.
pub(crate) fn cid_widths(doc: &Document, font_id: lopdf::ObjectId) -> Option<CidWidths> {
    let top = doc.get_dictionary(font_id).ok()?;
    let first = match top.get(b"DescendantFonts").ok()? {
        Object::Array(a) => a.first().cloned(),
        Object::Reference(r) => doc
            .get_object(*r)
            .ok()
            .and_then(|x| x.as_array().ok())
            .and_then(|a| a.first().cloned()),
        _ => None,
    }?;
    let descendant = resolve(doc, &first)?;

    let default = descendant
        .get(b"DW")
        .ok()
        .and_then(number)
        .unwrap_or(DEFAULT_WIDTH);

    let mut by_cid = BTreeMap::new();
    let array = match descendant.get(b"W") {
        Ok(Object::Array(a)) => Some(a.clone()),
        Ok(Object::Reference(id)) => doc
            .get_object(*id)
            .ok()
            .and_then(|x| x.as_array().ok())
            .cloned(),
        _ => None,
    };
    if let Some(array) = array {
        let mut i = 0usize;
        while i < array.len() {
            let Ok(start) = array[i].as_i64() else { break };
            i += 1;
            let Some(next) = array.get(i) else { break };
            match next {
                Object::Array(list) => {
                    for (k, w) in list.iter().enumerate() {
                        if let Some(w) = number(w) {
                            if let Ok(cid) = u16::try_from(start + k as i64) {
                                by_cid.insert(cid, w);
                            }
                        }
                    }
                    i += 1;
                }
                _ => {
                    let Ok(end) = next.as_i64() else { break };
                    i += 1;
                    let width = array.get(i).and_then(number).unwrap_or(default);
                    i += 1;
                    // A malformed span is skipped rather than iterated forever.
                    if end >= start && end - start < u16::MAX as i64 {
                        for cid in start..=end {
                            if let Ok(cid) = u16::try_from(cid) {
                                by_cid.insert(cid, width);
                            }
                        }
                    }
                }
            }
        }
    }
    Some(CidWidths { default, by_cid })
}

/// Glyph ids as the two-byte codes an Identity-H font takes.
///
/// ⚠️ CODE IS CID IS GID, and only for the font these glyphs were shaped
/// against. A glyph id means nothing whatsoever against a different face: an
/// earlier experiment aimed the right ids at the wrong font object and drew
/// "Replacement text" as "5eSlaFePent text".
fn codes(gids: &[u16]) -> Object {
    Object::String(
        gids.iter().flat_map(|g| g.to_be_bytes()).collect(),
        StringFormat::Hexadecimal,
    )
}

/// The `TJ` operands for a shaped run, and what it advances by in glyph space.
///
/// The run is RIGID: it carries the corrections that make the renderer advance
/// the way the shaper intended, and nothing else. Stretching spaces inside a
/// replacement is a separate question, and the line's own justification slots
/// live outside this array.
pub(crate) fn run(p: &Provisioned, text: &str, widths: &CidWidths) -> Run {
    let size_pts = p.size_pts;
    let mut out: Vec<Object> = Vec::new();
    let mut pending: Vec<u16> = Vec::new();
    let mut slots: Vec<usize> = Vec::new();
    let mut advance = 0.0f64;

    // ⚠️ A SPACE IS FOUND THROUGH ITS CLUSTER, NOT BY ITS GLYPH ID. The cluster
    // is the byte offset of the characters a glyph came from, which is the
    // font-independent answer; comparing against whatever id the face happens
    // to give U+0020 assumes a mapping the face is free not to have.
    let source = text.as_bytes();
    let is_space = |glyph: &ShapedGlyph| -> bool {
        source.get(glyph.cluster as usize) == Some(&b' ')
    };

    for (index, glyph) in p.glyphs.iter().enumerate() {
        let cid = glyph.id as u16;
        pending.push(cid);

        // Both in glyph space, thousandths of an em, so the type size cancels.
        let shaped = if size_pts > 0.0 {
            glyph.x_advance as f64 / size_pts as f64 * 1000.0
        } else {
            0.0
        };
        let drawn = widths.of(cid);

        // ⚠️ THE ADVANCE IS WHAT THE PAGE WILL DO, NOT WHAT THE SHAPER WANTED.
        // The renderer moves by /W and then by whatever correction is written,
        // so both are accumulated exactly as it will apply them, INCLUDING the
        // rounding and the f32 the operand is stored as. Reporting the shaped
        // advance instead left the line 0.0034pt short: every correction below
        // the epsilon is a difference the renderer keeps and the caller was
        // never told about, and the caller is what solves the justification.
        advance += drawn;
        let excess = drawn - shaped;
        let correcting = excess.abs() > CORRECTION_EPSILON;

        // ⚠️ A TRAILING SPACE IS NOT A SLOT, exactly as on the line's own
        // arrays: a justification slot is the gap BETWEEN two things, and
        // there is nothing after the last glyph to hold apart.
        let slot = is_space(glyph) && index + 1 < p.glyphs.len();

        if correcting || slot {
            out.push(codes(&std::mem::take(&mut pending)));
        }
        if correcting {
            let written = ((excess * 1000.0).round() / 1000.0) as f32;
            out.push(Object::Real(written));
            advance -= written as f64;
        }
        if slot {
            // Emitted SEPARATELY from the correction rather than folded into
            // it. The solver overwrites a slot wholesale, so a correction
            // sharing the element would be thrown away and the space would
            // draw at the wrong width.
            slots.push(out.len());
            out.push(Object::Real(0.0));
        }
    }
    if !pending.is_empty() {
        out.push(codes(&pending));
    }
    Run { array: out, advance, slots }
}

/// A shaped replacement ready to be written into a `TJ`.
pub(crate) struct Run {
    pub array: Vec<Object>,
    /// What the page will advance by, before any justification stretch.
    pub advance: f64,
    /// Which elements of `array` are justification slots, for the solver to
    /// fill. Empty when the replacement holds no interior space.
    pub slots: Vec<usize>,
}
/// The mark that ties an invisible searchable run to the Path B edit it stands
/// for.
///
/// ⚠️ DELIBERATELY NOT `AyaanSearch:`. That layer is rebuilt WHOLESALE from the
/// page's ANNOTATIONS by `sync_text_layer_inner`, which deletes every object
/// carrying its mark and re-adds only what an annotation accounts for. A Path B
/// edit is not an annotation, so a run wearing that mark would be swept away on
/// the next sync and never come back. Its own prefix gives it its own
/// lifecycle, written and removed by lopdf alongside the edit itself.
pub(crate) const PATH_B_MARK_PREFIX: &str = "AyaanPathB:";

/// The property the invisible run RECORDS its logical text under.
///
/// ⚠️ RECORDED, NOT RE-DERIVED, AND THAT IS THE WHOLE POINT. The text is known
/// exactly at write time: it is what the user typed. Reading it back by
/// decoding the glyphs was measured to depend on where the run sits: the same
/// four codes with the same gaps came back as four characters on one line and
/// three on another, because extraction answers through a text page whose
/// answers depend on what else is near them.
pub(crate) const PATH_B_TEXT_KEY: &str = "AyaanText";

/// The replacement's characters as codes, one glyph per CHARACTER, stretched to
/// occupy `target` glyph-space units.
///
/// ⚠️ UNSHAPED, AND THAT IS THE ENTIRE POINT. The visible run's glyphs are what
/// the shaper produced, and PDFium builds `/ToUnicode` by walking the font's
/// cmap, so every glyph GSUB invented is missing from it: measured, a Devanagari
/// conjunct extracts as `Ɨा` and Burmese extracts in visual order. The
/// per-character glyphs are all in the cmap, so they carry correct `/ToUnicode`
/// entries and the logical string survives extraction.
///
/// ⚠️ AND IT IS STRETCHED TO THE VISIBLE WIDTH. A highlight is drawn on these
/// characters' boxes, and it has to land on the glyphs a reader can actually
/// see. The stand-ins are a different width entirely, so the difference is
/// spread across the gaps between them. A single character has no gaps and
/// keeps its natural width, positioned at the replacement's left edge.
pub(crate) fn logical_codes(p: &Provisioned, text: &str, target: f64) -> Option<Vec<Object>> {
    let face = rustybuzz::Face::from_slice(p.bytes.as_slice(), 0)?;
    let upem = face.units_per_em() as f64;
    if upem <= 0.0 {
        return None;
    }

    let mut glyphs: Vec<(u16, f64)> = Vec::new();
    for c in text.chars() {
        // A character the face cannot spell would draw .notdef and mean
        // nothing; the visible run has already been refused in that case, so
        // reaching here with one is not something to paper over.
        let id = face.glyph_index(c)?;
        let advance = face.glyph_hor_advance(id).unwrap_or(0) as f64 / upem * 1000.0;
        glyphs.push((id.0, advance));
    }
    if glyphs.is_empty() {
        return None;
    }

    let natural: f64 = glyphs.iter().map(|(_, a)| a).sum();
    let gaps = glyphs.len().saturating_sub(1);
    let spread = if gaps > 0 { (target - natural) / gaps as f64 } else { 0.0 };

    let mut out: Vec<Object> = Vec::with_capacity(glyphs.len() * 2);
    for (i, (id, _)) in glyphs.iter().enumerate() {
        out.push(codes(&[*id]));
        if i < gaps {
            // A TJ number is SUBTRACTED, so a negative one opens the gap.
            out.push(Object::Real(((-spread * 1000.0).round() / 1000.0) as f32));
        }
    }
    Some(out)
}
