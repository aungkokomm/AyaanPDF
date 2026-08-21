using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A shadow belongs to the OBJECT, not to each mark the object is drawn from.
///
/// An arrow is two render items, a stroked shaft and a filled head, emitted one
/// after the other. Shadowing each of them separately is wrong twice over:
///
/// 1. THE SHADOWS COMPOSITE WITH EACH OTHER. Where the shaft's shadow and the
///    head's shadow overlap, a translucent shadow is laid over a translucent
///    shadow and the overlap comes out darker. One object casts one shadow, of
///    one tone.
/// 2. THE ORDER IS WRONG. Painted per item the sequence runs shadow, shaft,
///    shadow, head, so the head's shadow lands ON TOP of the shaft. A shadow
///    belongs under the whole object, not under half of it.
///
/// Both were invisible while the only shadow available was hard and opaque: an
/// opaque shadow hides its own overlap, and the head's shadow falls clear of the
/// shaft unless it is cast back along the arrow. Neither stays hidden once the
/// alpha comes down, which is the first thing a person will do.
///
/// These paint through the real emit path rather than hand-built items, so the
/// arrow under test is the arrow the app draws.
/// </summary>
public class ObjectShadowTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, ContentH, 0, Scale);

    /// <summary>Opaque blue, so the shape is never mistaken for its own grey shadow.</summary>
    private const string ShapeBlue = "#FF0000FF";

    /// <summary>
    /// A shadow at half alpha, in black, so single coverage and double coverage
    /// are far apart and easy to tell apart: over white, one layer gives about
    /// 127 and two give about 63.
    /// </summary>
    private static ShapeEffects Shadow(double dx, double dy, byte alpha = 0x80) =>
        new(new DropShadow(
            Math.Atan2(dy, -dx) * 180.0 / Math.PI,
            Math.Sqrt((dx * dx) + (dy * dy)),
            new RenderColor(alpha, 0, 0, 0)));

    private static ShapeAnnotation Shape(
        ShapeKind kind, double x1, double y1, double x2, double y2, ShapeEffects? effects) =>
        new(0, new ShapeDraft(kind, x1, y1, x2, y2), ShapeBlue, 0.02) { Effects = effects };

    /// <summary>A horizontal arrow, pointing right, as the app emits one.</summary>
    private static IReadOnlyList<ShapeRenderItem> Arrow(ShapeEffects? effects) =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[] { Shape(ShapeKind.Arrow, 0.25, 0.30, 0.75, 0.30, effects) });

    private static SKBitmap Paint(IReadOnlyList<ShapeRenderItem> items)
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => View, Plain);
        return bitmap;
    }

    /// <summary>
    /// Every pixel of the shadow, as its grey level. The shape is blue and the
    /// page is white, so a pixel with equal channels below 255 is shadow and
    /// nothing else can be.
    /// </summary>
    private static List<int> ShadowGreys(SKBitmap bitmap)
    {
        var greys = new List<int>();
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                byte r = pixels[at];
                byte g = pixels[at + 1];
                byte b = pixels[at + 2];
                if (r == g && g == b && r != 255) { greys.Add(r); }
            }
        }

        return greys;
    }

    /// <summary>Every pixel that is decisively blue: the shape itself.</summary>
    private static int BlueCount(SKBitmap bitmap)
    {
        int count = 0;
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                if (pixels[at + 2] > pixels[at] + 40) { count++; }
            }
        }

        return count;
    }

    // ---------------- the arrow really is two marks ----------------

    [Fact]
    public void an_arrow_is_emitted_as_two_render_items()
    {
        // Everything below is only meaningful because of this. If an arrow ever
        // became one item these tests would pass without proving anything.
        var items = Arrow(null);

        Assert.Equal(2, items.Count);
        Assert.Equal(RenderStyle.Stroked, items[0].Style);
        Assert.Equal(RenderStyle.Filled, items[1].Style);
    }

    // ---------------- one object, one shadow, one tone ----------------

    [Fact]
    public void an_arrows_shadow_is_one_tone_and_not_darker_where_its_parts_overlap()
    {
        // THE FALSIFICATION. A per-item shadow lays the head's shadow over the
        // shaft's, and the overlap composites to roughly 63 rather than 127.
        // Antialiasing only ever makes an edge LIGHTER than full coverage, so
        // anything materially darker than one layer is two layers.
        using var bitmap = Paint(Arrow(Shadow(-0.06, 0.05)));
        var greys = ShadowGreys(bitmap);

        Assert.NotEmpty(greys);
        Assert.True(
            greys.Min() >= 120,
            "the darkest shadow pixel was " + greys.Min() + ", and one layer of a half-alpha "
            + "black shadow over white is about 127. Darker than that is the shadow of one "
            + "part of the arrow composited over the shadow of another.");
    }

    [Fact]
    public void the_whole_object_casts_the_shadow_and_not_just_its_shaft()
    {
        // The other half of the bargain. Fixing the overlap by shadowing only
        // the first item would give one tone and an arrow whose point floats
        // free of its own shadow, so the object's shadow has to cover at least
        // what the shaft alone would cast.
        var full = Arrow(Shadow(-0.06, 0.05));
        var shaftOnly = new[] { full[0], full[1] with { Effects = null } };

        using var whole = Paint(full);
        using var partial = Paint(shaftOnly);

        Assert.NotEmpty(ShadowGreys(whole));
        Assert.True(
            ShadowGreys(whole).Count >= ShadowGreys(partial).Count,
            "the object's shadow must cover at least what the shaft alone casts");
    }

    // ---------------- the shadow goes under the whole object ----------------

    [Fact]
    public void a_shadow_cast_back_along_the_arrow_never_lands_on_top_of_it()
    {
        // The z-order half. The offset points back down the shaft, so the
        // head's shadow falls squarely on the shaft. Painted per item that
        // shadow is drawn AFTER the shaft and covers it, and the shape loses
        // blue pixels to it.
        using var plain = Paint(Arrow(null));
        using var shadowed = Paint(Arrow(Shadow(-0.12, 0, alpha: 0xFF)));

        Assert.Equal(BlueCount(plain), BlueCount(shadowed));
    }

    // ---------------- nothing else changes ----------------

    [Fact]
    public void an_object_with_no_shadow_is_painted_exactly_as_it_was()
    {
        // The guarantee everything else rests on: grouping items into objects
        // must not disturb a frame that has no effects in it at all.
        using var before = Paint(Arrow(null));
        using var after = Paint(Arrow(new ShapeEffects()));

        Assert.Equal(BlueCount(before), BlueCount(after));
        Assert.Empty(ShadowGreys(after));
    }

    [Fact]
    public void a_single_item_object_still_casts_its_own_shadow()
    {
        // A rectangle is one item, which is the case the grouping must not
        // break while it is busy fixing the two-item one.
        var rectangle = ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[] { Shape(ShapeKind.Rectangle, 0.25, 0.30, 0.65, 0.55, Shadow(-0.06, 0.05)) });

        Assert.Single(rectangle);

        using var bitmap = Paint(rectangle);
        var greys = ShadowGreys(bitmap);

        Assert.NotEmpty(greys);
        Assert.True(greys.Min() >= 120, "a single mark cast a doubled shadow: " + greys.Min());
    }

    [Fact]
    public void two_separate_arrows_cast_two_separate_shadows()
    {
        // Grouping must not run two objects together. Placed so their shadows
        // cannot touch, and each must still be a full-strength single tone.
        var arrows = ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                Shape(ShapeKind.Arrow, 0.25, 0.20, 0.60, 0.20, Shadow(-0.04, 0.04)),
                Shape(ShapeKind.Arrow, 0.25, 0.70, 0.60, 0.70, Shadow(-0.04, 0.04)),
            });

        Assert.Equal(4, arrows.Count);

        using var bitmap = Paint(arrows);
        var greys = ShadowGreys(bitmap);

        Assert.NotEmpty(greys);
        Assert.True(greys.Min() >= 120, "two objects merged into one layer: " + greys.Min());
    }
}
