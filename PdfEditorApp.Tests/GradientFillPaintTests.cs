using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A gradient as REAL VECTOR PAINT on the live surface.
///
/// Not a picture of a gradient. A Skia shader on the shape's own path, so it is
/// clipped by the path rather than by a rectangle, and it is as sharp at any
/// zoom as the outline round it.
///
/// THE COORDINATE MODEL IS WHAT THESE ACTUALLY TEST. A gradient's endpoints are
/// fractions of the shape's upright box, resolved into normalized page-local
/// points beside the mark's own points and then projected by the same rule. Move,
/// resize, page turn and zoom are therefore not four features: they are one
/// consequence, and each of the tests below is a different way of asking whether
/// that consequence really holds.
///
/// The fixture is the painter's own: a rectangle whose every edge lands on a
/// whole DIP, normalized (0.1, 0.1) to (0.4, 0.3) at a scale of 800, which is
/// slot (80, 80) to (320, 240).
/// </summary>
public class GradientFillPaintTests
{
    private const double Scale = 800;
    private const double StrokeWidthNorm = 0.004;
    private const double ExpectedThicknessDips = 3.2;

    private const int Left = 80, Top = 80, Right = 320, Bottom = 240;

    private static readonly RenderColor Red = new(0xFF, 0xFF, 0x00, 0x00);
    private static readonly RenderColor Blue = new(0xFF, 0x00, 0x00, 0xFF);
    private static readonly RenderColor White = new(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>Left to right across the shape's own box, red to blue.</summary>
    private static GradientFill Across => new(Red, Blue, 0, 0.5, 1, 0.5);

    /// <summary>Top to bottom, the same two colours.</summary>
    private static GradientFill Down => new(Red, Blue, 0.5, 0, 0.5, 1);

    /// <summary>Corner to corner, so neither axis alone can explain it.</summary>
    private static GradientFill Corners => new(Red, Blue, 0, 0, 1, 1);

    private static ShapeAnnotation Shape(
        GradientFill? gradient,
        ShapeKind kind = ShapeKind.Rectangle,
        double x2 = 0.4,
        string strokeHex = "#FF000000",
        double cornerFraction = ShapeGeometry.DefaultCornerFraction,
        ShapeEffects? effects = null) =>
        new(0,
            new ShapeDraft(kind, 0.1, 0.1, x2, 0.3) { CornerFraction = cornerFraction },
            strokeHex,
            StrokeWidthNorm)
        {
            Fill = gradient is { } g ? ShapeFill.Of(g) : ShapeFill.None,
            Effects = effects,
        };

    private static IReadOnlyList<ShapeRenderItem> Items(ShapeAnnotation shape) =>
        ShapeRenderList.From([], [shape]);

    private static PageTransform Flat(int page) => PageTransform.For(1, 1, 0, 1);

    private static SKBitmap Render(
        IReadOnlyList<ShapeRenderItem> items,
        double pageTop = 0,
        Func<int, PageTransform>? view = null,
        int w = 400,
        int h = 400)
    {
        var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.Paint(canvas, items, Scale, _ => pageTop, view ?? Flat);
        return bitmap;
    }

    private static SKBitmap Render(
        ShapeAnnotation shape,
        double pageTop = 0,
        Func<int, PageTransform>? view = null,
        int w = 400,
        int h = 400) =>
        Render(Items(shape), pageTop, view, w, h);

    private static bool IsPaper(SKColor c) => c.Red == 255 && c.Green == 255 && c.Blue == 255;

    /// <summary>
    /// How far along the red-to-blue ramp a pixel is, from 0 at the red end to
    /// 1 at the blue end. Reading one number rather than three channels is what
    /// makes a direction assertion legible.
    /// </summary>
    private static double Ramp(SKColor c) => c.Blue / 255.0;

    // ---------------- the direction it runs ----------------

    [Fact]
    public void a_horizontal_gradient_runs_from_one_side_to_the_other()
    {
        using var bitmap = Render(Shape(Across));

        int y = (Top + Bottom) / 2;

        Assert.InRange(Ramp(bitmap.GetPixel(Left + 4, y)), 0.0, 0.06);
        Assert.InRange(Ramp(bitmap.GetPixel((Left + Right) / 2, y)), 0.45, 0.55);
        Assert.InRange(Ramp(bitmap.GetPixel(Right - 4, y)), 0.94, 1.0);

        // And it only ever goes one way. A shader placed at the wrong two
        // points can still be red at one end and blue at the other.
        double last = -1;
        for (int x = Left + 4; x <= Right - 4; x += 4)
        {
            double now = Ramp(bitmap.GetPixel(x, y));
            Assert.True(now >= last - 0.001, $"the ramp went backwards at x={x}");
            last = now;
        }
    }

    [Fact]
    public void a_vertical_gradient_runs_down_the_shape()
    {
        using var bitmap = Render(Shape(Down));

        int x = (Left + Right) / 2;

        Assert.InRange(Ramp(bitmap.GetPixel(x, Top + 4)), 0.0, 0.06);
        Assert.InRange(Ramp(bitmap.GetPixel(x, Bottom - 4)), 0.94, 1.0);

        // Across it, nothing changes. A vertical gradient that also varied
        // sideways would be at some other angle.
        Assert.Equal(
            Ramp(bitmap.GetPixel(Left + 6, (Top + Bottom) / 2)),
            Ramp(bitmap.GetPixel(Right - 6, (Top + Bottom) / 2)),
            2);
    }

    [Fact]
    public void a_gradient_at_an_angle_is_constant_along_its_own_perpendicular()
    {
        // THE REAL TEST OF AN ANGLE. Corner to corner, on a box that is wider
        // than it is tall, so the axis is not 45 degrees and the two opposite
        // corners are NOT the same colour. What is equal is any two points that
        // project to the same place along the axis: (160,220) and (240,100)
        // both sit halfway, and a gradient at any other angle separates them.
        using var bitmap = Render(Shape(Corners));

        Assert.Equal(Ramp(bitmap.GetPixel(160, 220)), Ramp(bitmap.GetPixel(240, 100)), 2);
        Assert.InRange(Ramp(bitmap.GetPixel(160, 220)), 0.45, 0.55);

        Assert.InRange(Ramp(bitmap.GetPixel(Left + 5, Top + 5)), 0.0, 0.08);
        Assert.InRange(Ramp(bitmap.GetPixel(Right - 5, Bottom - 5)), 0.92, 1.0);
    }

    // ---------------- and what it is clipped by ----------------

    [Fact]
    public void a_gradient_is_clipped_by_a_rectangle()
    {
        using var bitmap = Render(Shape(Across));

        Assert.True(IsPaper(bitmap.GetPixel(Left - 4, (Top + Bottom) / 2)), "outside the left edge");
        Assert.True(IsPaper(bitmap.GetPixel(Right + 4, (Top + Bottom) / 2)), "outside the right edge");
        Assert.True(IsPaper(bitmap.GetPixel((Left + Right) / 2, Top - 4)), "above the top edge");
    }

    [Fact]
    public void a_gradient_is_clipped_by_an_ellipse_and_not_by_its_box()
    {
        using var bitmap = Render(Shape(Across, ShapeKind.Ellipse));

        Assert.InRange(Ramp(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)), 0.45, 0.55);
        Assert.True(
            IsPaper(bitmap.GetPixel(Left + 4, Top + 4)),
            "the gradient spilled into the corner of the ellipse's box");
    }

    [Fact]
    public void a_gradient_is_clipped_by_a_rounded_rectangle_and_not_by_its_box()
    {
        using var bitmap = Render(
            Shape(Across, ShapeKind.RoundedRectangle, cornerFraction: 1));

        Assert.InRange(Ramp(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)), 0.45, 0.55);
        Assert.True(
            IsPaper(bitmap.GetPixel(Left + 4, Top + 4)),
            "the gradient spilled into the corner of the rounded rectangle's box");
    }

    // ---------------- and what moving the shape does to it ----------------

    [Fact]
    public void moving_the_shape_does_not_change_where_the_gradient_sits_in_it()
    {
        using var still = Render(Shape(Across));
        using var moved = Render(Shape(Across), pageTop: 40);

        // The same place INSIDE the shape, before and after. If the gradient
        // were anchored to the page instead of to the shape, the shape would
        // slide across a stationary ramp and these would differ.
        for (int x = Left + 8; x <= Right - 8; x += 20)
        {
            Assert.Equal(
                Ramp(still.GetPixel(x, (Top + Bottom) / 2)),
                Ramp(moved.GetPixel(x, ((Top + Bottom) / 2) + 40)),
                2);
        }
    }

    [Fact]
    public void resizing_the_shape_stretches_the_gradient()
    {
        // DELIBERATE, and the consequence of storing fractions of the shape's
        // own box. A gradient that kept its absolute length would slide out of
        // a shape being dragged wider.
        using var narrow = Render(Shape(Across), w: 640, h: 400);
        using var wide = Render(Shape(Across, x2: 0.7), w: 640, h: 400);

        int y = (Top + Bottom) / 2;

        // Halfway along each is halfway along the ramp, whatever each is.
        Assert.InRange(Ramp(narrow.GetPixel(200, y)), 0.45, 0.55);
        Assert.InRange(Ramp(wide.GetPixel(320, y)), 0.45, 0.55);

        // And the wide one has NOT simply run out at the narrow one's edge,
        // which is what an unstretched gradient would look like.
        Assert.InRange(Ramp(wide.GetPixel(Right - 4, y)), 0.45, 0.55);
    }

    // ---------------- and turning it ----------------

    [Fact]
    public void the_gradient_turns_with_the_page()
    {
        // A quarter turn of a taller-than-wide page, which is the turn the
        // production path actually applies today. The shape's box runs from
        // normalized (0.1, 0.1) to (0.4, 0.3); turned, its red end lands to the
        // RIGHT and the ramp runs down the surface rather than across it.
        PageTransform Turned(int page) => PageTransform.For(Scale, 1000, 90, Scale);

        using var bitmap = Render(Shape(Across), view: Turned, w: 800, h: 800);

        // Along the turned shape's own axis the ramp still runs red to blue,
        // and across it nothing changes. Two samples on one side of the axis
        // and their partners on the other.
        double near = Ramp(bitmap.GetPixel(672, 100));
        double far = Ramp(bitmap.GetPixel(672, 220));

        Assert.True(near < far - 0.4, $"the ramp did not run down the turned page: {near} to {far}");
        Assert.Equal(near, Ramp(bitmap.GetPixel(700, 100)), 2);
    }

    [Fact]
    public void the_gradient_turns_with_the_shape()
    {
        // A shape's OWN rotation is carried by rotating its points, which is
        // what render_core does and what the shadow rasteriser does. The
        // gradient's endpoints live in the same space as those points, so the
        // same rotation is the whole of the work: no angle is stored and none
        // is applied by the painter.
        //
        // Turned a quarter about the box's centre, so a gradient that ran
        // across the shape now runs down it.
        var turned = Turn(Items(Shape(Across))[0], 0.25, 0.2, 90);

        using var bitmap = Render([turned]);

        Assert.True(
            Ramp(bitmap.GetPixel(200, 100)) < Ramp(bitmap.GetPixel(200, 220)) - 0.4,
            "the gradient did not turn with the shape's points");
    }

    /// <summary>
    /// One mark turned about a centre, points and gradient endpoints alike,
    /// exactly as <c>ShadowRasterizer.CasterItemsFor</c> turns its points.
    /// </summary>
    private static ShapeRenderItem Turn(
        ShapeRenderItem item, double cx, double cy, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        (double X, double Y) About((double X, double Y) p) => (
            cx + (((p.X - cx) * cos) - ((p.Y - cy) * sin)),
            cy + (((p.X - cx) * sin) + ((p.Y - cy) * cos)));

        var g = item.Fill.Gradient!.Value;
        var (x0, y0) = About((g.X0, g.Y0));
        var (x1, y1) = About((g.X1, g.Y1));

        return item with
        {
            Points = item.Points.Select(About).ToList(),
            Fill = ShapeFill.Of(g with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 }),
        };
    }

    // ---------------- and zooming ----------------

    [Fact]
    public void doubling_the_zoom_changes_nothing_but_the_scale()
    {
        var one = new ViewportProjection(Zoom: 1, DeviceScale: 1, 0, 0);
        var two = new ViewportProjection(Zoom: 2, DeviceScale: 1, 0, 0);

        using var small = Zoomed(one);
        using var big = Zoomed(two);

        // Every sample at twice the distance from the origin reads the same
        // place along the ramp. The gradient is geometry, not a picture that
        // was made at one size.
        for (int x = Left + 8; x <= Right - 8; x += 20)
        {
            // To one decimal, which is four units of a 255-step channel. The
            // two surfaces sample the ramp at points half a pixel apart, so
            // asking for more than that would be asking the two grids to
            // agree about something neither is measuring.
            Assert.Equal(
                Ramp(small.GetPixel(x, (Top + Bottom) / 2)),
                Ramp(big.GetPixel(x * 2, Top + Bottom)),
                1);
        }

        SKBitmap Zoomed(ViewportProjection projection)
        {
            var bitmap = new SKBitmap(800, 800, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            ShapeSkiaPainter.PaintViewport(
                canvas, Items(Shape(Across)), Scale, _ => 0, Flat, projection);
            return bitmap;
        }
    }

    // ---------------- and what it must not disturb ----------------

    [Fact]
    public void a_solid_fill_is_untouched_by_any_of_this()
    {
        var solid = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.4, 0.3), "#FF000000", StrokeWidthNorm)
        {
            Fill = ShapeFill.FromHex("#FF3B82F6"),
        };

        using var bitmap = Render(solid);

        // Flat, edge to edge. A solid that had quietly become a one-colour
        // gradient would still be blue in the middle.
        foreach (int x in new[] { Left + 6, (Left + Right) / 2, Right - 6 })
        {
            Assert.Equal(
                new SKColor(0x3B, 0x82, 0xF6), bitmap.GetPixel(x, (Top + Bottom) / 2));
        }
    }

    [Fact]
    public void the_stroke_round_a_gradient_is_the_weight_it_always_was()
    {
        // Both stops WHITE, so the paper and the fill are the same colour and
        // summing darkness down a column across the top edge measures the
        // stroke and nothing else. A gradient painted over the stroke would
        // come back at about half this.
        var pale = new GradientFill(White, White, 0, 0.5, 1, 0.5);

        using var hollow = Render(Shape(null));
        using var filled = Render(Shape(pale));

        int x = (Left + Right) / 2;
        double bare = InkDownColumn(hollow, x, Top - 10, Top + 10);
        double under = InkDownColumn(filled, x, Top - 10, Top + 10);

        Assert.InRange(bare, ExpectedThicknessDips - 0.5, ExpectedThicknessDips + 0.5);
        Assert.Equal(bare, under, 2);
    }

    private static double InkDownColumn(SKBitmap bitmap, int x, int fromY, int toY)
    {
        double ink = 0;
        for (int y = fromY; y <= toY; y++)
        {
            ink += (255 - bitmap.GetPixel(x, y).Red) / 255.0;
        }

        return ink;
    }

    // ---------------- and the effects beside it ----------------

    [Fact]
    public void a_drop_shadow_still_falls_from_a_gradient_filled_shape()
    {
        // The shadow is made from the object's own ALPHA, so a gradient-filled
        // shape casts a solid one where a hollow shape casts a hollow one. What
        // it must NOT do is take the gradient's colours: a shadow is its own
        // colour and the filter only reads coverage.
        var shadow = new ShapeEffects(
            new DropShadow(135, 0.02, new RenderColor(0xFF, 0, 0, 0)).ToSpec());

        using var bitmap = Render(Shape(Across, effects: shadow));

        // The light at 135 degrees throws the shadow down and to the right by
        // about 11 slot DIPs, so just past the bottom-right corner is shadow
        // and the shape is still itself.
        var cast = bitmap.GetPixel(Right + 8, Bottom + 8);

        Assert.True(cast.Red < 60 && cast.Green < 60 && cast.Blue < 60,
            $"no black shadow past the corner: {cast}");
        Assert.InRange(Ramp(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)), 0.45, 0.55);
    }

    [Fact]
    public void an_outer_glow_still_surrounds_a_gradient_filled_shape()
    {
        var glow = new ShapeEffects(new Glow(new RenderColor(0xFF, 0xFF, 0xD4, 0x00), 0.01).ToSpec());

        using var bitmap = Render(Shape(Across, effects: glow));

        // Just outside the left edge, where a glow with nowhere to fall puts
        // its halo. Yellow: high red and green, low blue.
        var halo = bitmap.GetPixel(Left - 5, (Top + Bottom) / 2);

        Assert.True(halo.Red > 200 && halo.Green > 150 && halo.Blue < 200,
            $"no yellow halo outside the edge: {halo}");
        Assert.InRange(Ramp(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)), 0.45, 0.55);
    }

    // ---------------- the conversion the whole model rests on ----------------

    [Fact]
    public void the_endpoints_are_resolved_into_the_marks_own_space()
    {
        // Stated directly, because every behaviour above is a consequence of
        // it. Fractions of the shape's box become normalized page-local points
        // beside the mark's own points.
        var g = Items(Shape(Across))[0].Fill.Gradient!.Value;

        Assert.Equal(0.1, g.X0, 9);
        Assert.Equal(0.2, g.Y0, 9);
        Assert.Equal(0.4, g.X1, 9);
        Assert.Equal(0.2, g.Y1, 9);

        // And the model's own copy is untouched: the shape still describes its
        // gradient in its own terms, which is what survives a save.
        Assert.Equal(Across, Shape(Across).Fill.Gradient);
    }
}
