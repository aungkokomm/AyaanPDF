using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>
/// Marshals the two word-cluster calls, always freeing the native buffer.
///
/// Marshalling only. The decode lives in <see cref="WordClusterReader"/>, where
/// a test assembly can reach it, exactly as <see cref="PageTextObjectLoader"/>
/// splits from <see cref="PageTextObjectReader"/>.
/// </summary>
internal static class WordClusterGateway
{
    /// <summary>Every word on the page, or nothing when it has none.</summary>
    public static IReadOnlyList<WordClusterSnapshot> Load(ulong docHandle, int pageIndex)
    {
        var buffer = RenderCoreNative.get_page_word_clusters(docHandle, pageIndex);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium
                || buffer.Data == IntPtr.Zero
                || buffer.Len == 0)
            {
                return Array.Empty<WordClusterSnapshot>();
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return WordClusterReader.Parse(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// Rewrites one word.
    ///
    /// The stand-in font is resolved HERE rather than in the core, because
    /// choosing a font is the app's business: the same matching the font picker
    /// already does. Passing null when nothing matches is what makes the core
    /// refuse instead of substituting something that merely looks close.
    /// </summary>
    public static int Write(
        ulong docHandle, int pageIndex, WordClusterSnapshot cluster, string newText)
    {
        uint[] objects = new uint[cluster.ObjectIndices.Count];
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i] = (uint)cluster.ObjectIndices[i];
        }

        byte[] text = Encoding.UTF8.GetBytes(newText);

        string? fontPath = SystemFontMatch.PathFor(cluster.FontName);
        byte[]? font = fontPath is null ? null : Encoding.UTF8.GetBytes(fontPath);

        return RenderCoreNative.set_word_cluster_text(
            docHandle, pageIndex,
            objects, (nuint)objects.Length,
            (uint)cluster.PrefixChars,
            text, (nuint)text.Length,
            font, (nuint)(font?.Length ?? 0));
    }
}
