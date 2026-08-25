using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the two line calls, always freeing the native buffer.
///
/// Marshalling only. The decode lives in <see cref="LineReader"/>, where a test
/// assembly can reach it, exactly as <see cref="WordClusterGateway"/> splits
/// from <see cref="WordClusterReader"/>.
/// </summary>
internal static class LineGateway
{
    /// <summary>Every visual line on the page, or nothing when it has none.</summary>
    public static IReadOnlyList<LineSnapshot> Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.get_page_lines(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return Array.Empty<LineSnapshot>();
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return LineReader.Parse(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Retypes one line.
    ///
    /// The stand-in font is resolved HERE rather than in the core, because
    /// choosing a font is the app's business: the same matching the font picker
    /// already does. Passing null when nothing matches is what makes the core
    /// refuse instead of substituting something that merely looks close.
    /// </summary>
    public static int Write(
        ulong docHandle, int pageIndex, int firstObject, int lastObject, int prefixChars,
        string fontName, string newText)
    {
        byte[] text = Encoding.UTF8.GetBytes(newText);

        string? fontPath = SystemFontMatch.PathFor(fontName);
        byte[]? font = fontPath is null ? null : Encoding.UTF8.GetBytes(fontPath);

        return RenderCoreNative.set_line_text(
            docHandle, pageIndex,
            (uint)firstObject, (uint)lastObject, (uint)prefixChars,
            text, (nuint)text.Length,
            font, (nuint)(font?.Length ?? 0));
    }
}
