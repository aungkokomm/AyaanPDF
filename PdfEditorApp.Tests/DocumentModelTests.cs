using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The read-only document model, built from annotation data.
///
/// These are the pure half: given the tag strings the writer produces, does the
/// model describe the object correctly? The other half lives in
/// <see cref="DocumentModelInteropTests"/>, which writes real shapes through
/// the real FFI and builds the model from what comes back, so the two together
/// cover both "the parser is right" and "the parser agrees with the writer".
/// </summary>
public class DocumentModelTests
{
    private static AnnotationSnapshot Snap(
        int index, string? contents, int subtype = 4,
        double left = 0.1, double top = 0.2, double right = 0.5, double bottom = 0.4,
        Guid id = default, double opacity = 1.0) =>
        new(index, subtype, left, top, right, bottom, opacity,
            id == default ? Guid.NewGuid() : id, contents);

    // Tags exactly as shape_tag in render_core emits them, shortest form first.
    private const string PlainRect = "AyaanShape:0:FF0000FF:2.5000:1:1";
    private const string RotatedEllipse = "AyaanShape:1:00FF00CC:1.0000:1:1:45.00";
    private const string FilledLine = "AyaanShape:2:0000FFFF:3.0000:0:1:0.00:FF112233";
    private const string Arrow = "AyaanShape:3:112233FF:4.0000:0:0";
    private const string RoundedRect = "AyaanShape:4:DC0000FF:1.8360:1:1:0.00:FFDC0000:91.8000";

    // ---------------- Every shape kind is represented ----------------

    [Theory]
    [InlineData(PlainRect, ShapeKind.Rectangle)]
    [InlineData(RotatedEllipse, ShapeKind.Ellipse)]
    [InlineData(FilledLine, ShapeKind.Line)]
    [InlineData(Arrow, ShapeKind.Arrow)]
    [InlineData(RoundedRect, ShapeKind.RoundedRectangle)]
    public void every_shape_kind_becomes_a_shape_object_of_that_kind(string tag, ShapeKind expected)
    {
        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, tag)]);

        var shape = Assert.IsType<ShapeObject>(Assert.Single(page.Objects));
        Assert.Equal(DocumentObjectKind.Shape, shape.Kind);
        Assert.Equal(expected, shape.ShapeKind);
        Assert.Equal(expected, shape.Geometry.Kind);
    }

    [Fact]
    public void a_rectangles_every_modelled_property_comes_off_its_tag()
    {
        var id = Guid.NewGuid();
        var page = DocumentModelBuilder.BuildPage(
            3, [Snap(7, PlainRect, id: id, opacity: 0.5,
                     left: 0.1, top: 0.2, right: 0.5, bottom: 0.4)]);

        var s = Assert.IsType<ShapeObject>(page.Objects[0]);

        Assert.Equal(id, s.Id);
        Assert.Equal(3, s.PageIndex);
        Assert.Equal(7, s.ZOrder);
        Assert.Equal(new TextRect(0.1, 0.2, 0.5, 0.4), s.Bounds);
        Assert.Equal(0.5, s.Opacity, 6);
        Assert.Equal("#FFFF0000", s.StrokeHex);   // tag stores RRGGBBAA, app uses AARRGGBB
        Assert.Equal(2.5, s.StrokeWidthPts, 6);
        Assert.Null(s.FillHex);
        Assert.Equal(0, s.RotationDeg);
        Assert.Equal(0, s.CornerRadiusPts);
        Assert.Equal(PlainRect, s.RawTag);
    }

    [Fact]
    public void rotation_is_read_from_a_tag_that_carries_one()
    {
        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, RotatedEllipse)]);
        var s = Assert.IsType<ShapeObject>(page.Objects[0]);

        Assert.Equal(45, s.RotationDeg, 6);
        Assert.Equal("#CC00FF00", s.StrokeHex);
    }

    [Fact]
    public void fill_is_read_and_a_stroke_only_shape_reports_none()
    {
        var filled = DocumentModelBuilder.BuildPage(0, [Snap(0, FilledLine)]);
        Assert.Equal("#FF112233", Assert.IsType<ShapeObject>(filled.Objects[0]).FillHex);

        var strokeOnly = DocumentModelBuilder.BuildPage(0, [Snap(0, PlainRect)]);
        Assert.Null(Assert.IsType<ShapeObject>(strokeOnly.Objects[0]).FillHex);
    }

    [Fact]
    public void an_all_zero_fill_is_stroke_only_not_transparent_black()
    {
        var page = DocumentModelBuilder.BuildPage(
            0, [Snap(0, "AyaanShape:0:FF0000FF:2.0000:1:1:0.00:00000000")]);

        Assert.Null(Assert.IsType<ShapeObject>(page.Objects[0]).FillHex);
    }

    [Fact]
    public void the_corner_radius_is_read_for_a_rounded_rectangle_only()
    {
        var rounded = DocumentModelBuilder.BuildPage(0, [Snap(0, RoundedRect)]);
        Assert.Equal(91.8, Assert.IsType<ShapeObject>(rounded.Objects[0]).CornerRadiusPts, 4);

        var plain = DocumentModelBuilder.BuildPage(0, [Snap(0, PlainRect)]);
        Assert.Equal(0, Assert.IsType<ShapeObject>(plain.Objects[0]).CornerRadiusPts);
    }

    [Fact]
    public void the_drag_direction_survives_so_an_arrow_still_points_where_it_pointed()
    {
        // Arrow's tag has fx=0, fy=0: the drag ran right-to-left and
        // bottom-to-top, so X1 must be the RIGHT edge, not the left.
        var page = DocumentModelBuilder.BuildPage(
            0, [Snap(0, Arrow, left: 0.1, top: 0.2, right: 0.5, bottom: 0.4)]);

        var g = Assert.IsType<ShapeObject>(page.Objects[0]).Geometry;

        Assert.Equal(0.5, g.X1, 6);
        Assert.Equal(0.1, g.X2, 6);
        Assert.Equal(0.4, g.Y1, 6);
        Assert.Equal(0.2, g.Y2, 6);
        // The normalized box is the same either way round.
        Assert.Equal(0.1, g.Left, 6);
        Assert.Equal(0.5, g.Right, 6);
    }

    // ---------------- Non-shape objects ----------------

    [Fact]
    public void a_text_box_is_modelled_as_opaque_rather_than_as_a_shape()
    {
        // The text pipeline owns its own representation; the model records that
        // the object exists and where it sits, and nothing more.
        string tag = "AyaanText:24:FF0000FF:" + Convert.ToBase64String("hello"u8.ToArray());
        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, tag)]);

        var o = Assert.IsType<OpaqueObject>(page.Objects[0]);
        Assert.Equal(DocumentObjectKind.TextBox, o.Kind);
        Assert.Equal(tag, o.RawTag);
    }

    [Fact]
    public void a_stamp_is_modelled_as_opaque()
    {
        var page = DocumentModelBuilder.BuildPage(
            0, [Snap(0, "AyaanStamp:0.00:0.1000:0.2000:0.5000:0.4000")]);

        Assert.Equal(DocumentObjectKind.Stamp, page.Objects[0].Kind);
    }

    [Theory]
    [InlineData(PdfAnnotationSubtype.Ink, DocumentObjectKind.Ink)]
    [InlineData(PdfAnnotationSubtype.Highlight, DocumentObjectKind.Highlight)]
    [InlineData(PdfAnnotationSubtype.Stamp, DocumentObjectKind.Stamp)]
    [InlineData(PdfAnnotationSubtype.Square, DocumentObjectKind.Unknown)]
    public void an_untagged_annotation_is_classified_by_its_subtype(int subtype, DocumentObjectKind expected)
    {
        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, contents: null, subtype: subtype)]);
        Assert.Equal(expected, page.Objects[0].Kind);
    }

    [Fact]
    public void a_foreign_annotation_is_kept_so_the_z_order_stays_honest()
    {
        // The trap this guards: dropping annotations the app did not create
        // would make every object above them report the wrong stacking.
        var page = DocumentModelBuilder.BuildPage(0,
        [
            Snap(0, "Please review this", subtype: PdfAnnotationSubtype.Text),
            Snap(1, PlainRect),
        ]);

        Assert.Equal(2, page.Objects.Count);
        Assert.Equal(DocumentObjectKind.Unknown, page.Objects[0].Kind);
        Assert.Equal(1, page.Objects[1].ZOrder);
    }

    // ---------------- Page and document shape ----------------

    [Fact]
    public void objects_are_ordered_back_to_front_by_index()
    {
        var page = DocumentModelBuilder.BuildPage(0,
        [
            Snap(2, Arrow),
            Snap(0, PlainRect),
            Snap(1, RotatedEllipse),
        ]);

        Assert.Equal([0, 1, 2], page.Objects.Select(o => o.ZOrder));
        Assert.Equal(
            [ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Arrow],
            page.Shapes.Select(s => s.ShapeKind));
    }

    [Fact]
    public void an_object_can_be_found_by_its_identity_across_the_document()
    {
        var wanted = Guid.NewGuid();
        var model = DocumentModelBuilder.Build(
        [
            DocumentModelBuilder.BuildPage(0, [Snap(0, PlainRect)]),
            DocumentModelBuilder.BuildPage(1, [Snap(0, Arrow, id: wanted)]),
        ]);

        var found = model.ById(wanted);
        Assert.NotNull(found);
        Assert.Equal(1, found!.PageIndex);
        Assert.Null(model.ById(Guid.NewGuid()));
        Assert.Null(model.ById(Guid.Empty));
    }

    [Fact]
    public void an_empty_page_yields_an_empty_model_rather_than_null()
    {
        var page = DocumentModelBuilder.BuildPage(4, []);
        Assert.Equal(4, page.PageIndex);
        Assert.Empty(page.Objects);
        Assert.Empty(page.Shapes);
        Assert.Null(page.ById(Guid.NewGuid()));
    }

    // ---------------- What can be reordered ----------------
    //
    // Reordering is a run of removals and re-adds, so an object that cannot be
    // rebuilt cannot be reordered past either: the operation would destroy it.
    // This rule is now the ONLY guard on the z-order engine, so it is the thing
    // standing between a Send to Back and a lost Acrobat comment.

    [Fact]
    public void our_own_objects_can_be_rebuilt_and_so_can_be_reordered()
    {
        var page = DocumentModelBuilder.BuildPage(0,
        [
            Snap(0, PlainRect),
            Snap(1, "AyaanText:24:FF0000FF:" + Convert.ToBase64String("hi"u8.ToArray())),
            Snap(2, "AyaanStamp:0.00:0.1000:0.2000:0.5000:0.4000"),
        ]);

        Assert.All(page.Objects, o => Assert.True(o.IsRebuildable, $"{o.Kind} should be rebuildable"));
    }

    [Theory]
    [InlineData(PdfAnnotationSubtype.Ink)]
    [InlineData(PdfAnnotationSubtype.Highlight)]
    [InlineData(PdfAnnotationSubtype.Square)]
    [InlineData(PdfAnnotationSubtype.Text)]
    public void anything_this_app_cannot_recreate_refuses_to_be_reordered(int subtype)
    {
        // An ink stroke is an arbitrary point cloud with no description to
        // rebuild from, and a foreign annotation would come back as something
        // else or not at all. Refusing is the only honest answer.
        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, contents: null, subtype: subtype)]);

        Assert.False(page.Objects[0].IsRebuildable);
    }

    [Fact]
    public void an_untagged_stamps_pixels_are_recoverable_so_it_can_be_reordered()
    {
        // The core reads a stamp's image back out of the annotation, so a stamp
        // placed before stamps carried tags is still safe to rebuild.
        var page = DocumentModelBuilder.BuildPage(
            0, [Snap(0, contents: null, subtype: PdfAnnotationSubtype.Stamp)]);

        Assert.Equal(DocumentObjectKind.Stamp, page.Objects[0].Kind);
        Assert.True(page.Objects[0].IsRebuildable);
    }

    // ---------------- Tag reader edge cases ----------------

    [Fact]
    public void a_malformed_or_foreign_tag_is_not_read_as_a_shape()
    {
        foreach (string bad in new[]
        {
            "", "Please review this", "AyaanShape", "AyaanShape:",
            "AyaanShape:0:FFFFFFFF:2:1",              // too few fields
            "AyaanShape:0:GGGGGGGG:2:1:1",            // bad colour
            "AyaanShape:0:FFFFFFFF:0:1:1",            // zero width
            "AyaanShape:0:FFFFFFFF:abc:1:1",          // unparseable width
            "AyaanShape:99:FFFFFFFF:2:1:1",           // unknown kind
        })
        {
            Assert.False(ShapeTagReader.TryParse(bad, out _), $"wrongly parsed {bad:q}");
        }

        Assert.False(ShapeTagReader.TryParse(null, out _));
    }

    [Fact]
    public void a_corrupt_trailing_field_still_yields_a_usable_shape()
    {
        // Rotation and the fields after it are optional. A bad value there must
        // degrade to the historic default, not invalidate an otherwise good
        // shape, or one damaged byte would make a rectangle unrecognisable.
        Assert.True(ShapeTagReader.TryParse("AyaanShape:0:FF0000FF:2.0:1:1:zzz", out var t));
        Assert.Equal(ShapeKind.Rectangle, t.Kind);
        Assert.Equal(0, t.RotationDeg);
        Assert.Null(t.FillHex);
        Assert.Equal(0, t.CornerRadiusPts);
    }

    [Fact]
    public void a_tag_still_carrying_its_identity_prefix_is_read_as_a_shape()
    {
        // get_annotation_contents returns the RAW /Contents including the
        // "ID:<32 hex>|" prefix; the app's own reader strips it first. A reader
        // that only worked on stripped input is exactly the mistake that made
        // every shape stop being recognised as one for two weeks, so this reads
        // both forms.
        string raw = "ID:0123456789abcdef0123456789abcdef|" + PlainRect;

        Assert.True(ShapeTagReader.TryParse(raw, out var t));
        Assert.Equal(ShapeKind.Rectangle, t.Kind);
        Assert.Equal(PlainRect, ShapeTagReader.StripIdPrefix(raw));

        var page = DocumentModelBuilder.BuildPage(0, [Snap(0, raw)]);
        Assert.IsType<ShapeObject>(page.Objects[0]);
    }

    [Fact]
    public void something_that_merely_starts_with_ID_is_left_alone()
    {
        // A user comment beginning "ID:" must not have its first characters
        // eaten. Only a full 32-hex-then-pipe prefix counts.
        Assert.Equal("ID:not a guid|whatever",
            ShapeTagReader.StripIdPrefix("ID:not a guid|whatever"));
        Assert.Equal("ID:0123|short", ShapeTagReader.StripIdPrefix("ID:0123|short"));
    }

    [Fact]
    public void a_negative_radius_in_a_tag_reads_as_none()
    {
        Assert.True(ShapeTagReader.TryParse(
            "AyaanShape:4:FF0000FF:2.0:1:1:0.00:FF112233:-5.0", out var t));
        Assert.Equal(0, t.CornerRadiusPts);
    }
}
