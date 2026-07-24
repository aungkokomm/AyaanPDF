using PdfEditorApp.Viewport;

namespace PdfEditorApp.Tests;

public class PanZoomStateTests
{
    /// <summary>
    /// ResetView now fits content to the viewport width, so straight after it
    /// the content exactly fills the width and there is no horizontal pan
    /// range at all. Zoom in past fit to get a state where panning is
    /// actually meaningful.
    /// </summary>
    private static PanZoomState CreateLargeContentState()
    {
        var state = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        state.ResetView(contentWidth: 2000, contentHeight: 1500);
        // 2.5x past fit: content spans 2000x1500 on screen against an 800x600
        // viewport, giving real pan range in both axes.
        state.ZoomAtPoint(2.5, 0, 0);
        state.SnapRawToDisplayed();
        return state;
    }

    [Fact]
    public void ResetView_fits_content_to_the_viewport_width()
    {
        var state = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        state.ResetView(contentWidth: 2000, contentHeight: 1500);

        // Fit width: 800 / 2000 = 0.4, and the page spans the viewport exactly.
        Assert.Equal(0.4, state.Scale, precision: 6);
        Assert.Equal(800, state.ScaledContentWidth, precision: 6);
        Assert.Equal(0, state.PanX, precision: 6);
    }

    [Fact]
    public void ResetView_reports_fit_as_100_percent_regardless_of_content_pixel_size()
    {
        // The same page rendered at two different pixel widths (a 100% vs a
        // 200% display, say) must both read as 100% zoom once fitted.
        var lowDpi = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        lowDpi.ResetView(contentWidth: 800, contentHeight: 600);

        var highDpi = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        highDpi.ResetView(contentWidth: 1600, contentHeight: 1200);

        Assert.Equal(1.0, lowDpi.ZoomRelativeToFit, precision: 6);
        Assert.Equal(1.0, highDpi.ZoomRelativeToFit, precision: 6);
    }

    // ---- Regressions from testing the actual running app ----
    //
    // The suite previously passed while all three of these were broken on
    // screen, because every test drove PanZoomState through the call sequence
    // I had assumed rather than the one the app really performs.

    [Fact]
    public void Zoom_readout_is_exactly_the_fraction_of_viewport_the_page_covers()
    {
        var state = new PanZoomState { ViewportWidth = 1000, ViewportHeight = 800 };
        state.ResetView(contentWidth: 500, contentHeight: 400);

        Assert.Equal(1.0, state.ZoomRelativeToFit, precision: 6);
        Assert.Equal(1000, state.ScaledContentWidth, precision: 6);

        state.ZoomAtPoint(0.5, 500, 400);

        // Half the fit scale: the page now covers half the viewport width,
        // so the readout must say 50% — not drift to some other number.
        Assert.Equal(0.5, state.ZoomRelativeToFit, precision: 6);
        Assert.Equal(state.ScaledContentWidth / state.ViewportWidth, state.ZoomRelativeToFit, precision: 6);
    }

    [Fact]
    public void Zoom_readout_tracks_repeated_zoom_steps_proportionally()
    {
        // The observed bug: two 1.25x steps moved the readout 56% -> 62%
        // instead of 56% -> 87.5%, because a re-render landing in between
        // silently moved the fit reference.
        var state = new PanZoomState { ViewportWidth = 1200, ViewportHeight = 900 };
        state.ResetView(contentWidth: 600, contentHeight: 600);
        state.ZoomAtPoint(0.56, 600, 450);

        double before = state.ZoomRelativeToFit;

        state.ZoomAtPoint(1.25, 600, 450);
        // A sharper re-render arrives mid-gesture, as it does in the app.
        state.ReplaceContentPreservingView(823, 823);
        state.ZoomAtPoint(1.25, 600, 450);

        Assert.Equal(before * 1.25 * 1.25, state.ZoomRelativeToFit, precision: 6);
    }

    [Fact]
    public void Fit_reference_follows_the_viewport_when_the_window_is_resized()
    {
        var state = new PanZoomState { ViewportWidth = 600, ViewportHeight = 800 };
        state.ResetView(contentWidth: 600, contentHeight: 600);
        Assert.True(state.IsAtFitWidth);

        // Maximize: the viewport doubles. Fit must follow it, so the page
        // that used to fill the window now covers only half of it.
        state.ViewportWidth = 1200;

        Assert.Equal(0.5, state.ZoomRelativeToFit, precision: 6);
        Assert.False(state.IsAtFitWidth);

        // And re-fitting must genuinely fill the new width.
        state.FitToWidth();
        Assert.Equal(1200, state.ScaledContentWidth, precision: 6);
        Assert.True(state.IsAtFitWidth);
    }

    [Fact]
    public void FitToWidth_works_without_any_re_render_arriving()
    {
        // Fit Width did nothing in the app because it only requested a render
        // and let the arrival path do the fitting. It must stand alone.
        var state = new PanZoomState { ViewportWidth = 1000, ViewportHeight = 800 };
        state.ResetView(contentWidth: 500, contentHeight: 400);
        state.ZoomAtPoint(3.0, 500, 400);
        Assert.False(state.IsAtFitWidth);

        state.FitToWidth();

        Assert.True(state.IsAtFitWidth);
        Assert.Equal(1000, state.ScaledContentWidth, precision: 6);
    }

    [Fact]
    public void ReplaceContentPreservingView_keeps_the_zoom_readout_stable()
    {
        var state = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        state.ResetView(contentWidth: 800, contentHeight: 600);
        state.ZoomAtPoint(2.0, 400, 300);

        double zoomBefore = state.ZoomRelativeToFit;

        // A sharper re-render lands at double the pixel size; nothing about
        // what the user sees changed, so the readout must not move either.
        state.ReplaceContentPreservingView(1600, 1200);

        Assert.Equal(zoomBefore, state.ZoomRelativeToFit, precision: 6);
    }

    [Fact]
    public void ResetView_centers_content_shorter_than_the_viewport_vertically()
    {
        var state = new PanZoomState { ViewportWidth = 800, ViewportHeight = 600 };
        // Wide and short: fitting to width leaves vertical slack to center.
        state.ResetView(contentWidth: 1600, contentHeight: 400);

        Assert.Equal(0.5, state.Scale, precision: 6);
        Assert.Equal(0, state.PanX, precision: 6);
        Assert.Equal((600 - 200) / 2.0, state.PanY, precision: 6);
    }

    [Fact]
    public void ZoomAtPoint_keeps_the_content_point_under_the_cursor_fixed()
    {
        var state = CreateLargeContentState();
        state.PanBy(-300, -200); // move somewhere non-trivial first

        double cursorX = 350;
        double cursorY = 250;

        // The content-local coordinate under the cursor before zooming.
        double contentXBefore = (cursorX - state.PanX) / state.Scale;
        double contentYBefore = (cursorY - state.PanY) / state.Scale;

        state.ZoomAtPoint(1.5, cursorX, cursorY);

        double contentXAfter = (cursorX - state.PanX) / state.Scale;
        double contentYAfter = (cursorY - state.PanY) / state.Scale;

        Assert.Equal(contentXBefore, contentXAfter, precision: 6);
        Assert.Equal(contentYBefore, contentYAfter, precision: 6);
    }

    [Fact]
    public void ZoomAtPoint_clamps_to_min_and_max_scale()
    {
        var state = CreateLargeContentState();

        state.ZoomAtPoint(0.0001, 0, 0);
        Assert.Equal(PanZoomState.MinScale, state.Scale, precision: 6);

        // Reset then zoom up aggressively to hit the max clamp.
        state = CreateLargeContentState();
        state.ZoomAtPoint(1_000_000, 0, 0);
        Assert.Equal(PanZoomState.MaxScale, state.Scale, precision: 6);
    }

    [Fact]
    public void PanBy_moves_linearly_when_comfortably_within_bounds()
    {
        var state = CreateLargeContentState();
        double before = state.PanX;

        state.PanBy(-50, 0);

        Assert.Equal(before - 50, state.PanX, precision: 6);
    }

    [Fact]
    public void PanBy_past_the_edge_is_resisted_not_a_hard_stop()
    {
        var state = CreateLargeContentState();

        // Drag far past the right/bottom edge (raw pan goes strongly positive).
        state.PanBy(5000, 5000);

        // Resisted position should have moved (not hard-clamped exactly to 0)
        // but should stay bounded near the edge, nowhere close to the raw 5000.
        Assert.True(state.PanX > 0, "expected some give past the edge");
        Assert.True(state.PanX < 200, "expected resistance to keep it close to the edge");
    }

    [Fact]
    public void PanBy_resistance_shrinks_as_overshoot_grows()
    {
        var state = CreateLargeContentState();
        state.PanBy(500, 0);
        double displayedAtModestOvershoot = state.PanX;

        state = CreateLargeContentState();
        state.PanBy(50_000, 0);
        double displayedAtHugeOvershoot = state.PanX;

        // Diminishing returns: a much bigger overshoot should not translate
        // into a proportionally bigger on-screen displacement.
        Assert.True(displayedAtHugeOvershoot - displayedAtModestOvershoot < displayedAtModestOvershoot);
    }

    [Fact]
    public void TryGetSpringBackTarget_reports_in_bounds_after_snapping()
    {
        var state = CreateLargeContentState();
        state.PanBy(5000, 5000);

        Assert.True(state.TryGetSpringBackTarget(out double targetX, out double targetY));

        state.SetPanDirect(targetX, targetY);
        state.SnapRawToDisplayed();

        Assert.False(state.TryGetSpringBackTarget(out _, out _));
    }

    [Fact]
    public void TryGetSpringBackTarget_is_false_when_already_in_bounds()
    {
        var state = CreateLargeContentState();
        state.PanBy(-100, -50); // still well within range

        Assert.False(state.TryGetSpringBackTarget(out _, out _));
    }

    [Fact]
    public void ReplaceContentPreservingView_keeps_on_screen_footprint_and_pan()
    {
        var state = CreateLargeContentState();
        state.ZoomAtPoint(1.5, 400, 300);
        state.PanBy(-120, -80);

        double footprintBefore = state.ScaledContentWidth;
        double panXBefore = state.PanX;
        double panYBefore = state.PanY;

        // A debounced re-render lands at a different pixel width than the
        // bitmap currently on screen (the normal case: we asked for one
        // width, but by the time it lands the zoom may have moved on).
        state.ReplaceContentPreservingView(newContentWidth: 3000, newContentHeight: 2250);

        Assert.Equal(footprintBefore, state.ScaledContentWidth, precision: 3);
        Assert.Equal(panXBefore, state.PanX, precision: 3);
        Assert.Equal(panYBefore, state.PanY, precision: 3);
    }
}
