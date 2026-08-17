using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The geometry behind view rotation.
///
/// Worth testing hard and in one place, because every visible symptom of
/// getting it wrong looks the same from the outside: the page is in the wrong
/// place, or clicks land somewhere other than where they were aimed.
/// </summary>
public class PageTransformTests
{
    /// <summary>
    /// Applies the transform the way CompositeTransform does, scale then rotate
    /// then translate.
    ///
    /// The test owns this rather than the production code, deliberately. It is
    /// a statement of what the markup will do with these four numbers, so if
    /// the numbers ever stop meaning that, these tests fail rather than quietly
    /// agreeing with a changed implementation.
    /// </summary>
    private static (double X, double Y) Forward(PageTransform t, double x, double y)
    {
        double sx = x * t.Scale;
        double sy = y * t.Scale;

        var (rx, ry) = t.Rotation switch
        {
            90 => (-sy, sx),
            180 => (-sx, -sy),
            270 => (sy, -sx),
            _ => (sx, sy),
        };

        return (rx + t.TranslateX, ry + t.TranslateY);
    }

    // A4-ish portrait in the app's 800-wide slot space.
    private const double W = 800;
    private const double H = 1035;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_content_lands_exactly_inside_its_card(int rotation)
    {
        // The whole point: whatever the rotation, the four corners of the page
        // map onto the four corners of the card, with nothing hanging outside
        // and no gap inside.
        var t = PageTransform.For(W, H, rotation, W);

        var corners = new[] { (0.0, 0.0), (W, 0.0), (0.0, H), (W, H) };

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var (x, y) in corners)
        {
            var p = Forward(t, x, y);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        Assert.Equal(0, minX, 6);
        Assert.Equal(0, minY, 6);
        Assert.Equal(t.CardWidth, maxX, 6);
        Assert.Equal(t.CardHeight, maxY, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void a_click_maps_back_to_the_point_it_was_aimed_at(int rotation)
    {
        // ToContent is the inverse of what the markup does. If it drifts, every
        // tool in the app aims at the wrong place while the view is turned, and
        // nothing else in the codebase would notice.
        var t = PageTransform.For(W, H, rotation, W);

        foreach (var (x, y) in new[] { (0.0, 0.0), (W, H), (17.5, 903.25), (W / 2, H / 2), (W, 0.0) })
        {
            var card = Forward(t, x, y);
            var (bx, by) = t.ToContent(card.X, card.Y);

            Assert.Equal(x, bx, 6);
            Assert.Equal(y, by, 6);
        }
    }

    [Fact]
    public void the_card_always_keeps_the_layout_width()
    {
        // Fit-width is a pure function of the one fixed layout width, so a
        // rotated page that came out wider than the layout would break it for
        // the whole document, not just for that page.
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        {
            Assert.Equal(W, PageTransform.For(W, H, rotation, W).CardWidth, 6);
        }
    }

    [Fact]
    public void a_quarter_turn_gives_the_card_the_reciprocal_shape()
    {
        // A tall page turned on its side has to become a short wide card, and
        // the stack's total height changes with it. 800x1035 upright becomes
        // 800x618 on its side.
        var upright = PageTransform.For(W, H, 0, W);
        var turned = PageTransform.For(W, H, 90, W);

        Assert.Equal(H, upright.CardHeight, 6);
        Assert.Equal(W * W / H, turned.CardHeight, 6);
        Assert.Equal(PageTransform.For(W, H, 270, W).CardHeight, turned.CardHeight, 6);
    }

    [Fact]
    public void half_a_turn_changes_nothing_but_the_direction()
    {
        // 180 is the one rotation that does not need scaling, and getting a
        // scale other than 1 here would shrink the page for no reason.
        var t = PageTransform.For(W, H, 180, W);

        Assert.Equal(1.0, t.Scale, 6);
        Assert.Equal(H, t.CardHeight, 6);
    }

    [Fact]
    public void not_rotating_is_exactly_what_the_app_did_before()
    {
        // The safety property for every document nobody rotates: identity
        // scale, no offset, card the same as content.
        var t = PageTransform.For(W, H, 0, W);

        Assert.Equal(1.0, t.Scale, 6);
        Assert.Equal(0, t.TranslateX, 6);
        Assert.Equal(0, t.TranslateY, 6);
        Assert.Equal(W, t.CardWidth, 6);
        Assert.Equal(H, t.CardHeight, 6);
    }

    [Fact]
    public void the_visible_region_of_the_card_maps_onto_the_page()
    {
        // Tiles are addressed in page coordinates but chosen from what is on
        // screen, which is card coordinates. Turned a quarter, the top strip of
        // the card is one SIDE of the page.
        var t = PageTransform.For(W, H, 90, W);

        // The top-left quarter of the card.
        var (left, top, right, bottom) = t.ContentBounds(0, 0, t.CardWidth / 2, t.CardHeight / 2);

        Assert.True(left >= -0.001 && right <= W + 0.001, $"outside the page horizontally: {left}..{right}");
        Assert.True(top >= -0.001 && bottom <= H + 0.001, $"outside the page vertically: {top}..{bottom}");
        Assert.True(right > left && bottom > top, "the region collapsed");

        // Turned clockwise, the card's left edge is the page's BOTTOM, so the
        // left half of the card must reach the bottom of the page.
        Assert.Equal(H, bottom, 6);
    }

    [Fact]
    public void the_whole_card_maps_to_the_whole_page()
    {
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        {
            var t = PageTransform.For(W, H, rotation, W);
            var (left, top, right, bottom) = t.ContentBounds(0, 0, t.CardWidth, t.CardHeight);

            Assert.Equal(0, left, 6);
            Assert.Equal(0, top, 6);
            Assert.Equal(W, right, 6);
            Assert.Equal(H, bottom, 6);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    [InlineData(-90, 270)]
    [InlineData(-360, 0)]
    [InlineData(37, 0)]
    public void rotations_come_back_as_quarter_turns(int given, int expected)
    {
        Assert.Equal(expected, PageTransform.Normalize(given));
    }

    [Fact]
    public void turning_the_same_way_four_times_returns_to_the_start()
    {
        int r = 0;
        for (int i = 0; i < 4; i++)
        {
            r = PageTransform.Normalize(r + 90);
        }

        Assert.Equal(0, r);

        int back = 0;
        for (int i = 0; i < 4; i++)
        {
            back = PageTransform.Normalize(back - 90);
        }

        Assert.Equal(0, back);
    }

    [Fact]
    public void a_page_scaled_down_to_fit_is_rendered_at_the_size_it_is_shown()
    {
        // Asking for the resolution the page's own width would need, while it
        // is being drawn at 0.77 of that, renders more pixels than the screen
        // can show. The budget takes a zoom, so the scale folds into it.
        var t = PageTransform.For(W, H, 90, W);

        Assert.Equal(2.0 * t.Scale, t.EffectiveZoom(2.0), 6);
        Assert.Equal(2.0, PageTransform.For(W, H, 0, W).EffectiveZoom(2.0), 6);
    }

    [Fact]
    public void a_degenerate_page_does_not_produce_a_degenerate_card()
    {
        // Page sizes come from the document and a broken one can report zero.
        // A zero-sized card would collapse the stack and take every page below
        // it with it.
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        {
            var t = PageTransform.For(0, 0, rotation, W);

            Assert.True(t.CardWidth > 0);
            Assert.True(t.CardHeight > 0);
            Assert.True(t.Scale > 0);
        }
    }

    // ---- ToCard: the direction needed to DRAW with the turn ----

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void to_card_is_what_the_markup_does(int rotation)
    {
        // The strongest form this can take. Forward above is the test's own
        // independent statement of what CompositeTransform will do with these
        // four numbers, written before ToCard existed. If the two agree, ToCard
        // draws a mark exactly where the markup draws the page under it.
        var t = PageTransform.For(W, H, rotation, W);

        foreach (var (x, y) in new[]
        {
            (0.0, 0.0), (W, 0.0), (0.0, H), (W, H),
            (W / 2, H / 2), (17.5, 903.25), (123.75, 41.5),
        })
        {
            var expected = Forward(t, x, y);
            var (cx, cy) = t.ToCard(x, y);

            Assert.Equal(expected.X, cx, 6);
            Assert.Equal(expected.Y, cy, 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void to_card_and_to_content_undo_each_other(int rotation)
    {
        // Both ways round. One direction being right is not the same as the two
        // being consistent, and hit testing uses one while drawing uses the
        // other: if they disagree, a shape is drawn where it cannot be clicked.
        var t = PageTransform.For(W, H, rotation, W);

        foreach (var (x, y) in new[] { (0.0, 0.0), (W, H), (17.5, 903.25), (W / 2, H / 2) })
        {
            var (cx, cy) = t.ToCard(x, y);
            var (bx, by) = t.ToContent(cx, cy);

            Assert.Equal(x, bx, 6);
            Assert.Equal(y, by, 6);
        }

        foreach (var (cx, cy) in new[] { (0.0, 0.0), (t.CardWidth, t.CardHeight), (31.25, 502.5) })
        {
            var (x, y) = t.ToContent(cx, cy);
            var (rx, ry) = t.ToCard(x, y);

            Assert.Equal(cx, rx, 6);
            Assert.Equal(cy, ry, 6);
        }
    }

    [Fact]
    public void at_no_rotation_to_card_changes_nothing_at_all()
    {
        // The safety property the whole correction rests on. An unrotated page
        // has Scale 1 and no translation, so routing a mark through ToCard must
        // be a byte-for-byte no-op. If this ever fails, every unrotated
        // document in existence has just moved.
        var t = PageTransform.For(W, H, 0, W);

        Assert.Equal(1.0, t.Scale, 6);
        Assert.Equal(0.0, t.TranslateX, 6);
        Assert.Equal(0.0, t.TranslateY, 6);

        foreach (var (x, y) in new[]
        {
            (0.0, 0.0), (W, H), (17.5, 903.25), (W / 2, H / 2), (-4.5, 1200.75),
        })
        {
            var (cx, cy) = t.ToCard(x, y);

            Assert.Equal(x, cx);
            Assert.Equal(y, cy);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]        // top-left stays top-left
    [InlineData(90, 1, 0)]       // a clockwise quarter turn takes it to top-right
    [InlineData(180, 1, 1)]      // half turn, to bottom-right
    [InlineData(270, 0, 1)]      // three quarters, to bottom-left
    public void the_pages_top_left_corner_lands_on_the_cards_expected_corner(
        int rotation, int cardRight, int cardBottom)
    {
        // Which corner goes where is the one thing a sign error cannot survive,
        // and it is also the difference between clockwise and anticlockwise.
        var t = PageTransform.For(W, H, rotation, W);

        var (cx, cy) = t.ToCard(0, 0);

        Assert.Equal(cardRight * t.CardWidth, cx, 6);
        Assert.Equal(cardBottom * t.CardHeight, cy, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_four_page_corners_land_on_the_four_card_corners(int rotation)
    {
        // A bijection, not just four points inside the card. Two corners
        // landing on the same one would still pass a bounds check.
        var t = PageTransform.For(W, H, rotation, W);

        var landed = new System.Collections.Generic.HashSet<(long, long)>();
        foreach (var (x, y) in new[] { (0.0, 0.0), (W, 0.0), (0.0, H), (W, H) })
        {
            var (cx, cy) = t.ToCard(x, y);

            Assert.True(Math.Abs(cx) < 1e-6 || Math.Abs(cx - t.CardWidth) < 1e-6,
                        $"x {cx} is not a card edge");
            Assert.True(Math.Abs(cy) < 1e-6 || Math.Abs(cy - t.CardHeight) < 1e-6,
                        $"y {cy} is not a card edge");

            landed.Add(((long)Math.Round(cx), (long)Math.Round(cy)));
        }

        Assert.Equal(4, landed.Count);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(180, 1.0)]
    public void an_upright_or_inverted_page_is_not_rescaled(int rotation, double expected)
    {
        // A half turn does not change which axis is across the card, so the
        // content still fits at 1:1 and a stroke keeps its weight.
        Assert.Equal(expected, PageTransform.For(W, H, rotation, W).Scale, 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void a_quarter_turn_rescales_by_the_aspect_ratio(int rotation)
    {
        // On its side, the page's HEIGHT is what has to be brought to the card's
        // width, so a portrait page is scaled DOWN. This is the factor a stroke
        // width has to be multiplied by as well, which the ink layer currently
        // does not do.
        var t = PageTransform.For(W, H, rotation, W);

        Assert.Equal(W / H, t.Scale, 6);
        Assert.True(t.Scale < 1, "a portrait page on its side should shrink");
    }
}
