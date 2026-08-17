using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Two frames in a row on one surface, which is the only way the bug this
/// guards against can appear.
///
/// Partial painting works because the bitmap is NOT cleared between frames.
/// That is also how it fails: clear only where the mark is going and the mark
/// stays where it was, so a drag smears instead of moving. One frame can never
/// show it, and every test before this file rendered exactly one.
///
/// So these paint frame one, paint frame two through the tracker's plan on the
/// SAME surface, and then look at where the first mark used to be. Anything
/// still lit there is a ghost, and the assertion says so in those words.
///
/// The layer itself is WinUI and cannot be loaded here, but nothing in the loop
/// under test is: the tracker decides, ShapeSkiaPainter draws, and both live
/// where a test can reach them. What is reproduced here is exactly the sequence
/// SkiaShapeLayer.OnPaintSurface runs.
/// </summary>
public class DirtyRegionGhostTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const double Stroke = 0.008;
    /// <summary>
    /// Big enough to hold the content box at EVERY rotation. The page is 800
    /// by 1000 slot DIPs, so a mark near the top left at 0 degrees lands near
    /// the bottom right at 180, past the edge of anything smaller. At 700 the
    /// first frame of the 180 degree case drew nothing at all, and a ghost test
    /// whose first frame is blank cannot fail for the right reason.
    /// </summary>
    private const int Surface = 1024;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);

    private static PageTransform View(int rotation) =>
        PageTransform.For(Scale, ContentH, rotation, Scale);

    private static IReadOnlyList<ShapeRenderItem> MarkAt(double l, double t, ShapeKind kind) =>
        ShapeRenderList.From(
            [], [new ShapeAnnotation(
                0, new ShapeDraft(kind, l, t, l + 0.18, t + 0.15), "#FF000000", Stroke)]);

    /// <summary>
    /// One frame, painted the way the layer paints it: ask the tracker, clip,
    /// clear the clip, draw, then tell the tracker it happened.
    /// </summary>
    private static PaintScope Frame(
        SKCanvas canvas,
        DirtyRegionTracker tracker,
        IReadOnlyList<ShapeRenderItem> items,
        PageTransform view,
        ViewportProjection projection)
    {
        var plan = tracker.Plan(
            items, Scale, _ => 0, _ => view, projection, Surface, Surface);

        if (plan.Scope == PaintScope.Nothing)
        {
            tracker.Painted(plan);
            return plan.Scope;
        }

        int saved = canvas.Save();
        canvas.ClipRect(SKRect.Create(
            plan.Rect.L, plan.Rect.T,
            plan.Rect.R - plan.Rect.L, plan.Rect.B - plan.Rect.T));
        canvas.Clear(SKColors.White);

        var visible = ShapeCulling.Visible(
            items, projection.VisibleSlotBounds(Surface, Surface, 64),
            Scale, _ => 0, _ => view);

        if (visible.Count > 0)
        {
            ShapeSkiaPainter.PaintViewport(canvas, visible, Scale, _ => 0, _ => view, projection);
        }

        canvas.RestoreToCount(saved);
        tracker.Painted(plan);

        return plan.Scope;
    }

    /// <summary>Every pixel that is not pure white, as a set of coordinates.</summary>
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

    private static SKBitmap NewSurface()
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        return bitmap;
    }

    // ---------------- the ghost ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle, 0)]
    [InlineData(ShapeKind.Rectangle, 90)]
    [InlineData(ShapeKind.Ellipse, 180)]
    [InlineData(ShapeKind.Arrow, 0)]
    [InlineData(ShapeKind.Arrow, 270)]
    [InlineData(ShapeKind.Line, 90)]
    public void a_mark_that_moves_leaves_nothing_behind(ShapeKind kind, int rotation)
    {
        var view = View(rotation);
        var tracker = new DirtyRegionTracker();

        using var bitmap = NewSurface();
        using var canvas = new SKCanvas(bitmap);

        // Frame one, and a record of exactly which pixels it lit.
        Frame(canvas, tracker, MarkAt(0.10, 0.10, kind), view, Plain);
        var first = Lit(bitmap);
        Assert.NotEmpty(first);

        // Frame two, well clear of the first.
        Frame(canvas, tracker, MarkAt(0.55, 0.50, kind), view, Plain);
        var second = Lit(bitmap);

        Assert.True(
            second.Count > 0, "the second frame drew nothing, so this proves nothing");

        // A ghost is a pixel lit NOW that a clean surface would not have lit.
        // Subtracting the other way round, which this test did at first, takes
        // the first frame's pixels away from themselves and is non-empty
        // whether or not anything is wrong.
        var ghost = new HashSet<(int, int)>(second);
        ghost.ExceptWith(SecondFrameAlone(kind, view));

        Assert.Empty(ghost);
    }

    /// <summary>
    /// The second mark painted alone on a clean surface: the picture the moved
    /// mark is supposed to leave. Compared against rather than assumed, so a
    /// dirty rectangle that is too SMALL and one that is too LARGE are both
    /// caught, the first as a ghost and the second as a missing mark.
    /// </summary>
    private static HashSet<(int X, int Y)> SecondFrameAlone(
        ShapeKind kind, PageTransform view)
    {
        using var clean = NewSurface();
        using var canvas = new SKCanvas(clean);
        var fresh = new DirtyRegionTracker();

        Frame(canvas, fresh, MarkAt(0.55, 0.50, kind), view, Plain);

        return Lit(clean);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle, 0)]
    [InlineData(ShapeKind.Ellipse, 90)]
    [InlineData(ShapeKind.Arrow, 180)]
    public void a_moved_mark_looks_exactly_like_one_drawn_fresh(ShapeKind kind, int rotation)
    {
        // The strongest statement available: after a move, the surface is
        // pixel-for-pixel what a clean surface with only the new mark on it
        // looks like. Nothing left over, nothing missing.
        var view = View(rotation);
        var tracker = new DirtyRegionTracker();

        using var moved = NewSurface();
        using (var canvas = new SKCanvas(moved))
        {
            Frame(canvas, tracker, MarkAt(0.10, 0.10, kind), view, Plain);
            Frame(canvas, tracker, MarkAt(0.55, 0.50, kind), view, Plain);
        }

        using var fresh = NewSurface();
        using (var canvas = new SKCanvas(fresh))
        {
            Frame(canvas, new DirtyRegionTracker(), MarkAt(0.55, 0.50, kind), view, Plain);
        }

        Assert.Equal(Lit(fresh), Lit(moved));
    }

    [Fact]
    public void a_mark_dragged_a_long_way_in_small_steps_leaves_no_trail()
    {
        // The real gesture. Each step overlaps the one before, which is the
        // case where a union that is subtly wrong still looks right for one
        // frame and accumulates over twenty.
        var view = View(0);
        var tracker = new DirtyRegionTracker();

        using var dragged = NewSurface();
        using (var canvas = new SKCanvas(dragged))
        {
            for (int step = 0; step <= 20; step++)
            {
                double at = 0.08 + (step * 0.02);
                Frame(canvas, tracker, MarkAt(at, at, ShapeKind.Rectangle), view, Plain);
            }
        }

        using var fresh = NewSurface();
        using (var canvas = new SKCanvas(fresh))
        {
            Frame(canvas, new DirtyRegionTracker(),
                MarkAt(0.08 + (20 * 0.02), 0.08 + (20 * 0.02), ShapeKind.Rectangle), view, Plain);
        }

        Assert.Equal(Lit(fresh), Lit(dragged));
    }

    [Fact]
    public void the_end_of_a_drag_clears_the_preview_off_the_surface()
    {
        // A cancelled or committed drag hands over an empty frame, and the
        // preview has to go. There is no new rectangle to union with, so this
        // only works if the LAST painted one is remembered.
        var view = View(0);
        var tracker = new DirtyRegionTracker();

        using var bitmap = NewSurface();
        using var canvas = new SKCanvas(bitmap);

        Frame(canvas, tracker, MarkAt(0.2, 0.2, ShapeKind.Rectangle), view, Plain);
        Assert.NotEmpty(Lit(bitmap));

        Frame(canvas, tracker, [], view, Plain);

        Assert.Empty(Lit(bitmap));
    }

    [Fact]
    public void a_mark_dragged_off_the_surface_is_cleared_from_it()
    {
        var view = View(0);
        var tracker = new DirtyRegionTracker();

        using var bitmap = NewSurface();
        using var canvas = new SKCanvas(bitmap);

        Frame(canvas, tracker, MarkAt(0.2, 0.2, ShapeKind.Rectangle), view, Plain);
        Assert.NotEmpty(Lit(bitmap));

        // Far off the top left, so nothing of it lands on the surface.
        Frame(canvas, tracker, MarkAt(-3.0, -3.0, ShapeKind.Rectangle), view, Plain);

        Assert.Empty(Lit(bitmap));
    }

    // ---------------- when the whole view moves ----------------

    [Fact]
    public void scrolling_repaints_everything_rather_than_a_rectangle()
    {
        var view = View(0);
        var tracker = new DirtyRegionTracker();

        using var bitmap = NewSurface();
        using var canvas = new SKCanvas(bitmap);

        Frame(canvas, tracker, MarkAt(0.2, 0.2, ShapeKind.Rectangle), view, Plain);

        var scope = Frame(
            canvas, tracker, MarkAt(0.2, 0.2, ShapeKind.Rectangle), view,
            new ViewportProjection(1, 1, -40, -25));

        Assert.Equal(PaintScope.Full, scope);
    }

    [Theory]
    [InlineData(2.0, 1.0, 0, 0)]
    [InlineData(1.0, 2.0, 0, 0)]
    [InlineData(1.0, 1.0, -40, 0)]
    [InlineData(1.0, 1.0, 0, -40)]
    public void the_view_moving_leaves_no_ghost_either(
        double zoom, double device, double originX, double originY)
    {
        // Belt and braces both checked. Even if the full-repaint decision were
        // removed, the union of old and new would still have to cover this;
        // this asserts the OUTCOME rather than the mechanism, so it keeps
        // holding whichever of the two is doing the work.
        var view = View(0);
        var tracker = new DirtyRegionTracker();
        var moved = new ViewportProjection(zoom, device, originX, originY);
        var items = MarkAt(0.2, 0.2, ShapeKind.Rectangle);

        using var shifted = NewSurface();
        using (var canvas = new SKCanvas(shifted))
        {
            Frame(canvas, tracker, items, view, Plain);
            Frame(canvas, tracker, items, view, moved);
        }

        using var fresh = NewSurface();
        using (var canvas = new SKCanvas(fresh))
        {
            Frame(canvas, new DirtyRegionTracker(), items, view, moved);
        }

        Assert.Equal(Lit(fresh), Lit(shifted));
    }

    [Fact]
    public void a_page_turning_under_a_still_mark_leaves_no_ghost()
    {
        // Rotation does not go through the projection, so the full-repaint
        // check does NOT fire for it. This is the case that rests entirely on
        // the union of old and new, and it is why the union is the mechanism
        // rather than the backstop.
        var items = MarkAt(0.2, 0.2, ShapeKind.Rectangle);
        var tracker = new DirtyRegionTracker();

        using var turned = NewSurface();
        using (var canvas = new SKCanvas(turned))
        {
            Frame(canvas, tracker, items, View(0), Plain);
            Frame(canvas, tracker, items, View(90), Plain);
        }

        using var fresh = NewSurface();
        using (var canvas = new SKCanvas(fresh))
        {
            Frame(canvas, new DirtyRegionTracker(), items, View(90), Plain);
        }

        Assert.Equal(Lit(fresh), Lit(turned));
    }

    // ---------------- the tracker's own contract ----------------

    [Fact]
    public void a_frame_that_was_not_painted_does_not_move_the_tracker_on()
    {
        // Plan must be a question, not an announcement. If Plan recorded the
        // new position, a skipped paint would leave the tracker believing the
        // mark had moved and the old one would never be cleared.
        var tracker = new DirtyRegionTracker();
        var view = View(0);
        var first = MarkAt(0.2, 0.2, ShapeKind.Rectangle);

        var one = tracker.Plan(first, Scale, _ => 0, _ => view, Plain, Surface, Surface);
        tracker.Painted(one);

        // Asked twice about a move, and told about neither.
        var moved = MarkAt(0.6, 0.6, ShapeKind.Rectangle);
        var asked = tracker.Plan(moved, Scale, _ => 0, _ => view, Plain, Surface, Surface);
        var again = tracker.Plan(moved, Scale, _ => 0, _ => view, Plain, Surface, Surface);

        Assert.Equal(asked.Rect, again.Rect);
    }

    [Fact]
    public void a_reset_tracker_repaints_the_whole_surface_next_time()
    {
        var tracker = new DirtyRegionTracker();
        var view = View(0);
        var items = MarkAt(0.2, 0.2, ShapeKind.Rectangle);

        tracker.Painted(tracker.Plan(items, Scale, _ => 0, _ => view, Plain, Surface, Surface));
        tracker.Reset();

        var after = tracker.Plan(items, Scale, _ => 0, _ => view, Plain, Surface, Surface);

        Assert.Equal(PaintScope.Full, after.Scope);
    }

    [Fact]
    public void a_steady_mark_asks_for_a_rectangle_far_smaller_than_the_surface()
    {
        // The point of the exercise, as an assertion rather than a hope. A
        // correct implementation that still cleared the whole surface would
        // pass every test above and save nothing.
        var tracker = new DirtyRegionTracker();
        var view = View(0);
        var items = MarkAt(0.2, 0.2, ShapeKind.Rectangle);

        tracker.Painted(tracker.Plan(items, Scale, _ => 0, _ => view, Plain, Surface, Surface));
        var second = tracker.Plan(items, Scale, _ => 0, _ => view, Plain, Surface, Surface);

        Assert.Equal(PaintScope.Partial, second.Scope);

        long area = (long)(second.Rect.R - second.Rect.L) * (second.Rect.B - second.Rect.T);

        Assert.True(
            area < Surface * (long)Surface / 4,
            $"dirty rectangle is {area}px2 of a {Surface}x{Surface} surface, which saves little");
    }
}
