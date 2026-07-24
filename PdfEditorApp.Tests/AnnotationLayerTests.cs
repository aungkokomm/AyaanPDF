using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class AnnotationLayerTests
{
    private static HighlightAnnotation Highlight(int page, params TextRect[] rects) =>
        new(page, rects.ToList(), "#FFFF00");

    private static InkStrokeAnnotation Ink(int page, params (double X, double Y)[] pts) =>
        new(page, pts.ToList(), "#FFE00000", 0.002);

    // ---------------- Identity ----------------

    [Fact]
    public void every_annotation_gets_its_own_id()
    {
        var a = Highlight(0, new TextRect(0, 0, 1, 1));
        var b = Highlight(0, new TextRect(0, 0, 1, 1));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void translating_preserves_identity()
    {
        // The whole point of the id: a moved annotation must still be the same
        // one, so a drag does not lose the selection.
        var a = Highlight(0, new TextRect(0.1, 0.1, 0.2, 0.2));
        var moved = a.Translate(0.05, 0.05);
        Assert.Equal(a.Id, moved.Id);
    }

    // ---------------- Highlight hit-testing ----------------

    [Fact]
    public void a_click_inside_a_highlight_rect_hits()
    {
        var h = Highlight(0, new TextRect(0.1, 0.1, 0.4, 0.2));
        Assert.True(h.HitTest(0.25, 0.15, 0));
    }

    [Fact]
    public void a_click_in_the_gap_between_highlight_lines_misses()
    {
        // Two lines with a gap. The bounding box covers the gap, so a
        // box-based test would wrongly select here.
        var h = Highlight(0,
            new TextRect(0.1, 0.10, 0.4, 0.15),
            new TextRect(0.1, 0.30, 0.4, 0.35));

        Assert.True(h.HitTest(0.2, 0.12, 0));
        Assert.True(h.HitTest(0.2, 0.32, 0));
        Assert.False(h.HitTest(0.2, 0.22, 0));
    }

    [Fact]
    public void highlight_bounds_span_all_rects()
    {
        var h = Highlight(0,
            new TextRect(0.1, 0.10, 0.4, 0.15),
            new TextRect(0.2, 0.30, 0.6, 0.35));

        var b = h.Bounds;
        Assert.Equal(0.1, b.Left, 6);
        Assert.Equal(0.10, b.Top, 6);
        Assert.Equal(0.6, b.Right, 6);
        Assert.Equal(0.35, b.Bottom, 6);
    }

    [Fact]
    public void translating_a_highlight_moves_every_rect()
    {
        var h = Highlight(0, new TextRect(0.1, 0.1, 0.2, 0.2), new TextRect(0.3, 0.3, 0.4, 0.4));
        var moved = (HighlightAnnotation)h.Translate(0.1, 0.05);

        Assert.Equal(0.2, moved.Rects[0].Left, 6);
        Assert.Equal(0.15, moved.Rects[0].Top, 6);
        Assert.Equal(0.4, moved.Rects[1].Left, 6);
        Assert.Equal(0.35, moved.Rects[1].Top, 6);
    }

    // ---------------- Ink hit-testing ----------------

    [Fact]
    public void a_click_on_a_diagonal_stroke_hits_but_the_empty_corner_does_not()
    {
        // A diagonal fills almost none of its bounding box. Both points below
        // are INSIDE the box; only one is near the ink.
        var ink = Ink(0, (0.0, 0.0), (1.0, 1.0));

        Assert.True(ink.HitTest(0.5, 0.5, 0.01));
        Assert.False(ink.HitTest(0.9, 0.1, 0.01));
    }

    [Fact]
    public void a_click_near_a_stroke_hits_within_tolerance()
    {
        var ink = Ink(0, (0.2, 0.5), (0.8, 0.5));
        Assert.True(ink.HitTest(0.5, 0.505, 0.01));
        Assert.False(ink.HitTest(0.5, 0.7, 0.01));
    }

    [Fact]
    public void a_click_past_the_end_of_a_stroke_misses()
    {
        // Distance is to the SEGMENT, not the infinite line it lies on.
        var ink = Ink(0, (0.2, 0.5), (0.4, 0.5));
        Assert.False(ink.HitTest(0.9, 0.5, 0.01));
    }

    [Fact]
    public void a_single_point_stroke_is_still_clickable()
    {
        var ink = Ink(0, (0.5, 0.5));
        Assert.True(ink.HitTest(0.5, 0.5, 0.01));
        Assert.False(ink.HitTest(0.8, 0.5, 0.01));
    }

    [Fact]
    public void a_degenerate_stroke_with_repeated_points_does_not_divide_by_zero()
    {
        var ink = Ink(0, (0.5, 0.5), (0.5, 0.5));
        Assert.True(ink.HitTest(0.5, 0.5, 0.01));
        Assert.False(ink.HitTest(0.9, 0.9, 0.01));
    }

    // ---------------- Picking ----------------

    [Fact]
    public void picking_returns_the_topmost_annotation()
    {
        // Both cover the point; the later one is drawn on top and must win.
        var under = Highlight(0, new TextRect(0.0, 0.0, 1.0, 1.0));
        var over = Highlight(0, new TextRect(0.4, 0.4, 0.6, 0.6));
        var list = new List<IAnnotation> { under, over };

        Assert.Equal(over.Id, AnnotationHitTester.HitTest(list, 0, 0.5, 0.5)!.Id);
    }

    [Fact]
    public void picking_ignores_annotations_on_other_pages()
    {
        var onPage1 = Highlight(1, new TextRect(0.0, 0.0, 1.0, 1.0));
        var list = new List<IAnnotation> { onPage1 };

        Assert.Null(AnnotationHitTester.HitTest(list, 0, 0.5, 0.5));
        Assert.NotNull(AnnotationHitTester.HitTest(list, 1, 0.5, 0.5));
    }

    [Fact]
    public void picking_empty_space_returns_nothing()
    {
        var list = new List<IAnnotation> { Highlight(0, new TextRect(0.0, 0.0, 0.1, 0.1)) };
        Assert.Null(AnnotationHitTester.HitTest(list, 0, 0.9, 0.9));
    }

    [Fact]
    public void picking_an_empty_layer_returns_nothing()
    {
        Assert.Null(AnnotationHitTester.HitTest([], 0, 0.5, 0.5));
    }
}
