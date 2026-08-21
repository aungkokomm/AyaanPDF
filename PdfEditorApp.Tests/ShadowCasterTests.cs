using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// WHAT CASTS THE SHADOW.
///
/// A shadow is the silhouette of the thing casting it. Held up to a light, a
/// rectangle drawn as four lines does not throw four lines: it throws a solid
/// rectangle, because the thing is a card and not a wire frame.
///
/// The renderer used to cast whatever mark the shape was drawn with, so an
/// unfilled rectangle threw an unfilled rectangle. The report was that the
/// result "looks like an offset duplicate of the shape rather than a shadow",
/// which is exactly what an outline offset by a few points is.
///
/// The rule cannot be "always fill", because a line encloses nothing and
/// filling it would leave no shadow at all. It is: a mark that comes back to
/// where it started encloses an area, and an area is what casts a silhouette.
/// The same rule runs in render_core, so the preview and the saved file agree
/// about what a shadow is.
/// </summary>
public class ShadowCasterTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, ContentH, 0, Scale);

    /// <summary>Opaque blue, so the shape is never mistaken for its grey shadow.</summary>
    private const string ShapeBlue = "#FF0000FF";

    /// <summary>A half-alpha black shadow, thrown by exactly (dx, dy).</summary>
    private static ShapeEffects Shadow(double dx, double dy) =>
        new(new DropShadow(
            Math.Atan2(dy, -dx) * 180.0 / Math.PI,
            Math.Sqrt((dx * dx) + (dy * dy)),
            new RenderColor(0x80, 0, 0, 0)));

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

    /// <summary>The pixel at a NORMALIZED page position, which is where the
    /// test can name a place without doing the projection by hand.</summary>
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

    // ---------------- an enclosed shape casts a silhouette ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.RoundedRectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public void an_unfilled_shape_still_casts_a_solid_shadow(ShapeKind kind)
    {
        // The shape sits at 0.20-0.50 and the shadow is thrown clear of it, to
        // 0.55-0.85, so the two never overlap and the middle of the shadow can
        // only be the shadow.
        var bitmap = Paint(Items(kind, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        Assert.True(
            IsShadow(At(bitmap, 0.70, 0.35)),
            "the middle of the shadow is empty: it is still tracing the outline " +
            "instead of filling the silhouette, which reads as an offset copy");

        // And the shape it was cast by is still hollow. Filling the SHADOW must
        // not have filled the shape.
        Assert.True(
            IsPage(At(bitmap, 0.35, 0.35)),
            "the shape itself got filled in");
    }

    [Fact]
    public void the_silhouette_reaches_the_shapes_own_edges()
    {
        // Not merely non-empty: the same size as the thing casting it. A
        // silhouette that stopped short of the outline would be a smaller
        // shape, and the stroke is part of what blocks the light.
        var bitmap = Paint(Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        foreach (double ny in new[] { 0.21, 0.35, 0.49 })
        {
            Assert.True(IsShadow(At(bitmap, 0.56, ny)), $"the left edge is missing at {ny}");
            Assert.True(IsShadow(At(bitmap, 0.84, ny)), $"the right edge is missing at {ny}");
        }

        Assert.True(IsPage(At(bitmap, 0.70, 0.12)), "the shadow spread above the shape");
        Assert.True(IsPage(At(bitmap, 0.70, 0.58)), "the shadow spread below the shape");
    }

    [Fact]
    public void the_silhouette_includes_the_stroke_that_blocked_the_light()
    {
        // The outline is part of the card, so the silhouette is the fill PLUS
        // the stroke, and reaches half a stroke width beyond the shape's edge.
        // Drawn at a fat weight so the band is wide enough to sample: 0.04
        // normalized puts the silhouette's edge at 0.53, a clear 0.02 outside
        // the fill's own 0.55.
        var items = ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50), ShapeBlue, 0.04)
                {
                    Effects = Shadow(0.35, 0),
                },
            });

        var bitmap = Paint(items);

        Assert.True(
            IsShadow(At(bitmap, 0.54, 0.35)),
            "the silhouette stops at the fill: the stroke that blocked the light is missing");
        Assert.True(
            IsPage(At(bitmap, 0.52, 0.35)),
            "the silhouette reaches further than the stroke does");
    }

    [Fact]
    public void one_tone_throughout_and_not_two()
    {
        // Filling and stroking the silhouette separately would lay the stroke's
        // alpha over the fill's and leave the border darker than the middle: a
        // shadow with an outline drawn round it. It is one mark, one tone.
        var bitmap = Paint(Items(ShapeKind.Rectangle, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        var edge = At(bitmap, 0.55, 0.35);
        var middle = At(bitmap, 0.70, 0.35);

        Assert.True(IsShadow(edge) && IsShadow(middle), "the shadow is not where it was expected");
        Assert.True(
            Math.Abs(edge.R - middle.R) <= 2,
            $"the border came out at {edge.R} against the middle's {middle.R}: " +
            "the silhouette is being composited twice");
    }

    // ---------------- and an open one casts its stroke ----------------

    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void a_shape_that_encloses_nothing_still_casts_its_stroke(ShapeKind kind)
    {
        // The other side of the rule, and the reason it is not "always fill":
        // a line has no interior, so filling it would produce nothing at all.
        var bitmap = Paint(Items(kind, 0.20, 0.20, 0.50, 0.50, Shadow(0.35, 0)));

        Assert.True(
            IsShadow(At(bitmap, 0.605, 0.255)),
            $"a {kind} lost its shadow entirely");

        // Its bounding box does not fill in. A diagonal line whose box was
        // filled would blot out a third of the page.
        Assert.True(
            IsPage(At(bitmap, 0.80, 0.25)),
            $"a {kind} cast the whole of its bounding box");
    }

    // ---------------- the rule itself ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle, true)]
    [InlineData(ShapeKind.RoundedRectangle, true)]
    [InlineData(ShapeKind.Ellipse, true)]
    [InlineData(ShapeKind.Line, false)]
    [InlineData(ShapeKind.Arrow, false)]
    public void what_encloses_an_area_is_decided_by_the_geometry(ShapeKind kind, bool encloses)
    {
        // Read off the points rather than the kind, so it stays true for
        // whatever is added next without anyone remembering to update a list.
        // The arrow here is its SHAFT, which encloses nothing; its head is a
        // filled triangle already.
        var items = Items(kind, 0.20, 0.20, 0.50, 0.50, Shadow(0.1, 0.1));

        Assert.Equal(encloses, items[0].EnclosesAnArea);
    }

    [Fact]
    public void an_arrows_head_is_an_area_and_its_shaft_is_not()
    {
        var items = Items(ShapeKind.Arrow, 0.20, 0.20, 0.50, 0.50, Shadow(0.1, 0.1));

        Assert.Equal(2, items.Count);
        Assert.False(items[0].EnclosesAnArea, "the shaft is a line");
        Assert.True(items[1].EnclosesAnArea, "the head is a triangle");
    }
}
