using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The one committed shape Skia draws, and the narrow reason it does.
///
/// Committed shapes are PDFium's. That was decided deliberately in v1.72 when
/// shapes stopped living in an overlay list and became real annotations, which
/// is what gave them handles, rotation, resize and restyle, and nothing here
/// reopens it: there is no list, no second life, and no shape in this path that
/// is not the one currently selected.
///
/// WHAT PDFIUM CANNOT DO is make a shading. A gradient reaches a shape's
/// appearance stream only when the file is saved, so between choosing a
/// gradient and saving, PDFium has nothing to draw inside the shape and the
/// shape appears empty. This paints it, over the top, until the save catches
/// up.
///
/// ONLY A GRADIENT, and only while it is selected. A solid fill is already in
/// the appearance stream and painting it again would double a translucent one.
///
/// AND NOT ITS EFFECTS. A drop shadow and an outer glow are already in the
/// annotation, as paths or as a rasterised picture, and PDFium draws them
/// underneath. Sending them here as well would paint a second shadow over the
/// first.
///
/// The overlap this causes was measured before it was written: the fill
/// interior agrees with the saved file to within 3 of 255, nothing lands on the
/// paper, the body of the stroke does not move, and the difference is a
/// one-pixel antialiased rim that comes from using two rasterisers at all
/// rather than from drawing the stroke twice.
/// </summary>
public static class SelectedShapeOverlay
{
    /// <summary>
    /// What Skia should paint for the selected shape, or nothing at all.
    ///
    /// Empty is the ordinary answer: no gradient, no overlay. Every renderer
    /// downstream then behaves exactly as it did before this existed.
    /// </summary>
    /// <param name="left">
    /// The shape's UPRIGHT box in normalized page units, which is the box its
    /// tag describes rather than the axis-aligned rectangle a turned shape
    /// occupies. <c>shape_upright_bounds</c> in render_core is the one
    /// inversion that recovers it.
    /// </param>
    /// <param name="pageWidthPts">
    /// What turns the tag's points into the model's normalized lengths.
    /// </param>
    public static IReadOnlyList<ShapeRenderItem> ItemsFor(
        ShapeTag tag,
        int pageIndex,
        double left,
        double top,
        double right,
        double bottom,
        double pageWidthPts)
    {
        if (pageWidthPts <= 0 || right <= left || bottom <= top)
        {
            return Array.Empty<ShapeRenderItem>();
        }

        var fill = ShapeFillTag.From(tag);
        if (fill.Gradient is null)
        {
            return Array.Empty<ShapeRenderItem>();
        }

        var draft = new ShapeDraft(tag.Kind, left, top, right, bottom)
        {
            CornerFraction = ShapeGeometry.CornerFractionFromRadius(
                tag.CornerRadiusPts / pageWidthPts, right - left, bottom - top),
        };

        // NO EFFECTS. PDFium is already drawing them from the annotation, and a
        // second copy would double every shadow and every glow.
        var shape = new ShapeAnnotation(
            pageIndex, draft, tag.StrokeHex, tag.StrokeWidthPts / pageWidthPts)
        {
            Fill = fill,
        };

        var items = ShapeRenderList.From([], [shape]);

        if (tag.RotationDeg == 0)
        {
            return items;
        }

        // Turned about the shape's own centre, which is where render_core turns
        // it. Points and gradient endpoints go together, by one method, because
        // they are the same kind of number in the same space.
        double cx = (left + right) / 2;
        double cy = (top + bottom) / 2;

        var turned = new List<ShapeRenderItem>(items.Count);
        foreach (var item in items)
        {
            turned.Add(item.TurnedAbout(cx, cy, tag.RotationDeg));
        }

        return turned;
    }
}
