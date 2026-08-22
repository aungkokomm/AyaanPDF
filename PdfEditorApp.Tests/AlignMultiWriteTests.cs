using System;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The delete+re-add pattern that align/distribute/move-all use on multiple
/// annotations at once. Each resize_shape_annotation call deletes the old
/// annotation and re-adds a rebuilt one at the END of the page's list, and
/// every annotation with an index HIGHER than the deleted one shifts down
/// by one. When you then delete a lower index, that shift-down applies to
/// EVERY item currently at a higher index - including items THIS BATCH just
/// re-added at the end. So the newIndex a single write reports back is L-1
/// at that moment, but it is not the annotation's index once the batch is
/// done.
///
/// The claim these tests pin: on a page that starts with L annotations, if
/// N successful writes go in DESCENDING original-index order, the writes
/// end up at positions [L - N, L - N + 1, ..., L - 1] in WRITE ORDER.
///
/// Read positions with get_annotations rather than trusting each write's
/// returned newIndex.
/// </summary>
public class AlignMultiWriteTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;

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
        // ...and a fourth time, for the effects. Getting these right is not
        // optional bookkeeping, and it is worse than a wrong stride now: the
        // core reads bytes 48 to 55 as a POINTER to the effects text. A mirror
        // that still spells the old five shadow floats is the SAME 64 bytes, so
        // the stride is right and the size assertion passes, and it only stays
        // harmless while those bytes happen to be zero, which reads as null.
        // Anything writing a value there hands the core a wild pointer.
        public IntPtr EffectsUtf8;
        public nuint EffectsLen;
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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

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

    private static ShapeSpec Rect(float x1, float y1, float x2, float y2) => new()
    {
        PageIndex = 0,
        Kind = (int)ShapeKind.Rectangle,
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        R = 200, G = 30, B = 30, A = 255,
        WidthPx = 3f,
    };

    private static AnnotationInfo[] ReadAll(ulong handle)
    {
        var array = get_annotations(handle, 0);
        try
        {
            Assert.Equal(OkPdfium, array.Status);
            var result = new AnnotationInfo[(int)array.Len];
            int stride = Marshal.SizeOf<AnnotationInfo>();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Marshal.PtrToStructure<AnnotationInfo>(array.Items + i * stride);
            }
            return result;
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    /// <summary>
    /// The core claim behind v1.79.1's fix. Add three shapes, resize them in
    /// DESCENDING index order, then verify the run-at-end pattern: the last-
    /// written shape lands at count-1, second-to-last at count-2, and so on.
    /// </summary>
    [Fact]
    public void descending_writes_end_up_in_write_order_at_the_end()
    {
        ulong handle = OpenFixture();
        try
        {
            // Add shapes one at a time (batch add has a pre-existing regression
            // where it returns InvalidInput past the first shape - see
            // a_batch_of_shapes_all_arrive; that is not this test's job).
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(100, 100, 200, 200) }, 1));  // A -> idx 0
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(300, 100, 400, 200) }, 1));  // B -> idx 1
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(500, 100, 600, 200) }, 1));  // C -> idx 2

            var before = ReadAll(handle);
            Assert.Equal(3, before.Length);
            int lengthBefore = before.Length;

            // Write in DESCENDING original index: C first, then B, then A.
            // Same order CommitAlignedOrDistributed uses.
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 2, 1000, 500, 300, 600, 400, out _));
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 1, 1000, 300, 300, 400, 400, out _));
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, 1000, 100, 300, 200, 400, out _));

            var after = ReadAll(handle);
            Assert.Equal(lengthBefore, after.Length);

            // Run-at-end claim: writes in order [C, B, A] land at
            // positions [L-3, L-2, L-1] in that same write order.
            // Every shape moved to Top ≈ 0.3 (300 / 1000). Distinguish by X.
            int count = after.Length;
            AnnotationInfo cNew = after[count - 3];
            AnnotationInfo bNew = after[count - 2];
            AnnotationInfo aNew = after[count - 1];

            // A shape has stroke padding around its /Rect, so tolerate a small band.
            Assert.InRange(cNew.Left, 0.48, 0.52); // C is X=500
            Assert.InRange(bNew.Left, 0.28, 0.32); // B is X=300
            Assert.InRange(aNew.Left, 0.08, 0.12); // A is X=100

            // All should now be at the new Y (Top ≈ 0.3).
            Assert.InRange(cNew.Top, 0.28, 0.32);
            Assert.InRange(bNew.Top, 0.28, 0.32);
            Assert.InRange(aNew.Top, 0.28, 0.32);
        }
        finally
        {
            close_document(handle);
        }
    }

    /// <summary>
    /// Two consecutive multi-writes: what the user's align-top-then-align-bottom
    /// actually does. If the second batch's cached indices point at slid-down
    /// neighbours, the second write hits the wrong annotations and shapes end
    /// up in nonsense positions. This is the regression v1.79.1 was supposed
    /// to fix.
    /// </summary>
    [Fact]
    public void two_consecutive_batches_land_where_they_are_meant_to()
    {
        ulong handle = OpenFixture();
        try
        {
            // Add shapes one at a time (batch add is broken - see the other test).
            // A: h=100, B: h=200, C: h=50 - all different so align top vs bottom
            // actually moves each of them.
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(100, 100, 200, 200) }, 1));
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(300, 200, 400, 400) }, 1));
            Assert.Equal(OkPdfium, add_shape_annotations(handle, 1000, new[] { Rect(500, 300, 600, 350) }, 1));

            // Batch 1: align top (all tops -> 100). Descending index order.
            // Each write returns a newIndex but we DELIBERATELY ignore it here -
            // that value goes stale on the next write's delete. The C# fix
            // recomputes real indices via the run-at-end pattern instead.
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 2, 1000, 500, 100, 600, 150, out _));  // C: h=50
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 1, 1000, 300, 100, 400, 300, out _));  // B: h=200
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, 0, 1000, 100, 100, 200, 200, out _));  // A: h=100

            var mid = ReadAll(handle);
            string dump = string.Join(" | ",
                mid.Select(a => $"idx={a.Index} L={a.Left:F3} T={a.Top:F3} R={a.Right:F3} B={a.Bottom:F3}"));
            Assert.True(mid.Length == 3, $"expected 3 after align-top, got {mid.Length}. Dump: {dump}");

            // Identify each by X position (unchanged by align-top).
            int Locate(float leftFrac)
            {
                for (int i = 0; i < mid.Length; i++)
                {
                    if (Math.Abs(mid[i].Left - leftFrac) < 0.03) { return i; }
                }
                throw new Xunit.Sdk.XunitException(
                    $"shape near Left={leftFrac} not found after align-top. Dump: {dump}");
            }
            int aIdxNow = Locate(0.10f);
            int bIdxNow = Locate(0.30f);
            int cIdxNow = Locate(0.50f);

            // The run-at-end pattern: writes went [C, B, A], so final positions
            // are [C at 0, B at 1, A at 2]. NOT what naive newIndex tracking says
            // (which would have all three claiming index 2).
            Assert.Equal(0, cIdxNow);
            Assert.Equal(1, bIdxNow);
            Assert.Equal(2, aIdxNow);

            // Batch 2: align bottom. Bounding bottom = 300 (from B). Descending
            // index order after batch 1 means A (idx 2) first, then B (1), then C (0).
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, aIdxNow, 1000, 100, 200, 200, 300, out _));
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, bIdxNow, 1000, 300, 100, 400, 300, out _));
            Assert.Equal(OkPdfium, resize_shape_annotation(
                handle, 0, cIdxNow, 1000, 500, 250, 600, 300, out _));

            var end = ReadAll(handle);
            Assert.Equal(3, end.Length);

            // Every shape should now have Bottom near 0.3 (300/1000).
            foreach (var a in end)
            {
                Assert.InRange(a.Bottom, 0.28, 0.32);
            }

            // And each is where the align actually moved it, identifiable by X.
            AnnotationInfo Find(float leftFrac)
            {
                foreach (var a in end)
                {
                    if (Math.Abs(a.Left - leftFrac) < 0.03) { return a; }
                }
                throw new Xunit.Sdk.XunitException($"shape near Left={leftFrac} disappeared after align-bottom");
            }

            var aEnd = Find(0.10f);
            var bEnd = Find(0.30f);
            var cEnd = Find(0.50f);

            // A: bottom=300, top=200 (h=100)
            Assert.InRange(aEnd.Top, 0.18, 0.22);
            // B: bottom=300, top=100 (h=200)
            Assert.InRange(bEnd.Top, 0.08, 0.12);
            // C: bottom=300, top=250 (h=50)
            Assert.InRange(cEnd.Top, 0.23, 0.27);
        }
        finally
        {
            close_document(handle);
        }
    }
}
