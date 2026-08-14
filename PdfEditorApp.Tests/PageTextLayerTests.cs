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

    // ---------------- Search options ----------------
    //
    // Matching used to be case-insensitive with no way to say otherwise, which
    // is the right default and the wrong only choice: a reader looking for "IT"
    // in a technical document, or for a name, has no way to say so.

    /// <summary>One glyph per character on a single line, so a test can be
    /// written as the sentence it is about rather than as ten CharGlyphs.</summary>
    private static PageTextLayer LayerOf(string text)
    {
        var chars = new List<CharGlyph>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            chars.Add(new CharGlyph(i * 10, 0, (i * 10) + 10, 20, text[i]));
        }
        return new PageTextLayer(chars);
    }

    private static int[] Starts(PageTextLayer layer, string query, SearchOptions options) =>
        layer.FindMatches(query, options).Select(m => m.Start).ToArray();

    [Fact]
    public void default_options_match_the_behaviour_the_one_argument_overload_has_always_had()
    {
        // The overload without options is what every existing caller and the
        // five tests above use. If these two ever disagree, one of them is
        // silently changing search for everybody.
        var layer = CreateTwoLineLayer();

        Assert.Equal(
            layer.FindMatches("cd"),
            layer.FindMatches("cd", new SearchOptions()));
    }

    [Fact]
    public void match_case_separates_the_two_spellings()
    {
        // "AB CDab cd": "CD" at 3, "cd" at 8.
        var layer = CreateTwoLineLayer();
        var cased = new SearchOptions(MatchCase: true);

        Assert.Equal([3], Starts(layer, "CD", cased));
        Assert.Equal([8], Starts(layer, "cd", cased));
    }

    [Fact]
    public void without_match_case_both_spellings_come_back()
    {
        var layer = CreateTwoLineLayer();
        Assert.Equal([3, 8], Starts(layer, "cd", new SearchOptions()));
    }

    [Fact]
    public void whole_word_skips_a_word_that_merely_contains_the_query()
    {
        var layer = LayerOf("a cat sat");
        Assert.Equal([2], Starts(layer, "cat", new SearchOptions(WholeWord: true)));

        Assert.Empty(Starts(layer, "at", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void whole_word_keeps_looking_after_it_rejects_one()
    {
        // The easy bug: reject the embedded "cat" in "concatenate" and stop,
        // so the real one later in the line is never found. Rejecting has to
        // advance the scan, not end it.
        var layer = LayerOf("concatenate a cat");

        Assert.Equal([14], Starts(layer, "cat", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void whole_word_matches_at_the_very_start_and_the_very_end_of_a_page()
    {
        // Nothing before index 0 and nothing after the last character, and
        // "no neighbour" has to count as a boundary or the first and last words
        // on every page become unfindable.
        Assert.Equal([0], Starts(LayerOf("cat sat"), "cat", new SearchOptions(WholeWord: true)));
        Assert.Equal([4], Starts(LayerOf("sat cat"), "cat", new SearchOptions(WholeWord: true)));
        Assert.Equal([0], Starts(LayerOf("cat"), "cat", new SearchOptions(WholeWord: true)));
    }

    [Theory]
    [InlineData("the cat.")]      // trailing full stop
    [InlineData("(cat) here")]    // brackets
    [InlineData("a \"cat\"")]     // quotes
    [InlineData("cat-like")]      // hyphen
    [InlineData("cat\nsat")]      // line break
    public void punctuation_counts_as_a_word_boundary(string text)
    {
        Assert.NotEmpty(LayerOf(text).FindMatches("cat", new SearchOptions(WholeWord: true)));
    }

    [Theory]
    [InlineData("cat5")]
    [InlineData("5cat")]
    [InlineData("cats")]
    [InlineData("scat")]
    public void a_letter_or_digit_either_side_is_not_a_word_boundary(string text)
    {
        Assert.Empty(LayerOf(text).FindMatches("cat", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void an_underscore_reads_as_a_boundary_which_is_a_consequence_worth_knowing()
    {
        // char.IsLetterOrDigit('_') is false, so "my_cat" contains a whole-word
        // "cat". Programmers would call that wrong and prose readers would never
        // notice. Asserted rather than special-cased: this is a PDF reader, the
        // case is vanishingly rare in prose, and a hand-rolled word-character
        // class would be a bigger thing to be wrong about.
        Assert.Equal([3], Starts(LayerOf("my_cat"), "cat", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void both_options_together_apply_both()
    {
        var layer = LayerOf("Cat cat CAT");

        Assert.Equal([4], Starts(layer, "cat", new SearchOptions(MatchCase: true, WholeWord: true)));
        Assert.Equal([0, 4, 8], Starts(layer, "cat", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void overlapping_matches_are_still_returned()
    {
        // Long-standing behaviour: the scan advances one character past the
        // start of a hit, not past its end. Options must not quietly change it.
        Assert.Equal([0, 1], Starts(LayerOf("aaa"), "aa", new SearchOptions()));
        Assert.Equal([0, 1], Starts(LayerOf("aaa"), "aa", new SearchOptions(MatchCase: true)));
    }

    [Fact]
    public void an_empty_query_finds_nothing_whatever_the_options()
    {
        var layer = CreateTwoLineLayer();

        Assert.Empty(layer.FindMatches("", new SearchOptions(MatchCase: true, WholeWord: true)));
        Assert.Empty(layer.FindMatches(null!, new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void comparison_is_ordinal_either_way()
    {
        // Ordinal, never culture-aware. A culture-sensitive comparison makes
        // search depend on the machine's locale, which is how "I" stops
        // matching "i" for a Turkish user and nobody can reproduce it.
        Assert.Equal(StringComparison.Ordinal, new SearchOptions(MatchCase: true).Comparison);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, new SearchOptions().Comparison);
    }
}
