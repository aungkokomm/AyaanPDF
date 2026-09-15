using System;
using System.Runtime.InteropServices;
using PdfEditorApp.Interop;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Ocr;

/// <summary>Renders a page the way the recognisers want to see it.</summary>
internal static class OcrPageImage
{
    /// <summary>300 DPI. On this project's benchmark 200 DPI was 40% faster but
    /// damaged low-resolution scans (a Grade 1 textbook's text changed a lot).</summary>
    public const int Dpi = 300;

    /// <summary>The largest side Windows OCR accepts, less one.</summary>
    public const double MaxSide = 9999;

    public static GrayImage? Render(ulong docHandle, int pageIndex, double widthPoints, double heightPoints)
    {
        if (docHandle == 0 || widthPoints <= 0 || heightPoints <= 0)
        {
            return null;
        }

        double scale = Dpi / 72.0;
        double fit = Math.Min(1.0, MaxSide / Math.Max(widthPoints * scale, heightPoints * scale));
        int width = Math.Max(1, (int)(widthPoints * scale * fit));

        RenderResult result = RenderCoreNative.render_uncached(docHandle, pageIndex, width);
        try
        {
            if (result.Buffer == IntPtr.Zero || (long)result.Len != (long)result.Width * result.Height * 4)
            {
                return null;
            }

            var bgra = new byte[(int)result.Len];
            Marshal.Copy(result.Buffer, bgra, 0, bgra.Length);
            return GrayImage.FromBgra(bgra, result.Width, result.Height);
        }
        finally
        {
            RenderCoreNative.free_render_result(result);
        }
    }
}
