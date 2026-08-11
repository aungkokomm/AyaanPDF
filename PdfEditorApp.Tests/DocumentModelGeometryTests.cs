using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The model's GEOMETRY, as opposed to its structure.
///
/// An annotation's /Rect is not the object. The writer grows it by the stroke
/// pad so PDFium will not clip the stroke, and for anything turned it grows
/// again to the axis-aligned box of the rotated content. Every one of these
/// tests is about getting back from that rectangle to the shape the user drew,
/// which is the thing a hit test has to measure against.
///
/// The reconstruction mirrors <c>resize_shape_annotation_inner</c> in
/// render_core, because that is the code that already has to undo the same two
/// additions in order to redraw a shape at a new size. If these two ever
/// disagree, the model and the document disagree.
/// </summary>
public class DocumentModelGeometryTests
{
    /// <summary>A round page width, so points convert to normalized units by a
    /// factor of a thousand and every expectation below can be read off by eye.</summary>
    private const double PageWidthPts = 1000;

    private static AnnotationSnapshot Snap(
        string? contents, double left, double top, double right, double bottom,
        int index = 0, int subtype = 4) =>
        new(index, subtype, left, top, right, bottom, 1.0, Guid.NewGuid(), contents);

    private static ShapeObject Shape(
        string tag, double left, double top, double right, double bottom,
        double pageWidthPts = PageWidthPts) =>
        Assert.IsType<ShapeObject>(
            DocumentModelBuilder.BuildPage(
                0, [Snap(tag, left, top, right, bottom)], pageWidthPts).Objects[0]);

    private static OpaqueObject Opaque(
        string? tag, int subtype = 4,
        double left = 0.1, double top = 0.1, double right = 0.5, double bottom = 0.3) =>
        Assert.IsType<OpaqueObject>(
            DocumentModelBuilder.BuildPage(
                0, [Snap(tag, left, top, right, bottom, subtype: subtype)], PageWidthPts).Objects[0]);

    // ---------------- The tag fields that were being dropped ----------------

    [Fact]
    public void a_turned_shapes_upright_size_is_read_off_its_tag()
    {
        // render_core has always written these two fields and read them back.
        // The C# reader stopped at the corner radius, so the size a rotated
        // shape needs in order to be reconstructible was parsed by the writer's
        // own language and thrown away by this one.
        Assert.True(ShapeTagReader.TryParse(
            "AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000:100.0000:50.0000", out var t));

        Assert.Equal(100, t.BoxWidthPts, 4);
        Assert.Equal(50, t.BoxHeightPts, 4);
    }

    [Theory]
    // Older tags simply stop before these fields.
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000")]
    // A zero or negative size is not a size. Rust filters these out too, so
    // "recorded" and "usable" mean the same thing on both sides of the FFI.
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000:0.0000:0.0000")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000:-5.0000:-5.0000")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000:zzz:zzz")]
    public void an_absent_or_unusable_upright_size_reads_as_not_recorded(string tag)
    {
        Assert.True(ShapeTagReader.TryParse(tag, out var t));
        Assert.Equal(0, t.BoxWidthPts);
        Assert.Equal(0, t.BoxHeightPts);
    }

    [Fact]
    public void a_stamps_angle_and_upright_rect_are_read_and_normalized()
    {
        // The tag stores the rect in CAPTURE pixels; everything downstream works
        // in normalized units. Converting once, in the reader, is the difference
        // between a hit test and a factor of a thousand.
        Assert.True(StampTagReader.TryParse(
            "AyaanStamp:30.00:100.0000:200.0000:500.0000:400.0000", out var t));

        Assert.Equal(30, t.RotationDeg, 4);
        Assert.Equal(new TextRect(0.1, 0.2, 0.5, 0.4), t.Bounds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Please review this")]
    [InlineData("AyaanStamp:")]
    [InlineData("AyaanStamp:0.00:1:2:3")]          // too few fields
    [InlineData("AyaanStamp:zz:1:2:3:4")]          // unparseable angle
    [InlineData("AyaanShape:0:FF0000FF:2.0:1:1")]  // a shape is not a stamp
    public void anything_that_is_not_a_stamp_tag_is_refused(string? contents)
    {
        Assert.False(StampTagReader.TryParse(contents, out _));
    }

    [Fact]
    public void a_stamp_tag_is_read_through_its_identity_prefix()
    {
        // Every tag reader in this app has to strip the prefix. One that only
        // worked on pre-stripped input is what made every shape stop being
        // recognised as a shape for a fortnight.
        Assert.True(StampTagReader.TryParse(
            "ID:0123456789abcdef0123456789abcdef|AyaanStamp:0.00:100:200:500:400", out var t));
        Assert.Equal(new TextRect(0.1, 0.2, 0.5, 0.4), t.Bounds);
    }

    // ---------------- Undoing the stroke pad ----------------

    [Fact]
    public void an_upright_shapes_stroke_pad_is_taken_back_off()
    {
        // The writer grew /Rect by width/2 + 1 points on every side. On a
        // 1000pt page a 2pt stroke is 0.002 of the page width, so a shape drawn
        // from 0.1 to 0.5 reports a /Rect of 0.098 to 0.502.
        var s = Shape("AyaanShape:0:FF0000FF:2.0000:1:1", 0.098, 0.098, 0.502, 0.302);

        Assert.Equal(0.100, s.UprightBounds.Left, 9);
        Assert.Equal(0.100, s.UprightBounds.Top, 9);
        Assert.Equal(0.500, s.UprightBounds.Right, 9);
        Assert.Equal(0.300, s.UprightBounds.Bottom, 9);

        // Bounds itself is untouched: the move and resize paths are threaded on
        // /Rect and must keep seeing it.
        Assert.Equal(new TextRect(0.098, 0.098, 0.502, 0.302), s.Bounds);
    }

    [Fact]
    public void the_upright_drag_keeps_its_direction_so_an_arrow_still_points()
    {
        // fx=0, fy=0: the drag ran right to left and bottom to top. The
        // de-padded box must come back the same way round, or an arrow rebuilt
        // from it would point backwards.
        var s = Shape("AyaanShape:3:FF0000FF:2.0000:0:0", 0.098, 0.098, 0.502, 0.302);

        Assert.Equal(0.500, s.UprightGeometry.X1, 9);
        Assert.Equal(0.100, s.UprightGeometry.X2, 9);
        Assert.Equal(0.300, s.UprightGeometry.Y1, 9);
        Assert.Equal(0.100, s.UprightGeometry.Y2, 9);
    }

    [Fact]
    public void a_shape_thinner_than_its_own_pad_is_not_turned_inside_out()
    {
        // A hairline shape dragged very small has a pad wider than the shape.
        // Insetting by the full pad would invert the box, and an inverted box
        // is an object that cannot be hit anywhere at all.
        var s = Shape("AyaanShape:0:FF0000FF:40.0000:1:1", 0.100, 0.100, 0.104, 0.104);

        Assert.True(s.UprightBounds.Right >= s.UprightBounds.Left);
        Assert.True(s.UprightBounds.Bottom >= s.UprightBounds.Top);
    }

    // ---------------- Reconstructing a turned shape ----------------

    [Fact]
    public void a_turned_shape_is_rebuilt_from_its_centre_and_its_recorded_size()
    {
        // A 100 x 50pt box turned 45 degrees reports a /Rect of about 106 x 106
        // points: the axis-aligned box of the turned content. That cannot be
        // inverted (at 45 degrees infinitely many boxes share one AABB), so the
        // upright size comes off the tag and the centre comes off /Rect, which
        // IS exact at every angle.
        var s = Shape(
            "AyaanShape:0:FF0000FF:2.0000:1:1:45.00:00000000:0.0000:100.0000:50.0000",
            0.24697, 0.14697, 0.35303, 0.25303);

        Assert.Equal(0.25, s.UprightBounds.Left, 6);
        Assert.Equal(0.175, s.UprightBounds.Top, 6);
        Assert.Equal(0.35, s.UprightBounds.Right, 6);
        Assert.Equal(0.225, s.UprightBounds.Bottom, 6);

        // 0.1 x 0.05 of the page width, which is the 100 x 50 points it was
        // drawn as, and nothing like the 0.106 square /Rect reports.
        Assert.Equal(0.10, s.UprightBounds.Right - s.UprightBounds.Left, 6);
        Assert.Equal(0.05, s.UprightBounds.Bottom - s.UprightBounds.Top, 6);
    }

    [Fact]
    public void a_turned_shape_that_never_recorded_its_size_keeps_its_rect()
    {
        // Written before the upright size existed. There is nothing to
        // reconstruct from, and inventing one would be worse than the padded
        // rectangle: the core makes the same choice.
        var s = Shape("AyaanShape:1:00FF00CC:1.0000:1:1:45.00", 0.1, 0.1, 0.5, 0.3);

        Assert.Equal(s.Bounds, s.UprightBounds);
    }

    [Fact]
    public void without_a_page_width_the_model_reports_exactly_what_it_did_before()
    {
        // Points cannot be turned into normalized units without the page width,
        // so a caller that does not supply one gets the old answer rather than a
        // guessed scale. This is what keeps every existing caller correct.
        var s = Shape("AyaanShape:0:FF0000FF:2.0000:1:1", 0.098, 0.098, 0.502, 0.302,
                      pageWidthPts: 0);

        Assert.Equal(s.Bounds, s.UprightBounds);
        Assert.Equal(s.Geometry, s.UprightGeometry);
    }

    [Fact]
    public void the_page_width_is_carried_on_the_page_and_on_every_object()
    {
        var page = DocumentModelBuilder.BuildPage(
            0, [Snap("AyaanShape:0:FF0000FF:2.0000:1:1", 0.1, 0.1, 0.5, 0.3)], PageWidthPts);

        Assert.Equal(PageWidthPts, page.WidthPts);
        Assert.Equal(PageWidthPts, page.Objects[0].PageWidthPts);
    }

    // ---------------- Objects the model does not describe ----------------

    [Fact]
    public void a_turned_text_box_reports_its_angle_and_its_own_tight_rect()
    {
        // size:rgba:align:fill:outline:outlineW:flags:font:rotation:l:t:r:b:text
        string tag = "AyaanTextB:24:FF0000FF:0:00000000:00000000:0:0::30:0.2:0.2:0.4:0.3:"
                     + Convert.ToBase64String("hi"u8.ToArray());

        var o = Opaque(tag, left: 0.15, top: 0.15, right: 0.45, bottom: 0.35);

        Assert.Equal(DocumentObjectKind.TextBox, o.Kind);
        Assert.Equal(30, o.RotationDeg, 6);
        Assert.Equal(new TextRect(0.2, 0.2, 0.4, 0.3), o.UprightBounds);
    }

    [Fact]
    public void a_turned_stamp_reports_its_angle_and_the_box_it_was_placed_in()
    {
        var o = Opaque("AyaanStamp:30.00:200.0000:200.0000:400.0000:300.0000",
                       left: 0.15, top: 0.15, right: 0.45, bottom: 0.35);

        Assert.Equal(DocumentObjectKind.Stamp, o.Kind);
        Assert.Equal(30, o.RotationDeg, 6);
        Assert.Equal(new TextRect(0.2, 0.2, 0.4, 0.3), o.UprightBounds);
    }

    [Fact]
    public void an_object_with_nothing_better_to_say_falls_back_to_its_rect()
    {
        var o = Opaque("Please review this", subtype: PdfAnnotationSubtype.Text);

        Assert.Equal(0, o.RotationDeg);
        Assert.Equal(o.Bounds, o.UprightBounds);
    }

    [Fact]
    public void an_untagged_stamp_is_upright_and_keeps_its_rect()
    {
        // Placed before stamps carried tags, which means upright by definition.
        var o = Opaque(tag: null, subtype: PdfAnnotationSubtype.Stamp);

        Assert.Equal(0, o.RotationDeg);
        Assert.Equal(o.Bounds, o.UprightBounds);
    }

    // ---------------- Ink ----------------

    [Fact]
    public void our_own_ink_gives_up_its_points()
    {
        // A stroke is the one object whose shape really is a point cloud. Its
        // /Rect says nothing useful: a diagonal scribble fills very little of it.
        var o = Opaque("AyaanInk:FF0000FF:0.01:0.1,0.1;0.3,0.2;0.5,0.5",
                       subtype: PdfAnnotationSubtype.Ink);

        Assert.Equal(DocumentObjectKind.Ink, o.Kind);
        Assert.Equal(3, o.InkPoints.Count);
        Assert.Equal((0.1, 0.1), o.InkPoints[0]);
        Assert.Equal((0.5, 0.5), o.InkPoints[2]);
        Assert.Equal(0.01, o.InkStrokeWidth, 6);
    }

    [Fact]
    public void ink_from_another_editor_has_no_points_to_offer()
    {
        var o = Opaque(tag: null, subtype: PdfAnnotationSubtype.Ink);

        Assert.Equal(DocumentObjectKind.Ink, o.Kind);
        Assert.Empty(o.InkPoints);
        Assert.Equal(0, o.InkStrokeWidth);
    }

    [Fact]
    public void only_ink_carries_points()
    {
        var s = Shape("AyaanShape:0:FF0000FF:2.0000:1:1", 0.1, 0.1, 0.5, 0.3);
        Assert.IsType<ShapeObject>(s);

        var stamp = Opaque("AyaanStamp:0.00:100:100:500:300");
        Assert.Empty(stamp.InkPoints);
    }
}
