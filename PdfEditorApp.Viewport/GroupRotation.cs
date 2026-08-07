using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Geometry for turning a multi-selection as one rigid body.
///
/// Rotating a group is two things happening to every member at once: it
/// ORBITS the selection's centre, which changes where it is, and it SPINS by
/// the same angle about its own centre, which changes how it sits. Doing only
/// the spin leaves the marks in place and turns each on the spot; doing only
/// the orbit swings them round but leaves them all upright. Both are needed,
/// and every editor that gets this right does both.
///
/// Pure, so the arithmetic can be tested without a document. The writes that
/// apply it are the view model's problem.
/// </summary>
public static class GroupRotation
{
    /// <summary>
    /// The smallest box containing every rectangle, which is what a
    /// multi-selection turns about. Returns null for an empty set.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom)? BoundingBox(
        IReadOnlyList<(double Left, double Top, double Right, double Bottom)> rects)
    {
        if (rects is null || rects.Count == 0) { return null; }

        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;
        foreach (var rect in rects)
        {
            l = Math.Min(l, rect.Left);
            t = Math.Min(t, rect.Top);
            r = Math.Max(r, rect.Right);
            b = Math.Max(b, rect.Bottom);
        }
        return (l, t, r, b);
    }

    /// <summary>
    /// A point turned about a pivot by <paramref name="degrees"/> CLOCKWISE as
    /// the user sees it.
    ///
    /// The plain rotation matrix gives clockwise here because these are screen
    /// coordinates, where y runs DOWN; the same matrix in a y-up space would
    /// turn the other way. That sign trips everyone once, and it is the same
    /// one that made stamp rotation go backwards in v2.4.0.
    /// </summary>
    public static (double X, double Y) RotatePointAbout(
        double x, double y, double pivotX, double pivotY, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = x - pivotX, dy = y - pivotY;
        return (pivotX + dx * cos - dy * sin, pivotY + dx * sin + dy * cos);
    }

    /// <summary>
    /// Where one member's rectangle lands when the group turns.
    ///
    /// The rectangle keeps its SIZE: only its centre moves. The turn of the
    /// mark itself is carried as the annotation's own angle, not by reshaping
    /// its box, because a rotated mark's box is the enlarged one that contains
    /// it and rebuilding that here would compound with what the writer already
    /// does.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) OrbitRect(
        (double Left, double Top, double Right, double Bottom) rect,
        double pivotX, double pivotY, double degrees)
    {
        double w = rect.Right - rect.Left;
        double h = rect.Bottom - rect.Top;
        double cx = rect.Left + w / 2.0;
        double cy = rect.Top + h / 2.0;

        var (nx, ny) = RotatePointAbout(cx, cy, pivotX, pivotY, degrees);
        return (nx - w / 2.0, ny - h / 2.0, nx + w / 2.0, ny + h / 2.0);
    }
}
