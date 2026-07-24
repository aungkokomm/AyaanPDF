using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Interop;

/// <summary>Marshals render_core::get_page_chars into a plain <see cref="PageTextLayer"/>, always freeing the native array.</summary>
internal static class TextLayerLoader
{
    public static PageTextLayer? Load(ulong docHandle, int pageIndex, int targetWidth)
    {
        CharInfoArray array = RenderCoreNative.get_page_chars(docHandle, pageIndex, targetWidth);
        try
        {
            if (array.Status != RenderStatus.OkPdfium || array.Chars == System.IntPtr.Zero)
            {
                return null;
            }

            int count = (int)array.Len;
            int structSize = Marshal.SizeOf<NativeCharInfo>();
            var glyphs = new List<CharGlyph>(count);

            for (int i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<NativeCharInfo>(array.Chars + i * structSize);
                // Codepoints outside the Basic Multilingual Plane don't fit a
                // single UTF-16 `char` — replace rather than silently
                // truncate into an unrelated BMP codepoint.
                char ch = native.Codepoint <= 0xFFFF ? (char)native.Codepoint : '�';
                glyphs.Add(new CharGlyph(native.Left, native.Top, native.Right, native.Bottom, ch));
            }

            return new PageTextLayer(glyphs);
        }
        finally
        {
            RenderCoreNative.free_char_info_array(array);
        }
    }
}
