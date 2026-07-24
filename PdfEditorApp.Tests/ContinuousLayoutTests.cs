using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class ContinuousLayoutTests
{
    private static List<PageSizePoints> Uniform(int count, double w = 612, double h = 792) =>
        Enumerable.Range(0, count).Select(_ => new PageSizePoints(w, h)).ToList();

    [Fact]
    public void slots_stack_with_gaps_and_preserve_aspect_ratio()
    {
        var layout = new ContinuousLayout(pageGap: 10);
        layout.Rebuild(Uniform(3), layoutWidth: 100);

        // 612x792 at width 100 -> height 129.41
        double expectedH = 100 * (792.0 / 612.0);

        Assert.Equal(3, layout.PageCount);
        Assert.Equal(0, layout.Slots[0].Top, 3);
        Assert.Equal(expectedH + 10, layout.Slots[1].Top, 3);
        Assert.Equal(2 * (expectedH + 10), layout.Slots[2].Top, 3);
        Assert.All(layout.Slots, s => Assert.Equal(expectedH, s.Height, 3));
    }

    [Fact]
    public void total_height_has_no_trailing_gap()
    {
        var layout = new ContinuousLayout(pageGap: 10);
        layout.Rebuild(Uniform(3), layoutWidth: 100);

        double h = 100 * (792.0 / 612.0);
        Assert.Equal(3 * h + 2 * 10, layout.TotalHeight, 3);
    }

    [Fact]
    public void mixed_page_sizes_each_get_their_own_height()
    {
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild([
            new PageSizePoints(100, 100),   // square
            new PageSizePoints(100, 200),   // tall
        ], layoutWidth: 50);

        Assert.Equal(50, layout.Slots[0].Height, 3);
        Assert.Equal(100, layout.Slots[1].Height, 3);
        Assert.Equal(50, layout.Slots[1].Top, 3);
    }

    [Fact]
    public void a_degenerate_page_size_still_gets_a_nonzero_slot()
    {
        // A page that fails to load must not collapse to zero height, or every
        // page below it would shift and the scrollbar would be wrong.
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild([new PageSizePoints(0, 0)], layoutWidth: 80);

        Assert.Equal(80, layout.Slots[0].Height, 3);
    }

    [Fact]
    public void visible_range_covers_only_intersecting_pages()
    {
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild(Uniform(10, 100, 100), layoutWidth: 100);  // 10 slots of height 100

        Assert.Equal((0, 0), layout.VisibleRange(0, 50));
        Assert.Equal((0, 1), layout.VisibleRange(0, 150));
        Assert.Equal((2, 4), layout.VisibleRange(250, 450));
        Assert.Equal((9, 9), layout.VisibleRange(950, 1200));
    }

    [Fact]
    public void visible_range_is_empty_past_the_end()
    {
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild(Uniform(2, 100, 100), layoutWidth: 100);

        Assert.Equal((-1, -1), layout.VisibleRange(5000, 6000));
    }

    [Fact]
    public void dominant_page_is_the_one_filling_most_of_the_viewport()
    {
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild(Uniform(5, 100, 100), layoutWidth: 100);

        // Viewport 90..190: 10px of page 0, 90px of page 1.
        Assert.Equal(1, layout.DominantPage(90, 190));
        // Viewport 110..190 sits wholly in page 1.
        Assert.Equal(1, layout.DominantPage(110, 190));
        // Straddling 0/1 evenly biases to the first, which is stable.
        Assert.Equal(0, layout.DominantPage(50, 150));
    }

    [Fact]
    public void fit_width_zoom_scales_layout_width_to_the_viewport()
    {
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(2), layoutWidth: 500);

        Assert.Equal(2.0, layout.FitWidthZoom(1000), 6);
        Assert.Equal(0.5, layout.FitWidthZoom(250), 6);
    }

    [Fact]
    public void rebuilding_replaces_the_previous_stack()
    {
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild(Uniform(10, 100, 100), layoutWidth: 100);
        Assert.Equal(10, layout.PageCount);

        layout.Rebuild(Uniform(3, 100, 100), layoutWidth: 100);
        Assert.Equal(3, layout.PageCount);
        Assert.Equal(300, layout.TotalHeight, 3);
    }

    [Fact]
    public void an_empty_document_has_no_slots_and_no_height()
    {
        var layout = new ContinuousLayout();
        layout.Rebuild([], layoutWidth: 100);

        Assert.Equal(0, layout.PageCount);
        Assert.Equal(0, layout.TotalHeight, 6);
        Assert.Equal((-1, -1), layout.VisibleRange(0, 100));
        Assert.Equal(0, layout.DominantPage(0, 100));
    }

    [Fact]
    public void top_of_matches_the_slot_and_is_safe_out_of_range()
    {
        var layout = new ContinuousLayout(pageGap: 5);
        layout.Rebuild(Uniform(4, 100, 100), layoutWidth: 100);

        Assert.Equal(0, layout.TopOf(0), 3);
        Assert.Equal(105, layout.TopOf(1), 3);
        Assert.Equal(0, layout.TopOf(-1), 3);
        Assert.Equal(0, layout.TopOf(99), 3);
    }
}
