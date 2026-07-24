using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class DocumentSelectionTests
{
    [Fact]
    public void a_forward_selection_on_one_page_covers_the_dragged_run()
    {
        var sel = DocumentSelection.At(2, 10).ExtendTo(2, 20);

        Assert.False(sel.SpansMultiplePages);
        Assert.Equal((10, 11), sel.RangeForPage(2, 100));
        Assert.Null(sel.RangeForPage(1, 100));
        Assert.Null(sel.RangeForPage(3, 100));
    }

    [Fact]
    public void dragging_backwards_selects_the_same_run()
    {
        var forward = DocumentSelection.At(2, 10).ExtendTo(2, 20);
        var backward = DocumentSelection.At(2, 20).ExtendTo(2, 10);

        Assert.Equal(forward.RangeForPage(2, 100), backward.RangeForPage(2, 100));
        Assert.Equal(forward.Start, backward.Start);
        Assert.Equal(forward.End, backward.End);
    }

    [Fact]
    public void a_two_page_selection_splits_at_the_seam()
    {
        // Page 1 from char 90 to its end, page 2 from its start to char 5.
        var sel = DocumentSelection.At(1, 90).ExtendTo(2, 5);

        Assert.True(sel.SpansMultiplePages);
        Assert.Equal((90, 10), sel.RangeForPage(1, 100));   // 90..99
        Assert.Equal((0, 6), sel.RangeForPage(2, 100));     // 0..5
    }

    [Fact]
    public void pages_in_the_middle_are_fully_selected()
    {
        var sel = DocumentSelection.At(1, 50).ExtendTo(4, 3);

        Assert.Equal((50, 50), sel.RangeForPage(1, 100));
        Assert.Equal((0, 80), sel.RangeForPage(2, 80));     // whole page, its own length
        Assert.Equal((0, 120), sel.RangeForPage(3, 120));   // whole page, different length
        Assert.Equal((0, 4), sel.RangeForPage(4, 100));
    }

    [Fact]
    public void a_backwards_multi_page_drag_covers_the_same_pages()
    {
        var down = DocumentSelection.At(1, 50).ExtendTo(3, 10);
        var up = DocumentSelection.At(3, 10).ExtendTo(1, 50);

        Assert.Equal(down.PageRange, up.PageRange);
        Assert.Equal(down.RangeForPage(1, 100), up.RangeForPage(1, 100));
        Assert.Equal(down.RangeForPage(2, 100), up.RangeForPage(2, 100));
        Assert.Equal(down.RangeForPage(3, 100), up.RangeForPage(3, 100));
    }

    [Fact]
    public void a_collapsed_selection_still_covers_one_character()
    {
        // A click without a drag: not nothing, but a single caret position.
        var sel = DocumentSelection.At(0, 7);
        Assert.Equal((7, 1), sel.RangeForPage(0, 100));
    }

    [Fact]
    public void untouched_pages_return_nothing()
    {
        var sel = DocumentSelection.At(5, 0).ExtendTo(6, 0);

        Assert.Null(sel.RangeForPage(0, 100));
        Assert.Null(sel.RangeForPage(4, 100));
        Assert.Null(sel.RangeForPage(7, 100));
    }

    [Fact]
    public void a_page_with_no_text_contributes_nothing()
    {
        // An image-only page in the middle of a selection must not produce a
        // bogus zero-length range that downstream code would treat as real.
        var sel = DocumentSelection.At(1, 10).ExtendTo(3, 10);
        Assert.Null(sel.RangeForPage(2, 0));
    }

    [Fact]
    public void indices_past_the_end_of_a_page_are_clamped()
    {
        // The text layer can be re-extracted with a different character count
        // than when the drag started; the selection must not run off the end.
        var sel = DocumentSelection.At(0, 5).ExtendTo(0, 999);
        Assert.Equal((5, 5), sel.RangeForPage(0, 10));   // 5..9
    }

    [Fact]
    public void positions_order_by_page_then_character()
    {
        Assert.True(new TextPosition(1, 500) < new TextPosition(2, 0));
        Assert.True(new TextPosition(2, 0) < new TextPosition(2, 1));
        Assert.True(new TextPosition(3, 0) > new TextPosition(2, 999));
    }

    [Fact]
    public void extending_keeps_the_anchor_put()
    {
        var sel = DocumentSelection.At(1, 10);
        var extended = sel.ExtendTo(4, 2).ExtendTo(2, 8);

        Assert.Equal(new TextPosition(1, 10), extended.Anchor);
        Assert.Equal(new TextPosition(2, 8), extended.Focus);
    }
}
