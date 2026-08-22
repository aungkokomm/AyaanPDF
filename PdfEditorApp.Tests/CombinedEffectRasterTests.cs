using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Several effects, ONE picture.
///
/// The annotation has a single image slot, so a shape with a shadow and a glow
/// cannot have a bitmap each: they are drawn into the same one, in list order,
/// over a box big enough for both. Two pictures would need two image objects,
/// which is a second attach path, a second thing to keep in step with the tag,
/// and a second thing to leave behind when an effect is removed.
///
/// ONLY THE SOFT ONES GO IN. A hard drop shadow is still vector paths written by
/// render_core, crisp at any zoom, so it contributes nothing here and must not
/// change the box either, or the picture would be offset inside a rectangle
/// sized for something that is not in it.
/// </summary>
public class CombinedEffectRasterTests
{
    private const double PageWidthPts = 612;

    private static EffectSpec Shadow(double softnessPts, double distancePts = 12) =>
        new(EffectKind.DropShadow, new RenderColor(0xFF, 0, 0, 0xFF),
            Blur: softnessPts / PageWidthPts,
            Distance: distancePts / PageWidthPts,
            AngleDeg: 135);

    private static EffectSpec Glow(double softnessPts) =>
        new(EffectKind.Glow, new RenderColor(0xFF, 0, 0xFF, 0), Blur: softnessPts / PageWidthPts);

    private static IReadOnlyList<ShapeRenderItem> Rect() =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.5, 0.5),
                    "#FF000000", 0.004),
            });

    /// <summary>How many pixels carry each effect's own colour.</summary>
    private static (int Blue, int Green) Counts(ShadowRaster raster)
    {
        int blue = 0, green = 0;

        // Premultiplied BGRA. The shadow is pure blue and the glow pure green,
        // so a pixel belongs to whichever channel it has.
        for (int at = 0; at + 3 < raster.Bgra.Length; at += 4)
        {
            if (raster.Bgra[at + 3] == 0) { continue; }
            if (raster.Bgra[at] > raster.Bgra[at + 1]) { blue++; }
            else if (raster.Bgra[at + 1] > 0) { green++; }
        }

        return (blue, green);
    }

    // ---------------- one effect, unchanged ----------------

    [Fact]
    public void a_glow_alone_makes_a_picture()
    {
        var raster = ShadowRasterizer.Rasterize(Rect(), new[] { Glow(8) }, PageWidthPts);

        Assert.NotNull(raster);
        Assert.True(Counts(raster!.Value).Green > 100, "the glow is not in the picture");
    }

    [Fact]
    public void the_single_effect_call_is_the_list_of_one()
    {
        // The old signature is kept and delegates, so the shadow's existing
        // path cannot drift from the list path underneath it.
        var one = ShadowRasterizer.Rasterize(Rect(), Shadow(8), PageWidthPts)!.Value;
        var list = ShadowRasterizer.Rasterize(Rect(), new[] { Shadow(8) }, PageWidthPts)!.Value;

        Assert.Equal(one.PixelWidth, list.PixelWidth);
        Assert.Equal(one.PixelHeight, list.PixelHeight);
        Assert.Equal(one.Left, list.Left);
        Assert.True(one.Bgra.AsSpan().SequenceEqual(list.Bgra), "the two paths drew different pictures");
    }

    // ---------------- two effects, one picture ----------------

    [Fact]
    public void a_shadow_and_a_glow_land_in_the_same_picture()
    {
        var raster = ShadowRasterizer.Rasterize(
            Rect(), new[] { Shadow(8), Glow(8) }, PageWidthPts);

        var (blue, green) = Counts(raster!.Value);

        Assert.True(blue > 100, "the shadow is missing from the picture");
        Assert.True(green > 100, "the glow is missing from the picture");
    }

    [Fact]
    public void the_later_effect_is_the_one_on_top()
    {
        // Same order the painter draws them in, and the reason the list is
        // ordered at all: the canonical order puts the shadow underneath.
        var shadowUnder = ShadowRasterizer.Rasterize(
            Rect(), new[] { Shadow(6, distancePts: 0), Glow(6) }, PageWidthPts)!.Value;
        var glowUnder = ShadowRasterizer.Rasterize(
            Rect(), new[] { Glow(6), Shadow(6, distancePts: 0) }, PageWidthPts)!.Value;

        // Cast from the same place, so whichever is painted second covers the
        // other and the two orders cannot produce the same pixels.
        Assert.False(
            shadowUnder.Bgra.AsSpan().SequenceEqual(glowUnder.Bgra),
            "the order of the list did not reach the picture");

        Assert.True(Counts(shadowUnder).Green > Counts(shadowUnder).Blue, "the glow should be on top");
        Assert.True(Counts(glowUnder).Blue > Counts(glowUnder).Green, "the shadow should be on top");
    }

    [Fact]
    public void the_box_holds_every_effect_that_is_in_the_picture()
    {
        var shadowOnly = ShadowRasterizer.BoundsOf(Rect(), new[] { Shadow(8) });
        var glowOnly = ShadowRasterizer.BoundsOf(Rect(), new[] { Glow(30) });
        var both = ShadowRasterizer.BoundsOf(Rect(), new[] { Shadow(8), Glow(30) });

        Assert.Equal(Math.Min(shadowOnly.L, glowOnly.L), both.L, 9);
        Assert.Equal(Math.Min(shadowOnly.T, glowOnly.T), both.T, 9);
        Assert.Equal(Math.Max(shadowOnly.R, glowOnly.R), both.R, 9);
        Assert.Equal(Math.Max(shadowOnly.B, glowOnly.B), both.B, 9);
    }

    // ---------------- and the hard ones stay out of it ----------------

    [Fact]
    public void an_unblurred_effect_is_not_in_the_picture_at_all()
    {
        // It is drawn as paths by render_core. Rasterising it as well would
        // double it, and the file would carry a soft copy under a crisp one.
        var raster = ShadowRasterizer.Rasterize(
            Rect(), new[] { Shadow(softnessPts: 0), Glow(8) }, PageWidthPts)!.Value;

        Assert.True(Counts(raster).Green > 100, "the glow is missing");
        Assert.Equal(0, Counts(raster).Blue);
    }

    [Fact]
    public void an_unblurred_effect_does_not_move_the_box_either()
    {
        // The box has to be the box of what is IN the picture. Sized for a
        // shadow that is not there, the picture would sit off-centre inside it.
        var glowOnly = ShadowRasterizer.BoundsOf(Rect(), new[] { Glow(8) });
        var withHard = ShadowRasterizer.BoundsOf(
            Rect(), new[] { Shadow(softnessPts: 0, distancePts: 40), Glow(8) });

        Assert.Equal(glowOnly.L, withHard.L, 9);
        Assert.Equal(glowOnly.R, withHard.R, 9);
    }

    [Fact]
    public void nothing_soft_makes_no_picture()
    {
        Assert.Null(ShadowRasterizer.Rasterize(
            Rect(), new[] { Shadow(softnessPts: 0) }, PageWidthPts));
        Assert.Null(ShadowRasterizer.Rasterize(
            Rect(), Array.Empty<EffectSpec>(), PageWidthPts));
    }

    [Fact]
    public void an_invisible_effect_is_not_in_the_picture()
    {
        var raster = ShadowRasterizer.Rasterize(
            Rect(),
            new[] { Shadow(8) with { Color = new RenderColor(0, 0, 0, 0xFF) }, Glow(8) },
            PageWidthPts)!.Value;

        Assert.Equal(0, Counts(raster).Blue);
    }
}
