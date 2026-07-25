using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Identity for an annotation, stable across edits.
///
/// The annotation types are immutable records, so moving one produces a NEW
/// instance rather than mutating the old. Reference equality therefore cannot
/// track "the thing the user has selected" across a drag, and value equality
/// would treat a moved mark as a different object. An explicit id survives
/// both: `with` copies it, so an edited annotation is still recognisably the
/// same one.
/// </summary>
public interface IAnnotation
{
    Guid Id { get; }
    int PageIndex { get; }

    /// <summary>Axis-aligned bounds in normalized page coordinates.</summary>
    TextRect Bounds { get; }

    /// <summary>True if a normalized page-local point lies on this annotation.</summary>
    bool HitTest(double x, double y, double tolerance);

    /// <summary>Returns a copy translated by a normalized delta.</summary>
    IAnnotation Translate(double dx, double dy);
}

/// <summary>
/// One highlight rectangle paired with its colour.
///
/// Exists because the inner item template is per-RECT while the colour lives
/// on the parent highlight, and x:Bind cannot reach up out of a template.
/// Carrying the colour on each rect keeps the binding compiled rather than
/// falling back to a runtime ancestor lookup.
/// </summary>
public readonly record struct ColoredRect(double Left, double Top, double Right, double Bottom, string ColorHex)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>A committed highlight over a run of text, as normalized rects.</summary>
public sealed record HighlightAnnotation(int PageIndex, IReadOnlyList<TextRect> Rects, string ColorHex)
    : IAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The rects with the colour attached, for binding.</summary>
    public IReadOnlyList<ColoredRect> ColoredRects =>
        Rects.Select(r => new ColoredRect(r.Left, r.Top, r.Right, r.Bottom, ColorHex)).ToList();

    public TextRect Bounds => Rects.Count == 0
        ? new TextRect(0, 0, 0, 0)
        : new TextRect(
            Rects.Min(r => r.Left),
            Rects.Min(r => r.Top),
            Rects.Max(r => r.Right),
            Rects.Max(r => r.Bottom));

    /// <summary>
    /// Tests the individual rects, not the bounding box. A highlight spanning
    /// several lines has a bounding box covering the gaps between them, and
    /// clicking a gap should not select it.
    /// </summary>
    public bool HitTest(double x, double y, double tolerance) =>
        Rects.Any(r =>
            x >= r.Left - tolerance && x <= r.Right + tolerance &&
            y >= r.Top - tolerance && y <= r.Bottom + tolerance);

    public IAnnotation Translate(double dx, double dy) => this with
    {
        Rects = Rects
            .Select(r => new TextRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy))
            .ToList(),
    };
}

/// <summary>A single freehand stroke, as normalized points in drawing order.</summary>
public sealed record InkStrokeAnnotation(
    int PageIndex,
    IReadOnlyList<(double X, double Y)> Points,
    string ColorHex,
    double StrokeWidth) : IAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public TextRect Bounds => Points.Count == 0
        ? new TextRect(0, 0, 0, 0)
        : new TextRect(
            Points.Min(p => p.X),
            Points.Min(p => p.Y),
            Points.Max(p => p.X),
            Points.Max(p => p.Y));

    /// <summary>
    /// Tests distance to the stroke's segments rather than to its bounding
    /// box. A diagonal stroke fills very little of its box, so a box test
    /// would grab clicks far from any actual ink.
    /// </summary>
    public bool HitTest(double x, double y, double tolerance)
    {
        double reach = Math.Max(tolerance, StrokeWidth);

        if (Points.Count == 1)
        {
            var only = Points[0];
            return Math.Abs(x - only.X) <= reach && Math.Abs(y - only.Y) <= reach;
        }

        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(x, y, Points[i - 1], Points[i]) <= reach)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double lenSq = vx * vx + vy * vy;

        // Degenerate segment: fall back to point distance.
        if (lenSq <= double.Epsilon)
        {
            return Math.Sqrt((px - a.X) * (px - a.X) + (py - a.Y) * (py - a.Y));
        }

        double t = Math.Clamp(((px - a.X) * vx + (py - a.Y) * vy) / lenSq, 0, 1);
        double cx = a.X + t * vx;
        double cy = a.Y + t * vy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public IAnnotation Translate(double dx, double dy) => this with
    {
        Points = Points.Select(p => (p.X + dx, p.Y + dy)).ToList(),
    };
}
