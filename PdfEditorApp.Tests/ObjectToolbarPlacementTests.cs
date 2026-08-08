using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The floating object toolbar's geometry. These are the cases that look wrong
/// on screen rather than throw: a toolbar sitting off the edge of the window, or
/// covering the object it is meant to act on.
/// </summary>
public class ObjectToolbarPlacementTests
{
    // A roomy viewport with a small selection in the middle of it, which is the
    // ordinary case every other test varies from.
    private const double ViewW = 1000;
    private const double ViewH = 800;
    private const double BarW = 160;
    private const double BarH = 40;

    [Fact]
    public void it_sits_above_the_selection_and_centred_on_it()
    {
        var p = ObjectToolbarPlacement.Place(
            selLeft: 400, selTop: 300, selRight: 600, selBottom: 500,
            BarW, BarH, ViewW, ViewH);

        Assert.False(p.Below);
        // Centre of the selection is x=500, so a 160-wide bar starts at 420.
        Assert.Equal(420, p.Left, 3);
        // Bottom edge of the bar sits one Gap above the selection's top.
        Assert.Equal(300 - ObjectToolbarPlacement.Gap - BarH, p.Top, 3);
    }

    [Fact]
    public void it_flips_below_when_the_selection_is_against_the_top_of_the_viewport()
    {
        var p = ObjectToolbarPlacement.Place(
            selLeft: 400, selTop: 5, selRight: 600, selBottom: 120,
            BarW, BarH, ViewW, ViewH);

        Assert.True(p.Below);
        Assert.Equal(120 + ObjectToolbarPlacement.Gap, p.Top, 3);
    }

    [Fact]
    public void it_never_hangs_off_the_left_edge()
    {
        // Selection hard against the left: centring would put the bar at -70.
        var p = ObjectToolbarPlacement.Place(
            selLeft: 0, selTop: 300, selRight: 20, selBottom: 400,
            BarW, BarH, ViewW, ViewH);

        Assert.Equal(ObjectToolbarPlacement.EdgeMargin, p.Left, 3);
    }

    [Fact]
    public void it_never_hangs_off_the_right_edge()
    {
        var p = ObjectToolbarPlacement.Place(
            selLeft: 980, selTop: 300, selRight: 1000, selBottom: 400,
            BarW, BarH, ViewW, ViewH);

        Assert.Equal(ViewW - ObjectToolbarPlacement.EdgeMargin - BarW, p.Left, 3);
    }

    [Fact]
    public void a_selection_flipped_below_but_running_past_the_bottom_is_pulled_back_in()
    {
        // No room above (top edge) AND the selection ends past the viewport, so
        // both preferences fail and the clamp has to be what saves it.
        var p = ObjectToolbarPlacement.Place(
            selLeft: 400, selTop: 0, selRight: 600, selBottom: 5000,
            BarW, BarH, ViewW, ViewH);

        Assert.True(p.Below);
        Assert.True(p.Top + BarH <= ViewH,
            $"bar bottom {p.Top + BarH} escaped a {ViewH}-high viewport");
    }

    [Fact]
    public void a_viewport_narrower_than_the_toolbar_still_yields_a_visible_position()
    {
        // Degenerate, but a user CAN drag the window this small, and a negative
        // Left here would park the buttons out of reach.
        var p = ObjectToolbarPlacement.Place(
            selLeft: 10, selTop: 40, selRight: 60, selBottom: 90,
            toolbarWidth: 300, toolbarHeight: BarH,
            viewportWidth: 120, viewportHeight: 200);

        Assert.True(p.Left >= 0, $"Left was {p.Left}");
        Assert.True(p.Top >= 0, $"Top was {p.Top}");
    }

    [Fact]
    public void it_tracks_the_selection_rather_than_snapping_to_a_fixed_spot()
    {
        // Guards the whole point of a contextual toolbar: two different
        // selections must not produce the same position.
        var a = ObjectToolbarPlacement.Place(100, 300, 200, 400, BarW, BarH, ViewW, ViewH);
        var b = ObjectToolbarPlacement.Place(700, 300, 800, 400, BarW, BarH, ViewW, ViewH);

        Assert.NotEqual(a.Left, b.Left);
    }
}
