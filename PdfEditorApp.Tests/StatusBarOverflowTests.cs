using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What the floating bar gives up when the canvas is too narrow for it.
///
/// The bar sits in the canvas column, so a laptop with the thumbnail panel and
/// the property panel both open leaves it a good deal less room than the
/// window suggests. A horizontal StackPanel that does not fit is simply
/// clipped, and what gets clipped is the two ends: the drag grip and find.
/// </summary>
public class StatusBarOverflowTests
{
    // Roughly what the bar measures at, so the numbers below read like widths
    // rather than arbitrary constants.
    private const double Natural = 560;
    private const double ViewModes = 120;
    private const double NavHistory = 60;

    [Fact]
    public void a_wide_canvas_keeps_everything()
    {
        Assert.Equal(BarGroups.All, StatusBarOverflow.Decide(1200, Natural, ViewModes, NavHistory));
    }

    [Fact]
    public void exactly_enough_room_is_enough()
    {
        // The boundary is inclusive on purpose: a bar that fits to the pixel
        // fits, and rounding a group away at the exact width would make the
        // last DIP of a resize do something visible for no reason.
        Assert.Equal(BarGroups.All, StatusBarOverflow.Decide(Natural, Natural, ViewModes, NavHistory));
    }

    [Fact]
    public void the_view_toggles_go_first()
    {
        // Because the "..." button beside them carries every one as a named
        // menu item, so dropping them costs a click and nothing else.
        var fit = StatusBarOverflow.Decide(Natural - 1, Natural, ViewModes, NavHistory);

        Assert.False(fit.ViewModes);
        Assert.True(fit.NavHistory);
    }

    [Fact]
    public void back_and_forward_go_only_when_dropping_the_toggles_was_not_enough()
    {
        // They were given permanent width because a chord cannot be
        // discovered, so they are the last thing the bar lets go of.
        var stillTight = StatusBarOverflow.Decide(
            Natural - ViewModes - 1, Natural, ViewModes, NavHistory);

        Assert.False(stillTight.ViewModes);
        Assert.False(stillTight.NavHistory);
    }

    [Fact]
    public void an_unmeasured_bar_shows_everything()
    {
        // Before the first layout pass there are no widths to reason from.
        // Showing the whole bar is what the XAML already does, so this is the
        // answer that changes nothing.
        Assert.Equal(BarGroups.All, StatusBarOverflow.Decide(1200, 0, 0, 0));
        Assert.Equal(BarGroups.All, StatusBarOverflow.Decide(0, Natural, ViewModes, NavHistory));
    }

    [Fact]
    public void widening_never_takes_a_group_away()
    {
        // The property that makes the bar predictable. Without it a control
        // could vanish as the window GREW, which reads as a fault rather than
        // as a layout.
        bool sawView = false;
        bool sawNav = false;

        for (double available = 1200; available >= 100; available -= 5)
        {
            var fit = StatusBarOverflow.Decide(available, Natural, ViewModes, NavHistory);

            // Narrowing: once a group has gone it must not come back, which is
            // the same statement read the other way round.
            if (sawView) { Assert.False(fit.ViewModes); }
            if (sawNav) { Assert.False(fit.NavHistory); }

            sawView |= !fit.ViewModes;
            sawNav |= !fit.NavHistory;
        }

        Assert.True(sawView && sawNav, "the sweep never got narrow enough to drop anything");
    }

    [Fact]
    public void a_group_is_never_dropped_for_less_than_it_is_worth()
    {
        // A zero-width group would be dropped without freeing anything, which
        // is how a measurement taken while the group was HIDDEN would behave.
        var fit = StatusBarOverflow.Decide(Natural - 1, Natural, viewModes: 0, navHistory: 0);

        Assert.False(fit.NavHistory);
        Assert.False(fit.ViewModes);

        // And with real widths at the same available space, the history stays.
        Assert.True(StatusBarOverflow.Decide(Natural - 1, Natural, ViewModes, NavHistory).NavHistory);
    }
}
