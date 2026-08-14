using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The things a drawing should be able to do because every other object can.
///
/// Ink spent a long time as a second-class mark: it could be selected, moved,
/// grouped and deleted, but not resized, turned, reordered, copied or
/// duplicated. Each of those was gated on a rule written when a stroke really
/// was an opaque point cloud, and none was revisited when InkTag made a stroke
/// as describable as a shape.
/// </summary>
public class InkParityTests
{
    private const string OurInk = "AyaanInk:FFFF0000:0.004:0.1,0.1;0.3,0.2;0.5,0.5";

    private static DocumentObject Ink(string? contents) =>
        DocumentModelBuilder.BuildPage(
            0,
            [new AnnotationSnapshot(
                0, PdfAnnotationSubtype.Ink, 0.1, 0.1, 0.5, 0.5, 1.0, Guid.NewGuid(), contents)],
            1000).Objects[0];

    // ---------------- Z-order ----------------

    [Fact]
    public void a_stroke_this_app_drew_can_be_rebuilt_and_so_can_be_reordered()
    {
        // Reordering is a run of removals and re-adds, so the guard asks whether
        // an object can be put back faithfully. A tagged stroke can: its control
        // points ARE the description, which is the same basis on which a shape
        // qualifies. Refusing meant a page with any drawing on it could not have
        // its z-order changed at all, and said so with a message about a mark
        // this app did not create.
        Assert.True(Ink(OurInk).IsRebuildable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Please review this")]
    [InlineData("AyaanInk:FFFF0000:0.004:0.1,0.1")]   // one point is not a stroke
    public void a_stroke_with_nothing_to_rebuild_from_still_refuses(string? contents)
    {
        // A drawing from another editor is still an arbitrary point cloud.
        // Reordering past it would destroy it, so the refusal has to stay.
        Assert.False(Ink(contents).IsRebuildable);
    }

    [Fact]
    public void the_other_kinds_are_unchanged()
    {
        var page = DocumentModelBuilder.BuildPage(
            0,
            [
                new AnnotationSnapshot(0, PdfAnnotationSubtype.Stamp, 0, 0, 1, 1, 1.0, Guid.NewGuid(),
                    "AyaanShape:0:FF0000FF:2.0000:1:1"),
                new AnnotationSnapshot(1, PdfAnnotationSubtype.Stamp, 0, 0, 1, 1, 1.0, Guid.NewGuid(),
                    "AyaanText:24:FF0000FF:" + Convert.ToBase64String("hi"u8.ToArray())),
                new AnnotationSnapshot(2, PdfAnnotationSubtype.Stamp, 0, 0, 1, 1, 1.0, Guid.NewGuid(), null),
                new AnnotationSnapshot(3, PdfAnnotationSubtype.Square, 0, 0, 1, 1, 1.0, Guid.NewGuid(),
                    "Please review this"),
            ],
            1000);

        Assert.True(page.Objects[0].IsRebuildable);    // shape
        Assert.True(page.Objects[1].IsRebuildable);    // text box
        Assert.True(page.Objects[2].IsRebuildable);    // untagged stamp, pixels recoverable
        Assert.False(page.Objects[3].IsRebuildable);   // someone else's mark
    }

    // ---------------- Copy, paste and duplicate ----------------
    //
    // All three rebuild a stroke from its tag into a target rectangle, which is
    // exactly what a resize does. What each needs from this layer is that a tag
    // survives being read and written back, and that the points land in the box
    // asked for.

    [Fact]
    public void a_copied_stroke_can_be_rebuilt_somewhere_else()
    {
        Assert.True(InkTag.TryParse(OurInk, out string color, out double width, out var control, out double deg));

        // Pasted at an offset, the way the clipboard places a copy.
        var moved = InkTag.ScaleTo(control, 0.3, 0.3, 0.7, 0.7);
        string rewritten = InkTag.Write(color, width, moved, deg);

        Assert.True(InkTag.TryParse(rewritten, out string c2, out double w2, out var read, out double d2));
        Assert.Equal(color, c2);
        Assert.Equal(width, w2, 9);
        Assert.Equal(deg, d2, 9);
        Assert.Equal(control.Count, read.Count);
        Assert.Equal(0.3, read[0].X, 4);
    }

    [Fact]
    public void a_duplicated_stroke_keeps_its_angle()
    {
        // Ctrl+drag clones in place and the drag moves the clone. A turned
        // stroke must clone turned, or the copy springs upright.
        string turned = InkTag.Write("FFFF0000", 0.004,
            [(0.1, 0.1), (0.3, 0.2), (0.5, 0.5)], 37.5);

        Assert.True(InkTag.TryParse(turned, out string color, out double width, out var control, out double deg));
        Assert.Equal(37.5, deg, 4);

        string clone = InkTag.Write(color, width, control, deg);
        Assert.Equal(turned, clone);
    }
}
