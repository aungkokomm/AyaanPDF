using System;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The re-edit cycle across the real FFI boundary: place a text box, read its
/// words back out, then delete and replace it. This is exactly what
/// double-clicking a text box does, and none of the pure tests exercise the
/// native round trip the feature depends on.
/// </summary>
public class TextBoxInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int InvalidInput = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationInfo
    {
        public int Index;
        public int Subtype;
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
        public int Color;
        public float Opacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationArray
    {
        public IntPtr Items;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_text_box_annotation(
        ulong docHandle, int pageIndex, int captureWidth,
        float left, float top, float right, float bottom,
        [In] byte[] text, nuint textLen,
        float fontSizePx, byte r, byte g, byte b, byte a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static int AddText(ulong handle, string text, float sizePx = 20f)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return add_text_box_annotation(
            handle, 0, 1000, 100, 100, 500, 200, utf8, (nuint)utf8.Length, sizePx, 0x11, 0x22, 0x33, 0xFF);
    }

    private static string? ReadContents(ulong handle, int index)
    {
        var buf = get_annotation_contents(handle, 0, index);
        try
        {
            if (buf.Status != OkPdfium)
            {
                return null;
            }

            byte[] bytes = new byte[(int)buf.Len];
            if (buf.Len > 0)
            {
                Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
            }

            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            free_byte_buffer(buf);
        }
    }

    private static int Count(ulong handle)
    {
        var array = get_annotations(handle, 0);
        try
        {
            return (int)array.Len;
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    [Fact]
    public void the_words_read_back_are_the_words_that_were_written()
    {
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, AddText(handle, "meeting at 10:30\nroom 4B", 24f));

            // Through the same parser the app uses, so this proves the whole
            // read-back the double-click editor relies on.
            Assert.True(TextBoxTagReader.TryParse(ReadContents(handle, 0), out var tag));
            Assert.Equal("meeting at 10:30\nroom 4B", tag.Text);
            Assert.Equal(24.0 / 1000.0, tag.FontSizeNorm, 6);
            Assert.Equal("#FF112233", tag.ColorHex);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void replacing_a_text_box_leaves_exactly_one_with_the_new_words()
    {
        // Delete + re-add is what ReplaceTextBox does. The failure this guards
        // against is a leftover duplicate, or the new box carrying the old text.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, AddText(handle, "before"));
            Assert.Equal(1, Count(handle));

            Assert.Equal(OkPdfium, delete_annotation(handle, 0, 0));
            Assert.Equal(0, Count(handle));

            Assert.Equal(OkPdfium, AddText(handle, "after"));
            Assert.Equal(1, Count(handle));

            Assert.True(TextBoxTagReader.TryParse(ReadContents(handle, 0), out var tag));
            Assert.Equal("after", tag.Text);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void reading_contents_of_a_missing_annotation_is_refused_not_a_crash()
    {
        ulong handle = OpenFixture();
        try
        {
            var buf = get_annotation_contents(handle, 0, 3);
            Assert.Equal(InvalidInput, buf.Status);
            free_byte_buffer(buf);
        }
        finally
        {
            close_document(handle);
        }
    }
}
