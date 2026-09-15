using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Interop;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Ocr;

/// <summary>Hands a page's recognised words to the core, which writes them as the page's OCR layer.</summary>
internal static class OcrLayerWriter
{
    /// <summary>
    /// Replaces the page's OCR layer with these words. Returns how many were
    /// written, or a negative status from the core.
    /// </summary>
    public static int Write(ulong docHandle, int pageIndex, IReadOnlyList<OcrWord> words)
    {
        string? font = OcrAssets.FontFor(string.Concat(words.Select(w => w.Text)));

        // One buffer holds every word's UTF-8, pinned for the length of the call.
        var encoded = words.Select(w => Encoding.UTF8.GetBytes(w.Text)).ToArray();
        var buffer = new byte[Math.Max(1, encoded.Sum(e => e.Length))];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr start = pin.AddrOfPinnedObject();
            var native = new OcrWordNative[words.Count];
            int offset = 0;
            for (int i = 0; i < words.Count; i++)
            {
                Buffer.BlockCopy(encoded[i], 0, buffer, offset, encoded[i].Length);
                native[i] = new OcrWordNative
                {
                    Left = (float)words[i].Left,
                    Top = (float)words[i].Top,
                    Right = (float)words[i].Right,
                    Bottom = (float)words[i].Bottom,
                    Text = start + offset,
                    TextLen = (nuint)encoded[i].Length,
                };
                offset += encoded[i].Length;
            }

            return RenderCoreNative.add_ocr_words(docHandle, pageIndex, native, (nuint)native.Length, font);
        }
        finally
        {
            pin.Free();
        }
    }
}
