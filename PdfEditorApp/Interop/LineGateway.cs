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
    /// <summary>
    /// Every visual line on the page, or nothing when it has none.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE ANSWER IS NOT ALWAYS FINAL, AND THE CALLER MUST NOT CACHE ONE
    /// THAT IS NOT. A page of shaped text is read on a background thread, and
    /// until that finishes this reports what PDFium made of it, which on a
    /// Word-produced Burmese page is a hundred and fifty fragments of scrambled
    /// text. Caching that would mean the reading finished and nobody ever
    /// looked at it.
    /// </remarks>
    public static IReadOnlyList<LineSnapshot> Load(
        ulong docHandle, int pageIndex, out bool settled)
    {
        settled = true;
        var found = Read(docHandle, pageIndex, ref settled);
        return found;
    }

    /// <summary>The same, for callers that do not keep the result.</summary>
    public static IReadOnlyList<LineSnapshot> Load(ulong docHandle, int pageIndex) =>
        Load(docHandle, pageIndex, out _);

    private static IReadOnlyList<LineSnapshot> Read(
        ulong docHandle, int pageIndex, ref bool settled)
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

            var lines = LineReader.Parse(bytes);

            // ⚠️ THE ONE PLACE THAT KNOWS IN TIME. A page whose script cannot be
            // read through the file's own tables says so right here, and the
            // work of reading it from the FONT instead takes about seventeen
            // seconds. Started now, it is finished long before a reader has
            // clicked on anything. Started when they click, they wait for it.
            if (!LineReader.NeedsReshaping(lines))
            {
                return lines;
            }

            RenderCoreNative.prepare_recovery(docHandle, pageIndex);

            // ⚠️ AND NEVER WAITED FOR. This runs on the UI thread every time a
            // page is looked at, and asking for the text before it is ready
            // blocks for the whole seventeen seconds, which is precisely the
            // freeze prepare_recovery exists to prevent. Until it is ready the
            // page keeps the lines PDFium gave it, refusals and all, and the
            // caller asks again: see the settled flag.
            if (RenderCoreNative.recovery_is_ready(docHandle, pageIndex) == 0)
            {
                settled = false;
                return lines;
            }
            return RecoveredLines.Merge(lines, RecoveryGateway.Load(docHandle, pageIndex));
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Writes into a recorded range of objects, addressed by position.
    ///
    /// No font is resolved here, unlike <see cref="Write"/>: this path exists
    /// so that a deletion can be undone, and the stand-in font would shift the
    /// indices it is addressing by.
    /// </summary>
    public static int WriteAtAnchor(
        ulong docHandle, int pageIndex, int firstObject, int lastObject, int prefixChars,
        string expected, string newText)
    {
        byte[] want = Encoding.UTF8.GetBytes(expected);
        byte[] text = Encoding.UTF8.GetBytes(newText);

        return RenderCoreNative.set_object_range_text(
            docHandle, pageIndex,
            (uint)firstObject, (uint)lastObject, (uint)prefixChars,
            want, (nuint)want.Length, text, (nuint)text.Length);
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
        string fontName, string newText, string? expected = null)
    {
        byte[] text = Encoding.UTF8.GetBytes(newText);

        string? fontPath = SystemFontMatch.PathFor(fontName);
        byte[]? font = fontPath is null ? null : Encoding.UTF8.GetBytes(fontPath);

        int status = RenderCoreNative.set_line_text(
            docHandle, pageIndex,
            (uint)firstObject, (uint)lastObject, (uint)prefixChars,
            text, (nuint)text.Length,
            font, (nuint)(font?.Length ?? 0));

        if (status == RenderStatus.OkPdfium || expected is null)
        {
            return status;
        }

        // The object-replacing writer would not take this line. Ask the block
        // writer, which splices the line where it stands instead of rebuilding
        // it, and takes the multi-piece lines the first one cannot.
        //
        // ⚠️ THE FIRST REFUSAL IS THE ONE REPORTED. When both decline, the
        // caller gets exactly the status it would have got before this fallback
        // existed, so every message it already shows still means what it meant.
        int spliced = WriteAsBlock(docHandle, pageIndex, firstObject, lastObject, expected, newText);
        return spliced == RenderStatus.OkPdfium ? spliced : status;
    }

    /// <summary>
    /// Retypes one line by splicing it, leaving the producer's own pieces where
    /// they are. See <see cref="RenderCoreNative.set_block_line_text"/>.
    /// </summary>
    public static int WriteAsBlock(
        ulong docHandle, int pageIndex, int firstObject, int lastObject,
        string expected, string newText)
    {
        byte[] want = Encoding.UTF8.GetBytes(expected);
        byte[] text = Encoding.UTF8.GetBytes(newText);

        return RenderCoreNative.set_block_line_text(
            docHandle, pageIndex,
            (uint)firstObject, (uint)lastObject,
            want, (nuint)want.Length, text, (nuint)text.Length);
    }
}
