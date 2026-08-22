using PdfEditorApp.Viewport;
using SkiaSharp;

namespace PdfEditorApp.Rendering.Skia;

/// <summary>A rasterised shadow, ready to go into a page.</summary>
/// <param name="Bgra">Premultiplied BGRA, as PDFium takes it.</param>
/// <param name="Left">The box it covers, in NORMALIZED page units, which is
/// what the rest of the app measures in.</param>
public readonly record struct ShadowRaster(
    byte[] Bgra, int PixelWidth, int PixelHeight,
    double Left, double Top, double Right, double Bottom);

/// <summary>
/// The committed half of an effect.
///
/// A page renders through PDFium, which draws paths and has no blur, so a SOFT
/// shadow cannot be a path in the file. It is drawn here by the same
/// SKImageFilter the preview uses and handed to render_core as pixels, which it
/// puts in the shape's own annotation underneath the shape. That is what makes
/// the preview and the saved page the same picture: one renderer decides what a
/// shadow looks like, and the file carries the result.
///
/// A HARD shadow never comes here. render_core draws it as paths, crisp at any
/// zoom, and no raster resolution reproduces a hard edge: measured in the
/// prototype, the error never converges and it is visibly soft at eight pixels
/// to the point.
/// </summary>
public static class ShadowRasterizer
{
    /// <summary>
    /// How finely to sample, in pixels per POINT.
    ///
    /// Measured against a 32 px/pt reference at 8x zoom: the worst channel
    /// error stays at or below 3 levels across every blur the UI allows, for 4
    /// to 10 KB a shadow. A blurred shadow has no fine detail to lose, so the
    /// demanding case is a SMALL blur rather than a large one, which is why
    /// this is a floor rather than something that scales down with the radius.
    /// </summary>
    public const double PixelsPerPoint = 6.0;

    /// <summary>
    /// The most pixels the longest side may take.
    ///
    /// A full-page shape at the rate above would be four thousand pixels
    /// across, which is tens of megabytes for something nobody can see. Where
    /// the cap bites the shape is large, and a shape that large with a blur big
    /// enough to matter is sampled far more finely than it needs anyway.
    /// </summary>
    public const int MaxSide = 2048;

    /// <summary>
    /// The marks that cast a committed shape's shadow, from its own tag and box.
    ///
    /// Built through ShapeGeometry, the same outline the shape itself is drawn
    /// from, so the shadow and the thing casting it cannot describe different
    /// shapes.
    ///
    /// A FILLED shape gets a filled mark as well as its outline. ShapeRenderList
    /// emits only a stroked outline, because the live overlay has never drawn a
    /// fill, and handing that straight to the filter made a solid rectangle cast
    /// the shadow of a wire frame: a thin soft band instead of a solid one.
    /// Measured, at 50 per cent black over white: 185 where it should have been
    /// 127. The alpha the filter reads has to be the alpha the shape really has.
    /// </summary>
    public static IReadOnlyList<ShapeRenderItem> CasterItemsFor(
        ShapeTag tag, double left, double top, double right, double bottom, double pageWidthPts)
    {
        if (pageWidthPts <= 0 || right <= left || bottom <= top)
        {
            return Array.Empty<ShapeRenderItem>();
        }

        var draft = new ShapeDraft(tag.Kind, left, top, right, bottom)
        {
            CornerFraction = ShapeGeometry.CornerFractionFromRadius(
                tag.CornerRadiusPts / pageWidthPts, right - left, bottom - top),
        };

        double strokeNorm = tag.StrokeWidthPts / pageWidthPts;
        var shape = new ShapeAnnotation(0, draft, tag.StrokeHex, strokeNorm);
        var items = new List<ShapeRenderItem>(
            ShapeRenderList.From(Array.Empty<InkStrokeAnnotation>(), new[] { shape }));

        // DOES THE SHAPE HAVE AN INSIDE? Asked of the whole fill and not of the
        // positional field alone, which is the bug this line used to be.
        //
        // A gradient lives on the tag's TAIL and clears the positional field,
        // because a shape must never carry both. So a gradient-filled shape
        // read that field as null, cast its shadow as a hollow one, and got
        // back the exact wire-frame band the comment below was written about.
        // A solid and a gradient are both an inside; only a stroke-only shape
        // has none, and that one is still correctly hollow.
        if (!ShapeFillTag.From(tag).IsEmpty && items.Count > 0)
        {
            // UNDER the outline, the way a fill sits under its own stroke.
            // Colour is irrelevant here, only coverage, but the shape's own
            // fill colour keeps it honest for anyone reading the pixels.
            items.Insert(0, items[0] with { Style = RenderStyle.Filled });
        }

        if (tag.RotationDeg == 0)
        {
            return items;
        }

        // Turned about the shape's own centre, which is where render_core turns
        // it. The picture carries the turn in its pixels rather than being
        // turned as a whole later, because turning the picture would turn the
        // light with it.
        //
        // THROUGH THE SHARED TURN, which also carries a gradient's endpoints.
        // This path never has one today, and the point of going through the one
        // method is that it cannot start having one that fails to turn.
        double cx = (left + right) / 2;
        double cy = (top + bottom) / 2;

        return items
            .Select(i => i.TurnedAbout(cx, cy, tag.RotationDeg))
            .ToList();
    }

    /// <summary>
    /// The box a shadow's ink can land in, in normalized page units.
    ///
    /// The object's own box, moved by the offset, opened out by half the stroke
    /// and by the blur's reach. The SHADOW's box, not the object's: the object
    /// is drawn as paths by render_core and does not belong in the picture.
    ///
    /// render_core reserves exactly this much room in the annotation's
    /// rectangle, through its own shadow_reach_pts, because PDFium crops an
    /// appearance to that rectangle and a shadow that reached past it would be
    /// cut off.
    /// </summary>
    public static (double L, double T, double R, double B) BoundsOf(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow) =>
        BoundsOf(group, shadow.ToSpec());

    /// <summary>
    /// The box every RASTERISED effect's ink can land in, in normalized page
    /// units: the union of what each one needs.
    ///
    /// ONLY THE ONES THAT ARE IN THE PICTURE count. An unblurred effect is drawn
    /// as paths by render_core, so counting its reach here would size the box
    /// for something the picture does not contain and leave the picture sitting
    /// off-centre inside it.
    /// </summary>
    public static (double L, double T, double R, double B) BoundsOf(
        IReadOnlyList<ShapeRenderItem> group, IReadOnlyList<EffectSpec> specs)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;
        bool any = false;

        foreach (var spec in specs)
        {
            if (!GoesInThePicture(spec))
            {
                continue;
            }

            var box = BoundsOf(group, spec);
            l = Math.Min(l, box.L);
            t = Math.Min(t, box.T);
            r = Math.Max(r, box.R);
            b = Math.Max(b, box.B);
            any = true;
        }

        return any ? (l, t, r, b) : (0, 0, 0, 0);
    }

    /// <summary>
    /// Whether this effect is drawn here rather than as paths in the file.
    ///
    /// A BLUR IS WHAT DECIDES. PDF has no blur for a path object, so anything
    /// with one has to arrive as pixels; anything without one is crisp vector
    /// geometry that no raster resolution improves on. An effect in no colour
    /// paints nothing either way.
    /// </summary>
    private static bool GoesInThePicture(EffectSpec spec) =>
        spec.Blur > 0 && spec.Color.A != 0;

    /// <inheritdoc cref="BoundsOf(IReadOnlyList{ShapeRenderItem}, DropShadow)"/>
    public static (double L, double T, double R, double B) BoundsOf(
        IReadOnlyList<ShapeRenderItem> group, EffectSpec spec)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;
        double pad = 0;

        foreach (var item in group)
        {
            foreach (var (x, y) in item.Points)
            {
                l = Math.Min(l, x);
                t = Math.Min(t, y);
                r = Math.Max(r, x);
                b = Math.Max(b, y);
            }

            pad = Math.Max(pad, item.StrokeWidth / 2);
        }

        if (l > r || t > b)
        {
            return (0, 0, 0, 0);
        }

        // EffectSpec.Reach, which is the same number the preview's layer and
        // the dirty region open by and the same one render_core reserves in the
        // annotation, each in its own units.
        double reach = pad + spec.Reach;

        return (l + spec.OffsetX - reach, t + spec.OffsetY - reach,
                r + spec.OffsetX + reach, b + spec.OffsetY + reach);
    }

    /// <summary>
    /// Draws the shadow for one object, or null when there is nothing to draw.
    ///
    /// <paramref name="pageWidthPts"/> converts the model's normalized lengths
    /// into points, which is what the sampling rate is expressed in.
    /// </summary>
    public static ShadowRaster? Rasterize(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow, double pageWidthPts) =>
        Rasterize(group, new[] { shadow.ToSpec() }, pageWidthPts);

    /// <inheritdoc cref="Rasterize(IReadOnlyList{ShapeRenderItem}, DropShadow, double)"/>
    public static ShadowRaster? Rasterize(
        IReadOnlyList<ShapeRenderItem> group, EffectSpec spec, double pageWidthPts) =>
        Rasterize(group, new[] { spec }, pageWidthPts);

    /// <summary>
    /// Draws every rasterised effect for one object into ONE picture, in list
    /// order with the first underneath, or null when there is nothing to draw.
    ///
    /// One picture because the annotation has ONE image slot. Two would mean two
    /// image objects, a second attach path, a second thing to keep in step with
    /// the tag, and a second thing to leave behind when an effect is removed.
    /// </summary>
    public static ShadowRaster? Rasterize(
        IReadOnlyList<ShapeRenderItem> group, IReadOnlyList<EffectSpec> specs,
        double pageWidthPts)
    {
        if (group.Count == 0 || pageWidthPts <= 0)
        {
            return null;
        }

        var drawn = new List<EffectSpec>(specs.Count);
        foreach (var spec in specs)
        {
            if (GoesInThePicture(spec))
            {
                drawn.Add(spec);
            }
        }

        if (drawn.Count == 0)
        {
            return null;
        }

        var box = BoundsOf(group, drawn);
        double wNorm = box.R - box.L;
        double hNorm = box.B - box.T;
        if (wNorm <= 0 || hNorm <= 0)
        {
            return null;
        }

        double rate = PixelsPerPoint;
        double longest = Math.Max(wNorm, hNorm) * pageWidthPts;
        if (longest * rate > MaxSide)
        {
            rate = MaxSide / longest;
        }

        int pxW = Math.Max(1, (int)Math.Ceiling(wNorm * pageWidthPts * rate));
        int pxH = Math.Max(1, (int)Math.Ceiling(hNorm * pageWidthPts * rate));

        // A private page space for the drawing, so the projection the painter
        // needs is an ordinary unrotated one and the box maps onto the bitmap.
        // Any scale would do; the page's own width in points keeps the numbers
        // recognisable while debugging.
        double scale = pageWidthPts;
        var view = PageTransform.For(scale, scale, 0, scale);

        var bitmap = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)(pxW / (wNorm * scale)), (float)(pxH / (hNorm * scale)));
            canvas.Translate((float)(-box.L * scale), (float)(-box.T * scale));

            // ONE LAYER PER EFFECT, in list order, so a later one composites
            // over an earlier one exactly as it does in the preview.
            var casters = group.Select(i => i with { Effects = null }).ToList();

            foreach (var spec in drawn)
            {
                // THE SAME RECIPE THE PREVIEW USES, which is what makes the
                // saved page and the screen the same picture rather than two
                // renderings that happen to agree. Null is an effect this build
                // cannot draw, so it contributes nothing rather than sinking
                // the whole picture.
                using var filter = EffectRecipe.FilterFor(spec, scale, view);
                if (filter is null)
                {
                    continue;
                }

                using var paint = new SKPaint { ImageFilter = filter };

                canvas.SaveLayer(paint);
                ShapeSkiaPainter.PaintViewport(
                    canvas, casters, scale, _ => 0, _ => view,
                    new ViewportProjection(1, 1, 0, 0));
                canvas.Restore();
            }
        }

        var bytes = new byte[bitmap.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(
            bitmap.GetPixels(), bytes, 0, bytes.Length);
        bitmap.Dispose();

        return new ShadowRaster(bytes, pxW, pxH, box.L, box.T, box.R, box.B);
    }
}
