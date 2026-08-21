using System.Runtime.InteropServices;
using SkiaSharp;

namespace ShadowLab;

/// <summary>
/// Just enough of the render_core FFI to draw a shadowed shape the way the
/// SAVED PAGE draws one, so the preview can be compared against the thing a
/// person actually looks at. Copied from PdfEditorApp.Interop rather than
/// referenced, because the prototype must not touch the app.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderResult
{
    public int Width;
    public int Height;
    public IntPtr Buffer;
    public nuint Len;
    public int Status;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeShapeSpec
{
    public int PageIndex;
    public int Kind;
    public float X1;
    public float Y1;
    public float X2;
    public float Y2;
    public byte R;
    public byte G;
    public byte B;
    public byte A;
    public float WidthPx;
    public float RotationDeg;
    public uint FillRgba;
    public float CornerRadiusPx;
    public float ShadowAngleDeg;
    public float ShadowDistancePx;
    public float ShadowSoftnessPx;
    public float ShadowSpreadPx;
    public uint ShadowRgba;
}

public static class Native
{
    private const string Lib = "render_core.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] NativeShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_uncached(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_render_result(RenderResult result);

    /// <summary>The page as the app shows it, once the shape is committed.</summary>
    public static SKBitmap RenderPage(string fixture, NativeShapeSpec[] shapes, int captureWidth, int width)
    {
        ulong doc = open_document(fixture);
        if (doc == 0)
        {
            throw new InvalidOperationException($"could not open {fixture}");
        }

        try
        {
            if (shapes.Length > 0)
            {
                int status = add_shape_annotations(doc, captureWidth, shapes, (nuint)shapes.Length);
                if (status != 0 && status != 1)
                {
                    throw new InvalidOperationException($"add_shape_annotations returned {status}");
                }
            }

            var result = render_uncached(doc, 0, width);
            try
            {
                var bitmap = new SKBitmap(
                    result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)result.Buffer, (void*)bitmap.GetPixels(),
                        (long)result.Len, (long)result.Len);
                }

                return bitmap;
            }
            finally
            {
                free_render_result(result);
            }
        }
        finally
        {
            close_document(doc);
        }
    }
}
