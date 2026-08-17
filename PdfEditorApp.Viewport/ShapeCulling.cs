using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which of a frame's items can reach the surface being drawn.
///
/// A viewport-sized surface only needs the marks that land on it, and a long
/// document's other few thousand are work for nothing. Pure, and separate from
/// the painter, so the decision can be tested without rendering anything.
///
/// It culls, it does not reorder. The list arrives in paint order and leaves in
/// paint order with gaps, because dropping an item must never restack the ones
/// that remain.
/// </summary>
public static class ShapeCulling
{
    /// <summary>
    /// The items whose slot-space bounds meet the given slot-space rectangle.
    /// </summary>
    /// <param name="pageTop">Where each item's page starts in the stack.</param>
    /// <param name="pageView">
    /// How each item's page is turned. Culling MUST project the same way the
    /// painter does: deciding what is on screen from an untuned position while
    /// painting at a turned one drops marks that are visible and keeps ones that
    /// are not, and only ever while the view is rotated.
    /// </param>
    public static IReadOnlyList<ShapeRenderItem> Visible(
        IReadOnlyList<ShapeRenderItem> items,
        (double Left, double Top, double Right, double Bottom) slotBounds,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        var kept = new List<ShapeRenderItem>(items.Count);

        foreach (var item in items)
        {
            if (Meets(item, slotBounds, scale, pageTop(item.PageIndex), pageView(item.PageIndex)))
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    private static bool Meets(
        ShapeRenderItem item,
        (double Left, double Top, double Right, double Bottom) bounds,
        double scale,
        double pageTop,
        PageTransform view)
    {
        if (item.Points.Count == 0)
        {
            return false;
        }

        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, bottom = double.MinValue;

        foreach (var point in item.Points)
        {
            var (x, y) = OverlayProjection.ToSlot(point, scale, pageTop, view);
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        // Half the stroke reaches outside the path on every side, so a mark
        // tested on its centreline alone vanishes half a stroke early.
        double reach = OverlayProjection.ToSlotThickness(item.StrokeWidth, scale, view) / 2;

        return left - reach <= bounds.Right
            && right + reach >= bounds.Left
            && top - reach <= bounds.Bottom
            && bottom + reach >= bounds.Top;
    }
}
