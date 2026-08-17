using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What the corrected ink renderer does to a shape at each view rotation.
///
/// The renderer itself is a WinUI page this assembly cannot load, so the
/// projection is mirrored here and RotationEvidenceTests holds the real methods
/// to the same arithmetic. What is asserted below is deliberately not the
/// formula restated: it is the PROPERTIES that formula has to have. A rectangle
/// wider than it is tall must come out taller than it is wide after a quarter
/// turn. An arrow's head must stay on the end of its own shaft. The preview and
/// the committed mark must be the same thing. And at 0 nothing may move at all.
/// </summary>
public class ShapeRotationProjectionTests
{
    private const double Scale = 800;          // OverlayScale
    private const double ContentH = 1035;      // an A4-ish portrait page

    private static PageTransform View(int rotation) =>
        PageTransform.For(Scale, ContentH, rotation, Scale);

    /// <summary>What BuildStrokePolyline now does to one point.</summary>
    private static (double X, double Y) Project(
        (double X, double Y) normalized, PageTransform view, double pageTop)
    {
        var (cx, cy) = view.ToCard(normalized.X * Scale, normalized.Y * Scale);
        return (cx, cy + pageTop);
    }

    /// <summary>What it used to do, kept verbatim as the 0-degree control.</summary>
    private static (double X, double Y) Legacy((double X, double Y) n, double pageTop) =>
        (n.X * Scale, (n.Y * Scale) + pageTop);

    private static double Thickness(double strokeWidth, PageTransform view) =>
        strokeWidth * Scale * view.Scale;

    private static (double L, double T, double R, double B) BoundsOf(
        System.Collections.Generic.IEnumerable<(double X, double Y)> points)
    {
        var list = points.ToList();
        return (list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
    }

    private static readonly (double X, double Y)[] Sample =
    [
        (0.0, 0.0), (0.15, 0.10), (0.62, 0.37), (1.0, 0.55), (0.5, 0.9),
    ];

    // ---- 0 degrees must be exactly what it always was ----

    [Fact]
    public void at_zero_degrees_every_point_is_where_it_has_always_been()
    {
        // Requirement 5, asserted with exact equality and no tolerance. Routing
        // marks through the new projection must not move a single unrotated
        // document by a fraction of a pixel.
        var view = View(0);

        foreach (double pageTop in new[] { 0.0, 1051.0, 98765.25 })
        {
            foreach (var n in Sample)
            {
                var now = Project(n, view, pageTop);
                var before = Legacy(n, pageTop);

                Assert.Equal(before.X, now.X);
                Assert.Equal(before.Y, now.Y);
            }
        }
    }

    [Fact]
    public void at_zero_degrees_the_thickness_is_unchanged_too()
    {
        Assert.Equal(0.004 * Scale, Thickness(0.004, View(0)));
    }

    // ---- the turned cases ----

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void a_turned_page_moves_its_marks(int rotation)
    {
        // The defect commit 1 measured: at every rotation the ink layer produced
        // byte-identical output. Anything that still does has not been fixed.
        var turned = View(rotation);

        Assert.NotEqual(
            BoundsOf(Sample.Select(n => Project(n, View(0), 0))),
            BoundsOf(Sample.Select(n => Project(n, turned, 0))));
    }

    [Fact]
    public void a_quarter_turn_makes_a_wide_shape_tall()
    {
        // Orientation, not merely position. A shape that moved but kept its
        // proportions would pass a position check and still be drawn upright on
        // a sideways page.
        var wide = new[] { (0.10, 0.10), (0.90, 0.10), (0.90, 0.30), (0.10, 0.30) };

        var upright = BoundsOf(wide.Select(n => Project(n, View(0), 0)));
        var turned = BoundsOf(wide.Select(n => Project(n, View(90), 0)));

        Assert.True(upright.R - upright.L > upright.B - upright.T, "fixture is not wide");
        Assert.True(turned.B - turned.T > turned.R - turned.L,
                    "a wide shape did not become tall after a quarter turn");
    }

    [Fact]
    public void a_half_turn_keeps_the_shape_and_only_moves_it()
    {
        // 180 does not swap the axes, so the footprint is the same size in a
        // different place. A transform that transposed here would be wrong in a
        // way a quarter-turn test cannot see.
        var shape = new[] { (0.10, 0.10), (0.60, 0.10), (0.60, 0.40), (0.10, 0.40) };

        var a = BoundsOf(shape.Select(n => Project(n, View(0), 0)));
        var b = BoundsOf(shape.Select(n => Project(n, View(180), 0)));

        Assert.Equal(a.R - a.L, b.R - b.L, 6);
        Assert.Equal(a.B - a.T, b.B - b.T, 6);
        Assert.NotEqual(a.L, b.L, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void a_mark_stays_inside_its_own_page_slot(int rotation)
    {
        // Page boundaries. A mark on page 3 must land between page 3's top and
        // its bottom, whatever the rotation, or it draws over its neighbour.
        var view = View(rotation);
        double pageTop = 4000;

        foreach ((double X, double Y) n in new[]
        {
            (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (0.5, 0.5),
        })
        {
            // The content box is ContentH tall in normalized-by-width terms, so
            // the far edge of the page is at ContentH / Scale.
            (double X, double Y) clamped = (n.X, n.Y * (ContentH / Scale));
            var (_, y) = Project(clamped, view, pageTop);

            Assert.InRange(y, pageTop - 0.001, pageTop + view.CardHeight + 0.001);
        }
    }

    // ---- thickness ----

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(180, 1.0)]
    public void an_upright_or_inverted_page_keeps_the_stroke_weight(int rotation, double factor)
    {
        Assert.Equal(0.004 * Scale * factor, Thickness(0.004, View(rotation)), 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void a_page_on_its_side_thins_the_stroke_with_everything_else(int rotation)
    {
        // The content is scaled down to bring its height to the card's width, so
        // a stroke that kept its weight would be too heavy for the shape it
        // outlines.
        var view = View(rotation);

        Assert.Equal(0.004 * Scale * (Scale / ContentH), Thickness(0.004, view), 6);
        Assert.True(Thickness(0.004, view) < Thickness(0.004, View(0)));
    }

    // ---- the arrow ----

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void an_arrows_head_stays_on_the_end_of_its_own_shaft(int rotation)
    {
        // The head is a separate polygon built by a separate method. If only one
        // of the two learned the transform, the tip detaches from the line, and
        // it would do so only while rotated.
        var draft = new ShapeDraft(ShapeKind.Arrow, 0.15, 0.20, 0.70, 0.55);
        var shape = new ShapeAnnotation(0, draft, "#FF000000", 0.004);
        var view = View(rotation);

        var shaft = shape.Outline.Select(p => Project(p, view, 0)).ToList();
        var head = shape.Head.Select(p => Project(p, view, 0)).ToList();

        Assert.Equal(3, head.Count);

        // The shaft's far end must be within the head's footprint.
        var end = shaft[^1];
        var (l, t, r, b) = BoundsOf(head);

        Assert.InRange(end.X, l - 0.001, r + 0.001);
        Assert.InRange(end.Y, t - 0.001, b + 0.001);
    }

    // ---- the live preview ----

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_preview_and_the_committed_shape_are_the_same_geometry(int rotation)
    {
        // A drag previews from ShapeGeometry.Outline and commits from the same
        // function, so with one projection over both they cannot disagree. This
        // pins that they take the SAME transform: the preview resolving a
        // different page's transform is the way this breaks.
        var draft = new ShapeDraft(ShapeKind.Rectangle, 0.12, 0.12, 0.55, 0.42);
        var view = View(rotation);
        const double pageTop = 2070;

        var preview = ShapeGeometry.Outline(draft, 0.004)
            .Select(p => Project(p, view, pageTop)).ToList();

        var committed = new ShapeAnnotation(0, draft, "#FF000000", 0.004).Outline
            .Select(p => Project(p, view, pageTop)).ToList();

        Assert.Equal(preview, committed);
    }

    // ---- the text box preview ----

    /// <summary>What CardRect now does: project both corners, THEN take bounds.</summary>
    private static (double L, double T, double W, double H) CardRect(
        PageTransform view, double pageTop, double nx1, double ny1, double nx2, double ny2)
    {
        var a = view.ToCard(nx1 * Scale, ny1 * Scale);
        var b = view.ToCard(nx2 * Scale, ny2 * Scale);

        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y) + pageTop,
                Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    [Fact]
    public void at_zero_degrees_the_text_box_preview_is_where_it_always_was()
    {
        var box = CardRect(View(0), 500, 0.2, 0.3, 0.6, 0.5);

        Assert.Equal(0.2 * Scale, box.L, 6);
        Assert.Equal((0.3 * Scale) + 500, box.T, 6);
        Assert.Equal(0.4 * Scale, box.W, 6);
        Assert.Equal(0.2 * Scale, box.H, 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void a_quarter_turn_swaps_the_text_boxs_width_and_height(int rotation)
    {
        // A quarter turn maps an axis-aligned rectangle to an axis-aligned
        // rectangle with the axes exchanged and everything scaled, so no
        // RenderTransform is needed and the swap is the observable consequence.
        var view = View(rotation);
        var upright = CardRect(View(0), 0, 0.2, 0.3, 0.6, 0.5);
        var turned = CardRect(view, 0, 0.2, 0.3, 0.6, 0.5);

        Assert.Equal(upright.H * view.Scale, turned.W, 6);
        Assert.Equal(upright.W * view.Scale, turned.H, 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_text_box_corner_is_taken_after_the_turn_not_before(int rotation)
    {
        // Projecting only the normalized top-left would keep a corner that is
        // no longer the top-left once the page is turned, putting the box a
        // whole width or height away from where it belongs.
        var view = View(rotation);

        var correct = CardRect(view, 0, 0.2, 0.3, 0.6, 0.5);
        var naive = view.ToCard(0.2 * Scale, 0.3 * Scale);

        Assert.True(Math.Abs(correct.L - naive.X) > 1 || Math.Abs(correct.T - naive.Y) > 1,
                    $"at {rotation} the naive corner happened to be right; fixture is not discriminating");
    }
}
