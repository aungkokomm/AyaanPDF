using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The page picker's "1-4, 7, 9" box: what is typed and what is ticked in the
/// thumbnail grid are the same set of pages, both ways.
/// </summary>
public class PageSelectionTests
{
    private static int[] Parse(string text, int pageCount)
    {
        Assert.True(PageSelection.TryParse(text, pageCount, out var indices, out var error), error);
        return indices.ToArray();
    }

    [Fact]
    public void pages_and_ranges_are_read_as_zero_based_indices_in_page_order()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 8 }, Parse("1-4, 7, 9", 12));
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 8 }, Parse("9,7 , 1-4", 12));
    }

    [Fact]
    public void every_page_is_all_and_blank_is_none()
    {
        Assert.Equal(Enumerable.Range(0, 5), Parse("All", 5));
        Assert.Equal(Enumerable.Range(0, 5), Parse(" all ", 5));
        Assert.Empty(Parse("", 5));
        Assert.Empty(Parse("  ", 5));
    }

    [Fact]
    public void a_reversed_range_an_overlap_and_an_en_dash_are_all_understood()
    {
        Assert.Equal(new[] { 2, 3, 4 }, Parse("5-3", 10));
        Assert.Equal(new[] { 0, 1, 2 }, Parse("1-3, 2", 10));
        Assert.Equal(new[] { 1, 2 }, Parse("2–3", 10));
    }

    [Theory]
    [InlineData("0", "Pages start at 1.")]
    [InlineData("13", "Page 13 is past the end. This file has 12 pages.")]
    [InlineData("abc", "\"abc\" isn't a page number.")]
    [InlineData("1-x", "\"1-x\" isn't a page range.")]
    [InlineData("-3", "\"-3\" isn't a page range.")]
    public void a_mistake_is_refused_with_words_the_box_can_show(string text, string expected)
    {
        Assert.False(PageSelection.TryParse(text, 12, out var indices, out var error));
        Assert.Empty(indices);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void a_one_page_file_says_page_not_pages()
    {
        Assert.False(PageSelection.TryParse("2", 1, out _, out var error));
        Assert.Equal("Page 2 is past the end. This file has 1 page.", error);
    }

    [Fact]
    public void chosen_pages_are_written_back_the_way_they_are_read()
    {
        Assert.Equal("1-4, 7, 9", PageSelection.Format(new[] { 8, 0, 1, 2, 3, 6, 6 }, 12));
        Assert.Equal("All", PageSelection.Format(Enumerable.Range(0, 12), 12));
        Assert.Equal(string.Empty, PageSelection.Format(Array.Empty<int>(), 12));
        Assert.Equal("3", PageSelection.Format(new[] { 2 }, 12));

        int[] chosen = { 1, 2, 5, 9, 10, 11 };
        Assert.Equal(chosen, Parse(PageSelection.Format(chosen, 20), 20));
    }

    [Fact]
    public void neighbours_become_runs_so_a_grid_selects_a_range_in_one_call()
    {
        Assert.Equal(new[] { (0, 4), (6, 1), (8, 2) }, PageSelection.Runs(new[] { 0, 1, 2, 3, 6, 8, 9 }).ToArray());
        Assert.Empty(PageSelection.Runs(Array.Empty<int>()));
    }

    [Fact]
    public void odd_and_even_are_the_printed_page_numbers()
    {
        Assert.Equal(new[] { 0, 2, 4 }, PageSelection.OddPages(5));
        Assert.Equal(new[] { 1, 3 }, PageSelection.EvenPages(5));
        Assert.Empty(PageSelection.EvenPages(1));
    }
}
