using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Stage 1 of the Skia migration, and its only question: can Skia put one
/// rectangle in exactly the place, at exactly the weight and colour, that the
/// existing overlay puts it?
///
/// Measured off screen, against pixels. The overlay itself is a WinUI page this
/// assembly cannot load, so parity is established the same way it was in stage
/// 0: the projection is computed independently here, the pixels are checked
/// against it, and source guards in ShapeRenderListTests hold the overlay to
/// that same arithmetic.
///
/// The rectangle is chosen so every edge lands on a whole DIP: normalized
/// (0.1, 0.1) to (0.4, 0.3) at a scale of 800 is (80, 80) to (320, 240). A
/// fixture whose edges fall between pixels would blur every assertion into an
/// antialiasing argument.
/// </summary>
public class ShapeSkiaPainterTests
{
    private const double Scale = 800;
    private const double StrokeWidthNorm = 0.004;      // 3.2 DIPs at this scale
    private const double ExpectedThicknessDips = 3.2;

    private const int Left = 80, Top = 80, Right = 320, Bottom = 240;

    private static ShapeRenderItem Rectangle(string colorHex = "#FF000000", int page = 0)
    {
        var shape = new ShapeAnnotation(
            page, new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.4, 0.3),
            colorHex, StrokeWidthNorm);

        return ShapeRenderList.From([], [shape])[0];
    }

    /// <summary>
    /// An unturned view, so every assertion below goes on measuring what it
    /// always measured. The turn is the identity at 0 degrees, which is what
    /// lets this whole file stay unchanged; the rotated cases are in
    /// SkiaRotationParityTests.
    /// </summary>
    private static PageTransform Flat(int page) => PageTransform.For(1, 1, 0, 1);

    private static SKBitmap Render(
        IReadOnlyList<ShapeRenderItem> items, double pageTop = 0, int w = 400, int h = 400)
    {
        var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.Paint(canvas, items, Scale, _ => pageTop, Flat);
        return bitmap;
    }

    private static bool IsWhite(SKColor c) => c.Red == 255 && c.Green == 255 && c.Blue == 255;

    /// <summary>
    /// How much ink a column of pixels holds, in pixels' worth, for black on
    /// white. Antialiasing spreads a 3.2-wide stroke over five rows at partial
    /// coverage, so counting "dark pixels" measures nothing; summing coverage
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

    [Fact]
    public void the_rectangle_lands_where_the_projection_says_it_does()
    {
        using var bitmap = Render([Rectangle()]);

        // The midpoint of each of the four edges, on the path itself.
        foreach (var (x, y) in new[]
        {
            ((Left + Right) / 2, Top),        // top
            ((Left + Right) / 2, Bottom),     // bottom
            (Left, (Top + Bottom) / 2),       // left
            (Right, (Top + Bottom) / 2),      // right
        })
        {
            Assert.False(IsWhite(bitmap.GetPixel(x, y)), $"no stroke at ({x}, {y})");
        }
    }

    [Fact]
    public void all_four_corners_are_drawn()
    {
        // A polyline that failed to close, or dropped its repeated first point,
        // loses an edge without losing the shape's overall look.
        using var bitmap = Render([Rectangle()]);

        foreach (var (x, y) in new[] { (Left, Top), (Right, Top), (Right, Bottom), (Left, Bottom) })
        {
            Assert.False(IsWhite(bitmap.GetPixel(x, y)), $"no corner at ({x}, {y})");
        }
    }

    [Fact]
    public void a_rectangle_is_an_outline_and_not_a_fill()
    {
        // The overlay draws shapes as a Polyline with a Stroke and no Fill, so
        // a filled rectangle here would be a new behaviour, not a parity match.
        // Shape fill lives in the PDF write path, which this does not touch.
        using var bitmap = Render([Rectangle()]);

        Assert.True(IsWhite(bitmap.GetPixel((Left + Right) / 2, (Top + Bottom) / 2)), "middle is filled");
        Assert.True(IsWhite(bitmap.GetPixel(Left + 20, Top + 20)), "inside the corner is filled");
    }

    [Fact]
    public void nothing_is_painted_outside_the_shape()
    {
        using var bitmap = Render([Rectangle()]);

        Assert.True(IsWhite(bitmap.GetPixel(20, 20)));
        Assert.True(IsWhite(bitmap.GetPixel(380, 380)));
        Assert.True(IsWhite(bitmap.GetPixel((Left + Right) / 2, Top - 10)));
    }

    [Fact]
    public void the_stroke_is_within_half_a_pixel_of_the_projected_width()
    {
        // Deliberately a BOUND, not an equality, and this is the one criterion
        // stage 1 did not settle.
        //
        // 0.004 normalized at a scale of 800 is 3.2 DIPs, which is what the
        // overlay sets StrokeThickness to. Skia renders 3.0. Whether Direct2D
        // under a XAML Polyline renders 3.2 or also snaps is NOT knowable from
        // this assembly, so claiming equality here would be asserting something
        // unmeasured. The bound is what is actually demonstrated.
        using var bitmap = Render([Rectangle()]);

        double ink = InkDownColumn(bitmap, x: (Left + Right) / 2, fromY: Top - 12, toY: Top + 12);

        Assert.InRange(ink, ExpectedThicknessDips - 0.5, ExpectedThicknessDips + 0.5);
    }

    [Fact]
    public void skia_snaps_stroke_width_to_the_nearest_half_device_pixel()
    {
        // The measured cause of the gap above, pinned so a future Skia version
        // that changes it is noticed here and not on somebody's page.
        //
        // 3.2 renders as 3.0, 3.6 as 3.5, 2.3 as 2.5. It is the STROKER: a
        // filled rectangle of the same height is exact to three decimal places,
        // and GetFillPath inherits the same rounding. It is applied in device
        // space, so the relative error falls as DPI and zoom rise (-6.1% at
        // 1.0x, +1.6% at 2.0x) and is bounded by a quarter of a device pixel.
        foreach (var (widthDips, expected) in new[] { (3.2, 3.0), (3.6, 3.5), (2.3, 2.5) })
        {
            var shape = new ShapeAnnotation(
                0, new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.4, 0.3),
                "#FF000000", widthDips / Scale);

            using var bitmap = Render([ShapeRenderList.From([], [shape])[0]]);
            double ink = InkDownColumn(bitmap, (Left + Right) / 2, Top - 12, Top + 12);

            Assert.Equal(expected, ink, precision: 1);
        }
    }

    [Fact]
    public void the_stroke_is_centred_on_the_path_not_inside_it()
    {
        // Half the ink each side. An inside or outside stroke would measure the
        // same width and sit in the wrong place.
        //
        // The split falls AT the path, not either side of it: the top edge is
        // at y = 80.0, which is the boundary between rows 79 and 80, so rows up
        // to 79 are above the line and rows from 80 are below it. Excluding a
        // row at "the line" would drop real ink from one side only.
        using var bitmap = Render([Rectangle()]);

        int x = (Left + Right) / 2;
        double above = InkDownColumn(bitmap, x, Top - 12, Top - 1);
        double below = InkDownColumn(bitmap, x, Top, Top + 12);

        Assert.Equal(above, below, precision: 1);
    }

    [Fact]
    public void the_page_offset_moves_the_whole_shape_down_the_stack()
    {
        // Only Y takes the page top. A rectangle on the second page of a
        // continuous view must land on that page, and must not drift sideways.
        const int pageTop = 50;
        using var bitmap = Render([Rectangle()], pageTop);

        Assert.True(IsWhite(bitmap.GetPixel((Left + Right) / 2, Top)), "still drawn at the old top");
        Assert.False(IsWhite(bitmap.GetPixel((Left + Right) / 2, Top + pageTop)), "not drawn at the new top");
        Assert.False(IsWhite(bitmap.GetPixel(Left, ((Top + Bottom) / 2) + pageTop)), "left edge moved sideways");
    }

    [Fact]
    public void the_colour_reaches_the_pixels_with_its_channels_in_order()
    {
        // ARGB in the app, RGBA in Skia. A swap here produces a plausible
        // colour and no error, which is exactly how it would ship.
        using var bitmap = Render([Rectangle("#FF112233")]);

        var pixel = bitmap.GetPixel((Left + Right) / 2, Top);

        Assert.Equal(0x11, pixel.Red);
        Assert.Equal(0x22, pixel.Green);
        Assert.Equal(0x33, pixel.Blue);
    }

    [Fact]
    public void opacity_composites_the_way_a_half_transparent_overlay_stroke_does()
    {
        // Half-opaque black over white is mid grey. Alpha dropped on the floor
        // would give solid black and still look like a working renderer.
        using var bitmap = Render([Rectangle("#80000000")]);

        var pixel = bitmap.GetPixel((Left + Right) / 2, Top);

        Assert.InRange(pixel.Red, 120, 136);
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Green, pixel.Blue);
    }

    [Fact]
    public void a_filled_item_is_not_painted_yet()
    {
        // Stage 1 covers stroked marks only. Pinned rather than assumed, so the
        // day an arrow head is expected to appear, this test says where it went.
        var arrow = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Arrow, 0.1, 0.1, 0.4, 0.3), "#FF000000", StrokeWidthNorm);

        var head = ShapeRenderList.From([], [arrow])[1];
        Assert.Equal(RenderStyle.Filled, head.Style);

        using var bitmap = Render([head]);

        for (int y = 0; y < 400; y += 4)
        {
            for (int x = 0; x < 400; x += 4)
            {
                Assert.True(IsWhite(bitmap.GetPixel(x, y)), $"filled head painted at ({x}, {y})");
            }
        }
    }

    [Fact]
    public void an_item_with_nothing_to_draw_draws_nothing()
    {
        var single = new ShapeRenderItem(
            0, [(0.2, 0.2)], new RenderColor(255, 0, 0, 0), StrokeWidthNorm, RenderStyle.Stroked);

        using var bitmap = Render([single]);

        Assert.True(IsWhite(bitmap.GetPixel(160, 160)));
    }

    [Fact]
    public void an_empty_frame_leaves_the_canvas_alone()
    {
        using var bitmap = Render([]);

        Assert.True(IsWhite(bitmap.GetPixel(200, 200)));
    }
}
