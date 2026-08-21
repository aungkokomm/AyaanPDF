using System;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Resizing a TURNED shape, through the real writer and a real document.
///
/// The bug: dragging a corner handle of a rotated shape did nothing to its
/// size. The app sent every shape edit, move and resize alike, through
/// <c>move_shape_annotation</c>, which tells the core "these bounds are the
/// padded /Rect". For a turned shape that instructs the core to rebuild from
/// the size recorded on the tag, because a turned /Rect cannot be de-padded or
/// inverted. Right for a move. For a resize it discards the entire gesture.
///
/// These pin the two halves of the contract the fix depends on, at several
/// angles rather than at the one that happens to be easy to reason about.
/// 45 degrees is the singular case people reach for, and it is precisely the
/// angle at which an AABB carries the least information, so passing there and
/// nowhere else would prove very little.
/// </summary>
public class RotatedShapeResizeInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

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
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

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
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document_from_bytes(IntPtr data, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int move_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int resize_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

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

    private static ShapeTag TagOf(ulong handle, int page, int index)
    {
        Assert.True(ShapeTagReader.TryParse(Tag(handle, page, index), out var tag),
            "the shape's tag did not parse");
        return tag;
    }

    /// <summary>A shape spanning capture 200..400 by 300..400, i.e. 200 x 100.</summary>
    private static ShapeSpec Spec(ShapeKind kind, float rotation) => new()
    {
        PageIndex = 0,
        Kind = (int)kind,
        X1 = 200, Y1 = 300, X2 = 400, Y2 = 400,
        R = 200, G = 30, B = 30, A = 255,
        WidthPx = 3f,
        RotationDeg = rotation,
        FillRgba = 0u,
        CornerRadiusPx = 0f,
    };

    /// <summary>The annotation's own /Rect, normalized the way the app reads it.</summary>
    private static TextRect RectOf(ulong handle, int page, int index)
    {
        var array = get_annotations(handle, page);
        try
        {
            Assert.Equal(OkPdfium, array.Status);
            int stride = Marshal.SizeOf<AnnotationInfo>();
            var a = Marshal.PtrToStructure<AnnotationInfo>(array.Items + (index * stride));
            return new TextRect(a.Left, a.Top, a.Right, a.Bottom);
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    // ---------------- The fix's contract ----------------

    [Theory]
    [InlineData(ShapeKind.Rectangle, 15f)]
    [InlineData(ShapeKind.Rectangle, 30f)]
    [InlineData(ShapeKind.Rectangle, 45f)]
    [InlineData(ShapeKind.Rectangle, 90f)]
    [InlineData(ShapeKind.Rectangle, 135f)]
    [InlineData(ShapeKind.Rectangle, 270f)]
    [InlineData(ShapeKind.Arrow, 30f)]
    [InlineData(ShapeKind.Arrow, 45f)]
    [InlineData(ShapeKind.Ellipse, 60f)]
    [InlineData(ShapeKind.Line, 120f)]
    public void a_turned_shape_redrawn_into_a_bigger_upright_box_actually_grows(
        ShapeKind kind, float rotation)
    {
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(kind, rotation)], 1));

            var before = TagOf(handle, 0, 0);
            Assert.True(before.BoxWidthPts > 0, "a turned shape must record its upright size");

            // Twice the width, twice the height, same centre: what the corner
            // drag works out to once the frame's ratio is applied.
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, Cap, 200, 300, 600, 500, out int newIndex));

            var after = TagOf(handle, 0, newIndex);

            Assert.Equal(before.BoxWidthPts * 2, after.BoxWidthPts, 1);
            Assert.Equal(before.BoxHeightPts * 2, after.BoxHeightPts, 1);

            // And the shape is still what it was, at the angle it was.
            Assert.Equal(kind, after.Kind);
            Assert.Equal(rotation, after.RotationDeg, 2);
            Assert.Equal(before.StrokeHex, after.StrokeHex);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void a_turned_shape_can_be_made_smaller_too(float factor)
    {
        // Growing and shrinking are not the same code path in anyone's head,
        // and a clamp that only bites downward would pass every test above.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, 40f)], 1));
            var before = TagOf(handle, 0, 0);

            float w = 200 * factor;
            float h = 100 * factor;
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, Cap, 200, 300, 200 + w, 300 + h, out int newIndex));

            var after = TagOf(handle, 0, newIndex);

            Assert.Equal(before.BoxWidthPts * factor, after.BoxWidthPts, 1);
            Assert.Equal(before.BoxHeightPts * factor, after.BoxHeightPts, 1);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_turned_shapes_new_rect_is_not_the_rectangle_it_was_given()
    {
        // Why the caller must re-read the annotation's bounds after a resize
        // rather than keeping the rectangle it dragged.
        //
        // A turned shape's /Rect is the axis-aligned box of its rotated, padded
        // content, and that box is NOT a linear stretch of the old one unless
        // both axes were scaled by the same factor. Stretch one axis only and
        // the true /Rect comes back a different shape from the drag. Keeping
        // the dragged rectangle would leave the frame wrong, and would feed a
        // wrong starting size into the NEXT resize, which is how "every resize
        // makes it bigger" bugs begin.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, 45f)], 1));

            // Width only: 200x100 upright becomes 400x100.
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, Cap, 200, 300, 600, 400, out int newIndex));

            var actual = RectOf(handle, 0, newIndex);
            var asked = new TextRect(0.2, 0.3, 0.6, 0.4);

            Assert.True(
                Math.Abs(actual.Width - asked.Width) > 0.01
                || Math.Abs(actual.Height - asked.Height) > 0.01,
                $"expected the real /Rect to differ from the drag, got {actual.Width:F4}x{actual.Height:F4}");

            // At 45 degrees a 400x100 box has an AABB about 354 capture units
            // square, so the true rect is very nearly square whatever the drag
            // looked like.
            Assert.Equal(actual.Width, actual.Height, 3);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- The move contract, which must NOT change ----------------

    [Theory]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(135f)]
    public void moving_a_turned_shape_still_keeps_its_size_exactly(float rotation)
    {
        // This is the behaviour `move_shape_annotation` exists to provide, and
        // the reason it reads the size off the tag. A turned /Rect cannot be
        // de-padded, so a move that tried to derive the size from it would
        // inflate the shape a little every time it was dragged. The fix must
        // leave this completely alone.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, rotation)], 1));
            var before = TagOf(handle, 0, 0);

            // Shift the /Rect by a fixed amount, keeping its size.
            Assert.Equal(OkPdfium, move_shape_annotation(
                handle, 0, 0, Cap, 300, 400, 500, 500, out int newIndex));

            var after = TagOf(handle, 0, newIndex);

            Assert.Equal(before.BoxWidthPts, after.BoxWidthPts, 3);
            Assert.Equal(before.BoxHeightPts, after.BoxHeightPts, 3);
            Assert.Equal(rotation, after.RotationDeg, 2);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void repeated_moves_of_a_turned_shape_do_not_let_it_drift_in_size()
    {
        // The regression this whole design guards: four moves used to take a
        // rotated shape from 0.241 to 0.896 wide.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, 45f)], 1));
            var before = TagOf(handle, 0, 0);

            int index = 0;
            for (int i = 0; i < 4; i++)
            {
                var t = TagOf(handle, 0, index);
                Assert.Equal(before.BoxWidthPts, t.BoxWidthPts, 3);

                // Move by 10 capture units, preserving the CURRENT /Rect size.
                Assert.Equal(OkPdfium, move_shape_annotation(
                    handle, 0, index, Cap, 200 + (10 * i), 300, 400 + (10 * i), 400, out index));
            }

            var after = TagOf(handle, 0, index);
            Assert.Equal(before.BoxWidthPts, after.BoxWidthPts, 3);
            Assert.Equal(before.BoxHeightPts, after.BoxHeightPts, 3);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- A KNOWN GAP, pinned deliberately ----------------

    [Fact]
    public void undoing_a_turned_shapes_resize_does_NOT_yet_restore_its_size()
    {
        // CHARACTERISATION, not an endorsement. This asserts what the app
        // currently does so the gap cannot be forgotten; it is expected to be
        // deleted by whoever closes it.
        //
        // Undo replays a move to the pre-gesture rectangle, and a move rebuilds
        // a turned shape from the size on its tag, which the resize has already
        // overwritten. So the shape returns to the right PLACE at the wrong
        // SIZE. Closing this needs the undo record to carry the shape's tag as
        // well as its rectangle, and TagRecord holds a single Rect, so it
        // cannot express a resize's before and after. That is a change to the
        // undo data model rather than to this path.
        ulong handle = OpenFixture();
        try
        {
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, 45f)], 1));
            var original = TagOf(handle, 0, 0);
            var originalRect = RectOf(handle, 0, 0);

            // Resize, the way the fixed app now does.
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, Cap, 200, 300, 600, 500, out int resized));
            Assert.Equal(original.BoxWidthPts * 2, TagOf(handle, 0, resized).BoxWidthPts, 1);

            // Undo, the way ApplyBoundsRecord does: move back to the old /Rect.
            Assert.Equal(OkPdfium, move_shape_annotation(
                handle, 0, resized, Cap,
                (float)(originalRect.Left * Cap), (float)(originalRect.Top * Cap),
                (float)(originalRect.Right * Cap), (float)(originalRect.Bottom * Cap),
                out int undone));

            var after = TagOf(handle, 0, undone);

            // The size did NOT come back. When this assertion starts failing,
            // the gap has been closed and this test should be replaced by one
            // asserting equality with `original`.
            Assert.NotEqual(original.BoxWidthPts, after.BoxWidthPts, 1);
            Assert.Equal(original.BoxWidthPts * 2, after.BoxWidthPts, 1);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- It has to survive the file ----------------

    [Theory]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(120f)]
    public void a_resized_turned_shape_comes_back_the_same_size_after_save_and_reopen(float rotation)
    {
        ulong handle = OpenFixture();
        Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, [Spec(ShapeKind.Rectangle, rotation)], 1));
        Assert.Equal(OkPdfium, resize_shape_annotation(
            handle, 0, 0, Cap, 200, 300, 600, 500, out int newIndex));

        var before = TagOf(handle, 0, newIndex);
        var saved = snapshot_document(handle);
        close_document(handle);
        Assert.Equal(OkPdfium, saved.Status);

        ulong reopened = open_document_from_bytes(saved.Data, saved.Len);
        Assert.NotEqual(0UL, reopened);
        try
        {
            var after = TagOf(reopened, 0, 0);

            Assert.Equal(before.BoxWidthPts, after.BoxWidthPts, 3);
            Assert.Equal(before.BoxHeightPts, after.BoxHeightPts, 3);
            Assert.Equal(rotation, after.RotationDeg, 2);
            Assert.Equal(before.Kind, after.Kind);
            Assert.Equal(before.StrokeHex, after.StrokeHex);

            // And the reopened shape still reconstructs into the model at the
            // size it was dragged to, which is what selection measures against.
            var page = DocumentModelBuilder.BuildPage(
                0,
                [new AnnotationSnapshot(0, 4, 0, 0, 0, 0, 1.0, Guid.NewGuid(), Tag(reopened, 0, 0))],
                1000);
            var shape = Assert.IsType<ShapeObject>(page.Objects[0]);
            Assert.Equal(after.BoxWidthPts / 1000, shape.UprightBounds.Width, 6);
        }
        finally
        {
            close_document(reopened);
        }
    }
}
