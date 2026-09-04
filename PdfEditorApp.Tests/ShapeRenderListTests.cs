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

    // ---- the live preview ----

    private static ShapeAnnotation Draft(ShapeKind kind, int page = 0, string? color = null) =>
        new(page, new ShapeDraft(kind, 0.2, 0.2, 0.6, 0.5), color ?? Blue, 0.004);

    [Fact]
    public void nothing_in_progress_adds_nothing_to_the_frame()
    {
        // The common case, by a wide margin: the frame is rebuilt on every
        // scroll and every commit, and almost none of those are mid-drag.
        var committed = ShapeRenderList.From([], [Shape(ShapeKind.Rectangle)]);
        var withNoPreview = ShapeRenderList.From([], [Shape(ShapeKind.Rectangle)], preview: null);

        Assert.Equal(committed.Count, withNoPreview.Count);
    }

    [Fact]
    public void the_shape_being_dragged_is_painted_last()
    {
        // On top of everything committed, which is what the overlay does by
        // adding the preview to the canvas after the rest. Anywhere else in the
        // list and a preview disappears behind a mark it overlaps.
        var items = ShapeRenderList.From(
            [Stroke(0, (0, 0), (0.1, 0.1))],
            [Shape(ShapeKind.Rectangle), Shape(ShapeKind.Ellipse)],
            preview: Draft(ShapeKind.Rectangle, color: FourDistinctChannels));

        Assert.Equal(4, items.Count);
        Assert.Equal(new RenderColor(0x80, 0x11, 0x22, 0x33), items[^1].Color);
    }

    [Fact]
    public void a_dragged_arrow_previews_its_shaft_then_its_head()
    {
        // Same order as a committed arrow, so the head sits over the shaft in
        // the preview exactly as it will once the pointer lifts.
        var items = ShapeRenderList.From([], [], preview: Draft(ShapeKind.Arrow));

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
    public void a_dragged_shape_that_is_not_an_arrow_previews_no_head(ShapeKind kind)
    {
        var only = Assert.Single(ShapeRenderList.From([], [], preview: Draft(kind)));

        Assert.Equal(RenderStyle.Stroked, only.Style);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void the_preview_is_the_same_geometry_as_the_shape_that_replaces_it(ShapeKind kind)
    {
        // THE regression this feature can produce: a shape that jumps the
        // instant the pointer lifts. It cannot, because the preview goes
        // through the same emit path as a committed shape and derives its
        // points from the same draft, and this is what says so.
        var shape = Draft(kind, page: 3);

        var previewed = ShapeRenderList.From([], [], preview: shape);
        var committed = ShapeRenderList.From([], [shape]);

        // Field by field, and the points as a SEQUENCE. Comparing the items
        // directly passes for the wrong reason or fails for one: a record
        // struct's generated equality compares its IReadOnlyList field by
        // reference, and Outline builds a fresh list on every call, so two
        // identical arrows are never "equal" and two aliased ones always are.
        Assert.Equal(committed.Count, previewed.Count);
        for (int at = 0; at < committed.Count; at++)
        {
            Assert.Equal(committed[at].PageIndex, previewed[at].PageIndex);
            Assert.Equal(committed[at].Color, previewed[at].Color);
            Assert.Equal(committed[at].StrokeWidth, previewed[at].StrokeWidth);
            Assert.Equal(committed[at].Style, previewed[at].Style);
            Assert.Equal(committed[at].Points, previewed[at].Points);
        }
    }

    [Fact]
    public void the_preview_keeps_its_own_page()
    {
        // A drag can start on a visible page that is not the current one, so
        // the preview has to carry the page it began on or it is drawn on the
        // wrong one.
        var items = ShapeRenderList.From([], [], preview: Draft(ShapeKind.Rectangle, page: 7));

        Assert.Equal(7, items[0].PageIndex);
    }

    // ---- the freehand guide ----

    [Fact]
    public void the_freehand_guide_is_red_and_a_fixed_two_dips()
    {
        // Not the ink's colour and not the ink's weight, which is the ONE way a
        // freehand preview differs from a shape preview. It shows where the pen
        // has been; it is not a preview of what the ink will look like.
        var guide = ShapeRenderList.InkGuide(0, [(0.1, 0.1), (0.2, 0.2)]);

        Assert.Equal(new RenderColor(0xFF, 0xFF, 0x00, 0x00), guide.Color);
        Assert.Equal(2.0, guide.SlotWidth);
        Assert.Equal(RenderStyle.Stroked, guide.Style);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void the_guide_stays_two_dips_whatever_the_page_is_doing(int rotation)
    {
        // The whole reason SlotWidth exists. A normalized width is multiplied
        // by the overlay scale AND the page's scale, so on a turned page it
        // would come out thinner; the guide does not, because the overlay's is
        // a literal 2 that nothing scales.
        var view = PageTransform.For(800, 1000, rotation, 800);
        var guide = ShapeRenderList.InkGuide(0, [(0.1, 0.1), (0.2, 0.2)]);

        Assert.Equal(2.0, OverlayProjection.WidthOf(guide, 800, view));
    }

    [Fact]
    public void everything_without_a_slot_width_is_still_normalized()
    {
        // The other half of the same rule, so adding the override cannot have
        // quietly changed how an ordinary mark is measured.
        var view = PageTransform.For(800, 1000, 0, 800);
        var normal = ShapeRenderList.From([], [Shape(ShapeKind.Rectangle)])[0];

        Assert.Null(normal.SlotWidth);
        Assert.Equal(3.2, OverlayProjection.WidthOf(normal, 800, view), 6);
    }

    [Fact]
    public void the_stroke_being_drawn_is_painted_last()
    {
        var items = ShapeRenderList.From(
            [Stroke(0, (0, 0), (0.1, 0.1))],
            [Shape(ShapeKind.Rectangle)],
            inkPreview: ShapeRenderList.InkGuide(4, [(0.3, 0.3), (0.4, 0.4)]));

        Assert.Equal(3, items.Count);
        Assert.Equal(4, items[^1].PageIndex);
        Assert.Equal(2.0, items[^1].SlotWidth);
    }

    [Fact]
    public void no_stroke_in_progress_adds_nothing()
    {
        var items = ShapeRenderList.From([], [Shape(ShapeKind.Rectangle)], inkPreview: null);

        Assert.Single(items);
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
        //
        // This is the page's own frame, WITHOUT the view's turn, which is what
        // the two diagnostic harnesses want and what a renderer must not stop
        // at. The turned rule is pinned in SkiaRotationParityTests.
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
        // ⚠️ NORMALIZED, BECAUSE A LINE ENDING IS NOT A FACT ABOUT THE CODE.
        // This repository is checked out with core.autocrlf, so whether a file
        // arrives with CRLF or LF depends on the machine and on which tool last
        // wrote it. An assertion that spans a line break and happens to name one
        // of the two is a test that passes or fails for a reason having nothing
        // to do with what it is checking.
        return File.ReadAllText(Path.Combine(dir!.FullName, path))
            .Replace("\r\n", "\n");
    }

    [Fact]
    public void the_overlay_still_projects_the_way_this_assembly_believes_it_does()
    {
        // OverlayProjection claims to reproduce BuildStrokePolyline, and this
        // assembly cannot call it, so the claim is guarded at the source.
        //
        // The claim was PARTIAL for one commit and is whole again. The overlay
        // was corrected to route every point through the page's PageTransform,
        // which left normalized-to-slot as only the first half of the story;
        // OverlayProjection now states both halves and Skia takes the same two,
        // so there is one rule and two renderers held to it. What this guards is
        // the end the tests cannot call: that the overlay is still performing
        // that arithmetic and has not quietly gone back to the half.
        //
        // Read from OverlayShapeBuilder, where that arithmetic now lives so the
        // parity harness measures the real construction instead of its own copy.
        string builder = ReadSource("PdfEditorApp", "Rendering", "OverlayShapeBuilder.cs");

        Assert.Contains("view.ToCard(x * scale, y * scale)", builder, StringComparison.Ordinal);
        Assert.Contains("stroke.StrokeWidth * scale * view.Scale", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void the_skia_layer_is_fed_on_every_preview_change_too()
    {
        // The whole reason a preview never appeared under Skia: the layer was
        // fed only when the COMMITTED collection changed, which is pointer-up.
        // The preview signal has to reach it as well, and the refresh has to
        // sit outside the preview builder, because that method returns early
        // when nothing is in progress and that is precisely the case which
        // clears a finished or cancelled drag off the surface.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains(
            "UpdateInkPreview();\n        RefreshSkiaShapeLayer();", page, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.AllInkStrokes, ViewModel.AllShapes, PreviewShape(), PreviewInkGuide()",
            page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_freehand_guide_resolves_by_the_same_rule_in_both_renderers()
    {
        // The overlay decides with a ternary that prefers the shape draft. The
        // Skia feed has to agree, or the two disagree about which preview is on
        // screen the moment both could answer.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains(
            "ViewModel.ShapeInProgress is null && ViewModel.CurrentStrokeInProgress is { } points",
            page, StringComparison.Ordinal);
        Assert.Contains("ShapeRenderList.InkGuide(ViewModel.ActiveInkPage, points)",
                        page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_skia_layer_is_handed_the_same_page_transform_the_overlay_uses()
    {
        // Both renderers must read the turn from the SAME source. The overlay
        // calls ViewTransformOf per stroke; if the Skia host were passed
        // anything else, the two would agree at 0 degrees and diverge silently
        // the moment the view was turned, which is the exact failure this whole
        // commit exists to remove.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("ViewModel.ViewTransformOf", page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_heads_hairline_is_still_unscaled_in_the_overlay()
    {
        // The one number in the overlay that is not multiplied by the scale.
        // It moved with the builder it belongs to.
        string builder = ReadSource("PdfEditorApp", "Rendering", "OverlayShapeBuilder.cs");

        Assert.Contains("StrokeThickness = 0.5", builder, StringComparison.Ordinal);
        Assert.Equal(0.5, OverlayProjection.HeadHairlineDips);
    }

    [Fact]
    public void the_skia_painter_draws_that_same_hairline_round_a_filled_head()
    {
        // The overlay's head is a Polygon with Fill AND Stroke set to the same
        // brush, so the hairline is part of how big an arrow tip is, not
        // decoration. Skia fills and strokes the same path for the same reason.
        //
        // Guarded at the source rather than in pixels on purpose: half a DIP
        // centred on the path is a quarter of a pixel of extra coverage at 1x,
        // which is inside the fill's own antialiasing and cannot be measured
        // without asserting on the antialiaser's behaviour instead of ours.
        string painter = ReadSource("PdfEditorApp.Rendering.Skia", "ShapeSkiaPainter.cs");

        Assert.Contains("SKPaintStyle.Fill", painter, StringComparison.Ordinal);
        Assert.Contains("StrokeWidth = (float)OverlayProjection.HeadHairlineDips",
                        painter, StringComparison.Ordinal);
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
        foreach (string file in new[]
        {
            "ShapeRenderItem.cs", "OverlayProjection.cs",
            "ViewportProjection.cs", "ShapeCulling.cs",
        })
        {
            string source = ReadSource("PdfEditorApp.Viewport", file);
            Assert.DoesNotContain("SkiaSharp", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SK", source, StringComparison.Ordinal);
        }
    }
}
