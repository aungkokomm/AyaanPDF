using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class RotationPlanTests
{
    private static readonly IReadOnlyList<bool> NoOrientation = new List<bool>();

    private static List<int> Plan(
        int count, RotateRange range, int current, int from, int to,
        RotateParity parity = RotateParity.All,
        RotateOrientation orientation = RotateOrientation.Any,
        IReadOnlyList<bool>? landscape = null) =>
        RotationPlan.SelectPages(count, range, current, from, to, parity, orientation, landscape ?? NoOrientation);

    [Fact]
    public void all_selects_every_page()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, Plan(4, RotateRange.All, 0, 0, 0));
    }

    [Fact]
    public void current_page_selects_just_that_one()
    {
        Assert.Equal(new[] { 2 }, Plan(5, RotateRange.CurrentPage, 2, 0, 0));
    }

    [Fact]
    public void a_page_range_is_one_based_and_inclusive()
    {
        // Pages 2 to 4 are indices 1,2,3.
        Assert.Equal(new[] { 1, 2, 3 }, Plan(10, RotateRange.PageRange, 0, 2, 4));
    }

    [Fact]
    public void a_reversed_range_is_read_the_sensible_way()
    {
        Assert.Equal(new[] { 1, 2, 3 }, Plan(10, RotateRange.PageRange, 0, 4, 2));
    }

    [Fact]
    public void a_range_past_the_end_is_clamped()
    {
        Assert.Equal(new[] { 7, 8, 9 }, Plan(10, RotateRange.PageRange, 0, 8, 99));
    }

    [Fact]
    public void odd_only_keeps_pages_1_3_5_which_are_indices_0_2_4()
    {
        Assert.Equal(new[] { 0, 2, 4 }, Plan(6, RotateRange.All, 0, 0, 0, RotateParity.OddOnly));
    }

    [Fact]
    public void even_only_keeps_pages_2_4_6_which_are_indices_1_3_5()
    {
        Assert.Equal(new[] { 1, 3, 5 }, Plan(6, RotateRange.All, 0, 0, 0, RotateParity.EvenOnly));
    }

    [Fact]
    public void parity_is_applied_within_a_range_not_across_the_whole_document()
    {
        // Range pages 2-5 (indices 1..4); odd page NUMBERS in that span are 3,5
        // (indices 2,4).
        Assert.Equal(new[] { 2, 4 }, Plan(10, RotateRange.PageRange, 0, 2, 5, RotateParity.OddOnly));
    }

    [Fact]
    public void landscape_only_keeps_the_landscape_pages()
    {
        var landscape = new List<bool> { false, true, false, true };
        Assert.Equal(new[] { 1, 3 }, Plan(4, RotateRange.All, 0, 0, 0,
            RotateParity.All, RotateOrientation.LandscapeOnly, landscape));
    }

    [Fact]
    public void portrait_only_keeps_the_portrait_pages()
    {
        var landscape = new List<bool> { false, true, false, true };
        Assert.Equal(new[] { 0, 2 }, Plan(4, RotateRange.All, 0, 0, 0,
            RotateParity.All, RotateOrientation.PortraitOnly, landscape));
    }

    [Fact]
    public void a_missing_orientation_flag_is_treated_as_portrait()
    {
        // Two pages, only one flag supplied. Landscape-only keeps none of the
        // unknown ones rather than crashing.
        var landscape = new List<bool> { true };
        Assert.Equal(new[] { 0 }, Plan(3, RotateRange.All, 0, 0, 0,
            RotateParity.All, RotateOrientation.LandscapeOnly, landscape));
    }

    [Fact]
    public void parity_and_orientation_combine()
    {
        // Odd page numbers AND landscape.
        var landscape = new List<bool> { true, true, true, true, true, true };
        Assert.Equal(new[] { 0, 2, 4 }, Plan(6, RotateRange.All, 0, 0, 0,
            RotateParity.OddOnly, RotateOrientation.LandscapeOnly, landscape));
    }

    [Fact]
    public void an_empty_document_selects_nothing()
    {
        Assert.Empty(Plan(0, RotateRange.All, 0, 0, 0));
    }
}
