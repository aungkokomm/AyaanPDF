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

    // ---------------- View rotation ----------------

    [Fact]
    public void a_turned_view_gives_every_card_the_other_shape()
    {
        // Tall pages laid on their side make short wide cards, and the stack
        // gets shorter with them. If the cards did not change shape the pages
        // would be drawn turned inside upright boxes, overhanging their
        // neighbours.
        var layout = new ContinuousLayout(pageGap: 0);
        layout.Rebuild(Uniform(3, 100, 200), layoutWidth: 100, viewRotation: 90);

        foreach (var slot in layout.Slots)
        {
            Assert.Equal(100, slot.Width, 6);
            Assert.Equal(50, slot.Height, 6);
        }

        Assert.Equal(150, layout.TotalHeight, 6);
    }

    [Fact]
    public void the_stack_is_untouched_when_nothing_is_turned()
    {
        // The safety property for every document nobody rotates.
        var upright = new ContinuousLayout(pageGap: 5);
        upright.Rebuild(Uniform(4, 100, 200), layoutWidth: 100);

        var explicitly = new ContinuousLayout(pageGap: 5);
        explicitly.Rebuild(Uniform(4, 100, 200), layoutWidth: 100, viewRotation: 0);

        Assert.Equal(upright.TotalHeight, explicitly.TotalHeight, 6);
        Assert.Equal(200, upright.Slots[0].Height, 6);
    }

    [Fact]
    public void half_a_turn_leaves_the_stack_the_same_height()
    {
        var half = new ContinuousLayout(pageGap: 5);
        half.Rebuild(Uniform(4, 100, 200), layoutWidth: 100, viewRotation: 180);

        Assert.Equal(200, half.Slots[0].Height, 6);
        Assert.Equal(180, half.ViewRotation);
    }

    [Fact]
    public void fit_width_still_answers_when_the_view_is_turned()
    {
        // Fit-width is a pure function of the ONE fixed layout width. Cards
        // that came out wider than it when turned would silently break it for
        // the whole document, so every card keeps the layout width.
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(3, 100, 200), layoutWidth: 800, viewRotation: 270);

        Assert.Equal(800, layout.LayoutWidth, 6);
        Assert.All(layout.Slots, s => Assert.Equal(800, s.Width, 6));
        Assert.Equal(2.0, layout.FitWidthZoom(1600), 6);
    }

    // ---------------- Single-page view ----------------

    [Fact]
    public void single_page_view_lays_out_one_page()
    {
        // Not every page with the rest hidden: the stack's height IS the scroll
        // range, so leaving them in would let the reader scroll through a
        // document's worth of nothing below the page they are on.
        var layout = new ContinuousLayout(pageGap: 12);
        layout.Rebuild(Uniform(10, 100, 200), layoutWidth: 100, viewRotation: 0, onlyPage: 4);

        Assert.Equal(1, layout.PageCount);
        Assert.Equal(200, layout.TotalHeight, 6);

        // No trailing gap, and the page starts at the top.
        Assert.Equal(0, layout.Slots[0].Top, 6);
    }

    [Fact]
    public void the_one_slot_knows_which_page_it_is()
    {
        // THE thing that breaks if this is got wrong. The card's position in
        // the list is no longer its page number, so anything that treats the
        // two as the same shows page 5's annotations on page 40.
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(10, 100, 200), layoutWidth: 100, onlyPage: 7);

        Assert.Equal(7, layout.Slots[0].PageIndex);
        Assert.Equal(7, layout.DominantPage(0, 200));
    }

    [Fact]
    public void lookups_are_by_page_number_not_by_position()
    {
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(10, 100, 200), layoutWidth: 100, onlyPage: 7);

        // The page being shown answers; the others are simply not there.
        Assert.Equal(200, layout.HeightOf(7), 6);
        Assert.Equal(0, layout.TopOf(7), 6);
        Assert.NotNull(layout.SlotForPage(7));

        Assert.Equal(0, layout.HeightOf(0), 6);
        Assert.Null(layout.SlotForPage(0));
        Assert.Null(layout.SlotForPage(9));
    }

    [Fact]
    public void continuous_view_still_answers_by_page_the_same_way()
    {
        // The lookups changed from position-based to page-based. In continuous
        // view they must give exactly the answers they always did.
        var layout = new ContinuousLayout(pageGap: 5);
        layout.Rebuild(Uniform(4, 100, 200), layoutWidth: 100);

        for (int page = 0; page < 4; page++)
        {
            Assert.Equal(page * 205, layout.TopOf(page), 6);
            Assert.Equal(200, layout.HeightOf(page), 6);
            Assert.Equal(page, layout.SlotForPage(page)!.Value.PageIndex);
        }

        Assert.Null(layout.SlotForPage(4));
        Assert.Null(layout.SlotForPage(-1));
    }

    [Fact]
    public void a_single_page_can_also_be_turned()
    {
        // The two modes are independent, and both change the card's shape.
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(10, 100, 200), layoutWidth: 100, viewRotation: 90, onlyPage: 3);

        Assert.Equal(1, layout.PageCount);
        Assert.Equal(3, layout.Slots[0].PageIndex);
        Assert.Equal(50, layout.Slots[0].Height, 6);
        Assert.Equal(90, layout.Slots[0].Transform.Rotation);
    }

    [Fact]
    public void an_out_of_range_page_shows_nothing_rather_than_the_wrong_page()
    {
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(3, 100, 200), layoutWidth: 100, onlyPage: 99);

        Assert.Equal(0, layout.PageCount);
        Assert.Equal(0, layout.TotalHeight, 6);
        Assert.Equal((-1, -1), layout.VisibleRange(0, 100));
    }

    [Fact]
    public void a_turned_page_still_carries_its_own_content_box()
    {
        // The content box is what every rect on the page is measured against,
        // and it must NOT change with rotation or every overlay in the app
        // would be laid out against the wrong scale.
        var layout = new ContinuousLayout();
        layout.Rebuild(Uniform(1, 100, 200), layoutWidth: 800, viewRotation: 90);

        var t = layout.Slots[0].Transform;

        Assert.Equal(800, t.ContentWidth, 6);
        Assert.Equal(1600, t.ContentHeight, 6);
        Assert.Equal(90, t.Rotation);
    }
}
