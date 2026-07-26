using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The shapes the shape tool can draw.
///
/// These numbers cross the FFI boundary to render_core's SHAPE_* constants, so
/// they may be appended to but never reordered.
/// </summary>
public enum ShapeKind
{
    Rectangle = 0,
    Ellipse = 1,
    Line = 2,
    Arrow = 3,
}

/// <summary>
/// A shape as drawn, in normalized page coordinates: the drag's START and END,
/// not a rectangle.
///
/// Direction is part of the shape. An arrow drawn right to left points left,
/// and normalizing to a box on the way in would throw that away with no way to
/// recover it.
/// </summary>
public readonly record struct ShapeDraft(
    ShapeKind Kind, double X1, double Y1, double X2, double Y2)
{
    public double Left => Math.Min(X1, X2);
    public double Top => Math.Min(Y1, Y2);
    public double Right => Math.Max(X1, X2);
    public double Bottom => Math.Max(Y1, Y2);

    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>
/// A drawn shape, as an annotation on a page.
///
/// Keeps the DRAFT rather than the resulting points, so the shape stays a
/// rectangle or an arrow rather than becoming an anonymous polyline. That is
/// what lets it be redrawn at a new size later, and what a bare point list
/// cannot express.
/// </summary>
public sealed record ShapeAnnotation(
    int PageIndex, ShapeDraft Draft, string ColorHex, double StrokeWidth) : IAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The polyline this shape draws as, including any arrowhead.</summary>
    public IReadOnlyList<(double X, double Y)> Outline =>
        ShapeGeometry.Outline(Draft, StrokeWidth);

    /// <summary>
    /// Hit-testing and bounds both defer to an ink stroke over this shape's own
    /// outline, rather than reimplementing segment distance. An ellipse or an
    /// arrow fills very little of its bounding box, so a box test would grab
    /// clicks in empty space, and the segment maths is already tested.
    /// </summary>
    private InkStrokeAnnotation AsStroke =>
        new(PageIndex, Outline, ColorHex, StrokeWidth);

    public TextRect Bounds => AsStroke.Bounds;

    public bool HitTest(double x, double y, double tolerance) =>
        AsStroke.HitTest(x, y, tolerance);

    public IAnnotation Translate(double dx, double dy) => this with
    {
        Draft = Draft with
        {
            X1 = Draft.X1 + dx,
            Y1 = Draft.Y1 + dy,
            X2 = Draft.X2 + dx,
            Y2 = Draft.Y2 + dy,
        },
    };
}

/// <summary>
/// Shape maths shared by the live preview and the annotation writer.
///
/// It lives here, in the viewport library, because the last three geometry bugs
/// in this app were all in code buried in the view model where no test could
/// reach it: the highlight quads wound the wrong way, annotation picking ran
/// front to back, and stamps were placed from the wrong corner. Every one of
/// them was invisible to a green build.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>Spread of an arrow's barbs from its shaft, in radians (about 26°).</summary>
    public const double ArrowHeadAngle = 0.45;

    /// <summary>Barb length as a multiple of stroke width.</summary>
    public const double ArrowHeadScale = 6.0;

    /// <summary>
    /// Shortest barb, in normalized units, so the finest pen still draws a head
    /// rather than a dot. Mirrors ARROW_HEAD_MIN in render_core, converted from
    /// points at the 800-unit slot width the app captures at.
    /// </summary>
    public const double ArrowHeadMin = 6.0 / 800.0;

    /// <summary>
    /// A drag this short is a click, not a shape. Without a floor, a stray
    /// click while the tool is armed drops a zero-sized mark on the page that
    /// is then almost impossible to see or select in order to delete.
    /// </summary>
    public const double MinDragLength = 0.004;

    public static bool IsWorthDrawing(ShapeDraft s)
    {
        double dx = s.X2 - s.X1;
        double dy = s.Y2 - s.Y1;

        // Distance for line and arrow, since a diagonal drag has extent even
        // when neither side alone does; extent for the boxed kinds, so a
        // deliberately flat rectangle still counts.
        return s.Kind is ShapeKind.Line or ShapeKind.Arrow
            ? Math.Sqrt((dx * dx) + (dy * dy)) >= MinDragLength
            : Math.Max(Math.Abs(dx), Math.Abs(dy)) >= MinDragLength;
    }

    /// <summary>
    /// The two barb endpoints of an arrowhead at (<paramref name="tipX"/>,
    /// <paramref name="tipY"/>), for a shaft coming from
    /// (<paramref name="tailX"/>, <paramref name="tailY"/>).
    ///
    /// Deliberately mirrors <c>arrow_head</c> in render_core: the preview the
    /// user drags and the arrow written to the file have to be the same shape,
    /// or committing a stroke visibly changes it.
    /// </summary>
    public static ((double X, double Y) Left, (double X, double Y) Right) ArrowHead(
        double tailX, double tailY, double tipX, double tipY, double width)
    {
        double dx = tipX - tailX;
        double dy = tipY - tailY;
        double len = Math.Sqrt((dx * dx) + (dy * dy));

        // A zero-length shaft has no direction. Pick one rather than dividing
        // by zero and producing NaN coordinates.
        double ux = len < 1e-9 ? 1.0 : dx / len;
        double uy = len < 1e-9 ? 0.0 : dy / len;

        double barb = Math.Max(width * ArrowHeadScale, ArrowHeadMin);
        double sin = Math.Sin(ArrowHeadAngle);
        double cos = Math.Cos(ArrowHeadAngle);

        // Rotate the REVERSED shaft direction by plus and minus the head angle,
        // so the barbs sweep back from the tip.
        double bx = -ux * barb;
        double by = -uy * barb;

        return (
            (tipX + (bx * cos) - (by * sin), tipY + (bx * sin) + (by * cos)),
            (tipX + (bx * cos) + (by * sin), tipY - (bx * sin) + (by * cos)));
    }

    /// <summary>
    /// The polyline that previews a shape, in normalized coordinates.
    ///
    /// One list for every kind, including the boxed ones, so the preview layer
    /// draws shapes exactly the way it already draws ink and needs no new
    /// element type per shape.
    /// </summary>
    /// <param name="ellipseSegments">
    /// Straight segments approximating an ellipse. 48 is smooth at any zoom
    /// this app reaches and stays cheap to rebuild on every pointer move.
    /// </param>
    public static IReadOnlyList<(double X, double Y)> Outline(
        ShapeDraft s, double width, int ellipseSegments = 48)
    {
        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
                return
                [
                    (s.Left, s.Top),
                    (s.Right, s.Top),
                    (s.Right, s.Bottom),
                    (s.Left, s.Bottom),
                    (s.Left, s.Top),
                ];

            case ShapeKind.Ellipse:
            {
                double cx = (s.Left + s.Right) / 2;
                double cy = (s.Top + s.Bottom) / 2;
                double rx = s.Width / 2;
                double ry = s.Height / 2;

                var points = new List<(double, double)>(ellipseSegments + 1);
                for (int i = 0; i <= ellipseSegments; i++)
                {
                    double t = 2 * Math.PI * i / ellipseSegments;
                    points.Add((cx + (rx * Math.Cos(t)), cy + (ry * Math.Sin(t))));
                }

                return points;
            }

            case ShapeKind.Arrow:
            {
                var (left, right) = ArrowHead(s.X1, s.Y1, s.X2, s.Y2, width);

                // Same single polyline the file gets: tip, barb, back to the
                // tip, other barb. Retracing costs nothing on a stroke.
                return
                [
                    (s.X1, s.Y1),
                    (s.X2, s.Y2),
                    (left.X, left.Y),
                    (s.X2, s.Y2),
                    (right.X, right.Y),
                ];
            }

            default:
                return [(s.X1, s.Y1), (s.X2, s.Y2)];
        }
    }
}
