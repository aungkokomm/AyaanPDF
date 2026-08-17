using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Stage 4, tier 1: page boundaries, page routing, and the difference between
/// a mark that was culled and one that was merely clipped.
///
/// Four outcomes look identical from a distance and have to be told apart,
/// because three of them are correct and one is a bug:
///
///   CULLED       the culler dropped it, and it was genuinely off screen
///   CLIPPED      it was drawn, and the surface only holds part of it
///   MISPROJECTED it was drawn somewhere it does not belong
///   DISAGREEMENT both renderers drew it, in different places
///
/// The first three are answerable here, against arithmetic, without either
/// renderer being treated as an oracle. The fourth needs the reference
/// renderer's real pixels and belongs to tier 2.
///
/// Everything reuses ShapeCulling, ViewportProjection, PageTransform and
/// OverlayProjection. Nothing here reimplements a projection.
/// </summary>
public class PagePlacementParityTests
{
    private const double Scale = 800;
    private const double ContentH = 1000;
    private const double Stroke = 0.006;
    private const int Surface = 600;

    /// <summary>Pages stack a card apart. A card is as tall as the turn makes it.</summary>
    private static double PageTop(int page, PageTransform view) => page * view.CardHeight;

    private static PageTransform View(int rotation) =>
        PageTransform.For(Scale, ContentH, rotation, Scale);

    private static IReadOnlyList<ShapeRenderItem> Mark(
        int page, double l, double t, double r, double b) =>
        ShapeRenderList.From([], [
            new ShapeAnnotation(page, new ShapeDraft(ShapeKind.Rectangle, l, t, r, b),
                                "#FF000000", Stroke)]);

    /// <summary>
    /// The mark's slot-space box, from the shared projection, with no renderer
    /// involved. Every expectation below is built from this.
    /// </summary>
    private static (double L, double T, double R, double B) SlotBox(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view, Func<int, double> pageTop)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

        foreach (var item in items)
        {
            double reach = OverlayProjection.WidthOf(item, Scale, view) / 2;
            foreach (var p in item.Points)
            {
                var (x, y) = OverlayProjection.ToSlot(p, Scale, pageTop(item.PageIndex), view);
                l = Math.Min(l, x - reach); t = Math.Min(t, y - reach);
                r = Math.Max(r, x + reach); b = Math.Max(b, y + reach);
            }
        }

        return (l, t, r, b);
    }

    private static SKBitmap Paint(
        IReadOnlyList<ShapeRenderItem> items, PageTransform view, Func<int, double> pageTop,
        ViewportProjection projection, int side = Surface)
    {
        var bitmap = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        // Through the FULL path, culler included, because "was it culled or was
        // it misprojected" is a question about the two of them together.
        var visible = ShapeCulling.Visible(
            items, projection.VisibleSlotBounds(side, side), Scale, pageTop, _ => view);

        ShapeSkiaPainter.PaintViewport(canvas, visible, Scale, pageTop, _ => view, projection);
        return bitmap;
    }

    private static (int L, int T, int R, int B)? InkedBounds(SKBitmap bitmap)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red == 255 && c.Green == 255 && c.Blue == 255)
                {
                    continue;
                }

                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        return l <= r ? (l, t, r, b) : null;
    }

    // ---------------- the five placements, culled versus clipped ----------------

    public static TheoryData<string, int> Placements()
    {
        var cells = new TheoryData<string, int>();
        foreach (string where in new[] { "inside", "touching", "half", "corner", "outside" })
        {
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                cells.Add(where, rotation);
            }
        }

        return cells;
    }

    /// <summary>
    /// Placements in slot DIPs relative to a 600-point surface, converted to
    /// normalized page-local so they travel through the real projection.
    ///
    /// Given in CARD space and mapped back through ToContent, so "touching the
    /// left edge of the surface" means that at every rotation instead of only
    /// at zero, where card space and page space happen to coincide.
    /// </summary>
    private static (double L, double T, double R, double B) CardBox(string where) => where switch
    {
        "inside" => (150, 150, 350, 300),
        "touching" => (0, 150, 200, 300),
        "half" => (-100, 150, 100, 300),
        "corner" => (-80, -60, 40, 30),
        _ => (-400, -400, -260, -300),
    };

    private static IReadOnlyList<ShapeRenderItem> MarkInCard(string where, PageTransform view)
    {
        var (l, t, r, b) = CardBox(where);
        var a = view.ToContent(l, t);
        var c = view.ToContent(r, b);

        return Mark(0,
            Math.Min(a.X, c.X) / Scale, Math.Min(a.Y, c.Y) / Scale,
            Math.Max(a.X, c.X) / Scale, Math.Max(a.Y, c.Y) / Scale);
    }

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);

    [Theory]
    [MemberData(nameof(Placements))]
    public void a_placement_is_culled_only_when_it_is_genuinely_off_the_surface(
        string where, int rotation)
    {
        // The culler's decision, checked against the arithmetic rather than
        // against itself: a box that meets the surface must be kept, and one
        // that does not must be dropped. This is what separates a correctly
        // culled mark from a wrongly culled one.
        var view = View(rotation);
        var items = MarkInCard(where, view);
        var box = SlotBox(items, view, _ => 0);

        var window = Plain.VisibleSlotBounds(Surface, Surface);
        bool meets = box.L <= window.Right && box.R >= window.Left
                  && box.T <= window.Bottom && box.B >= window.Top;

        var kept = ShapeCulling.Visible(items, window, Scale, _ => 0, _ => view);

        Assert.Equal(meets, kept.Count > 0);
        Assert.Equal(where != "outside", meets);
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void what_survives_culling_is_painted_and_what_does_not_is_absent(
        string where, int rotation)
    {
        // Culling and painting have to agree, or a mark disappears at the edge
        // of the window while the arithmetic says it is there. Note this is a
        // property of the PAIR: either one alone can be self-consistently
        // wrong.
        var view = View(rotation);
        using var bitmap = Paint(MarkInCard(where, view), view, _ => 0, Plain);

        if (where == "outside")
        {
            Assert.Null(InkedBounds(bitmap));
            return;
        }

        Assert.NotNull(InkedBounds(bitmap));
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void a_clipped_mark_is_cut_by_the_surface_and_not_moved_by_it(
        string where, int rotation)
    {
        // CLIPPED, not misprojected. A mark half off the edge must paint the
        // part that is on the surface exactly where it would have been, with
        // the rest simply absent. The failure this excludes is a renderer that
        // "helpfully" brings a clipped mark back into view, which would look
        // fine in isolation and put the mark on the wrong part of the page.
        if (where == "outside")
        {
            return;
        }

        var view = View(rotation);
        var items = MarkInCard(where, view);
        var box = SlotBox(items, view, _ => 0);

        using var bitmap = Paint(items, view, _ => 0, Plain);
        var ink = InkedBounds(bitmap);

        Assert.NotNull(ink);
        var got = ink!.Value;

        // The visible part is the projected box intersected with the surface.
        double wantL = Math.Max(box.L, 0), wantT = Math.Max(box.T, 0);
        double wantR = Math.Min(box.R, Surface - 1), wantB = Math.Min(box.B, Surface - 1);

        Assert.InRange(got.L, wantL - 2, wantL + 2);
        Assert.InRange(got.T, wantT - 2, wantT + 2);
        Assert.InRange(got.R, wantR - 2, wantR + 2);
        Assert.InRange(got.B, wantB - 2, wantB + 2);
    }

    // ---------------- multi-page ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void a_mark_stays_inside_its_own_pages_band(int rotation)
    {
        // Page N's marks belong between page N's top and page N+1's. A mark
        // that leaks into a neighbour draws over another page's content, and
        // in a continuous view both pages are on screen at once so it is
        // visible immediately and inexplicable.
        var view = View(rotation);

        for (int page = 0; page < 4; page++)
        {
            var items = Mark(page, 0.02, 0.02, 0.98, 0.98 * (ContentH / Scale));
            var box = SlotBox(items, view, p => PageTop(p, view));

            double top = PageTop(page, view);

            Assert.InRange(box.T, top - 1, top + view.CardHeight);
            Assert.InRange(box.B, top, top + view.CardHeight + 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void neighbouring_pages_marks_never_overlap(int rotation)
    {
        // The same assertion from the other side, and the one that actually
        // catches a page-index that is ignored: two identical marks on
        // consecutive pages must occupy disjoint bands.
        var view = View(rotation);
        Func<int, double> tops = p => PageTop(p, view);

        var first = SlotBox(Mark(2, 0.1, 0.1, 0.9, 0.9), view, tops);
        var second = SlotBox(Mark(3, 0.1, 0.1, 0.9, 0.9), view, tops);

        Assert.True(first.B < second.T,
                    $"page 2 ends at {first.B:F1} and page 3 starts at {second.T:F1}");
    }

    [Fact]
    public void a_pages_own_turn_does_not_leak_into_its_neighbour()
    {
        // Each page is asked for ITS transform. A stack where one page is
        // turned and the next is not must produce two differently shaped marks;
        // a renderer that resolved the transform once would produce two of the
        // same shape.
        var flat = View(0);
        var turned = View(90);

        Func<int, PageTransform> perPage = p => p == 0 ? flat : turned;
        Func<int, double> tops = p => p == 0 ? 0 : flat.CardHeight;

        var wide = new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.9, 0.3);

        var onFlat = ShapeRenderList.From([], [new ShapeAnnotation(0, wide, "#FF000000", Stroke)]);
        var onTurned = ShapeRenderList.From([], [new ShapeAnnotation(1, wide, "#FF000000", Stroke)]);

        var a = SlotBox(onFlat, perPage(0), tops);
        var b = SlotBox(onTurned, perPage(1), tops);

        Assert.True(a.R - a.L > a.B - a.T, "the mark on the flat page is not wide");
        Assert.True(b.B - b.T > b.R - b.L, "the mark on the turned page did not stand up");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void a_window_over_one_page_paints_that_page_only(int rotation)
    {
        // End to end, through the culler and the painter: three pages carrying
        // the same mark, a viewport scrolled onto the middle one, and only the
        // middle one's ink on the surface. Wrong page routing puts a
        // neighbour's mark here, or leaves this one blank.
        var view = View(rotation);
        Func<int, double> tops = p => PageTop(p, view);

        var items = new List<ShapeRenderItem>();
        for (int page = 0; page < 3; page++)
        {
            items.AddRange(Mark(page, 0.2, 0.2, 0.6, 0.5));
        }

        // Scrolled so page 1's top sits at the surface's top.
        var scrolled = new ViewportProjection(1, 1, 0, -PageTop(1, view));

        using var bitmap = Paint(items, view, tops, scrolled);
        var ink = InkedBounds(bitmap);
        Assert.NotNull(ink);

        var want = SlotBox(Mark(1, 0.2, 0.2, 0.6, 0.5), view, tops);
        var (wantL, wantT) = scrolled.SlotToDevice(want.L, want.T);
        var (wantR, wantB) = scrolled.SlotToDevice(want.R, want.B);

        // Clamped to the surface, because a turned page's card is 800 wide
        // against a 600 surface and the mark really does run off the right
        // edge at 90 degrees. That is CLIPPING, which is correct, and the
        // unclamped version of this assertion failed on it: the renderer was
        // right and the expectation had forgotten the surface exists.
        wantL = Math.Max(wantL, 0); wantT = Math.Max(wantT, 0);
        wantR = Math.Min(wantR, Surface - 1); wantB = Math.Min(wantB, Surface - 1);

        var got = ink!.Value;
        Assert.InRange(got.L, wantL - 2, wantL + 2);
        Assert.InRange(got.T, wantT - 2, wantT + 2);
        Assert.InRange(got.R, wantR - 2, wantR + 2);
        Assert.InRange(got.B, wantB - 2, wantB + 2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void scrolling_to_a_later_page_moves_the_mark_by_the_pages_own_height(double zoom)
    {
        // Zoom and page offset combined, which is where a stack drifts: the
        // offset is in slot DIPs and the zoom multiplies it, so applying them
        // in the wrong order puts page 40 in the wrong place by a factor.
        var view = View(0);
        Func<int, double> tops = p => PageTop(p, view);

        var p0 = new ViewportProjection(zoom, 1, 0, 0);
        var p1 = new ViewportProjection(zoom, 1, 0, -PageTop(1, view) * zoom);

        using var first = Paint(Mark(0, 0.2, 0.2, 0.5, 0.4), view, tops, p0);
        using var later = Paint(Mark(1, 0.2, 0.2, 0.5, 0.4), view, tops, p1);

        Assert.Equal(InkedBounds(first), InkedBounds(later));
    }
}
