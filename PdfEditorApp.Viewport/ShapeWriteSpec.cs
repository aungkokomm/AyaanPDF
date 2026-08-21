using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Everything render_core needs to draw one shape, in capture-space pixels.
/// Mirrors render_core::ShapeSpec field for field, minus the page index and
/// the colour bytes the caller unpacks.
///
/// A pure record so the DECISION of what to write can be tested. A rounded
/// rectangle shipped square because one of the app's three write sites built
/// this by hand and left the radius at zero; the value was correct everywhere
/// it could be tested and wrong in the one place that mattered.
/// </summary>
public readonly record struct ShapeWriteSpec(
    ShapeKind Kind,
    float X1,
    float Y1,
    float X2,
    float Y2,
    float StrokeWidthPx,
    float RotationDeg,
    float CornerRadiusPx);

/// <summary>
/// A shape's appearance, as the core takes it: the colour bytes the caller
/// unpacks, plus the two features that are a single value meaning "off" when
/// zero.
///
/// Separate from <see cref="ShapeWriteSpec"/> rather than folded into it,
/// because that record deliberately mirrors the geometry render_core needs and
/// is built by a path that has no tag to read. Composing the two keeps one
/// definition of the geometry instead of a second copy that can drift.
/// </summary>
public readonly record struct ShapeStyleSpec(
    byte A,
    byte R,
    byte G,
    byte B,
    uint FillRgba,
    float ShadowAngleDeg,
    float ShadowDistancePx,
    float ShadowSoftnessPx,
    float ShadowSpreadPx,
    uint ShadowRgba);

/// <summary>Everything needed to write a shape rebuilt from its own tag.</summary>
public readonly record struct ShapeRewriteSpec(ShapeWriteSpec Geometry, ShapeStyleSpec Style);

/// <summary>
/// Builds the spec for a shape about to be written to the document.
///
/// One function, used by every path that writes a NEW shape, so a field cannot
/// be remembered in one place and forgotten in another.
/// </summary>
public static class ShapeWriter
{
    /// <summary>
    /// The spec for a shape the user has just drawn.
    ///
    /// Geometry and corner radius both come from the DRAFT, which is also what
    /// the live preview draws from, so the shape written to the page is the
    /// shape that was on screen when the pointer lifted.
    /// </summary>
    public static ShapeWriteSpec ForNewShape(ShapeDraft draft, double strokeWidthNorm, int captureWidth)
    {
        return new ShapeWriteSpec(
            Kind: draft.Kind,
            X1: (float)(draft.X1 * captureWidth),
            Y1: (float)(draft.Y1 * captureWidth),
            X2: (float)(draft.X2 * captureWidth),
            Y2: (float)(draft.Y2 * captureWidth),
            StrokeWidthPx: (float)(strokeWidthNorm * captureWidth),
            RotationDeg: 0f,
            CornerRadiusPx: (float)(draft.CornerRadius * captureWidth));
    }

    /// <summary>
    /// The spec for a shape being rebuilt from its OWN tag, at a given
    /// rectangle: what pasting, duplicating and undoing a delete all need.
    ///
    /// It exists because the view model grew its own hand-rolled copy of the
    /// tag parser, which reads the first eight fields and silently drops the
    /// rest. That has now cost three separate bugs of one kind: a duplicate
    /// losing its fill, a duplicate losing its corner radius, and a duplicate
    /// losing its drop shadow. Each was found by a person, not a test, because
    /// the copy lives in a WinUI class no test assembly can load.
    ///
    /// So this reads through <see cref="ShapeTagReader"/>, the one parser that
    /// is already canonical and already tested, and a field appended to the tag
    /// arrives here without anybody remembering to thread it.
    ///
    /// TWO DIFFERENCES FROM THE COPY IT IS MEANT TO REPLACE, both deliberate
    /// and both worth knowing before the call sites move over:
    ///
    /// <list type="bullet">
    /// <item>MORE PERMISSIVE about the identity prefix. A tag may arrive as
    /// "ID:&lt;32 hex&gt;|AyaanShape:..." and the reader strips it; the copy
    /// tests StartsWith("AyaanShape:") and rejects the prefixed form.</item>
    /// <item>STRICTER about a malformed tag. The reader requires the kind to be
    /// a shape kind that exists and the stroke width to be finite and positive;
    /// the copy accepts any integer and any parseable width, including zero.
    /// render_core rejects both of those on the way in, so nothing the writer
    /// produces is affected.</item>
    /// </list>
    ///
    /// POINTS ARE USED AS PIXELS, deliberately and exactly as the existing code
    /// does it. The tag stores lengths in points and the core wants capture
    /// pixels, and the page width that converts between them is not available
    /// at these call sites. Correcting it here would change what a duplicate
    /// looks like, which is a different change with a different risk; this one
    /// only moves the reading.
    /// </summary>
    /// <param name="left">The rectangle to rebuild into, in normalized units.</param>
    public static bool TryForExistingShape(
        string? contents,
        double left,
        double top,
        double right,
        double bottom,
        int captureWidth,
        out ShapeRewriteSpec spec)
    {
        spec = default;

        if (!ShapeTagReader.TryParse(contents, out var tag))
        {
            return false;
        }

        // The stored corner flags put the drag back the way round it was drawn,
        // so an arrow rebuilt from its tag keeps its head on the same end.
        double x1 = tag.FlipX ? left : right;
        double x2 = tag.FlipX ? right : left;
        double y1 = tag.FlipY ? top : bottom;
        double y2 = tag.FlipY ? bottom : top;

        var (a, r, g, b) = InkPresets.ParseHex(tag.StrokeHex);

        spec = new ShapeRewriteSpec(
            new ShapeWriteSpec(
                Kind: tag.Kind,
                X1: (float)(x1 * captureWidth),
                Y1: (float)(y1 * captureWidth),
                X2: (float)(x2 * captureWidth),
                Y2: (float)(y2 * captureWidth),
                StrokeWidthPx: (float)tag.StrokeWidthPts,
                RotationDeg: (float)tag.RotationDeg,
                CornerRadiusPx: (float)tag.CornerRadiusPts),
            new ShapeStyleSpec(
                A: a, R: r, G: g, B: b,
                FillRgba: RgbaOf(tag.FillHex),
                ShadowAngleDeg: (float)tag.ShadowAngleDeg,
                ShadowDistancePx: (float)tag.ShadowDistancePts,
                ShadowSoftnessPx: (float)tag.ShadowSoftnessPts,
                ShadowSpreadPx: (float)tag.ShadowSpreadPts,
                ShadowRgba: RgbaOf(tag.ShadowHex)));

        return true;
    }

    /// <summary>
    /// An "#AARRGGBB" string as the 0xAARRGGBB the core takes, or 0 for absent.
    /// Zero is how both the fill and the shadow say "this feature is off".
    /// </summary>
    private static uint RgbaOf(string? hex) =>
        hex is null ? 0u : Convert.ToUInt32(hex.TrimStart('#'), 16);
}
