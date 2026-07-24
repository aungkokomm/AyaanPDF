using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class RenderBudgetTests
{
    // The production defaults, so these tests pin real behaviour rather than
    // a configuration nothing ships with.
    private static readonly RenderBudget Budget = new();

    [Fact]
    public void sharp_width_tracks_zoom_and_display_scale()
    {
        // 800 DIP page at 2x zoom on a 150% display wants 800*2*1.5 = 2400px.
        Assert.Equal(2400, Budget.SharpWidthFor(800, zoom: 2.0, rasterizationScale: 1.5, aspect: 1.0));
    }

    [Fact]
    public void sharp_width_never_drops_below_the_base_tier()
    {
        // Zoomed far out, the ideal width is tiny, but re-rendering BELOW the
        // cached base render would make the page worse, not better.
        int w = Budget.SharpWidthFor(800, zoom: 0.2, rasterizationScale: 1.0, aspect: 1.0);
        Assert.Equal(Budget.BaseWidth, w);
    }

    [Fact]
    public void sharp_width_is_capped_by_max_width()
    {
        int w = Budget.SharpWidthFor(800, zoom: 8.0, rasterizationScale: 2.0, aspect: 1.0);
        Assert.Equal(Budget.MaxSharpWidth, w);
    }

    [Fact]
    public void a_tall_page_is_bound_by_the_area_cap_not_the_width_cap()
    {
        // A 1:6 panoramic page at the width cap would be 2600 x 15600 = 40M
        // pixels, about 162MB. The area cap has to bite first.
        int w = Budget.SharpWidthFor(800, zoom: 8.0, rasterizationScale: 2.0, aspect: 6.0);

        Assert.True(w < Budget.MaxSharpWidth, $"expected the area cap to bind, got {w}");
        long pixels = (long)w * (long)(w * 6.0);
        Assert.True(pixels <= Budget.MaxSharpPixels, $"{pixels} pixels exceeds the cap");
    }

    [Fact]
    public void a_square_page_at_the_area_cap_stays_within_budget()
    {
        int w = Budget.SharpWidthFor(4000, zoom: 4.0, rasterizationScale: 2.0, aspect: 1.0);
        long pixels = (long)w * w;
        Assert.True(pixels <= Budget.MaxSharpPixels, $"{pixels} pixels exceeds the cap");
    }

    [Fact]
    public void a_degenerate_slot_width_falls_back_to_the_base_tier()
    {
        Assert.Equal(Budget.BaseWidth, Budget.SharpWidthFor(0, 2.0, 1.0, 1.0));
        Assert.Equal(Budget.BaseWidth, Budget.SharpWidthFor(-5, 2.0, 1.0, 1.0));
    }

    [Fact]
    public void an_unrendered_page_always_wants_a_render()
    {
        Assert.True(Budget.ShouldResharpen(currentWidth: 0, desiredWidth: 900));
    }

    [Fact]
    public void a_small_sharpness_gain_does_not_justify_a_rerender()
    {
        Assert.False(Budget.ShouldResharpen(1000, 1100));
    }

    [Fact]
    public void a_large_sharpness_gain_does_justify_a_rerender()
    {
        Assert.True(Budget.ShouldResharpen(1000, 1400));
    }

    [Fact]
    public void a_high_dpi_display_sharpens_at_the_default_zoom()
    {
        // The case that matters most and the one a too-high threshold silently
        // breaks: a 150% display sitting at zoom 1.0, where the page is soft
        // unless the sharpen pass actually runs. Caught by tracing the real
        // app, which showed want=1200 have=900 being rejected.
        double slotWidth = 800;
        int desired = Budget.SharpWidthFor(slotWidth, zoom: 1.0, rasterizationScale: 1.5, aspect: 1.0);

        Assert.True(desired > Budget.BaseWidth, $"desired {desired} should beat base {Budget.BaseWidth}");
        Assert.True(
            Budget.ShouldResharpen(Budget.BaseWidth, desired),
            $"a 150% display at zoom 1.0 must sharpen: have={Budget.BaseWidth} want={desired}");
    }

    [Fact]
    public void sharpening_is_stable_once_it_has_run()
    {
        // After one sharpen at a given zoom, asking again must NOT re-render,
        // or the debounced pass would loop forever re-rasterizing the page.
        int desired = Budget.SharpWidthFor(800, zoom: 1.0, rasterizationScale: 1.5, aspect: 1.0);
        Assert.False(Budget.ShouldResharpen(desired, desired));
    }

    [Fact]
    public void zooming_out_never_triggers_a_rerender()
    {
        // Rendering DOWN would discard a good bitmap for a worse one and
        // flicker when the user zooms back in.
        Assert.False(Budget.ShouldResharpen(2400, 900));
        Assert.False(Budget.ShouldResharpen(2400, 2399));
    }

    [Fact]
    public void widen_clamps_to_the_document_bounds()
    {
        Assert.Equal((0, 5), RenderBudget.Widen(0, 3, margin: 2, pageCount: 20));
        Assert.Equal((0, 6), RenderBudget.Widen(1, 4, margin: 2, pageCount: 20));
        Assert.Equal((15, 19), RenderBudget.Widen(17, 19, margin: 2, pageCount: 20));
        Assert.Equal((0, 0), RenderBudget.Widen(0, 0, margin: 5, pageCount: 1));
    }

    [Fact]
    public void widen_reports_empty_for_an_empty_range()
    {
        Assert.Equal((-1, -1), RenderBudget.Widen(-1, -1, margin: 2, pageCount: 20));
        Assert.Equal((-1, -1), RenderBudget.Widen(0, 3, margin: 2, pageCount: 0));
    }
}
