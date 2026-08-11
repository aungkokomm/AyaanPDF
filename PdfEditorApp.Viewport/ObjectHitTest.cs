using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What lies under a point, asked of the DOCUMENT MODEL rather than of the
/// document.
///
/// The app has picked objects by their annotation's /Rect since annotations
/// became objects, and /Rect is not the object. It is the drag's extent grown by
/// the stroke pad, and for anything turned it is the axis-aligned box of the
/// rotated content. The visible consequence is a diagonal arrow that can be
/// selected anywhere inside a box it barely touches: click well away from the
/// arrow, in empty page, and the arrow is what comes back.
///
/// This module answers with the object's own geometry instead. It depends on
/// nothing but the model and <see cref="Geometry2D"/>: no PDFium, no WinUI, no
/// renderer of any kind. That is deliberate, and it is what lets the awkward
/// cases (a 45-degree arrow, an ellipse's corners, a rounded rectangle's square
/// corners) be pinned by tests instead of by clicking.
/// </summary>
public static class ObjectHitTest
{
    /// <summary>
    /// Default pick tolerance in normalized units, shared with
    /// <see cref="AnnotationHitTester.DefaultTolerance"/> so a mark made this
    /// session and the same mark after a reload are equally easy to click.
    /// Roughly a few pixels at a typical page width.
    /// </summary>
    public const double DefaultTolerance = AnnotationHitTester.DefaultTolerance;

    /// <summary>
    /// The topmost object under a normalized page-local point, or null.
    ///
    /// Searched BACK TO FRONT, because a page's objects are painted in list
    /// order, so the last one is on top. Picking the first match instead hands
    /// back whatever is underneath whenever two objects overlap, which is
    /// exactly the case where the user is being precise about which one they
    /// want.
    /// </summary>
    public static DocumentObject? PickTopmost(
        PageModel? page, double x, double y, double tolerance = DefaultTolerance)
    {
        if (page is null) { return null; }

        var objects = page.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (Hit(objects[i], x, y, tolerance)) { return objects[i]; }
        }

        return null;
    }

    /// <summary>
    /// Whether a normalized page-local point lies on this object.
    ///
    /// The point is moved into the object's own upright frame first, so every
    /// test below can be written as though nothing were rotated. Everything
    /// after that is a question about the shape the user can see.
    /// </summary>
    public static bool Hit(
        DocumentObject? o, double x, double y, double tolerance = DefaultTolerance)
    {
        if (o is null) { return false; }

        // Rotation is about the centre of /Rect, which is the one thing /Rect
        // reports exactly at any angle. render_core turns each object about that
        // same centre when it writes it.
        var (lx, ly) = Geometry2D.InverseRotate(
            x, y,
            (o.Bounds.Left + o.Bounds.Right) / 2,
            (o.Bounds.Top + o.Bounds.Bottom) / 2,
            o.RotationDeg);

        return o switch
        {
            ShapeObject s => HitShape(s, lx, ly, tolerance),
            OpaqueObject p => HitOpaque(p, lx, ly, tolerance),
            _ => Geometry2D.InRect(lx, ly, o.UprightBounds, tolerance),
        };
    }

    /// <summary>
    /// One of our shapes, tested as the thing it was drawn as.
    ///
    /// The split is between shapes that ENCLOSE an area and shapes that do not.
    /// A rectangle drawn round a paragraph is mostly empty, and requiring the
    /// user to trace its two-pixel outline to select it would be wrong: every
    /// drawing app treats a drawn shape as an object you grab anywhere on it.
    /// A line and an arrow enclose nothing at all, so the same rule applied to
    /// them is what swallows half a page.
    /// </summary>
    private static bool HitShape(ShapeObject s, double x, double y, double tolerance)
    {
        double reach = Math.Max(tolerance, Math.Max(StrokeWidthNorm(s), ShapeAnnotation.MinimumGrab));
        var g = s.UprightGeometry;

        switch (s.ShapeKind)
        {
            case ShapeKind.Line:
                return Geometry2D.DistanceToSegment(x, y, (g.X1, g.Y1), (g.X2, g.Y2)) <= reach;

            case ShapeKind.Arrow:
            {
                // The shaft STOPS at the base of the head, exactly as it is
                // drawn: a stroked line running on under a filled triangle would
                // poke out past the point. Both parts are tested because both
                // parts are the arrow.
                var a = ShapeGeometry.Arrow(g.X1, g.Y1, g.X2, g.Y2, StrokeWidthNorm(s));
                return Geometry2D.DistanceToSegment(x, y, a.ShaftStart, a.ShaftEnd) <= reach
                    || Geometry2D.InTriangle(x, y, a.Tip, a.Left, a.Right, reach);
            }

            case ShapeKind.Ellipse:
                return Geometry2D.InEllipse(x, y, s.UprightBounds, reach);

            case ShapeKind.RoundedRectangle:
                return Geometry2D.InRoundedRect(x, y, s.UprightBounds, CornerRadiusNorm(s), reach);

            default:
                return Geometry2D.InRect(x, y, s.UprightBounds, reach);
        }
    }

    /// <summary>
    /// An object the model records but does not describe.
    ///
    /// Ink is the one that has real geometry to test: its points are on its tag,
    /// and a diagonal scribble fills almost none of its own box. Everything else
    /// is a rectangle, tested against its UPRIGHT box so a turned text box or
    /// signature is not selectable across the enlarged rectangle rotation gave
    /// its annotation.
    /// </summary>
    private static bool HitOpaque(OpaqueObject o, double x, double y, double tolerance)
    {
        if (o.Kind == DocumentObjectKind.Ink && o.InkPoints.Count > 0)
        {
            return Geometry2D.DistanceToPolyline(x, y, o.InkPoints)
                   <= Math.Max(tolerance, o.InkStrokeWidth);
        }

        return Geometry2D.InRect(x, y, o.UprightBounds, tolerance);
    }

    /// <summary>
    /// The shape's stroke width in normalized units, or 0 when the page width is
    /// unknown and the two cannot be related. A zero here only means the reach
    /// falls back to the tolerance floor, never that the shape becomes unhittable.
    /// </summary>
    private static double StrokeWidthNorm(ShapeObject s) =>
        s.PageWidthPts > 0 ? s.StrokeWidthPts / s.PageWidthPts : 0;

    /// <summary>The corner radius in normalized units, on the same terms.</summary>
    private static double CornerRadiusNorm(ShapeObject s) =>
        s.PageWidthPts > 0 ? s.CornerRadiusPts / s.PageWidthPts : 0;
}
