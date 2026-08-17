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
/// <param name="StrokeWidth">
/// Normalized, like the points: a width in pixels would mean something
/// different on every page size and at every zoom.
/// </param>
/// <param name="SlotWidth">
/// A width that is ALREADY in slot DIPs, for the one mark whose weight is fixed
/// rather than derived from the page: the freehand guide, which the overlay
/// draws at a flat 2 whatever the page is doing. When set it is used as-is and
/// <paramref name="StrokeWidth"/> is ignored.
///
/// A field rather than a reciprocal at the call site. The alternative was to
/// pass 2 / (scale * view.Scale) so the painter's multiplication cancels out,
/// which produces the right pixels and stores a number that is not a width of
/// anything, and that bakes one page's transform into an item the frame may
/// outlive. This says what it means instead.
/// </param>
public readonly record struct ShapeRenderItem(
    int PageIndex,
    IReadOnlyList<(double X, double Y)> Points,
    RenderColor Color,
    double StrokeWidth,
    RenderStyle Style,
    double? SlotWidth = null);

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

    /// <summary>The overlay's freehand guide colour, plain red.</summary>
    private static readonly RenderColor GuideRed = new(0xFF, 0xFF, 0x00, 0x00);

    /// <summary>
    /// The stroke being drawn right now, as the thin red guide the overlay
    /// draws for it.
    ///
    /// Its colour and weight are the guide's, NOT the ink's, which is the one
    /// way a freehand preview differs from a shape preview. Built here rather
    /// than at the call site so that rule is stated once, in the library that
    /// can be tested, instead of in a WinUI page that cannot.
    /// </summary>
    public static ShapeRenderItem InkGuide(
        int pageIndex, IReadOnlyList<(double X, double Y)> points) =>
        new(pageIndex, points, GuideRed, StrokeWidth: 0, RenderStyle.Stroked,
            SlotWidth: OverlayProjection.InkGuideWidthDips);

    /// <param name="preview">
    /// The shape being dragged right now, if there is one, as the annotation it
    /// is about to become.
    ///
    /// It is a ShapeAnnotation and not a type of its own because that is
    /// literally what it is: an in-progress draft differs from a committed
    /// shape only in not having been written yet, and ShapeAnnotation.Outline
    /// and .Head are already the exact geometry the live preview draws. Giving
    /// the preview its own emit path is how a shape comes to jump the instant
    /// the pointer lifts, so it goes through the same lines below.
    ///
    /// LAST, so it paints over everything committed, which is what the overlay
    /// does by adding it to the canvas after the rest.
    /// </param>
    /// <param name="inkPreview">
    /// The freehand stroke being drawn right now, from <see cref="InkGuide"/>.
    ///
    /// A separate argument from <paramref name="preview"/> because the two are
    /// different marks with different rules, not because both can happen: you
    /// are either dragging a shape or drawing freehand, never both. Kept in
    /// this method rather than concatenated by the caller so that PAINT ORDER
    /// stays decided in one tested place.
    /// </param>
    public static IReadOnlyList<ShapeRenderItem> From(
        IEnumerable<InkStrokeAnnotation> strokes,
        IEnumerable<ShapeAnnotation> shapes,
        ShapeAnnotation? preview = null,
        ShapeRenderItem? inkPreview = null)
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
            Emit(shape);
        }

        if (preview is not null)
        {
            Emit(preview);
        }

        if (inkPreview is { } guide)
        {
            items.Add(guide);
        }

        return items;

        void Emit(ShapeAnnotation shape)
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
