namespace PdfEditorApp.Viewport;

/// <summary>
/// Where a selection's frame, its grips and their hit zones are laid out.
///
/// The overlay draws all three UPRIGHT and then turns the whole assembly about
/// the box centre, which is what lets a rotated mark be framed at its real
/// angle. That only works if what is laid out upright really is the mark's own
/// box.
///
/// For a shape it was not. The frame was laid out in the annotation's /Rect,
/// and for a turned shape the /Rect is the axis-aligned box CONTAINING the
/// rotation, so the frame was effectively turned twice: at 90 degrees a
/// landscape rectangle standing on its side was framed by a landscape box lying
/// flat, the shape's own box with its sides swapped.
///
/// THE CENTRE IS THE PART THAT WAS ALWAYS RIGHT. A rotation about the centre
/// cannot move the centre, so the /Rect's centre is the shape's centre at any
/// angle. Only the extent has to be replaced, which is why this takes a
/// rectangle and a size rather than recomputing anything.
/// </summary>
public static class SelectionFrame
{
    /// <summary>
    /// The upright rectangle to lay a selection out in, in the same units as
    /// the bounds given.
    /// </summary>
    /// <param name="boxWidth">
    /// The mark's own upright width, or zero when it is not known. A mark whose
    /// own size cannot be recovered (anything that is not a shape, or a shape
    /// whose tag predates the recorded size) keeps the bounds it was given,
    /// which is what the overlay has always drawn.
    /// </param>
    public static (double Left, double Top, double Right, double Bottom) Upright(
        double left, double top, double right, double bottom,
        double boxWidth, double boxHeight)
    {
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            return (left, top, right, bottom);
        }

        double centreX = (left + right) / 2;
        double centreY = (top + bottom) / 2;

        return (centreX - boxWidth / 2, centreY - boxHeight / 2,
                centreX + boxWidth / 2, centreY + boxHeight / 2);
    }
}
