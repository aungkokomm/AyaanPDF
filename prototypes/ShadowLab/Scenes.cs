using PdfEditorApp.Viewport;

namespace ShadowLab;

/// <summary>One thing to draw, and what to call it on the sheet.</summary>
public sealed record Scene(string Name, IReadOnlyList<ShapeRenderItem> Items);

/// <summary>
/// The cases a drop shadow has to survive.
///
/// Built from REAL Ayaan geometry: every outline comes from ShapeGeometry, the
/// same call the app makes, and every mark is a production ShapeRenderItem.
///
/// The items are assembled here rather than through ShapeRenderList.From
/// because the app's shape model carries neither a fill nor a rotation: the
/// overlay draws a stroked outline and, for an arrow, a filled head, and that
/// is all it can express. A filled or turned shape reaches the screen only
/// through PDFium. Both are still worth putting in front of the filter, so
/// they are built directly, out of the same parts.
/// </summary>
public static class Scenes
{
    private static readonly RenderColor Ink = new(0xFF, 0x1F, 0x29, 0x37);
    private static readonly RenderColor Blue = new(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly RenderColor Amber = new(0xFF, 0xF5, 0x9E, 0x0B);

    private const double Width = 0.008;

    /// <summary>A shadow in the units the model uses: lengths as a fraction of
    /// the page width, opacity as the colour's alpha.</summary>
    public static DropShadow Shadow(
        double angle = 135, double distancePts = 10, double blurPts = 8,
        byte alpha = 0x80, double pageWpts = 612) =>
        new(angle, distancePts / pageWpts, new RenderColor(alpha, 0, 0, 0), blurPts / pageWpts);

    private static ShapeDraft Draft(ShapeKind kind, double x1, double y1, double x2, double y2) =>
        new(kind, x1, y1, x2, y2);

    private static ShapeRenderItem Mark(
        IReadOnlyList<(double X, double Y)> points, RenderColor color, RenderStyle style,
        DropShadow? shadow, Guid id, double width = Width) =>
        new(0, points, color, width, style, Effects: shadow is { } s ? new ShapeEffects(s) : null)
        {
            ObjectId = id,
        };

    /// <summary>A stroked outline, as the overlay draws every shape today.</summary>
    private static IReadOnlyList<ShapeRenderItem> Stroked(
        ShapeKind kind, DropShadow? shadow, double width = Width)
    {
        var d = Draft(kind, 0.22, 0.26, 0.68, 0.62);
        return [Mark(ShapeGeometry.Outline(d, width), Ink, RenderStyle.Stroked, shadow, Guid.NewGuid(), width)];
    }

    /// <summary>The same outline, filled, which is what the saved page shows.</summary>
    private static IReadOnlyList<ShapeRenderItem> Filled(
        ShapeKind kind, RenderColor color, DropShadow? shadow)
    {
        var d = Draft(kind, 0.22, 0.26, 0.68, 0.62);
        var id = Guid.NewGuid();
        return
        [
            Mark(ShapeGeometry.Outline(d, Width), color, RenderStyle.Filled, shadow, id),
            Mark(ShapeGeometry.Outline(d, Width), Ink, RenderStyle.Stroked, shadow, id),
        ];
    }

    private static IReadOnlyList<ShapeRenderItem> Arrow(DropShadow? shadow)
    {
        const double w = 0.016;
        var d = Draft(ShapeKind.Arrow, 0.18, 0.30, 0.74, 0.58);
        var id = Guid.NewGuid();

        // Shaft then head, both carrying the same object id, exactly as
        // ShapeRenderList.From emits an arrow.
        return
        [
            Mark(ShapeGeometry.Outline(d, w), Ink, RenderStyle.Stroked, shadow, id, w),
            Mark(ShapeGeometry.ArrowHeadTriangle(d, w), Ink, RenderStyle.Filled, shadow, id, w),
        ];
    }

    private static IReadOnlyList<ShapeRenderItem> Rotated(double degrees, DropShadow? shadow)
    {
        var d = Draft(ShapeKind.Rectangle, 0.22, 0.26, 0.68, 0.62);
        var id = Guid.NewGuid();
        var turned = Turn(ShapeGeometry.Outline(d, Width), degrees);

        return
        [
            Mark(turned, Blue, RenderStyle.Filled, shadow, id),
            Mark(turned, Ink, RenderStyle.Stroked, shadow, id),
        ];
    }

    /// <summary>The outline turned about its own centre, which is how a rotated
    /// shape reaches a renderer: as points, already turned.</summary>
    private static IReadOnlyList<(double X, double Y)> Turn(
        IReadOnlyList<(double X, double Y)> points, double degrees)
    {
        double cx = points.Average(p => p.X);
        double cy = points.Average(p => p.Y);
        double t = degrees * Math.PI / 180.0;
        double cos = Math.Cos(t), sin = Math.Sin(t);

        return points
            .Select(p => (
                X: cx + (((p.X - cx) * cos) - ((p.Y - cy) * sin)),
                Y: cy + (((p.X - cx) * sin) + ((p.Y - cy) * cos))))
            .ToList();
    }

    /// <summary>Two objects that overlap, so one object's shadow must not
    /// darken where it crosses the other's.</summary>
    private static IReadOnlyList<ShapeRenderItem> TwoObjects(DropShadow? shadow)
    {
        var a = Draft(ShapeKind.Rectangle, 0.14, 0.20, 0.54, 0.50);
        var b = Draft(ShapeKind.Ellipse, 0.36, 0.36, 0.80, 0.66);
        var ia = Guid.NewGuid();
        var ib = Guid.NewGuid();

        return
        [
            Mark(ShapeGeometry.Outline(a, Width), Blue, RenderStyle.Filled, shadow, ia),
            Mark(ShapeGeometry.Outline(a, Width), Ink, RenderStyle.Stroked, shadow, ia),
            Mark(ShapeGeometry.Outline(b, Width), Amber, RenderStyle.Filled, shadow, ib),
            Mark(ShapeGeometry.Outline(b, Width), Ink, RenderStyle.Stroked, shadow, ib),
        ];
    }

    public static IReadOnlyList<Scene> Grid(DropShadow shadow) =>
    [
        new("filled rectangle", Filled(ShapeKind.Rectangle, Blue, shadow)),
        new("stroke-only rectangle", Stroked(ShapeKind.Rectangle, shadow)),
        new("stroke-only ellipse", Stroked(ShapeKind.Ellipse, shadow)),
        new("filled rounded rect", Filled(ShapeKind.RoundedRectangle, Amber, shadow)),
        new("arrow, two marks", Arrow(shadow)),
        new("line", Stroked(ShapeKind.Line, shadow, 0.016)),
        new("rotated 30 degrees", Rotated(30, shadow)),
        new("two objects overlapping", TwoObjects(shadow)),
    ];
}
