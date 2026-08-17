using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which pixels a frame of preview marks can have touched.
///
/// A viewport-sized surface costs its AREA to clear, whatever is on it, and a
/// preview is one mark being dragged. Clearing and repainting only the
/// rectangle the mark can reach is what makes the cost follow the mark instead
/// of the window.
///
/// Pure, and separate from the layer that paints, for the same reason
/// <see cref="ShapeCulling"/> is: the decision about which pixels are at stake
/// is arithmetic, and arithmetic can be tested without a display. The layer
/// keeps the state; this works out the rectangles.
///
/// THE OLD FRAME IS STILL THERE. SKXamlCanvas reuses its bitmap between paints
/// and never clears it, so last frame's pixels survive until something writes
/// over them. That is what makes partial painting possible at all, and it is
/// also the trap: a frame that clears only where the mark is GOING leaves the
/// mark where it WAS. Every rectangle here is a union of the two.
/// </summary>
public static class DirtyRegion
{
    /// <summary>
    /// The antialiasing fringe, in DEVICE pixels: how far a lit pixel sits past
    /// the geometric edge that produced it.
    ///
    /// DERIVED, not chosen. DirtyRegionBleedTests paints every subject the
    /// preview layer draws at every rotation, zoom and display scale in the
    /// Stage 4 matrix and finds the outermost pixel that is not pure white. The
    /// worst any of them managed, once the joins below are accounted for
    /// separately, is one pixel. Three is that with room, because the cost of
    /// padding is a few percent of a small rectangle and the cost of
    /// under-padding is a visible rind of the last frame on every drag.
    ///
    /// A number of PIXELS, so it does not scale with anything: antialiasing
    /// spreads an edge across roughly one pixel whatever the zoom.
    /// </summary>
    public const double AntialiasPadDevicePx = 3.0;

    /// <summary>
    /// The other way ink escapes its box, and the one a measured constant
    /// cannot cover: a mitred join overshoots its own vertex.
    ///
    /// Skia mitres to a limit of 4, so the tip of a join reaches at most
    /// <c>(w / 2) * 4 = 2w</c> from the vertex before falling back to a bevel.
    /// <see cref="OverlayProjection.SlotBoundsOf"/> already allows <c>w / 2</c>,
    /// so at most <c>1.5w</c> escapes. That is an upper bound from the stroker's
    /// documented limit rather than from whatever the sample geometry did, which
    /// matters because FREEHAND angles are chosen by the user: the gentle
    /// sample stroke bleeds 0.6px and a deliberate hairpin bleeds 3.4px, and
    /// neither is the worst a hand can draw.
    ///
    /// Proportional to the stroke, therefore, and not a constant. On a rectangle
    /// this pads more than the one pixel it needs; the rectangle is a few
    /// hundred pixels across and the surface is millions, so the trade is not
    /// close.
    /// </summary>
    public const double MiterOvershootFactor = 1.5;

    /// <summary>
    /// How far outside its own box one item can put ink, in device pixels.
    /// </summary>
    public static double PadFor(
        ShapeRenderItem item, double scale, PageTransform view, ViewportProjection projection)
    {
        double deviceWidth =
            projection.SlotToDeviceLength(OverlayProjection.WidthOf(item, scale, view));

        return AntialiasPadDevicePx + (MiterOvershootFactor * deviceWidth);
    }

    /// <summary>
    /// The device-pixel rectangle a frame of items can touch, or null when the
    /// frame is empty and there is nothing to paint.
    ///
    /// Device rather than slot space on purpose. Zoom, the scroll origin and
    /// the display scale all sit between a slot coordinate and a pixel, and the
    /// pad is a number of PIXELS: padding in slot space would grow and shrink
    /// with the zoom and stop covering the fringe at exactly the moment the
    /// user zoomed out.
    /// </summary>
    public static (double L, double T, double R, double B)? DeviceBoundsOf(
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView,
        ViewportProjection projection)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;
        bool any = false;

        foreach (var item in items)
        {
            if (item.Points.Count == 0)
            {
                continue;
            }

            var view = pageView(item.PageIndex);
            var (sl, st, sr, sb) = OverlayProjection.SlotBoundsOf(
                item, scale, pageTop(item.PageIndex), view);

            var (dl, dt) = projection.SlotToDevice(sl, st);
            var (dr, db) = projection.SlotToDevice(sr, sb);

            // Padded per ITEM, before the union. A frame can hold a hairline
            // shaft and a heavy freehand guide at once, and padding the union
            // by the larger would be wrong for one of them and padding it by
            // the smaller would ghost the other.
            double pad = PadFor(item, scale, view, projection);

            l = Math.Min(l, dl - pad); t = Math.Min(t, dt - pad);
            r = Math.Max(r, dr + pad); b = Math.Max(b, db + pad);
            any = true;
        }

        return any ? (l, t, r, b) : null;
    }

    /// <summary>
    /// Both rectangles at once: where the last frame put ink, and where this
    /// one will. Either may be absent, because a frame with nothing in it is
    /// ordinary at both ends of a drag.
    /// </summary>
    public static (double L, double T, double R, double B)? Union(
        (double L, double T, double R, double B)? a,
        (double L, double T, double R, double B)? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return (Math.Min(a.Value.L, b.Value.L), Math.Min(a.Value.T, b.Value.T),
                Math.Max(a.Value.R, b.Value.R), Math.Max(a.Value.B, b.Value.B));
    }

    /// <summary>
    /// The rectangle cut down to the surface, or null when none of it is on
    /// the surface at all.
    ///
    /// Clipping to the surface is what stops a mark dragged far off-screen from
    /// asking for an enormous clear that would mostly miss the bitmap.
    /// </summary>
    public static (int L, int T, int R, int B)? ClampToSurface(
        (double L, double T, double R, double B)? rect, int width, int height)
    {
        if (rect is null)
        {
            return null;
        }

        var (l, t, r, b) = rect.Value;

        // Outward, so a partly covered pixel is included rather than shaved.
        int il = (int)Math.Max(0, Math.Floor(l));
        int it = (int)Math.Max(0, Math.Floor(t));
        int ir = (int)Math.Min(width, Math.Ceiling(r));
        int ib = (int)Math.Min(height, Math.Ceiling(b));

        if (ir <= il || ib <= it)
        {
            return null;
        }

        return (il, it, ir, ib);
    }

    /// <summary>
    /// Whether everything on the surface has to be redrawn regardless of where
    /// the marks are.
    ///
    /// A union of old and new already covers anything that MOVES the marks,
    /// including scroll, zoom and a page being turned, because the old
    /// rectangle is remembered as pixels that were actually painted rather than
    /// recomputed under the new numbers. This is the belt to that pair of
    /// braces: when the surface itself is new, or the projection changed at
    /// all, the cheap and certain answer is to repaint the lot.
    ///
    /// A changed projection also tends to make the union nearly surface-sized
    /// anyway, so forcing a full repaint there costs almost nothing and removes
    /// a whole class of reasoning error about what a transform did to old ink.
    /// </summary>
    public static bool NeedsFullRepaint(
        ViewportProjection? lastProjection,
        double lastScale,
        int lastWidth,
        int lastHeight,
        ViewportProjection projection,
        double scale,
        int width,
        int height) =>
        lastProjection is null
        || !lastProjection.Value.Equals(projection)
        || lastScale != scale
        || lastWidth != width
        || lastHeight != height;
}
