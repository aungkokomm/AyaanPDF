using PdfEditorApp.Viewport;

namespace PdfEditorApp.Tests;

public class PageTextLayerTests
{
    // Two visual lines: "AB CD" on the first row (Top 0-20), "ab cd" on the
    // second row (Top 30-50) — simulates text that wraps across a line.
    private static PageTextLayer CreateTwoLineLayer()
    {
        var chars = new List<CharGlyph>
        {
            new(0, 0, 10, 20, 'A'),
            new(10, 0, 20, 20, 'B'),
            new(20, 0, 30, 20, ' '),
            new(30, 0, 40, 20, 'C'),
            new(40, 0, 50, 20, 'D'),
            new(0, 30, 10, 50, 'a'),
            new(10, 30, 20, 50, 'b'),
            new(20, 30, 30, 50, ' '),
            new(30, 30, 40, 50, 'c'),
            new(40, 30, 50, 50, 'd'),
        };
        return new PageTextLayer(chars);
    }

    [Fact]
    public void Text_reconstructs_the_full_string_in_order()
    {
        var layer = CreateTwoLineLayer();
        Assert.Equal("AB CDab cd", layer.Text);
        Assert.Equal(10, layer.CharCount);
    }

    [Fact]
    public void FindMatches_is_case_insensitive()
    {
        var layer = CreateTwoLineLayer();

        var matches = layer.FindMatches("cd");

        Assert.Equal(2, matches.Count);
        Assert.Contains((3, 2), matches); // "CD" at index 3
        Assert.Contains((8, 2), matches); // "cd" at index 8
    }

    [Fact]
    public void FindMatches_returns_empty_for_null_or_empty_query()
    {
        var layer = CreateTwoLineLayer();

        Assert.Empty(layer.FindMatches(""));
        Assert.Empty(layer.FindMatches(null!));
    }

    [Fact]
    public void FindMatches_returns_empty_when_nothing_matches()
    {
        var layer = CreateTwoLineLayer();
        Assert.Empty(layer.FindMatches("xyz"));
    }

    [Fact]
    public void HitTestNearest_finds_exact_hit()
    {
        var layer = CreateTwoLineLayer();

        // Point inside the 'C' box (index 3: Left=30,Top=0,Right=40,Bottom=20).
        int index = layer.HitTestNearest(35, 10);

        Assert.Equal(3, index);
    }

    [Fact]
    public void HitTestNearest_falls_back_to_closest_char_when_outside_all_boxes()
    {
        var layer = CreateTwoLineLayer();

        // Far to the right of the last character on line 1 — nearest should be 'D' (index 4).
        int index = layer.HitTestNearest(1000, 10);

        Assert.Equal(4, index);
    }

    [Fact]
    public void HitTestNearest_returns_minus_one_for_empty_layer()
    {
        var layer = new PageTextLayer(new List<CharGlyph>());
        Assert.Equal(-1, layer.HitTestNearest(0, 0));
    }

    [Fact]
    public void GetRangeRects_returns_one_rect_for_a_single_line_range()
    {
        var layer = CreateTwoLineLayer();

        // "CD" is indices 3-4, both on line 1.
        var rects = layer.GetRangeRects(3, 2);

        Assert.Single(rects);
        Assert.Equal(new TextRect(30, 0, 50, 20), rects[0]);
    }

    [Fact]
    public void GetRangeRects_splits_a_range_that_spans_two_lines()
    {
        var layer = CreateTwoLineLayer();

        // Indices 3..7 span "CDab " — line 1 tail + line 2 head.
        var rects = layer.GetRangeRects(3, 5);

        Assert.Equal(2, rects.Count);
        Assert.Equal(new TextRect(30, 0, 50, 20), rects[0]); // "CD"
        Assert.Equal(new TextRect(0, 30, 30, 50), rects[1]); // "ab "
    }

    [Fact]
    public void GetRangeRects_returns_empty_for_invalid_ranges()
    {
        var layer = CreateTwoLineLayer();

        Assert.Empty(layer.GetRangeRects(0, 0));
        Assert.Empty(layer.GetRangeRects(-1, 2));
        Assert.Empty(layer.GetRangeRects(100, 2));
    }
}
