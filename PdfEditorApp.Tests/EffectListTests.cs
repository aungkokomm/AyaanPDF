using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The effects on a mark are an ORDERED LIST, not a named slot per effect.
///
/// The slot per effect is what made the second effect expensive. Adding a glow
/// meant a property here, a branch in the painter, a branch in the bounds, a
/// field pair in the core's spec struct, an element in the tag tuple and an
/// override entry point of its own: thirteen files for something that is a drop
/// shadow with the offset set to nothing. A list means the pipeline walks
/// whatever it is given and a new effect is a new value, not a new code path.
///
/// The drop shadow keeps its named view. <see cref="ShapeEffects.Shadow"/> is
/// read out of the list rather than stored beside it, so the two cannot say
/// different things, and every caller that only ever wanted the shadow carries
/// on working.
///
/// These paint with TWO shadows deliberately. Nothing in the app makes two yet,
/// and that is the point: the pipeline has to be generic before there is a
/// second effect to prove it with, so the second effect here is another shadow.
/// </summary>
public class EffectListTests
{
    private const double Scale = 800;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, 1000, 0, Scale);

    private static readonly RenderColor Black = new(0xFF, 0, 0, 0);

    /// <summary>A shadow thrown at <paramref name="angleDeg"/>, as a spec.</summary>
    private static EffectSpec Cast(double angleDeg, RenderColor color) =>
        new(EffectKind.DropShadow, color, Blur: 0, Distance: 0.20, AngleDeg: angleDeg);

    private static SKBitmap Paint(ShapeEffects effects)
    {
        var shape = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Rectangle, 0.25, 0.25, 0.40, 0.40),
            "#FF0000FF", 0.02) { Effects = effects };

        var items = ShapeRenderList.From(Array.Empty<InkStrokeAnnotation>(), new[] { shape });

        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => View, Plain);

        return bitmap;
    }

    /// <summary>The pixel at a normalized page point.</summary>
    private static (byte R, byte G, byte B) At(SKBitmap bitmap, double nx, double ny)
    {
        var pixels = bitmap.GetPixelSpan();
        int at = ((int)(ny * Scale) * bitmap.RowBytes) + ((int)(nx * Scale) * 4);

        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    // ---------------- what the list holds ----------------

    [Fact]
    public void a_drop_shadow_becomes_one_spec_in_the_list()
    {
        var effects = new ShapeEffects(
            new DropShadow(135, 0.05, new RenderColor(0x80, 0x11, 0x22, 0x33), 0.02, 0.01));

        var spec = Assert.Single(effects.Specs);

        Assert.Equal(EffectKind.DropShadow, spec.Kind);
        Assert.Equal(135, spec.AngleDeg);
        Assert.Equal(0.05, spec.Distance);
        Assert.Equal(new RenderColor(0x80, 0x11, 0x22, 0x33), spec.Color);
        Assert.Equal(0.02, spec.Blur);
        Assert.Equal(0.01, spec.Amount);
    }

    [Fact]
    public void the_named_shadow_is_read_back_out_of_the_list()
    {
        // Not stored beside it. A shadow that goes in has to come back out
        // unchanged, or every caller still reading the named view is reading
        // something the renderers no longer draw.
        var shadow = new DropShadow(35, 0.04, new RenderColor(0x99, 0xAA, 0xBB, 0xCC), 0.03, 0.02);

        Assert.Equal(shadow, new ShapeEffects(shadow).Shadow);
    }

    [Fact]
    public void no_effects_is_an_empty_list()
    {
        var none = new ShapeEffects();

        Assert.Empty(none.Specs);
        Assert.True(none.IsEmpty);
        Assert.Null(none.Shadow);
    }

    [Fact]
    public void the_list_keeps_the_order_it_was_given()
    {
        var first = Cast(180, Black);
        var second = Cast(0, new RenderColor(0xFF, 0xFF, 0, 0));

        var effects = new ShapeEffects(first, second);

        Assert.Equal(new[] { first, second }, effects.Specs);
    }

    [Fact]
    public void the_named_shadow_is_the_first_drop_shadow_in_the_list()
    {
        var first = Cast(180, Black);

        var effects = new ShapeEffects(first, Cast(0, new RenderColor(0xFF, 0xFF, 0, 0)));

        Assert.Equal(180, effects.Shadow!.Value.AngleDeg);
        Assert.Equal(first.Color, effects.Shadow!.Value.Color);
    }

    [Fact]
    public void a_spec_throws_its_offset_exactly_where_a_shadow_does()
    {
        // One derivation for both. The angle is where the LIGHT is and the mark
        // falls the other way; two copies of that arithmetic is how a shadow
        // comes to point one way in the preview and the other in the file.
        var shadow = new DropShadow(35, 0.04, Black);
        var spec = new EffectSpec(EffectKind.DropShadow, Black, Distance: 0.04, AngleDeg: 35);

        Assert.Equal(shadow.OffsetX, spec.OffsetX, 12);
        Assert.Equal(shadow.OffsetY, spec.OffsetY, 12);
    }

    // ---------------- and what the painter does with it ----------------

    [Fact]
    public void every_effect_in_the_list_is_painted()
    {
        // The loop is over the list. Painting only the first is the shape this
        // regression would take, and with one effect in the list nothing else
        // would notice.
        using var bitmap = Paint(new ShapeEffects(Cast(180, Black), Cast(0, Black)));

        // ON the shadow's own left edge: a stroked rectangle casts a HOLLOW
        // shadow, so the middle of one is the page and proves nothing.
        var right = At(bitmap, 0.25 + 0.20, 0.325);
        var left = At(bitmap, 0.25 - 0.20, 0.325);

        Assert.True(right is (0, 0, 0), $"the first effect did not paint: {right}");
        Assert.True(left is (0, 0, 0), $"the second effect did not paint: {left}");
    }

    [Fact]
    public void the_effects_are_painted_in_the_order_the_list_gives_them()
    {
        // Two opaque marks in the same place: the one later in the list is the
        // one on top, which is the ordinary rule for everything else the app
        // paints and the reason the list is ordered rather than a set.
        using var bitmap = Paint(new ShapeEffects(
            Cast(180, new RenderColor(0xFF, 0xFF, 0, 0)),
            Cast(180, new RenderColor(0xFF, 0, 0xFF, 0))));

        var (r, g, _) = At(bitmap, 0.25 + 0.20, 0.325);

        Assert.True(g > 200 && r < 60, $"the first effect landed on top of the second: ({r}, {g})");
    }

    [Fact]
    public void a_mark_with_an_empty_list_paints_what_it_always_did()
    {
        using var none = Paint(new ShapeEffects());
        using var empty = Paint(new ShapeEffects(Array.Empty<EffectSpec>()));

        Assert.True(none.Bytes.AsSpan().SequenceEqual(empty.Bytes));
    }
}
