namespace PdfEditorApp.Viewport;

/// <summary>Where the floating object toolbar should sit, in the coordinate
/// space of the viewport it floats over. <see cref="Below"/> reports which side
/// of the selection it ended up on, which the caller uses to point the toolbar's
/// shadow the right way.</summary>
public readonly record struct ToolbarPlacement(double Left, double Top, bool Below);

/// <summary>
/// Positions a floating toolbar against the object(s) it acts on.
///
/// Pure geometry, deliberately separate from the page: getting this wrong is
/// invisible in a unit test of the view model and very visible on screen (a
/// toolbar half off the edge, or parked on top of the thing you are editing),
/// so the rules live somewhere they can be asserted directly.
/// </summary>
public static class ObjectToolbarPlacement
{
    /// <summary>Clear air between the toolbar and the selection frame. Enough
    /// that the toolbar reads as a separate object rather than a handle on the
    /// selection, and that it misses the corner grips.</summary>
    public const double Gap = 10;

    /// <summary>Closest the toolbar may come to a viewport edge.</summary>
    public const double EdgeMargin = 6;

    /// <summary>
    /// Above the selection and horizontally centred on it, which is where every
    /// editor puts this control and where it covers the least of what you are
    /// working on. Falls BELOW when there is no room above, and is clamped so it
    /// can never leave the viewport, however far the page is scrolled.
    /// </summary>
    /// <param name="topInset">
    /// Height of chrome already occupying the top of the viewport, currently the
    /// property bar. The toolbar must not go under it: the property bar draws
    /// ABOVE this toolbar, so a position beneath it is not merely ugly, it is
    /// unreachable. The two collided the moment the floating toolbar was added
    /// and it took three attempts to notice, because the symptom was a control
    /// on the OTHER bar appearing to be missing.
    /// </param>
    public static ToolbarPlacement Place(
        double selLeft, double selTop, double selRight, double selBottom,
        double toolbarWidth, double toolbarHeight,
        double viewportWidth, double viewportHeight,
        double topInset = 0)
    {
        double left = (selLeft + selRight) / 2 - toolbarWidth / 2;
        double ceiling = Math.Max(EdgeMargin, topInset + Gap);

        // Preferred side is above. Flipping below is decided against the
        // viewport, not the page, because a selection near the top of a long
        // page is still mid-screen once scrolled.
        bool below = false;
        double top = selTop - Gap - toolbarHeight;
        if (top < ceiling)
        {
            below = true;
            top = selBottom + Gap;
        }

        return new ToolbarPlacement(
            ClampToViewport(left, toolbarWidth, viewportWidth),
            ClampToViewport(top, toolbarHeight, viewportHeight, ceiling),
            below);
    }

    /// <summary>Keeps a span inside the viewport. When the viewport is smaller
    /// than the toolbar there is no correct answer, so pin to the near edge and
    /// let it overflow the far one rather than return a negative position.</summary>
    private static double ClampToViewport(double position, double size, double extent, double near = EdgeMargin)
    {
        double max = extent - EdgeMargin - size;
        if (max <= near) { return near; }
        return Math.Clamp(position, near, max);
    }
}
