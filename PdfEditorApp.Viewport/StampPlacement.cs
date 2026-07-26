using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Where a stamp lands when you click, in normalized page coordinates with a
/// top-left origin and both axes over the page WIDTH.
///
/// Separate from the view model so it can be tested. The last two bugs that
/// reached the user were both geometry living somewhere a test could not run:
/// annotation picking, and quad point ordering. Placement is the same shape of
/// problem, so it goes here from the start.
/// </summary>
public static class StampPlacement
{
    /// <summary>Smallest stamp worth placing, as a fraction of the page width.</summary>
    public const double MinWidthFraction = 0.02;

    /// <summary>
    /// The rectangle for a stamp dropped at a point.
    ///
    /// Centred on the click, because that is where the pointer is and anything
    /// else feels like the stamp jumped. Height follows the IMAGE's aspect
    /// rather than the requested width, so a stamp is never squashed: a wide
    /// signature stays wide and a tall seal stays tall.
    ///
    /// Then nudged back inside the top-left edges. A stamp hanging off the top
    /// or left of the page is not merely ugly: its selection handle would sit
    /// off-page where it cannot be clicked, so it could never be moved or
    /// resized again.
    /// </summary>
    /// <param name="clickX">Click position, normalized over page width.</param>
    /// <param name="clickY">Click position, normalized over page width.</param>
    /// <param name="imageWidth">Source image width in pixels.</param>
    /// <param name="imageHeight">Source image height in pixels.</param>
    /// <param name="widthFraction">Desired width as a fraction of the page width.</param>
    public static (double Left, double Top, double Right, double Bottom) Compute(
        double clickX,
        double clickY,
        int imageWidth,
        int imageHeight,
        double widthFraction)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageWidth), "a stamp image must have a positive size");
        }

        double w = Math.Clamp(widthFraction, MinWidthFraction, 1.0);

        // Aspect from the IMAGE, in the same normalized space: both axes are
        // divided by the page width, so a square image really is square here.
        double h = w * imageHeight / imageWidth;

        double left = Math.Max(0, clickX - w / 2);
        double top = Math.Max(0, clickY - h / 2);

        return (left, top, left + w, top + h);
    }
}
