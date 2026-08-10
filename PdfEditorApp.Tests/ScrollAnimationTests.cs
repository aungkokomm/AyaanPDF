using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The rule that stopped a bookmark jump taking half a minute.
/// </summary>
public class ScrollAnimationTests
{
    private const double Viewport = 900;

    [Fact]
    public void a_short_hop_still_glides()
    {
        // The case animation is FOR: moving to the next page, or to a
        // neighbouring one from the thumbnails. The movement shows which way
        // the view went, so you keep your place.
        Assert.True(ScrollAnimation.ShouldAnimate(0, 800, Viewport, requested: true));
        Assert.True(ScrollAnimation.ShouldAnimate(5000, 4200, Viewport, requested: true));
    }

    [Fact]
    public void the_jumps_from_the_real_session_no_longer_glide()
    {
        // Straight from the user's diag.log on a 3352-page book. These two took
        // 26.7 s and 30.8 s respectively, which is the whole bug.
        Assert.False(ScrollAnimation.ShouldAnimate(0, 794_500, Viewport, requested: true));
        Assert.False(ScrollAnimation.ShouldAnimate(794_564, 1_563_133, Viewport, requested: true));
    }

    [Fact]
    public void the_boundary_is_three_viewports()
    {
        double limit = Viewport * ScrollAnimation.MaxAnimatedViewports;

        Assert.True(ScrollAnimation.ShouldAnimate(0, limit, Viewport, requested: true));
        Assert.False(ScrollAnimation.ShouldAnimate(0, limit + 1, Viewport, requested: true));

        // Direction cannot matter: scrolling back is the same distance.
        Assert.True(ScrollAnimation.ShouldAnimate(limit, 0, Viewport, requested: true));
        Assert.False(ScrollAnimation.ShouldAnimate(limit + 1, 0, Viewport, requested: true));
    }

    [Fact]
    public void a_caller_that_asked_for_no_animation_never_gets_one()
    {
        // This only ever downgrades. Callers that deliberately jump, such as
        // restoring a position on open, must keep jumping however short the
        // distance turns out to be.
        Assert.False(ScrollAnimation.ShouldAnimate(0, 10, Viewport, requested: false));
    }

    [Fact]
    public void an_unmeasurable_viewport_jumps_rather_than_guesses()
    {
        // Before the first layout there is no height to judge against, and an
        // un-judgeable distance could be the whole document. Jumping is the
        // answer that cannot cost 30 seconds.
        Assert.False(ScrollAnimation.ShouldAnimate(0, 100, 0, requested: true));
        Assert.False(ScrollAnimation.ShouldAnimate(0, 100, double.NaN, requested: true));
    }
}
