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
///
/// That includes the VIEW'S TURN, which it did not used to. Each page's
/// <see cref="PageTransform"/> arrives as data and is applied through the shared
/// projection, not composed into an SKMatrix of its own. Rotation stays a
/// property of the page, described in the app's coordinate system, and Skia
/// stays a thing that draws what it is handed.
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
        Func<int, PageTransform> pageView,
        ViewportProjection projection)
    {
        int saved = canvas.Save();
        canvas.Concat(MatrixFor(projection));
        Paint(canvas, items, scale, pageTop, pageView);
        canvas.RestoreToCount(saved);
    }

    /// <summary>
    /// Paints every stroked item in the frame, in list order, so later items
    /// land on top exactly as they do on the overlay.
    /// </summary>
    /// <param name="scale">The overlay scale, the fixed content-box width.</param>
    /// <param name="pageTop">Where a page's slot starts in the stack.</param>
    /// <param name="pageView">
    /// How each page is turned by the view. Asked per item, not once, because a
    /// continuous stack can show pages of different shapes and the turn's scale
    /// is a function of the page's own proportions.
    /// </param>
    public static void Paint(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        foreach (var item in items)
        {
            double top = pageTop(item.PageIndex);
            var view = pageView(item.PageIndex);

            // UNDERNEATH, and before this item rather than before the whole
            // frame: a shadow belongs to its own mark, so it goes below that
            // mark and above whatever was already painted. Shadowing the entire
            // frame first would put one shape's shadow over another shape.
            if (item.Effects?.Shadow is { } shadow)
            {
                PaintShadow(canvas, item, shadow, scale, top, view);
            }

            if (item.Style == RenderStyle.Filled)
            {
                PaintFilled(canvas, item, scale, top, view);
            }
            else
            {
                PaintStroked(canvas, item, scale, top, view);
            }
        }
    }

    /// <summary>
    /// The item again, shifted and recoloured, painted under it.
    ///
    /// The shift is applied to the NORMALIZED points, before the projection, so
    /// everything downstream is the code that already exists: the page's turn,
    /// the zoom, the display scale and the page's position in the stack all
    /// reach the shadow because they reach every point that goes through
    /// <see cref="OverlayProjection.ToSlot"/>. A shadow offset in device pixels
    /// would need all four of those handled again, by hand, and would slide out
    /// from under its shape the moment anybody rotated the page.
    ///
    /// Painted through the same two style branches as the mark itself, so an
    /// arrow's head casts a filled shadow and its shaft a stroked one, at the
    /// same weights. A shadow drawn as a generic outline would be a different
    /// shape from the thing casting it.
    /// </summary>
    private static void PaintShadow(
        SKCanvas canvas, ShapeRenderItem item, DropShadow shadow,
        double scale, double pageTop, PageTransform view)
    {
        var shifted = new (double X, double Y)[item.Points.Count];
        for (int at = 0; at < item.Points.Count; at++)
        {
            shifted[at] = (item.Points[at].X + shadow.OffsetX,
                           item.Points[at].Y + shadow.OffsetY);
        }

        // Same geometry, same weight, same style: only the position and the
        // colour differ, and Effects is dropped so the shadow cannot cast one.
        var ghost = item with { Points = shifted, Color = shadow.Color, Effects = null };

        if (ghost.Style == RenderStyle.Filled)
        {
            PaintFilled(canvas, ghost, scale, pageTop, view);
        }
        else
        {
            PaintStroked(canvas, ghost, scale, pageTop, view);
        }
    }

    /// <summary>
    /// The item's points, projected into slot space.
    ///
    /// Shared by both styles so there is one place a mark can be positioned,
    /// and NOT closed: the overlay draws a shaft as a Polyline whose last point
    /// repeats its first, and closing the path here would round the join at
    /// that vertex and stop the two renderers agreeing pixel for pixel. The
    /// filled path closes itself, because a XAML Polygon does.
    /// </summary>
    private static SKPath PathFor(
        ShapeRenderItem item, double scale, double pageTop, PageTransform view)
    {
        var path = new SKPath();

        for (int at = 0; at < item.Points.Count; at++)
        {
            var (x, y) = OverlayProjection.ToSlot(item.Points[at], scale, pageTop, view);
            if (at == 0)
            {
                path.MoveTo((float)x, (float)y);
            }
            else
            {
                path.LineTo((float)x, (float)y);
            }
        }

        return path;
    }

    /// <summary>
    /// A solid area: today only an arrow's head.
    ///
    /// The overlay builds one as a Polygon with Fill AND Stroke set to the same
    /// brush, at a literal StrokeThickness of 0.5, so it is painted here the
    /// same way: filled, then outlined with that same hairline. The hairline is
    /// what makes the tip the size it is in the file; dropping it leaves every
    /// arrow head a half-DIP smaller than the reference draws it.
    ///
    /// That 0.5 is NOT scaled, by the overlay or here. It is the one number in
    /// the projection that stays in slot DIPs whatever the page is doing, which
    /// is why OverlayProjection names it rather than leaving a bare literal in
    /// a paint call.
    /// </summary>
    private static void PaintFilled(
        SKCanvas canvas, ShapeRenderItem item, double scale, double pageTop, PageTransform view)
    {
        // A triangle needs three. Guarded the way the mapper guards on the head
        // count, so a head that could not be built is not painted as a sliver.
        if (item.Points.Count < 3)
        {
            return;
        }

        using var path = PathFor(item, scale, pageTop, view);
        path.Close();

        var color = ToSkColor(item.Color);

        using var fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = color,
            IsAntialias = true,
        };
        canvas.DrawPath(path, fill);

        using var hairline = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = color,
            StrokeWidth = (float)OverlayProjection.HeadHairlineDips,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Butt,
            StrokeJoin = SKStrokeJoin.Miter,
        };
        canvas.DrawPath(path, hairline);
    }

    private static void PaintStroked(
        SKCanvas canvas, ShapeRenderItem item, double scale, double pageTop, PageTransform view)
    {
        if (item.Points.Count < 2)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ToSkColor(item.Color),
            // WidthOf, not ToSlotThickness: the freehand guide carries a fixed
            // slot width instead of a normalized one, and asking the shared
            // rule is what keeps the painter and the culler measuring the same
            // mark the same way.
            StrokeWidth = (float)OverlayProjection.WidthOf(item, scale, view),

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

        using var path = PathFor(item, scale, pageTop, view);
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Skia takes its channels in RGBA order while the app carries them as
    /// ARGB, so this is the one place the two conventions meet.
    /// </summary>
    private static SKColor ToSkColor(RenderColor c) => new(c.R, c.G, c.B, c.A);
}
