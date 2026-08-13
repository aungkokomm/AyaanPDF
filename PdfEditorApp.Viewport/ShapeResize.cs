using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What rectangle to send when a TURNED shape is resized.
///
/// A shape's /Rect is the axis-aligned box of its rotated, padded content. That
/// is not the shape, and at 45 degrees it cannot be turned back into one:
/// infinitely many upright boxes share a single AABB. So the writer records the
/// shape's own upright size on its tag, and every rebuild reads it back rather
/// than trying to invert the rectangle.
///
/// Reading it back is right for a MOVE and wrong for a RESIZE, which is the one
/// operation that means to change that size. Resizing a rotated shape therefore
/// re-drew it at its original size and only re-centred it: the corner handles
/// appeared to do nothing at all. Unrotated shapes were unaffected, because
/// their /Rect de-pads exactly and never needed the tag.
///
/// The way out is neither to invert the AABB nor to trust the tag: take the
/// RATIO the frame changed by and apply it to the size the tag recorded. A move
/// has a ratio of one on both axes and so is left alone by construction, which
/// is what keeps this from disturbing the move path.
/// </summary>
public static class ShapeResize
{
    /// <summary>
    /// Smallest upright extent to ask for, normalized. The FFI rejects a
    /// rectangle that is empty or inside out, so a drag that scales a shape to
    /// nothing must still name a real box rather than fail the write and leave
    /// the user's shape where it was with no explanation.
    /// </summary>
    public const double MinExtent = 1e-4;

    /// <summary>
    /// The UNPADDED upright rectangle to redraw a turned shape into, or null
    /// when there is nothing reliable to compute from.
    ///
    /// Null means "keep doing what the app does today". A shape whose tag never
    /// recorded an upright size, or a start rectangle with no extent to take a
    /// ratio against, is left on the existing move path rather than being given
    /// an invented geometry. That is the same rule the model follows when a page
    /// width is unknown.
    /// </summary>
    /// <param name="start">The annotation's /Rect when the drag began.</param>
    /// <param name="dragged">The /Rect the drag produced, same space as
    /// <paramref name="start"/>.</param>
    /// <param name="uprightWidth">The shape's own upright width, normalized,
    /// as recorded on its tag.</param>
    /// <param name="uprightHeight">The same for its height.</param>
    public static TextRect? UprightTargetFor(
        TextRect start, TextRect dragged, double uprightWidth, double uprightHeight)
    {
        double startW = start.Right - start.Left;
        double startH = start.Bottom - start.Top;

        if (!IsUsable(startW) || !IsUsable(startH)
            || !IsUsable(uprightWidth) || !IsUsable(uprightHeight))
        {
            return null;
        }

        double draggedW = dragged.Right - dragged.Left;
        double draggedH = dragged.Bottom - dragged.Top;

        if (!double.IsFinite(draggedW) || !double.IsFinite(draggedH))
        {
            return null;
        }

        // The frame and the shape are both axis-aligned in the box's own upright
        // frame, so the ratio the frame grew by is the ratio the shape grew by,
        // whatever the angle. No trigonometry, and nothing that degenerates at
        // 45 degrees.
        double width = Math.Max(MinExtent, uprightWidth * (draggedW / startW));
        double height = Math.Max(MinExtent, uprightHeight * (draggedH / startH));

        // Centred on where the drag put the rectangle. The CENTRE of /Rect is
        // exact at every angle, which is the one thing that survives the
        // rotation, so it is what the rebuild is anchored to.
        double cx = (dragged.Left + dragged.Right) / 2;
        double cy = (dragged.Top + dragged.Bottom) / 2;

        return new TextRect(cx - (width / 2), cy - (height / 2), cx + (width / 2), cy + (height / 2));
    }

    private static bool IsUsable(double v) => double.IsFinite(v) && v > 0;
}
