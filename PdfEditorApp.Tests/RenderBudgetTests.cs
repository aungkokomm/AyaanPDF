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
    public void deep_zoom_gets_a_much_sharper_render_than_before()
    {
        // At 800% on a 150% display an 800-DIP page wants 9600px. It will
        // still be capped, but the cap has to be high enough that text is not
        // obviously upscaled: 2600px was a 3.7x magnification of the render.
        int w = Budget.SharpWidthFor(800, zoom: 8.0, rasterizationScale: 1.5, aspect: 1.4);

        Assert.True(w >= 4000, $"deep zoom should render at least 4000px wide, got {w}");

        // And still inside the area budget for a portrait page.
        long pixels = (long)w * (long)(w * 1.4);
        Assert.True(pixels <= Budget.MaxSharpPixels, $"{pixels} exceeds the area cap");
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

    // ---------------- When tiles take over ----------------

    [Fact]
    public void a_whole_page_render_is_kept_while_it_is_still_exact()
    {
        // 800 DIP page at 2x on a 150% display wants 2400px, well inside the
        // 4200px cap, so one bitmap is both exact and cheaper than a grid.
        Assert.False(Budget.NeedsTiles(800, zoom: 2.0, rasterizationScale: 1.5, aspect: 1.29));
    }

    [Fact]
    public void tiles_take_over_once_the_page_would_be_upscaled()
    {
        // 800 * 8 * 1.5 = 9600px wanted against a 4200px cap: everything past
        // the cap is upscaling, which is exactly the soft text tiles exist to
        // remove.
        Assert.True(Budget.NeedsTiles(800, zoom: 8.0, rasterizationScale: 1.5, aspect: 1.29));
    }

    [Fact]
    public void the_switch_tracks_the_caps_rather_than_a_fixed_zoom()
    {
        // The same zoom that needs tiles under the shipping caps must not need
        // them once the whole-page cap is raised past what the screen wants.
        var generous = new RenderBudget(maxSharpWidth: 20000, maxSharpPixels: 800_000_000);

        Assert.True(Budget.NeedsTiles(800, zoom: 8.0, rasterizationScale: 1.5, aspect: 1.29));
        Assert.False(generous.NeedsTiles(800, zoom: 8.0, rasterizationScale: 1.5, aspect: 1.29));
    }

    [Fact]
    public void the_area_cap_can_trigger_tiles_at_a_legal_width()
    {
        // A panoramic page: 3000px wide is under the width cap, but at aspect 6
        // that is 18000px tall and 54M pixels, so the AREA cap binds and the
        // page has outgrown a single bitmap even though its width has not.
        Assert.True(Budget.NeedsTiles(2000, zoom: 1.5, rasterizationScale: 1.0, aspect: 6.0));
    }

    [Fact]
    public void a_page_with_no_width_never_asks_for_tiles()
    {
        // Slots exist before their size is known; tiling an unsized page would
        // divide by zero in the grid.
        Assert.False(Budget.NeedsTiles(0, zoom: 8.0, rasterizationScale: 2.0, aspect: 1.29));
    }

    [Fact]
    public void the_switch_has_a_dead_zone_so_it_cannot_flicker()
    {
        // Zoom exactly at the cap must stay on the whole-page path, and stay
        // there for a sliver beyond it, or a view parked on the boundary would
        // flip between the two renderers on every pass.
        // 4200px cap / 800 DIP / 1.0 scale = zoom 5.25.
        Assert.False(Budget.NeedsTiles(800, zoom: 5.25, rasterizationScale: 1.0, aspect: 1.29));
        Assert.False(Budget.NeedsTiles(800, zoom: 5.30, rasterizationScale: 1.0, aspect: 1.29));
        Assert.True(Budget.NeedsTiles(800, zoom: 6.00, rasterizationScale: 1.0, aspect: 1.29));
    }
}
