using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Skia as a vector renderer for a SOLID fill.
///
/// The painter has only ever drawn outlines. An arrow's head is filled, but a
/// head is a triangle in the stroke's own colour and has nothing to do with
/// what the inside of a shape is painted with. This is the first time the
/// renderer has had a fill at all.
///
/// Measured against pixels, on the fixture the painter's own tests use: a
/// rectangle whose every edge lands on a whole DIP, normalized (0.1, 0.1) to
/// (0.4, 0.3) at a scale of 800, so nothing here turns into an argument about
/// antialiasing.
///
/// THE ORDER IS THE POINT. PDF's combined paint operator fills and then
/// strokes, so PDFium puts the stroke's inner half on top of the fill. Half of
/// these tests exist to prove this renderer does the same, because the failure
/// looks like a shape that is merely a bit thin rather than like a bug.
/// </summary>
public class ShapeFillPaintTests
{
    private const double Scale = 800;
    private const double StrokeWidthNorm = 0.004;      // 3.2 DIPs at this scale
    private const double ExpectedThicknessDips = 3.2;

    private const int Left = 80, Top = 80, Right = 320, Bottom = 240;

    private static readonly SKColor Paper = SKColors.White;

    private static ShapeAnnotation Shape(
        ShapeKind kind = ShapeKind.Rectangle,
        string strokeHex = "#FF000000",
        string? fillHex = null,
        double cornerFraction = ShapeGeometry.DefaultCornerFraction) =>
        new(0,
            new ShapeDraft(kind, 0.1, 0.1, 0.4, 0.3) { CornerFraction = cornerFraction },
            strokeHex,
            StrokeWidthNorm)
        {
            Fill = ShapeFill.FromHex(fillHex),
        };

    private static IReadOnlyList<ShapeRenderItem> Items(ShapeAnnotation shape) =>
        ShapeRenderList.From([], [shape]);

    private static PageTransform Flat(int page) => PageTransform.For(1, 1, 0, 1);

    private static SKBitmap Render(
        ShapeAnnotation shape,
        double pageTop = 0,
        Func<int, PageTransform>? view = null,
        int w = 400,
        int h = 400)
    {
        var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);
        ShapeSkiaPainter.Paint(canvas, Items(shape), Scale, _ => pageTop, view ?? Flat);
        return bitmap;
    }

    private static bool IsPaper(SKColor c) => c.Red == 255 && c.Green == 255 && c.Blue == 255;

    /// <summary>
    /// How much ink a column of pixels holds, in pixels' worth, for black on
    /// white. Antialiasing spreads a 3.2-wide stroke over five rows at partial
    /// coverage, so counting dark pixels measures nothing; summing coverage
    /// recovers the real width.
    /// </summary>
    private static double InkDownColumn(SKBitmap bitmap, int x, int fromY, int toY)
    {
        double ink = 0;
        for (int y = fromY; y <= toY; y++)
        {
            ink += (255 - bitmap.GetPixel(x, y).Red) / 255.0;
        }

        return ink;
    }

    // ---------------- the fill is there at all ----------------

    [Fact]
    public void a_filled_rectangle_is_painted_inside()
    {
        using var bitmap = Render(Shape(fillHex: "#FF3B82F6"));

        var inside = bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2);

        Assert.Equal(new SKColor(0x3B, 0x82, 0xF6), inside);
    }

    [Fact]
    public void a_shape_with_no_fill_is_still_hollow()
    {
        // The historic behaviour, and the default. Everything the renderer drew
        // before this stage has to keep coming out identical, which is what
        // lets the parity evidence gathered against the XAML overlay still mean
        // something.
        using var bitmap = Render(Shape());

        Assert.True(Items(Shape())[0].Fill.IsEmpty, "an unfilled shape carried a fill");
        Assert.True(
            IsPaper(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)),
            "an unfilled rectangle was painted inside");
    }

    [Fact]
    public void a_translucent_fill_lets_the_page_through()
    {
        // The bargain render_core's own fill makes, in its words: a light fill
        // still shows the page through it. Half-opaque red over white paper is
        // pink, not red.
        using var bitmap = Render(Shape(fillHex: "#80FF0000"));

        var inside = bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2);

        Assert.Equal(255, inside.Red);
        Assert.InRange(inside.Green, 120, 136);
        Assert.InRange(inside.Blue, 120, 136);
    }

    // ---------------- and it does not eat the stroke ----------------

    [Fact]
    public void a_fill_does_not_cover_the_inside_half_of_the_stroke()
    {
        // THE MEASUREMENT THIS STAGE TURNS ON, and the reason the fill is
        // opaque WHITE: the paper is white too, so summing darkness down a
        // column across the top edge measures the stroke and nothing else.
        //
        // Painted in the wrong order, a white fill would erase the stroke's
        // inner half and this would come back at about half the width. That is
        // not a subtle difference in a number, it is every filled shape in the
        // app being drawn thinner than the file says.
        using var hollow = Render(Shape());
        using var filled = Render(Shape(fillHex: "#FFFFFFFF"));

        int x = (Left + Right) / 2;
        double bare = InkDownColumn(hollow, x, Top - 10, Top + 10);
        double under = InkDownColumn(filled, x, Top - 10, Top + 10);

        // Within half a DIP of the width asked for, which is the bound
        // ShapeSkiaPainterTests establishes and explains: Skia's STROKER snaps
        // a width to the nearest half device pixel, so 3.2 renders as 3.0. That
        // is pre-existing and has nothing to do with the fill.
        Assert.InRange(bare, ExpectedThicknessDips - 0.5, ExpectedThicknessDips + 0.5);

        // And the fill changed it by NOTHING, which is the actual claim and is
        // exact rather than bounded.
        Assert.Equal(bare, under, 2);
    }

    [Fact]
    public void the_stroke_is_the_same_weight_on_every_edge_of_a_filled_shape()
    {
        using var bitmap = Render(Shape(fillHex: "#FFFFFFFF"));

        int x = (Left + Right) / 2;
        double top = InkDownColumn(bitmap, x, Top - 10, Top + 10);
        double bottom = InkDownColumn(bitmap, x, Bottom - 10, Bottom + 10);

        Assert.InRange(top, ExpectedThicknessDips - 0.5, ExpectedThicknessDips + 0.5);
        Assert.Equal(top, bottom, 2);
    }

    [Fact]
    public void no_lighter_seam_appears_where_the_fill_meets_the_stroke()
    {
        // Fill and stroke in ONE colour, which is the case a seam would show
        // in: two antialiased edges meeting at the path. Crossing inwards the
        // ink can only rise, and once the column is solid it must stay solid.
        // A dip is a pale line drawn round the inside of every filled shape.
        using var bitmap = Render(Shape(fillHex: "#FF000000"));

        int x = (Left + Right) / 2;
        bool solid = false;

        for (int y = Top - 4; y <= Top + 20; y++)
        {
            double ink = (255 - bitmap.GetPixel(x, y).Red) / 255.0;

            if (ink >= 0.999)
            {
                solid = true;
                continue;
            }

            Assert.False(solid, $"the ink dipped to {ink:F3} at y={y}, which is a seam");
        }

        Assert.True(solid, "the edge never became solid, so this measured nothing");
    }

    // ---------------- the fill is the shape, not its box ----------------

    [Fact]
    public void an_ellipse_fills_its_own_shape_and_not_its_box()
    {
        using var bitmap = Render(Shape(ShapeKind.Ellipse, fillHex: "#FF3B82F6"));

        Assert.Equal(
            new SKColor(0x3B, 0x82, 0xF6),
            bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2));

        // Inside the bounding box, outside the ellipse. A fill that painted the
        // box would be indistinguishable from a correct one at the centre.
        Assert.True(
            IsPaper(bitmap.GetPixel(Left + 4, Top + 4)),
            "the ellipse's fill spilled into the corner of its box");
    }

    [Fact]
    public void a_rounded_rectangle_fills_its_own_shape_and_not_its_box()
    {
        // Fully rounded, so the corner is missing by 80 DIPs rather than by a
        // couple and the assertion cannot be satisfied by antialiasing.
        var shape = Shape(ShapeKind.RoundedRectangle, fillHex: "#FF3B82F6", cornerFraction: 1);

        using var bitmap = Render(shape);

        Assert.Equal(
            new SKColor(0x3B, 0x82, 0xF6),
            bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2));
        Assert.True(
            IsPaper(bitmap.GetPixel(Left + 4, Top + 4)),
            "the rounded rectangle's fill spilled into the corner of its box");
    }

    // ---------------- what has no inside is unaffected ----------------

    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void a_shape_with_no_inside_is_pixel_for_pixel_what_it_was(ShapeKind kind)
    {
        // A line encloses no area and an arrow's shaft stops at its head's
        // base, so a fill on either paints nothing. Nothing decides that: the
        // fill is the same path as the stroke, and a two-point path has no
        // inside. The arrow also proves its HEAD is untouched, since the head
        // is filled in the STROKE's colour and a change would show here.
        using var bare = Render(Shape(kind));
        using var filled = Render(Shape(kind, fillHex: "#FF3B82F6"));

        for (int y = 0; y < bare.Height; y++)
        {
            for (int x = 0; x < bare.Width; x++)
            {
                if (bare.GetPixel(x, y) != filled.GetPixel(x, y))
                {
                    Assert.Fail($"a fill changed a {kind} at ({x}, {y})");
                }
            }
        }
    }

    // ---------------- fill without a stroke ----------------

    [Fact]
    public void a_shape_whose_stroke_is_invisible_is_still_filled()
    {
        // The model has always allowed it: render_core takes any stroke colour
        // including a transparent one, and the width it insists on is only the
        // geometry's.
        using var bitmap = Render(Shape(strokeHex: "#00000000", fillHex: "#FF3B82F6"));

        Assert.Equal(
            new SKColor(0x3B, 0x82, 0xF6),
            bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2));

        // And nothing was drawn round it. Just outside the path, where the
        // outer half of a visible stroke would be.
        Assert.True(
            IsPaper(bitmap.GetPixel((Left + Right) / 2, Top - 2)),
            "an invisible stroke still painted");
    }

    // ---------------- and it goes where the shape goes ----------------

    [Fact]
    public void the_fill_follows_the_page_down_the_stack()
    {
        using var bitmap = Render(Shape(fillHex: "#FF3B82F6"), pageTop: 40);

        Assert.Equal(
            new SKColor(0x3B, 0x82, 0xF6),
            bitmap.GetPixel((Left + Right) / 2, ((Top + Bottom) / 2) + 40));
        // A point that was inside the shape before the page moved and is above
        // it now. Its own centre would still be inside after a 40 DIP shift, so
        // asserting on that would prove nothing.
        Assert.True(
            IsPaper(bitmap.GetPixel((Left + Right) / 2, Top + 10)),
            "the fill stayed where the page used to be");
    }

    [Fact]
    public void the_fill_turns_with_the_page()
    {
        // A quarter turn of a taller-than-wide page. The shape's centre is
        // normalized (0.25, 0.2), which at this scale is slot (200, 160) and
        // lands at (672, 160) once turned.
        PageTransform Turned(int page) => PageTransform.For(Scale, 1000, 90, Scale);

        using var bitmap = Render(Shape(fillHex: "#FF3B82F6"), view: Turned, w: 800, h: 800);

        Assert.Equal(new SKColor(0x3B, 0x82, 0xF6), bitmap.GetPixel(672, 160));

        // And the place it would have landed untouched is empty, which is what
        // makes this able to fail.
        Assert.True(IsPaper(bitmap.GetPixel(200, 160)), "the fill did not turn with the page");
    }

    [Fact]
    public void the_fill_scales_with_the_zoom()
    {
        var projection = new ViewportProjection(Zoom: 2, DeviceScale: 1, 0, 0);

        var bitmap = new SKBitmap(800, 800, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Paper);
            ShapeSkiaPainter.PaintViewport(
                canvas, Items(Shape(fillHex: "#FF3B82F6")), Scale, _ => 0, Flat, projection);
        }

        using (bitmap)
        {
            // Every edge twice as far out, so the old centre is still inside
            // and the old corner is now well within the fill.
            Assert.Equal(new SKColor(0x3B, 0x82, 0xF6), bitmap.GetPixel(Left * 2 + 8, Top * 2 + 8));
            Assert.True(
                IsPaper(bitmap.GetPixel(Left * 2 - 8, Top * 2 - 8)),
                "the fill did not stop where the zoomed shape does");
        }
    }

    // ---------------- and it stays out of the effects ----------------

    [Fact]
    public void the_shadow_caster_does_not_use_the_new_fill_property()
    {
        // A filled shape has cast a SOLID shadow since long before this stage,
        // by the rasteriser inserting a copy of the outline in the filled
        // style. That is untouched, and it must stay untouched: honouring the
        // new property as well would paint the silhouette twice, and two
        // half-opaque coats are not one.
        var tag = new ShapeTag(
            ShapeKind.Rectangle, "#FF000000", 2, false, false, 0, "#803B82F6", 0);

        var items = ShadowRasterizer.CasterItemsFor(tag, 0.1, 0.1, 0.4, 0.3, 612);

        Assert.Equal(2, items.Count);
        Assert.Equal(RenderStyle.Filled, items[0].Style);
        Assert.Equal(RenderStyle.Stroked, items[1].Style);

        foreach (var item in items)
        {
            Assert.True(item.Fill.IsEmpty, "the caster started carrying a fill of its own");
        }
    }
}
