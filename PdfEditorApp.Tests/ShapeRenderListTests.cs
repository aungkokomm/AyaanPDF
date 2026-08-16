using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The seam a second renderer is built on.
///
/// Stage 0 of the Skia migration ships no renderer at all. It ships a flat
/// description of what a frame contains, and these tests exist to pin that
/// description to what the EXISTING overlay draws, so the renderer that arrives
/// next can be proved equal to the one already on screen rather than compared
/// by eye.
///
/// The overlay itself cannot be loaded here: it is a WinUI page. So the two
/// halves are covered separately. The arithmetic is tested directly, and a
/// source guard asserts the overlay still performs that same arithmetic, which
/// is what makes the first half mean anything.
/// </summary>
public class ShapeRenderListTests
{
    private static readonly string Blue = "#FF3B82F6";

    /// <summary>
    /// Every channel a different value, on purpose. A colour whose red and blue
    /// happen to match cannot tell a channel swap from a correct parse, and a
    /// test that cannot fail is worse than no test: this fixture started as
    /// #8000FF00 and a deliberate R/B swap in the mapper went undetected.
    /// </summary>
    private static readonly string FourDistinctChannels = "#80112233";

    private static InkStrokeAnnotation Stroke(int page, params (double X, double Y)[] points) =>
        new(page, points, Blue, 0.004);

    private static ShapeAnnotation Shape(ShapeKind kind, int page = 0, string? color = null) =>
        new(page, new ShapeDraft(kind, 0.1, 0.1, 0.4, 0.3), color ?? Blue, 0.004);

    [Fact]
    public void nothing_to_draw_is_an_empty_frame()
    {
        Assert.Empty(ShapeRenderList.From([], []));
    }

    [Fact]
    public void ink_comes_before_shapes_and_order_is_preserved()
    {
        // The list IS the paint order. The overlay adds every stroke, then every
        // shape, and two marks that overlap stack in that order. Reordering here
        // would put the wrong one on top with nothing on screen to explain it.
        var strokes = new[] { Stroke(0, (0, 0), (0.1, 0.1)), Stroke(1, (0.2, 0.2), (0.3, 0.3)) };
        var shapes = new[] { Shape(ShapeKind.Rectangle), Shape(ShapeKind.Ellipse, page: 2) };

        var items = ShapeRenderList.From(strokes, shapes);

        Assert.Equal(4, items.Count);
        Assert.Equal([0, 1, 0, 2], items.Select(i => i.PageIndex));
    }

    [Fact]
    public void an_arrow_is_a_shaft_and_a_filled_head()
    {
        // Two items, and the head must come second: it is painted over the
        // shaft, and a filled triangle underneath its own line is not the same
        // picture.
        var items = ShapeRenderList.From([], [Shape(ShapeKind.Arrow)]);

        Assert.Equal(2, items.Count);
        Assert.Equal(RenderStyle.Stroked, items[0].Style);
        Assert.Equal(RenderStyle.Filled, items[1].Style);
        Assert.Equal(3, items[1].Points.Count);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.RoundedRectangle)]
    public void everything_that_is_not_an_arrow_has_no_head(ShapeKind kind)
    {
        var items = ShapeRenderList.From([], [Shape(kind)]);

        var only = Assert.Single(items);
        Assert.Equal(RenderStyle.Stroked, only.Style);
    }

    [Fact]
    public void points_arrive_in_normalized_units_untouched()
    {
        // The whole architectural rule in one assertion: the mapper does not
        // scale, does not offset, and does not hand a renderer anything already
        // converted into a space that renderer chose. Normalized page-local
        // stays canonical and the projection happens later, in one place.
        var points = new[] { (0.25, 0.5), (0.75, 0.125) };

        var only = Assert.Single(ShapeRenderList.From([Stroke(0, points)], []));

        Assert.Equal(points, only.Points);
        Assert.Equal(0.004, only.StrokeWidth);
    }

    [Fact]
    public void a_colour_is_parsed_the_way_the_overlay_parses_it()
    {
        // The overlay reads AARRGGBB in that order. Getting the order wrong
        // gives a plausible-looking colour, which is the kind of difference that
        // survives a review and shows up as "the blue looks off".
        var items = ShapeRenderList.From([], [Shape(ShapeKind.Rectangle, color: FourDistinctChannels)]);

        Assert.Equal(new RenderColor(0x80, 0x11, 0x22, 0x33), items[0].Color);
    }

    [Fact]
    public void the_head_takes_the_shapes_colour_not_a_default()
    {
        var items = ShapeRenderList.From([], [Shape(ShapeKind.Arrow, color: FourDistinctChannels)]);

        Assert.Equal(items[0].Color, items[1].Color);
    }

    [Fact]
    public void the_projection_is_the_arithmetic_the_overlay_performs()
    {
        // scale, then drop to the page's top. Only Y takes the offset: X is
        // measured from the same left edge on every page in the stack.
        const double scale = 800;
        const double pageTop = 1234.5;

        Assert.Equal((200.0, 1634.5), OverlayProjection.ToSlot((0.25, 0.5), scale, pageTop));
        Assert.Equal(3.2, OverlayProjection.ToSlotThickness(0.004, scale));
    }

    [Fact]
    public void the_first_page_still_gets_the_offset_applied()
    {
        // A page top of zero must go through the same path rather than being
        // special-cased, or page 0 and page 1 are drawn by two different rules.
        Assert.Equal((80.0, 40.0), OverlayProjection.ToSlot((0.1, 0.05), 800, 0));
    }

    // ---- source guards: these are what make the arithmetic tests mean something ----

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

    [Fact]
    public void the_overlay_still_projects_the_way_this_assembly_believes_it_does()
    {
        // OverlayProjection claims to reproduce BuildStrokePolyline. This
        // assembly cannot call it, so the claim is guarded at the source. If
        // someone changes the reference renderer, the parity work built on top
        // of it must fail loudly rather than quietly stop being parity.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("new Point(x * scale, y * scale + pageTop)", page, StringComparison.Ordinal);
        Assert.Contains("StrokeThickness = stroke.StrokeWidth * scale", page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_heads_hairline_is_still_unscaled_in_the_overlay()
    {
        // The one number in the overlay that is not multiplied by the scale.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("StrokeThickness = 0.5", page, StringComparison.Ordinal);
        Assert.Equal(0.5, OverlayProjection.HeadHairlineDips);
    }

    [Fact]
    public void the_reference_renderer_is_still_there()
    {
        // The fallback is the plan's rollback. It is not to be refactored away
        // while a second renderer is being proved against it.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("private void RebuildInkCanvas()", page, StringComparison.Ordinal);
        Assert.Contains("private Polyline BuildStrokePolyline(", page, StringComparison.Ordinal);
        Assert.Contains("private Polygon BuildFilledHead(", page, StringComparison.Ordinal);
    }

    [Fact]
    public void no_skia_type_has_leaked_into_the_shared_library()
    {
        // Enforced from stage 0, before there is anything to leak. SK* types
        // live in the rendering project only; the day one appears in a shared
        // signature is the day the coordinate system starts migrating by
        // accident.
        foreach (string file in new[] { "ShapeRenderItem.cs", "OverlayProjection.cs" })
        {
            string source = ReadSource("PdfEditorApp.Viewport", file);
            Assert.DoesNotContain("SkiaSharp", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SK", source, StringComparison.Ordinal);
        }
    }
}
