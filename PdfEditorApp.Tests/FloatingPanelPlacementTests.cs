using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where a movable panel is allowed to be.
///
/// The rules that matter are the ones about NOT losing it. A panel that can be
/// dragged anywhere can be dragged off the screen, and the only thing that
/// could bring it back is the title bar that went with it.
/// </summary>
public class FloatingPanelPlacementTests
{
    private const double W = 264;
    private const double H = 320;
    private const double View = 1200;
    private const double Tall = 900;

    // ---------------- opening ----------------

    [Fact]
    public void it_opens_against_the_right_edge()
    {
        // The left is the tool rail and the page, the middle is the selection.
        // The right is the one place reliably not the thing being worked on.
        var at = FloatingPanelPlacement.Opening(W, H, View, Tall);

        Assert.Equal(View - FloatingPanelPlacement.EdgeMargin - W, at.Left, 6);
        Assert.Equal(FloatingPanelPlacement.EdgeMargin, at.Top, 6);
    }

    [Fact]
    public void it_opens_below_the_chrome_that_already_owns_the_top()
    {
        // That chrome draws ABOVE the panel, so a position under it is not
        // merely ugly, it is unreachable.
        var at = FloatingPanelPlacement.Opening(W, H, View, Tall, topInset: 96);

        Assert.True(at.Top >= 96, $"opened at {at.Top}, under 96 points of chrome");
    }

    [Fact]
    public void it_opens_on_screen_even_when_the_window_is_smaller_than_it_is()
    {
        var at = FloatingPanelPlacement.Opening(W, H, 120, 100);

        Assert.True(at.Left > -W, "opened entirely off the left");
        Assert.True(at.Left < 120, "opened entirely off the right");
        Assert.True(at.Top >= 0, "opened above the top");
    }

    // ---------------- keeping hold of it ----------------

    [Fact]
    public void a_panel_dragged_off_the_right_keeps_a_strip_in_view()
    {
        var at = FloatingPanelPlacement.Clamp(5000, 100, W, H, View, Tall);

        double visible = View - at.Left;

        Assert.True(
            visible >= FloatingPanelPlacement.MinimumVisible - 0.001,
            $"only {visible} points left on screen");
    }

    [Fact]
    public void a_panel_dragged_off_the_left_keeps_a_strip_in_view()
    {
        var at = FloatingPanelPlacement.Clamp(-5000, 100, W, H, View, Tall);

        double visible = at.Left + W;

        Assert.True(
            visible >= FloatingPanelPlacement.MinimumVisible - 0.001,
            $"only {visible} points left on screen");
    }

    [Fact]
    public void a_panel_may_hang_off_an_edge_because_that_is_the_point_of_moving_it()
    {
        // Pinning the whole panel inside the viewport means it can never be
        // pushed mostly out of the way, which is the entire reason it moves.
        var at = FloatingPanelPlacement.Clamp(View - 90, 100, W, H, View, Tall);

        Assert.True(at.Left + W > View, "the panel was refused a position it should have kept");
    }

    [Fact]
    public void the_title_bar_can_never_go_above_the_top()
    {
        // It is the only handle. Lose it and the panel cannot be moved again.
        var at = FloatingPanelPlacement.Clamp(100, -5000, W, H, View, Tall);

        Assert.True(at.Top >= FloatingPanelPlacement.EdgeMargin - 0.001, $"the title bar went to {at.Top}");
    }

    [Fact]
    public void the_title_bar_can_never_go_under_the_chrome_either()
    {
        var at = FloatingPanelPlacement.Clamp(100, 0, W, H, View, Tall, topInset: 96);

        Assert.True(at.Top >= 96, $"the title bar went to {at.Top}, under 96 points of chrome");
    }

    [Fact]
    public void a_panel_dragged_off_the_bottom_keeps_its_title_bar_in_view()
    {
        var at = FloatingPanelPlacement.Clamp(100, 5000, W, H, View, Tall);

        Assert.True(at.Top < Tall, $"the title bar went to {at.Top}, off a viewport {Tall} tall");
    }

    [Fact]
    public void a_position_already_legal_is_left_exactly_alone()
    {
        var at = FloatingPanelPlacement.Clamp(300, 200, W, H, View, Tall);

        Assert.Equal(300, at.Left, 6);
        Assert.Equal(200, at.Top, 6);
    }

    // ---------------- the window changing under it ----------------

    [Fact]
    public void shrinking_the_window_brings_a_far_panel_back()
    {
        // Parked against the right edge of a wide window, then the window is
        // made narrow. Nothing else would ever recover it.
        var parked = FloatingPanelPlacement.Clamp(1800, 100, W, H, 2100, Tall);
        Assert.True(parked.Left > 1000);

        var recovered = FloatingPanelPlacement.Clamp(
            parked.Left, parked.Top, W, H, 700, Tall);

        Assert.True(
            recovered.Left < 700,
            $"still at {recovered.Left} in a viewport 700 wide");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(50, 40)]
    public void a_collapsed_viewport_does_not_throw(double w, double h)
    {
        // A window being restored is momentarily zero-sized, and Math.Clamp
        // THROWS when its minimum exceeds its maximum. An unguarded clamp here
        // is an ArgumentException during an ordinary resize.
        var at = FloatingPanelPlacement.Clamp(100, 100, W, H, w, h);
        var opened = FloatingPanelPlacement.Opening(W, H, w, h);

        Assert.True(double.IsFinite(at.Left) && double.IsFinite(at.Top));
        Assert.True(double.IsFinite(opened.Left) && double.IsFinite(opened.Top));
    }

    [Fact]
    public void a_panel_narrower_than_the_strip_is_kept_whole()
    {
        double narrow = FloatingPanelPlacement.MinimumVisible / 2;

        var at = FloatingPanelPlacement.Clamp(5000, 100, narrow, H, View, Tall);

        Assert.True(at.Left + narrow <= View, "a panel smaller than the strip was pushed off anyway");
    }

    // ---------------- and it sits above the toolbar it is dragged away from ----------------

    [Fact]
    public void the_panel_floats_above_the_object_toolbar()
    {
        // The toolbar sits on the selection, which is exactly what a person
        // drags the panel away from. A panel sliding under it looks like it
        // has gone.
        Assert.True(
            FloatingPanelPlacement.Elevation > ObjectToolbarPlacement.Elevation,
            "the panel would be drawn under the object toolbar");
    }
}
