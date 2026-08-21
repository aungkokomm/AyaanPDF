using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The first effect: a hard drop shadow, in the Skia renderer.
///
/// A shadow is the item's own geometry moved and recoloured, which is a small
/// enough idea that the interesting questions are all about where it ENDS UP
/// rather than what it looks like. The offset is normalized and page-local, so
/// it has to survive everything a page-local length has to survive: a turned
/// page, a zoom, a display scale, and a page halfway down a stack.
///
/// Two of these tests are not about pixels at all. A shadow is ink outside the
/// box the shape used to occupy, and two other pieces of machinery read that
/// box: the culler decides what is worth drawing from it, and the dirty region
/// decides what to clear from it. Both were written before shadows existed. If
/// the box does not grow, a shadow is culled while it is visible and left
/// behind while it is on screen, and neither shows up in a picture of one frame.
/// </summary>
public class DropShadowTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const double Stroke = 0.008;
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly RenderColor Black = new(0xFF, 0, 0, 0);

    private static PageTransform View(int rotation) =>
        PageTransform.For(Scale, ContentH, rotation, Scale);

    /// <summary>
    /// A shadow stated as the OFFSET these tests think in, converted into the
    /// angle and distance the model stores.
    ///
    /// The model keeps where the light is and how far the shadow falls, because
    /// an angle cannot be recovered from an offset of no length. These are
    /// renderer tests and a renderer only ever sees the offset, so they say
    /// which offset they expect and this turns it back. Every case below has a
    /// real length, so the conversion loses nothing.
    ///
    /// The inverse of DropShadow.OffsetX/OffsetY: the shadow falls opposite the
    /// light, and y runs down the page.
    /// </summary>
    private static ShapeEffects Shadow(double dx, double dy, byte alpha = 0xFF) =>
        new(new DropShadow(
            Math.Atan2(dy, -dx) * 180.0 / Math.PI,
            Math.Sqrt((dx * dx) + (dy * dy)),
            new RenderColor(alpha, 0, 0, 0)));

    private static ShapeRenderItem Mark(
        double l, double t, ShapeEffects? effects = null, int page = 0) =>
        new(page,
            [(l, t), (l + 0.2, t), (l + 0.2, t + 0.16), (l, t + 0.16), (l, t)],
            Black, Stroke, RenderStyle.Stroked, null, effects);

    private static SKBitmap Paint(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view,
        ViewportProjection? projection = null, Func<int, double>? pageTop = null)
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.PaintViewport(
            canvas, items, Scale, pageTop ?? (_ => 0), _ => view, projection ?? Plain);
        return bitmap;
    }

    /// <summary>Every pixel that is not pure white.</summary>
    private static HashSet<(int X, int Y)> Lit(SKBitmap bitmap)
    {
        var lit = new HashSet<(int, int)>();
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                if (pixels[at] != 255 || pixels[at + 1] != 255 || pixels[at + 2] != 255)
                {
                    lit.Add((x, y));
                }
            }
        }

        return lit;
    }

    /// <summary>Every pixel that is a shade of grey: the black shadow, and
    /// nothing the blue shape painted.</summary>
    private static HashSet<(int X, int Y)> Grey(SKBitmap bitmap)
    {
        var grey = new HashSet<(int, int)>();
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int at = (y * stride) + (x * 4);
                if (pixels[at] != 255 && pixels[at] == pixels[at + 1]
                    && pixels[at + 1] == pixels[at + 2])
                {
                    grey.Add((x, y));
                }
            }
        }

        return grey;
    }

    private static (int L, int T, int R, int B) Box(HashSet<(int X, int Y)> lit)
    {
        Assert.NotEmpty(lit);
        return (lit.Min(p => p.X), lit.Min(p => p.Y), lit.Max(p => p.X), lit.Max(p => p.Y));
    }

    // ---------------- nothing changes when there is no effect ----------------

    [Fact]
    public void a_mark_with_no_effects_is_painted_exactly_as_it_was_before()
    {
        // The guarantee the whole design rests on, and the reason the parity
        // capture is still worth something: null effects means the identical
        // frame, pixel for pixel.
        using var plain = Paint([Mark(0.2, 0.2)], View(0));
        using var empty = Paint([Mark(0.2, 0.2, new ShapeEffects())], View(0));

        Assert.Equal(Lit(plain), Lit(empty));
    }

    [Fact]
    public void an_effects_object_with_no_effect_in_it_knows_it_is_empty()
    {
        Assert.True(new ShapeEffects().IsEmpty);
        Assert.False(Shadow(0.02, 0.02).IsEmpty);
    }

    // ---------------- translation ----------------

    [Fact]
    public void a_shadow_puts_ink_where_the_shape_alone_puts_none()
    {
        using var plain = Paint([Mark(0.2, 0.2)], View(0));
        using var shadowed = Paint([Mark(0.2, 0.2, Shadow(0.05, 0.04))], View(0));

        var before = Box(Lit(plain));
        var after = Box(Lit(shadowed));

        // Down and to the right, by the offset, and not up or left at all.
        Assert.Equal(before.L, after.L);
        Assert.Equal(before.T, after.T);
        Assert.True(after.R > before.R, "the shadow must extend the mark to the right");
        Assert.True(after.B > before.B, "the shadow must extend the mark downward");
    }

    [Theory]
    [InlineData(0.05, 0.0)]
    [InlineData(0.0, 0.05)]
    [InlineData(-0.05, -0.04)]
    [InlineData(0.03, -0.06)]
    public void the_shadow_lands_exactly_the_offset_away(double dx, double dy)
    {
        // The arithmetic, on all four diagonals so a sign dropped on either axis
        // fails. The shadow's box is the shape's box moved by the offset in slot
        // DIPs, which at zoom 1 and scale 1 is the offset times the overlay
        // scale.
        //
        // The shape is painted BLUE and the shadow read off the grey pixels.
        // This used to hide the shape by making it transparent, which no longer
        // photographs the shadow on its own: the shadow is derived from the
        // shape's alpha, so a fully transparent shape casts none at all. That is
        // correct, since nothing invisible blocks any light, but it means the
        // two have to be told apart by colour instead.
        var blue = new RenderColor(0xFF, 0, 0, 0xFF);

        using var plain = Paint([Mark(0.3, 0.3) with { Color = blue }], View(0));
        using var both = Paint(
            [Mark(0.3, 0.3, Shadow(dx, dy)) with { Color = blue }], View(0));

        var shape = Box(Lit(plain));
        var shadow = Box(Grey(both));

        Assert.Equal(shape.L + (dx * Scale), shadow.L, tolerance: 2.0);
        Assert.Equal(shape.T + (dy * Scale), shadow.T, tolerance: 2.0);
    }

    // ---------------- opacity ----------------

    [Fact]
    public void a_transparent_shadow_paints_nothing()
    {
        using var plain = Paint([Mark(0.2, 0.2)], View(0));
        using var clear = Paint([Mark(0.2, 0.2, Shadow(0.05, 0.04, alpha: 0))], View(0));

        Assert.Equal(Lit(plain), Lit(clear));
    }

    [Fact]
    public void a_fainter_shadow_is_lighter_and_covers_the_same_ground()
    {
        // Opacity is the colour's alpha and nothing else, so a half-alpha shadow
        // must cover the same pixels and simply weigh less on them. Testing the
        // extent alone would pass for a shadow whose alpha was ignored.
        using var solid = Paint([Mark(0.2, 0.2, Shadow(0.06, 0.05, 0xFF))], View(0));
        using var faint = Paint([Mark(0.2, 0.2, Shadow(0.06, 0.05, 0x40))], View(0));

        Assert.Equal(Box(Lit(solid)), Box(Lit(faint)));
        Assert.True(Darkness(faint) < Darkness(solid),
            "a lower alpha must leave lighter pixels");
        Assert.True(Darkness(faint) > 0, "a visible alpha must leave some ink");
    }

    /// <summary>Total ink laid down, as distance from white summed over the surface.</summary>
    private static long Darkness(SKBitmap bitmap)
    {
        long sum = 0;
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                sum += 255 - pixels[(y * stride) + (x * 4)];
            }
        }

        return sum;
    }

    // ---------------- rotation ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_shadow_turns_with_the_page_it_is_on(int rotation)
    {
        // The offset is page-local, so on a turned page the shadow goes where
        // the PAGE says down-and-right is, not where the screen does. Stated as
        // a property that holds at every rotation: the shadow sits exactly where
        // the same shape shifted by the offset would sit.
        var view = View(rotation);

        using var shadowed = Paint([Mark(0.3, 0.3, Shadow(0.06, 0.05))], View(rotation));
        using var shifted = Paint([Mark(0.36, 0.35)], view);
        using var plain = Paint([Mark(0.3, 0.3)], view);

        var whole = Box(Lit(shadowed));
        var union = Union(Box(Lit(shifted)), Box(Lit(plain)));

        Assert.Equal(union.L, whole.L, tolerance: 2.0);
        Assert.Equal(union.T, whole.T, tolerance: 2.0);
        Assert.Equal(union.R, whole.R, tolerance: 2.0);
        Assert.Equal(union.B, whole.B, tolerance: 2.0);
    }

    private static (int L, int T, int R, int B) Union(
        (int L, int T, int R, int B) a, (int L, int T, int R, int B) b) =>
        (Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));

    // ---------------- zoom and display scale ----------------

    [Theory]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(1.5, 1.5)]
    public void the_shadow_scales_with_the_view_like_the_shape_does(double zoom, double device)
    {
        // A normalized offset means the gap between shape and shadow is part of
        // the object, so it grows with the view. An offset that had been fixed
        // in pixels somewhere would keep its size here and the ratio would drop.
        var view = View(0);

        using var one = Paint([Mark(0.2, 0.2, Shadow(0.06, 0.05))], view);
        using var big = Paint(
            [Mark(0.2, 0.2, Shadow(0.06, 0.05))], view,
            new ViewportProjection(zoom, device, 0, 0));

        var small = Box(Lit(one));
        var large = Box(Lit(big));

        double factor = zoom * device;

        Assert.Equal((small.R - small.L) * factor, large.R - large.L, tolerance: 3.0);
        Assert.Equal((small.B - small.T) * factor, large.B - large.T, tolerance: 3.0);
    }

    // ---------------- page boundaries ----------------

    [Fact]
    public void a_shadow_sits_on_its_own_pages_band()
    {
        // Page placement reaches the shadow because it reaches every projected
        // point. A shadow computed outside that chain would stay on page zero
        // while its shape moved down the stack.
        var view = View(0);

        using var first = Paint([Mark(0.2, 0.2, Shadow(0.06, 0.05))], view, Plain, _ => 0);
        using var second = Paint(
            [Mark(0.2, 0.2, Shadow(0.06, 0.05), page: 1)], view, Plain, page => page * 300.0);

        var a = Box(Lit(first));
        var b = Box(Lit(second));

        Assert.Equal(a.L, b.L);
        Assert.Equal(a.T + 300, b.T);
        Assert.Equal(a.B + 300, b.B);
    }

    [Fact]
    public void the_bounds_grow_to_cover_the_shadow()
    {
        // The test that stops the shadow being culled while it is visible and
        // left behind while it is on screen. Both the culler and the dirty
        // region read this one function, and it was written before shadows.
        var view = View(0);
        var plain = Mark(0.3, 0.3);
        var cast = Mark(0.3, 0.3, Shadow(0.06, 0.05));

        var (_, _, r1, b1) = OverlayProjection.SlotBoundsOf(plain, Scale, 0, view);
        var (l2, t2, r2, b2) = OverlayProjection.SlotBoundsOf(cast, Scale, 0, view);
        var (l1, t1, _, _) = OverlayProjection.SlotBoundsOf(plain, Scale, 0, view);

        Assert.Equal(l1, l2, 6);
        Assert.Equal(t1, t2, 6);
        Assert.Equal(r1 + (0.06 * Scale), r2, 6);
        Assert.Equal(b1 + (0.05 * Scale), b2, 6);
    }

    [Fact]
    public void a_shape_off_screen_whose_shadow_is_on_screen_is_not_culled()
    {
        // The consequence of the bounds, through the culler that reads them. The
        // shape's own box misses the window; the shadow reaches into it.
        var view = View(0);
        var window = (0.0, 0.0, 200.0, 200.0);

        var plain = Mark(0.30, 0.30);
        var cast = Mark(0.30, 0.30, Shadow(-0.15, -0.15));

        Assert.Empty(ShapeCulling.Visible([plain], window, Scale, _ => 0, _ => view));
        Assert.Single(ShapeCulling.Visible([cast], window, Scale, _ => 0, _ => view));
    }

    [Fact]
    public void a_moving_shadow_leaves_no_ghost()
    {
        // The dirty region, over two frames, which is the only way this failure
        // appears. If the region did not know about the shadow it would clear
        // the shape's box and leave the shadow's tail behind.
        var view = View(0);
        var tracker = new DirtyRegionTracker();

        using var dragged = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(dragged))
        {
            canvas.Clear(SKColors.White);
            Frame(canvas, tracker, [Mark(0.20, 0.20, Shadow(0.06, 0.05))], view);
            Frame(canvas, tracker, [Mark(0.45, 0.42, Shadow(0.06, 0.05))], view);
        }

        using var fresh = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(fresh))
        {
            canvas.Clear(SKColors.White);
            Frame(canvas, new DirtyRegionTracker(), [Mark(0.45, 0.42, Shadow(0.06, 0.05))], view);
        }

        Assert.Equal(Lit(fresh), Lit(dragged));
    }

    private static void Frame(
        SKCanvas canvas, DirtyRegionTracker tracker,
        IReadOnlyList<ShapeRenderItem> items, PageTransform view)
    {
        var plan = tracker.Plan(items, Scale, _ => 0, _ => view, Plain, Surface, Surface);

        if (plan.Scope != PaintScope.Nothing)
        {
            int saved = canvas.Save();
            canvas.ClipRect(SKRect.Create(
                plan.Rect.L, plan.Rect.T,
                plan.Rect.R - plan.Rect.L, plan.Rect.B - plan.Rect.T));
            canvas.Clear(SKColors.White);
            ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => view, Plain);
            canvas.RestoreToCount(saved);
        }

        tracker.Painted(plan);
    }

    // ---------------- more than one object ----------------

    [Fact]
    public void each_shape_casts_its_own_shadow_and_only_its_own()
    {
        var view = View(0);

        using var both = Paint(
            [Mark(0.10, 0.10, Shadow(0.05, 0.04)), Mark(0.50, 0.50, Shadow(0.05, 0.04))], view);
        using var one = Paint([Mark(0.10, 0.10, Shadow(0.05, 0.04))], view);
        using var other = Paint([Mark(0.50, 0.50, Shadow(0.05, 0.04))], view);

        var union = new HashSet<(int, int)>(Lit(one));
        union.UnionWith(Lit(other));

        Assert.Equal(union, Lit(both));
    }

    [Fact]
    public void a_shadow_can_be_given_to_one_object_and_not_another()
    {
        var view = View(0);

        using var mixed = Paint(
            [Mark(0.10, 0.10, Shadow(0.05, 0.04)), Mark(0.50, 0.50)], view);
        using var cast = Paint([Mark(0.10, 0.10, Shadow(0.05, 0.04))], view);
        using var bare = Paint([Mark(0.50, 0.50)], view);

        var union = new HashSet<(int, int)>(Lit(cast));
        union.UnionWith(Lit(bare));

        Assert.Equal(union, Lit(mixed));
    }

    [Fact]
    public void a_shadow_goes_under_its_own_shape_and_over_the_one_before_it()
    {
        // Paint order. The shadow belongs to its mark, so it goes directly
        // beneath that mark rather than beneath the whole frame; a frame that
        // laid every shadow down first would put the second shape's shadow
        // under the first shape.
        var view = View(0);
        var under = Mark(0.20, 0.20) with { Color = new RenderColor(0xFF, 0, 0, 0xFF) };
        var over = Mark(0.24, 0.24, Shadow(-0.03, -0.03));

        using var painted = Paint([under, over], view);

        // Where the shadow overlaps the first shape, the shadow wins.
        var (l, t, _, _) = OverlayProjection.SlotBoundsOf(over, Scale, 0, view);
        var probe = painted.GetPixel((int)Math.Round(l) + 3, (int)Math.Round(t) + 3);

        Assert.True(probe.Blue < 128, "the later mark's shadow must cover the earlier mark");
    }

    // ---------------- an arrow is two marks describing one object ----------------

    [Fact]
    public void both_halves_of_an_arrow_carry_the_shapes_effects()
    {
        // A shaft and a head, and a shadow under only one of them would be an
        // arrow whose point floats free of its own shadow.
        var effects = Shadow(0.05, 0.04);
        var arrow = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Arrow, 0.2, 0.2, 0.6, 0.5), "#FF000000", Stroke)
        {
            Effects = effects,
        };

        var items = ShapeRenderList.From([], [arrow]);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Same(effects, item.Effects));
    }

    [Fact]
    public void a_shape_without_effects_still_emits_items_without_them()
    {
        var items = ShapeRenderList.From(
            [], [new ShapeAnnotation(
                0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.6, 0.5), "#FF000000", Stroke)]);

        Assert.All(items, item => Assert.Null(item.Effects));
    }

    // ---------------- nothing that is written down has changed ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void an_effect_changes_nothing_that_is_persisted_or_measured(ShapeKind kind)
    {
        // A shape is stored as a tag built field by field in render_core, from
        // the kind, the colour, the width and the draft. An effect is none of
        // those, and this says so by construction: two shapes differing ONLY in
        // their effects agree on every value the tag is written from, and on
        // the geometry and bounds that hit testing and resizing read.
        //
        // Which is also why an effect does NOT survive a save and reload. It is
        // not in the tag, nothing writes it, and a shape read back from a file
        // has none. Persisting it means extending the tag format on both sides
        // of the FFI, deliberately, in its own change.
        var draft = new ShapeDraft(kind, 0.2, 0.2, 0.6, 0.5);
        var plain = new ShapeAnnotation(0, draft, "#FF3B82F6", Stroke);
        var cast = new ShapeAnnotation(0, draft, "#FF3B82F6", Stroke)
        {
            Effects = Shadow(0.05, 0.04),
        };

        Assert.Equal(plain.PageIndex, cast.PageIndex);
        Assert.Equal(plain.ColorHex, cast.ColorHex);
        Assert.Equal(plain.StrokeWidth, cast.StrokeWidth);
        Assert.Equal(plain.Draft, cast.Draft);

        Assert.Equal(plain.Outline, cast.Outline);
        Assert.Equal(plain.Head, cast.Head);
        Assert.Equal(plain.Bounds, cast.Bounds);
    }

    [Fact]
    public void a_shape_read_back_from_a_tag_has_no_effects()
    {
        // The round trip, stated from the reading end: whatever was in memory,
        // what comes back out of a file carries no effect. This is the limit of
        // this commit written as a test rather than only as a comment, so that
        // adding persistence later has something that must be updated.
        // A real tag, in the numeric-kind form render_core writes.
        Assert.True(ShapeTagReader.TryParse(
            "AyaanShape:0:FF0000FF:2.0000:1:1", out var tag));
        Assert.Equal(ShapeKind.Rectangle, tag.Kind);

        var rebuilt = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.6, 0.5), "#FF3B82F6", Stroke);

        Assert.Null(rebuilt.Effects);
    }
}
