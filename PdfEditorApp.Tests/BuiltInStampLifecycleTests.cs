using System;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A generated stamp survives everything an ordinary stamp survives.
///
/// The claim the whole design rests on is that a built-in becomes the SAME
/// image annotation a user's PNG becomes, so move, resize, rotate, copy,
/// delete, undo and z-order keep working without being taught about it. That
/// is a claim about real PDFium behaviour, so it is tested against a real
/// document rather than asserted.
///
/// Win2D cannot run in a plain test host, so the pixels here are synthesised
/// at the DIMENSIONS BuiltInStamps.Measure actually produces. What is being
/// proved is that the shape of the renderer's output is one PDFium handles:
/// wide aspect ratios, a rotated banner's padded bounds, and the megabyte-plus
/// buffers a 1200px-wide stamp implies.
/// </summary>
public class BuiltInStampLifecycleTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;

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

    [DllImport(Lib)] private static extern ulong open_document(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib)] private static extern void close_document(ulong docHandle);
    [DllImport(Lib)] private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);
    [DllImport(Lib)] private static extern void free_annotation_array(AnnotationArray array);
    [DllImport(Lib)] private static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib)]
    private static extern int add_stamp_annotation(
        ulong docHandle, int pageIndex, int captureWidth,
        float left, float top, float right, float bottom,
        byte[] bgra, nuint len, int width, int height);

    [DllImport(Lib)]
    private static extern int set_annotation_bounds(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom);

    [DllImport(Lib)]
    private static extern int resize_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib)]
    private static extern int rotate_stamp_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float degrees, out int newIndex);

    [DllImport(Lib)]
    private static extern int raise_stamp_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    private const int Capture = 1000;

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    /// <summary>
    /// A buffer the size and shape StampRenderer really emits for a stamp.
    ///
    /// Opaque ink with a transparent margin, which is what a drawn stamp is:
    /// the point is the DIMENSIONS, not the picture.
    /// </summary>
    private static (byte[] Bgra, int Width, int Height) Pixels(string id)
    {
        var stamp = BuiltInStamps.ById(id)!;
        var metrics = BuiltInStamps.Measure(stamp, 1200, StampTheme.Default);

        var (w, h) = stamp.Diagonal
            ? BuiltInStamps.RotatedBounds(
                metrics.Width, metrics.Height, StampTheme.Default.DiagonalDegrees)
            : (metrics.Width, metrics.Height);

        var bgra = new byte[w * h * 4];
        for (int y = h / 4; y < h * 3 / 4; y++)
        {
            for (int x = w / 4; x < w * 3 / 4; x++)
            {
                int i = ((y * w) + x) * 4;
                bgra[i] = stamp.Color.B;
                bgra[i + 1] = stamp.Color.G;
                bgra[i + 2] = stamp.Color.R;
                bgra[i + 3] = 255;
            }
        }

        return (bgra, w, h);
    }

    private static int Place(ulong handle, string id, float left = 100, float top = 100)
    {
        var (bgra, w, h) = Pixels(id);
        var stamp = BuiltInStamps.ById(id)!;

        // The same normalized geometry PlaceStamp computes, through the same
        // tested helper, so this places it where the app would.
        var (l, t, r, b) = StampPlacement.Compute(
            left / Capture, top / Capture, w, h, 0.25);

        Assert.Equal(OkPdfium, add_stamp_annotation(
            handle, 0, Capture,
            (float)(l * Capture), (float)(t * Capture),
            (float)(r * Capture), (float)(b * Capture),
            bgra, (nuint)bgra.Length, w, h));

        Assert.NotNull(stamp);
        return Count(handle) - 1;
    }

    private static int Count(ulong handle)
    {
        var array = get_annotations(handle, 0);
        try
        {
            return (int)(uint)array.Len;
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    private static AnnotationInfo At(ulong handle, int index)
    {
        var array = get_annotations(handle, 0);
        try
        {
            Assert.True(index < (int)(uint)array.Len, "no annotation at that index");
            return Marshal.PtrToStructure<AnnotationInfo>(
                array.Items + (index * Marshal.SizeOf<AnnotationInfo>()));
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    /// <summary>Every built-in, so a badly proportioned one cannot hide.</summary>
    public static TheoryData<string> EveryStamp()
    {
        var data = new TheoryData<string>();
        foreach (var stamp in BuiltInStamps.All)
        {
            data.Add(stamp.Id);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStamp))]
    public void every_built_in_places_as_an_ordinary_stamp_annotation(string id)
    {
        // Subtype 4 is Stamp. This is the assertion that the feature is not a
        // new kind of object: whatever drew it, what lands in the PDF is the
        // thing every existing tool already knows how to edit.
        ulong handle = OpenFixture();
        try
        {
            int index = Place(handle, id);
            var info = At(handle, index);

            Assert.Equal(4, info.Subtype);
            Assert.True(info.Right > info.Left && info.Bottom > info.Top,
                $"{id} placed with an empty rectangle");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Theory]
    [MemberData(nameof(EveryStamp))]
    public void every_built_in_keeps_the_aspect_it_was_drawn_at(string id)
    {
        // StampPlacement takes the height from the IMAGE, so a banner must land
        // wide and a mark must land square. A stamp that squashed here would be
        // one whose Measure and whose placement disagree.
        ulong handle = OpenFixture();
        try
        {
            var (_, w, h) = Pixels(id);
            var info = At(handle, Place(handle, id));

            double placed = (info.Right - info.Left) / (info.Bottom - info.Top);

            Assert.Equal((double)w / h, placed, 1);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_generated_stamp_moves_resizes_rotates_and_deletes()
    {
        // The lifecycle, on the widest and most awkward of them: a rotated
        // banner, whose buffer is padded on both axes.
        ulong handle = OpenFixture();
        try
        {
            int index = Place(handle, "draft");
            var placed = At(handle, index);

            // Move. TRANSLATION ONLY, and deliberately so: a stamp owns page
            // objects, and set_annotation_bounds moves those but cannot scale
            // them, so it answers STATUS_UNSUPPORTED to a rectangle of a
            // different size. Scaling is resize_annotation's job, below, which
            // rebuilds the annotation instead. Asking for both at once here is
            // what this test got wrong first time.
            const float Shift = 0.2f;
            Assert.Equal(OkPdfium, set_annotation_bounds(
                handle, 0, index, Capture,
                (placed.Left + Shift) * Capture, (placed.Top + Shift) * Capture,
                (placed.Right + Shift) * Capture, (placed.Bottom + Shift) * Capture));

            var moved = At(handle, index);
            Assert.Equal(placed.Left + Shift, moved.Left, 3);
            Assert.Equal(placed.Top + Shift, moved.Top, 3);

            // And it really moved rather than resized.
            Assert.Equal(placed.Right - placed.Left, moved.Right - moved.Left, 3);

            // Resize. Returns a new index because the annotation is rebuilt.
            Assert.Equal(OkPdfium, resize_annotation(
                handle, 0, index, Capture, 300, 300, 900, 500, out int resized));
            Assert.Equal(0.90, At(handle, resized).Right, 2);

            // Rotate.
            Assert.Equal(OkPdfium, rotate_stamp_annotation(
                handle, 0, resized, Capture, 15, out int rotated));
            Assert.Equal(4, At(handle, rotated).Subtype);

            // Delete.
            Assert.Equal(OkPdfium, delete_annotation(handle, 0, rotated));
            Assert.Equal(0, Count(handle));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void z_order_still_works_on_a_generated_stamp()
    {
        // Bring-to-front is raise_stamp_annotation, which rebuilds the
        // annotation at the end of the page's list. Two stamps, raise the
        // first, and it has to end up last.
        ulong handle = OpenFixture();
        try
        {
            int first = Place(handle, "approved", 100, 100);
            Place(handle, "void", 400, 400);
            Assert.Equal(2, Count(handle));

            var before = At(handle, first);
            Assert.Equal(OkPdfium, raise_stamp_annotation(
                handle, 0, first, Capture,
                before.Left * Capture, before.Top * Capture,
                before.Right * Capture, before.Bottom * Capture,
                out int raised));

            Assert.Equal(Count(handle) - 1, raised);
            Assert.Equal(4, At(handle, raised).Subtype);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void copying_a_generated_stamp_makes_a_second_independent_one()
    {
        // Paste re-places the same pixels, so two stamps that can be moved
        // apart is the whole of it. They must not share a rectangle.
        ulong handle = OpenFixture();
        try
        {
            int original = Place(handle, "confidential", 100, 100);
            int copy = Place(handle, "confidential", 500, 500);

            Assert.Equal(2, Count(handle));
            Assert.NotEqual(At(handle, original).Left, At(handle, copy).Left);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void undoing_a_move_puts_a_generated_stamp_back_exactly()
    {
        // Undo replays the old rectangle. If it drifts, undo quietly moves
        // things, which is worse than not undoing at all.
        ulong handle = OpenFixture();
        try
        {
            int index = Place(handle, "paid");
            var original = At(handle, index);

            // Moved by a translation, for the reason the lifecycle test
            // records: a stamp's rectangle can be moved but not scaled.
            const float Shift = 0.25f;
            Assert.Equal(OkPdfium, set_annotation_bounds(
                handle, 0, index, Capture,
                (original.Left + Shift) * Capture, (original.Top + Shift) * Capture,
                (original.Right + Shift) * Capture, (original.Bottom + Shift) * Capture));

            Assert.Equal(OkPdfium, set_annotation_bounds(
                handle, 0, index, Capture,
                original.Left * Capture, original.Top * Capture,
                original.Right * Capture, original.Bottom * Capture));

            var restored = At(handle, index);
            Assert.Equal(original.Left, restored.Left, 4);
            Assert.Equal(original.Top, restored.Top, 4);
            Assert.Equal(original.Right, restored.Right, 4);
            Assert.Equal(original.Bottom, restored.Bottom, 4);
        }
        finally
        {
            close_document(handle);
        }
    }
}
