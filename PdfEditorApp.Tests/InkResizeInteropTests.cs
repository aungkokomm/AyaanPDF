using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Resizing a freehand stroke through the real writer and a real document.
///
/// Replays what the app's rebuild does: scale the control points onto the new
/// rectangle, delete the annotation, add it again from the fitted curve, and
/// write the tag back. If any part of that round trip loses the description,
/// a stroke would resize once and then be unresizable, or come back a
/// different shape after a save.
/// </summary>
public class InkResizeInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BurnPoint
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BurnStroke
    {
        public int PageIndex;
        public uint PointOffset;
        public uint PointCount;
        public float WidthPx;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
    }

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
    private static extern int add_ink_annotations(
        ulong docHandle, int captureWidth,
        [In] BurnStroke[]? strokes, nuint strokeCount,
        [In] BurnPoint[]? points, nuint pointCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_annotation_body(
        ulong docHandle, int pageIndex, int index, [In] byte[] bodyUtf8, nuint bodyLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document_from_bytes(IntPtr data, nuint len);

    private static readonly List<(double X, double Y)> Squiggle =
    [
        (0.10, 0.20), (0.20, 0.10), (0.30, 0.30), (0.40, 0.15), (0.50, 0.25),
    ];

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static string? Tag(ulong handle, int page, int index)
    {
        var buffer = get_annotation_contents(handle, page, index);
        try
        {
            if (buffer.Status != OkPdfium || buffer.Data == IntPtr.Zero || buffer.Len == 0) { return null; }
            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    private static int AnnotationCount(ulong handle, int page)
    {
        var array = get_annotations(handle, page);
        try { return (int)array.Len; }
        finally { free_annotation_array(array); }
    }

    /// <summary>Adds a stroke exactly as the app does: the FITTED curve into the
    /// document, the CONTROL points into the tag.</summary>
    private static int AddStroke(ulong handle, IReadOnlyList<(double X, double Y)> control)
    {
        var curve = StrokeSmoothing.Fit(control);
        var pts = curve.Select(p => new BurnPoint { X = (float)(p.X * Cap), Y = (float)(p.Y * Cap) }).ToArray();
        var stroke = new BurnStroke
        {
            PageIndex = 0, PointOffset = 0, PointCount = (uint)pts.Length,
            WidthPx = 4f, R = 255, G = 0, B = 0, A = 255,
        };
        Assert.Equal(OkPdfium, add_ink_annotations(handle, Cap, [stroke], 1, pts, (nuint)pts.Length));

        int index = AnnotationCount(handle, 0) - 1;
        WriteTag(handle, index, control);
        return index;
    }

    private static void WriteTag(ulong handle, int index, IReadOnlyList<(double X, double Y)> control)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(InkTag.Write("FFFF0000", 0.004, control));
        Assert.Equal(OkPdfium, set_annotation_body(handle, 0, index, body, (nuint)body.Length));
    }

    /// <summary>The app's rebuild: scale, delete, re-add, re-tag.</summary>
    private static int Resize(ulong handle, int index, double l, double t, double r, double b)
    {
        Assert.True(InkTag.TryParse(Tag(handle, 0, index), out _, out _, out var control));
        var scaled = InkTag.ScaleTo(control, l, t, r, b);

        Assert.Equal(OkPdfium, delete_annotation(handle, 0, index));
        var curve = StrokeSmoothing.Fit(scaled);
        var pts = curve.Select(p => new BurnPoint { X = (float)(p.X * Cap), Y = (float)(p.Y * Cap) }).ToArray();
        var stroke = new BurnStroke
        {
            PageIndex = 0, PointOffset = 0, PointCount = (uint)pts.Length,
            WidthPx = 4f, R = 255, G = 0, B = 0, A = 255,
        };
        Assert.Equal(OkPdfium, add_ink_annotations(handle, Cap, [stroke], 1, pts, (nuint)pts.Length));

        int newIndex = AnnotationCount(handle, 0) - 1;
        WriteTag(handle, newIndex, scaled);
        return newIndex;
    }

    private static IReadOnlyList<(double X, double Y)> PointsOf(ulong handle, int index)
    {
        Assert.True(InkTag.TryParse(Tag(handle, 0, index), out _, out _, out var control));
        return control;
    }

    private static (double W, double H) BoxOf(IReadOnlyList<(double X, double Y)> pts) =>
        (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));

    // ---------------- The round trip ----------------

    [Fact]
    public void a_stroke_written_to_a_real_document_keeps_its_description()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = AddStroke(handle, Squiggle);
            var read = PointsOf(handle, index);

            Assert.Equal(Squiggle.Count, read.Count);
            Assert.Equal(0.4, BoxOf(read).W, 3);
            Assert.Equal(0.2, BoxOf(read).H, 3);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Theory]
    [InlineData(0.1, 0.1, 0.9, 0.3, 0.8, 0.2)]     // wider only
    [InlineData(0.1, 0.1, 0.5, 0.9, 0.4, 0.8)]     // taller only
    [InlineData(0.0, 0.0, 0.8, 0.8, 0.8, 0.8)]     // both, non-uniform
    [InlineData(0.4, 0.4, 0.5, 0.45, 0.1, 0.05)]   // shrunk
    public void a_stroke_can_be_redrawn_at_a_new_size_in_a_real_document(
        double l, double t, double r, double b, double expectW, double expectH)
    {
        ulong handle = OpenFixture();
        try
        {
            int index = AddStroke(handle, Squiggle);
            int resized = Resize(handle, index, l, t, r, b);

            var box = BoxOf(PointsOf(handle, resized));
            Assert.Equal(expectW, box.W, 3);
            Assert.Equal(expectH, box.H, 3);

            // Still exactly one annotation: the rebuild replaced it rather than
            // leaving the original behind.
            Assert.Equal(1, AnnotationCount(handle, 0));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void resizing_and_resizing_back_restores_the_stroke()
    {
        // The app's undo replays the pre-gesture rectangle through the same
        // rebuild, so this is what an undo of a resize amounts to.
        ulong handle = OpenFixture();
        try
        {
            int index = AddStroke(handle, Squiggle);
            var before = PointsOf(handle, index).ToList();

            int grown = Resize(handle, index, 0.0, 0.0, 0.8, 0.6);
            int back = Resize(handle, grown, 0.1, 0.1, 0.5, 0.3);

            var after = PointsOf(handle, back);
            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].X, after[i].X, 3);
                Assert.Equal(before[i].Y, after[i].Y, 3);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void repeated_resizes_do_not_let_a_stroke_drift()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = AddStroke(handle, Squiggle);
            var before = PointsOf(handle, index).ToList();

            for (int i = 0; i < 5; i++)
            {
                index = Resize(handle, index, 0.1, 0.1, 0.5 + (0.02 * i), 0.3 + (0.02 * i));
            }
            index = Resize(handle, index, 0.1, 0.1, 0.5, 0.3);

            var after = PointsOf(handle, index);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].X, after[i].X, 3);
                Assert.Equal(before[i].Y, after[i].Y, 3);
            }
            Assert.Equal(1, AnnotationCount(handle, 0));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_resized_stroke_survives_a_save_and_reopen()
    {
        ulong handle = OpenFixture();
        int index = AddStroke(handle, Squiggle);
        int resized = Resize(handle, index, 0.2, 0.2, 0.9, 0.7);
        var before = PointsOf(handle, resized).ToList();

        var saved = snapshot_document(handle);
        close_document(handle);
        Assert.Equal(OkPdfium, saved.Status);

        ulong reopened = open_document_from_bytes(saved.Data, saved.Len);
        Assert.NotEqual(0UL, reopened);
        try
        {
            var after = PointsOf(reopened, 0);

            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].X, after[i].X, 4);
                Assert.Equal(before[i].Y, after[i].Y, 4);
            }

            // And it is STILL resizable after a reopen, which is the property
            // that makes the guard safe to open up.
            Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Ink, Tag(reopened, 0, 0)));
        }
        finally
        {
            close_document(reopened);
        }
    }
}
