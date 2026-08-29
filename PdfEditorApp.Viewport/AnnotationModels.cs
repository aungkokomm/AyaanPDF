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
/// A rectangle already multiplied into slot-space DIPs, ready to bind.
///
/// Overlays used to hold NORMALIZED rects inside a Grid carrying a
/// ScaleTransform of the slot width. That fails: RenderTransform runs after
/// layout, so a selection rect was laid out at roughly 0.3 x 0.02 DIPs, a
/// sub-pixel box that never produced any visible geometry to scale up. The
/// data was right and nothing appeared, which is why text selection,
/// highlights and search matches were all invisible while ink — drawn on a
/// separate canvas from pre-multiplied points — worked fine.
///
/// Pre-multiplying means every overlay element is laid out at its real
/// on-screen size and no transform is involved.
/// </summary>
public readonly record struct ScaledRect(double Left, double Top, double Width, double Height, string ColorHex)
{
    public static ScaledRect From(TextRect r, double scale, string colorHex = "") =>
        new(r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale, colorHex);

    /// <summary>
    /// From a rect that already carries its colour, which is what a markup
    /// annotation hands over once it has decided what to DRAW.
    ///
    /// ⚠️ THE OVERLAY MUST COME THROUGH HERE. Taking the marked band instead
    /// draws every kind as a full-width wash: underline and strikeout were
    /// shipped invisible that way, because the geometry that told them apart
    /// was computed by a property nothing on screen called.
    /// </summary>
    public static ScaledRect From(ColoredRect r, double scale) =>
        new(r.Left * scale, r.Top * scale, r.Width * scale, r.Height * scale, r.ColorHex);

    /// <summary>
    /// False for degenerate rects. Line breaks and zero-width joiners produce
    /// empty glyph boxes, and a rect with no area is an invisible element that
    /// still costs a container to lay out.
    /// </summary>
    public bool IsVisible => Width > 0.5 && Height > 0.5;
}

/// <summary>
/// A short sentence pinned beside something on the page. At most one per page.
///
/// ⚠️ IT CARRIES BOTH ANCHORS, and that is the whole design rather than an
/// oversight. The label belongs ABOVE the frame it explains, and how tall it
/// is not known until its text has wrapped, so the margin that puts it there
/// is measured from the BOTTOM of the page and the element is bottom-aligned.
/// A frame near the top of the page has nothing above it, so that one is
/// placed below and top-aligned instead. The alternative was to estimate the
/// height and always anchor at the top, and an estimate that comes out short
/// drops the label onto the very words it is about.
/// </summary>
/// <param name="Left">From the page's left edge, in the overlay's own units.</param>
/// <param name="TopMargin">Used when <paramref name="Above"/> is false.</param>
/// <param name="BottomMargin">Used when <paramref name="Above"/> is true.</param>
/// <param name="MaxWidth">How wide the text may run before it wraps.</param>
public readonly record struct PageNotice(
    double Left,
    double TopMargin,
    double BottomMargin,
    double MaxWidth,
    bool Above,
    string Text,
    string ColorHex);

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

/// <summary>Which mark a text markup annotation is. Mirrors the core's MARKUP_ codes.</summary>
public enum MarkupKind
{
    /// <summary>A wash behind the words, `/Highlight`.</summary>
    Highlight = 0,

    /// <summary>A rule under them, `/Underline`.</summary>
    Underline = 1,

    /// <summary>A rule through them, `/StrikeOut`.</summary>
    Strikeout = 2,

    /// <summary>A wavy rule under them, `/Squiggly`.</summary>
    Squiggly = 3,
}

/// <summary>A committed markup over a run of text, as normalized rects.</summary>
public sealed record HighlightAnnotation(int PageIndex, IReadOnlyList<TextRect> Rects, string ColorHex)
    : IAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Which of the three marks this is.
    /// </summary>
    ///
    /// <remarks>
    /// An init property with a default rather than a fourth positional
    /// parameter: every existing construction site and every existing test
    /// means Highlight, and saying so in one place beats saying it in all of
    /// them.
    /// </remarks>
    public MarkupKind Kind { get; init; } = MarkupKind.Highlight;

    /// <summary>How much of the text band an underline or a strikeout inks, as
    /// a fraction of that band's height.</summary>
    public const double RuleThickness = 0.09;

    /// <summary>
    /// The most pieces a squiggle is built from.
    ///
    /// A cap, not a target: a mark across a whole page would otherwise emit one
    /// element per step and the overlay lays each one out.
    /// </summary>
    public const int MaxSquigglySegments = 48;

    /// <summary>
    /// WHAT TO DRAW, which is not the same as what was marked.
    ///
    /// ⚠️ THE ONE MOVE THAT KEPT THIS SMALL. `Rects` stays the text band: it is
    /// what the hit test, the selection frame and the write to the core all
    /// need, and a two-pixel line would be impossible to click. What changes
    /// per kind is only the rectangle DRAWN, so the overlay keeps its one
    /// filled-rectangle template and gains no code at all: a highlight fills
    /// the band, an underline is a rule at its foot, a strikeout one across its
    /// middle.
    ///
    /// The colour and its alpha are used exactly as they were: nothing about
    /// the palette varies by kind.
    /// </summary>
    public IReadOnlyList<ColoredRect> ColoredRects =>
        Rects.SelectMany(r =>
        {
            double rule = Math.Max((r.Bottom - r.Top) * RuleThickness, 0.001);
            return Kind switch
            {
                MarkupKind.Underline => new[]
                {
                    new ColoredRect(r.Left, r.Bottom - rule, r.Right, r.Bottom, ColorHex),
                },
                MarkupKind.Strikeout => new[]
                {
                    new ColoredRect(
                        r.Left, ((r.Top + r.Bottom) / 2) - (rule / 2),
                        r.Right, ((r.Top + r.Bottom) / 2) + (rule / 2), ColorHex),
                },

                // The one kind that is not a single rectangle, which is why the
                // whole property became a SelectMany.
                MarkupKind.Squiggly => Squiggle(r, rule),

                _ => new[] { new ColoredRect(r.Left, r.Top, r.Right, r.Bottom, ColorHex) },
            };
        }).ToList();

    /// <summary>
    /// A squiggle, as a run of small rectangles stepped alternately up and down
    /// along the foot of the band.
    ///
    /// ⚠️ AN APPROXIMATION, DELIBERATELY. The overlay draws filled rectangles
    /// and nothing else, and there is no rectangle that is a wave. The two
    /// honest options were a flat rule, which shows the reader an underline for
    /// a mark that is not one, or this: still pure geometry, still the same one
    /// template, and it reads as a zigzag rather than as a line. What the SAVED
    /// file carries is PDFium's own squiggle, so the two will not match stroke
    /// for stroke.
    /// </summary>
    private IEnumerable<ColoredRect> Squiggle(TextRect r, double rule)
    {
        double height = r.Bottom - r.Top;
        double amplitude = rule * 1.4;
        double width = r.Right - r.Left;
        double step = Math.Max(height * 0.16, 0.0015);

        int count = Math.Clamp((int)Math.Ceiling(width / step), 2, MaxSquigglySegments);
        step = width / count;

        for (int i = 0; i < count; i++)
        {
            double left = r.Left + (i * step);
            double top = r.Bottom - rule - (i % 2 == 0 ? amplitude : 0);
            yield return new ColoredRect(left, top, left + step, top + rule, ColorHex);
        }
    }

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
