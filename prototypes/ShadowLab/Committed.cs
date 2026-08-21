using System.Runtime.InteropServices;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// The SHIPPED path, end to end: the production ShadowRasterizer draws the
/// shadow, the production FFI puts it in a real PDF, PDFium renders it back.
///
/// Nothing here imitates the app. It calls the same two things the app calls,
/// in the same order, so what comes out is what a person sees.
/// </summary>
public static class Committed
{
    [DllImport("render_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_shape_shadow_image(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom,
        byte[]? bgra, nuint byteLen, int pixelWidth, int pixelHeight, out int outNewIndex);

    private const int Capture = 1000;

    /// <summary>A page with one shadowed rectangle on it, as the app makes one.</summary>
    public static SKBitmap Render(
        string fixture, double pageWidthPts, DropShadow shadow, bool filled, int renderWidth)
    {
        var draft = new ShapeDraft(ShapeKind.Rectangle, 0.18, 0.24, 0.72, 0.60);

        // Through the app's own builder, so the fill reaches what casts the
        // shadow exactly as it does in the app.
        var tag = new ShapeTag(
            ShapeKind.Rectangle, "#FF1F2937", 0.006 * pageWidthPts, false, false, 0,
            filled ? "#FF00B4CC" : null, 0);

        var items = ShadowRasterizer.CasterItemsFor(
            tag, draft.Left, draft.Top, draft.Right, draft.Bottom, pageWidthPts);

        ulong doc = Native.open_document(fixture);
        if (doc == 0)
        {
            throw new InvalidOperationException($"could not open {fixture}");
        }

        try
        {
            var spec = new NativeShapeSpec
            {
                PageIndex = 0,
                Kind = 0,
                X1 = (float)(draft.X1 * Capture),
                Y1 = (float)(draft.Y1 * Capture),
                X2 = (float)(draft.X2 * Capture),
                Y2 = (float)(draft.Y2 * Capture),
                R = 0x1F, G = 0x29, B = 0x37, A = 0xFF,
                WidthPx = (float)(0.006 * Capture),
                FillRgba = filled ? 0xFF00B4CCu : 0u,
                ShadowAngleDeg = (float)shadow.AngleDeg,
                ShadowDistancePx = (float)(shadow.Distance * Capture),
                ShadowSoftnessPx = (float)(shadow.Softness * Capture),
                ShadowRgba =
                    ((uint)shadow.Color.A << 24) | ((uint)shadow.Color.R << 16)
                    | ((uint)shadow.Color.G << 8) | shadow.Color.B,
            };

            Native.add_shape_annotations(doc, Capture, [spec], 1);

            // Exactly what ViewportViewModel.SyncShadowImages does.
            var raster = ShadowRasterizer.Rasterize(items, shadow, pageWidthPts);
            if (raster is { } r)
            {
                int status = set_shape_shadow_image(
                    doc, 0, 0, Capture,
                    (float)(r.Left * Capture), (float)(r.Top * Capture),
                    (float)(r.Right * Capture), (float)(r.Bottom * Capture),
                    r.Bgra, (nuint)r.Bgra.Length, r.PixelWidth, r.PixelHeight, out _);

                if (status != 0 && status != 1)
                {
                    throw new InvalidOperationException($"set_shape_shadow_image -> {status}");
                }
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
}
