using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The page stack's card positions, which the ItemsRepeater layout places
/// cards at.
///
/// ⚠️ WRITTEN AFTER A REAL FAILURE. A link to page 39175 of a 39881-page book
/// showed blank sheets: WinUI's StackLayout estimated card positions from its
/// first, taller pages and realized cards for pages near 36000 at that offset.
/// The shapes below are the book's own, measured: 612 x 828 for the opening
/// pages, 612 x 756 for page 39174 and the pages around it.
/// </summary>
public class PageStackGeometryTests
{
    private const double Gap = 16;
    private const double LayoutWidth = 1200;

    private static List<PageSizePoints> BookShapedLikeTheReaders(int pages)
    {
        var sizes = new List<PageSizePoints>(pages);
        for (int i = 0; i < pages; i++)
        {
            sizes.Add(i < 5 ? new PageSizePoints(612, 828) : new PageSizePoints(612, 756));
        }
        return sizes;
    }

    private static (ContinuousLayout Layout, PageStackGeometry Geometry) Build(List<PageSizePoints> sizes)
    {
        var layout = new ContinuousLayout(pageGap: Gap);
        layout.Rebuild(sizes, LayoutWidth);

        var geometry = new PageStackGeometry(Gap);
        geometry.Rebuild(layout.Slots.Count, i => (layout.Slots[i].Width, layout.Slots[i].Height));
        return (layout, geometry);
    }

    [Fact]
    public void every_card_sits_exactly_where_the_view_model_scrolls_to()
    {
        // The view model scrolls to ContinuousLayout's tops, so the layout that
        // places the cards has to agree with them to the DIP, for every page.
        var (layout, geometry) = Build(BookShapedLikeTheReaders(39881));

        Assert.Equal(layout.Slots.Count, geometry.Count);
        for (int i = 0; i < layout.Slots.Count; i++)
        {
            Assert.Equal(layout.Slots[i].Top, geometry.TopOf(i), 6);
        }
        Assert.Equal(layout.TotalHeight, geometry.Height, 6);
        Assert.Equal(LayoutWidth, geometry.Width, 6);
    }

    [Fact]
    public void a_jump_far_down_the_book_realizes_the_page_that_is_really_there()
    {
        var (layout, geometry) = Build(BookShapedLikeTheReaders(39881));
        double top = layout.TopOf(39174);

        var (first, last) = geometry.Range(top + 1, top + 600);

        Assert.Equal(39174, first);
        Assert.Equal(39174, last);

        // The control: estimating from the first pages, which is what the stock
        // layout does, lands thousands of pages away at the same offset.
        double averageOfOpening = Enumerable.Range(0, 6).Average(i => layout.Slots[i].Height + Gap);
        int estimated = (int)(top / averageOfOpening);
        Assert.True(Math.Abs(estimated - 39174) > 1000, $"the estimate landed on {estimated}");
    }

    [Fact]
    public void a_band_across_two_cards_realizes_both()
    {
        var (layout, geometry) = Build(BookShapedLikeTheReaders(100));
        double boundary = layout.TopOf(10);

        Assert.Equal((9, 10), geometry.Range(boundary - 50, boundary + 50));
    }

    [Fact]
    public void a_band_inside_a_gap_realizes_nothing()
    {
        var (layout, geometry) = Build(BookShapedLikeTheReaders(100));
        double gapTop = layout.TopOf(10) - Gap;

        Assert.Equal((-1, -1), geometry.Range(gapTop + 2, gapTop + 10));
    }

    [Fact]
    public void bands_outside_the_stack_realize_nothing()
    {
        var (_, geometry) = Build(BookShapedLikeTheReaders(100));

        Assert.Equal((-1, -1), geometry.Range(-500, -1));
        Assert.Equal((-1, -1), geometry.Range(geometry.Height + 1, geometry.Height + 500));
        Assert.Equal((-1, -1), new PageStackGeometry(Gap).Range(0, 1000));
    }

    [Fact]
    public void a_band_covering_the_whole_stack_realizes_every_card()
    {
        var (_, geometry) = Build(BookShapedLikeTheReaders(7));

        Assert.Equal((0, 6), geometry.Range(0, geometry.Height));
    }
}
