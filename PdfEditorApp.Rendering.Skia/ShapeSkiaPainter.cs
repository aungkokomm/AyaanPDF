using PdfEditorApp.Viewport;
using SkiaSharp;

namespace PdfEditorApp.Rendering.Skia;

/// <summary>
/// Paints a frame of <see cref="ShapeRenderItem"/> onto a Skia canvas.
///
/// The candidate renderer, and nothing more. It is a pure function of the items
/// and the projection: it reads no view model, holds no state between frames,
/// and is not wired into the app. The XAML overlay remains the renderer on
/// screen and the reference this is measured against.
///
/// Every number it draws with comes from <see cref="OverlayProjection"/>. Skia's
/// own coordinate system is not adopted anywhere: points arrive normalized, are
/// projected by the app's existing rule, and the results are handed to Skia as
/// plain floats. No SK type crosses back out of this project.
/// </summary>
public static class ShapeSkiaPainter
{
    /// <summary>
    /// Slot DIPs to device pixels, as one matrix.
    ///
    /// Composed to agree with <see cref="ViewportProjection.SlotToDevice"/>
    /// exactly, and pinned to it by test: zoom applies in DIP space where the
    /// content lives, then the origin, then the display scale over everything.
    /// Swapping the first two agrees whenever the origin is zero, which is the
    /// state a window is in before anybody scrolls.
    ///
    /// This is the ONLY place Skia touches the coordinate chain. Normalized
    /// page-local remains canonical, slot DIPs remain the app's working space,
    /// and this is the last step before pixels.
    /// </summary>
    public static SKMatrix MatrixFor(ViewportProjection p) =>
        SKMatrix.CreateScale((float)p.DeviceScale, (float)p.DeviceScale)
            .PreConcat(SKMatrix.CreateTranslation((float)p.OriginXDips, (float)p.OriginYDips))
            .PreConcat(SKMatrix.CreateScale((float)p.Zoom, (float)p.Zoom));

    /// <summary>
    /// Paints a frame onto a viewport-sized surface: the matrix above, then the
    /// slot-space painter below, which is unchanged and does not know a
    /// viewport exists.
    /// </summary>
    public static void PaintViewport(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop,
        ViewportProjection projection)
    {
        int saved = canvas.Save();
        canvas.Concat(MatrixFor(projection));
        Paint(canvas, items, scale, pageTop);
        canvas.RestoreToCount(saved);
    }

    /// <summary>
    /// Paints every stroked item in the frame, in list order, so later items
    /// land on top exactly as they do on the overlay.
    /// </summary>
    /// <param name="scale">The overlay scale, the fixed content-box width.</param>
    /// <param name="pageTop">Where a page's slot starts in the stack.</param>
    public static void Paint(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop)
    {
        foreach (var item in items)
        {
            // Filled items (an arrow's head) are stage 2. Skipping is stated
            // here and pinned by a test rather than left to be discovered as a
            // missing arrow tip.
            if (item.Style != RenderStyle.Stroked)
            {
                continue;
            }

            PaintStroked(canvas, item, scale, pageTop(item.PageIndex));
        }
    }

    private static void PaintStroked(
        SKCanvas canvas, ShapeRenderItem item, double scale, double pageTop)
    {
        if (item.Points.Count < 2)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ToSkColor(item.Color),
            StrokeWidth = (float)OverlayProjection.ToSlotThickness(item.StrokeWidth, scale),

            // The overlay is a XAML Polyline, which antialiases. Skia does not
            // by default, and leaving it off is a visible parity difference on
            // every diagonal and every curve.
            IsAntialias = true,

            // Polyline's defaults, restated because Skia's happen to agree and
            // a silent agreement is not the same as a checked one: flat caps,
            // mitred joins.
            StrokeCap = SKStrokeCap.Butt,
            StrokeJoin = SKStrokeJoin.Miter,
        };

        using var path = new SKPath();
        for (int at = 0; at < item.Points.Count; at++)
        {
            var (x, y) = OverlayProjection.ToSlot(item.Points[at], scale, pageTop);
            if (at == 0)
            {
                path.MoveTo((float)x, (float)y);
            }
            else
            {
                path.LineTo((float)x, (float)y);
            }
        }

        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Skia takes its channels in RGBA order while the app carries them as
    /// ARGB, so this is the one place the two conventions meet.
    /// </summary>
    private static SKColor ToSkColor(RenderColor c) => new(c.R, c.G, c.B, c.A);
}
