using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class GroupRotationTests
{
    private static (double Left, double Top, double Right, double Bottom) Rect(
        double l, double t, double r, double b) => (l, t, r, b);

    [Fact]
    public void The_bounding_box_covers_every_member()
    {
        var box = GroupRotation.BoundingBox(new[]
        {
            Rect(0.20, 0.45, 0.30, 0.55),
            Rect(0.45, 0.40, 0.55, 0.50),
            Rect(0.70, 0.45, 0.80, 0.55),
        });

        Assert.NotNull(box);
        Assert.Equal(0.20, box!.Value.Left, 6);
        Assert.Equal(0.40, box.Value.Top, 6);
        Assert.Equal(0.80, box.Value.Right, 6);
        Assert.Equal(0.55, box.Value.Bottom, 6);
    }

    [Fact]
    public void An_empty_selection_has_no_bounding_box()
    {
        Assert.Null(GroupRotation.BoundingBox(Array.Empty<(double, double, double, double)>()));
    }

    [Fact]
    public void A_quarter_turn_is_clockwise_as_the_user_sees_it()
    {
        // Screen coordinates: y runs DOWN. A point to the RIGHT of the pivot
        // must end up BELOW it after a clockwise quarter turn. Getting this
        // backwards is what made stamp rotation turn the wrong way in v2.4.0.
        var (x, y) = GroupRotation.RotatePointAbout(1.0, 0.0, 0.0, 0.0, 90);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(1.0, y, 6);
    }

    [Fact]
    public void Rotating_about_a_point_leaves_that_point_alone()
    {
        var (x, y) = GroupRotation.RotatePointAbout(0.5, 0.5, 0.5, 0.5, 37);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
    }

    [Fact]
    public void A_row_of_three_becomes_a_column_after_a_quarter_turn()
    {
        // The whole point of orbiting: members change POSITION, not just
        // orientation. Three side by side must end up stacked.
        var rects = new[]
        {
            Rect(0.20, 0.45, 0.30, 0.55),
            Rect(0.45, 0.45, 0.55, 0.55),
            Rect(0.70, 0.45, 0.80, 0.55),
        };
        var box = GroupRotation.BoundingBox(rects)!.Value;
        double px = (box.Left + box.Right) / 2.0;
        double py = (box.Top + box.Bottom) / 2.0;
        Assert.Equal(0.50, px, 6);
        Assert.Equal(0.50, py, 6);

        var turned = new List<(double L, double T, double R, double B)>();
        foreach (var r in rects) { turned.Add(GroupRotation.OrbitRect(r, px, py, 90)); }

        // All three now share an x centre, and their y centres are spread.
        foreach (var t in turned)
        {
            Assert.Equal(0.50, (t.L + t.R) / 2.0, 6);
        }
        Assert.Equal(0.25, (turned[0].T + turned[0].B) / 2.0, 6);
        Assert.Equal(0.50, (turned[1].T + turned[1].B) / 2.0, 6);
        Assert.Equal(0.75, (turned[2].T + turned[2].B) / 2.0, 6);
    }

    [Fact]
    public void Orbiting_keeps_each_rectangles_own_size()
    {
        // The member's own turn is carried as its ANGLE, not by reshaping its
        // box. Reshaping here would compound with the enlarged box the writer
        // already produces for a rotated mark.
        var r = Rect(0.10, 0.20, 0.40, 0.30);
        var moved = GroupRotation.OrbitRect(r, 0.5, 0.5, 41);

        Assert.Equal(0.30, moved.Right - moved.Left, 6);
        Assert.Equal(0.10, moved.Bottom - moved.Top, 6);
    }

    [Fact]
    public void Four_quarter_turns_return_every_member_to_where_it_started()
    {
        var r = Rect(0.15, 0.25, 0.35, 0.45);
        var moved = r;
        for (int i = 0; i < 4; i++)
        {
            moved = GroupRotation.OrbitRect(moved, 0.5, 0.5, 90);
        }

        Assert.Equal(r.Left, moved.Left, 6);
        Assert.Equal(r.Top, moved.Top, 6);
        Assert.Equal(r.Right, moved.Right, 6);
        Assert.Equal(r.Bottom, moved.Bottom, 6);
    }

    [Fact]
    public void A_zero_degree_turn_changes_nothing()
    {
        var r = Rect(0.11, 0.22, 0.33, 0.44);
        var moved = GroupRotation.OrbitRect(r, 0.5, 0.5, 0);
        Assert.Equal(r.Left, moved.Left, 6);
        Assert.Equal(r.Top, moved.Top, 6);
        Assert.Equal(r.Right, moved.Right, 6);
        Assert.Equal(r.Bottom, moved.Bottom, 6);
    }
}
