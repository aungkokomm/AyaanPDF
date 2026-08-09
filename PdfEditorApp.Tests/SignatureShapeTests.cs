using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A signature has one thing it must never get wrong: its proportions. These
/// pin that down at every step, capture and placement.
/// </summary>
public class SignatureShapeTests
{
    // Twice as wide as it is tall, which is roughly what a signature is.
    private static readonly List<(string, double, IReadOnlyList<(double X, double Y)>)> Wide = new()
    {
        ("FF000000", 0.004, new List<(double X, double Y)> { (0.2, 0.4), (0.6, 0.5), (0.2, 0.6) }),
        // Ends level with the first stroke's lowest point, so the whole thing
        // is exactly 0.4 wide by 0.2 tall and the ratios below are round.
        ("FF000000", 0.004, new List<(double X, double Y)> { (0.25, 0.6), (0.55, 0.6) }),
    };

    [Fact]
    public void capture_records_the_shape_of_what_was_drawn()
    {
        var sig = SignatureShape.FromDrawn("test", Wide);

        // 0.4 wide by 0.2 tall, so half as tall as it is wide.
        Assert.Equal(0.5, sig.AspectRatio, 6);
    }

    [Fact]
    public void capture_normalises_into_the_unit_box_without_stretching()
    {
        var sig = SignatureShape.FromDrawn("test", Wide);
        var pts = sig.Strokes.SelectMany(s => s.Points).ToList();

        Assert.All(pts, p => Assert.InRange(p.X, 0.0, 1.0));
        Assert.All(pts, p => Assert.InRange(p.Y, 0.0, 1.0));

        // The long side fills the box; the short side is centred, so it must
        // NOT reach the edges. Stretching to fill both is the bug.
        Assert.Equal(0.0, pts.Min(p => p.X), 6);
        Assert.Equal(1.0, pts.Max(p => p.X), 6);
        Assert.True(pts.Min(p => p.Y) > 0.2, "the short axis was stretched to fill the box");
    }

    [Fact]
    public void placing_keeps_the_signature_the_shape_it_was_drawn()
    {
        // A deliberately square target. A wide signature must NOT become square.
        var placed = SignatureShape.FromDrawn("test", Wide).PlaceInto(0.1, 0.1, 0.5, 0.5);
        var pts = placed.SelectMany(s => s.Points).ToList();

        double w = pts.Max(p => p.X) - pts.Min(p => p.X);
        double h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        Assert.Equal(0.5, h / w, 3);
    }

    [Fact]
    public void placing_stays_inside_the_box_it_was_given()
    {
        var placed = SignatureShape.FromDrawn("test", Wide).PlaceInto(0.2, 0.3, 0.8, 0.6);

        foreach (var p in placed.SelectMany(s => s.Points))
        {
            Assert.InRange(p.X, 0.2, 0.8);
            Assert.InRange(p.Y, 0.3, 0.6);
        }
    }

    [Fact]
    public void the_line_thickness_scales_with_the_signature()
    {
        // Placed at a quarter of the size, the pen must be a quarter as thick,
        // or a small signature comes out looking drawn with a marker.
        var sig = SignatureShape.FromDrawn("test", Wide);
        var big = sig.PlaceInto(0, 0, 1, 1);
        var small = sig.PlaceInto(0, 0, 0.25, 0.25);

        Assert.Equal(big[0].WidthNorm / 4, small[0].WidthNorm, 6);
    }

    [Fact]
    public void a_click_places_the_signature_centred_on_the_pointer()
    {
        var sig = SignatureShape.FromDrawn("test", Wide);
        var (l, t, r, b) = sig.BoxAt(0.5, 0.5, width: 0.3);

        Assert.Equal(0.5, (l + r) / 2, 6);
        Assert.Equal(0.5, (t + b) / 2, 6);
        Assert.Equal(0.3, r - l, 6);
        Assert.Equal(0.15, b - t, 6);   // half as tall as wide
    }

    [Fact]
    public void an_empty_capture_is_refused_rather_than_stored()
    {
        var sig = SignatureShape.FromDrawn("blank", new List<(string, double, IReadOnlyList<(double X, double Y)>)>());
        Assert.Empty(sig.Strokes);
    }

    [Fact]
    public void a_perfectly_flat_signature_does_not_divide_by_zero()
    {
        // Someone draws a single horizontal line. Zero height.
        var flat = new List<(string, double, IReadOnlyList<(double X, double Y)>)>
        {
            ("FF000000", 0.004, new List<(double X, double Y)> { (0.1, 0.5), (0.9, 0.5) }),
        };

        var placed = SignatureShape.FromDrawn("flat", flat).PlaceInto(0, 0, 0.5, 0.5);
        foreach (var p in placed.SelectMany(s => s.Points))
        {
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
        }
    }
}
