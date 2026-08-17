using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What a viewport-sized surface bothers to draw.
///
/// The surface covers the window, not the document, so a three-hundred page
/// file's marks are almost all off it. Culling is what makes that affordable,
/// and getting it wrong is invisible in the common case and infuriating in the
/// rare one: a shape that vanishes as it approaches the edge.
/// </summary>
public class ShapeCullingTests
{
    private const double Scale = 800;

    private static ShapeRenderItem Item(int page, params (double X, double Y)[] points) =>
        new(page, points, new RenderColor(255, 0, 0, 0), 0.004, RenderStyle.Stroked);

    /// <summary>Every page 1000 slot DIPs tall, so page N starts at N * 1000.</summary>
    private static double PageTop(int page) => page * 1000.0;

    /// <summary>
    /// An unturned view, so these keep testing culling and not rotation. The
    /// turned cases live in SkiaRotationParityTests, next to the painter they
    /// have to agree with.
    /// </summary>
    private static PageTransform Flat(int page) => PageTransform.For(1, 1, 0, 1);

    private static IReadOnlyList<ShapeRenderItem> Cull(
        IReadOnlyList<ShapeRenderItem> items,
        (double, double, double, double) bounds) =>
        ShapeCulling.Visible(items, bounds, Scale, PageTop, Flat);

    [Fact]
    public void a_shape_inside_the_region_is_kept()
    {
        var items = new[] { Item(0, (0.1, 0.1), (0.4, 0.3)) };   // slot (80,80)-(320,240)

        Assert.Single(Cull(items, (0, 0, 1000, 1000)));
    }

    [Fact]
    public void a_shape_on_another_page_far_below_is_dropped()
    {
        // The case culling exists for. Page 40 starts 40,000 slot DIPs down.
        var items = new[] { Item(40, (0.1, 0.1), (0.4, 0.3)) };

        Assert.Empty(Cull(items, (0, 0, 1000, 1000)));
    }

    [Fact]
    public void a_shape_overlapping_the_edge_is_kept()
    {
        // Mostly off screen is still on screen.
        var items = new[] { Item(0, (0.1, 0.1), (0.4, 0.3)) };   // slot y 80..240

        Assert.Single(Cull(items, (0, 200, 1000, 1000)));
    }

    [Fact]
    public void the_stroke_counts_as_part_of_the_shape()
    {
        // A path whose centreline is just outside still paints inside, because
        // half its width reaches over. Testing the centreline alone makes a
        // thick mark disappear half a stroke early.
        //
        // Stroke 0.004 * 800 = 3.2 slot DIPs, so 1.6 either side. A path at
        // slot y = 241.0 reaches to 239.4.
        var items = new[] { Item(0, (0.1, 0.30125), (0.4, 0.30125)) };   // slot y = 241

        Assert.Single(Cull(items, (0, 0, 1000, 240)));
    }

    [Fact]
    public void paint_order_survives_culling()
    {
        // Dropping an item must not restack the rest.
        var items = new[]
        {
            Item(0, (0.1, 0.1)),
            Item(40, (0.1, 0.1)),        // dropped
            Item(0, (0.2, 0.2)),
            Item(0, (0.3, 0.3)),
        };

        var kept = Cull(items, (0, 0, 1000, 1000));

        Assert.Equal(3, kept.Count);
        Assert.Equal([(0.1, 0.1), (0.2, 0.2), (0.3, 0.3)],
                     kept.Select(k => k.Points[0]));
    }

    [Fact]
    public void an_item_with_no_geometry_is_dropped_rather_than_kept_by_accident()
    {
        var items = new[] { Item(0) };

        Assert.Empty(Cull(items, (0, 0, 100000, 100000)));
    }

    // ---------------- the five boundary placements ----------------
    //
    // A mark is kept or dropped, and the two failures look nothing alike: a
    // wrongly dropped mark vanishes at the edge of the window, a wrongly kept
    // one costs work and draws nothing. The placements below are the cases that
    // separate them, and they are named rather than numbered so a failure says
    // which one.

    /// <summary>A 200x200 slot-DIP window at the origin, for placement tests.</summary>
    private static readonly (double, double, double, double) Window = (0.0, 0.0, 200.0, 200.0);

    /// <summary>
    /// A mark of a given slot-space box, expressed normalized so it goes
    /// through the real projection rather than being placed by hand.
    /// </summary>
    private static ShapeRenderItem At(double l, double t, double r, double b) =>
        Item(0, (l / Scale, t / Scale), (r / Scale, t / Scale),
                (r / Scale, b / Scale), (l / Scale, b / Scale));

    [Fact]
    public void a_mark_fully_inside_is_kept()
    {
        Assert.Single(Cull([At(40, 40, 160, 160)], Window));
    }

    [Fact]
    public void a_mark_touching_an_edge_is_kept()
    {
        // Its right edge sits exactly ON the window's. Dropping a mark that
        // touches is how a shape disappears the instant it reaches the edge,
        // which is the complaint culling bugs actually generate.
        Assert.Single(Cull([At(40, 40, 200, 160)], Window));
    }

    [Fact]
    public void a_mark_whose_stroke_exactly_reaches_the_edge_is_kept()
    {
        // EXACTLY on the boundary, to the last decimal: the centreline is off
        // screen and half the stroke lands on the very first column, so the
        // padded box's right edge equals the window's left edge.
        //
        // The looser "touching" fixture above does NOT catch a strict-inequality
        // overlap test, because its own stroke pads it well past the edge; that
        // was found by breaking the culler and watching every test still pass.
        // This is the one that fails.
        double reach = 0.004 * Scale / 2;

        Assert.Single(Cull([At(-100, 40, -reach, 160)], Window));
    }

    [Fact]
    public void a_mark_a_hair_beyond_the_edge_is_dropped()
    {
        // The other side of the same boundary, so "keep everything" is not a
        // way to pass the test above.
        double reach = 0.004 * Scale / 2;

        Assert.Empty(Cull([At(-100, 40, -reach - 0.5, 160)], Window));
    }

    [Fact]
    public void a_mark_half_outside_is_kept_because_half_of_it_shows()
    {
        Assert.Single(Cull([At(120, 40, 320, 160)], Window));
    }

    [Fact]
    public void a_mark_crossing_only_a_corner_is_kept()
    {
        // Overlaps in both axes but by very little. A test that ANDs the axes
        // wrongly, or checks the centre instead of the box, drops this one.
        Assert.Single(Cull([At(190, 190, 400, 400)], Window));
    }

    [Fact]
    public void a_mark_completely_outside_is_dropped()
    {
        Assert.Empty(Cull([At(400, 400, 500, 500)], Window));
    }

    [Fact]
    public void a_mark_outside_on_one_axis_only_is_still_dropped()
    {
        // Fully inside vertically, fully outside horizontally. An overlap test
        // that ORs the axes keeps this, and it is not on screen.
        Assert.Empty(Cull([At(400, 40, 500, 160)], Window));
    }

    // ---------------- rotation changes which marks are on screen ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void culling_uses_each_pages_own_turn(int rotation)
    {
        // The property, stated so it holds at every rotation rather than
        // checked at one: a mark is kept exactly when its PROJECTED box meets
        // the window. Culling that projected differently from the painter would
        // disagree with this at some rotation and not others.
        var view = PageTransform.For(Scale, 1000, rotation, Scale);
        var item = At(120, 300, 260, 420);

        double reach = OverlayProjection.WidthOf(item, Scale, view) / 2;
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var p in item.Points)
        {
            var (x, y) = OverlayProjection.ToSlot(p, Scale, 0, view);
            l = Math.Min(l, x - reach); t = Math.Min(t, y - reach);
            r = Math.Max(r, x + reach); b = Math.Max(b, y + reach);
        }

        // A window that exactly contains the projected box is a keep, and one
        // just clear of it is a drop.
        var over = (l, t, r, b);
        var clear = (r + 10, b + 10, r + 100, b + 100);

        Assert.Single(ShapeCulling.Visible([item], over, Scale, _ => 0, _ => view));
        Assert.Empty(ShapeCulling.Visible([item], clear, Scale, _ => 0, _ => view));
    }

    // ---------------- multi-page ----------------

    [Fact]
    public void only_the_pages_near_the_window_survive()
    {
        // What culling is for. The same mark on forty pages, a window over one
        // of them, and exactly the neighbours that reach it are kept.
        var items = new List<ShapeRenderItem>();
        for (int page = 0; page < 40; page++)
        {
            items.Add(Item(page, (0.1, 0.1), (0.4, 0.3)));
        }

        // Page 10 spans 10000..11000. A window inside it touches only page 10.
        var kept = ShapeCulling.Visible(items, (0, 10050, 800, 10250), Scale, PageTop, Flat);

        Assert.Equal(10, Assert.Single(kept).PageIndex);
    }

    [Fact]
    public void a_window_straddling_two_pages_keeps_both()
    {
        var items = new[] { Item(3, (0.1, 0.9), (0.4, 0.95)), Item(4, (0.1, 0.02), (0.4, 0.06)) };

        var kept = ShapeCulling.Visible(items, (0, 3700, 800, 4100), Scale, PageTop, Flat);

        Assert.Equal([3, 4], kept.Select(k => k.PageIndex));
    }

    [Fact]
    public void each_page_is_asked_for_its_own_top_and_its_own_turn()
    {
        // Page routing. If either lookup ignored the item's page and used a
        // fixed one, two marks on different pages would cull identically, and
        // here exactly one of them is on screen.
        var pages = new List<int>();
        var items = new[] { Item(0, (0.2, 0.2), (0.3, 0.3)), Item(7, (0.2, 0.2), (0.3, 0.3)) };

        var kept = ShapeCulling.Visible(
            items, (0, 6900, 800, 7400), Scale,
            page => { pages.Add(page); return PageTop(page); },
            Flat);

        Assert.Equal(7, Assert.Single(kept).PageIndex);
        Assert.Equal([0, 7], pages);
    }

    [Fact]
    public void culling_and_the_projection_agree_about_what_is_on_screen()
    {
        // The join between the two halves: the region comes from the
        // projection, so a shape the projection maps onto the surface must be
        // one culling keeps.
        var p = new ViewportProjection(Zoom: 2, DeviceScale: 1.5, OriginXDips: -100, OriginYDips: -2400);
        var bounds = p.VisibleSlotBounds(1200, 800);

        // Something in the middle of what the surface can see.
        double midY = (bounds.Top + bounds.Bottom) / 2;
        int page = (int)(midY / 1000);
        double normY = (midY - PageTop(page)) / Scale;

        var items = new[] { Item(page, (0.2, normY), (0.3, normY)) };

        Assert.Single(ShapeCulling.Visible(items, bounds, Scale, PageTop, Flat));
    }
}
