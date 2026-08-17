using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>How much of the surface a frame has to touch.</summary>
public enum PaintScope
{
    /// <summary>Nothing reaches the surface. Leave the bitmap alone.</summary>
    Nothing,

    /// <summary>One rectangle: clear it, draw into it, leave the rest.</summary>
    Partial,

    /// <summary>Everything, because what is already there cannot be trusted.</summary>
    Full,
}

/// <summary>
/// What one frame decided, and what the frame after it needs to know.
///
/// Handed back rather than kept, so deciding and committing are separate steps
/// and a frame that never got painted cannot leave the tracker believing it
/// did. That is the ordering bug this shape exists to make impossible: a
/// tracker that recorded the new position at DECISION time would think the mark
/// had moved when the paint was skipped, and the old one would sit there
/// forever.
/// </summary>
public readonly record struct DirtyPlan(
    PaintScope Scope,
    (int L, int T, int R, int B) Rect,
    (double L, double T, double R, double B)? Bounds,
    ViewportProjection Projection,
    double Scale,
    int Width,
    int Height);

/// <summary>
/// Which pixels each frame has to clear, given what the frame before it drew.
///
/// The whole of the frame-to-frame reasoning behind partial painting, kept away
/// from the layer that owns the Skia surface so it can be tested without one.
/// The layer asks, paints what it is told, and says so.
///
/// WHAT MAKES THIS NECESSARY. The surface is not cleared between frames, so a
/// mark stays where it was drawn until something writes over it. Clearing only
/// where the mark is GOING therefore leaves it where it WAS, twice over, on
/// every pointer move. Every rectangle here is a union of the two.
///
/// THE ACCUMULATOR IS WHAT WAS PAINTED, not what was asked for. Show can be
/// called several times before a paint happens, because SKXamlCanvas queues an
/// invalidation per call and does not coalesce them, and only the last frame's
/// items are ever drawn. So the thing that is actually on the bitmap is the
/// bounds of the last PAINT, and that is what the next frame has to clear.
/// Accumulating requests instead would grow a rectangle that no ink was ever
/// put in.
/// </summary>
public sealed class DirtyRegionTracker
{
    private (double L, double T, double R, double B)? _lastPainted;
    private ViewportProjection? _lastProjection;
    private double _lastScale;
    private int _lastWidth;
    private int _lastHeight;

    /// <summary>
    /// Works out this frame's scope without changing anything. Call
    /// <see cref="Painted"/> afterwards, and only if the paint really happened.
    /// </summary>
    public DirtyPlan Plan(
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView,
        ViewportProjection projection,
        int width,
        int height)
    {
        var bounds = DirtyRegion.DeviceBoundsOf(items, scale, pageTop, pageView, projection);

        if (DirtyRegion.NeedsFullRepaint(
                _lastProjection, _lastScale, _lastWidth, _lastHeight,
                projection, scale, width, height))
        {
            return new DirtyPlan(
                PaintScope.Full, (0, 0, width, height),
                bounds, projection, scale, width, height);
        }

        // Where it was, and where it is going. Dropping either half is the
        // ghost: the first leaves the old mark, the second fails to draw the
        // new one.
        var union = DirtyRegion.Union(_lastPainted, bounds);
        var rect = DirtyRegion.ClampToSurface(union, width, height);

        return rect is null
            ? new DirtyPlan(
                PaintScope.Nothing, default, bounds, projection, scale, width, height)
            : new DirtyPlan(
                PaintScope.Partial, rect.Value, bounds, projection, scale, width, height);
    }

    /// <summary>
    /// Records that the plan was carried out. Until this is called the tracker
    /// still believes the previous frame is what is on the bitmap, which is the
    /// safe thing for it to believe.
    /// </summary>
    public void Painted(in DirtyPlan plan)
    {
        _lastPainted = plan.Bounds;
        _lastProjection = plan.Projection;
        _lastScale = plan.Scale;
        _lastWidth = plan.Width;
        _lastHeight = plan.Height;
    }

    /// <summary>
    /// Forgets everything, so the next frame repaints the lot.
    ///
    /// For the cases where the bitmap goes away or stops being ours: the
    /// control being unloaded, or the flag being turned off and on again. The
    /// contents of a surface nobody has drawn into are not knowable, and
    /// guessing is a ghost that survives until the next full repaint.
    /// </summary>
    public void Reset()
    {
        _lastPainted = null;
        _lastProjection = null;
        _lastScale = 0;
        _lastWidth = 0;
        _lastHeight = 0;
    }
}
