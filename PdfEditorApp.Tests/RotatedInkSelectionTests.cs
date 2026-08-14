using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Picking a stroke that has been TURNED.
///
/// The regression this exists to stop: a stroke's tag stores its UPRIGHT points
/// and an angle, and the model handed those upright points straight to the hit
/// test. So a turned drawing could not be selected by clicking on it, while
/// clicking empty page where it would have been if it had never been turned
/// selected it instead. Nothing showed it up in normal use, because a stroke is
/// still selected immediately after being rotated; it only bites on the NEXT
/// click.
///
/// The model therefore reports the points AS DRAWN ON THE PAGE, already turned.
/// Note that it does NOT report an angle for ink and must not: a stroke's /Rect
/// is the bounding box of the turned point cloud, whose centre is not the point
/// the stroke was turned about, so inverse-rotating about it would not undo the
/// rotation. Testing the placed points directly avoids the question entirely.
/// </summary>
public class RotatedInkSelectionTests
{
    /// <summary>A straight horizontal stroke, 0.2..0.6 across at y = 0.3.</summary>
    private static readonly List<(double X, double Y)> Horizontal =
    [
        (0.20, 0.30), (0.40, 0.30), (0.60, 0.30),
    ];

    /// <summary>
    /// Turned a quarter turn about its own centre (0.4, 0.3), the stroke becomes
    /// VERTICAL, running 0.1..0.5 down at x = 0.4. The annotation's rectangle is
    /// the box containing that, plus a little stroke padding.
    /// </summary>
    private static readonly TextRect TurnedBounds = new(0.39, 0.09, 0.41, 0.51);

    private static OpaqueObject InkObject(double rotationDeg, TextRect bounds)
    {
        string tag = InkTag.Write("FFFF0000", 0.004, Horizontal, rotationDeg);
        var page = DocumentModelBuilder.BuildPage(
            0,
            [new AnnotationSnapshot(
                0, PdfAnnotationSubtype.Ink,
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                1.0, Guid.NewGuid(), tag)],
            1000);

        return Assert.IsType<OpaqueObject>(page.Objects[0]);
    }

    [Fact]
    public void a_turned_stroke_is_hit_where_it_actually_lies()
    {
        var ink = InkObject(90, TurnedBounds);

        // On the VERTICAL stroke the quarter turn produced.
        Assert.True(ObjectHitTest.Hit(ink, 0.40, 0.20));
        Assert.True(ObjectHitTest.Hit(ink, 0.40, 0.30));
        Assert.True(ObjectHitTest.Hit(ink, 0.40, 0.45));
    }

    [Fact]
    public void a_turned_stroke_is_NOT_hit_where_it_would_have_been_unturned()
    {
        // The assertion that fails when the model hands over upright points:
        // (0.25, 0.30) sits squarely on the horizontal stroke as DRAWN, and
        // nowhere near it once it has been turned.
        var ink = InkObject(90, TurnedBounds);

        Assert.False(ObjectHitTest.Hit(ink, 0.25, 0.30));
        Assert.False(ObjectHitTest.Hit(ink, 0.55, 0.30));
    }

    [Fact]
    public void an_unturned_stroke_is_picked_exactly_as_before()
    {
        var ink = InkObject(0, new TextRect(0.19, 0.29, 0.61, 0.31));

        Assert.True(ObjectHitTest.Hit(ink, 0.25, 0.30));
        Assert.True(ObjectHitTest.Hit(ink, 0.40, 0.30));
        Assert.False(ObjectHitTest.Hit(ink, 0.40, 0.45));
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(135.0)]
    [InlineData(220.0)]
    public void the_points_the_model_reports_are_the_ones_on_the_page(double deg)
    {
        // The model's points must equal what the writer put on the page: the
        // upright points turned about their own centre. If these two ever
        // disagree, picking disagrees with what the user can see.
        var ink = InkObject(deg, TurnedBounds);
        var expected = Geometry2D.RotateAboutCentre(Horizontal, deg);

        Assert.Equal(expected.Count, ink.InkPoints.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, ink.InkPoints[i].X, 4);
            Assert.Equal(expected[i].Y, ink.InkPoints[i].Y, 4);
        }
    }

    [Fact]
    public void the_model_reports_no_angle_for_ink_so_nothing_rotates_twice()
    {
        // Deliberate. The points are already placed, so an angle here would
        // make ObjectHitTest turn the pointer as well and the two would cancel
        // out to the wrong place.
        Assert.Equal(0, InkObject(90, TurnedBounds).RotationDeg);
    }

    [Fact]
    public void a_stroke_from_another_editor_still_falls_back_to_its_box()
    {
        var page = DocumentModelBuilder.BuildPage(
            0,
            [new AnnotationSnapshot(
                0, PdfAnnotationSubtype.Ink, 0.1, 0.1, 0.5, 0.5, 1.0, Guid.NewGuid(), null)],
            1000);

        Assert.True(ObjectHitTest.Hit(page.Objects[0], 0.45, 0.15));
    }
}
