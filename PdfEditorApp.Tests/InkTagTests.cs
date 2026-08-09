using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The stroke description that makes a drawing an object: undo rebuilds from
/// it, and resize re-draws from it.
/// </summary>
public class InkTagTests
{
    private static readonly List<(double X, double Y)> Squiggle =
        new() { (0.10, 0.20), (0.20, 0.30), (0.30, 0.25), (0.40, 0.40) };

    [Fact]
    public void a_stroke_round_trips_through_its_tag()
    {
        string tag = InkTag.Write("FF0000FF", 0.004, Squiggle);

        Assert.True(InkTag.TryParse(tag, out string color, out double width, out var pts));
        Assert.Equal("FF0000FF", color);
        Assert.Equal(0.004, width, 6);
        Assert.Equal(Squiggle.Count, pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            Assert.Equal(Squiggle[i].X, pts[i].X, 4);
            Assert.Equal(Squiggle[i].Y, pts[i].Y, 4);
        }
    }

    [Fact]
    public void the_id_prefix_is_stripped_before_matching()
    {
        // Identity is written into the same field. Every tag parser in this app
        // has to strip it first; the one that forgot silently broke every shape
        // for two weeks.
        string tag = "ID:" + new string('a', 32) + "|" + InkTag.Write("00FF00FF", 0.003, Squiggle);

        Assert.True(InkTag.TryParse(tag, out _, out _, out var pts));
        Assert.Equal(Squiggle.Count, pts.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AyaanShape:0:FF0000FF:2:0.1:0.2")]
    [InlineData("AyaanInk:FF0000FF")]
    [InlineData("AyaanInk:FF0000FF:0.004:0.1,0.2")]
    public void anything_that_is_not_a_stroke_is_refused(string? tag)
    {
        // Including a one-point tag: a stroke needs two points to be a line,
        // and accepting one would put a degenerate annotation in the document.
        Assert.False(InkTag.TryParse(tag, out _, out _, out _));
    }

    [Fact]
    public void a_shape_tag_is_not_mistaken_for_a_stroke()
    {
        Assert.False(InkTag.TryParse("AyaanShape:4:FF0000FF:2:0.1:0.2:0:0:0.18", out _, out _, out _));
    }

    [Fact]
    public void scaling_maps_the_stroke_onto_the_target_box()
    {
        // The whole point of resize: the stroke is re-drawn at the new size
        // from its own description, not stretched as pixels.
        var scaled = InkTag.ScaleTo(Squiggle, 0.5, 0.5, 0.9, 0.9);
        var (l, t, r, b) = InkTag.Bounds(scaled);

        Assert.Equal(0.5, l, 6);
        Assert.Equal(0.5, t, 6);
        Assert.Equal(0.9, r, 6);
        Assert.Equal(0.9, b, 6);
    }

    [Fact]
    public void scaling_preserves_the_shape_of_the_stroke()
    {
        // Doubling the box must double every gap between points, or the
        // signature comes back distorted rather than bigger.
        var scaled = InkTag.ScaleTo(Squiggle, 0.0, 0.0, 0.6, 0.6);
        var (sl, st, sr, sb) = InkTag.Bounds(Squiggle);

        double kx = 0.6 / (sr - sl);
        double ky = 0.6 / (sb - st);
        for (int i = 0; i < Squiggle.Count; i++)
        {
            Assert.Equal((Squiggle[i].X - sl) * kx, scaled[i].X, 6);
            Assert.Equal((Squiggle[i].Y - st) * ky, scaled[i].Y, 6);
        }
    }

    [Fact]
    public void a_perfectly_flat_stroke_does_not_divide_by_zero()
    {
        // A ruler-straight horizontal line has a zero-height box. Scaling by it
        // would produce NaN for every point and write a corrupt annotation.
        var flat = new List<(double X, double Y)> { (0.1, 0.5), (0.3, 0.5), (0.6, 0.5) };

        var scaled = InkTag.ScaleTo(flat, 0.2, 0.2, 0.8, 0.4);
        foreach (var p in scaled)
        {
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
            Assert.Equal(0.3, p.Y, 6);   // centred in the target box
        }
        Assert.Equal(0.2, scaled[0].X, 6);
        Assert.Equal(0.8, scaled[^1].X, 6);
    }

    [Fact]
    public void the_rebuilt_curve_matches_the_one_that_was_drawn()
    {
        // The tag stores control points and the fit is re-run on load, so this
        // is the claim that makes that safe: same points in, same curve out.
        var raw = new List<(double X, double Y)>();
        for (int i = 0; i < 40; i++) { raw.Add((0.1 + i * 0.01, 0.5 + System.Math.Sin(i * 0.3) * 0.05)); }

        var control = StrokeSmoothing.Thin(raw);
        var drawn = StrokeSmoothing.Fit(control);

        string tag = InkTag.Write("000000FF", 0.003, control);
        Assert.True(InkTag.TryParse(tag, out _, out _, out var reloaded));
        var rebuilt = StrokeSmoothing.Fit(reloaded);

        Assert.Equal(drawn.Count, rebuilt.Count);

        // Measured as a distance rather than by decimal places: the tag rounds
        // to four places, so a coordinate can sit either side of a rounding
        // boundary and fail a places-based check while being a thousandth of a
        // pixel away. What matters is that the rebuilt stroke lands within a
        // fraction of a pixel, and 5e-4 of the page width is about half a pixel
        // on a 900px render.
        double worst = 0;
        for (int i = 0; i < drawn.Count; i++)
        {
            double dx = drawn[i].X - rebuilt[i].X;
            double dy = drawn[i].Y - rebuilt[i].Y;
            worst = System.Math.Max(worst, System.Math.Sqrt(dx * dx + dy * dy));
        }
        Assert.True(worst < 5e-4, $"rebuilt stroke drifted {worst:F6} of the page width from the drawn one");
    }
}
