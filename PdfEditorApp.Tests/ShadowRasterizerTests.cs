using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The committed half of the shadow: the picture that goes into the file.
///
/// A page renders through PDFium, which has no blur, so a soft shadow is drawn
/// by Skia and embedded. What matters here is that the picture covers what the
/// blur can reach, is sampled finely enough to survive a zoom, and is not made
/// at all when there is nothing to blur.
/// </summary>
public class ShadowRasterizerTests
{
    private const double PageWidthPts = 612;

    private static DropShadow Shadow(double softnessPts, double distancePts = 12, byte a = 0x80) =>
        new(135, distancePts / PageWidthPts, new RenderColor(a, 0, 0, 0), softnessPts / PageWidthPts);

    private static IReadOnlyList<ShapeRenderItem> Rect(double w = 0.3) =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.2 + w, 0.2 + w),
                    "#FF000000", 0.004),
            });

    // ---------------- when there is nothing to draw ----------------

    [Fact]
    public void a_hard_shadow_is_not_rasterised_at_all()
    {
        // It stays a vector path in render_core, crisp at any zoom. No raster
        // resolution reproduces a hard edge, so making a picture of one would
        // be strictly worse than what is already there.
        Assert.Null(ShadowRasterizer.Rasterize(Rect(), Shadow(softnessPts: 0), PageWidthPts));
    }

    [Fact]
    public void an_invisible_shadow_is_not_rasterised_either()
    {
        Assert.Null(ShadowRasterizer.Rasterize(Rect(), Shadow(8, a: 0), PageWidthPts));
    }

    [Fact]
    public void nothing_to_cast_a_shadow_makes_no_picture()
    {
        Assert.Null(ShadowRasterizer.Rasterize(
            Array.Empty<ShapeRenderItem>(), Shadow(8), PageWidthPts));
    }

    // ---------------- the box ----------------

    [Fact]
    public void the_box_follows_the_shadow_not_the_shape()
    {
        // Offset by the shadow's own offset, so the picture sits where the
        // shadow falls rather than where the shape is.
        var shadow = Shadow(softnessPts: 0.0001, distancePts: 24);
        var box = ShadowRasterizer.BoundsOf(Rect(), shadow);

        Assert.True(box.L > 0.2 + shadow.OffsetX - 0.01, "the box is not where the shadow falls");
        Assert.True(box.L < 0.2 + shadow.OffsetX + 0.01, "the box is not where the shadow falls");
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(12.0)]
    [InlineData(24.0)]
    public void the_box_makes_room_for_three_sigma(double softnessPts)
    {
        // The reach a gaussian is spent by, and the SAME three sigma the
        // preview's layer and render_core's annotation rectangle use. Short of
        // it, the blur is cut off in a straight line.
        var shadow = Shadow(softnessPts);
        var tight = ShadowRasterizer.BoundsOf(Rect(), Shadow(softnessPts: 0.0));
        var box = ShadowRasterizer.BoundsOf(Rect(), shadow);

        double expected = shadow.Softness * OverlayProjection.BlurReachSigmas / 2;

        Assert.Equal(tight.L - expected, box.L, 6);
        Assert.Equal(tight.R + expected, box.R, 6);
        Assert.Equal(tight.T - expected, box.T, 6);
        Assert.Equal(tight.B + expected, box.B, 6);
    }

    [Fact]
    public void the_box_is_the_one_render_core_reserves_room_for()
    {
        // render_core grows the annotation's rectangle by softness * 1.5, in
        // points, and PDFium crops the appearance to it. If this box were
        // bigger, the picture would be clipped in the file even though it looks
        // right in the preview.
        double softnessPts = 10;
        var shadow = Shadow(softnessPts);
        var reserved = softnessPts * 1.5;

        var tight = ShadowRasterizer.BoundsOf(Rect(), Shadow(softnessPts: 0.0));
        var box = ShadowRasterizer.BoundsOf(Rect(), shadow);

        Assert.Equal(reserved, (tight.L - box.L) * PageWidthPts, 4);
    }

    // ---------------- how finely ----------------

    [Fact]
    public void it_samples_at_six_pixels_to_the_point()
    {
        var raster = ShadowRasterizer.Rasterize(Rect(), Shadow(8), PageWidthPts);

        Assert.NotNull(raster);
        var box = ShadowRasterizer.BoundsOf(Rect(), Shadow(8));
        double widthPts = (box.R - box.L) * PageWidthPts;

        Assert.Equal(
            Math.Ceiling(widthPts * ShadowRasterizer.PixelsPerPoint),
            raster!.Value.PixelWidth);
    }

    [Fact]
    public void a_very_large_shape_is_capped_rather_than_sampled_to_death()
    {
        // A full-page shape at six pixels to the point would be four thousand
        // across. Where the cap bites, the shape is large enough that nobody
        // can see the difference.
        var huge = ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.01, 0.01, 0.99, 1.28),
                    "#FF000000", 0.004),
            });

        var raster = ShadowRasterizer.Rasterize(huge, Shadow(8), PageWidthPts);

        Assert.NotNull(raster);
        Assert.True(
            Math.Max(raster!.Value.PixelWidth, raster.Value.PixelHeight) <= ShadowRasterizer.MaxSide,
            $"{raster.Value.PixelWidth}x{raster.Value.PixelHeight} is over the cap");
    }

    // ---------------- the pixels ----------------

    [Fact]
    public void the_buffer_is_the_size_its_dimensions_promise()
    {
        // PDFium reads what the dimensions say, not what was allocated, so a
        // disagreement here is a buffer overrun in native code.
        var raster = ShadowRasterizer.Rasterize(Rect(), Shadow(8), PageWidthPts)!.Value;

        Assert.Equal(raster.PixelWidth * raster.PixelHeight * 4, raster.Bgra.Length);
    }

    [Fact]
    public void it_draws_the_shadow_and_not_the_shape()
    {
        // CreateDropShadowOnly, so what comes back is the blurred silhouette in
        // the shadow's colour. The shape is drawn separately as paths by
        // render_core, and having it here as well would double every edge.
        var raster = ShadowRasterizer.Rasterize(
            Rect(), Shadow(8) with { Color = new RenderColor(0xFF, 0, 0, 0xFF) },
            PageWidthPts)!.Value;

        // Premultiplied BGRA: a blue shadow has blue where it has alpha, and
        // nothing anywhere else. The shape's own colour is black, so any red or
        // green would mean the shape came along too.
        int blue = 0, other = 0;
        for (int i = 0; i + 3 < raster.Bgra.Length; i += 4)
        {
            if (raster.Bgra[i + 3] == 0) { continue; }
            if (raster.Bgra[i] > 0 && raster.Bgra[i + 1] == 0 && raster.Bgra[i + 2] == 0)
            {
                blue++;
            }
            else
            {
                other++;
            }
        }

        Assert.True(blue > 100, "the shadow is not in the picture");
        Assert.True(other < blue / 50, $"{other} pixels are not the shadow's colour");
    }

    [Fact]
    public void softness_changes_the_picture()
    {
        int Ink(double softnessPts)
        {
            var r = ShadowRasterizer.Rasterize(Rect(), Shadow(softnessPts), PageWidthPts)!.Value;
            int n = 0;
            for (int i = 3; i < r.Bgra.Length; i += 4)
            {
                if (r.Bgra[i] > 0) { n++; }
            }

            return n;
        }

        // A wider blur spreads the same ink over more of the page.
        Assert.True(Ink(20) > Ink(4) * 1.5, "the blur value is not reaching the rasteriser");
    }

    [Fact]
    public void a_hollow_shape_casts_a_hollow_picture()
    {
        // The alpha rule again, on the committed side: the preview and the file
        // have to agree about what a shadow IS, not merely about how blurred it
        // is.
        var raster = ShadowRasterizer.Rasterize(
            Rect(), Shadow(softnessPts: 1), PageWidthPts)!.Value;

        int middle = ((raster.PixelHeight / 2) * raster.PixelWidth + (raster.PixelWidth / 2)) * 4;

        Assert.True(
            raster.Bgra[middle + 3] < 40,
            "the middle of a hollow shape's shadow is solid, so something is " +
            "filling the silhouette instead of reading the alpha");
    }
}
