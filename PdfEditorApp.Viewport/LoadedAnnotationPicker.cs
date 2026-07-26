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
        Math.Abs(now.Left - start.Left) > epsilon || Math.Abs(now.Top - start.Top) > epsilon
        || Math.Abs(now.Width - start.Width) > epsilon
        || Math.Abs(now.Height - start.Height) > epsilon;

    /// <summary>Which corner of a selected annotation a drag has hold of.</summary>
    public enum Grip
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>
    /// Half the width of a corner grip, in normalized units. Generous on
    /// purpose: a grip drawn 8 DIP across is a small target, and missing it
    /// starts a MOVE instead of a resize, which is a surprising thing to have
    /// happen to a carefully placed signature.
    /// </summary>
    public const double GripReach = 0.012;

    /// <summary>
    /// The corner under a point, or None.
    ///
    /// Corners are tested BEFORE the body, because they overlap it: a click in
    /// the corner region is a resize, and only a click elsewhere inside the
    /// box is a move.
    /// </summary>
    public static Grip GripAt(AnnotationBox box, double x, double y, double reach = GripReach)
    {
        // A tiny annotation would have grips covering the whole of it, leaving
        // no way to move the thing. Shrink the reach so the middle stays
        // grabbable.
        double limit = Math.Min(reach, Math.Min(box.Width, box.Height) / 3);

        bool left = Math.Abs(x - box.Left) <= limit;
        bool right = Math.Abs(x - box.Right) <= limit;
        bool top = Math.Abs(y - box.Top) <= limit;
        bool bottom = Math.Abs(y - box.Bottom) <= limit;

        if (top && left) return Grip.TopLeft;
        if (top && right) return Grip.TopRight;
        if (bottom && left) return Grip.BottomLeft;
        if (bottom && right) return Grip.BottomRight;
        return Grip.None;
    }

    /// <summary>Smallest an annotation may be dragged to, normalized.</summary>
    public const double MinSize = 0.01;

    /// <summary>
    /// The box that results from dragging a corner to a point.
    ///
    /// The OPPOSITE corner stays put, which is what makes a resize feel like
    /// one: dragging the bottom-right moves only the bottom-right edge. The
    /// dragged corner is then stopped from crossing over, since a box turned
    /// inside out is rejected by both native calls and would look like the
    /// annotation vanished.
    /// </summary>
    /// <param name="aspect">
    /// Height divided by width to hold constant, or 0 for a free resize. A
    /// picture keeps its aspect: dragging a corner freely turns a signature
    /// into a stretched version of someone's handwriting, which is not a thing
    /// they can put their name to.
    /// </param>
    public static AnnotationBox Resized(
        AnnotationBox start, Grip grip, double x, double y, double aspect = 0)
    {
        if (grip == Grip.None)
        {
            return start;
        }

        double left = start.Left;
        double top = start.Top;
        double right = start.Right;
        double bottom = start.Bottom;

        switch (grip)
        {
            case Grip.TopLeft:
                left = Math.Min(x, right - MinSize);
                top = Math.Min(y, bottom - MinSize);
                break;
            case Grip.TopRight:
                right = Math.Max(x, left + MinSize);
                top = Math.Min(y, bottom - MinSize);
                break;
            case Grip.BottomLeft:
                left = Math.Min(x, right - MinSize);
                bottom = Math.Max(y, top + MinSize);
                break;
            case Grip.BottomRight:
                right = Math.Max(x, left + MinSize);
                bottom = Math.Max(y, top + MinSize);
                break;
        }

        // Hold the aspect by deriving the height from the width, anchored to
        // whichever corner is NOT being dragged, so the fixed corner stays put.
        if (aspect > 0)
        {
            // The minimum has to be applied to the WIDTH alone, chosen so the
            // resulting height also clears it. Flooring each dimension
            // separately quietly breaks the aspect at small sizes: a 2:1 stamp
            // squeezed to the minimum came out 1:1, because the height was
            // raised to the floor after the width had already set it.
            double minWidth = Math.Max(MinSize, MinSize / aspect);
            double width = Math.Max(minWidth, right - left);
            double height = width * aspect;

            if (grip is Grip.TopLeft or Grip.BottomLeft)
            {
                left = right - width;
            }
            else
            {
                right = left + width;
            }

            if (grip is Grip.TopLeft or Grip.TopRight)
            {
                top = bottom - height;
            }
            else
            {
                bottom = top + height;
            }
        }

        // Never off the top or left, where the grips would be unreachable.
        // Shifted rather than clipped, so the aspect just fixed above is not
        // undone by the clamp.
        if (left < 0)
        {
            right -= left;
            left = 0;
        }
        if (top < 0)
        {
            bottom -= top;
            top = 0;
        }

        return start with { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}
