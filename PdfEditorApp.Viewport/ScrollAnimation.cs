using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Whether a scroll should glide or jump.
///
/// A smooth scroll is worth it over a short distance: it shows which way the
/// view moved, so you keep your place. Over a long one it is the opposite of
/// helpful. The animation's duration grows with the distance, and every frame
/// of the ride is rendered, so jumping to a bookmark 1500 pages away took
/// nearly half a minute of scenery nobody asked to see. Measured from a real
/// session on a 3352-page book:
///
///   scrollToPage 1532: from 0 to 794500      -> 26.7 s
///   scrollToPage 3014: from 794564 to 1563133 -> 30.8 s
///
/// So distance decides. Nothing else about the call changes.
/// </summary>
public static class ScrollAnimation
{
    /// <summary>
    /// How far a scroll may travel and still be animated, in viewport heights.
    ///
    /// Three is about the distance over which the eye can still follow the
    /// movement and learn something from it. Beyond that the intermediate
    /// pages go by too fast to read, so the animation conveys nothing and only
    /// costs time.
    /// </summary>
    public const double MaxAnimatedViewports = 3.0;

    /// <summary>
    /// Whether to animate a scroll from <paramref name="from"/> to
    /// <paramref name="to"/>.
    /// </summary>
    /// <param name="requested">
    /// What the caller wanted. A caller that asked for no animation never gets
    /// one; this only ever downgrades.
    /// </param>
    public static bool ShouldAnimate(double from, double to, double viewportHeight, bool requested)
    {
        if (!requested)
        {
            return false;
        }

        // No viewport to measure against yet (first layout). Jumping is the
        // safe answer: an un-judgeable distance could be the whole document.
        if (viewportHeight <= 0 || double.IsNaN(viewportHeight))
        {
            return false;
        }

        double distance = Math.Abs(to - from);
        return !double.IsNaN(distance) && distance <= viewportHeight * MaxAnimatedViewports;
    }
}
