using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The bounding box of an annotation that was already in the opened file.
///
/// Normalized with a top-left origin and BOTH axes divided by the page width,
/// which is the convention render_core reports and the overlay draws in.
/// </summary>
public readonly record struct AnnotationBox(
    int Index, double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public bool Contains(double x, double y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>The same box shifted, keeping its size.</summary>
    public AnnotationBox MovedBy(double dx, double dy) =>
        this with { Left = Left + dx, Top = Top + dy, Right = Right + dx, Bottom = Bottom + dy };
}

/// <summary>
/// Decides which annotation a click lands on, and where a drag puts it.
///
/// Pulled out of the view model so it can actually be tested. The view model
/// lives in the WinUI project, which a plain test assembly cannot load, so
/// anything left in there is only ever exercised by running the app and
/// clicking, which is how untested picking logic ships.
/// </summary>
public static class LoadedAnnotationPicker
{
    /// <summary>
    /// The topmost annotation containing a point, or null.
    ///
    /// Searched BACKWARDS, because a page's annotations are drawn in list
    /// order, so the last one is on top. Picking the first match instead would
    /// hand back whatever is underneath whenever two marks overlap, which is
    /// exactly the case where the user is being precise about which one they
    /// want.
    /// </summary>
    public static AnnotationBox? PickTopmost(IReadOnlyList<AnnotationBox> boxes, double x, double y)
    {
        if (boxes is null)
        {
            return null;
        }

        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            if (boxes[i].Contains(x, y))
            {
                return boxes[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Where a box ends up when a drag that began at <paramref name="fromX"/>,
    /// <paramref name="fromY"/> reaches <paramref name="toX"/>,
    /// <paramref name="toY"/>.
    ///
    /// Measured from the box the drag STARTED on, not from wherever it is now,
    /// so the mark cannot creep: accumulating each move onto the previous
    /// result compounds any rounding, and a mark dragged in a circle would not
    /// come back to where it began.
    /// </summary>
    public static AnnotationBox Dragged(
        AnnotationBox start, double fromX, double fromY, double toX, double toY) =>
        start.MovedBy(toX - fromX, toY - fromY);

    /// <summary>
    /// Whether a drag actually moved the mark far enough to be worth writing
    /// to the document. A click that wobbles by a pixel should select, not
    /// edit, and certainly should not mark the file dirty.
    /// </summary>
    public static bool IsRealMove(AnnotationBox start, AnnotationBox now, double epsilon = 1e-6) =>
        Math.Abs(now.Left - start.Left) > epsilon || Math.Abs(now.Top - start.Top) > epsilon;
}
