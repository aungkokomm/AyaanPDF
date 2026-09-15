using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class PageReorderTests
{
    [Fact]
    public void identity_is_zero_to_count()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, PageReorder.Identity(4));
    }

    [Fact]
    public void moving_a_page_down_shifts_the_ones_it_passes_up()
    {
        // Move page 0 to position 2 in a 4-page doc: 1,2 slide up, 0 lands third.
        Assert.Equal(new[] { 1, 2, 0, 3 }, PageReorder.Move(4, 0, 2));
    }

    [Fact]
    public void moving_a_page_up_shifts_the_ones_it_passes_down()
    {
        Assert.Equal(new[] { 0, 3, 1, 2 }, PageReorder.Move(4, 3, 1));
    }

    [Fact]
    public void moving_to_the_same_place_or_out_of_range_changes_nothing()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageReorder.Move(3, 1, 1));
        Assert.Equal(new[] { 0, 1, 2 }, PageReorder.Move(3, 5, 0));
    }

    [Fact]
    public void duplicate_repeats_the_index_right_after_it()
    {
        Assert.Equal(new[] { 0, 1, 1, 2 }, PageReorder.Duplicate(3, 1));
    }

    [Fact]
    public void duplicating_several_pages_puts_each_copy_right_after_its_page()
    {
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, PageReorder.DuplicateSet(4, new[] { 2, 0 }));
    }

    [Fact]
    public void several_pages_move_up_together_and_one_at_the_top_stays()
    {
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, PageReorder.MoveSet(5, new[] { 2, 3 }, -1));
        Assert.Equal(new[] { 1, 0, 3, 2 }, PageReorder.MoveSet(4, new[] { 1, 3 }, -1));
        Assert.Equal(new[] { 0, 1, 2 }, PageReorder.MoveSet(3, new[] { 0, 1 }, -1));
    }

    [Fact]
    public void several_pages_move_down_together_and_one_at_the_bottom_stays()
    {
        Assert.Equal(new[] { 0, 3, 1, 2, 4 }, PageReorder.MoveSet(5, new[] { 1, 2 }, 1));
        Assert.Equal(new[] { 0, 1, 2 }, PageReorder.MoveSet(3, new[] { 1, 2 }, 1));
    }

    [Fact]
    public void delete_omits_the_index()
    {
        Assert.Equal(new[] { 0, 2, 3 }, PageReorder.Delete(4, 1));
    }

    [Fact]
    public void delete_never_empties_the_document()
    {
        Assert.Equal(new[] { 0 }, PageReorder.Delete(1, 0));
    }

    // ---- The overlay remap: where a page's marks follow it ----

    [Fact]
    public void a_marks_page_follows_the_page_when_it_moves()
    {
        var order = PageReorder.Move(4, 0, 2); // 1,2,0,3
        Assert.Equal(2, PageReorder.NewIndexOf(order, 0)); // page 0 is now third
        Assert.Equal(0, PageReorder.NewIndexOf(order, 1)); // page 1 is now first
        Assert.Equal(3, PageReorder.NewIndexOf(order, 3)); // page 3 stayed last
    }

    [Fact]
    public void marks_on_a_deleted_page_are_dropped()
    {
        var order = PageReorder.Delete(4, 1); // 0,2,3
        Assert.Equal(-1, PageReorder.NewIndexOf(order, 1));
        Assert.Equal(1, PageReorder.NewIndexOf(order, 2)); // page 2 shifted down to 1
    }

    [Fact]
    public void a_duplicated_pages_marks_follow_its_first_copy()
    {
        var order = PageReorder.Duplicate(3, 1); // 0,1,1,2
        Assert.Equal(1, PageReorder.NewIndexOf(order, 1)); // first copy, not the second at 2
    }

    [Fact]
    public void is_permutation_accepts_a_reorder_and_rejects_adds_or_drops()
    {
        Assert.True(PageReorder.IsPermutation(new[] { 2, 0, 1 }, 3));
        Assert.False(PageReorder.IsPermutation(new[] { 0, 1, 1 }, 3)); // duplicate
        Assert.False(PageReorder.IsPermutation(new[] { 0, 1 }, 3));    // dropped
        Assert.False(PageReorder.IsPermutation(new[] { 0, 1, 3 }, 3)); // out of range
    }

    [Fact]
    public void a_full_move_round_trips_every_page_exactly_once()
    {
        // A move must never lose or clone a page: the result is always a
        // permutation of the original indices.
        var order = PageReorder.Move(6, 4, 1);
        Assert.Equal(Enumerable.Range(0, 6).OrderBy(i => i), order.OrderBy(i => i));
    }
}
