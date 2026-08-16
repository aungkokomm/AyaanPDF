using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The wheel at the edge of a page.
///
/// This exists because single-page view shipped without it and the reader was
/// stuck: the app has no next-page button anywhere, so in continuous view the
/// wheel IS the navigation, and laying out one page left it with nowhere to go.
/// </summary>
public class SinglePageScrollTests
{
    [Fact]
    public void mid_page_the_wheel_just_scrolls()
    {
        // The common case. Turning the page while there is still page left
        // would make a long page unreadable.
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(-120, verticalOffset: 400, scrollableHeight: 1000));
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(120, verticalOffset: 400, scrollableHeight: 1000));
    }

    [Fact]
    public void at_the_bottom_scrolling_down_turns_the_page()
    {
        Assert.Equal(PageStep.Next, SinglePageScroll.Resolve(-120, verticalOffset: 1000, scrollableHeight: 1000));
    }

    [Fact]
    public void at_the_top_scrolling_up_goes_back()
    {
        Assert.Equal(PageStep.Previous, SinglePageScroll.Resolve(120, verticalOffset: 0, scrollableHeight: 1000));
    }

    [Fact]
    public void at_the_bottom_scrolling_up_is_still_a_scroll()
    {
        // The direction that still has page to cover must not turn anything.
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(120, verticalOffset: 1000, scrollableHeight: 1000));
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(-120, verticalOffset: 0, scrollableHeight: 1000));
    }

    [Fact]
    public void a_page_that_fits_turns_on_any_notch()
    {
        // Fit-page zoom: there is nothing to scroll, so every notch is a page
        // turn. Without this the mode would be completely inert at the zoom
        // most people read single pages at.
        Assert.Equal(PageStep.Next, SinglePageScroll.Resolve(-120, verticalOffset: 0, scrollableHeight: 0));
        Assert.Equal(PageStep.Previous, SinglePageScroll.Resolve(120, verticalOffset: 0, scrollableHeight: 0));
    }

    [Fact]
    public void being_a_hair_off_the_edge_still_counts_as_the_edge()
    {
        // The offset is a float that has been through a zoom factor, so the
        // bottom of a page is rarely exactly the scrollable height. A reader
        // who has plainly scrolled to the end should not have to hunt for the
        // last half pixel.
        Assert.Equal(PageStep.Next, SinglePageScroll.Resolve(-120, verticalOffset: 999.4, scrollableHeight: 1000));
        Assert.Equal(PageStep.Previous, SinglePageScroll.Resolve(120, verticalOffset: 0.6, scrollableHeight: 1000));
    }

    [Fact]
    public void just_short_of_the_edge_still_scrolls()
    {
        // The tolerance must not be so generous that it eats a real scroll.
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(-120, verticalOffset: 990, scrollableHeight: 1000));
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(120, verticalOffset: 10, scrollableHeight: 1000));
    }

    [Fact]
    public void a_notch_of_nothing_does_nothing()
    {
        Assert.Equal(PageStep.Scroll, SinglePageScroll.Resolve(0, verticalOffset: 0, scrollableHeight: 0));
    }

    [Fact]
    public void a_negative_extent_is_treated_as_no_room()
    {
        // ExtentHeight * ZoomFactor - ViewportHeight goes negative whenever the
        // content is smaller than the window, which is most of the time in this
        // mode.
        Assert.Equal(PageStep.Next, SinglePageScroll.Resolve(-120, verticalOffset: 0, scrollableHeight: -250));
    }

    [Fact]
    public void turning_forward_lands_at_the_top_and_back_lands_at_the_bottom()
    {
        // So the text carries on from where the eye left off in both
        // directions. Landing at the top when going backwards would skip the
        // part of the page the reader was about to re-read.
        Assert.Equal(0, SinglePageScroll.LandingOffset(PageStep.Next, 1234), 6);
        Assert.Equal(1234, SinglePageScroll.LandingOffset(PageStep.Previous, 1234), 6);
        Assert.Equal(0, SinglePageScroll.LandingOffset(PageStep.Previous, -5), 6);
    }

    [Fact]
    public void one_flick_of_the_wheel_turns_one_page()
    {
        // A flick is several notches. Without a cooldown it would fly through
        // four or five pages.
        Assert.False(SinglePageScroll.MayTurn(TimeSpan.Zero));
        Assert.False(SinglePageScroll.MayTurn(SinglePageScroll.TurnCooldown - TimeSpan.FromMilliseconds(1)));
        Assert.True(SinglePageScroll.MayTurn(SinglePageScroll.TurnCooldown));
        Assert.True(SinglePageScroll.MayTurn(TimeSpan.FromSeconds(2)));
    }
}
