using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class StampPlacementTests
{
    [Fact]
    public void a_stamp_lands_centred_on_the_click()
    {
        // A square image, so width and height are equal in normalized space.
        var (left, top, right, bottom) = StampPlacement.Compute(0.5, 0.5, 100, 100, 0.2);

        Assert.Equal(0.4, left, 6);
        Assert.Equal(0.4, top, 6);
        Assert.Equal(0.6, right, 6);
        Assert.Equal(0.6, bottom, 6);
    }

    [Fact]
    public void height_follows_the_images_aspect_so_a_stamp_is_never_squashed()
    {
        // A signature: four times wider than it is tall. Asking for a quarter
        // of the page width must give a SIXTEENTH of the width in height, not
        // a quarter.
        var wide = StampPlacement.Compute(0.5, 0.5, 400, 100, 0.25);
        Assert.Equal(0.25, wide.Right - wide.Left, 6);
        Assert.Equal(0.0625, wide.Bottom - wide.Top, 6);

        // And a tall seal keeps its height.
        var tall = StampPlacement.Compute(0.5, 0.5, 100, 400, 0.25);
        Assert.Equal(0.25, tall.Right - tall.Left, 6);
        Assert.Equal(1.0, tall.Bottom - tall.Top, 6);
    }

    [Fact]
    public void a_click_near_the_top_left_pushes_the_stamp_fully_onto_the_page()
    {
        // Clicking at the very corner would otherwise centre the stamp half
        // off the page, putting its selection handle somewhere unclickable so
        // it could never be moved or deleted again.
        var (left, top, right, bottom) = StampPlacement.Compute(0.0, 0.0, 100, 100, 0.2);

        Assert.Equal(0.0, left, 6);
        Assert.Equal(0.0, top, 6);

        // Nudging must MOVE it, not shrink it.
        Assert.Equal(0.2, right - left, 6);
        Assert.Equal(0.2, bottom - top, 6);
    }

    [Fact]
    public void the_size_survives_being_nudged_on_one_axis_only()
    {
        // Near the left edge but vertically clear: x is clamped, y is not, and
        // neither dimension changes.
        var (left, top, right, bottom) = StampPlacement.Compute(0.01, 0.5, 100, 200, 0.2);

        Assert.Equal(0.0, left, 6);
        Assert.Equal(0.2, right - left, 6);
        Assert.Equal(0.4, bottom - top, 6);
        Assert.Equal(0.3, top, 6);   // 0.5 - 0.4/2, untouched
    }

    [Fact]
    public void an_absurd_width_is_clamped_rather_than_accepted()
    {
        // Guards against a caller passing 0, a negative, or something wider
        // than the page. A zero-width stamp would be invisible AND unclickable.
        var tiny = StampPlacement.Compute(0.5, 0.5, 100, 100, 0.0);
        Assert.Equal(StampPlacement.MinWidthFraction, tiny.Right - tiny.Left, 6);

        var negative = StampPlacement.Compute(0.5, 0.5, 100, 100, -3);
        Assert.Equal(StampPlacement.MinWidthFraction, negative.Right - negative.Left, 6);

        var huge = StampPlacement.Compute(0.5, 0.5, 100, 100, 5);
        Assert.Equal(1.0, huge.Right - huge.Left, 6);
    }

    [Fact]
    public void an_image_with_no_size_is_refused()
    {
        // render_core would reject it too, but with a bare status code. Failing
        // here names the problem.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StampPlacement.Compute(0.5, 0.5, 0, 100, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StampPlacement.Compute(0.5, 0.5, 100, 0, 0.2));
    }

    [Fact]
    public void the_rectangle_is_always_well_formed()
    {
        // set_annotation_bounds and add_stamp_annotation both reject a
        // rectangle whose edges are inverted or equal, so no input should ever
        // produce one.
        foreach (var (x, y) in new[] { (0.0, 0.0), (1.0, 1.0), (0.5, 0.0), (0.0, 0.5), (0.99, 0.99) })
        {
            foreach (var (iw, ih) in new[] { (100, 100), (1000, 50), (50, 1000) })
            {
                var r = StampPlacement.Compute(x, y, iw, ih, 0.25);
                Assert.True(r.Right > r.Left, $"click ({x},{y}) image {iw}x{ih}: left {r.Left} right {r.Right}");
                Assert.True(r.Bottom > r.Top, $"click ({x},{y}) image {iw}x{ih}: top {r.Top} bottom {r.Bottom}");
                Assert.True(r.Left >= 0 && r.Top >= 0, "a stamp must not start off the page");
            }
        }
    }
}
