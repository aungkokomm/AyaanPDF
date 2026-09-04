using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Telling a click apart from a drag on the document's own text.
///
/// ⚠️ THE CLICK IS THE ONE THAT MUST NOT BREAK. A press inside the selected box
/// already means "put a caret here", and that gesture is how the reader edits
/// anything at all. A move that began on press would take it away.
/// </summary>
public class DragGestureTests
{
    [Fact]
    public void a_press_on_its_own_is_not_a_drag()
    {
        var g = new DragGesture();
        g.Press(0, 0.5, 0.5);

        Assert.False(g.IsDragging);
        Assert.False(g.Release());
    }

    [Fact]
    public void a_press_that_barely_wanders_is_still_a_click()
    {
        var g = new DragGesture();
        g.Press(0, 0.5, 0.5);

        Assert.False(g.Move(0.5 + (DragGesture.Threshold / 3), 0.5));
        Assert.False(g.Move(0.5, 0.5 + (DragGesture.Threshold / 3)));
        Assert.False(g.IsDragging);
        Assert.False(g.Release());
    }

    [Fact]
    public void a_press_that_travels_becomes_a_drag()
    {
        var g = new DragGesture();
        g.Press(2, 0.5, 0.5);

        Assert.True(g.Move(0.5 + (DragGesture.Threshold * 2), 0.5));
        Assert.True(g.IsDragging);
        Assert.Equal(2, g.Page);
        Assert.True(g.Release());
    }

    /// <summary>
    /// ⚠️ TRUE ON THE ONE MOVE THAT CHANGES IT, so a caller can act on the
    /// change without keeping its own copy of the answer.
    /// </summary>
    [Fact]
    public void it_says_so_once_and_not_again()
    {
        var g = new DragGesture();
        g.Press(0, 0.5, 0.5);

        Assert.True(g.Move(0.6, 0.5));
        Assert.False(g.Move(0.7, 0.5));
        Assert.False(g.Move(0.8, 0.5));
        Assert.True(g.IsDragging);
    }

    /// <summary>
    /// ⚠️ THE DISTANCE, NOT EITHER AXIS ON ITS OWN. A pointer that wandered a
    /// little in both directions has travelled nowhere.
    /// </summary>
    [Fact]
    public void wandering_in_both_directions_at_once_is_measured_as_one_distance()
    {
        var g = new DragGesture();
        g.Press(0, 0.5, 0.5);

        // Each axis alone is under the threshold; together they clear it.
        double each = DragGesture.Threshold * 0.8;
        Assert.True(g.Move(0.5 + each, 0.5 + each));
    }

    [Fact]
    public void it_reports_how_far_it_has_come_from_where_it_started()
    {
        var g = new DragGesture();
        g.Press(0, 0.20, 0.30);
        g.Move(0.35, 0.10);

        Assert.Equal(0.15, g.Dx, 6);
        Assert.Equal(-0.20, g.Dy, 6);
    }

    /// <summary>Nothing armed means nothing moves, however far the pointer goes.</summary>
    [Fact]
    public void a_move_without_a_press_does_nothing()
    {
        var g = new DragGesture();

        Assert.False(g.Move(10, 10));
        Assert.False(g.IsDragging);
        Assert.Equal(-1, g.Page);
    }

    [Fact]
    public void releasing_leaves_nothing_armed()
    {
        var g = new DragGesture();
        g.Press(1, 0.5, 0.5);
        g.Move(0.9, 0.9);
        g.Release();

        Assert.False(g.IsDragging);
        Assert.Equal(-1, g.Page);
        Assert.Equal(0, g.Dx);

        // And a move after the release is not the old drag carrying on.
        Assert.False(g.Move(0.95, 0.95));
    }

    [Fact]
    public void clearing_abandons_a_drag_in_progress()
    {
        var g = new DragGesture();
        g.Press(0, 0.5, 0.5);
        g.Move(0.9, 0.9);
        g.Clear();

        Assert.False(g.IsDragging);
        Assert.False(g.Release());
    }

    /// <summary>
    /// ⚠️ THE PRESS POSITION OUTLIVES THE PRESS. What this decides is whether a
    /// click or a drag was meant, and it cannot know until the pointer either
    /// travels or lifts. By then the click still has to happen somewhere, and
    /// the only right answer is where the button went down: a pointer that
    /// drifted a couple of points on the way up would otherwise put the caret
    /// at a different character than the reader aimed at.
    /// </summary>
    [Fact]
    public void a_press_that_wobbles_is_still_acted_on_where_it_landed()
    {
        var g = new DragGesture();
        g.Press(2, 0.4, 0.6);

        // Under the threshold, so still a click.
        Assert.False(g.Move(0.4015, 0.6015));
        Assert.False(g.IsDragging);

        Assert.Equal(0.4, g.FromX, 6);
        Assert.Equal(0.6, g.FromY, 6);
        Assert.Equal(2, g.Page);
    }
}
