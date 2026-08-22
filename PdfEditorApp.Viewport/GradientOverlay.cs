using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The gradient-filled shapes Skia stands in for, and the narrow reason it has
/// to.
///
/// PDFium cannot make a shading. A gradient reaches a shape's appearance stream
/// only when the file is saved, by the lopdf writer, so between choosing one
/// and saving there is nothing inside the shape for PDFium to draw. This paints
/// it, over the top.
///
/// NOT THE OLD OVERLAY LIST. Committed shapes are PDFium's and stay that way,
/// which is what v1.72 decided when shapes stopped living in an overlay and
/// became real annotations. The filter here is one specific gap: a shape whose
/// appearance holds something PDFium cannot generate. Every other shape on
/// every page is drawn exactly as it was before this existed, and a document
/// with no gradient produces nothing at all.
///
/// AND NOT THEIR EFFECTS. A drop shadow and an outer glow are already in the
/// annotation, as paths or as a rasterised picture, and PDFium draws them
/// underneath. Sending them here as well would paint a second shadow over the
/// first.
///
/// The overlap was measured before any of this was written. The fill agrees
/// with the saved file to within 3 of 255, nothing lands on the paper, the body
/// of the stroke does not move, and what differs is a one-pixel antialiased rim
/// that comes from using two rasterisers at all rather than from drawing the
/// stroke twice. Painting over a shape whose gradient PDFium ALREADY draws,
/// which is what a saved and reopened document is, was measured to be identical
/// to the pixel: an opaque fill covers whatever was under it. That is why there
/// is one rule here and not one for each state of the file.
/// </summary>
public static class GradientOverlay
{
    /// <summary>
    /// What Skia should paint for one page, which is nothing at all unless that
    /// page has a gradient-filled shape on it.
    ///
    /// Everything comes from the page's MODEL, which was built for selection
    /// and hit-testing and has already read every tag. Nothing here reaches the
    /// document, so a page that is cached costs a walk of its own objects and
    /// no more.
    /// </summary>
    public static IReadOnlyList<ShapeRenderItem> ItemsFor(PageModel page)
    {
        List<ShapeRenderItem>? items = null;

        foreach (var shape in page.Shapes)
        {
            if (shape.Gradient is not { } gradient)
            {
                continue;
            }

            (items ??= new List<ShapeRenderItem>()).AddRange(ItemsFor(shape, page));
        }

        return (IReadOnlyList<ShapeRenderItem>?)items ?? Array.Empty<ShapeRenderItem>();
    }

    /// <summary>
    /// One gradient shape, as the painter takes it.
    ///
    /// THE UPRIGHT GEOMETRY, which the model already recovered: the shape as
    /// drawn, with the stroke pad taken off and any rotation undone. A fraction
    /// of the shape's box has to be measured against the box the person dragged
    /// out, not against the annotation's rectangle, which is bigger than the
    /// shape in both axes as soon as it is turned.
    /// </summary>
    private static IReadOnlyList<ShapeRenderItem> ItemsFor(ShapeObject shape, PageModel page)
    {
        double pageWidthPts = page.WidthPts;
        if (pageWidthPts <= 0)
        {
            return Array.Empty<ShapeRenderItem>();
        }

        // THE CORNER RADIUS HAS TO BE PUT BACK. The model's geometry carries
        // the box and the drag's direction but takes the record's default
        // corner fraction, because nothing reading the model for selection or
        // hit-testing needs the roundness. A renderer does: without this every
        // rounded rectangle would be drawn square.
        var upright = shape.UprightGeometry;
        var box = upright with
        {
            CornerFraction = ShapeGeometry.CornerFractionFromRadius(
                shape.CornerRadiusPts / pageWidthPts,
                upright.Right - upright.Left,
                upright.Bottom - upright.Top),
        };

        // NO EFFECTS. PDFium is already drawing them from the annotation, and a
        // second copy would double every shadow and every glow.
        var annotation = new ShapeAnnotation(
            page.PageIndex, box, shape.StrokeHex, shape.StrokeWidthPts / pageWidthPts)
        {
            Fill = ShapeFill.Of(shape.Gradient!.Value),
        };

        var items = ShapeRenderList.From([], [annotation]);

        if (shape.RotationDeg == 0)
        {
            return items;
        }

        // Turned about the shape's own centre, which is where render_core turns
        // it. Points and gradient endpoints go together, by one method, because
        // they are the same kind of number in the same space.
        double cx = (box.Left + box.Right) / 2;
        double cy = (box.Top + box.Bottom) / 2;

        var turned = new List<ShapeRenderItem>(items.Count);
        foreach (var item in items)
        {
            turned.Add(item.TurnedAbout(cx, cy, shape.RotationDeg));
        }

        return turned;
    }
}
