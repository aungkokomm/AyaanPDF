using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Turning a freehand stroke.
///
/// The stroke's tag keeps the UPRIGHT points and an angle, and the rotated
/// points are what gets written to the page. Baking the rotation into the
/// stored points would look identical right up until the next resize: a drag
/// scales a point cloud along the PAGE's axes, so a stroke turned 45 degrees
/// and then stretched sideways would come out sheared rather than wider.
/// Keeping upright geometry plus an angle is what makes a shape immune to that,
/// and these pin that the same holds here.
/// </summary>
public class InkRotationTests
{
    private static readonly List<(double X, double Y)> Squiggle =
    [
        (0.10, 0.20), (0.20, 0.10), (0.30, 0.30), (0.40, 0.15), (0.50, 0.25),
    ];

    private static (double W, double H) BoxOf(IReadOnlyList<(double X, double Y)> pts) =>
        (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));

    // ---------------- The rotation itself ----------------

    [Fact]
    public void turning_by_nothing_leaves_every_point_alone()
    {
        var turned = Geometry2D.RotateAboutCentre(Squiggle, 0);

        Assert.Equal(Squiggle.Count, turned.Count);
        for (int i = 0; i < Squiggle.Count; i++)
        {
            Assert.Equal(Squiggle[i].X, turned[i].X, 12);
            Assert.Equal(Squiggle[i].Y, turned[i].Y, 12);
        }
    }

    [Fact]
    public void a_quarter_turn_swaps_the_strokes_width_and_height()
    {
        // 0.4 x 0.2 upright becomes 0.2 x 0.4 on its side.
        var turned = Geometry2D.RotateAboutCentre(Squiggle, 90);
        var box = BoxOf(turned);

        Assert.Equal(0.2, box.W, 9);
        Assert.Equal(0.4, box.H, 9);
    }

    [Fact]
    public void the_point_turned_about_stays_exactly_where_it_is()
    {
        foreach (double deg in new[] { 30.0, 45.0, 90.0, 200.0, 359.0 })
        {
            var (x, y) = Geometry2D.Rotate(0.3, 0.4, 0.3, 0.4, deg);
            Assert.Equal(0.3, x, 12);
            Assert.Equal(0.4, y, 12);
        }
    }

    [Fact]
    public void turning_clockwise_is_the_inverse_of_turning_back()
    {
        // The forward and inverse rotations have to agree, or a stroke would
        // drift a little every time it was turned and turned back. Getting the
        // sign wrong here is invisible at 0 and 180 degrees.
        //
        // About an EXPLICIT centre, because that is the operation that has an
        // inverse; see the test below for why the convenience overload does not.
        foreach (double deg in new[] { 15.0, 45.0, 90.0, 137.5 })
        {
            var there = Geometry2D.Rotate(Squiggle, 0.3, 0.2, deg);
            var back = Geometry2D.Rotate(there, 0.3, 0.2, -deg);

            for (int i = 0; i < Squiggle.Count; i++)
            {
                Assert.Equal(Squiggle[i].X, back[i].X, 9);
                Assert.Equal(Squiggle[i].Y, back[i].Y, 9);
            }
        }
    }

    [Fact]
    public void turning_about_the_bounding_box_centre_is_NOT_its_own_inverse()
    {
        // A trap worth pinning rather than discovering. Turning a point cloud
        // MOVES its bounding box, so the centre computed from the result is a
        // different point from the one it was turned about, and turning back
        // lands somewhere else.
        //
        // This is exactly why a stroke stores its upright points and an
        // absolute angle, and is turned from upright every time, instead of
        // being nudged from wherever it currently sits. Anyone who "simplifies"
        // that to an incremental rotation gets drift on every drag.
        var there = Geometry2D.RotateAboutCentre(Squiggle, 45);
        var back = Geometry2D.RotateAboutCentre(there, -45);

        bool drifted = Squiggle.Zip(back).Any(p =>
            Math.Abs(p.First.X - p.Second.X) > 1e-6 || Math.Abs(p.First.Y - p.Second.Y) > 1e-6);
        Assert.True(drifted, "if this ever round-trips, the note above needs revisiting");
    }

    [Fact]
    public void a_full_turn_comes_back_to_the_start()
    {
        var turned = Geometry2D.RotateAboutCentre(Squiggle, 360);

        for (int i = 0; i < Squiggle.Count; i++)
        {
            Assert.Equal(Squiggle[i].X, turned[i].X, 9);
            Assert.Equal(Squiggle[i].Y, turned[i].Y, 9);
        }
    }

    [Fact]
    public void the_rotation_matches_the_apps_own_inverse()
    {
        // The overlay turns a selection frame one way and the hit test turns the
        // pointer back the other. A stroke has to be drawn by the SAME
        // convention or it would appear at an angle nothing else agreed with.
        var (x, y) = Geometry2D.Rotate(0.5, 0.2, 0.3, 0.3, 40);
        var (bx, by) = Geometry2D.InverseRotate(x, y, 0.3, 0.3, 40);

        Assert.Equal(0.5, bx, 9);
        Assert.Equal(0.2, by, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void a_degenerate_stroke_survives_being_turned(int count)
    {
        var pts = Squiggle.Take(count).ToList();
        var turned = Geometry2D.RotateAboutCentre(pts, 45);

        Assert.Equal(count, turned.Count);
        Assert.All(turned, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    // ---------------- The tag ----------------

    [Fact]
    public void a_turned_stroke_records_its_angle()
    {
        string tag = InkTag.Write("FFFF0000", 0.004, Squiggle, 42.5);

        Assert.True(InkTag.TryParse(tag, out _, out _, out var points, out double deg));
        Assert.Equal(42.5, deg, 6);
        Assert.Equal(Squiggle.Count, points.Count);
    }

    [Fact]
    public void the_points_on_the_tag_stay_upright()
    {
        // The whole design: what is stored is what was drawn, and the angle is
        // applied on the way to the page. If the stored points were the turned
        // ones, this box would be the rotated one.
        string tag = InkTag.Write("FFFF0000", 0.004, Squiggle, 90);

        Assert.True(InkTag.TryParse(tag, out _, out _, out var points, out _));
        var box = BoxOf(points);
        Assert.Equal(0.4, box.W, 3);
        Assert.Equal(0.2, box.H, 3);
    }

    [Fact]
    public void a_stroke_with_no_rotation_is_written_exactly_as_before()
    {
        // Byte-identical, so every stroke in every existing file stays as it is
        // and no diff appears where nothing changed.
        Assert.Equal(
            InkTag.Write("FFFF0000", 0.004, Squiggle),
            InkTag.Write("FFFF0000", 0.004, Squiggle, 0));
    }

    [Fact]
    public void a_stroke_written_before_rotation_existed_reads_as_upright()
    {
        Assert.True(InkTag.TryParse(
            "AyaanInk:FFFF0000:0.004:0.1,0.1;0.5,0.5", out _, out _, out var pts, out double deg));

        Assert.Equal(0, deg);
        Assert.Equal(2, pts.Count);
    }

    [Fact]
    public void a_corrupt_angle_reads_as_upright_rather_than_breaking_the_stroke()
    {
        // One damaged byte at the end of a tag must not turn a good drawing
        // into an unreadable mark, the same rule every other tag follows.
        Assert.True(InkTag.TryParse(
            "AyaanInk:FFFF0000:0.004:0.1,0.1;0.5,0.5:zzz", out _, out _, out var pts, out double deg));

        Assert.Equal(0, deg);
        Assert.Equal(2, pts.Count);
    }

    [Fact]
    public void the_angle_survives_a_write_and_read_round_trip()
    {
        foreach (double deg in new[] { 15.0, 90.0, 180.0, 270.5 })
        {
            string tag = InkTag.Write("FF00FF00", 0.01, Squiggle, deg);
            Assert.True(InkTag.TryParse(tag, out _, out _, out _, out double read));
            Assert.Equal(deg, read, 2);
        }
    }

    // ---------------- Rotation and resize together ----------------

    [Fact]
    public void resizing_a_turned_stroke_scales_it_without_shearing_it()
    {
        // THE reason the angle is stored rather than baked in. Scale the
        // UPRIGHT points and turn the result, and the stroke is genuinely
        // wider. Turn first and then scale along the page's axes, as a baked
        // point cloud would be, and it comes out skewed: a right angle in the
        // drawing stops being one.
        var upright = InkTag.ScaleTo(Squiggle, 0.1, 0.1, 0.9, 0.3);   // twice as wide
        var correct = Geometry2D.RotateAboutCentre(upright, 45);

        var baked = Geometry2D.RotateAboutCentre(Squiggle, 45);
        var sheared = InkTag.ScaleTo(baked, 0.1, 0.1, 0.9, 0.3);

        // The two disagree, which is the whole point of the design.
        bool differs = correct.Zip(sheared).Any(p =>
            Math.Abs(p.First.X - p.Second.X) > 0.01 || Math.Abs(p.First.Y - p.Second.Y) > 0.01);
        Assert.True(differs, "scaling before and after the turn should not agree");

        // And the correct one preserves the stroke's proportions: turning it
        // back leaves exactly the box that was asked for.
        var restored = Geometry2D.RotateAboutCentre(correct, -45);
        Assert.Equal(0.8, BoxOf(restored).W, 6);
        Assert.Equal(0.2, BoxOf(restored).H, 6);
    }
}
