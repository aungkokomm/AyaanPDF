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
    /// <summary>TEMPORARY diagnostic counters for the gradient live-editing trace.</summary>
    public static int GradientDraws;

    /// <summary>TEMPORARY diagnostic for the gradient live-editing trace.</summary>
    public static string LastGradientSlot = "-";

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

    /// <summary>One object: its effects once, underneath, then its marks in order.</summary>
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
        //
        // IN LIST ORDER, first at the bottom, each in its own layer, so a second
        // effect composites over the first the way everything else the app
        // paints does.
        foreach (var spec in items[from].Effects?.Specs ?? Array.Empty<EffectSpec>())
        {
            PaintObjectEffect(canvas, items, from, to, spec, scale, pageTop, pageView);
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
    /// One of the object's effects, produced by Skia from the object itself.
    ///
    /// ONE LAYER PER EFFECT, with that effect's image filter on it. The object
    /// is painted into the layer and what comes out is the effect alone, with
    /// no effect-shaped geometry built by hand anywhere. Which filter is
    /// <see cref="EffectRecipe"/>'s to decide; this is only where it is applied.
    ///
    /// THE EFFECT IS DERIVED FROM THE OBJECT'S ALPHA, which is the whole reason
    /// to do it this way. A hollow rectangle casts a hollow shadow because its
    /// middle is transparent, and an arrow's two marks are unioned inside the
    /// layer before the filter runs, so they cast one shadow rather than two
    /// that darken where they overlap. The rule that used to decide which marks
    /// cast a solid silhouette is gone: nothing has to decide, because the
    /// alpha already says.
    ///
    /// THE LAYER IS BOUNDED. Left to itself Skia allocates one the size of the
    /// surface, measured at 550 to 750ms a frame against 5 to 12 bounded. The
    /// bounds are the object's own, every effect's reach included, from the same
    /// SlotBoundsOf the dirty region reads, so the buffer follows the ink rather
    /// than the viewport.
    /// </summary>
    private static void PaintObjectEffect(
        SKCanvas canvas,
        IReadOnlyList<ShapeRenderItem> items,
        int from,
        int to,
        EffectSpec spec,
        double scale,
        Func<int, double> pageTop,
        Func<int, PageTransform> pageView)
    {
        using var filter = EffectRecipe.FilterFor(spec, scale, pageView(items[from].PageIndex));
        if (filter is null)
        {
            // An effect this build cannot draw. The object still paints.
            return;
        }

        using var lift = new SKPaint { ImageFilter = filter };

        int saved = canvas.SaveLayer(
            LayerBounds(items, from, to, scale, pageTop, pageView), lift);

        for (int at = from; at < to; at++)
        {
            // Effects dropped, so the effect cannot cast one of its own, and
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

    /// <summary>
    /// A mark's outline, and its inside first when it has one.
    ///
    /// FILL THEN STROKE, in that order, which is not a preference: PDF's
    /// combined paint operator fills and then strokes, so PDFium puts the
    /// stroke's inner half on TOP of the fill and the shape is exactly as thick
    /// as its stroke says. Painting the other way round eats half the outline
    /// and every filled shape comes out looking thinner here than in the file.
    ///
    /// THE SAME PATH, not a second one built for the fill. Whatever moves,
    /// turns, resizes or zooms the outline moves the fill with it, because
    /// there is only one geometry to move. A line or an arrow shaft encloses no
    /// area, so a fill on one paints nothing without anybody having to decide
    /// that it should not.
    ///
    /// A GRADIENT IS REAL VECTOR PAINT, a Skia shader on the same path, not a
    /// picture of one. It stays sharp at any zoom for the same reason the
    /// outline does, and it is clipped by the path rather than by a rectangle,
    /// because the path is what is being filled.
    /// </summary>
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

        // Skia closes a contour to fill it, which is what a fill means. The
        // path itself stays open so the STROKE is unchanged: a shaft is drawn
        // as a polyline whose last point repeats its first, and closing it
        // would round the join at that vertex.
        PaintInside(canvas, path, item, scale, pageTop, view);

        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// The inside of a mark: one colour, a gradient, or nothing at all.
    ///
    /// A GRADIENT IS A SHADER, not a picture of one. The paint stays
    /// resolution-free, so it is as sharp at eight hundred percent as the
    /// outline round it, and it is clipped by the PATH because the path is what
    /// is being filled. Nothing here knows what shape it is filling.
    ///
    /// THE ENDPOINTS ARE PROJECTED LIKE ANY OTHER POINT, through the same
    /// <see cref="OverlayProjection.ToSlot"/> the path itself goes through, and
    /// that is the whole of the transform story. The page's turn, the scroll
    /// and the zoom all reach the gradient because they reach that projection.
    /// Moving, resizing or turning the SHAPE reaches it because the endpoints
    /// were resolved into the mark's own space beside its points; see
    /// <see cref="GradientFill.InBox"/>.
    ///
    /// CLAMPED at both ends, which is what PDF's <c>Extend</c> array says when
    /// both its entries are true. A gradient may begin before the shape and end
    /// after it, and past its ends the paint is that end's own colour rather
    /// than nothing.
    /// </summary>
    private static void PaintInside(
        SKCanvas canvas,
        SKPath path,
        ShapeRenderItem item,
        double scale,
        double pageTop,
        PageTransform view)
    {
        if (item.Fill.Solid is { } inside)
        {
            using var solid = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = ToSkColor(inside),

                // As the stroke is, and for the same reason: without it every
                // curve and diagonal edge is a staircase against the page.
                IsAntialias = true,
            };

            canvas.DrawPath(path, solid);
            return;
        }

        if (item.Fill.Gradient is not { } gradient)
        {
            return;
        }

        var from = OverlayProjection.ToSlot((gradient.X0, gradient.Y0), scale, pageTop, view);
        var to = OverlayProjection.ToSlot((gradient.X1, gradient.Y1), scale, pageTop, view);

        // TEMPORARY diagnostic for the gradient live-editing trace.
        GradientDraws++;
        var pb = path.Bounds;
        LastGradientSlot =
            $"from=({from.X:F1},{from.Y:F1}) to=({to.X:F1},{to.Y:F1}) pathBounds=({pb.Left:F1},{pb.Top:F1})-({pb.Right:F1},{pb.Bottom:F1})";

        // Both live until after the draw, the way the effect layer's filter and
        // its paint do. Which of the two owns the native object is not a thing
        // to be clever about at a call site.
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint((float)from.X, (float)from.Y),
            new SKPoint((float)to.X, (float)to.Y),
            [ToSkColor(gradient.From), ToSkColor(gradient.To)],
            SKShaderTileMode.Clamp);

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Shader = shader,
        };

        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Skia takes its channels in RGBA order while the app carries them as
    /// ARGB, so this is the one place the two conventions meet.
    /// </summary>
    private static SKColor ToSkColor(RenderColor c) => new(c.R, c.G, c.B, c.A);
}
