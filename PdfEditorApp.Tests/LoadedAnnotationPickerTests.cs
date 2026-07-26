using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The picking and dragging behind "click a mark that was already in the file
/// and move it". Verified here rather than by asking someone to click.
/// </summary>
public class LoadedAnnotationPickerTests
{
    // A highlight across the middle of a page, as add_highlight_annotations
    // writes one: normalized, top-left origin, both axes over the page width.
    private static readonly AnnotationBox Highlight = new(0, 0.30, 0.30, 0.70, 0.38);

    private static readonly AnnotationBox Ink = new(1, 0.29, 0.49, 0.71, 0.61);

    private static readonly List<AnnotationBox> Page = [Highlight, Ink];

    [Fact]
    public void a_click_inside_a_mark_picks_it()
    {
        Assert.Equal(Highlight, LoadedAnnotationPicker.PickTopmost(Page, 0.5, 0.34));
        Assert.Equal(Ink, LoadedAnnotationPicker.PickTopmost(Page, 0.5, 0.55));
    }

    [Fact]
    public void a_click_on_blank_paper_picks_nothing()
    {
        // Between the two marks, and outside them entirely.
        Assert.Null(LoadedAnnotationPicker.PickTopmost(Page, 0.5, 0.44));
        Assert.Null(LoadedAnnotationPicker.PickTopmost(Page, 0.05, 0.05));
        Assert.Null(LoadedAnnotationPicker.PickTopmost(Page, 0.95, 0.95));
    }

    [Fact]
    public void the_edges_of_a_mark_count_as_inside_it()
    {
        // A user aiming at a thin highlight lands on its edge constantly.
        Assert.Equal(Highlight, LoadedAnnotationPicker.PickTopmost(Page, 0.30, 0.30));
        Assert.Equal(Highlight, LoadedAnnotationPicker.PickTopmost(Page, 0.70, 0.38));
    }

    [Fact]
    public void overlapping_marks_hand_back_the_one_drawn_on_top()
    {
        // Annotations draw in list order, so the LAST one is on top and is
        // what the user sees and is aiming at. Returning the first match would
        // silently select the one underneath.
        var under = new AnnotationBox(0, 0.1, 0.1, 0.9, 0.9);
        var over = new AnnotationBox(1, 0.4, 0.4, 0.6, 0.6);
        var stack = new List<AnnotationBox> { under, over };

        Assert.Equal(over, LoadedAnnotationPicker.PickTopmost(stack, 0.5, 0.5));

        // Outside the top one, the one underneath is still reachable.
        Assert.Equal(under, LoadedAnnotationPicker.PickTopmost(stack, 0.2, 0.2));
    }

    [Fact]
    public void an_empty_page_picks_nothing_rather_than_failing()
    {
        Assert.Null(LoadedAnnotationPicker.PickTopmost([], 0.5, 0.5));
        Assert.Null(LoadedAnnotationPicker.PickTopmost(null!, 0.5, 0.5));
    }

    [Fact]
    public void dragging_moves_a_mark_by_the_pointer_delta_and_keeps_its_size()
    {
        var moved = LoadedAnnotationPicker.Dragged(Highlight, 0.5, 0.34, 0.6, 0.49);

        Assert.Equal(0.40, moved.Left, 6);
        Assert.Equal(0.45, moved.Top, 6);
        Assert.Equal(0.80, moved.Right, 6);
        Assert.Equal(0.53, moved.Bottom, 6);

        // Size must survive: a move that also resizes is a different edit, and
        // for a stamp or ink stroke PDFium refuses to scale at all.
        Assert.Equal(Highlight.Width, moved.Width, 6);
        Assert.Equal(Highlight.Height, moved.Height, 6);
    }

    [Fact]
    public void a_drag_is_measured_from_where_it_started_so_the_mark_cannot_creep()
    {
        // Every sample of a drag is computed against the ORIGINAL box. Feeding
        // each result into the next would compound rounding, and a mark
        // dragged around and back would not return to where it began.
        var start = Highlight;
        var a = LoadedAnnotationPicker.Dragged(start, 0.5, 0.34, 0.55, 0.40);
        var b = LoadedAnnotationPicker.Dragged(start, 0.5, 0.34, 0.60, 0.44);
        var home = LoadedAnnotationPicker.Dragged(start, 0.5, 0.34, 0.5, 0.34);

        Assert.NotEqual(a, b);
        Assert.Equal(start.Left, home.Left, 9);
        Assert.Equal(start.Top, home.Top, 9);
    }

    [Fact]
    public void a_click_that_did_not_really_move_is_not_treated_as_an_edit()
    {
        // Selecting must not dirty the document or write to the file. A hand
        // wobbles a fraction of a pixel on every click.
        var nudged = LoadedAnnotationPicker.Dragged(Highlight, 0.5, 0.34, 0.5, 0.34);
        Assert.False(LoadedAnnotationPicker.IsRealMove(Highlight, nudged));

        var actuallyMoved = LoadedAnnotationPicker.Dragged(Highlight, 0.5, 0.34, 0.51, 0.34);
        Assert.True(LoadedAnnotationPicker.IsRealMove(Highlight, actuallyMoved));
    }
}

/// <summary>Corner grips and the resize they drive.</summary>
public class AnnotationResizeTests
{
    private static readonly AnnotationBox Box = new(0, 0.20, 0.20, 0.60, 0.50);

    [Fact]
    public void each_corner_is_grabbable()
    {
        Assert.Equal(LoadedAnnotationPicker.Grip.TopLeft,
                     LoadedAnnotationPicker.GripAt(Box, 0.20, 0.20));
        Assert.Equal(LoadedAnnotationPicker.Grip.TopRight,
                     LoadedAnnotationPicker.GripAt(Box, 0.60, 0.20));
        Assert.Equal(LoadedAnnotationPicker.Grip.BottomLeft,
                     LoadedAnnotationPicker.GripAt(Box, 0.20, 0.50));
        Assert.Equal(LoadedAnnotationPicker.Grip.BottomRight,
                     LoadedAnnotationPicker.GripAt(Box, 0.60, 0.50));
    }

    [Fact]
    public void the_middle_is_not_a_grip_so_it_can_still_be_dragged()
    {
        Assert.Equal(LoadedAnnotationPicker.Grip.None,
                     LoadedAnnotationPicker.GripAt(Box, 0.40, 0.35));
    }

    [Fact]
    public void a_tiny_annotation_keeps_a_grabbable_middle()
    {
        // With a fixed reach the four grips would cover a small mark
        // completely, leaving no way to MOVE it, only to resize it.
        var tiny = new AnnotationBox(0, 0.5, 0.5, 0.52, 0.52);
        Assert.Equal(LoadedAnnotationPicker.Grip.None,
                     LoadedAnnotationPicker.GripAt(tiny, 0.51, 0.51));

        // The corners still work.
        Assert.Equal(LoadedAnnotationPicker.Grip.TopLeft,
                     LoadedAnnotationPicker.GripAt(tiny, 0.5, 0.5));
    }

    [Fact]
    public void dragging_a_corner_leaves_the_opposite_one_alone()
    {
        var r = LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.BottomRight, 0.8, 0.7);

        Assert.Equal(0.20, r.Left, 6);    // untouched
        Assert.Equal(0.20, r.Top, 6);     // untouched
        Assert.Equal(0.80, r.Right, 6);
        Assert.Equal(0.70, r.Bottom, 6);
    }

    [Fact]
    public void dragging_the_top_left_moves_only_that_corner()
    {
        var r = LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.TopLeft, 0.10, 0.05);

        Assert.Equal(0.10, r.Left, 6);
        Assert.Equal(0.05, r.Top, 6);
        Assert.Equal(0.60, r.Right, 6);   // untouched
        Assert.Equal(0.50, r.Bottom, 6);  // untouched
    }

    [Fact]
    public void a_corner_cannot_be_dragged_through_the_opposite_edge()
    {
        // Turning the box inside out would produce an inverted rectangle,
        // which both native calls reject, so the annotation would look like it
        // had vanished.
        var r = LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.BottomRight, 0.05, 0.05);

        Assert.True(r.Right > r.Left, $"right {r.Right} crossed left {r.Left}");
        Assert.True(r.Bottom > r.Top, $"bottom {r.Bottom} crossed top {r.Top}");
        Assert.True(r.Width >= LoadedAnnotationPicker.MinSize);
        Assert.True(r.Height >= LoadedAnnotationPicker.MinSize);
    }

    [Fact]
    public void a_resize_never_pushes_the_annotation_off_the_top_or_left()
    {
        var r = LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.TopLeft, -0.5, -0.5);

        Assert.Equal(0.0, r.Left, 6);
        Assert.Equal(0.0, r.Top, 6);
    }

    [Fact]
    public void a_resize_counts_as_a_real_edit_even_when_the_corner_stays_put()
    {
        // IsRealMove decides whether to write to the document. It originally
        // compared only the top-left, so dragging the BOTTOM-RIGHT changed the
        // size while the test for "did anything happen" said no.
        var grown = LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.BottomRight, 0.9, 0.8);

        Assert.Equal(Box.Left, grown.Left, 9);
        Assert.Equal(Box.Top, grown.Top, 9);
        Assert.True(LoadedAnnotationPicker.IsRealMove(Box, grown),
                    "growing from the bottom-right must count as an edit");
    }

    [Fact]
    public void no_grip_means_no_change()
    {
        Assert.Equal(Box, LoadedAnnotationPicker.Resized(
            Box, LoadedAnnotationPicker.Grip.None, 0.9, 0.9));
    }
}

/// <summary>Aspect-preserving resize, which is what a picture needs.</summary>
public class AspectPreservingResizeTests
{
    // A 2:1 signature: twice as wide as it is tall.
    private static readonly AnnotationBox Signature = new(0, 0.20, 0.20, 0.60, 0.40);
    private const double Aspect = 0.5;   // height / width

    [Fact]
    public void dragging_a_corner_keeps_a_signature_from_being_stretched()
    {
        // Dragged far past where a free resize would put the bottom edge. The
        // height must follow the width, not the pointer, or the handwriting
        // comes out distorted.
        var r = LoadedAnnotationPicker.Resized(
            Signature, LoadedAnnotationPicker.Grip.BottomRight, 0.80, 0.95, Aspect);

        Assert.Equal(0.60, r.Width, 6);
        Assert.Equal(0.30, r.Height, 6);
        Assert.Equal(Aspect, r.Height / r.Width, 6);
    }

    [Fact]
    public void the_anchored_corner_stays_put_while_the_aspect_holds()
    {
        // Dragging the TOP-left must grow upward from the fixed bottom edge,
        // not downward from the top.
        var r = LoadedAnnotationPicker.Resized(
            Signature, LoadedAnnotationPicker.Grip.TopLeft, 0.10, 0.10, Aspect);

        Assert.Equal(0.60, r.Right, 6);    // fixed
        Assert.Equal(0.40, r.Bottom, 6);   // fixed
        Assert.Equal(Aspect, r.Height / r.Width, 6);
    }

    [Fact]
    public void a_free_resize_still_ignores_aspect_when_none_is_given()
    {
        // Text markup has no shape to protect, so passing 0 must behave
        // exactly as before.
        var r = LoadedAnnotationPicker.Resized(
            Signature, LoadedAnnotationPicker.Grip.BottomRight, 0.90, 0.90, 0);

        Assert.Equal(0.90, r.Right, 6);
        Assert.Equal(0.90, r.Bottom, 6);
    }

    [Fact]
    public void an_aspect_resize_pushed_off_the_page_shifts_rather_than_distorts()
    {
        // Clamping an edge to zero would silently change the aspect that was
        // just enforced, so the box moves instead.
        var r = LoadedAnnotationPicker.Resized(
            Signature, LoadedAnnotationPicker.Grip.TopLeft, -0.4, -0.4, Aspect);

        Assert.True(r.Left >= 0, $"left {r.Left} is off the page");
        Assert.True(r.Top >= 0, $"top {r.Top} is off the page");
        Assert.Equal(Aspect, r.Height / r.Width, 6);
    }

    [Fact]
    public void aspect_is_held_even_at_the_minimum_size()
    {
        var r = LoadedAnnotationPicker.Resized(
            Signature, LoadedAnnotationPicker.Grip.BottomRight, 0.0, 0.0, Aspect);

        Assert.True(r.Width >= LoadedAnnotationPicker.MinSize);
        Assert.True(r.Height > 0, "a collapsed height would make the stamp invisible");
        Assert.Equal(Aspect, r.Height / r.Width, 6);
    }
}

/// <summary>
/// Handles must be grabbable from OUTSIDE the shape, because that is where
/// half of each one is drawn.
/// </summary>
public class GripReachTests
{
    private static readonly AnnotationBox Box = new(0, 0.20, 0.20, 0.60, 0.50);

    [Theory]
    [InlineData(-1, -1, LoadedAnnotationPicker.Grip.TopLeft)]
    [InlineData(+1, -1, LoadedAnnotationPicker.Grip.TopRight)]
    [InlineData(-1, +1, LoadedAnnotationPicker.Grip.BottomLeft)]
    [InlineData(+1, +1, LoadedAnnotationPicker.Grip.BottomRight)]
    public void every_corner_is_grabbable_from_just_outside_the_box(
        int dx, int dy, LoadedAnnotationPicker.Grip expected)
    {
        // Half a grip beyond the corner, on both axes: exactly where a user
        // aiming at the visible handle often lands. All four must answer, not
        // just the one that happens to be clicked inward.
        const double Out = LoadedAnnotationPicker.GripReach / 2;

        double x = (dx < 0 ? Box.Left : Box.Right) + dx * Out;
        double y = (dy < 0 ? Box.Top : Box.Bottom) + dy * Out;

        Assert.Equal(expected, LoadedAnnotationPicker.GripAt(Box, x, y));
    }

    [Theory]
    [InlineData(-1, -1, LoadedAnnotationPicker.Grip.TopLeft)]
    [InlineData(+1, -1, LoadedAnnotationPicker.Grip.TopRight)]
    [InlineData(-1, +1, LoadedAnnotationPicker.Grip.BottomLeft)]
    [InlineData(+1, +1, LoadedAnnotationPicker.Grip.BottomRight)]
    public void every_corner_is_grabbable_from_just_inside_the_box(
        int dx, int dy, LoadedAnnotationPicker.Grip expected)
    {
        const double In = LoadedAnnotationPicker.GripReach / 2;

        double x = (dx < 0 ? Box.Left : Box.Right) - dx * In;
        double y = (dy < 0 ? Box.Top : Box.Bottom) - dy * In;

        Assert.Equal(expected, LoadedAnnotationPicker.GripAt(Box, x, y));
    }

    [Fact]
    public void a_point_well_outside_grabs_nothing()
    {
        Assert.Equal(LoadedAnnotationPicker.Grip.None,
                     LoadedAnnotationPicker.GripAt(Box, 0.9, 0.9));
        Assert.Equal(LoadedAnnotationPicker.Grip.None,
                     LoadedAnnotationPicker.GripAt(Box, 0.0, 0.0));
    }
}
