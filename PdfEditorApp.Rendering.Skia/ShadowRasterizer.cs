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
/// The committed half of a Drop Shadow.
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
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow)
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

        // Three sigma, and sigma is half the radius: the same reach
        // OverlayProjection uses for the preview's layer and the dirty region.
        double reach = pad + (shadow.Softness * OverlayProjection.BlurReachSigmas / 2);

        return (l + shadow.OffsetX - reach, t + shadow.OffsetY - reach,
                r + shadow.OffsetX + reach, b + shadow.OffsetY + reach);
    }

    /// <summary>
    /// Draws the shadow for one object, or null when there is nothing to draw.
    ///
    /// <paramref name="pageWidthPts"/> converts the model's normalized lengths
    /// into points, which is what the sampling rate is expressed in.
    /// </summary>
    public static ShadowRaster? Rasterize(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow, double pageWidthPts)
    {
        if (group.Count == 0 || shadow.Softness <= 0 || shadow.Color.A == 0
            || pageWidthPts <= 0)
        {
            return null;
        }

        var box = BoundsOf(group, shadow);
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

            float dx = (float)(shadow.OffsetX * scale);
            float dy = (float)(shadow.OffsetY * scale);
            float sigma = (float)OverlayProjection.BlurSigmaOf(shadow, scale, view);
            var color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A);

            using var filter = SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color);
            using var paint = new SKPaint { ImageFilter = filter };

            canvas.SaveLayer(paint);
            ShapeSkiaPainter.PaintViewport(
                canvas,
                group.Select(i => i with { Effects = null }).ToList(),
                scale, _ => 0, _ => view, new ViewportProjection(1, 1, 0, 0));
            canvas.Restore();
        }

        var bytes = new byte[bitmap.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(
            bitmap.GetPixels(), bytes, 0, bytes.Length);
        bitmap.Dispose();

        return new ShadowRaster(bytes, pxW, pxH, box.L, box.T, box.R, box.B);
    }
}
