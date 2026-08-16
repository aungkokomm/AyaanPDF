using System;

namespace PdfEditorApp.Viewport;

/// <summary>What a wheel notch should do in single-page view.</summary>
public enum PageStep
{
    /// <summary>Let the scroller scroll. There is more of this page to see.</summary>
    Scroll,

    /// <summary>Already at the bottom: show the next page, from its top.</summary>
    Next,

    /// <summary>Already at the top: show the previous page, from its bottom.</summary>
    Previous,
}

/// <summary>
/// Turning the wheel at the edge of a page into a page turn.
///
/// Single-page view without this is a trap, and it shipped as one. The app has
/// no next-page or previous-page button anywhere: in continuous view you move
/// between pages BY scrolling, so the wheel was the whole navigation model.
/// Laying out one page left the wheel with nowhere to go and the reader stuck
/// on whatever page they happened to be on, with only the page box, the
/// thumbnails and PageUp/PageDown still working, none of which is the thing a
/// hand reaches for.
/// </summary>
public static class SinglePageScroll
{
    /// <summary>
    /// How close to an edge still counts as being at it.
    ///
    /// Not zero: the offset is a float that has been through a zoom factor, so
    /// the bottom of a page is rarely exactly the scrollable height, and a
    /// reader who has plainly scrolled to the end should not have to find the
    /// last half pixel before the wheel does anything.
    /// </summary>
    public const double EdgeTolerance = 1.0;

    /// <summary>
    /// Decides what a wheel notch means.
    /// </summary>
    /// <param name="wheelDelta">Positive scrolls up, negative down, as Windows reports it.</param>
    /// <param name="verticalOffset">Where the view is now.</param>
    /// <param name="scrollableHeight">
    /// How far it can go. Zero when the whole page fits, which is the fit-page
    /// case: every notch is then a page turn, because there is nothing to
    /// scroll.
    /// </param>
    public static PageStep Resolve(double wheelDelta, double verticalOffset, double scrollableHeight)
    {
        if (wheelDelta == 0)
        {
            return PageStep.Scroll;
        }

        double limit = Math.Max(0, scrollableHeight);

        if (wheelDelta < 0)
        {
            return verticalOffset >= limit - EdgeTolerance ? PageStep.Next : PageStep.Scroll;
        }

        return verticalOffset <= EdgeTolerance ? PageStep.Previous : PageStep.Scroll;
    }

    /// <summary>
    /// Where to land on the page being turned to.
    ///
    /// Forwards lands at the top, backwards at the bottom, so the text carries
    /// on from where the eye left off in both directions. Landing at the top
    /// when going backwards would skip the part of the previous page the reader
    /// was about to re-read.
    /// </summary>
    public static double LandingOffset(PageStep step, double scrollableHeightOfTarget) =>
        step == PageStep.Previous ? Math.Max(0, scrollableHeightOfTarget) : 0;

    /// <summary>
    /// How long after a page turn further notches are ignored.
    ///
    /// One flick of a wheel is several notches, and without this a flick at the
    /// bottom of a page would fly through four or five of them.
    /// </summary>
    public static readonly TimeSpan TurnCooldown = TimeSpan.FromMilliseconds(350);

    /// <summary>Whether enough time has passed since the last turn.</summary>
    public static bool MayTurn(TimeSpan sinceLastTurn) => sinceLastTurn >= TurnCooldown;
}
