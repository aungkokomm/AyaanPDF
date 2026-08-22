using System;

namespace PdfEditorApp.Viewport;

/// <summary>Where a floating panel sits, in the coordinate space of the
/// viewport it floats over.</summary>
public readonly record struct PanelPlacement(double Left, double Top);

/// <summary>
/// Where a movable panel opens, and where a drag is allowed to leave it.
///
/// Pure geometry, beside <see cref="ObjectToolbarPlacement"/> and for the same
/// reason: a panel half off the edge, or parked over the thing being edited, is
/// invisible to a view-model test and very visible on screen.
///
/// THE DIFFERENCE FROM THE OBJECT TOOLBAR is who chooses. The toolbar follows
/// the selection and has one correct position; this follows the POINTER and has
/// as many as the person likes. So there is no "preferred side" here, only an
/// opening position and a rule about where a drag may end.
/// </summary>
public static class FloatingPanelPlacement
{
    /// <summary>Closest the panel may come to a viewport edge.</summary>
    public const double EdgeMargin = 6;

    /// <summary>
    /// How far the panel is lifted out of the page, in composition depth.
    ///
    /// ABOVE <see cref="ObjectToolbarPlacement.Elevation"/> deliberately: the
    /// object toolbar sits on the selection, which is exactly what a person
    /// drags this panel away from, and a panel that slid underneath it would
    /// look like it had gone.
    ///
    /// The same warning applies as there, and it is the one that costs a whole
    /// debugging session: apply this through <c>UIElement.Translation</c> and
    /// position through the SAME property. A RenderTransform drives the very
    /// same composition visual and wins, taking this Z with it, and the panel
    /// is then drawn behind every page card, because each card is raised by its
    /// own ThemeShadow and Canvas.ZIndex does not reach across depth.
    /// </summary>
    public const float Elevation = 40f;

    /// <summary>
    /// How much of the panel must stay on screen once it has been dragged.
    ///
    /// NOT ALL OF IT. Pinning the whole panel inside the viewport means it can
    /// never be pushed mostly out of the way, which is the entire point of a
    /// movable panel on a small window. What must never happen is losing the
    /// TITLE BAR, because that is the only thing that can drag it back, so the
    /// clamp keeps a grabbable strip of it in view on every side.
    /// </summary>
    public const double MinimumVisible = 72;

    /// <summary>
    /// Where the panel appears the first time it is opened.
    ///
    /// TOP RIGHT, under whatever chrome already owns the top. The left of the
    /// viewport is where the tool rail and the page usually are, and the middle
    /// is where the selection usually is; the right edge is the one place that
    /// is reliably not the thing being worked on.
    /// </summary>
    /// <param name="topInset">
    /// Height of chrome already occupying the top of the viewport. The panel
    /// must not open under it: that chrome draws above this panel, so a
    /// position beneath it is not merely ugly, it is unreachable.
    /// </param>
    public static PanelPlacement Opening(
        double panelWidth, double panelHeight,
        double viewportWidth, double viewportHeight,
        double topInset = 0)
    {
        double left = viewportWidth - EdgeMargin - panelWidth;
        double top = Math.Max(EdgeMargin, topInset + EdgeMargin);

        // Through the same clamp a drag takes, so opening can never put the
        // panel anywhere a drag would have been refused.
        return Clamp(left, top, panelWidth, panelHeight, viewportWidth, viewportHeight, topInset);
    }

    /// <summary>
    /// The nearest position to the one asked for that still leaves the panel
    /// grabbable.
    ///
    /// Applied on every drag move AND whenever the window changes size, because
    /// a panel parked against the right edge of a wide window is off the side
    /// of a narrow one, and nothing else would ever bring it back.
    /// </summary>
    public static PanelPlacement Clamp(
        double left, double top,
        double panelWidth, double panelHeight,
        double viewportWidth, double viewportHeight,
        double topInset = 0)
    {
        // Horizontally the panel may hang off either edge, so long as a strip
        // stays reachable. A panel narrower than that strip is kept whole.
        double keepX = Math.Min(MinimumVisible, panelWidth);
        double minLeft = EdgeMargin + keepX - panelWidth;
        double maxLeft = viewportWidth - EdgeMargin - keepX;

        // Vertically the TOP is special: the title bar lives there and is the
        // only handle, so the panel may hang off the bottom but never off the
        // top, and never up under the chrome.
        double ceiling = Math.Max(EdgeMargin, topInset + EdgeMargin);
        double keepY = Math.Min(MinimumVisible, panelHeight);
        double maxTop = viewportHeight - EdgeMargin - keepY;

        return new PanelPlacement(
            Squeeze(left, minLeft, maxLeft),
            Squeeze(top, ceiling, maxTop));
    }

    /// <summary>
    /// Clamps, and survives a range that has collapsed or inverted.
    ///
    /// A viewport can be smaller than the panel, and is momentarily zero-sized
    /// while the window is being restored. Math.Clamp THROWS when its minimum
    /// exceeds its maximum, so an unguarded clamp here is an ArgumentException
    /// during an ordinary window resize.
    /// </summary>
    private static double Squeeze(double value, double min, double max) =>
        max <= min ? min : Math.Clamp(value, min, max);
}
