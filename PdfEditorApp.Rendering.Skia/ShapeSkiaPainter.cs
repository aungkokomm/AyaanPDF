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
        // BY OBJECT, not by item. One object can take several marks to draw,
        // and its effects belong to the object: an arrow shadowed per mark
        // casts two shadows that darken where they overlap, and paints the
        // head's shadow on top of the shaft.
        //
        // Still in list order, and still one object at a time rather than all
        // the shadows first, so a shadow goes under its own object and above
        // whatever was already painted rather than under the whole frame.
        for (int at = 0; at < items.Count;)
        {
            int end = EndOfObject(items, at);
            PaintObject(canvas, items, at, end, scale, pageTop, pageView);
            at = end;
        }
    }

    /// <summary>
    /// One past the last item belonging to the same object as the one at
    /// <paramref name="first"/>.
    ///
    /// The parts of an object are ADJACENT, which the list has always
    /// guaranteed. An item with no object id stands alone, which is what every
    /// mark did before objects were expressed here and what keeps a hand-built
    /// frame behaving as it always did.
    /// </summary>
    private static int EndOfObject(IReadOnlyList<ShapeRenderItem> items, int first)
    {
        Guid id = items[first].ObjectId;
        if (id == Guid.Empty)
        {
            return first + 1;
        }

        int at = first + 1;
        while (at < items.Count && items[at].ObjectId == id)
        {
            at++;
        }

        return at;
    }

    /// <summary>One object: its shadow once, underneath, then its marks in order.</summary>
    private static void PaintObject(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        int from,
        int to,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        // The effects are the object's, so the first mark's are the object's.
        // Every mark of one object carries the same instance.
        if (items[from].Effects?.Shadow is { } shadow)
        {
            PaintObjectShadow(canvas, items, from, to, shadow, scale, pageTop, pageView);
        }

        for (int at = from; at < to; at++)
        {
            var item = items[at];
            double top = pageTop(item.PageIndex);
            var view = pageView(item.PageIndex);

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
    /// The object's shadow, produced by Skia from the object itself.
    ///
    /// ONE LAYER PER OBJECT, with a drop-shadow image filter on it. The object
    /// is painted into the layer and what comes out is the shadow alone:
    /// offset, blurred and coloured, with no shadow-shaped geometry built by
    /// hand anywhere.
    ///
    /// THE SHADOW IS DERIVED FROM THE OBJECT'S ALPHA, which is the whole reason
    /// to do it this way. A hollow rectangle casts a hollow shadow because its
    /// middle is transparent, and an arrow's two marks are unioned inside the
    /// layer before the filter runs, so they cast one shadow rather than two
    /// that darken where they overlap. The rule that used to decide which marks
    /// cast a solid silhouette is gone: nothing has to decide, because the
    /// alpha already says.
    ///
    /// CreateDropShadowOnly rather than CreateDropShadow: the object is painted
    /// again afterwards through the ordinary path, so it keeps its own colours
    /// and its own compositing, and the filter is left doing only the part that
    /// is hard. Measured in the prototype at about forty per cent cheaper than
    /// letting the filter draw the object too.
    ///
    /// THE LAYER IS BOUNDED. Left to itself Skia allocates one the size of the
    /// surface, measured at 550 to 750ms a frame against 5 to 12 bounded. The
    /// bounds are the object's own, blur reach included, from the same
    /// SlotBoundsOf the dirty region reads, so the buffer follows the shadow
    /// rather than the viewport.
    /// </summary>
    private static void PaintObjectShadow(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        int from,
        int to,
        DropShadow shadow,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        int page = items[from].PageIndex;
        var view = pageView(page);
        double top = pageTop(page);

        // THE OFFSET IS PROJECTED, not scaled. It is a page-local vector, so on
        // a turned page it has to go where the PAGE says down-and-right is, and
        // the page's turn lives in this projection rather than in a canvas
        // matrix. Measured as the difference between two projected points, so
        // the zoom, the display scale and the turn all reach it through exactly
        // the code every mark already goes through. Scaling the components
        // instead left the shadow pointing the wrong way at 90, 180 and 270.
        var origin = OverlayProjection.ToSlot((0, 0), scale, top, view);
        var thrown = OverlayProjection.ToSlot((shadow.OffsetX, shadow.OffsetY), scale, top, view);

        float dx = (float)(thrown.X - origin.X);
        float dy = (float)(thrown.Y - origin.Y);

        // Sigma is a length and has no direction, so it takes the ordinary
        // rule. It rides the canvas matrix, so the blur scales with the zoom.
        float sigma = (float)OverlayProjection.BlurSigmaOf(shadow, scale, view);

        var color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A);

        using var filter = SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color);
        using var lift = new SKPaint { ImageFilter = filter };

        int saved = canvas.SaveLayer(
            LayerBounds(items, from, to, scale, pageTop, pageView), lift);

        for (int at = from; at < to; at++)
        {
            // Effects dropped, so the shadow cannot cast one of its own, and
            // the marks go down exactly as they normally would: the filter
            // reads their alpha and nothing else about them matters.
            var item = items[at] with { Effects = null };

            if (item.Style == RenderStyle.Filled)
            {
                PaintFilled(canvas, item, scale, pageTop(item.PageIndex), pageView(item.PageIndex));
            }
            else
            {
                PaintStroked(canvas, item, scale, pageTop(item.PageIndex), pageView(item.PageIndex));
            }
        }

        canvas.RestoreToCount(saved);
    }

    /// <summary>
    /// The rectangle a shadow can reach, in the canvas's own slot DIPs.
    ///
    /// The union of what the dirty region already computes per item, which
    /// covers the mark, its offset shadow and the blur's reach past it. Read
    /// from the same place so the buffer painted into and the region cleared
    /// afterwards cannot disagree.
    /// </summary>
    private static SKRect LayerBounds(
        IReadOnlyList<ShapeRenderItem> items,
        int from,
        int to,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;

        for (int at = from; at < to; at++)
        {
            var item = items[at];
            var (il, it, ir, ib) = OverlayProjection.SlotBoundsOf(
                item, scale, pageTop(item.PageIndex), pageView(item.PageIndex));

            l = Math.Min(l, il); t = Math.Min(t, it);
            r = Math.Max(r, ir); b = Math.Max(b, ib);
        }

        return new SKRect((float)l, (float)t, (float)r, (float)b);
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
