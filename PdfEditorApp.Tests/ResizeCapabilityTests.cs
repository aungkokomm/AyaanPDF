using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The rule that decides whether an object gets resize handles at all.
///
/// It used to refuse every /Ink annotation, on the grounds that a stroke is an
/// arbitrary point cloud PDFium will not scale. That was true when it was
/// written and stopped being true when strokes started carrying
/// <see cref="InkTag"/>: a tagged stroke records its own control points, so it
/// can be redrawn into any rectangle exactly as a shape is. The guard was never
/// revisited, so drawings stayed the one object in the app that could be
/// selected, moved, grouped, reordered and deleted but not resized.
///
/// Ink this app did NOT write still has nothing to rebuild from, and must still
/// be refused.
/// </summary>
public class ResizeCapabilityTests
{
    private const string OurInk = "AyaanInk:FFFF0000:0.004:0.1,0.1;0.3,0.2;0.5,0.5";
    private const string OurShape = "AyaanShape:0:FF0000FF:2.0000:1:1";
    private static readonly string OurTextBox =
        "AyaanText:24:FF0000FF:" + Convert.ToBase64String("hi"u8.ToArray());
    private const string OurStamp = "AyaanStamp:0.00:100.0000:100.0000:500.0000:300.0000";

    // ---------------- Our own ink: the change ----------------

    [Fact]
    public void a_stroke_this_app_drew_can_be_resized()
    {
        // Its tag carries the control points, which is the whole description a
        // rebuild needs. InkTag.ScaleTo already maps them onto any rectangle.
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Ink, OurInk));
    }

    [Fact]
    public void a_stroke_still_carrying_its_identity_prefix_can_be_resized()
    {
        Assert.True(AnnotationResize.CanResize(
            PdfAnnotationSubtype.Ink,
            "ID:0123456789abcdef0123456789abcdef|" + OurInk));
    }

    // ---------------- Ink we cannot rebuild: unchanged ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Please review this")]
    [InlineData("AyaanInk:")]
    [InlineData("AyaanInk:FFFF0000:0.004:")]              // no points
    [InlineData("AyaanInk:FFFF0000:0.004:0.1,0.1")]       // a single point is not a stroke
    [InlineData("AyaanInk:FFFF0000:zzz:0.1,0.1;0.5,0.5")] // unparseable width
    public void a_stroke_with_nothing_to_rebuild_from_is_still_refused(string? contents)
    {
        // A drawing from Acrobat, or one of ours damaged beyond reading. There
        // is no description to redraw it at a new size, and offering handles
        // that cannot work would be worse than offering none.
        Assert.False(AnnotationResize.CanResize(PdfAnnotationSubtype.Ink, contents));
    }

    // ---------------- Everything else: unchanged ----------------

    [Fact]
    public void our_other_objects_can_still_be_resized()
    {
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Stamp, OurShape));
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Stamp, OurTextBox));
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Stamp, OurStamp));
    }

    [Fact]
    public void an_untagged_stamp_can_still_be_resized()
    {
        // Placed before stamps carried tags; the core reads its image back out.
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Stamp, null));
    }

    [Theory]
    [InlineData(PdfAnnotationSubtype.Square)]
    [InlineData(PdfAnnotationSubtype.Text)]
    [InlineData(PdfAnnotationSubtype.Highlight)]
    [InlineData(PdfAnnotationSubtype.FreeText)]
    public void marks_from_other_editors_are_treated_as_before(int subtype)
    {
        Assert.True(AnnotationResize.CanResize(subtype, "Please review this"));
    }

    [Fact]
    public void a_shape_is_resizable_whatever_its_subtype_claims()
    {
        // Our shapes are /Stamp annotations now but were /Ink historically, and
        // a file written by an older build still has them that way. The tag is
        // what decides, not the subtype.
        Assert.True(AnnotationResize.CanResize(PdfAnnotationSubtype.Ink, OurShape));
    }
}
