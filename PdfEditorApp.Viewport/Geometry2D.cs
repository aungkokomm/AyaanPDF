using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Plane geometry, in whatever units the caller is working in.
///
/// Nothing here knows about pages, annotations, PDFium or WinUI. It exists as
/// its own layer because the same three questions ("how far is this point from
/// that line", "is it inside this triangle", "is it inside this ellipse") were
/// about to be answered twice, once for the objects the app draws in an overlay
/// and once for the objects it reads back out of the document. Two copies of a
/// distance formula is two chances to get it wrong, and geometry bugs in this
/// app have a history of being invisible to a green build.
/// </summary>
public static class Geometry2D
{
    /// <summary>
    /// Shortest distance from a point to a line SEGMENT, not to the infinite
    /// line through it. The clamp is the whole difference: without it a point
    /// far beyond the end of a short segment measures as close to it.
    /// </summary>
    public static double DistanceToSegment(
        double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double lenSq = (vx * vx) + (vy * vy);

        // Degenerate segment: fall back to point distance.
        if (lenSq <= double.Epsilon)
        {
            return Math.Sqrt(((px - a.X) * (px - a.X)) + ((py - a.Y) * (py - a.Y)));
        }

        double t = Math.Clamp((((px - a.X) * vx) + ((py - a.Y) * vy)) / lenSq, 0, 1);
        double cx = a.X + (t * vx);
        double cy = a.Y + (t * vy);
        return Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
    }

    /// <summary>
    /// Shortest distance from a point to a polyline through the given points,
    /// or to the single point when there is only one. <see cref="double.MaxValue"/>
    /// for an empty list, so an object with no geometry can never be hit by
    /// accident.
    /// </summary>
    public static double DistanceToPolyline(
        double px, double py, IReadOnlyList<(double X, double Y)> points)
    {
        if (points is null || points.Count == 0) { return double.MaxValue; }

        if (points.Count == 1)
        {
            var only = points[0];
            return Math.Sqrt(((px - only.X) * (px - only.X)) + ((py - only.Y) * (py - only.Y)));
        }

        double best = double.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(px, py, points[i - 1], points[i]));
        }

        return best;
    }

    /// <summary>
    /// Whether a point is inside a triangle, or within <paramref name="reach"/>
    /// of one of its edges.
    ///
    /// Both halves are needed. The interior test alone misses a click just
    /// outside a small arrowhead, and an edge-distance test alone misses a click
    /// in the MIDDLE of a large filled one, which is the fattest and most
    /// obvious part of an arrow and the place people aim.
    /// </summary>
    public static bool InTriangle(
        double px, double py,
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c,
        double reach = 0)
    {
        // Same sign on all three cross products means the point is on the same
        // side of every edge, i.e. inside. Winding-agnostic, so it does not
        // matter which way round the caller supplies the corners.
        double d1 = Cross(px, py, a, b);
        double d2 = Cross(px, py, b, c);
        double d3 = Cross(px, py, c, a);

        // A COLLAPSED triangle has no inside. Its three cross products are all
        // zero for every point on its line and the sign test would then report
        // the whole plane as inside it. An arrowhead degenerates exactly this
        // way on a zero-length drag, so this is a real case, not a theoretical
        // one, and the honest answer is the edge test below.
        bool degenerate = Cross(a.X, a.Y, b, c) == 0;

        if (!degenerate)
        {
            bool anyNegative = d1 < 0 || d2 < 0 || d3 < 0;
            bool anyPositive = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(anyNegative && anyPositive)) { return true; }
        }

        if (reach <= 0) { return false; }

        return DistanceToSegment(px, py, a, b) <= reach
            || DistanceToSegment(px, py, b, c) <= reach
            || DistanceToSegment(px, py, c, a) <= reach;
    }

    private static double Cross(double px, double py, (double X, double Y) a, (double X, double Y) b) =>
        ((px - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (py - b.Y));

    /// <summary>
    /// Whether a point is inside an axis-aligned rectangle grown by
    /// <paramref name="reach"/> on every side.
    /// </summary>
    public static bool InRect(double px, double py, TextRect r, double reach = 0) =>
        px >= r.Left - reach && px <= r.Right + reach
        && py >= r.Top - reach && py <= r.Bottom + reach;

    /// <summary>
    /// Whether a point is inside the ellipse inscribed in a rectangle, grown by
    /// <paramref name="reach"/> on each axis.
    ///
    /// The point of having this at all is the four CORNERS: an ellipse fills
    /// about 79% of its bounding box, and the missing fifth is entirely at the
    /// corners, where a box test would claim a hit on empty page. A degenerate
    /// box has no ellipse to speak of and falls back to the rectangle, so a
    /// shape squashed to a line is still selectable.
    /// </summary>
    public static bool InEllipse(double px, double py, TextRect r, double reach = 0)
    {
        double rx = ((r.Right - r.Left) / 2) + reach;
        double ry = ((r.Bottom - r.Top) / 2) + reach;
        if (rx <= 0 || ry <= 0) { return InRect(px, py, r, reach); }

        double dx = (px - ((r.Left + r.Right) / 2)) / rx;
        double dy = (py - ((r.Top + r.Bottom) / 2)) / ry;
        return (dx * dx) + (dy * dy) <= 1;
    }

    /// <summary>
    /// Whether a point is inside a rounded rectangle, grown by
    /// <paramref name="reach"/>.
    ///
    /// Only the four corner regions differ from a plain rectangle: inside one,
    /// the point must be within the arc's radius of that arc's centre. A radius
    /// of zero degenerates to <see cref="InRect"/>, which is what a rounded
    /// rectangle drawn too thin to round is actually drawn as.
    /// </summary>
    public static bool InRoundedRect(double px, double py, TextRect r, double radius, double reach = 0)
    {
        if (!InRect(px, py, r, reach)) { return false; }

        double limited = ShapeGeometry.ClampCornerRadius(radius, r.Right - r.Left, r.Bottom - r.Top);
        if (limited <= 0) { return true; }

        // Each arc's centre is inset from the corner by the radius. A point
        // beyond BOTH insets is in a corner region and has to clear the arc.
        double cx = px < r.Left + limited ? r.Left + limited
                  : px > r.Right - limited ? r.Right - limited
                  : px;
        double cy = py < r.Top + limited ? r.Top + limited
                  : py > r.Bottom - limited ? r.Bottom - limited
                  : py;

        // Not in a corner region: the straight-sided part of the shape, already
        // covered by the rectangle test above.
        if (cx == px || cy == py) { return true; }

        double dx = px - cx;
        double dy = py - cy;
        double grown = limited + reach;
        return (dx * dx) + (dy * dy) <= grown * grown;
    }

    /// <summary>
    /// Turns a point back into an upright frame that has been rotated
    /// <paramref name="deg"/> CLOCKWISE about (<paramref name="cx"/>,
    /// <paramref name="cy"/>) on screen, where y runs DOWN.
    ///
    /// This is the inverse of the transform the overlay applies when it draws a
    /// turned object's frame, so testing a rotated object is a matter of moving
    /// the pointer into the object's own frame and then measuring as though
    /// nothing were rotated. Getting the sign wrong here is invisible at 0 and
    /// 180 degrees and wrong everywhere else, which is exactly the kind of bug
    /// that reaches a user.
    /// </summary>
    public static (double X, double Y) InverseRotate(
        double x, double y, double cx, double cy, double deg)
    {
        if (deg == 0) { return (x, y); }

        double rad = deg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double dx = x - cx;
        double dy = y - cy;
        return (cx + (dx * cos) + (dy * sin), cy - (dx * sin) + (dy * cos));
    }
}
