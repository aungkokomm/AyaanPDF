using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Commit 4: the candidate renderer follows the page, the way the reference
/// renderer now does.
///
/// Commit 3 corrected the XAML ink overlay to turn its own points through the
/// page's PageTransform, and the user then verified that renderer by hand at 0,
/// 90, 180 and 270. Skia was left doing only the first half of the projection: a
/// documented divergence, harmless at 0 because the turn is the identity there,
/// and wrong everywhere else. This closes it.
///
/// Two halves to the proof, because they fail differently.
///
/// The ARITHMETIC is pinned against the formula written out longhand here,
/// independently of the shared method, so agreement means something. That
/// formula is the one ShapeRotationProjectionTests describes and the one the
/// verified overlay performs.
///
/// The PIXELS are then checked, because a painter can be handed a correct
/// projection and still not use it. Every rotated case also asserts that the
/// place the mark WOULD have landed without the turn is empty, which is what
/// makes these tests able to fail.
/// </summary>
public class SkiaRotationParityTests
{
    private const double Scale = 800;            // OverlayScale, the content-box width
    private const double ContentH = 1000;        // a taller-than-wide page, so a turn is visible
    private const double StrokeWidthNorm = 0.004;

    private static PageTransform View(int rotation) =>
        PageTransform.For(Scale, ContentH, rotation, Scale);

    /// <summary>
    /// The corrected overlay's projection, written out rather than called, so
    /// that asserting the shared method equals it is a real comparison and not a
    /// tautology. Scale, then turn, then drop to the page's top, Y only.
    /// </summary>
    private static (double X, double Y) Longhand(
        (double X, double Y) n, int rotation, double pageTop)
    {
        double s = rotation is 90 or 270 ? Scale / ContentH : 1.0;
        double x = n.X * Scale;
        double y = n.Y * Scale;

        var (cx, cy) = rotation switch
        {
            90 => (s * (ContentH - y), s * x),
            180 => (s * (Scale - x), s * (ContentH - y)),
            270 => (s * y, s * (Scale - x)),
            _ => (s * x, s * y),
        };

        return (cx, cy + pageTop);
    }

    // ---------------- the shared rule ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_shared_projection_is_the_arithmetic_the_verified_overlay_performs(int rotation)
    {
        var view = View(rotation);

        foreach (double pageTop in new[] { 0.0, 1234.5 })
        {
            foreach (var n in new[] { (0.0, 0.0), (0.25, 0.5), (1.0, 1.25), (0.6, 0.1) })
            {
                Assert.Equal(
                    Longhand(n, rotation, pageTop),
                    OverlayProjection.ToSlot(n, Scale, pageTop, view));
            }
        }
    }

    [Theory]
    [InlineData(0, 200.0, 1634.5)]
    [InlineData(90, 480.0, 1394.5)]
    [InlineData(180, 600.0, 1834.5)]
    [InlineData(270, 320.0, 1714.5)]
    public void each_rotation_lands_a_point_somewhere_of_its_own(
        int rotation, double x, double y)
    {
        // Four literal answers, so a sign error or a transposed pair is a
        // changed number rather than a rearranged formula that still "looks
        // right". 90 and 270 differ, which is the whole direction question:
        // a renderer that turned the wrong way would swap exactly these two.
        Assert.Equal((x, y), OverlayProjection.ToSlot((0.25, 0.5), Scale, 1234.5, View(rotation)));
    }

    [Fact]
    public void at_zero_degrees_the_whole_rule_and_the_half_rule_agree_exactly()
    {
        // The safety property for every existing unrotated document: routing
        // marks through the turn must not move one by a fraction of a pixel.
        // Exact equality, no tolerance.
        foreach (var n in new[] { (0.0, 0.0), (0.25, 0.5), (1.0, 1.25), (0.6, 0.1) })
        {
            Assert.Equal(
                OverlayProjection.ToSlot(n, Scale, 1234.5),
                OverlayProjection.ToSlot(n, Scale, 1234.5, View(0)));
        }

        Assert.Equal(
            OverlayProjection.ToSlotThickness(StrokeWidthNorm, Scale),
            OverlayProjection.ToSlotThickness(StrokeWidthNorm, Scale, View(0)));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_page_top_is_added_after_the_turn_and_only_to_y(int rotation)
    {
        // Folding the stack offset in before the turn would rotate the whole
        // document's height into the page, which on page 40 is a mark tens of
        // thousands of DIPs from where it belongs. Adding it to X as well would
        // shear the column sideways one page at a time.
        var view = View(rotation);
        const double pageTop = 4321.0;

        var atTop = OverlayProjection.ToSlot((0.3, 0.4), Scale, 0, view);
        var lower = OverlayProjection.ToSlot((0.3, 0.4), Scale, pageTop, view);

        Assert.Equal(atTop.X, lower.X);
        Assert.Equal(atTop.Y + pageTop, lower.Y);
    }

    [Theory]
    [InlineData(0, 3.2)]
    [InlineData(180, 3.2)]
    [InlineData(90, 2.56)]
    [InlineData(270, 2.56)]
    public void a_stroke_takes_the_turns_scale_as_well(int rotation, double expected)
    {
        // A length has no coordinates to carry the scale in, so it has to be
        // multiplied separately. 0.004 * 800 is 3.2; a page on its side is
        // scaled by 800/1000 to fit the card, giving 2.56.
        Assert.Equal(expected, OverlayProjection.ToSlotThickness(StrokeWidthNorm, Scale, View(rotation)), 6);
    }

    // ---------------- the pixels ----------------

    private const double NormLeft = 0.1, NormTop = 0.1, NormRight = 0.4, NormBottom = 0.3;

    private static ShapeRenderItem Rectangle() =>
        ShapeRenderList.From([], [
            new ShapeAnnotation(
                0, new ShapeDraft(ShapeKind.Rectangle, NormLeft, NormTop, NormRight, NormBottom),
                "#FF000000", StrokeWidthNorm),
        ])[0];

    /// <summary>
    /// Big enough for every rotation of the fixture to land inside it: 180 puts
    /// the rectangle down at y 760..920.
    /// </summary>
    private const int Surface = 1000;

    private static SKBitmap Render(IReadOnlyList<ShapeRenderItem> items, int rotation)
    {
        var bitmap = new SKBitmap(Surface, Surface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        ShapeSkiaPainter.Paint(canvas, items, Scale, _ => 0, _ => View(rotation));
        return bitmap;
    }

    private static bool IsWhite(SKColor c) => c.Red == 255 && c.Green == 255 && c.Blue == 255;

    /// <summary>
    /// How far one pixel is from white, as coverage, measured on its DARKEST
    /// channel.
    ///
    /// Not the red channel. The freehand guide is pure red, so a fully covered
    /// pixel is (255, 0, 0) and reading red alone scores it as blank: the first
    /// version of the guide test measured 0 ink on a stroke that was plainly
    /// being painted. The darkest channel is 0 for both black and red and 255
    /// for white, so it measures every fixture in this file the same way.
    /// </summary>
    private static double Coverage(SKColor c) =>
        (255 - Math.Min(c.Red, Math.Min(c.Green, c.Blue))) / 255.0;

    private static double InkDownColumn(SKBitmap bitmap, int x, int fromY, int toY)
    {
        double ink = 0;
        for (int y = fromY; y <= toY; y++)
        {
            ink += Coverage(bitmap.GetPixel(x, y));
        }

        return ink;
    }

    private static double InkAcrossRow(SKBitmap bitmap, int y, int fromX, int toX)
    {
        double ink = 0;
        for (int x = fromX; x <= toX; x++)
        {
            ink += Coverage(bitmap.GetPixel(x, y));
        }

        return ink;
    }

    /// <summary>
    /// The midpoint of each of the four edges, in normalized units.
    ///
    /// Midpoints, NOT corners, and the difference is not cosmetic. The outline
    /// is an open polyline whose last point repeats its first, so that one
    /// vertex is two butt caps meeting rather than a mitred join, and it leaves
    /// a half-stroke notch on the OUTSIDE of the corner. Which side of the
    /// corner's own pixel that notch falls on depends on the rotation: at 0 the
    /// start vertex is the top-left and the notch is up and to the left of it;
    /// at 180 the same vertex is the bottom-right and the notch covers the
    /// pixel itself. Probing corners therefore measures the join, not the
    /// position. An edge midpoint has no such ambiguity.
    /// </summary>
    private static readonly (double X, double Y)[] EdgeMidpoints =
    [
        ((NormLeft + NormRight) / 2, NormTop),
        ((NormLeft + NormRight) / 2, NormBottom),
        (NormLeft, (NormTop + NormBottom) / 2),
        (NormRight, (NormTop + NormBottom) / 2),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void skia_paints_the_rectangle_where_the_shared_projection_puts_it(int rotation)
    {
        var view = View(rotation);
        using var bitmap = Render([Rectangle()], rotation);

        foreach (var edge in EdgeMidpoints)
        {
            var (x, y) = OverlayProjection.ToSlot(edge, Scale, 0, view);

            Assert.False(IsWhite(bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y))),
                         $"at {rotation} degrees nothing was painted at ({x}, {y})");
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void skia_no_longer_paints_where_an_unturned_page_would(int rotation)
    {
        // The failing half. Before this commit the painter stopped at the first
        // half of the projection, so the mark stayed here at every rotation
        // while the page moved out from under it.
        using var bitmap = Render([Rectangle()], rotation);

        foreach (var edge in EdgeMidpoints)
        {
            var (x, y) = OverlayProjection.ToSlot(edge, Scale, 0);

            Assert.True(IsWhite(bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y))),
                        $"at {rotation} degrees the mark was still painted at its unturned ({x}, {y})");
        }
    }

    [Fact]
    public void a_quarter_turn_paints_a_wide_rectangle_as_a_tall_one()
    {
        // Orientation, not just position. A mark that moved but kept its
        // proportions would pass a corner check on a square fixture and still
        // be drawn upright on a sideways page. The fixture is 240 by 160.
        using var flat = Render([Rectangle()], 0);
        using var side = Render([Rectangle()], 90);

        var (l0, t0, r0, b0) = InkedBounds(flat);
        var (l9, t9, r9, b9) = InkedBounds(side);

        Assert.True(r0 - l0 > b0 - t0, "fixture is not wider than it is tall");
        Assert.True(b9 - t9 > r9 - l9, "a quarter turn did not stand the rectangle up");
    }

    [Fact]
    public void a_page_on_its_side_is_stroked_more_lightly()
    {
        // 3.2 DIPs upright against 2.56 turned. Skia snaps a stroke to the
        // nearest half device pixel, so what lands is 3.0 against 2.5; the
        // assertion is the GAP, which exists only if the turn's scale reached
        // the stroke width. Dropping it leaves both at 3.0.
        //
        // Scanned ACROSS each edge, which is a different direction for each:
        // the normalized top edge is horizontal upright and vertical after a
        // quarter turn, so scanning a column both times runs the second scan
        // ALONG the stroke and measures its length.
        using var upright = Render([Rectangle()], 0);
        using var turned = Render([Rectangle()], 90);

        var top = ((NormLeft + NormRight) / 2, NormTop);
        var (ux, uy) = OverlayProjection.ToSlot(top, Scale, 0, View(0));
        var (tx, ty) = OverlayProjection.ToSlot(top, Scale, 0, View(90));

        double inkUpright = InkDownColumn(upright, (int)Math.Round(ux), (int)Math.Round(uy) - 12, (int)Math.Round(uy) + 12);
        double inkTurned = InkAcrossRow(turned, (int)Math.Round(ty), (int)Math.Round(tx) - 12, (int)Math.Round(tx) + 12);

        Assert.True(inkTurned < inkUpright - 0.25,
                    $"turned stroke {inkTurned:F2} is not lighter than upright {inkUpright:F2}");
    }

    private static (int L, int T, int R, int B) InkedBounds(SKBitmap bitmap)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (IsWhite(bitmap.GetPixel(x, y)))
                {
                    continue;
                }

                l = Math.Min(l, x);
                t = Math.Min(t, y);
                r = Math.Max(r, x);
                b = Math.Max(b, y);
            }
        }

        Assert.True(l <= r, "nothing was painted at all");
        return (l, t, r, b);
    }

    // ---------------- the freehand guide's fixed weight ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void skia_paints_the_guide_at_two_dips_on_a_turned_page_as_well(int rotation)
    {
        // A normalized width would thin to 1.6 at 90 degrees, because the page
        // is scaled by 800/1000 to fit the card. The guide does not thin: the
        // overlay draws a literal 2 and this has to match it.
        //
        // Measured across a long horizontal run so the scan crosses the stroke
        // once, and Skia's half-device-pixel snap leaves 2.0 exactly alone.
        //
        // The scan direction follows the mark: a horizontal segment stays
        // horizontal at 0 and 180 and stands up at 90 and 270, and scanning the
        // wrong way runs ALONG the stroke and measures its length instead.
        var guide = ShapeRenderList.InkGuide(0, [(0.1, 0.25), (0.6, 0.25)]);

        using var bitmap = Render([guide], rotation);

        var mid = OverlayProjection.ToSlot((0.35, 0.25), Scale, 0, View(rotation));
        int x = (int)Math.Round(mid.X), y = (int)Math.Round(mid.Y);

        double ink = rotation is 0 or 180
            ? InkDownColumn(bitmap, x, y - 8, y + 8)
            : InkAcrossRow(bitmap, y, x - 8, x + 8);

        Assert.Equal(2.0, ink, precision: 1);
    }

    // ---------------- tier 1: every subject, every rotation ----------------
    //
    // Tier 1 measures Skia's pixels against an expectation computed here, from
    // the shared projection, touching neither renderer. It is the gate that can
    // fail a build. Tier 2, the in-app capture, is the only thing that can
    // produce the REFERENCE renderer's pixels, and it produces a report rather
    // than a verdict. The two are deliberately not mixed.
    //
    // The fixture matches tier 2's, so a cell here and a row of
    // stage4-parity.csv are talking about the same shape.

    private const double MatrixStroke = 0.01;

    private static readonly ShapeDraft MatrixDraft = new(ShapeKind.Rectangle, 0.12, 0.12, 0.72, 0.68);

    private static readonly (double X, double Y)[] MatrixInk =
    [
        (0.15, 0.20), (0.28, 0.34), (0.41, 0.28), (0.55, 0.47),
        (0.62, 0.71), (0.74, 0.66), (0.80, 0.85),
    ];

    public static TheoryData<string, int> Cells()
    {
        var cells = new TheoryData<string, int>();
        foreach (string subject in new[] { "rect", "ellipse", "line", "arrow", "ink" })
        {
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                cells.Add(subject, rotation);
            }
        }

        return cells;
    }

    private static ShapeKind? KindOf(string subject) => subject switch
    {
        "rect" => ShapeKind.Rectangle,
        "ellipse" => ShapeKind.Ellipse,
        "line" => ShapeKind.Line,
        "arrow" => ShapeKind.Arrow,
        _ => null,
    };

    private static IReadOnlyList<ShapeRenderItem> ItemsFor(string subject) =>
        KindOf(subject) is { } kind
            ? ShapeRenderList.From([], [
                new ShapeAnnotation(0, MatrixDraft with { Kind = kind }, "#FF000000", MatrixStroke)])
            : ShapeRenderList.From([], [], inkPreview: ShapeRenderList.InkGuide(0, MatrixInk));

    /// <summary>
    /// Where the whole subject should land, from the shared projection alone.
    ///
    /// Each item is grown by HALF ITS OWN width, because a path is stroked
    /// about its centreline and a filled head carries only the hairline. Using
    /// one width for both would make the arrow's expectation wrong by the
    /// difference between them.
    /// </summary>
    private static (double L, double T, double R, double B) ExpectedBounds(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

        foreach (var item in items)
        {
            double reach = (item.Style == RenderStyle.Filled
                ? OverlayProjection.HeadHairlineDips
                : OverlayProjection.WidthOf(item, Scale, view)) / 2;

            foreach (var p in item.Points)
            {
                var (x, y) = OverlayProjection.ToSlot(p, Scale, 0, view);
                l = Math.Min(l, x - reach); t = Math.Min(t, y - reach);
                r = Math.Max(r, x + reach); b = Math.Max(b, y + reach);
            }
        }

        return (l, t, r, b);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void every_subject_lands_where_the_shared_projection_says(string subject, int rotation)
    {
        // The backbone. Skia's actual ink, against arithmetic that has not been
        // near a renderer, for all five subjects at all four rotations.
        //
        // Two pixels of slack, and no more: antialiasing spreads an edge about
        // a pixel either way and Skia's stroker snaps a width to the nearest
        // half device pixel, which moves both edges by up to a quarter each.
        var view = View(rotation);
        var items = ItemsFor(subject);

        using var bitmap = Render(items, rotation);
        var (l, t, r, b) = InkedBounds(bitmap);
        var want = ExpectedBounds(items, view);

        Assert.InRange(l, want.L - 2, want.L + 2);
        Assert.InRange(t, want.T - 2, want.T + 2);
        Assert.InRange(r, want.R - 2, want.R + 2);
        Assert.InRange(b, want.B - 2, want.B + 2);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void every_subject_is_painted_along_its_own_path(string subject, int rotation)
    {
        // Bounds alone cannot tell an ellipse from the rectangle that contains
        // it, or a line from its own bounding box. This samples a point that is
        // ON the shape and strictly off its box corners, so a subject drawn as
        // the wrong primitive fails even though its extent is right.
        var view = View(rotation);
        using var bitmap = Render(ItemsFor(subject), rotation);

        var (x, y) = OverlayProjection.ToSlot(OnPath(subject), Scale, 0, view);

        Assert.False(IsWhite(bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y))),
                     $"{subject} at {rotation} has no ink at its own ({x:F1}, {y:F1})");
    }

    /// <summary>
    /// A point genuinely on the drawn path, taken from the geometry rather than
    /// written down, so it cannot drift from what the shape actually is.
    /// </summary>
    private static (double X, double Y) OnPath(string subject)
    {
        if (KindOf(subject) is not { } kind)
        {
            return MatrixInk[3];            // a vertex of the guide
        }

        var draft = MatrixDraft with { Kind = kind };

        if (kind == ShapeKind.Ellipse)
        {
            // 45 degrees round the ellipse: inside the box's corner, outside
            // its edges, so neither a rectangle nor a diagonal passes here.
            double cx = (draft.Left + draft.Right) / 2, cy = (draft.Top + draft.Bottom) / 2;
            const double Diagonal = 0.70710678118654752;

            return (cx + (draft.Width / 2 * Diagonal), cy + (draft.Height / 2 * Diagonal));
        }

        if (kind == ShapeKind.Rectangle)
        {
            return ((draft.Left + draft.Right) / 2, draft.Top);
        }

        // Line and arrow: the middle of the shaft the geometry reports, which
        // for an arrow is shorter than the drag because the head takes the end.
        var outline = new ShapeAnnotation(0, draft, "#FF000000", MatrixStroke).Outline;

        return ((outline[0].X + outline[^1].X) / 2, (outline[0].Y + outline[^1].Y) / 2);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void turning_the_page_moves_every_subject(string subject, int rotation)
    {
        // At 0 this asserts the mark does NOT move, which is the property every
        // existing unrotated document depends on. At the other three it has to.
        using var flat = Render(ItemsFor(subject), 0);
        using var turned = Render(ItemsFor(subject), rotation);

        if (rotation == 0)
        {
            Assert.Equal(InkedBounds(flat), InkedBounds(turned));
            return;
        }

        Assert.NotEqual(InkedBounds(flat), InkedBounds(turned));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void an_arrows_head_is_solid_at_every_rotation(int rotation)
    {
        // The head is the only filled item the app produces, and it is painted
        // by its own branch. A point strictly inside the triangle is white for
        // an outline and coloured for a fill.
        var view = View(rotation);
        var items = ItemsFor("arrow");

        var head = Assert.Single(items, i => i.Style == RenderStyle.Filled);
        using var bitmap = Render(items, rotation);

        double cx = 0, cy = 0;
        foreach (var p in head.Points)
        {
            var (x, y) = OverlayProjection.ToSlot(p, Scale, 0, view);
            cx += x; cy += y;
        }

        int px = (int)Math.Round(cx / head.Points.Count);
        int py = (int)Math.Round(cy / head.Points.Count);

        Assert.False(IsWhite(bitmap.GetPixel(px, py)),
                     $"the head's interior at {rotation} degrees was not filled");
    }

    // ---------------- culling has to agree with painting ----------------

    [Fact]
    public void culling_turns_the_page_the_same_way_the_painter_does()
    {
        // If culling decides from the unturned position it drops marks that are
        // on screen and keeps ones that are not, and only ever while rotated.
        //
        // This mark sits low on a tall page, so unturned it is at slot y 880,
        // far below a small viewport. A quarter turn brings it to (96, 32),
        // which is on screen.
        var item = new ShapeRenderItem(
            0, [(0.05, 1.1), (0.06, 1.1)], new RenderColor(255, 0, 0, 0),
            StrokeWidthNorm, RenderStyle.Stroked);

        var window = (0.0, 0.0, 200.0, 200.0);

        Assert.Empty(ShapeCulling.Visible([item], window, Scale, _ => 0, _ => View(0)));
        Assert.Single(ShapeCulling.Visible([item], window, Scale, _ => 0, _ => View(90)));
    }

    // ---------------- the layer is still off, and still isolated ----------------

    [Fact]
    public void the_skia_layer_is_still_not_on_by_default()
    {
        // Commit 4 makes Skia correct under rotation. It does not make it the
        // renderer. Default-off is the rollback the whole migration rests on.
        Assert.False(new AppSettings().UseSkiaShapeLayer);
    }

    [Fact]
    public void the_turn_is_data_the_painter_is_handed_not_a_matrix_it_owns()
    {
        // The architectural rule for this commit: rotation stays described in
        // the app's coordinate system and reaches Skia through the shared
        // projection. Composing it into an SKMatrix here would quietly make the
        // renderer the authority on where a mark is, which is the one thing the
        // migration is not allowed to do.
        string painter = ReadSource("PdfEditorApp.Rendering.Skia", "ShapeSkiaPainter.cs");

        Assert.Contains("OverlayProjection.ToSlot(item.Points[at], scale, pageTop, view)",
                        painter, StringComparison.Ordinal);

        // WidthOf, which resolves the fixed-versus-normalized width question in
        // the shared library. The painter must not decide that itself.
        Assert.Contains("OverlayProjection.WidthOf(item, scale, view)",
                        painter, StringComparison.Ordinal);

        // MatrixFor is the ONE matrix, and it is the viewport step: zoom,
        // origin, DPI. No rotation may join it.
        Assert.DoesNotContain("SKMatrix.CreateRotation", painter, StringComparison.Ordinal);
        Assert.DoesNotContain("RotationDegrees", painter, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }
}
