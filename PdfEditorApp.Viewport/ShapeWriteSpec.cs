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
}
