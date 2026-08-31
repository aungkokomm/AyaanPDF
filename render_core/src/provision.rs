//! Choosing and acquiring the font Path B would draw a replacement with.
//!
//! ⚠️ THIS DECIDES, IT DOES NOT WRITE. Everything here reads: a font file, the
//! face's own embedding permission, whether it can spell the text, and what the
//! shaper makes of it. The single exception is `embed`, which adds the font
//! object to a document, and nothing in production calls it yet.
//!
//! ⚠️ AND IT KNOWS NOTHING ABOUT ANNOTATIONS. The Ayaan text-box engine is
//! reused here as a font-loading and shaping engine and nothing more. Its tags,
//! its bounds, its annotation output and its searchable layer are all absent on
//! purpose. When Path B comes to write an invisible searchable run it will
//! carry a mark of its OWN: `sync_text_layer_inner` rebuilds the `AyaanSearch:`
//! layer wholesale from the page's ANNOTATIONS, deleting every object that
//! carries that mark and re-adding only the ones an annotation accounts for. A
//! Path B edit is not an annotation, so a run marked `AyaanSearch:` would be
//! swept away on the next sync and never come back.

use std::sync::Arc;

use crate::{font_file_bytes, glyph_text_map, shape_run, ShapedGlyph};

/// Why a font cannot be used to draw a replacement.
///
/// Deliberately NOT an FFI status. Path B has no entry point yet, so deciding
/// which public status number each of these becomes belongs to the phase that
/// adds one. A closed set here lets a test assert on the actual reason instead
/// of on an integer that several unrelated reasons already share.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Refusal {
    /// No font was named. There is no default, on purpose.
    NoFontNamed,
    /// The file could not be read.
    FileUnreadable,
    /// The bytes are not a face a shaper can use.
    NotAFace,
    /// The font's own `OS/2` table says it must not be embedded.
    EmbeddingProhibited,
    /// The face has no glyph for something the replacement asks for.
    CannotSpell,
    /// PDFium would not take the font.
    NotEmbeddable,
}

/// A font that may be embedded, can spell the text, and what it would draw.
pub(crate) struct Provisioned {
    /// The face's bytes, shared with the text-box path's cache.
    pub bytes: Arc<Vec<u8>>,
    /// What the shaper decided: visual order, with per-glyph positions.
    pub glyphs: Vec<ShapedGlyph>,
    /// Glyph id to the text it means. Carried with the glyphs because
    /// `ShapedGlyph::cluster` is the only link back from a drawn glyph to the
    /// characters it came from, and it does not survive being recomputed later
    /// from the glyph ids alone.
    pub to_unicode: Vec<(u32, String)>,
    /// The size the glyphs were shaped at. Carried so an emitter cannot be
    /// handed a size the advances were never measured against.
    pub size_pts: f32,
}

/// Whether `text` can be drawn at `size_pts` in the font at `font_path`, and
/// everything needed to draw it if so.
pub(crate) fn provision(
    font_path: Option<&str>,
    text: &str,
    size_pts: f32,
) -> Result<Provisioned, Refusal> {
    let Some(path) = font_path.filter(|p| !p.is_empty()) else {
        return Err(Refusal::NoFontNamed);
    };
    let bytes = font_file_bytes(path).ok_or(Refusal::FileUnreadable)?;

    // The face is parsed here as well as inside `shape_run`, which is left
    // exactly as it is: the permission question is one to ask of the face, and
    // asking it here is cheaper than giving the shaper a second job.
    {
        let face = rustybuzz::Face::from_slice(bytes.as_slice(), 0).ok_or(Refusal::NotAFace)?;
        if !may_embed(&face) {
            return Err(Refusal::EmbeddingProhibited);
        }
    }

    let glyphs = shape_run(bytes.as_slice(), text, size_pts).ok_or(Refusal::NotAFace)?;

    // ⚠️ .notdef IS NOT A GLYPH. A face that cannot spell a character shapes it
    // to glyph 0, which draws as an empty box or as nothing at all. Refusing is
    // the entire point of asking: the alternative is an edit that reports
    // success and leaves the page wrong.
    if glyphs.iter().any(|g| g.id == 0) {
        return Err(Refusal::CannotSpell);
    }

    let to_unicode = glyph_text_map(&glyphs, text);
    Ok(Provisioned { bytes, glyphs, to_unicode, size_pts })
}

/// Whether the font's own `OS/2` table permits embedding.
///
/// ⚠️ THE RULE IS THE FONT'S, NOT OURS. Only an explicit prohibition refuses.
/// `Restricted` is the one value that means "must not be embedded"; a font with
/// no `OS/2` table, or with an `fsType` too malformed to read, has declared
/// nothing, and inventing a policy where the font states none is not ours to
/// do.
///
/// The most-permissive reading of `fsType` before version 3 and the
/// mutually-exclusive reading from version 3 onwards are both the font format's
/// own rules, and both are applied by `ttf_parser`.
fn may_embed(face: &rustybuzz::Face) -> bool {
    use rustybuzz::ttf_parser::Permissions;
    !matches!(
        face.tables().os2.and_then(|os2| os2.permissions()),
        Some(Permissions::Restricted)
    )
}

/// Embeds a provisioned font in `doc` and hands back the token to draw with.
///
/// ⚠️ DELIBERATELY NOT `resolve_text_font`. That one answers a text box's
/// question, "give me something to draw with", and falls back to Helvetica
/// whenever no path is given or the file will not load. For a text box that is
/// a sensible default. For Path B it is the worst outcome available: Latin
/// letters standing where Devanagari was asked for, on a page that reports the
/// edit succeeded. So this refuses instead, and `resolve_text_font` is left
/// untouched for the callers it already serves.
pub(crate) fn embed(
    doc: &mut pdfium_render::prelude::PdfDocument<'_>,
    provisioned: &Provisioned,
) -> Result<pdfium_render::prelude::PdfFontToken, Refusal> {
    doc.fonts_mut()
        .load_true_type_from_bytes(&provisioned.bytes, true)
        .map_err(|_| Refusal::NotEmbeddable)
}
