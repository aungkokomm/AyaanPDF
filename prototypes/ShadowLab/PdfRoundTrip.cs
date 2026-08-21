using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// CAN THE SAVED FILE LOOK LIKE THE PREVIEW?
///
/// render_core cannot blur: it draws paths, and PDF has no blur operator for
/// one. But it does not need to. Skia can rasterise the shadow, and render_core
/// already embeds a BGRA bitmap into an annotation, with a matrix and with
/// alpha, because that is how a stamp works.
///
/// So this takes the SAME SKImageFilter shadow the preview would draw, renders
/// it to a bitmap with the object knocked back out of it, puts it in the page
/// through the existing stamp path, and renders the page back through PDFium.
/// Nothing in the app is touched; every call is one that already exists.
/// </summary>
public static class PdfRoundTrip
{
    [DllImport("render_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_stamp_annotation(
        ulong docHandle, int pageIndex, int captureWidth,
        float left, float top, float right, float bottom,
        byte[] bgra, nuint byteLen, int pixelWidth, int pixelHeight);

    /// <summary>
    /// The shadow alone, as pixels: the object drawn into a layer through
    /// CreateDropShadowOnly, so what comes out is the blurred silhouette and
    /// nothing else. This is the bitmap that would go into the file.
    /// </summary>
    public static SKBitmap RasterizeShadow(
        IReadOnlyList<ShapeRenderItem> group, DropShadow shadow, int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // The tile covers the whole page box the scenes are laid out in.
        canvas.Scale(size / (float)Casters.Scale);

        float dx = (float)(shadow.OffsetX * Casters.Scale);
        float dy = (float)(shadow.OffsetY * Casters.Scale);
        float sigma = (float)OverlayProjection.BlurSigmaOf(
            shadow, Casters.Scale, PageTransform.For(Casters.Scale, Casters.ContentH, 0, Casters.Scale));

        var color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A);

        using var filter = SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color);
        using var paint = new SKPaint { ImageFilter = filter };

        canvas.SaveLayer(paint);
        Casters.Paint(canvas, group.Select(i => i with { Effects = null }).ToList(), Approach.None);
        canvas.Restore();

        return bitmap;
    }

    /// <summary>Puts that bitmap in a real PDF and renders the page back.</summary>
    public static SKBitmap ThroughThePdf(
        string fixture, IReadOnlyList<ShapeRenderItem> group, DropShadow shadow,
        NativeShapeSpec shape, int renderWidth)
    {
        ulong doc = Native.open_document(fixture);
        if (doc == 0)
        {
            throw new InvalidOperationException($"could not open {fixture}");
        }

        try
        {
            // The shadow goes down FIRST, so the shape lands on top of it.
            using (var shadowBitmap = RasterizeShadow(group, shadow, 1000))
            {
                byte[] bytes = new byte[shadowBitmap.ByteCount];
                Marshal.Copy(shadowBitmap.GetPixels(), bytes, 0, bytes.Length);

                // The stamp covers the whole capture box, so the bitmap's own
                // pixels put the shadow where it belongs; no second offset.
                int status = add_stamp_annotation(
                    doc, 0, 1000, 0, 0, 1000, 1000,
                    bytes, (nuint)bytes.Length, shadowBitmap.Width, shadowBitmap.Height);

                if (status != 0 && status != 1)
                {
                    throw new InvalidOperationException($"add_stamp_annotation returned {status}");
                }
            }

            // The shape itself, with NO shadow of its own: the picture is what
            // the file carries now.
            var bare = shape;
            bare.ShadowRgba = 0;
            Native.add_shape_annotations(doc, 1000, [bare], 1);

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
}
