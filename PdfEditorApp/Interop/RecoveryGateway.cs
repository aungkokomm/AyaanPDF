using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the two recovery calls, always freeing the native buffer.
///
/// Marshalling only. The decode lives in <see cref="RecoveredLineReader"/>,
/// where a test assembly can reach it, exactly as <see cref="LineGateway"/>
/// splits from <see cref="LineReader"/>.
/// </summary>
internal static class RecoveryGateway
{
    /// <summary>
    /// What the core managed to read of a page whose script the file's own
    /// tables cannot spell out, or nothing when it has not finished.
    /// </summary>
    public static IReadOnlyList<RecoveredLine> Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.recover_page_text(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return Array.Empty<RecoveredLine>();
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return RecoveredLineReader.Parse(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Retypes one recovered line, returning the WHOLE new document, or null
    /// when the core refused.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE DOCUMENT, NOT AN EDIT IN PLACE, and the caller has to adopt it.
    /// This writer works on the file's own structure through lopdf rather than
    /// through PDFium, because what it has to do is empty every placement the
    /// producer drew the line with and put one run where they were, and PDFium
    /// has no way to say that.
    ///
    /// ⚠️ <paramref name="expected"/> MUST BE EXACTLY WHAT WAS READ. The core
    /// re-derives the page's lines and refuses if none of them still says this,
    /// which is what stops a stale selection overwriting whatever is there now.
    /// </remarks>
    public static byte[]? Retype(
        ulong docHandle, int pageIndex, double pdfBaseline,
        string expected, string newText, string fontPath)
    {
        byte[] want = Encoding.UTF8.GetBytes(expected);
        byte[] text = Encoding.UTF8.GetBytes(newText);
        byte[] font = Encoding.UTF8.GetBytes(fontPath);

        var buffer = RenderCoreNative.retype_recovered_line(
            docHandle, pageIndex, (float)pdfBaseline,
            want, (nuint)want.Length,
            text, (nuint)text.Length,
            font, (nuint)font.Length);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return null;
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }
}
