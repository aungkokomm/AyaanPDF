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

    private static IReadOnlyList<ShapeRenderItem> Cull(
        IReadOnlyList<ShapeRenderItem> items,
        (double, double, double, double) bounds) =>
        ShapeCulling.Visible(items, bounds, Scale, PageTop);

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

        Assert.Single(ShapeCulling.Visible(items, bounds, Scale, PageTop));
    }
}
