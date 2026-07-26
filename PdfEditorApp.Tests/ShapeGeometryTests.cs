using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class ShapeGeometryTests
{
    private static ShapeDraft Draft(ShapeKind kind, double x1, double y1, double x2, double y2) =>
        new(kind, x1, y1, x2, y2);

    // ---- The drag, and the direction it carries ----

    [Fact]
    public void a_backwards_drag_still_describes_the_same_box()
    {
        // Dragging up-left must give the same rectangle as dragging down-right;
        // only the arrow's direction should care which way it went.
        var forward = Draft(ShapeKind.Rectangle, 0.2, 0.3, 0.6, 0.7);
        var backward = Draft(ShapeKind.Rectangle, 0.6, 0.7, 0.2, 0.3);

        Assert.Equal(forward.Left, backward.Left, 9);
        Assert.Equal(forward.Top, backward.Top, 9);
        Assert.Equal(forward.Right, backward.Right, 9);
        Assert.Equal(forward.Bottom, backward.Bottom, 9);
    }

    [Fact]
    public void an_arrow_drawn_backwards_points_backwards()
    {
        // The reason a shape carries its drag rather than a normalized box. If
        // this were normalized on the way in, every arrow would point the same
        // way regardless of how it was drawn.
        var a = ShapeGeometry.Arrow(1.0, 0.0, 0.0, 0.0, 0.01);
        var (left, right) = (a.Left, a.Right);
        Assert.True(left.X > 0.0 && right.X > 0.0, $"barbs at {left.X} and {right.X} ignored the drag direction");
    }

    // ---- Arrowheads ----

    [Fact]
    public void arrow_barbs_sweep_back_from_the_tip_and_straddle_the_shaft()
    {
        var a = ShapeGeometry.Arrow(0.0, 0.0, 1.0, 0.0, 0.01);
        var (left, right) = (a.Left, a.Right);

        Assert.True(left.X < 1.0 && right.X < 1.0, "a barb is ahead of the tip, which draws a bowtie");
        Assert.True(left.Y * right.Y < 0, "both barbs are on the same side of the shaft");
        Assert.Equal(0.0, left.Y + right.Y, 9);
    }

    [Fact]
    public void a_zero_length_arrow_produces_finite_points()
    {
        // A click with no drag. Dividing by a zero-length shaft would put NaN
        // into the preview and into the file.
        var a = ShapeGeometry.Arrow(0.5, 0.5, 0.5, 0.5, 0.01);
        var (left, right) = (a.Left, a.Right);
        foreach (double v in new[] { left.X, left.Y, right.X, right.Y })
        {
            Assert.True(double.IsFinite(v), $"non-finite arrow coordinate {v}");
        }
    }

    [Fact]
    public void a_hairline_arrow_still_gets_a_visible_head()
    {
        // Barb length scales with stroke width, so without a floor the finest
        // pen draws a head too small to read as one.
        var left = ShapeGeometry.Arrow(0.0, 0.0, 1.0, 0.0, 0.0).Left;
        double reach = Math.Sqrt(Math.Pow(1.0 - left.X, 2) + Math.Pow(left.Y, 2));
        Assert.True(reach >= ShapeGeometry.ArrowHeadMin - 1e-9, $"head reached only {reach}");
    }

    [Fact]
    public void a_thick_arrow_gets_a_head_in_proportion()
    {
        var thin = ShapeGeometry.Arrow(0.0, 0.0, 1.0, 0.0, 0.002).Left;
        var thick = ShapeGeometry.Arrow(0.0, 0.0, 1.0, 0.0, 0.02).Left;
        Assert.True(thick.X < thin.X, "a thicker arrow should have a longer head, not the same one");
    }

    [Fact]
    public void an_arrow_head_stays_symmetric_on_a_diagonal()
    {
        // The rotation is the part most likely to be wrong, and on a horizontal
        // shaft a sign error can still look plausible.
        var a = ShapeGeometry.Arrow(0.0, 0.0, 1.0, 1.0, 0.01);
        var (left, right) = (a.Left, a.Right);

        double tipToLeft = Math.Sqrt(Math.Pow(1.0 - left.X, 2) + Math.Pow(1.0 - left.Y, 2));
        double tipToRight = Math.Sqrt(Math.Pow(1.0 - right.X, 2) + Math.Pow(1.0 - right.Y, 2));

        Assert.Equal(tipToLeft, tipToRight, 9);
        Assert.True(left.X < 1.0 && left.Y < 1.0, "barb is past the tip on a diagonal");
    }

    // ---- Outlines ----

    [Fact]
    public void a_rectangle_outline_closes()
    {
        var points = ShapeGeometry.Outline(Draft(ShapeKind.Rectangle, 0.1, 0.2, 0.5, 0.6), 0.003);

        Assert.Equal(5, points.Count);
        Assert.Equal(points[0], points[^1]);
    }

    [Fact]
    public void a_rectangle_outline_touches_all_four_corners()
    {
        var s = Draft(ShapeKind.Rectangle, 0.1, 0.2, 0.5, 0.6);
        var points = ShapeGeometry.Outline(s, 0.003);

        Assert.Contains((s.Left, s.Top), points);
        Assert.Contains((s.Right, s.Top), points);
        Assert.Contains((s.Right, s.Bottom), points);
        Assert.Contains((s.Left, s.Bottom), points);
    }

    [Fact]
    public void an_ellipse_fills_its_box_exactly()
    {
        // An ellipse that overflows its box would be clipped by the
        // annotation's bounds; one that underfills it looks wrong against the
        // rectangle drawn from the same drag.
        var s = Draft(ShapeKind.Ellipse, 0.2, 0.1, 0.8, 0.5);
        var points = ShapeGeometry.Outline(s, 0.003);

        Assert.Equal(s.Left, points.Min(p => p.X), 6);
        Assert.Equal(s.Right, points.Max(p => p.X), 6);
        Assert.Equal(s.Top, points.Min(p => p.Y), 6);
        Assert.Equal(s.Bottom, points.Max(p => p.Y), 6);
    }

    [Fact]
    public void an_ellipse_outline_closes()
    {
        var points = ShapeGeometry.Outline(Draft(ShapeKind.Ellipse, 0.2, 0.2, 0.6, 0.4), 0.003);

        Assert.Equal(points[0].X, points[^1].X, 9);
        Assert.Equal(points[0].Y, points[^1].Y, 9);
    }

    [Fact]
    public void a_line_outline_is_just_its_two_ends()
    {
        var points = ShapeGeometry.Outline(Draft(ShapeKind.Line, 0.1, 0.1, 0.9, 0.4), 0.003);

        Assert.Equal(2, points.Count);
        Assert.Equal((0.1, 0.1), points[0]);
        Assert.Equal((0.9, 0.4), points[1]);
    }

    [Fact]
    public void an_arrow_shaft_stops_at_the_head_rather_than_running_through_it()
    {
        // A stroked line continuing under a filled triangle pokes out past the
        // point and blunts it, which is what the first version looked like.
        var s = Draft(ShapeKind.Arrow, 0.1, 0.5, 0.9, 0.5);
        var shaft = ShapeGeometry.Outline(s, 0.006);

        Assert.Equal(2, shaft.Count);
        Assert.Equal((s.X1, s.Y1), shaft[0]);
        Assert.True(shaft[1].X < s.X2, $"the shaft runs to {shaft[1].X}, past the head base");
    }

    [Fact]
    public void an_arrow_head_is_a_solid_triangle_at_the_tip()
    {
        var s = Draft(ShapeKind.Arrow, 0.1, 0.5, 0.9, 0.5);
        var head = ShapeGeometry.ArrowHeadTriangle(s, 0.006);

        Assert.Equal(3, head.Count);
        Assert.Equal((s.X2, s.Y2), head[0]);

        // The two base corners straddle the shaft, behind the tip.
        Assert.True(head[1].X < s.X2 && head[2].X < s.X2);
        Assert.True((head[1].Y - 0.5) * (head[2].Y - 0.5) < 0, "the base corners are on the same side");
    }

    [Fact]
    public void an_arrow_head_has_the_proportions_of_an_arrow()
    {
        // Roughly 5:2, length to full width. A head as wide as it is long reads
        // as a diamond; a very narrow one reads as a tick.
        var s = Draft(ShapeKind.Arrow, 0.0, 0.5, 1.0, 0.5);
        var head = ShapeGeometry.ArrowHeadTriangle(s, 0.01);

        double length = head[0].X - head[1].X;
        double width = Math.Abs(head[1].Y - head[2].Y);
        double ratio = length / width;

        Assert.InRange(ratio, 1.0, 1.5);
    }

    [Fact]
    public void a_short_drag_does_not_get_a_head_longer_than_the_arrow()
    {
        // Unclamped, a thick pen on a short drag puts the head base BEHIND the
        // tail, which draws a triangle pointing the wrong way.
        var s = Draft(ShapeKind.Arrow, 0.5, 0.5, 0.53, 0.5);
        var a = ShapeGeometry.Arrow(s.X1, s.Y1, s.X2, s.Y2, 0.02);

        Assert.True(a.ShaftEnd.X > s.X1, "the head swallowed the whole shaft");
        Assert.True(a.ShaftEnd.X <= s.X2);
    }

    [Fact]
    public void every_kind_produces_at_least_a_drawable_polyline()
    {
        foreach (ShapeKind kind in Enum.GetValues<ShapeKind>())
        {
            var points = ShapeGeometry.Outline(Draft(kind, 0.2, 0.2, 0.6, 0.5), 0.003);
            Assert.True(points.Count >= 2, $"{kind} produced {points.Count} points");
            Assert.All(points, p =>
            {
                Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), $"{kind} produced a non-finite point");
            });
        }
    }

    // ---- The click-versus-drag floor ----

    [Fact]
    public void a_click_without_a_drag_is_not_a_shape()
    {
        // Otherwise a stray click with the tool armed leaves a zero-sized mark
        // on the page that is nearly impossible to see, select or delete.
        foreach (ShapeKind kind in Enum.GetValues<ShapeKind>())
        {
            Assert.False(ShapeGeometry.IsWorthDrawing(Draft(kind, 0.5, 0.5, 0.5, 0.5)), $"{kind}");
            Assert.False(ShapeGeometry.IsWorthDrawing(Draft(kind, 0.5, 0.5, 0.5004, 0.5004)), $"{kind} (tiny)");
        }
    }

    [Fact]
    public void a_real_drag_is_a_shape()
    {
        foreach (ShapeKind kind in Enum.GetValues<ShapeKind>())
        {
            Assert.True(ShapeGeometry.IsWorthDrawing(Draft(kind, 0.2, 0.2, 0.5, 0.5)), $"{kind}");
        }
    }

    [Fact]
    public void a_diagonal_drag_counts_even_when_neither_side_alone_would()
    {
        // A short diagonal has real length while its width and height are each
        // below the floor. Testing the axes separately would reject a line the
        // user can plainly see they drew.
        double half = ShapeGeometry.MinDragLength * 0.8;
        var s = Draft(ShapeKind.Line, 0.5, 0.5, 0.5 + half, 0.5 + half);

        Assert.True(ShapeGeometry.IsWorthDrawing(s));
    }

    [Fact]
    public void a_deliberately_flat_rectangle_is_allowed()
    {
        // A wide, zero-height rectangle is a legitimate way to rule a line
        // under something, so the floor must not require both axes.
        var s = Draft(ShapeKind.Rectangle, 0.2, 0.5, 0.8, 0.5);
        Assert.True(ShapeGeometry.IsWorthDrawing(s));
    }
}
