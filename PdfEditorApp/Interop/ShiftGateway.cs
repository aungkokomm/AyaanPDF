using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the two calls that move the document's own text, always freeing the
/// native buffer.
/// </summary>
internal static class ShiftGateway
{
    /// <summary>
    /// Moves a line, or its whole paragraph, returning the new document or null
    /// when the core refused.
    /// </summary>
    /// <remarks>
    /// Everything here is normalized the way the app draws: top-left origin,
    /// both axes over the page WIDTH, <paramref name="dy"/> positive downwards.
    /// See <see cref="RenderCoreNative.shift_page_text"/>.
    /// </remarks>
    public static byte[]? Move(
        ulong docHandle, int pageIndex, double baseline, bool wholeBlock,
        double dx, double dy)
    {
        var buffer = RenderCoreNative.shift_page_text(
            docHandle, pageIndex, (float)baseline, wholeBlock ? 1 : 0,
            (float)dx, (float)dy);
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

    /// <summary>
    /// The baselines of every line that would move with this one, or just it
    /// when the core cannot say.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE LINE ITSELF IS THE HONEST FALLBACK. A page the core will not
    /// group is a page whose paragraph nobody knows, and framing the one line
    /// the reader actually picked is truthful where framing nothing would look
    /// like the drag had failed.
    /// </remarks>
    public static IReadOnlyList<double> BlockBaselines(
        ulong docHandle, int pageIndex, double baseline)
    {
        var buffer = RenderCoreNative.text_block_baselines(
            docHandle, pageIndex, (float)baseline);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len < 4)
            {
                return new[] { baseline };
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            uint count = BitConverter.ToUInt32(bytes, 0);
            if (count == 0 || 4 + (count * 4) > bytes.Length)
            {
                return new[] { baseline };
            }

            var found = new List<double>((int)count);
            for (uint i = 0; i < count; i++)
            {
                found.Add(BitConverter.ToSingle(bytes, 4 + ((int)i * 4)));
            }
            return found;
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }
}
