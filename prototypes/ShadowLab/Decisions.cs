using System.Diagnostics;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// The two open decisions, measured rather than argued: how finely the
/// committed shadow has to be rasterised, and what box the bitmap should
/// cover. Plus what has to happen to it when the shape moves, turns, resizes,
/// or its shadow changes.
/// </summary>
public static class Decisions
{
    [DllImport("render_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_stamp_annotation(
        ulong docHandle, int pageIndex, int captureWidth,
        float left, float top, float right, float bottom,
        byte[] bgra, nuint byteLen, int pixelWidth, int pixelHeight);

    private const int Capture = 1000;
    private static readonly PageTransform View =
        PageTransform.For(Casters.Scale, Casters.ContentH, 0, Casters.Scale);

    // ---------------- the shadow's own box, in normalized page units ----------------

    /// <summary>
    /// Where the shadow's ink can land: the object's own box, moved by the
    /// shadow's offset, opened out by half the stroke and by the blur's reach.
    ///
    /// The SHADOW's box, not the object's. The object is drawn as paths by
    /// render_core and does not belong in the bitmap at all.
    /// </summary>
    public static (double L, double T, double R, double B) ShadowBounds(
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

        // Normalized, so the same numbers OverlayProjection works in, divided
        // back out of slot DIPs. Reach is three sigma and sigma is half the
        // radius, which makes the reach one and a half times the softness.
        double reach = pad + (shadow.Softness * 1.5);

        return (l + shadow.OffsetX - reach, t + shadow.OffsetY - reach,
                r + shadow.OffsetX + reach, b + shadow.OffsetY + reach);
    }

    /// <summary>
    /// The shadow alone, rasterised over an arbitrary box at an arbitrary size.
    ///
    /// The box is what the bitmap covers; the pixel size is how finely. Those
    /// are the two decisions, so they are the two parameters.
    /// </summary>
    public static SKBitmap Rasterize(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow,
        (double L, double T, double R, double B) box, int pxW, int pxH,
        double rotateDeg = 0)
    {
        var bitmap = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Map the box onto the bitmap: the bitmap IS the box.
        canvas.Scale((float)(pxW / ((box.R - box.L) * Casters.Scale)),
                     (float)(pxH / ((box.B - box.T) * Casters.Scale)));
        canvas.Translate((float)(-box.L * Casters.Scale), (float)(-box.T * Casters.Scale));

        if (rotateDeg != 0)
        {
            var (cx, cy) = Centre(group);
            canvas.RotateDegrees(
                (float)rotateDeg, (float)(cx * Casters.Scale), (float)(cy * Casters.Scale));
        }

        float dx = (float)(shadow.OffsetX * Casters.Scale);
        float dy = (float)(shadow.OffsetY * Casters.Scale);
        float sigma = (float)OverlayProjection.BlurSigmaOf(shadow, Casters.Scale, View);
        var color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A);

        using var filter = SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color);
        using var paint = new SKPaint { ImageFilter = filter };

        canvas.SaveLayer(paint);
        Casters.Paint(canvas, Strip(group), Approach.None);
        canvas.Restore();

        return bitmap;
    }

    private static IReadOnlyList<ShapeRenderItem> Strip(IReadOnlyList<ShapeRenderItem> g) =>
        g.Select(i => i with { Effects = null }).ToList();

    private static (double X, double Y) Centre(IReadOnlyList<ShapeRenderItem> group)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var item in group)
        {
            foreach (var (x, y) in item.Points)
            {
                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        return ((l + r) / 2, (t + b) / 2);
    }

    /// <summary>That bitmap, in a real PDF, rendered back at a chosen width.</summary>
    public static SKBitmap ThroughPdf(
        string fixture, SKBitmap shadowBitmap,
        (double L, double T, double R, double B) box, int renderWidth)
    {
        ulong doc = Native.open_document(fixture);
        try
        {
            byte[] bytes = new byte[shadowBitmap.ByteCount];
            Marshal.Copy(shadowBitmap.GetPixels(), bytes, 0, bytes.Length);

            int status = add_stamp_annotation(
                doc, 0, Capture,
                (float)(box.L * Capture), (float)(box.T * Capture),
                (float)(box.R * Capture), (float)(box.B * Capture),
                bytes, (nuint)bytes.Length, shadowBitmap.Width, shadowBitmap.Height);

            if (status != 0 && status != 1)
            {
                throw new InvalidOperationException($"add_stamp_annotation returned {status}");
            }

            var result = Native.render_uncached(doc, 0, renderWidth);
            try
            {
                var page = new SKBitmap(
                    result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)result.Buffer, (void*)page.GetPixels(),
                        (long)result.Len, (long)result.Len);
                }

                return page;
            }
            finally
            {
                Native.free_render_result(result);
            }
        }
        finally
        {
            Native.close_document(doc);
        }
    }

    // ---------------- comparing ----------------

    /// <summary>Mean and worst per-channel difference, and how many pixels are
    /// off by more than one level.</summary>
    public static (double Mean, int Max, int Off) Compare(SKBitmap a, SKBitmap b)
    {
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        long total = 0;
        int max = 0, off = 0, n = 0;

        for (int i = 0; i + 3 < pa.Length && i + 3 < pb.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 3; c++)
            {
                d = Math.Max(d, Math.Abs(pa[i + c] - pb[i + c]));
            }

            total += d;
            max = Math.Max(max, d);
            if (d > 1) { off++; }
            n++;
        }

        return (n == 0 ? 0 : (double)total / n, max, off);
    }
}
