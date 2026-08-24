using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals render_core::get_page_text_objects, always freeing the native
/// buffer.
///
/// Marshalling only. The decode lives in <see cref="PageTextObjectReader"/>,
/// where a test assembly can reach it, exactly as
/// <see cref="StyledRunLoader"/> splits from <see cref="StyledRunReader"/>.
/// </summary>
internal static class PageTextObjectLoader
{
    public static IReadOnlyList<PageTextSnapshot> Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.get_page_text_objects(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return Array.Empty<PageTextSnapshot>();
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return PageTextObjectReader.Parse(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }
}
