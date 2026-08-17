using System;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Rounded rectangle across the real FFI boundary, plus the pure geometry the
/// live preview draws with.
///
/// A separate file from <see cref="ShapeInteropTests"/> on purpose: that file
/// declares its own shape struct that has been stale since RotationDeg was
/// added, so its tests fail for reasons that have nothing to do with this
/// feature. The struct below is the CURRENT shape of the native one, which is
/// the only way this file can prove anything about the boundary.
/// </summary>
public class RoundedRectangleTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;

    /// <summary>Mirrors render_core::ShapeSpec, including corner_radius_px.</summary>
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
        public float ShadowDxPx;
        public float ShadowDyPx;
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
    private static extern ulong open_document_from_bytes(IntPtr data, nuint len);

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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int delete_annotation(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer snapshot_document(ulong docHandle);

    private const int Cap = 1000;

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static ShapeSpec Spec(ShapeKind kind, float x1, float y1, float x2, float y2, float radius) => new()
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
        RotationDeg = 0f,
        FillRgba = 0u,
        CornerRadiusPx = radius,
    };

    private static string? Contents(ulong handle, int index)
    {
        var buf = get_annotation_contents(handle, 0, index);
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

    private static int AnnotationCount(ulong handle)
    {
        var array = get_annotations(handle, 0);
        int count = (int)array.Len;
        free_annotation_array(array);
        return count;
    }

    // ---------------- The boundary ----------------

    [Fact]
    public void the_shape_struct_is_the_size_the_native_side_now_writes()
    {
        // Two ints, four floats, four bytes, then width, rotation, fill and
        // radius. Getting this wrong does not error: it silently feeds the
        // native side garbage coordinates.
        Assert.Equal(56, Marshal.SizeOf<ShapeSpec>());
    }

    [Fact]
    public void the_rounded_rectangle_kind_number_matches_the_native_constant()
    {
        // Appended as 4. Renumbering would reinterpret every saved rounded
        // rectangle as some other shape on the next load.
        Assert.Equal(4, (int)ShapeKind.RoundedRectangle);
    }

    [Fact]
    public void drawing_one_creates_an_annotation_that_reloads_as_a_rounded_rectangle()
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 350, 60f) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));
            Assert.Equal(1, AnnotationCount(handle));

            string? tag = Contents(handle, 0);
            Assert.NotNull(tag);
            // ID:<guid>|AyaanShape:4:<rgba>:<width>:<fx>:<fy>:<rot>:<fill>:<radius>
            Assert.Contains("AyaanShape:4:", tag);

            string body = tag![tag.IndexOf("AyaanShape:", StringComparison.Ordinal)..];
            double radius = double.Parse(body.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(radius > 0, $"the radius was not written to the tag: {tag}");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void it_survives_a_save_and_reload_with_its_radius()
    {
        ulong handle = OpenFixture();
        var specs = new[] { Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 350, 60f) };
        Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));
        string? before = Contents(handle, 0);

        var saved = snapshot_document(handle);
        close_document(handle);
        Assert.Equal(OkPdfium, saved.Status);

        ulong reopened = open_document_from_bytes(saved.Data, saved.Len);
        Assert.NotEqual(0UL, reopened);
        try
        {
            string? after = Contents(reopened, 0);
            Assert.NotNull(after);
            Assert.Equal(before, after);

            // The radius field, last in the tag, must be a real positive number
            // rather than the 0 an older reader would have written.
            string body = after![after.IndexOf("AyaanShape:", StringComparison.Ordinal)..];
            double radius = double.Parse(body.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(radius > 0, $"radius reloaded as {radius} from {after}");
        }
        finally
        {
            close_document(reopened);
        }
    }

    [Fact]
    public void resizing_one_keeps_it_a_rounded_rectangle()
    {
        // Resize is a delete-and-rebuild from the tag, so any field the rebuild
        // forgets is lost on the first drag of a corner handle.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 350, 60f) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));

            Assert.Equal(OkPdfium,
                resize_shape_annotation(handle, 0, 0, Cap, 120, 120, 700, 520, out int newIndex));

            string? tag = Contents(handle, newIndex);
            Assert.NotNull(tag);
            Assert.Contains("AyaanShape:4:", tag);

            string body = tag![tag.IndexOf("AyaanShape:", StringComparison.Ordinal)..];
            double radius = double.Parse(body.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(radius > 0, $"the resize dropped the radius: {tag}");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void deleting_one_removes_it()
    {
        ulong handle = OpenFixture();
        try
        {
            var specs = new[] { Spec(ShapeKind.RoundedRectangle, 100, 100, 500, 350, 60f) };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, 1));
            Assert.Equal(1, AnnotationCount(handle));

            Assert.Equal(OkPdfium, delete_annotation(handle, 0, 0));
            Assert.Equal(0, AnnotationCount(handle));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void several_rounded_rectangles_all_arrive_for_multi_select_and_grouping()
    {
        // Grouping, aligning and group-moving all operate on a multi-selection
        // of annotations that came back from the page, so the batch write has to
        // produce one annotation per spec.
        ulong handle = OpenFixture();
        try
        {
            var specs = new[]
            {
                Spec(ShapeKind.RoundedRectangle, 50, 50, 200, 150, 20f),
                Spec(ShapeKind.RoundedRectangle, 250, 50, 400, 150, 20f),
                Spec(ShapeKind.Rectangle, 450, 50, 600, 150, 0f),
            };
            Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, specs, (nuint)specs.Length));
            Assert.Equal(3, AnnotationCount(handle));
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- The geometry the preview draws ----------------

    [Fact]
    public void the_default_radius_is_a_fraction_of_the_shorter_side()
    {
        Assert.Equal(0.18 * 100, ShapeGeometry.DefaultCornerRadius(400, 100), 6);
        Assert.Equal(0.18 * 80, ShapeGeometry.DefaultCornerRadius(80, 500), 6);
    }

    [Fact]
    public void the_radius_is_clamped_to_half_the_shorter_side()
    {
        Assert.Equal(30, ShapeGeometry.ClampCornerRadius(30, 400, 100), 6);
        Assert.Equal(50, ShapeGeometry.ClampCornerRadius(500, 400, 100), 6);
        Assert.Equal(20, ShapeGeometry.ClampCornerRadius(500, 40, 900), 6);
    }

    [Fact]
    public void a_negative_or_nan_radius_draws_square_corners_rather_than_failing()
    {
        Assert.Equal(0, ShapeGeometry.ClampCornerRadius(-10, 400, 100), 6);
        Assert.Equal(0, ShapeGeometry.ClampCornerRadius(double.NaN, 400, 100), 6);
    }

    [Fact]
    public void the_default_radius_can_never_exceed_the_clamp()
    {
        // The two rules have to agree, or a freshly drawn shape would be clamped
        // on its very first render and look different from its own preview.
        foreach (var (w, h) in new[] { (10.0, 10.0), (400.0, 5.0), (3.0, 900.0), (100.0, 100.0) })
        {
            double def = ShapeGeometry.DefaultCornerRadius(w, h);
            Assert.Equal(def, ShapeGeometry.ClampCornerRadius(def, w, h), 9);
        }
    }

    [Fact]
    public void the_draft_itself_carries_the_radius_the_writers_must_use()
    {
        // The regression this pins: the radius used to be worked out separately
        // at each write site, and one of the three simply did not do it, so the
        // preview rounded the shape and the annotation came out square. There is
        // now ONE definition, on the draft, and every writer reads it from here.
        var rounded = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5);
        Assert.True(rounded.CornerRadius > 0, "a rounded rectangle reported no radius");
        Assert.Equal(
            ShapeGeometry.DefaultCornerRadius(rounded.Width, rounded.Height),
            rounded.CornerRadius, 9);

        // And it is exactly what the preview traces, so the two cannot drift.
        double r = rounded.CornerRadius;
        double nearest = double.MaxValue;
        foreach (var (x, y) in ShapeGeometry.Outline(rounded, width: 0.004))
        {
            double dx = x - rounded.Left;
            double dy = y - rounded.Top;
            nearest = Math.Min(nearest, Math.Sqrt((dx * dx) + (dy * dy)));
        }
        Assert.Equal(r * (Math.Sqrt(2) - 1), nearest, 4);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void every_other_kind_reports_no_radius(ShapeKind kind)
    {
        // Guards the other direction: a stray radius on a plain rectangle would
        // silently round every shape in the app.
        var draft = new ShapeDraft(kind, 0.2, 0.3, 0.6, 0.5);
        Assert.Equal(0, draft.CornerRadius);
    }

    [Fact]
    public void the_corner_slider_shows_for_a_selected_rounded_rectangle()
    {
        Assert.True(ShapePropertyBar.ShouldShowCornerRadius(
            selectionIsRoundedRect: true,
            toolDrawsShapes: false,
            activeShapeKind: ShapeKind.Rectangle,
            anythingSelected: true));
    }

    [Fact]
    public void the_corner_slider_shows_when_the_rounded_tool_is_armed_with_nothing_selected()
    {
        Assert.True(ShapePropertyBar.ShouldShowCornerRadius(
            selectionIsRoundedRect: false,
            toolDrawsShapes: true,
            activeShapeKind: ShapeKind.RoundedRectangle,
            anythingSelected: false));
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void the_corner_slider_hides_for_every_other_shape_kind(ShapeKind kind)
    {
        Assert.False(ShapePropertyBar.ShouldShowCornerRadius(
            selectionIsRoundedRect: false,
            toolDrawsShapes: true,
            activeShapeKind: kind,
            anythingSelected: false));
    }

    [Fact]
    public void the_corner_slider_hides_when_some_other_object_is_selected()
    {
        // Otherwise it would look like it describes the selection while really
        // setting a default for the next shape.
        Assert.False(ShapePropertyBar.ShouldShowCornerRadius(
            selectionIsRoundedRect: false,
            toolDrawsShapes: true,
            activeShapeKind: ShapeKind.RoundedRectangle,
            anythingSelected: true));
    }

    [Fact]
    public void the_slider_fraction_and_the_radius_convert_back_and_forth()
    {
        // The slider stores a percentage but the tag stores a length, so the
        // two conversions have to be exact inverses. If they are not, selecting
        // a shape and letting go of the slider would nudge its corners.
        foreach (double pct in new[] { 0.0, 25.0, 36.0, 50.0, 100.0 })
        {
            double radius = ShapeGeometry.CornerRadiusFromFraction(pct / 100, 0.4, 0.2);
            double back = ShapeGeometry.CornerFractionFromRadius(radius, 0.4, 0.2) * 100;
            Assert.Equal(pct, back, 6);
        }
    }

    [Fact]
    public void the_slider_at_full_gives_the_roundest_box_that_can_be_drawn()
    {
        // 100% is half the shorter side, the stadium. Anything more would cross
        // the corner arcs, so the top of the slider and the clamp must agree.
        double radius = ShapeGeometry.CornerRadiusFromFraction(1.0, 0.4, 0.2);
        Assert.Equal(ShapeGeometry.MaxCornerRadius(0.4, 0.2), radius, 9);
        Assert.Equal(radius, ShapeGeometry.ClampCornerRadius(radius, 0.4, 0.2), 9);
    }

    [Fact]
    public void the_slider_at_zero_gives_square_corners()
    {
        Assert.Equal(0, ShapeGeometry.CornerRadiusFromFraction(0, 0.4, 0.2), 9);

        var draft = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5)
        {
            CornerFraction = 0,
        };
        Assert.Equal(0, draft.CornerRadius);
        // And the preview degrades to the five points of a plain rectangle.
        Assert.Equal(5, ShapeGeometry.Outline(draft, width: 0.004).Count);
    }

    [Fact]
    public void the_draft_honours_the_slider_rather_than_the_built_in_default()
    {
        // The setting has to travel ON the draft. If the preview read the slider
        // but the writer used the default (or the reverse), the shape would
        // change the moment the pointer lifted, which is the bug that shipped
        // when rounded rectangles were first wired up.
        var half = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5)
        {
            CornerFraction = 0.5,
        };
        Assert.Equal(ShapeGeometry.MaxCornerRadius(half.Width, half.Height) * 0.5,
            half.CornerRadius, 9);

        var full = half with { CornerFraction = 1.0 };
        Assert.True(full.CornerRadius > half.CornerRadius);
    }

    [Fact]
    public void a_draft_cloned_during_a_drag_keeps_its_corner_setting()
    {
        // ExtendShape rebuilds the draft with `with` on every pointer move. A
        // corner setting dropped there would make the preview flicker back to
        // the default as soon as the drag moved a pixel.
        var start = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.2, 0.3)
        {
            CornerFraction = 0.75,
        };
        var dragged = start with { X2 = 0.6, Y2 = 0.5 };

        Assert.Equal(0.75, dragged.CornerFraction, 9);
    }

    [Fact]
    public void an_out_of_range_fraction_is_pulled_back_rather_than_throwing()
    {
        double max = ShapeGeometry.MaxCornerRadius(0.4, 0.2);
        Assert.Equal(max, ShapeGeometry.CornerRadiusFromFraction(5.0, 0.4, 0.2), 9);
        Assert.Equal(0, ShapeGeometry.CornerRadiusFromFraction(-2.0, 0.4, 0.2), 9);
        Assert.Equal(0, ShapeGeometry.CornerRadiusFromFraction(double.NaN, 0.4, 0.2), 9);
    }

    [Fact]
    public void a_zero_sized_box_reports_no_fraction_instead_of_dividing_by_zero()
    {
        Assert.Equal(0, ShapeGeometry.CornerFractionFromRadius(10, 0, 0), 9);
    }

    [Fact]
    public void the_preview_outline_is_closed_and_stays_inside_the_drawn_box()
    {
        var draft = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5);
        var outline = ShapeGeometry.Outline(draft, width: 0.004);

        Assert.True(outline.Count > 8, $"only {outline.Count} points; corners are not being traced");
        Assert.Equal(outline[0], outline[^1]);

        foreach (var (x, y) in outline)
        {
            Assert.InRange(x, draft.Left - 1e-9, draft.Right + 1e-9);
            Assert.InRange(y, draft.Top - 1e-9, draft.Bottom + 1e-9);
        }
    }

    [Fact]
    public void the_preview_cuts_the_corners_off_the_box()
    {
        // The visible difference from a plain rectangle. The arc's closest
        // approach to the box corner is r*(sqrt2 - 1), about 0.414r, so no
        // outline point may come nearer than that. A plain rectangle has a point
        // AT the corner, distance zero, which is the control below: without it
        // this test would pass for a shape that was never rounded at all.
        var box = (Left: 0.2, Top: 0.3, Right: 0.6, Bottom: 0.5);
        var rounded = new ShapeDraft(ShapeKind.RoundedRectangle, box.Left, box.Top, box.Right, box.Bottom);
        var square = new ShapeDraft(ShapeKind.Rectangle, box.Left, box.Top, box.Right, box.Bottom);

        double r = ShapeGeometry.DefaultCornerRadius(rounded.Width, rounded.Height);
        double clearance = r * (Math.Sqrt(2) - 1);

        double NearestToCorner(ShapeDraft d)
        {
            double best = double.MaxValue;
            foreach (var (x, y) in ShapeGeometry.Outline(d, width: 0.004))
            {
                double dx = x - box.Left;
                double dy = y - box.Top;
                best = Math.Min(best, Math.Sqrt((dx * dx) + (dy * dy)));
            }
            return best;
        }

        Assert.True(NearestToCorner(square) < 1e-9,
            "the control rectangle should touch its own corner");
        Assert.True(NearestToCorner(rounded) >= clearance * 0.99,
            $"the rounded outline came within {NearestToCorner(rounded):F5} of the corner, "
            + $"but the arc should keep {clearance:F5} clear");
    }

    [Fact]
    public void a_box_too_thin_to_round_previews_as_a_plain_rectangle()
    {
        var draft = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.3);
        var outline = ShapeGeometry.Outline(draft, width: 0.004);

        Assert.Equal(5, outline.Count);
        Assert.Equal(outline[0], outline[^1]);
    }

    [Fact]
    public void a_rounded_rectangle_is_grabbed_anywhere_inside_it_like_a_rectangle()
    {
        // Selection, and therefore multi-select and grouping, depends on this.
        var shape = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5), "#FF0000", 0.004);

        Assert.True(shape.HitTest(0.4, 0.4, 0.001), "the middle of the shape did not select it");
        Assert.False(shape.HitTest(0.9, 0.9, 0.001), "a click far outside selected it");
    }

    [Fact]
    public void a_rounded_rectangle_moves_by_the_offset_it_is_given()
    {
        var shape = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5), "#FF0000", 0.004);

        var moved = (ShapeAnnotation)shape.Translate(0.1, -0.05);

        Assert.Equal(0.3, moved.Draft.X1, 9);
        Assert.Equal(0.25, moved.Draft.Y1, 9);
        Assert.Equal(0.7, moved.Draft.X2, 9);
        Assert.Equal(0.45, moved.Draft.Y2, 9);
        Assert.Equal(ShapeKind.RoundedRectangle, moved.Draft.Kind);
        Assert.Equal(shape.Id, moved.Id);
    }
}
