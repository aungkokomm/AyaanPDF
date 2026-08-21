using System;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The shape tool across the real FFI boundary.
///
/// The pure geometry is tested in <see cref="ShapeGeometryTests"/> and the PDF
/// behaviour in the Rust suite; neither covers the layer between them. A struct
/// whose fields do not line up with the native side produces no error, just
/// shapes drawn at nonsense coordinates or a silent access violation, and both
/// have happened in this app before.
/// </summary>
public class ShapeInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int InvalidInput = 1;
    private const int Unsupported = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShapeSpec
    {
        public int PageIndex;
        public int Kind;
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public float WidthPx;
        // The native struct has grown three times since this mirror was
        // written: RotationDeg, FillRgba, then CornerRadiusPx. A short mirror
        // does not error, it just marshals the array with the wrong stride and
        // feeds the core garbage from the next element.
        public float RotationDeg;
        public uint FillRgba;
        public float CornerRadiusPx;
        // ...and a fourth time, for the drop shadow. Adding the fields here is
        // not optional bookkeeping: a short mirror marshals the ARRAY with the
        // wrong stride, so every element after the first arrives as garbage and
        // the core rejects the batch. That is exactly how this one announced
        // itself, in six batch tests at once.
        public float ShadowAngleDeg;
        public float ShadowDistancePx;
        public float ShadowSoftnessPx;
        public float ShadowSpreadPx;
        public uint ShadowRgba;
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
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_ink_annotations(
        ulong docHandle, int captureWidth,
        [In] BurnStroke[]? strokes, nuint strokeCount,
        [In] BurnPoint[]? points, nuint pointCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int resize_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static ShapeSpec Spec(ShapeKind kind, float x1, float y1, float x2, float y2) => new()
    {
        PageIndex = 0,
        Kind = (int)kind,
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        R = 200,
        G = 30,
        B = 30,
        A = 255,
        WidthPx = 3f,
    };

    [Fact]
    public void the_shape_struct_is_the_size_the_native_side_writes()
    {
        // Two ints, four floats, four bytes, then width, rotation, fill,
        // corner radius, and the drop shadow's two offsets and colour. No
        // padding, because every field is four-byte aligned or a byte inside a
        // four-byte group.
        //
        // The number is the point of the test. It is the one assertion that
        // fails LOUDLY when the native struct grows and a mirror does not, in
        // place of six batch tests failing obscurely because the array was
        // marshalled with the wrong stride.
        Assert.Equal(64, Marshal.SizeOf<ShapeSpec>());
    }

    [Fact]
    public void the_kind_numbers_match_the_native_constants()
    {
        // These cross the boundary as plain ints. Reordering the C# enum would
        // silently turn every rectangle into an ellipse.
        Assert.Equal(0, (int)ShapeKind.Rectangle);
        Assert.Equal(1, (int)ShapeKind.Ellipse);
        Assert.Equal(2, (int)ShapeKind.Line);
        Assert.Equal(3, (int)ShapeKind.Arrow);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void every_kind_crosses_the_boundary_and_becomes_an_annotation(ShapeKind kind)
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(kind, 200, 200, 700, 500) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, specs, 1));

            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(OkPdfium, array.Status);
                Assert.Equal(1, (int)array.Len);
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_coordinates_that_arrive_are_the_ones_that_were_sent()
    {
        // Field-by-field marshalling, checked through geometry rather than
        // trusting the layout. A misaligned struct still returns success and
        // puts the shape somewhere else entirely.
        ulong handle = OpenFixture();
        try
        {
            // A wide, short rectangle: its proportions make a swapped or
            // shifted field obvious in a way a square never would.
            var specs = new[] { Spec(ShapeKind.Rectangle, 100, 400, 800, 500) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, specs, 1));

            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(1, (int)array.Len);
                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);

                // Normalized against page WIDTH on both axes, as everywhere
                // else in this app. Tolerance covers the stroke-width padding
                // the core adds so the outline is not clipped.
                Assert.InRange(info.Left, 0.08, 0.12);
                Assert.InRange(info.Right, 0.78, 0.82);
                Assert.InRange(info.Top, 0.38, 0.42);
                Assert.InRange(info.Bottom, 0.48, 0.52);
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void an_arrows_bounds_include_its_head()
    {
        // The head sticks out beyond the shaft. If the bounds only spanned the
        // drag, PDFium would clip the barbs off and the arrow would render as a
        // plain line, with nothing to indicate anything went wrong.
        ulong handle = OpenFixture();
        try
        {
            // Drawn straight down, so the barbs spread horizontally and the
            // extra width cannot be confused with the shaft's own extent.
            var specs = new[] { Spec(ShapeKind.Arrow, 500, 200, 500, 600) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, specs, 1));

            var array = get_annotations(handle, 0);
            try
            {
                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                double width = info.Right - info.Left;

                // A bare vertical line would be about one stroke wide (0.003).
                Assert.True(width > 0.02, $"the arrow's bounds are only {width} wide, so the head is clipped");
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_batch_of_shapes_all_arrive()
    {
        // Saving sends every shape in one call, so an offset error would show
        // up as a missing or duplicated mark rather than an error.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.Rectangle, 100, 100, 300, 200),
                Spec(ShapeKind.Ellipse, 350, 100, 550, 200),
                Spec(ShapeKind.Line, 100, 300, 550, 300),
                Spec(ShapeKind.Arrow, 100, 400, 550, 400),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, specs, (nuint)specs.Length));

            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(4, (int)array.Len);
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_shape_can_be_resized_across_the_boundary()
    {
        // The app calls this for every resize drag of a mark loaded from a
        // file. It is also the only annotation edit that reports a NEW index,
        // because the shape is rebuilt rather than stretched, and losing track
        // of that index means the selection outline ends up on the wrong mark.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.Ellipse, 100, 100, 300, 200) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, specs, 1));

            Assert.Equal(
                OkPdfium,
                resize_shape_annotation(handle, 0, 0, 1000, 100, 100, 800, 600, out int newIndex));
            Assert.True(newIndex >= 0, "resize did not report where the shape went");

            var array = get_annotations(handle, 0);
            try
            {
                // One annotation still: rebuilt, not duplicated.
                Assert.Equal(1, (int)array.Len);

                var info = Marshal.PtrToStructure<AnnotationInfo>(array.Items);
                Assert.True(info.Right - info.Left > 0.6, $"resized width is only {info.Right - info.Left}");
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void resizing_a_mark_that_is_not_a_shape_reports_unsupported()
    {
        // The app falls back to the general resize path on this exact code, so
        // it has to arrive intact rather than as a generic failure. An ink
        // stroke is the case that matters: it is a real annotation with no
        // description to rebuild from.
        ulong handle = OpenFixture();
        try
        {
            var points = new[]
            {
                new BurnPoint { X = 100, Y = 100 },
                new BurnPoint { X = 200, Y = 180 },
                new BurnPoint { X = 300, Y = 120 },
            };
            var strokes = new[]
            {
                new BurnStroke
                {
                    PageIndex = 0, PointOffset = 0, PointCount = 3,
                    WidthPx = 3f, R = 0, G = 0, B = 0, A = 255,
                },
            };
            Assert.Equal(OkPdfium, add_ink_annotations(handle, 1000, strokes, 1, points, 3));

            Assert.Equal(
                Unsupported,
                resize_shape_annotation(handle, 0, 0, 1000, 10, 10, 100, 100, out _));

            // And the stroke is still there, untouched.
            var array = get_annotations(handle, 0);
            try
            {
                Assert.Equal(1, (int)array.Len);
            }
            finally
            {
                free_annotation_array(array);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void resizing_an_annotation_that_does_not_exist_is_invalid_input()
    {
        // Distinct from Unsupported on purpose. Unsupported means "this mark
        // cannot be resized this way, try the other path"; a missing index is
        // a bug in the caller and must not be silently retried.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(
                InvalidInput,
                resize_shape_annotation(handle, 0, 0, 1000, 10, 10, 100, 100, out _));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void bad_input_is_refused_rather_than_crashing()
    {
        var specs = new[] { Spec(ShapeKind.Rectangle, 0, 0, 10, 10) };
        Assert.Equal(InvalidInput, add_shape_annotations(0, 1000, specs, 1));

        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(InvalidInput, add_shape_annotations(handle, 0, specs, 1));

            // An empty batch is the normal case when a document has no shapes,
            // and must not be an error.
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, null, 0));

            var bad = new[] { Spec((ShapeKind)77, 0, 0, 10, 10) };
            Assert.Equal(InvalidInput, add_shape_annotations(handle, 1000, bad, 1));
        }
        finally
        {
            close_document(handle);
        }
    }
}
