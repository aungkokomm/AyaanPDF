using System;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The document model built from shapes that were written by the REAL writer
/// and read back out of a REAL document.
///
/// This is the half that matters. <see cref="DocumentModelTests"/> proves the
/// model reads the tags it is handed; this proves those are the tags the writer
/// actually produces. A model that agrees with a hand-written fixture and
/// disagrees with render_core would be worse than no model at all, and nothing
/// short of a round trip catches that.
/// </summary>
public class DocumentModelInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

    /// <summary>Mirrors render_core::ShapeSpec, current shape.</summary>
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
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_id(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document_from_bytes(IntPtr data, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_annotation_id(
        ulong docHandle, int pageIndex, int index,
        [In] byte[] idUtf8, nuint idLen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int restyle_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        uint colorRgba, float widthPx, out int newIndex);

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static string? Text(ByteBuffer buf)
    {
        try
        {
            if (buf.Status != OkPdfium || buf.Data == IntPtr.Zero || buf.Len == 0) { return null; }
            byte[] bytes = new byte[(int)buf.Len];
            Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            free_byte_buffer(buf);
        }
    }

    /// <summary>
    /// Reads a page out of the document exactly the way the app does and builds
    /// the model from it: annotation bounds and subtype from get_annotations,
    /// the tag from get_annotation_contents, identity from get_annotation_id.
    /// </summary>
    private static PageModel ModelOf(ulong handle, int pageIndex)
    {
        var array = get_annotations(handle, pageIndex);
        try
        {
            Assert.Equal(OkPdfium, array.Status);
            int count = (int)array.Len;
            int stride = Marshal.SizeOf<AnnotationInfo>();

            var snapshots = new AnnotationSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<AnnotationInfo>(array.Items + (i * stride));
                string? hex = Text(get_annotation_id(handle, pageIndex, native.Index));
                Guid id = hex is { Length: 32 } && Guid.TryParseExact(hex, "N", out var parsed)
                    ? parsed
                    : Guid.Empty;

                snapshots[i] = new AnnotationSnapshot(
                    native.Index, native.Subtype,
                    native.Left, native.Top, native.Right, native.Bottom,
                    native.Opacity, id,
                    Text(get_annotation_contents(handle, pageIndex, native.Index)));
            }

            return DocumentModelBuilder.BuildPage(pageIndex, snapshots);
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    private static ShapeSpec Spec(
        ShapeKind kind, float x1, float y1, float x2, float y2,
        float rotation = 0f, uint fill = 0u, float radius = 0f) => new()
        {
            PageIndex = 0,
            Kind = (int)kind,
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            R = 200, G = 30, B = 30, A = 255,
            WidthPx = 3f,
            RotationDeg = rotation,
            FillRgba = fill,
            CornerRadiusPx = radius,
        };

    [Fact]
    public void every_shape_kind_the_writer_produces_is_modelled_as_that_kind()
    {
        // Written by render_core, read back by the model. If the tag format and
        // the reader ever drift apart, this is what notices.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.Rectangle, 50, 50, 200, 150),
                Spec(ShapeKind.Ellipse, 250, 50, 400, 150),
                Spec(ShapeKind.Line, 450, 50, 600, 150),
                Spec(ShapeKind.Arrow, 650, 50, 800, 150),
                Spec(ShapeKind.RoundedRectangle, 50, 250, 300, 400, radius: 40f),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, (nuint)specs.Length));

            var page = ModelOf(handle, 0);
            var shapes = page.Shapes.ToList();

            Assert.Equal(5, shapes.Count);
            Assert.Equal(
                [ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Line,
                 ShapeKind.Arrow, ShapeKind.RoundedRectangle],
                shapes.Select(s => s.ShapeKind));

            // Every one carries a real stroke and a sane width.
            foreach (var s in shapes)
            {
                Assert.Equal("#FFC81E1E", s.StrokeHex);
                Assert.True(s.StrokeWidthPts > 0, $"{s.ShapeKind} width was {s.StrokeWidthPts}");
                Assert.Equal(DocumentObjectKind.Shape, s.Kind);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_models_bounds_match_what_the_document_reports()
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.Rectangle, 100, 100, 500, 300) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));

            var page = ModelOf(handle, 0);
            var shape = page.Shapes.Single();

            // Drawn from (0.1,0.1) to (0.5,0.3) in normalized units, plus the
            // stroke padding the writer adds so PDFium does not clip it.
            Assert.InRange(shape.Bounds.Left, 0.08, 0.11);
            Assert.InRange(shape.Bounds.Right, 0.49, 0.52);
            Assert.InRange(shape.Bounds.Top, 0.08, 0.11);
            Assert.InRange(shape.Bounds.Bottom, 0.29, 0.32);
            Assert.True(shape.Bounds.Right > shape.Bounds.Left);
            Assert.True(shape.Bounds.Bottom > shape.Bounds.Top);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void rotation_fill_and_corner_radius_survive_into_the_model()
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 300,
                     rotation: 30f, fill: 0xFF112233, radius: 45f),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));

            var shape = ModelOf(handle, 0).Shapes.Single();

            Assert.Equal(ShapeKind.RoundedRectangle, shape.ShapeKind);
            Assert.Equal(30, shape.RotationDeg, 2);
            Assert.Equal("#FF112233", shape.FillHex);
            Assert.True(shape.CornerRadiusPts > 0,
                $"the radius came back as {shape.CornerRadiusPts}");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void z_order_in_the_model_is_the_order_they_were_written()
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.Rectangle, 50, 50, 200, 150),
                Spec(ShapeKind.Ellipse, 60, 60, 210, 160),
                Spec(ShapeKind.Arrow, 70, 70, 220, 170),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 3));

            var shapes = ModelOf(handle, 0).Shapes.ToList();

            Assert.Equal([0, 1, 2], shapes.Select(s => s.ZOrder));
            Assert.Equal(ShapeKind.Arrow, shapes[^1].ShapeKind);   // last written is on top
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_model_survives_a_save_and_reopen_unchanged()
    {
        // The model is built from persisted state, so it has to describe a
        // reopened document the same way it described the original.
        ulong handle = OpenFixture();
        var specs = new[]
        {
            Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 300, fill: 0xFF445566, radius: 40f),
            Spec(ShapeKind.Arrow, 600, 100, 800, 300),
        };
        Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 2));

        var before = ModelOf(handle, 0).Shapes.ToList();
        var saved = snapshot_document(handle);
        close_document(handle);
        Assert.Equal(OkPdfium, saved.Status);

        ulong reopened = open_document_from_bytes(saved.Data, saved.Len);
        Assert.NotEqual(0UL, reopened);
        try
        {
            var after = ModelOf(reopened, 0).Shapes.ToList();

            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].ShapeKind, after[i].ShapeKind);
                Assert.Equal(before[i].StrokeHex, after[i].StrokeHex);
                Assert.Equal(before[i].FillHex, after[i].FillHex);
                Assert.Equal(before[i].ZOrder, after[i].ZOrder);
                Assert.Equal(before[i].CornerRadiusPts, after[i].CornerRadiusPts, 3);
                Assert.Equal(before[i].StrokeWidthPts, after[i].StrokeWidthPts, 3);
            }
        }
        finally
        {
            close_document(reopened);
        }
    }

    [Fact]
    public void repeated_reorders_keep_every_objects_identity()
    {
        // The reported symptom: a z-order command works ONCE and then every
        // later command does nothing. That is what a lost identity looks like
        // from the outside. The engine works entirely in Guids, so an object
        // whose Guid stopped matching the page is invisible to the planner, the
        // plan comes back unchanged, and the command reports "already at the
        // back" rather than failing.
        //
        // This replays exactly what RaiseToTop does for a shape, three rounds
        // deep: rebuild via restyle, then re-stamp the identity onto whatever
        // index the rebuild reported.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.Rectangle, 50, 50, 200, 150),
                Spec(ShapeKind.Rectangle, 250, 50, 400, 150),
                Spec(ShapeKind.Rectangle, 450, 50, 600, 150),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 3));

            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            for (int i = 0; i < 3; i++)
            {
                byte[] hex = System.Text.Encoding.ASCII.GetBytes(ids[i].ToString("N"));
                Assert.Equal(OkPdfium, set_annotation_id(handle, 0, i, hex, (nuint)hex.Length));
            }

            for (int round = 1; round <= 3; round++)
            {
                // Raise whichever object is currently at the bottom, the way a
                // reorder rewrites the changed tail.
                var before = ModelOf(handle, 0).Objects;
                Guid moving = before[0].Id;
                Assert.NotEqual(Guid.Empty, moving);

                Assert.Equal(OkPdfium, restyle_shape_annotation(
                    handle, 0, before[0].ZOrder, Cap, 0, -1f, out int newIndex));

                byte[] hex = System.Text.Encoding.ASCII.GetBytes(moving.ToString("N"));
                Assert.Equal(OkPdfium, set_annotation_id(handle, 0, newIndex, hex, (nuint)hex.Length));

                var after = ModelOf(handle, 0).Objects;

                Assert.Equal(3, after.Count);
                Assert.All(after, o => Assert.NotEqual(Guid.Empty, o.Id));
                Assert.Equal(
                    ids.OrderBy(g => g).ToList(),
                    after.Select(o => o.Id).OrderBy(g => g).ToList());
                Assert.Equal(moving, after[^1].Id);
            }
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_shape_the_app_has_not_stamped_yet_reports_no_identity()
    {
        // A true property of the system, worth pinning: the WRITER does not
        // assign identity. add_shape_annotations emits the shape tag only, and
        // the app stamps the Guid afterwards. The model must report that
        // honestly rather than inventing an id, because an invented one would
        // differ on every read and silently break anything resolving by Guid.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.Rectangle, 100, 100, 500, 300) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));

            Assert.Equal(Guid.Empty, ModelOf(handle, 0).Shapes.Single().Id);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void identity_stamped_on_the_annotation_reaches_the_model()
    {
        // Everything the app does to an object resolves it by Guid, so a model
        // that lost identity would be useless for anything later. Stamped here
        // the same way the app stamps it, through the same FFI.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.Rectangle, 100, 100, 500, 300) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));

            var expected = Guid.NewGuid();
            byte[] hex = System.Text.Encoding.ASCII.GetBytes(expected.ToString("N"));
            Assert.Equal(OkPdfium, set_annotation_id(handle, 0, 0, hex, (nuint)hex.Length));

            var shape = ModelOf(handle, 0).Shapes.Single();
            Assert.Equal(expected, shape.Id);

            // Modelled twice, the same object must come back under the same
            // identity, and be findable by it.
            var rebuilt = ModelOf(handle, 0);
            Assert.Equal(expected, rebuilt.Shapes.Single().Id);
            Assert.NotNull(rebuilt.ById(expected));
        }
        finally
        {
            close_document(handle);
        }
    }
}
