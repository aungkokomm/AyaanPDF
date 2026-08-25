using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the three link calls, always freeing the native buffer.
///
/// Marshalling only. The decode lives in <see cref="LinkReader"/>, where a test
/// assembly can reach it, exactly as <see cref="WordClusterGateway"/> splits
/// from <see cref="WordClusterReader"/>.
/// </summary>
internal static class LinkGateway
{
    /// <summary>Every link on the page, or nothing when it has none.</summary>
    public static IReadOnlyList<LinkSnapshot> Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.get_page_links(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return Array.Empty<LinkSnapshot>();
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return LinkReader.Parse(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Adds a URI link over a rectangle in capture coordinates.
    ///
    /// <paramref name="index"/> comes back as the annotation index the link
    /// landed at, or -1. Undo needs it, and so does every later edit.
    /// </summary>
    public static int Add(
        ulong docHandle, int pageIndex, int captureWidth,
        double left, double top, double right, double bottom,
        string uri, out int index)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(uri);

        return RenderCoreNative.add_uri_link(
            docHandle, pageIndex, captureWidth,
            (float)left, (float)top, (float)right, (float)bottom,
            bytes, (nuint)bytes.Length, out index);
    }

    /// <summary>Re-points an existing URI link. Refuses anything else.</summary>
    public static int SetUri(ulong docHandle, int pageIndex, int index, string uri)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(uri);

        return RenderCoreNative.set_uri_link(
            docHandle, pageIndex, index, bytes, (nuint)bytes.Length);
    }
}
