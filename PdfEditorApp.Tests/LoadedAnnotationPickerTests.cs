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
