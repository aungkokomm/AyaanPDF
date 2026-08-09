using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Freehand smoothing. The properties that matter are the ones a signature
/// depends on: it must still start and end where the pen did, and it must not
/// invent a shape the user did not draw.
/// </summary>
public class StrokeSmoothingTests
{
    private static List<(double X, double Y)> Line(int n, double dx = 0.01)
    {
        var pts = new List<(double X, double Y)>();
        for (int i = 0; i < n; i++) { pts.Add((i * dx, 0.5)); }
        return pts;
    }

    [Fact]
    public void thinning_drops_samples_that_are_too_close_together()
    {
        // A pen resting still reports; without this the stroke carries a knot
        // of near-identical points wherever the hand paused.
        var raw = new List<(double X, double Y)>();
        for (int i = 0; i < 50; i++) { raw.Add((0.1 + i * 0.00001, 0.1)); }

        var kept = StrokeSmoothing.Thin(raw);
        Assert.True(kept.Count < 5, $"expected the cluster to collapse, kept {kept.Count}");
    }

    [Fact]
    public void thinning_always_keeps_the_first_and_last_sample()
    {
        // The last one is where the pen lifted. Dropping it because it sits a
        // hair from its predecessor visibly shortens a stroke that ended in a
        // slow curl, which is exactly how signatures end.
        var raw = new List<(double X, double Y)> { (0.1, 0.1), (0.5, 0.5), (0.500001, 0.500001) };
        var kept = StrokeSmoothing.Thin(raw);

        Assert.Equal((0.1, 0.1), kept[0]);
        Assert.Equal((0.500001, 0.500001), kept[^1]);
    }

    [Fact]
    public void smoothing_keeps_the_endpoints_exactly()
    {
        var raw = Line(10);
        var smooth = StrokeSmoothing.Smooth(raw);

        Assert.Equal(raw[0], smooth[0]);
        Assert.Equal(raw[^1].X, smooth[^1].X, 9);
        Assert.Equal(raw[^1].Y, smooth[^1].Y, 9);
    }

    [Fact]
    public void a_straight_stroke_stays_straight()
    {
        // Catmull-Rom passes through its control points, so a line must not
        // develop a wobble. A fit that overshoots here would put ripples in
        // every straight pen stroke.
        var smooth = StrokeSmoothing.Smooth(Line(12));

        foreach (var p in smooth)
        {
            Assert.Equal(0.5, p.Y, 9);
        }
    }

    [Fact]
    public void a_curve_gains_intermediate_points_rather_than_corners()
    {
        // Four samples round a corner should come back as a fitted arc, which
        // is the whole point: the faceting was the samples themselves.
        var raw = new List<(double X, double Y)>
        {
            (0.10, 0.50), (0.20, 0.45), (0.30, 0.50), (0.40, 0.55),
        };

        var smooth = StrokeSmoothing.Smooth(raw);
        Assert.True(smooth.Count > raw.Count * 3, $"expected a denser curve, got {smooth.Count}");
    }

    [Fact]
    public void the_fitted_curve_stays_near_the_points_it_was_drawn_through()
    {
        // Guards overshoot. Every fitted point on a gentle S must stay inside
        // the band the samples occupy, or the stroke bulges outside where the
        // pen actually went.
        var raw = new List<(double X, double Y)>
        {
            (0.10, 0.50), (0.20, 0.40), (0.30, 0.50), (0.40, 0.40), (0.50, 0.50),
        };

        foreach (var p in StrokeSmoothing.Smooth(raw))
        {
            Assert.InRange(p.Y, 0.38, 0.52);
            Assert.InRange(p.X, 0.10, 0.50);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void short_strokes_come_back_unchanged(int count)
    {
        // Two points are already the only curve through them, and one is a dot.
        // A fitter that assumed three would index off the end.
        var raw = new List<(double X, double Y)>();
        for (int i = 0; i < count; i++) { raw.Add((0.1 * i, 0.2)); }

        var smooth = StrokeSmoothing.Smooth(raw);
        Assert.Equal(count, smooth.Count);
    }

    [Fact]
    public void a_dot_does_not_blow_up()
    {
        // Tapping the pen sends many samples at one spot. Thinning collapses
        // them to one, and fitting must not divide by the zero-length span.
        var raw = new List<(double X, double Y)>();
        for (int i = 0; i < 30; i++) { raw.Add((0.42, 0.42)); }

        var smooth = StrokeSmoothing.Smooth(raw);
        Assert.Single(smooth);
        Assert.Equal((0.42, 0.42), smooth[0]);
    }

    [Fact]
    public void smoothing_is_deterministic()
    {
        // The preview and the write call this separately. If it were not a pure
        // function of its input the stroke would change shape when the pen
        // lifted, which is the split-path bug that cost two weeks on grouping.
        var raw = Line(20, 0.005);
        Assert.Equal(StrokeSmoothing.Smooth(raw), StrokeSmoothing.Smooth(raw));
    }
}
