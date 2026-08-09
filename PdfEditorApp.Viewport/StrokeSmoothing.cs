using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Turns raw pointer samples into the curve a freehand stroke should be.
///
/// A pointer reports a sample every few milliseconds, so a slow stroke arrives
/// as a dense cloud of nearly identical points and a fast one as a sparse
/// polyline. Drawing those samples directly is what makes strokes look
/// faceted: the corners are the samples.
///
/// Two steps. Thinning drops samples too close to the last one kept, which
/// removes the jitter of a hand resting and the cluster where the pen paused.
/// Fitting then runs a Catmull-Rom spline through what is left, which passes
/// THROUGH its control points rather than near them, so the stroke still goes
/// where it was drawn.
///
/// This lives here, pure and tested, because both the live preview and the
/// write into the PDF must use it. The one thing that must not happen is the
/// two disagreeing: that is the split-path failure where a stroke visibly
/// changes shape the moment the pen lifts.
/// </summary>
public static class StrokeSmoothing
{
    /// <summary>
    /// Spacing below which samples are dropped, in normalized page units where
    /// 1.0 is the page width. Roughly 4px on a 900px-wide render.
    ///
    /// The instinct is to thin as little as possible. That gets it backwards:
    /// leave the samples 1px apart and there is nothing for the fit to do, and
    /// the faceting stays because the facets ARE the samples. Thinning has to
    /// be coarse enough that the curve between kept points is doing the work,
    /// while staying finer than the smallest deliberate feature, which for a
    /// signature is a loop maybe 20px across.
    /// </summary>
    public const double DefaultMinDistance = 0.005;

    /// <summary>
    /// Points generated between each pair of kept samples. Six across a ~4px
    /// gap puts output points under a pixel apart, which is past what the
    /// renderer can show.
    /// </summary>
    public const int DefaultPerSegment = 6;

    /// <summary>
    /// Drops samples closer than <paramref name="minDistance"/> to the last one
    /// kept. The first and last sample are always kept, so the stroke starts
    /// and ends exactly where the pen did.
    /// </summary>
    public static List<(double X, double Y)> Thin(
        IReadOnlyList<(double X, double Y)> raw, double minDistance = DefaultMinDistance)
    {
        var kept = new List<(double X, double Y)>();
        if (raw.Count == 0)
        {
            return kept;
        }

        kept.Add(raw[0]);
        double minSq = minDistance * minDistance;
        for (int i = 1; i < raw.Count; i++)
        {
            var last = kept[^1];
            double dx = raw[i].X - last.X;
            double dy = raw[i].Y - last.Y;
            if (dx * dx + dy * dy >= minSq)
            {
                kept.Add(raw[i]);
            }
        }

        // The final sample matters even when it is a hair from the previous
        // one: it is where the user lifted the pen, and losing it visibly
        // shortens a stroke that ended in a slow curl.
        var end = raw[^1];
        if (kept.Count > 0 && (kept[^1].X != end.X || kept[^1].Y != end.Y))
        {
            kept.Add(end);
        }
        return kept;
    }

    /// <summary>
    /// The smoothed stroke: thinned, then fitted. Strokes of fewer than three
    /// kept points come back unchanged, since two points are already the only
    /// curve through them and one is a dot.
    /// </summary>
    public static List<(double X, double Y)> Smooth(
        IReadOnlyList<(double X, double Y)> raw,
        double minDistance = DefaultMinDistance,
        int perSegment = DefaultPerSegment)
        => Fit(Thin(raw, minDistance), perSegment);

    /// <summary>
    /// The curve through already-thinned control points.
    ///
    /// Split out from <see cref="Smooth"/> because the control points are what
    /// gets stored: a stroke's tag holds the handful of points it was thinned
    /// to, not the hundreds the fit produces, and rebuilding runs this over
    /// them again. Since the fit is a pure function the rebuilt curve is
    /// identical to the one originally drawn, at a fraction of the tag size.
    /// </summary>
    public static List<(double X, double Y)> Fit(
        IReadOnlyList<(double X, double Y)> control, int perSegment = DefaultPerSegment)
    {
        var pts = new List<(double X, double Y)>(control);
        if (pts.Count < 3 || perSegment < 1)
        {
            return pts;
        }

        var outPts = new List<(double X, double Y)> { pts[0] };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            // Reflect a virtual neighbour at each end rather than duplicating
            // the endpoint. A duplicate gives the segment a zero-length knot
            // span, which the centripetal parameterisation divides by, and it
            // also flattens the first and last segment.
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p0 = i == 0 ? Reflect(p1, p2) : pts[i - 1];
            var p3 = i + 2 >= pts.Count ? Reflect(p2, p1) : pts[i + 2];

            for (int s = 1; s <= perSegment; s++)
            {
                outPts.Add(CatmullRom(p0, p1, p2, p3, (double)s / perSegment));
            }
        }
        return outPts;
    }

    private static (double X, double Y) Reflect((double X, double Y) a, (double X, double Y) b)
        => (2 * a.X - b.X, 2 * a.Y - b.Y);

    /// <summary>
    /// The CENTRIPETAL Catmull-Rom point at <paramref name="t"/> along the
    /// p1..p2 segment, with knots spaced by the square root of the distance
    /// between control points.
    ///
    /// Uniform spacing is the textbook form and is wrong here: it overshoots
    /// wherever the stroke changes direction sharply, so the fitted curve
    /// bulges outside the points it was drawn through. On a signature that
    /// shows up as loops that swing wider than the pen did. Centripetal
    /// spacing is the variant that provably produces no cusps and no
    /// self-intersection within a segment.
    /// </summary>
    private static (double X, double Y) CatmullRom(
        (double X, double Y) p0, (double X, double Y) p1,
        (double X, double Y) p2, (double X, double Y) p3, double t)
    {
        // alpha = 0.5 is what makes it centripetal; 0 would be uniform and 1
        // chordal.
        double t0 = 0;
        double t1 = t0 + Knot(p0, p1);
        double t2 = t1 + Knot(p1, p2);
        double t3 = t2 + Knot(p2, p3);

        double tt = t1 + (t2 - t1) * t;

        var a1 = Lerp(p0, p1, t0, t1, tt);
        var a2 = Lerp(p1, p2, t1, t2, tt);
        var a3 = Lerp(p2, p3, t2, t3, tt);
        var b1 = Lerp(a1, a2, t0, t2, tt);
        var b2 = Lerp(a2, a3, t1, t3, tt);
        return Lerp(b1, b2, t1, t2, tt);

        // Coincident control points would give a zero span and divide by zero.
        // Thinning makes that rare, but the forced final sample can land a hair
        // from its predecessor, so the floor is not decorative.
        static double Knot((double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Max(Math.Sqrt(Math.Sqrt(dx * dx + dy * dy)), 1e-6);
        }

        static (double X, double Y) Lerp(
            (double X, double Y) a, (double X, double Y) b, double ta, double tb, double t)
        {
            double span = tb - ta;
            if (span <= 0) { return a; }
            double k = (t - ta) / span;
            return (a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
        }
    }
}
