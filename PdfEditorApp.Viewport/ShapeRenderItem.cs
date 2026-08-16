using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A colour as four channels, already parsed out of its hex string.</summary>
/// <remarks>
/// Parsed once in the mapper rather than in the paint loop. A renderer that
/// re-parses "#FF3B82F6" for every item on every frame is doing string work
/// inside the thing that has to stay smooth.
/// </remarks>
public readonly record struct RenderColor(byte A, byte R, byte G, byte B);

/// <summary>How an item is painted.</summary>
public enum RenderStyle
{
    /// <summary>An outline: ink, and the shaft of every shape.</summary>
    Stroked,

    /// <summary>
    /// A solid area, used only by an arrow's head. It carries a hairline in the
    /// same colour as well, which is what the overlay draws, and dropping it
    /// would make every arrow tip a fraction smaller than the one in the file.
    /// </summary>
    Filled,
}

/// <summary>
/// One thing to paint this frame, in NORMALIZED page-local units.
///
/// A flat, renderer-agnostic description with no WinUI and no Skia in it: the
/// point of the type is that two different renderers can be handed the same
/// list and be compared against each other. It deliberately carries no shape
/// kind and no rotation, because the existing overlay uses neither. A shape has
/// already become a point list by the time it gets here, and adding fields that
/// nothing reads would be inventing behaviour during a migration whose whole
/// rule is that rendering is the only variable.
/// </summary>
public readonly record struct ShapeRenderItem(
    int PageIndex,
    IReadOnlyList<(double X, double Y)> Points,
    RenderColor Color,
    double StrokeWidth,
    RenderStyle Style);

/// <summary>
/// Turns the marks the view model holds into a frame's worth of render items.
///
/// Pure, and the seam the Skia work is built on. The existing XAML overlay
/// stays exactly as it is and remains the reference renderer; this produces the
/// same marks in the same order so a second renderer can be proved equal to it
/// rather than merely looking similar.
///
/// ORDER IS THE OUTPUT. The list is in paint order, later items on top, and it
/// mirrors what the overlay builds: every ink stroke first, then each shape as
/// its shaft followed immediately by its head. Sorting or grouping this list
/// would silently restack overlapping marks.
/// </summary>
public static class ShapeRenderList
{
    /// <summary>An arrow's head is a triangle; anything else is not a head.</summary>
    private const int HeadPointCount = 3;

    public static IReadOnlyList<ShapeRenderItem> From(
        IEnumerable<InkStrokeAnnotation> strokes,
        IEnumerable<ShapeAnnotation> shapes)
    {
        var items = new List<ShapeRenderItem>();

        foreach (var stroke in strokes)
        {
            items.Add(new ShapeRenderItem(
                stroke.PageIndex,
                stroke.Points,
                ColorOf(stroke.ColorHex),
                stroke.StrokeWidth,
                RenderStyle.Stroked));
        }

        foreach (var shape in shapes)
        {
            var color = ColorOf(shape.ColorHex);

            items.Add(new ShapeRenderItem(
                shape.PageIndex, shape.Outline, color, shape.StrokeWidth, RenderStyle.Stroked));

            // Guarded on the count rather than on the kind, matching the
            // overlay: a shape whose head could not be built is not drawn as a
            // two-point sliver.
            var head = shape.Head;
            if (head.Count == HeadPointCount)
            {
                items.Add(new ShapeRenderItem(
                    shape.PageIndex, head, color, shape.StrokeWidth, RenderStyle.Filled));
            }
        }

        return items;
    }

    /// <summary>
    /// The overlay's own colour rule, reached through the parser the rest of the
    /// library already uses. It reads the same eight hex characters the overlay
    /// reads, and additionally tolerates a six-character or empty string, which
    /// the overlay's parser throws on. Being harder to crash is a safe
    /// difference; producing a different colour would not be.
    /// </summary>
    private static RenderColor ColorOf(string hex)
    {
        var (a, r, g, b) = InkPresets.ParseHex(hex);
        return new RenderColor(a, r, g, b);
    }
}
