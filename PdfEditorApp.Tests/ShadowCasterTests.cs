using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// WHAT CASTS THE SHADOW, and what the shadow is made of.
///
/// The shadow is produced by an SKImageFilter from the object itself, so it is
/// derived from the object's own ALPHA. Nothing decides which marks cast a
/// solid shape and which cast an outline, because the alpha already says: a
/// hollow rectangle is transparent in the middle, so it casts a hollow shadow.
///
/// A rule used to stand here that filled every closed shape, and it made a
/// hollow rectangle cast a solid grey slab. That rule is gone in both
/// renderers.
/// </summary>
public class ShadowCasterTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, ContentH, 0, Scale);

    private const string ShapeBlue = "#FF0000FF";

    /// <summary>A half-alpha black shadow, thrown by exactly (dx, dy).</summary>
    private static ShapeEffects Shadow(double dx, double dy, double softness = 0) =>
        new(new DropShadow(
            Math.Atan2(dy, -dx) * 180.0 / Math.PI,
            Math.Sqrt((dx * dx) + (dy * dy)),
            new RenderColor(0x80, 0, 0, 0),
            softness));

    private static IReadOnlyList<ShapeRenderItem> Items(
        ShapeKind kind, double x1, double y1, double x2, double y2, ShapeEffects effects) =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(0, new ShapeDraft(kind, x1, y1, x2, y2), ShapeBlue, 0.006)
                {
                    Effects = effects,
                },
            });

    private static SKBitmap Paint(IReadOnlyList<ShapeRenderItem> items)
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => View, Plain);
        return bitmap;
    }

    private static (byte R, byte G, byte B) At(SKBitmap bitmap, double nx, double ny)
    {
        int x = (int)Math.Round(nx * Scale);
        int y = (int)Math.Round(ny * Scale);
        var pixels = bitmap.GetPixelSpan();
        int at = (y * bitmap.RowBytes) + (x * 4);
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    private static bool IsShadow((byte R, byte G, byte B) p) =>
        p.R == p.G && p.G == p.B && p.R != 255;

    private static bool IsPage((byte R, byte G, byte B) p) =>
        p is { R: 255, G: 255, B: 255 };

    /// <summary>Every pixel darker than the page, as a count.</summary>
    private static int InkCount(SKBitmap bitmap)
    {
        int count = 0;
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255) { count++; }
        }

        return count;
    }

    // ---------------- the alpha rule ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.RoundedRectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public void a_hollow_shape_casts_a_hollow_shadow(ShapeKind kind)
    {
        // The shape sits at 0.20-0.50 and the shadow is thrown clear of it, so
        // the middle of the shadow can only be the shadow.
        var bitmap = Paint(Items(kind, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        Assert.True(
            IsPage(At(bitmap, 0.70, 0.35)),
            "the middle of the shadow is filled in: something is still casting a " +
            "silhouette instead of reading the object's alpha");

        // Its outline is there, though, which is what a hollow shape throws.
        Assert.True(IsShadow(At(bitmap, 0.55, 0.35)), "the left edge of the shadow is missing");
        Assert.True(IsShadow(At(bitmap, 0.85, 0.35)), "the right edge of the shadow is missing");
    }

    [Fact]
    public void a_filled_shape_casts_a_filled_shadow()
    {
        // The other side of the same rule, and the reason it is not "never
        // fill": a solid shape blocks the light across its whole area.
        var items = Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0));
        var filled = items.Select(i => i with { Style = RenderStyle.Filled }).ToList();

        var bitmap = Paint(filled);

        Assert.True(
            IsShadow(At(bitmap, 0.70, 0.35)),
            "a solid shape cast a hollow shadow");
    }

    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void a_shape_that_encloses_nothing_still_casts_its_stroke(ShapeKind kind)
    {
        var bitmap = Paint(Items(kind, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        Assert.True(IsShadow(At(bitmap, 0.605, 0.255)), $"a {kind} lost its shadow entirely");
        Assert.True(IsPage(At(bitmap, 0.80, 0.25)), $"a {kind} cast the whole of its bounding box");
    }

    [Fact]
    public void one_tone_throughout_and_not_two()
    {
        // An arrow is two marks. They are unioned inside one layer before the
        // filter runs, so the overlap is not composited twice and there is no
        // seam or dark patch where the head meets the shaft.
        var bitmap = Paint(Items(ShapeKind.Arrow, 0.20, 0.30, 0.60, 0.30, Shadow(0, 0.18)));

        var greys = new List<int>();
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] == pixels[i + 1] && pixels[i + 1] == pixels[i + 2] && pixels[i] != 255)
            {
                greys.Add(pixels[i]);
            }
        }

        Assert.True(greys.Count > 500, "the arrow cast no shadow worth measuring");

        // Half-alpha black over white is 127. A second layer over the first
        // would be 63, so anything approaching that is the shaft's shadow and
        // the head's shadow compositing against each other. Antialiasing only
        // makes edge pixels LIGHTER, so the darkest pixel is the test.
        Assert.True(
            greys.Min() >= 120,
            $"the darkest shadow pixel is {greys.Min()}, not the 127 a single " +
            "layer gives: the arrow's two marks are shadowed separately");
    }

    // ---------------- softness actually blurs ----------------

    [Fact]
    public void softness_changes_what_is_drawn()
    {
        // The complaint that started this: a blur control that changed nothing.
        var hard = Paint(Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.30, 0)));
        var soft = Paint(Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.30, 0, 0.02)));

        Assert.True(
            InkCount(soft) > InkCount(hard) * 1.2,
            $"a blurred shadow covered {InkCount(soft)} pixels against the hard one's " +
            $"{InkCount(hard)}: the blur is not reaching the picture");
    }

    [Fact]
    public void more_softness_reaches_further()
    {
        int Reach(double softness)
        {
            var bitmap = Paint(
                Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.30, 0, softness)));

            // How far right the ink gets, which is where the shadow's own edge
            // fades out.
            int furthest = 0;
            var pixels = bitmap.GetPixelSpan();
            for (int y = 0; y < Surface; y++)
            {
                for (int x = 0; x < Surface; x++)
                {
                    int at = (y * bitmap.RowBytes) + (x * 4);
                    if (pixels[at] != 255) { furthest = Math.Max(furthest, x); }
                }
            }

            return furthest;
        }

        int small = Reach(0.005);
        int large = Reach(0.02);

        Assert.True(large > small + 5, $"blur 0.02 reached {large}, blur 0.005 reached {small}");
    }

    [Fact]
    public void the_soft_fringe_is_not_clipped_by_the_layer()
    {
        // The layer is bounded for speed, and a bound that forgot the blur
        // would cut the fringe off in a straight line. Sampled just inside the
        // reach on the side AWAY from the shadow, where only the blur can put
        // ink.
        var soft = Shadow(0.30, 0, 0.02);
        var items = Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, soft);

        var bitmap = Paint(items);
        double reach = OverlayProjection.BlurReachOf(soft.Shadow!.Value, Scale, View) / Scale;

        // The shadow's left edge is at 0.50, and the blur puts ink to its left.
        Assert.True(
            IsShadow(At(bitmap, 0.50 - (reach * 0.4), 0.35)),
            "the fringe on the far side of the shadow is missing");
    }
}
