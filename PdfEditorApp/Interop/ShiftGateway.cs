using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the call that moves the document's own text, always freeing the
/// native buffer.
/// </summary>
internal static class ShiftGateway
{
    /// <summary>
    /// Moves every named line by the same displacement, returning the new
    /// document or null when the core refused.
    /// </summary>
    /// <remarks>
    /// Everything here is normalized the way the app draws: top-left origin,
    /// both axes over the page WIDTH, <paramref name="dy"/> positive downwards.
    /// See <see cref="RenderCoreNative.shift_page_text"/>.
    ///
    /// ⚠️ THE APP SAYS WHICH LINES, AND THAT IS THE POINT. The core used to be
    /// handed one baseline and a "whole block" flag and work the rest of the
    /// paragraph out itself, from the leading and the left edge, while the app
    /// drew its box from its own page segmentation. Two answers to the same
    /// question, agreeing often enough to look right. Now the box the reader
    /// can see is the list that gets sent.
    /// </remarks>
    public static byte[]? Move(
        ulong docHandle, int pageIndex, IReadOnlyList<double> baselines,
        double dx, double dy)
    {
        if (baselines.Count == 0) { return null; }

        var asked = new float[baselines.Count];
        for (int i = 0; i < baselines.Count; i++) { asked[i] = (float)baselines[i]; }

        var buffer = RenderCoreNative.shift_page_text(
            docHandle, pageIndex, asked, asked.Length, (float)dx, (float)dy);
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
