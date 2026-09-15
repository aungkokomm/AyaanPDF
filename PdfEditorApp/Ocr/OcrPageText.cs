using System;
using System.Runtime.InteropServices;
using PdfEditorApp.Interop;

namespace PdfEditorApp.Ocr;

/// <summary>Whether a page already carries text, for "Skip pages that already have text".</summary>
internal static class OcrPageText
{
    /// <summary>
    /// True when the page has any character that is not white space. A layer
    /// Recognize text wrote counts too, so a page read once is skipped the next
    /// time unless the reader asks for it to be read again.
    /// </summary>
    public static bool HasText(ulong docHandle, int pageIndex)
    {
        CharInfoArray array = RenderCoreNative.get_page_chars(docHandle, pageIndex, 100);
        try
        {
            if (array.Status != RenderStatus.OkPdfium || array.Chars == IntPtr.Zero)
            {
                return false;
            }

            int size = Marshal.SizeOf<NativeCharInfo>();
            for (int i = 0; i < (int)array.Len; i++)
            {
                // Above the space: an emptied text object reads as U+0000, which is not text.
                if (Marshal.PtrToStructure<NativeCharInfo>(array.Chars + i * size).Codepoint > ' ')
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            RenderCoreNative.free_char_info_array(array);
        }
    }
}
