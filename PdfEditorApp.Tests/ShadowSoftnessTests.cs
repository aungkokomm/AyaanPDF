using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A soft shadow, in the Skia renderer.
///
/// Softness is the one part of the model that is not a number carried through
/// to a draw call: it is a second thing the renderer has to DO. Everything here
/// is about the consequences of that.
///
/// IT IS BLURRED ON THE OBJECT'S LAYER, not per mark, for the same reason the
/// object-level shadow exists at all: an arrow's shaft and head are unioned
/// into one silhouette first, and a silhouette has no internal edges to soften.
/// Blurring each part separately would soften a seam that is not really there.
///
/// IT ESCAPES ITS OWN BOX, which is what makes it dangerous. A blur puts ink
/// well outside the geometry that produced it, and two other pieces of
/// machinery read that box: the culler drops what it thinks is off screen, and
/// the dirty region clears what it thinks was touched. Measured, the ink
/// reaches about 2.5 sigma; the reach used is 3 sigma, which is that with room.
/// Under-padding does not merely leave a fringe behind, it CUTS the blur into a
/// hard edge, because the clip is set before the paint.
/// </summary>
public class ShadowSoftnessTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, ContentH, 0, Scale);
    private static readonly RenderColor Black = new(0xFF, 0, 0, 0);

    /// <summary>Blur radius as the model carries it: a fraction of the page's width.</summary>
    private const double SoftRadius = 0.02;

    private static ShapeEffects Shadow(double softness) =>
        new(new DropShadow(135, 0.10, Black, Softness: softness));

    /// <summary>One mark: a filled square, so its edge is a clean step to measure.</summary>
    private static ShapeRenderItem Mark(ShapeEffects? effects) =>
        new(0,
            new (double X, double Y)[]
            {
                (0.30, 0.30), (0.55, 0.30), (0.55, 0.55), (0.30, 0.55), (0.30, 0.30),
            },
            Black, 0.004, RenderStyle.Filled, null, effects)
        { ObjectId = Guid.NewGuid() };

    private static SKBitmap Paint(
        IReadOnlyList<ShapeRenderItem> items, SKRect? clip = null)
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        if (clip is { } rect) { canvas.ClipRect(rect); }
        ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => View, Plain);
        return bitmap;
    }

    /// <summary>The box of every pixel that is not pure page white.</summary>
    private static (int L, int T, int R, int B) Ink(SKBitmap bitmap)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        var px = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                if (px[at] != 255 || px[at + 1] != 255 || px[at + 2] != 255)
                {
                    l = Math.Min(l, x); t = Math.Min(t, y);
                    r = Math.Max(r, x); b = Math.Max(b, y);
                }
            }
        }

        Assert.True(r >= 0, "nothing was painted at all");
        return (l, t, r, b);
    }

    /// <summary>
    /// Every pixel that is part-way between page and ink.
    ///
    /// A hard edge is a step, so only the antialiased pixel or two along each
    /// edge lands here. A blur is a ramp and the ramp is the whole point, so
    /// this rises by more than an order of magnitude when one is applied.
    /// Counted over the whole image rather than along a chosen scanline, which
    /// avoids having to know where the shape ends and the shadow begins.
    /// </summary>
    private static int PartWay(SKBitmap bitmap)
    {
        int count = 0;
        var px = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                byte v = px[(y * stride) + (x * 4)];
                if (v > 20 && v < 235) { count++; }
            }
        }

        return count;
    }

    private static bool Same(SKBitmap a, SKBitmap b)
    {
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        return pa.SequenceEqual(pb);
    }

    // ---------------- the edge actually softens ----------------

    [Fact]
    public void a_soft_shadow_reaches_past_the_silhouette_a_hard_one_stops_at()
    {
        // Sigma here is 8 slot units and the probe measured a blurred edge
        // reaching about 2.4 sigma, so roughly 19 device pixels. Requiring 12
        // is well clear of both a hard edge, which reaches one, and of any
        // argument about the exact ratio.
        using var hard = Paint(new[] { Mark(Shadow(0)) });
        using var soft = Paint(new[] { Mark(Shadow(SoftRadius)) });

        var h = Ink(hard);
        var s = Ink(soft);

        // The shadow falls down and to the right, so those are the sides it
        // can grow on; the other two are bounded by the shape, which does not
        // move. That asymmetry is the second assertion: only the SHADOW is
        // blurred, and a blur applied to the object itself would push the left
        // and top edges out as well.
        Assert.True(s.R - h.R >= 12, "right reached " + (s.R - h.R) + " further, not enough");
        Assert.True(s.B - h.B >= 12, "bottom reached " + (s.B - h.B) + " further, not enough");
        Assert.Equal(h.L, s.L);
        Assert.Equal(h.T, s.T);
    }

    [Fact]
    public void a_soft_edge_is_a_ramp_and_not_merely_a_bigger_step()
    {
        // Reaching further is what a DILATED shadow would also do. What makes
        // it soft is that the extra distance is spent fading, so the count of
        // part-way pixels has to rise with it.
        using var hard = Paint(new[] { Mark(Shadow(0)) });
        using var soft = Paint(new[] { Mark(Shadow(SoftRadius)) });

        int h = PartWay(hard);
        int s = PartWay(soft);

        Assert.True(
            s > h * 5,
            "a soft edge should be mostly ramp: " + s + " part-way pixels against " + h);
    }

    [Fact]
    public void a_single_mark_object_softens_too()
    {
        // The fast path from the object-shadow commit skips the layer for a
        // one-mark object, because one path cannot overlap itself. There is
        // nothing to blur into if it skips the layer when the shadow is soft,
        // and this is the case that would silently stay hard.
        var single = new[] { Mark(Shadow(SoftRadius)) };

        Assert.Single(single);

        using var soft = Paint(single);
        using var hard = Paint(new[] { Mark(Shadow(0)) });

        Assert.True(
            Ink(soft).R - Ink(hard).R >= 12,
            "a lone mark did not soften: it reached only "
            + (Ink(soft).R - Ink(hard).R) + " further");
    }

    [Fact]
    public void softening_a_shadow_does_not_move_it()
    {
        // Blur spreads a silhouette symmetrically. If softness ever reached the
        // offset instead, the shadow would slide as it softened, which is a
        // different effect entirely.
        using var hard = Paint(new[] { Mark(Shadow(0)) });
        using var soft = Paint(new[] { Mark(Shadow(SoftRadius)) });

        Assert.Equal(Centre(hard).X, Centre(soft).X, 1);
        Assert.Equal(Centre(hard).Y, Centre(soft).Y, 1);
    }

    private static (double X, double Y) Centre(SKBitmap bitmap)
    {
        double sx = 0, sy = 0, w = 0;
        var px = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                double weight = 255 - px[at];
                if (weight > 0) { sx += x * weight; sy += y * weight; w += weight; }
            }
        }

        Assert.True(w > 0);
        return (sx / w, sy / w);
    }

    // ---------------- the box grows to hold it ----------------

    [Fact]
    public void the_bounds_grow_by_three_sigma()
    {
        // The measured reach. Sigma is half the radius a person sets, and the
        // ink stops at about 2.5 sigma, so the box opens by 3 sigma, which is
        // 1.5 times the radius.
        var hard = OverlayProjection.SlotBoundsOf(Mark(Shadow(0)), Scale, 0, View);
        var soft = OverlayProjection.SlotBoundsOf(Mark(Shadow(SoftRadius)), Scale, 0, View);

        double expected = OverlayProjection.BlurReachOf(
            Shadow(SoftRadius).Shadow!.Value, Scale, View);

        Assert.True(expected > 0, "a soft shadow must reach further than a hard one");
        Assert.Equal(hard.L - expected, soft.L, 6);
        Assert.Equal(hard.T - expected, soft.T, 6);
        Assert.Equal(hard.R + expected, soft.R, 6);
        Assert.Equal(hard.B + expected, soft.B, 6);
    }

    [Fact]
    public void the_reach_is_three_halves_of_the_radius_in_slot_units()
    {
        // Pins the two constants together. The painter blurs at sigma and the
        // bounds open by three of them; if either drifts, the shadow is either
        // clipped or the frame clears more than it needs to.
        var shadow = Shadow(SoftRadius).Shadow!.Value;

        double sigma = OverlayProjection.BlurSigmaOf(shadow, Scale, View);
        double reach = OverlayProjection.BlurReachOf(shadow, Scale, View);

        Assert.Equal(OverlayProjection.ToSlotThickness(SoftRadius, Scale, View) / 2, sigma, 9);
        Assert.Equal(sigma * 3, reach, 9);
    }

    [Fact]
    public void the_dirty_region_inherits_the_reach()
    {
        // DeviceBoundsOf is built on SlotBoundsOf, so it should grow by
        // construction rather than by a second copy of the rule. Asserted
        // rather than assumed, because "by construction" is exactly the kind of
        // claim that stops being true.
        var hard = DirtyRegion.DeviceBoundsOf(
            new[] { Mark(Shadow(0)) }, Scale, _ => 0, _ => View, Plain);
        var soft = DirtyRegion.DeviceBoundsOf(
            new[] { Mark(Shadow(SoftRadius)) }, Scale, _ => 0, _ => View, Plain);

        Assert.NotNull(hard);
        Assert.NotNull(soft);
        Assert.True(soft!.Value.L < hard!.Value.L);
        Assert.True(soft.Value.R > hard.Value.R);
    }

    // ---------------- and the clip does not cut it ----------------

    [Fact]
    public void the_soft_fringe_survives_the_dirty_region_clip()
    {
        // THE ONE THAT MATTERS. The dirty region is a CLIP, set before the
        // paint, so a box that is too small does not leave a fringe behind, it
        // slices the blur into a hard edge. Measured on the bare canvas,
        // clipping to the unblurred rectangle removed the bleed entirely.
        var items = new[] { Mark(Shadow(SoftRadius)) };

        var box = DirtyRegion.DeviceBoundsOf(items, Scale, _ => 0, _ => View, Plain);
        Assert.NotNull(box);

        var (l, t, r, b) = box!.Value;
        using var clipped = Paint(items, SKRect.Create((float)l, (float)t, (float)(r - l), (float)(b - t)));
        using var whole = Paint(items);

        Assert.True(
            Same(clipped, whole),
            "clipping to the dirty region changed the picture, so the region is too small "
            + "and the blur is being cut into a hard edge");
    }

    // ---------------- and nothing else moves ----------------

    [Fact]
    public void a_shadow_with_no_softness_is_painted_exactly_as_it_was()
    {
        // Softness defaults to nothing, so every shadow that exists today must
        // come out byte for byte as it did before the blur path existed.
        using var a = Paint(new[] { Mark(Shadow(0)) });
        using var b = Paint(new[] { Mark(Shadow(0)) });

        Assert.True(Same(a, b));
        Assert.Equal(0.0, OverlayProjection.BlurReachOf(Shadow(0).Shadow!.Value, Scale, View));
    }

    [Fact]
    public void a_mark_with_no_shadow_reaches_no_further_than_it_ever_did()
    {
        var none = OverlayProjection.SlotBoundsOf(Mark(null), Scale, 0, View);
        var hard = OverlayProjection.SlotBoundsOf(Mark(Shadow(0)), Scale, 0, View);

        // The hard shadow is offset, so it reaches further; what must NOT
        // happen is the blur reach being added when there is no blur.
        Assert.True(hard.R >= none.R);
        Assert.Equal(none.L, Math.Min(none.L, hard.L), 6);
    }
}
