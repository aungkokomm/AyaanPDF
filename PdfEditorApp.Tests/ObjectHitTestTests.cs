using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Geometry-aware picking, over the document model.
///
/// The bug this exists to kill: a diagonal arrow could be selected anywhere
/// inside its bounding box. An arrow running corner to corner touches almost
/// none of that box, so clicking empty page a long way from any ink selected the
/// arrow. The same fault, less dramatically, made an ellipse selectable at the
/// four corners it does not reach and a rounded rectangle selectable at the
/// corners it deliberately rounds away.
///
/// Every case below is written twice over: what the object's own geometry says,
/// and what the bounding box would have said. The second half is the point. A
/// test that only asserts a miss cannot tell a fixed hit test from a broken one.
/// </summary>
public class ObjectHitTestTests
{
    /// <summary>A round page width, so a stroke width in points converts to
    /// normalized units by a factor of a thousand.</summary>
    private const double PageWidthPts = 1000;

    // A 2pt stroke on a 1000pt page: 0.002 of the page width, so the writer's
    // pad is (2/2 + 1)/1000 = 0.002 on every side. Reach works out at the 0.006
    // minimum-grab floor.
    private const string ThinRect = "AyaanShape:0:FF0000FF:2.0000:1:1";
    private const string ThinEllipse = "AyaanShape:1:FF0000FF:2.0000:1:1";
    private const string ThinLine = "AyaanShape:2:FF0000FF:2.0000:1:1";

    // A 20pt stroke: 0.02 normalized, pad 0.011. Deliberately fat, so the
    // arrowhead is comfortably larger than the pick reach and a hit on it cannot
    // be explained by the shaft.
    private const string FatArrow = "AyaanShape:3:FF0000FF:20.0000:1:1";

    private const double FatStrokeNorm = 0.02;

    private static AnnotationSnapshot Snap(
        string? contents, TextRect bounds, int index = 0, int subtype = 4, Guid id = default) =>
        new(index, subtype, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
            1.0, id == default ? Guid.NewGuid() : id, contents);

    private static PageModel Page(params AnnotationSnapshot[] snaps) =>
        DocumentModelBuilder.BuildPage(0, snaps, PageWidthPts);

    private static DocumentObject One(string? contents, TextRect bounds, int subtype = 4) =>
        Page(Snap(contents, bounds, subtype: subtype)).Objects[0];

    /// <summary>The /Rect a shape drawn between these corners would carry: the
    /// drag's extent grown by the writer's pad on every side.</summary>
    private static TextRect Padded(double l, double t, double r, double b, double strokePts) =>
        Padded(new TextRect(l, t, r, b), strokePts);

    private static TextRect Padded(TextRect drag, double strokePts)
    {
        double pad = ((strokePts / 2) + 1) / PageWidthPts;
        return new TextRect(drag.Left - pad, drag.Top - pad, drag.Right + pad, drag.Bottom + pad);
    }

    // ---------------- The diagonal arrow ----------------

    [Fact]
    public void a_diagonal_arrow_is_not_hit_in_an_empty_corner_of_its_bounding_box()
    {
        // Drawn corner to corner, top-left to bottom-right.
        var arrow = One(FatArrow, Padded(0.1, 0.1, 0.5, 0.5, 20));

        // The OTHER diagonal's corner: inside the box, nowhere near the arrow.
        // A fifth of the page away from the nearest ink.
        Assert.False(ObjectHitTest.Hit(arrow, 0.45, 0.15));
        Assert.False(ObjectHitTest.Hit(arrow, 0.15, 0.45));

        // And this is what the old test said about the very same point. Without
        // this line the assertion above proves nothing: a hit test that always
        // returned false would pass it too.
        Assert.True(Geometry2D.InRect(0.45, 0.15, arrow.Bounds));
        Assert.True(Geometry2D.InRect(0.15, 0.45, arrow.Bounds));
    }

    [Fact]
    public void a_diagonal_arrow_is_hit_on_its_shaft()
    {
        var arrow = One(FatArrow, Padded(0.1, 0.1, 0.5, 0.5, 20));

        Assert.True(ObjectHitTest.Hit(arrow, 0.25, 0.25));
        Assert.True(ObjectHitTest.Hit(arrow, 0.15, 0.15));
    }

    [Fact]
    public void a_diagonal_arrow_is_hit_on_its_head()
    {
        var arrow = Assert.IsType<ShapeObject>(One(FatArrow, Padded(0.1, 0.1, 0.5, 0.5, 20)));
        var g = arrow.UprightGeometry;

        // The head's own geometry, from the same function that draws it, so
        // this cannot drift away from the arrow on the page.
        var a = ShapeGeometry.Arrow(g.X1, g.Y1, g.X2, g.Y2, FatStrokeNorm);
        double cx = (a.Tip.X + a.Left.X + a.Right.X) / 3;
        double cy = (a.Tip.Y + a.Left.Y + a.Right.Y) / 3;

        Assert.True(ObjectHitTest.Hit(arrow, cx, cy));

        // The shaft STOPS at the base of the head, so the head's centre lies
        // beyond the end of it, further than the reach. The hit above can
        // therefore only have come from the head itself, which is the thing
        // being proved: a filled arrowhead is solid, not an outline.
        double reach = Math.Max(FatStrokeNorm, ShapeAnnotation.MinimumGrab);
        Assert.True(Geometry2D.DistanceToSegment(cx, cy, a.ShaftStart, a.ShaftEnd) > reach);
    }

    [Fact]
    public void an_arrow_drawn_the_other_way_keeps_its_head_at_the_end_it_points_to()
    {
        // fx=0, fy=0: dragged bottom-right to top-left, so the head is at the
        // TOP-LEFT. Direction is part of the shape, and a hit test that
        // normalized it away would look for the head at the wrong end.
        var arrow = Assert.IsType<ShapeObject>(
            One("AyaanShape:3:FF0000FF:20.0000:0:0", Padded(0.1, 0.1, 0.5, 0.5, 20)));

        var g = arrow.UprightGeometry;
        Assert.Equal(0.5, g.X1, 6);
        Assert.Equal(0.1, g.X2, 6);

        var a = ShapeGeometry.Arrow(g.X1, g.Y1, g.X2, g.Y2, FatStrokeNorm);
        Assert.True(ObjectHitTest.Hit(
            arrow,
            (a.Tip.X + a.Left.X + a.Right.X) / 3,
            (a.Tip.Y + a.Left.Y + a.Right.Y) / 3));
    }

    // ---------------- The ellipse ----------------

    [Theory]
    [InlineData(0.1, 0.1)]
    [InlineData(0.5, 0.1)]
    [InlineData(0.1, 0.3)]
    [InlineData(0.5, 0.3)]
    public void an_ellipse_is_not_hit_at_the_corners_of_its_bounding_box(double x, double y)
    {
        // An ellipse fills about four fifths of its box and the missing fifth is
        // entirely at the corners.
        var ellipse = One(ThinEllipse, Padded(0.1, 0.1, 0.5, 0.3, 2));

        Assert.False(ObjectHitTest.Hit(ellipse, x, y));
        Assert.True(Geometry2D.InRect(x, y, ellipse.Bounds));
    }

    [Fact]
    public void an_ellipse_is_hit_inside_it_and_on_its_edge()
    {
        var ellipse = One(ThinEllipse, Padded(0.1, 0.1, 0.5, 0.3, 2));

        Assert.True(ObjectHitTest.Hit(ellipse, 0.3, 0.2));    // centre
        Assert.True(ObjectHitTest.Hit(ellipse, 0.5, 0.2));    // rightmost point
        Assert.True(ObjectHitTest.Hit(ellipse, 0.3, 0.1));    // topmost point
    }

    [Fact]
    public void a_rectangle_the_same_size_IS_hit_at_those_corners()
    {
        // The control. A drawn shape is an object you grab anywhere on it, and
        // a rectangle really does reach its own corners. If this failed, the
        // ellipse test above would be proving nothing about ellipses.
        var rect = One(ThinRect, Padded(0.1, 0.1, 0.5, 0.3, 2));

        Assert.True(ObjectHitTest.Hit(rect, 0.1, 0.1));
        Assert.True(ObjectHitTest.Hit(rect, 0.5, 0.3));
        Assert.True(ObjectHitTest.Hit(rect, 0.3, 0.2));
    }

    // ---------------- The rounded rectangle ----------------

    [Theory]
    [InlineData(0.1, 0.1)]
    [InlineData(0.5, 0.1)]
    [InlineData(0.1, 0.5)]
    [InlineData(0.5, 0.5)]
    public void a_rounded_rectangles_square_corners_are_not_treated_as_filled(double x, double y)
    {
        // A 100pt radius on a 1000pt page is 0.1 normalized, in a box 0.4 across:
        // a visibly rounded shape whose corners are demonstrably not there.
        var rounded = One(
            "AyaanShape:4:FF0000FF:2.0000:1:1:0.00:00000000:100.0000",
            Padded(0.1, 0.1, 0.5, 0.5, 2));

        Assert.False(ObjectHitTest.Hit(rounded, x, y));
        Assert.True(Geometry2D.InRect(x, y, rounded.Bounds));
    }

    [Fact]
    public void a_rounded_rectangle_is_hit_on_its_straight_sides_and_inside()
    {
        var rounded = One(
            "AyaanShape:4:FF0000FF:2.0000:1:1:0.00:00000000:100.0000",
            Padded(0.1, 0.1, 0.5, 0.5, 2));

        Assert.True(ObjectHitTest.Hit(rounded, 0.3, 0.1));    // middle of the top edge
        Assert.True(ObjectHitTest.Hit(rounded, 0.1, 0.3));    // middle of the left edge
        Assert.True(ObjectHitTest.Hit(rounded, 0.3, 0.3));    // the middle
        Assert.True(ObjectHitTest.Hit(rounded, 0.2, 0.1));    // where the arc meets the edge
    }

    [Fact]
    public void a_rounded_rectangle_with_no_radius_is_hit_like_a_plain_one()
    {
        // Dragged too thin to round, it is drawn as a rectangle, so it must be
        // picked as one.
        var square = One(
            "AyaanShape:4:FF0000FF:2.0000:1:1:0.00:00000000:0.0000",
            Padded(0.1, 0.1, 0.5, 0.5, 2));

        Assert.True(ObjectHitTest.Hit(square, 0.1, 0.1));
    }

    // ---------------- Rotation ----------------

    // A line drawn from (0.2,0.2) to (0.4,0.3) and then turned 90 degrees
    // clockwise about its centre (0.3,0.25). On the page it runs from
    // (0.35,0.15) to (0.25,0.35). Its /Rect is the axis-aligned box of the
    // TURNED content, which is why the upright size has to come off the tag:
    // 200 x 100 points, i.e. 0.2 x 0.1 of the page width.
    private const string TurnedLine =
        "AyaanShape:2:FF0000FF:2.0000:1:1:90.00:00000000:0.0000:200.0000:100.0000";

    private static readonly TextRect TurnedLineRect = new(0.248, 0.148, 0.352, 0.352);

    [Fact]
    public void a_turned_object_is_rebuilt_at_its_real_size_not_its_rects()
    {
        var line = Assert.IsType<ShapeObject>(One(TurnedLine, TurnedLineRect));

        Assert.Equal(90, line.RotationDeg, 6);

        // Per component, because the rebuild is centre-minus-half-size and that
        // arithmetic lands a unit in the last place away from the literal.
        Assert.Equal(0.2, line.UprightBounds.Left, 9);
        Assert.Equal(0.2, line.UprightBounds.Top, 9);
        Assert.Equal(0.4, line.UprightBounds.Right, 9);
        Assert.Equal(0.3, line.UprightBounds.Bottom, 9);
    }

    [Fact]
    public void a_turned_line_is_hit_where_it_actually_lies()
    {
        var line = One(TurnedLine, TurnedLineRect);

        Assert.True(ObjectHitTest.Hit(line, 0.3, 0.25));      // its centre
        Assert.True(ObjectHitTest.Hit(line, 0.325, 0.20));    // a quarter along it
        Assert.True(ObjectHitTest.Hit(line, 0.275, 0.30));    // three quarters along it
    }

    [Fact]
    public void a_turned_line_is_not_hit_where_it_would_have_lain_unturned()
    {
        // (0.325, 0.2625) sits exactly on the UPRIGHT line. If the rotation
        // were ignored this would be a hit, so it is the assertion that proves
        // the point is being moved into the object's own frame.
        var line = One(TurnedLine, TurnedLineRect);

        Assert.False(ObjectHitTest.Hit(line, 0.325, 0.2625));
        Assert.True(Geometry2D.InRect(0.325, 0.2625, line.Bounds));
    }

    [Fact]
    public void a_turned_text_box_is_hit_as_a_diamond_not_as_its_rect()
    {
        // Upright 0.2 x 0.1 at (0.2,0.2)-(0.4,0.3), turned 45 degrees, so its
        // /Rect grows to roughly 0.212 square and the four corners of that
        // rectangle are empty page.
        string tag = "AyaanTextB:24:FF0000FF:0:00000000:00000000:0:0::45:0.2:0.2:0.4:0.3:"
                     + Convert.ToBase64String("hi"u8.ToArray());
        var box = One(tag, new TextRect(0.193934, 0.143934, 0.406066, 0.356066));

        Assert.True(ObjectHitTest.Hit(box, 0.3, 0.25));       // the middle
        Assert.False(ObjectHitTest.Hit(box, 0.20, 0.15));     // a corner of /Rect
        Assert.True(Geometry2D.InRect(0.20, 0.15, box.Bounds));
    }

    // ---------------- Ink ----------------

    [Fact]
    public void a_diagonal_stroke_is_hit_on_the_ink_and_not_beside_it()
    {
        var ink = One("AyaanInk:FF0000FF:0.004:0.1,0.1;0.5,0.5",
                      new TextRect(0.1, 0.1, 0.5, 0.5),
                      subtype: PdfAnnotationSubtype.Ink);

        Assert.True(ObjectHitTest.Hit(ink, 0.3, 0.3));
        Assert.False(ObjectHitTest.Hit(ink, 0.45, 0.15));
        Assert.True(Geometry2D.InRect(0.45, 0.15, ink.Bounds));
    }

    [Fact]
    public void a_stroke_from_another_editor_falls_back_to_its_box()
    {
        // No tag, so no points, so nothing better to measure against. Being
        // generous here is right: refusing to select a mark this app cannot
        // describe would make it unremovable.
        var ink = One(null, new TextRect(0.1, 0.1, 0.5, 0.5), subtype: PdfAnnotationSubtype.Ink);

        Assert.True(ObjectHitTest.Hit(ink, 0.45, 0.15));
    }

    // ---------------- Stacking ----------------

    [Fact]
    public void overlapping_objects_return_the_frontmost()
    {
        var back = Guid.NewGuid();
        var front = Guid.NewGuid();
        var page = Page(
            Snap(ThinRect, Padded(0.1, 0.1, 0.5, 0.5, 2), index: 0, id: back),
            Snap(ThinRect, Padded(0.2, 0.2, 0.3, 0.3, 2), index: 1, id: front));

        // In the overlap, the one painted last wins.
        Assert.Equal(front, ObjectHitTest.PickTopmost(page, 0.25, 0.25)!.Id);

        // Outside the front one, the back one is still there.
        Assert.Equal(back, ObjectHitTest.PickTopmost(page, 0.45, 0.45)!.Id);
    }

    [Fact]
    public void something_in_front_only_wins_where_it_actually_is()
    {
        // The payoff, in one test. An arrow drawn across a rectangle used to
        // steal every click inside its bounding box, including clicks on the
        // rectangle underneath it. Now it only takes the ones that land on it.
        var rect = Guid.NewGuid();
        var arrow = Guid.NewGuid();
        var page = Page(
            Snap(ThinRect, Padded(0.1, 0.1, 0.5, 0.5, 2), index: 0, id: rect),
            Snap(FatArrow, Padded(0.1, 0.1, 0.5, 0.5, 20), index: 1, id: arrow));

        Assert.Equal(arrow, ObjectHitTest.PickTopmost(page, 0.25, 0.25)!.Id);
        Assert.Equal(rect, ObjectHitTest.PickTopmost(page, 0.45, 0.15)!.Id);
    }

    [Fact]
    public void a_point_on_nothing_picks_nothing()
    {
        var page = Page(Snap(ThinRect, Padded(0.1, 0.1, 0.5, 0.3, 2)));

        Assert.Null(ObjectHitTest.PickTopmost(page, 0.9, 0.9));
        Assert.Null(ObjectHitTest.PickTopmost(DocumentModelBuilder.BuildPage(0, []), 0.5, 0.5));
        Assert.Null(ObjectHitTest.PickTopmost(null, 0.5, 0.5));
        Assert.False(ObjectHitTest.Hit(null, 0.5, 0.5));
    }

    // ---------------- Why the grip check has to come first ----------------

    [Fact]
    public void a_resize_grip_can_sit_where_the_object_itself_is_not()
    {
        // Grips are drawn on the corners of the annotation's /Rect, and a
        // diagonal arrow does not reach two of those corners. So the handle and
        // the object disagree about the very same point, and SelectAnnotationAt
        // must keep testing the SELECTED object's grips BEFORE it picks by
        // geometry, or a selected arrow could never be resized: the click would
        // fall through to the geometry test, miss the arrow, and deselect it.
        //
        // This pins the conflict rather than the ordering, which lives in the
        // view model where a test assembly cannot reach it. Anyone who later
        // decides the grip pre-check looks redundant has to explain this.
        var arrow = One(FatArrow, Padded(0.1, 0.1, 0.5, 0.5, 20));
        var box = new AnnotationBox(
            0, arrow.Bounds.Left, arrow.Bounds.Top, arrow.Bounds.Right, arrow.Bounds.Bottom);

        // The top-right corner of the box: a resize grip lives there.
        Assert.Equal(
            LoadedAnnotationPicker.Grip.TopRight,
            LoadedAnnotationPicker.GripAt(box, arrow.Bounds.Right, arrow.Bounds.Top));

        // And the arrow is nowhere near it.
        Assert.False(ObjectHitTest.Hit(arrow, arrow.Bounds.Right, arrow.Bounds.Top));
    }

    // ---------------- Tolerance ----------------

    [Fact]
    public void the_default_tolerance_is_the_one_the_overlay_already_uses()
    {
        // A mark made this session and the same mark after a reload must be
        // equally easy to click, or picking would change under the user the
        // moment a page was invalidated.
        Assert.Equal(AnnotationHitTester.DefaultTolerance, ObjectHitTest.DefaultTolerance);
    }

    [Fact]
    public void a_wider_tolerance_reaches_further_from_a_line()
    {
        // About 0.02 below a nearly horizontal line.
        var line = One(ThinLine, Padded(0.1, 0.2, 0.5, 0.2, 2));

        Assert.False(ObjectHitTest.Hit(line, 0.3, 0.22));
        Assert.True(ObjectHitTest.Hit(line, 0.3, 0.22, tolerance: 0.03));
    }

    [Fact]
    public void a_shape_is_never_harder_to_grab_than_its_minimum()
    {
        // A hairline shape is still a target the pointer can be expected to find
        // without precision aiming, so the reach never falls below the grab
        // floor however small the tolerance gets.
        var line = One(ThinLine, Padded(0.1, 0.2, 0.5, 0.2, 2));

        Assert.True(ObjectHitTest.Hit(line, 0.3, 0.2 + (ShapeAnnotation.MinimumGrab / 2),
                                      tolerance: 0));
    }

    [Fact]
    public void tolerance_applies_to_objects_the_model_does_not_describe_too()
    {
        var stamp = One("AyaanStamp:0.00:100.0000:100.0000:500.0000:300.0000",
                        new TextRect(0.1, 0.1, 0.5, 0.3));

        // 0.002 outside the right edge: inside the default tolerance, outside
        // no tolerance at all.
        Assert.True(ObjectHitTest.Hit(stamp, 0.502, 0.2));
        Assert.False(ObjectHitTest.Hit(stamp, 0.502, 0.2, tolerance: 0));
        Assert.False(ObjectHitTest.Hit(stamp, 0.6, 0.2));
    }
}
