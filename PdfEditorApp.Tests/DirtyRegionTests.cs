using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The arithmetic behind painting only part of the surface.
///
/// No pixels here on purpose: which rectangle is at stake is a calculation, and
/// the calculation is where the interesting mistakes are. The pixels are
/// checked next door, in DirtyRegionBleedTests, and the two halves are kept
/// apart so a failure says which one broke.
/// </summary>
public class DirtyRegionTests
{
    private const double Scale = 800;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);

    private static ShapeRenderItem Item(int page, params (double X, double Y)[] points) =>
        new(page, points, new RenderColor(255, 0, 0, 0), 0.004, RenderStyle.Stroked);

    private static PageTransform Flat(int page) => PageTransform.For(1, 1, 0, 1);

    private static double PageTop(int page) => page * 1000.0;

    private static (double L, double T, double R, double B)? Bounds(
        IReadOnlyList<ShapeRenderItem> items, ViewportProjection? p = null) =>
        DirtyRegion.DeviceBoundsOf(items, Scale, PageTop, Flat, p ?? Plain);

    // ---------------- what a frame can touch ----------------

    [Fact]
    public void an_empty_frame_asks_for_no_rectangle_at_all()
    {
        // Not an empty rectangle: nothing to paint has to be distinguishable
        // from a rectangle of zero size, because one means "skip" and the other
        // would mean "clear nothing" and leave the last frame on screen.
        Assert.Null(Bounds([]));
    }

    [Fact]
    public void an_item_with_no_geometry_contributes_nothing()
    {
        Assert.Null(Bounds([Item(0)]));
    }

    [Fact]
    public void the_rectangle_covers_the_mark_plus_its_pad()
    {
        var item = Item(0, (0.1, 0.1), (0.4, 0.3));      // slot (80,80)-(320,240)
        double pad = DirtyRegion.PadFor(item, Scale, Flat(0), Plain);
        double reach = 0.004 * Scale / 2;                 // half the stroke

        var (l, t, r, b) = Bounds([item])!.Value;

        Assert.Equal(80 - reach - pad, l, 6);
        Assert.Equal(80 - reach - pad, t, 6);
        Assert.Equal(320 + reach + pad, r, 6);
        Assert.Equal(240 + reach + pad, b, 6);
    }

    [Fact]
    public void two_marks_give_one_rectangle_that_holds_both()
    {
        var near = Item(0, (0.1, 0.1));
        var far = Item(0, (0.5, 0.6));

        var one = Bounds([near])!.Value;
        var two = Bounds([near, far])!.Value;

        Assert.Equal(one.L, two.L, 6);
        Assert.True(two.R > one.R, "the second mark must widen the rectangle");
        Assert.True(two.B > one.B, "the second mark must deepen the rectangle");
    }

    [Fact]
    public void each_mark_is_padded_by_its_own_stroke_not_the_frames_widest()
    {
        // A frame can hold a hairline shaft and a heavy freehand guide at once.
        // Padding both by the heavier one wastes area; padding both by the
        // lighter one leaves the heavier one's join outside the rectangle,
        // which is a ghost. So the pad is per item, and this is what says so.
        var thin = Item(0, (0.5, 0.5));
        var guide = ShapeRenderList.InkGuide(0, [(0.1, 0.1)]);

        double thinPad = DirtyRegion.PadFor(thin, Scale, Flat(0), Plain);
        double guidePad = DirtyRegion.PadFor(guide, Scale, Flat(0), Plain);

        Assert.True(guidePad != thinPad, "the fixture must use two different weights");

        var (l, _, r, _) = Bounds([thin, guide])!.Value;

        // The guide is on the left and the thin mark on the right, so each edge
        // is decided by a different item and carries that item's own pad.
        Assert.Equal((0.1 * Scale) - (OverlayProjection.InkGuideWidthDips / 2) - guidePad, l, 6);
        Assert.Equal((0.5 * Scale) + (0.004 * Scale / 2) + thinPad, r, 6);
    }

    // ---------------- the whole chain reaches the rectangle ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void a_turned_page_moves_the_rectangle_with_it(int rotation)
    {
        // The dirty rectangle has to be computed through the SAME projection
        // the painter draws through, or the clear lands somewhere the ink does
        // not. Stated as a property so it holds at every rotation: whatever the
        // turn does to the mark, the rectangle contains it.
        var view = PageTransform.For(Scale, 1000, rotation, Scale);
        var item = Item(0, (0.15, 0.2), (0.6, 0.5));

        var rect = DirtyRegion.DeviceBoundsOf(
            [item], Scale, _ => 0, _ => view, Plain)!.Value;
        var (sl, st, sr, sb) = OverlayProjection.SlotBoundsOf(item, Scale, 0, view);

        Assert.True(rect.L <= sl && rect.T <= st && rect.R >= sr && rect.B >= sb,
            $"rotation {rotation}: {rect} does not contain the mark's {(sl, st, sr, sb)}");
    }

    [Fact]
    public void zoom_and_display_scale_both_reach_the_rectangle()
    {
        var item = Item(0, (0.1, 0.1), (0.4, 0.3));

        var plain = Bounds([item])!.Value;
        var zoomed = Bounds([item], new ViewportProjection(2, 1, 0, 0))!.Value;
        var scaled = Bounds([item], new ViewportProjection(1, 2, 0, 0))!.Value;

        // Not asserting an exact size, because the pad does not double with the
        // mark: the antialiasing term is a fixed number of pixels. What must be
        // true is that both make the rectangle bigger, and by about twice.
        Assert.InRange((zoomed.R - zoomed.L) / (plain.R - plain.L), 1.8, 2.0);
        Assert.InRange((scaled.R - scaled.L) / (plain.R - plain.L), 1.8, 2.0);
    }

    [Fact]
    public void the_scroll_origin_moves_the_rectangle()
    {
        var item = Item(0, (0.1, 0.1), (0.4, 0.3));

        var home = Bounds([item])!.Value;
        var scrolled = Bounds([item], new ViewportProjection(1, 1, -50, -70))!.Value;

        Assert.Equal(home.L - 50, scrolled.L, 6);
        Assert.Equal(home.T - 70, scrolled.T, 6);
    }

    [Fact]
    public void a_mark_on_a_later_page_is_placed_by_its_own_pages_top()
    {
        var first = Bounds([Item(0, (0.2, 0.2))])!.Value;
        var third = Bounds([Item(2, (0.2, 0.2))])!.Value;

        Assert.Equal(first.T + 2000, third.T, 6);
        Assert.Equal(first.L, third.L, 6);
    }

    // ---------------- old and new ----------------

    [Fact]
    public void the_union_of_two_rectangles_holds_both()
    {
        var union = DirtyRegion.Union((10.0, 10.0, 20.0, 20.0), (15.0, 5.0, 40.0, 18.0))!.Value;

        Assert.Equal((10.0, 5.0, 40.0, 20.0), union);
    }

    [Fact]
    public void a_union_with_nothing_is_the_other_one()
    {
        var only = (10.0, 10.0, 20.0, 20.0);

        Assert.Equal(only, DirtyRegion.Union(only, null)!.Value);
        Assert.Equal(only, DirtyRegion.Union(null, only)!.Value);
        Assert.Null(DirtyRegion.Union(null, null));
    }

    // ---------------- the surface ----------------

    [Fact]
    public void a_rectangle_is_rounded_outward_so_a_part_covered_pixel_is_included()
    {
        // Rounding in would shave the fringe off the very edge of the clear,
        // which is the same ghost by a smaller amount.
        var clamped = DirtyRegion.ClampToSurface((10.4, 10.6, 20.1, 20.9), 100, 100)!.Value;

        Assert.Equal((10, 10, 21, 21), clamped);
    }

    [Fact]
    public void a_rectangle_hanging_off_the_surface_is_cut_to_it()
    {
        var clamped = DirtyRegion.ClampToSurface((-40.0, -30.0, 50.0, 60.0), 100, 100)!.Value;

        Assert.Equal((0, 0, 50, 60), clamped);
    }

    [Fact]
    public void a_rectangle_entirely_off_the_surface_asks_for_no_paint()
    {
        Assert.Null(DirtyRegion.ClampToSurface((-500.0, -500.0, -400.0, -400.0), 100, 100));
        Assert.Null(DirtyRegion.ClampToSurface((200.0, 200.0, 300.0, 300.0), 100, 100));
    }

    // ---------------- when partial painting is not safe ----------------

    [Fact]
    public void the_first_frame_is_always_a_full_repaint()
    {
        // Nothing is known about what is in the bitmap yet.
        Assert.True(DirtyRegion.NeedsFullRepaint(null, 0, 0, 0, Plain, Scale, 800, 600));
    }

    [Fact]
    public void an_unchanged_view_does_not_need_a_full_repaint()
    {
        Assert.False(DirtyRegion.NeedsFullRepaint(Plain, Scale, 800, 600, Plain, Scale, 800, 600));
    }

    [Theory]
    [InlineData(2, 1, 0, 0)]
    [InlineData(1, 2, 0, 0)]
    [InlineData(1, 1, 5, 0)]
    [InlineData(1, 1, 0, 5)]
    public void any_change_to_the_projection_forces_a_full_repaint(
        double zoom, double device, double originX, double originY)
    {
        // Zoom, display scale and both scroll axes each on their own. A
        // comparison that missed one of the four would leave the old frame
        // stranded when only that one moved, which is the hardest kind of
        // ghost to find because three out of four gestures look right.
        var moved = new ViewportProjection(zoom, device, originX, originY);

        Assert.True(DirtyRegion.NeedsFullRepaint(Plain, Scale, 800, 600, moved, Scale, 800, 600));
    }

    [Fact]
    public void a_resized_surface_forces_a_full_repaint()
    {
        // A new bitmap holds whatever it holds, and it is not the last frame.
        Assert.True(DirtyRegion.NeedsFullRepaint(Plain, Scale, 800, 600, Plain, Scale, 801, 600));
        Assert.True(DirtyRegion.NeedsFullRepaint(Plain, Scale, 800, 600, Plain, Scale, 800, 601));
    }

    [Fact]
    public void a_changed_overlay_scale_forces_a_full_repaint()
    {
        Assert.True(DirtyRegion.NeedsFullRepaint(Plain, Scale, 800, 600, Plain, 400, 800, 600));
    }
}
