using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Redrawing a freehand stroke at a new size.
///
/// A stroke is rebuilt by mapping its stored control points from their own
/// bounding box onto the dragged rectangle. That is the whole of a resize, and
/// it is the same call the move path already makes with a same-sized rectangle,
/// so the machinery is exercised on every drag today.
///
/// These cover the matrix a resize has to survive: one axis at a time, both at
/// once, up and down, an ordinary squiggle, and the degenerate strokes that
/// have no extent on an axis to scale from.
/// </summary>
public class InkResizeTests
{
    /// <summary>An ordinary freehand squiggle: bounding box 0.1..0.5 by 0.1..0.3.</summary>
    private static readonly List<(double X, double Y)> Squiggle =
    [
        (0.10, 0.20), (0.20, 0.10), (0.30, 0.30), (0.40, 0.15), (0.50, 0.25),
    ];

    private static (double W, double H) BoxOf(IReadOnlyList<(double X, double Y)> pts) =>
        (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));

    private static void AssertBox(
        IReadOnlyList<(double X, double Y)> pts,
        double left, double top, double right, double bottom, int precision = 9)
    {
        Assert.Equal(left, pts.Min(p => p.X), precision);
        Assert.Equal(top, pts.Min(p => p.Y), precision);
        Assert.Equal(right, pts.Max(p => p.X), precision);
        Assert.Equal(bottom, pts.Max(p => p.Y), precision);
    }

    // ---------------- One axis at a time ----------------

    [Fact]
    public void widening_changes_only_the_horizontal_extent()
    {
        var scaled = InkTag.ScaleTo(Squiggle, 0.1, 0.1, 0.9, 0.3);

        AssertBox(scaled, 0.1, 0.1, 0.9, 0.3);
        Assert.Equal(0.8, BoxOf(scaled).W, 9);
        Assert.Equal(0.2, BoxOf(scaled).H, 9);
    }

    [Fact]
    public void heightening_changes_only_the_vertical_extent()
    {
        var scaled = InkTag.ScaleTo(Squiggle, 0.1, 0.1, 0.5, 0.9);

        AssertBox(scaled, 0.1, 0.1, 0.5, 0.9);
        Assert.Equal(0.4, BoxOf(scaled).W, 9);
        Assert.Equal(0.8, BoxOf(scaled).H, 9);
    }

    // ---------------- Both axes, up and down ----------------

    [Fact]
    public void a_non_uniform_resize_scales_each_axis_by_its_own_factor()
    {
        // x2 across, x4 down: the axes must not be coupled.
        var scaled = InkTag.ScaleTo(Squiggle, 0.0, 0.0, 0.8, 0.8);

        AssertBox(scaled, 0.0, 0.0, 0.8, 0.8);
        Assert.Equal(0.8, BoxOf(scaled).W, 9);
        Assert.Equal(0.8, BoxOf(scaled).H, 9);
    }

    [Fact]
    public void enlarging_keeps_the_stroke_recognisable()
    {
        var scaled = InkTag.ScaleTo(Squiggle, 0.0, 0.0, 0.8, 0.4);

        // Same point count, same order, and the same SHAPE: each point sits at
        // the same fraction across the box as it did before.
        Assert.Equal(Squiggle.Count, scaled.Count);
        for (int i = 0; i < Squiggle.Count; i++)
        {
            double beforeFrac = (Squiggle[i].X - 0.1) / 0.4;
            double afterFrac = (scaled[i].X - 0.0) / 0.8;
            Assert.Equal(beforeFrac, afterFrac, 9);
        }
    }

    [Fact]
    public void shrinking_works_the_same_way()
    {
        var scaled = InkTag.ScaleTo(Squiggle, 0.4, 0.4, 0.5, 0.45);

        AssertBox(scaled, 0.4, 0.4, 0.5, 0.45);
        Assert.Equal(Squiggle.Count, scaled.Count);
    }

    [Fact]
    public void a_stroke_shrunk_to_almost_nothing_still_produces_points()
    {
        // The grip drag clamps to a minimum, but nothing here may divide by the
        // destination size, so a sliver must still come back as real points
        // rather than NaN.
        var scaled = InkTag.ScaleTo(Squiggle, 0.5, 0.5, 0.5001, 0.5001);

        Assert.Equal(Squiggle.Count, scaled.Count);
        Assert.All(scaled, p =>
        {
            Assert.True(double.IsFinite(p.X), $"X was {p.X}");
            Assert.True(double.IsFinite(p.Y), $"Y was {p.Y}");
        });
    }

    // ---------------- Degenerate strokes ----------------

    [Fact]
    public void a_perfectly_horizontal_stroke_has_no_height_to_scale_from()
    {
        // Source height is zero, so there is no ratio. The points must land
        // somewhere sane inside the target rather than dividing by zero.
        var flat = new List<(double X, double Y)> { (0.1, 0.2), (0.3, 0.2), (0.5, 0.2) };
        var scaled = InkTag.ScaleTo(flat, 0.2, 0.2, 0.8, 0.4);

        Assert.Equal(3, scaled.Count);
        Assert.All(scaled, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.Equal(0.2, scaled.Min(p => p.X), 9);
        Assert.Equal(0.8, scaled.Max(p => p.X), 9);
        // No height to distribute, so every point takes the box's middle.
        Assert.All(scaled, p => Assert.Equal(0.3, p.Y, 9));
    }

    [Fact]
    public void a_perfectly_vertical_stroke_has_no_width_to_scale_from()
    {
        var flat = new List<(double X, double Y)> { (0.2, 0.1), (0.2, 0.3), (0.2, 0.5) };
        var scaled = InkTag.ScaleTo(flat, 0.2, 0.2, 0.6, 0.8);

        Assert.Equal(3, scaled.Count);
        Assert.All(scaled, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.All(scaled, p => Assert.Equal(0.4, p.X, 9));
        Assert.Equal(0.2, scaled.Min(p => p.Y), 9);
        Assert.Equal(0.8, scaled.Max(p => p.Y), 9);
    }

    [Fact]
    public void a_single_point_stroke_does_not_explode()
    {
        var dot = new List<(double X, double Y)> { (0.3, 0.3) };
        var scaled = InkTag.ScaleTo(dot, 0.1, 0.1, 0.5, 0.5);

        Assert.Single(scaled);
        Assert.True(double.IsFinite(scaled[0].X) && double.IsFinite(scaled[0].Y));
    }

    [Fact]
    public void an_empty_stroke_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(InkTag.ScaleTo([], 0.1, 0.1, 0.5, 0.5));
    }

    // ---------------- Repeated resizing ----------------

    [Fact]
    public void resizing_out_and_back_returns_the_original_stroke()
    {
        // The drift guard. Each rebuild re-derives the points from the CURRENT
        // bounding box, so a round trip must land back where it started rather
        // than accumulating a little error each time.
        var out1 = InkTag.ScaleTo(Squiggle, 0.0, 0.0, 0.8, 0.4);
        var back = InkTag.ScaleTo(out1, 0.1, 0.1, 0.5, 0.3);

        Assert.Equal(Squiggle.Count, back.Count);
        for (int i = 0; i < Squiggle.Count; i++)
        {
            Assert.Equal(Squiggle[i].X, back[i].X, 9);
            Assert.Equal(Squiggle[i].Y, back[i].Y, 9);
        }
    }

    [Fact]
    public void ten_resizes_do_not_let_the_stroke_creep()
    {
        // Nudge the box a little, ten times, then put it back exactly. The
        // stroke must be where it started: this is the same class of bug as the
        // stroke pad that grew a shape on every move.
        var pts = (IReadOnlyList<(double X, double Y)>)Squiggle;
        for (int i = 0; i < 10; i++)
        {
            pts = InkTag.ScaleTo(pts, 0.1, 0.1, 0.5 + (0.01 * i), 0.3 + (0.01 * i));
        }
        pts = InkTag.ScaleTo(pts, 0.1, 0.1, 0.5, 0.3);

        for (int i = 0; i < Squiggle.Count; i++)
        {
            Assert.Equal(Squiggle[i].X, pts[i].X, 9);
            Assert.Equal(Squiggle[i].Y, pts[i].Y, 9);
        }
    }

    // ---------------- The tag round trip a resize depends on ----------------

    [Fact]
    public void a_resized_stroke_survives_being_written_and_read_back()
    {
        // A resize rewrites the tag with the new points. If that round trip
        // lost precision the stroke would drift a little every time it was
        // resized, which no amount of correct scaling would fix.
        var scaled = InkTag.ScaleTo(Squiggle, 0.15, 0.25, 0.85, 0.65);
        string tag = InkTag.Write("FFFF0000", 0.004, scaled);

        Assert.True(InkTag.TryParse(tag, out string color, out double width, out var read));
        Assert.Equal("FFFF0000", color);
        Assert.Equal(0.004, width, 9);
        Assert.Equal(scaled.Count, read.Count);
        for (int i = 0; i < scaled.Count; i++)
        {
            Assert.Equal(scaled[i].X, read[i].X, 4);   // the tag stores 4 decimals
            Assert.Equal(scaled[i].Y, read[i].Y, 4);
        }
    }
}
