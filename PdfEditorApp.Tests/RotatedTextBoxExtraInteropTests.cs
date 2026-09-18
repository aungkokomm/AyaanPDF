using System;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A TURNED text box moved as a secondary member of a multi-selection or group,
/// through the real writer.
/// </summary>
/// <remarks>
/// The bug: a multi-selection's extras carry the rectangle each mark REPORTS,
/// and a turned text box reports the larger box around its rotated text. That
/// rectangle was handed to the text box's re-layout as if it were the box, so
/// the box grew by the same factor every time the group moved. The anchor was
/// never affected, because its rectangle is taken from the tag when selected.
/// The fix carries the box's own upright size (from its tag) to wherever the
/// reported rectangle was moved, with <see cref="TurnedBox.Recentre"/>.
/// </remarks>
public class RotatedTextBoxExtraInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

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
    private static extern int rotate_text_box_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, float degrees, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int resize_text_box_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

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

    private static TextBoxTag TagOf(ulong handle, int index)
    {
        var buffer = get_annotation_contents(handle, 0, index);
        try
        {
            Assert.Equal(OkPdfium, buffer.Status);
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            Assert.True(TextBoxTagReader.TryParse(Encoding.UTF8.GetString(bytes), out var tag), "not a text box tag");
            Assert.True(tag.HasBoxRect, "the tag records no box");
            return tag;
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    private static (double Left, double Top, double Right, double Bottom) Reported(ulong handle, int index)
    {
        var array = get_annotations(handle, 0);
        try
        {
            Assert.Equal(OkPdfium, array.Status);
            var a = Marshal.PtrToStructure<AnnotationInfo>(array.Items + (index * Marshal.SizeOf<AnnotationInfo>()));
            return (a.Left, a.Top, a.Right, a.Bottom);
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    /// <summary>A text box turned to <paramref name="degrees"/>; returns its index.</summary>
    private static int TurnedTextBox(ulong handle, float degrees)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("A turned box of words");
        Assert.Equal(OkPdfium, add_text_box_annotation(
            handle, 0, Cap, 100, 100, 500, 200, utf8, (nuint)utf8.Length, 20f, 0x11, 0x22, 0x33, 0xFF));
        var tag = TagOf(handle, 0);
        Assert.Equal(OkPdfium, rotate_text_box_annotation(
            handle, 0, 0, Cap,
            (float)(tag.BoxLeft * Cap), (float)(tag.BoxTop * Cap), (float)(tag.BoxRight * Cap), (float)(tag.BoxBottom * Cap),
            degrees, out int index));
        return index;
    }

    /// <summary>The group moves: the extra's reported rectangle is shifted, and the box written from it.</summary>
    private static int MoveAsExtra(ulong handle, int index, double dx, bool fixedPath)
    {
        var r = Reported(handle, index);
        (double Left, double Top, double Right, double Bottom) moved = (r.Left + dx, r.Top, r.Right + dx, r.Bottom);
        var tag = TagOf(handle, index);
        var into = fixedPath
            ? TurnedBox.Recentre((tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom), moved)
            : moved;
        Assert.Equal(OkPdfium, resize_text_box_annotation(
            handle, 0, index, Cap,
            (float)(into.Left * Cap), (float)(into.Top * Cap), (float)(into.Right * Cap), (float)(into.Bottom * Cap),
            out int newIndex));
        return newIndex;
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    public void a_turned_text_box_moved_as_an_extra_keeps_its_size_and_turn(float degrees)
    {
        ulong handle = OpenFixture();
        try
        {
            int index = TurnedTextBox(handle, degrees);
            var before = TagOf(handle, index);
            double width = before.BoxRight - before.BoxLeft;
            double centre = (before.BoxLeft + before.BoxRight) / 2;

            for (int i = 0; i < 4; i++)
            {
                index = MoveAsExtra(handle, index, 0.01, fixedPath: true);
            }

            var after = TagOf(handle, index);
            Assert.Equal(width, after.BoxRight - after.BoxLeft, 3);
            Assert.Equal(degrees, after.RotationDeg, 1);
            // And it went where the group went.
            Assert.Equal(centre + 0.04, (after.BoxLeft + after.BoxRight) / 2, 3);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void control_a_turned_text_box_written_at_its_reported_bounds_grows_with_every_move()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = TurnedTextBox(handle, 30f);
            var before = TagOf(handle, index);
            double width = before.BoxRight - before.BoxLeft;

            for (int i = 0; i < 4; i++)
            {
                index = MoveAsExtra(handle, index, 0.01, fixedPath: false);
            }

            var after = TagOf(handle, index);
            Assert.True(after.BoxRight - after.BoxLeft > width * 1.2,
                $"expected the old path to grow the box: {width:F4} -> {after.BoxRight - after.BoxLeft:F4}");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void recentring_the_upright_box_on_itself_changes_nothing()
    {
        var box = (0.1, 0.2, 0.5, 0.3);
        var same = TurnedBox.Recentre(box, box);
        Assert.Equal(0.1, same.Left, 12);
        Assert.Equal(0.2, same.Top, 12);
        Assert.Equal(0.5, same.Right, 12);
        Assert.Equal(0.3, same.Bottom, 12);

        var moved = TurnedBox.Recentre(box, (0.0, 0.0, 0.8, 0.6));
        Assert.Equal(0.2, moved.Left, 12);
        Assert.Equal(0.25, moved.Top, 12);
        Assert.Equal(0.6, moved.Right, 12);
        Assert.Equal(0.35, moved.Bottom, 12);
    }
}
