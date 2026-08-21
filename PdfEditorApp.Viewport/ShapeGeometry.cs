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
    RoundedRectangle = 4,
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

    /// <summary>
    /// The corner radius this shape draws with, in the same normalized units,
    /// or zero for a kind that has no corners.
    ///
    /// Defined HERE, on the draft, rather than at each place that needs it. The
    /// live preview and the annotation writer must derive the same radius from
    /// the same box or the shape changes the instant the pointer lifts, and
    /// that is exactly what happened when the two write paths were each left to
    /// work it out for themselves: one of them simply did not, and every
    /// rounded rectangle came out square.
    /// </summary>
    public double CornerRadius => Kind == ShapeKind.RoundedRectangle
        ? ShapeGeometry.CornerRadiusFromFraction(CornerFraction, Width, Height)
        : 0;

    /// <summary>
    /// How round the corners are, as a fraction of the maximum: 0 square, 1
    /// fully rounded. Carried on the DRAFT so the shape the preview traces and
    /// the shape written to the page are the same one, whatever the corner
    /// slider was set to when the drag began.
    ///
    /// Note that <c>default(ShapeDraft)</c> gets 0, not this default. That is
    /// deliberate: a zero-initialised draft is not a real shape, and square
    /// corners are the safe reading of one.
    /// </summary>
    public double CornerFraction { get; init; } = ShapeGeometry.DefaultCornerFraction;
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

    /// <summary>
    /// Effects painted with this shape, or null for none, which is the default
    /// and what every shape read from a file has.
    ///
    /// An init-only property alongside <see cref="Id"/> rather than a positional
    /// parameter, so the record's constructor is unchanged and every existing
    /// call site still compiles and still means the same thing.
    ///
    /// PERSISTED, as one self-describing field on the shape's tag. Nothing
    /// reflects over this record: the tag is formatted field by field in
    /// render_core and read back by ShapeTagReader, so what survives a save is
    /// exactly what those two agree on. See ShapeEffectsTag for the conversion.
    /// </summary>
    public ShapeEffects? Effects { get; init; }

    /// <summary>The polyline this shape draws as, including any arrowhead.</summary>
    public IReadOnlyList<(double X, double Y)> Outline =>
        ShapeGeometry.Outline(Draft, StrokeWidth);

    /// <summary>The filled head, for an arrow; empty for everything else.</summary>
    public IReadOnlyList<(double X, double Y)> Head =>
        Draft.Kind == ShapeKind.Arrow
            ? ShapeGeometry.ArrowHeadTriangle(Draft, StrokeWidth)
            : [];

    private InkStrokeAnnotation AsStroke =>
        new(PageIndex, Outline, ColorHex, StrokeWidth);

    /// <summary>
    /// Covers the head as well as the shaft. The outline is only the shaft now
    /// an arrow's head is a separate filled triangle, and bounds that stopped
    /// there would clip the point off in the saved file and make the tip
    /// unselectable on screen.
    /// </summary>
    public TextRect Bounds
    {
        get
        {
            var b = AsStroke.Bounds;
            foreach (var (x, y) in Head)
            {
                b = new TextRect(
                    Math.Min(b.Left, x), Math.Min(b.Top, y),
                    Math.Max(b.Right, x), Math.Max(b.Bottom, y));
            }

            return b;
        }
    }

    /// <summary>
    /// A shape is an OBJECT, so anywhere on or inside it picks it up.
    ///
    /// This began as distance-to-the-outline, the same test ink uses, and that
    /// is wrong here: a rectangle drawn round a paragraph is mostly empty, so
    /// selecting it meant hitting a two-pixel line exactly. A drawn shape is a
    /// thing you grab, not a curve you must trace, and every drawing app treats
    /// it that way.
    ///
    /// Lines and arrows keep the distance test, generously padded, because they
    /// enclose nothing: a box test on a long diagonal arrow would swallow half
    /// the page.
    /// </summary>
    public bool HitTest(double x, double y, double tolerance)
    {
        double reach = Math.Max(tolerance, Math.Max(StrokeWidth, MinimumGrab));

        if (Draft.Kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            if (AsStroke.HitTest(x, y, reach))
            {
                return true;
            }

            // The head is a solid triangle, and it is the fattest, most
            // obvious part of an arrow: clicking the point has to work.
            var head = Head;
            if (head.Count == 3)
            {
                var t = new InkStrokeAnnotation(
                    PageIndex, [head[0], head[1], head[2], head[0]], ColorHex, StrokeWidth);
                return t.HitTest(x, y, reach);
            }

            return false;
        }

        var b = Bounds;
        return x >= b.Left - reach && x <= b.Right + reach
            && y >= b.Top - reach && y <= b.Bottom + reach;
    }

    /// <summary>
    /// How close counts as a hit, at minimum, in normalized page units: about
    /// five slot pixels. A hairline shape is still a target the pointer can be
    /// expected to find without precision aiming.
    /// </summary>
    public const double MinimumGrab = 0.006;

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
    /// <summary>
    /// A rounded rectangle's default corner radius, as a fraction of its SHORTER
    /// side. Proportional rather than absolute so a small badge and a full-page
    /// box read as the same shape family. Mirrors ROUNDED_RECT_DEFAULT_RADIUS in
    /// render_core: the preview and the written annotation must derive the same
    /// radius from the same box, or the shape would change the instant the
    /// pointer lifts.
    /// </summary>
    public const double RoundedRectDefaultRadius = 0.18;

    /// <summary>
    /// The largest radius a box this size can draw: half its shorter side, the
    /// point at which the shape becomes a stadium. Anything beyond makes the
    /// corner arcs cross.
    /// </summary>
    public static double MaxCornerRadius(double width, double height) =>
        Math.Min(Math.Abs(width), Math.Abs(height)) / 2;

    /// <summary>
    /// The default as a FRACTION of the maximum, which is what the corner-radius
    /// slider works in: 0 is square, 1 is fully rounded. Expressed this way so
    /// the control means the same thing on any size of box, and so a shape keeps
    /// looking right when it is resized.
    /// </summary>
    public const double DefaultCornerFraction = RoundedRectDefaultRadius * 2;

    /// <summary>Radius for a fraction of the maximum, in the box's own units.
    /// Out-of-range fractions are pulled back to 0..1 rather than rejected: a
    /// slider is a blunt instrument and its ends should be the shape's ends.</summary>
    public static double CornerRadiusFromFraction(double fraction, double width, double height)
    {
        if (double.IsNaN(fraction)) { return 0; }
        return Math.Clamp(fraction, 0, 1) * MaxCornerRadius(width, height);
    }

    /// <summary>The fraction of maximum that this radius represents, for putting
    /// the slider where a selected shape actually is. Zero-size boxes report 0
    /// rather than dividing by zero.</summary>
    public static double CornerFractionFromRadius(double radius, double width, double height)
    {
        double max = MaxCornerRadius(width, height);
        if (max <= 0 || double.IsNaN(radius)) { return 0; }
        return Math.Clamp(radius / max, 0, 1);
    }

    /// <summary>The default radius for a box of this size, in the same units.</summary>
    public static double DefaultCornerRadius(double width, double height) =>
        CornerRadiusFromFraction(DefaultCornerFraction, width, height);

    /// <summary>
    /// The radius that can actually be drawn in a box this size.
    ///
    /// Half the shorter side is the ceiling: past it the two corner arcs on a
    /// side meet and then cross, which draws as a bow tie rather than a stadium.
    /// Mirrors clamped_corner_radius in render_core, which is what the PDF is
    /// actually drawn with; this copy exists so the on-screen preview agrees.
    /// </summary>
    public static double ClampCornerRadius(double radius, double width, double height)
    {
        if (double.IsNaN(radius) || radius <= 0) { return 0; }
        double limit = Math.Min(Math.Abs(width), Math.Abs(height)) / 2;
        return Math.Min(radius, limit);
    }

    /// <summary>Spread of an arrow's barbs from its shaft, in radians (about 26°).</summary>
    public const double ArrowHeadAngle = 0.45;

    /// <summary>Head length as a multiple of stroke width.</summary>
    public const double ArrowHeadScale = 6.0;

    /// <summary>
    /// Half-width of the head as a fraction of its length, giving the roughly
    /// 5:2 taper a drawn arrow is expected to have. Two thin barbs at an angle
    /// read as a tick or a bird, not as an arrow; a solid triangle is what
    /// every drawing tool draws and what this now produces.
    /// </summary>
    public const double ArrowHeadTaper = 0.42;

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
    /// An arrow, split into the parts that are drawn differently: a stroked
    /// shaft and a FILLED triangular head.
    ///
    /// The head used to be two thin barbs drawn as part of the shaft's own
    /// polyline. That is not what an arrow looks like: at any real stroke width
    /// it reads as a tick mark. Every drawing tool fills the head, so this
    /// returns a triangle to fill.
    ///
    /// The shaft also STOPS at the base of the head rather than running to the
    /// tip. A stroked line continuing under a filled triangle pokes out past
    /// the point at anything but a hairline width, and blunts it.
    /// </summary>
    public static (
        (double X, double Y) ShaftStart,
        (double X, double Y) ShaftEnd,
        (double X, double Y) Tip,
        (double X, double Y) Left,
        (double X, double Y) Right) Arrow(
        double tailX, double tailY, double tipX, double tipY, double width)
    {
        double dx = tipX - tailX;
        double dy = tipY - tailY;
        double len = Math.Sqrt((dx * dx) + (dy * dy));

        // A zero-length shaft has no direction. Pick one rather than dividing
        // by zero and producing NaN coordinates.
        double ux = len < 1e-9 ? 1.0 : dx / len;
        double uy = len < 1e-9 ? 0.0 : dy / len;

        double head = Math.Max(width * ArrowHeadScale, ArrowHeadMin);

        // Never let the head eat the whole arrow. On a short drag an unclamped
        // head is longer than the shaft, and the result is a floating triangle
        // pointing backwards.
        head = Math.Min(head, len * 0.6);

        double half = head * ArrowHeadTaper;

        // Base of the head, and the perpendicular the barbs sit on.
        double bx = tipX - (ux * head);
        double by = tipY - (uy * head);
        double px = -uy * half;
        double py = ux * half;

        return (
            (tailX, tailY),
            (bx, by),
            (tipX, tipY),
            (bx + px, by + py),
            (bx - px, by - py));
    }

    /// <summary>The three points of an arrow's filled head.</summary>
    public static IReadOnlyList<(double X, double Y)> ArrowHeadTriangle(ShapeDraft s, double width)
    {
        var a = Arrow(s.X1, s.Y1, s.X2, s.Y2, width);
        return [a.Tip, a.Left, a.Right];
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

            case ShapeKind.RoundedRectangle:
            {
                double r = s.CornerRadius;
                if (r <= 0)
                {
                    // Too thin to round. Preview it as the rectangle it will be
                    // written as, rather than as nothing.
                    goto case ShapeKind.Rectangle;
                }

                // A quarter of the ellipse budget per corner, so a rounded rect
                // and an ellipse are equally smooth at the same zoom.
                int perCorner = Math.Max(2, ellipseSegments / 4);
                var pts = new List<(double, double)>((perCorner + 1) * 4 + 1);

                // Centre of each corner's arc, and the angle its sweep starts
                // at, going clockwise from the top-left in SCREEN coordinates
                // (y down), which is the order the outline is drawn in.
                (double Cx, double Cy, double Start)[] corners =
                [
                    (s.Left + r,  s.Top + r,     Math.PI),
                    (s.Right - r, s.Top + r,     -Math.PI / 2),
                    (s.Right - r, s.Bottom - r,  0),
                    (s.Left + r,  s.Bottom - r,  Math.PI / 2),
                ];

                foreach (var (cx0, cy0, start) in corners)
                {
                    for (int i = 0; i <= perCorner; i++)
                    {
                        double t = start + (Math.PI / 2 * i / perCorner);
                        pts.Add((cx0 + (r * Math.Cos(t)), cy0 + (r * Math.Sin(t))));
                    }
                }

                pts.Add(pts[0]);
                return pts;
            }

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

                // The ring closes EXACTLY. cos(2 pi) is not quite cos(0) in
                // floating point, so the last point came back a hair off the
                // first, and whether an outline returns to where it started is
                // how the renderer decides it encloses an area at all.
                points[^1] = points[0];

                return points;
            }

            case ShapeKind.Arrow:
            {
                // The SHAFT only. The head is a filled triangle and is fetched
                // separately, by ArrowHeadTriangle, because a stroked polyline
                // cannot express a solid one.
                var a = Arrow(s.X1, s.Y1, s.X2, s.Y2, width);
                return [a.ShaftStart, a.ShaftEnd];
            }

            default:
                return [(s.X1, s.Y1), (s.X2, s.Y2)];
        }
    }
}
