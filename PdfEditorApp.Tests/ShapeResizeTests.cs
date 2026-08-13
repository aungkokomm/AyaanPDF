using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The decision behind resizing a turned shape.
///
/// The defect this exists to stop coming back: a rotated shape could not be
/// resized at all. Its rebuild read the upright size off its own tag, which is
/// exactly right for a move and silently discards the whole point of a resize.
/// </summary>
public class ShapeResizeTests
{
    // A shape whose /Rect runs 0.2..0.6 by 0.2..0.5, i.e. 0.4 x 0.3 of the page.
    private static readonly TextRect Start = new(0.2, 0.2, 0.6, 0.5);

    // Its own upright size, as its tag would record it.
    private const double UprightW = 0.30;
    private const double UprightH = 0.10;

    [Fact]
    public void a_frame_dragged_twice_as_wide_doubles_the_shapes_width()
    {
        // 0.4 -> 0.8 across, height untouched.
        var target = ShapeResize.UprightTargetFor(
            Start, new TextRect(0.2, 0.2, 1.0, 0.5), UprightW, UprightH);

        Assert.NotNull(target);
        Assert.Equal(UprightW * 2, target!.Value.Right - target.Value.Left, 9);
        Assert.Equal(UprightH, target.Value.Bottom - target.Value.Top, 9);
    }

    [Fact]
    public void both_axes_scale_independently()
    {
        // 0.4 -> 0.6 across (x1.5) and 0.3 -> 0.15 down (x0.5).
        var target = ShapeResize.UprightTargetFor(
            Start, new TextRect(0.2, 0.2, 0.8, 0.35), UprightW, UprightH);

        Assert.NotNull(target);
        Assert.Equal(UprightW * 1.5, target!.Value.Right - target.Value.Left, 9);
        Assert.Equal(UprightH * 0.5, target.Value.Bottom - target.Value.Top, 9);
    }

    [Fact]
    public void a_move_leaves_the_size_exactly_alone()
    {
        // THE guard on the move path. A move produces a rectangle of the same
        // size in a new place, so the ratio is one on both axes and the shape
        // must come back at precisely its recorded size. If this ever drifts,
        // dragging a rotated shape around the page would grow or shrink it a
        // little on every single move, which is the old stroke-pad bug wearing
        // a different hat.
        var target = ShapeResize.UprightTargetFor(
            Start, new TextRect(0.5, 0.6, 0.9, 0.9), UprightW, UprightH);

        Assert.NotNull(target);
        Assert.Equal(UprightW, target!.Value.Right - target.Value.Left, 12);
        Assert.Equal(UprightH, target.Value.Bottom - target.Value.Top, 12);
    }

    [Fact]
    public void the_result_is_centred_where_the_drag_put_the_rectangle()
    {
        // The centre of /Rect is the one quantity that is exact at every angle,
        // so it is what the rebuild anchors to.
        var dragged = new TextRect(0.30, 0.40, 0.90, 0.80);
        var target = ShapeResize.UprightTargetFor(Start, dragged, UprightW, UprightH);

        Assert.NotNull(target);
        Assert.Equal(0.60, (target!.Value.Left + target.Value.Right) / 2, 9);
        Assert.Equal(0.60, (target.Value.Top + target.Value.Bottom) / 2, 9);
    }

    [Theory]
    [InlineData(0, 0.1)]        // a start rectangle with no width
    [InlineData(0.4, 0)]        // none with no height
    public void a_start_rectangle_with_no_extent_has_no_ratio_to_offer(double w, double h)
    {
        Assert.Null(ShapeResize.UprightTargetFor(
            new TextRect(0.2, 0.2, 0.2 + w, 0.2 + h),
            new TextRect(0.2, 0.2, 0.8, 0.8), UprightW, UprightH));
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(0.3, 0)]
    [InlineData(-1, 0.1)]
    [InlineData(double.NaN, 0.1)]
    public void a_shape_that_never_recorded_its_size_is_left_on_the_old_path(double w, double h)
    {
        // Null means "keep today's behaviour". Inventing a size for a shape
        // whose tag predates the upright-size field would be worse than not
        // resizing it: the shape would jump to a size nobody chose.
        Assert.Null(ShapeResize.UprightTargetFor(
            Start, new TextRect(0.2, 0.2, 0.8, 0.8), w, h));
    }

    [Fact]
    public void a_shape_dragged_to_nothing_still_names_a_real_rectangle()
    {
        // The FFI rejects an empty or inside-out rectangle, and a rejected write
        // leaves the shape where it was with no explanation. A floor keeps the
        // write valid.
        var target = ShapeResize.UprightTargetFor(
            Start, new TextRect(0.5, 0.5, 0.5000001, 0.5000001), UprightW, UprightH);

        Assert.NotNull(target);
        Assert.True(target!.Value.Right > target.Value.Left);
        Assert.True(target.Value.Bottom > target.Value.Top);
    }
}
