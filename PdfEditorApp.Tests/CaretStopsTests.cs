using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where a caret may stand in text the page drew with a shaper.
/// </summary>
/// <remarks>
/// ⚠️ THE PAGE'S CLUSTERS AND .NET'S TEXT ELEMENTS ARE NOT THE SAME
/// SEGMENTATION, and the editor used to move by one and draw by the other. Every
/// case below is MEASURED, by shaping the reader's own words with rustybuzz
/// against the faces the files actually use, so these are not invented shapes:
///
/// <code>
///   word                     face          cluster starts   text elements
///   dharmakshetra (11 u16)   Nirmala       0 1 4 8          0 1 3 4 6 8 10
///   putra          (5 u16)   Nirmala       0 2              0 2 4
///   kyawnaw        (9 u16)   MyanmarText   0 3 5            0 3 5 7
///   akhara         (6 u16)   MyanmarText   0 1 4            0 1 3 4 5
/// </code>
///
/// Every text element that is not a cluster start is an offset the page has no
/// position for. The caret was drawn at the NEXT cluster's edge, so the press
/// did nothing, and a backspace there removed characters from inside the cluster
/// the caret appeared to stand after.
/// </remarks>
public class CaretStopsTests
{
    // The reader's own words, from diag.log, with the cluster starts measured
    // against the face each file uses.
    private const string Dharmakshetra = "\u0927\u0930\u094D\u092E\u0915\u094D\u0937\u0947\u0924\u094D\u0930";
    private const string Putra = "\u092A\u0941\u0924\u094D\u0930";
    private const string Kyawnaw = "\u1000\u103B\u103D\u1014\u103A\u1010\u1031\u102C\u103A";
    private const string Akhara = "\u1021\u1000\u1039\u1001\u101B\u102C";

    public static TheoryData<string, int[], int[]> Measured => new()
    {
        // text,          cluster starts,      every position the page has
        { Dharmakshetra,  new[] { 0, 1, 4, 8 }, new[] { 0, 1, 4, 8, 11 } },
        { Putra,          new[] { 0, 2 },       new[] { 0, 2, 5 } },
        { Kyawnaw,        new[] { 0, 3, 5 },    new[] { 0, 3, 5, 9 } },
        { Akhara,         new[] { 0, 1, 4 },    new[] { 0, 1, 4, 6 } },
    };

    /// <summary>
    /// ⚠️ THE ONE THAT MATTERS. Pressing Right from the start of the word must
    /// visit exactly the positions the page draws, and no others.
    /// </summary>
    [Theory]
    [MemberData(nameof(Measured))]
    public void every_right_press_lands_on_a_cluster_boundary(
        string text, int[] clusterStarts, int[] expected)
    {
        var buffer = BufferFor(text, clusterStarts, caret: 0);

        var visited = new List<int> { buffer.Caret };
        for (int guard = 0; guard < 64 && buffer.Caret < text.Length; guard++)
        {
            buffer.MoveRight();
            visited.Add(buffer.Caret);
        }

        Assert.Equal(expected, visited);
    }

    /// <summary>And back again, which is not the same walk done in reverse.</summary>
    [Theory]
    [MemberData(nameof(Measured))]
    public void every_left_press_lands_on_a_cluster_boundary(
        string text, int[] clusterStarts, int[] expected)
    {
        var buffer = BufferFor(text, clusterStarts, caret: text.Length);

        var visited = new List<int> { buffer.Caret };
        for (int guard = 0; guard < 64 && buffer.Caret > 0; guard++)
        {
            buffer.MoveLeft();
            visited.Add(buffer.Caret);
        }

        Assert.Equal(expected.Reverse().ToArray(), visited);
    }

    /// <summary>
    /// ⚠️ A CONJUNCT MUST NEVER BE SPLIT BY THE CARET. The live tail is drawn
    /// as the text before the caret and the text after it, laid out separately,
    /// so a caret standing inside a cluster makes the page render the conjunct
    /// in halves while the reader arrows through it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Measured))]
    public void no_reachable_position_falls_inside_a_cluster(
        string text, int[] clusterStarts, int[] expected)
    {
        var buffer = BufferFor(text, clusterStarts, caret: 0);

        for (int guard = 0; guard < 64 && buffer.Caret < text.Length; guard++)
        {
            AssertWholeClusters(buffer.Caret, text, clusterStarts);
            buffer.MoveRight();
        }
        AssertWholeClusters(buffer.Caret, text, clusterStarts);

        // And the walk really did cover the word, rather than passing by never
        // moving at all.
        Assert.Equal(text.Length, buffer.Caret);
        Assert.True(expected.Length > 1);
    }

    /// <summary>
    /// ⚠️ THE CONTROL, AND WITHOUT IT THE TESTS ABOVE PROVE NOTHING. Text
    /// elements are what the buffer used before, and for these words they offer
    /// positions the page cannot place. If this ever stops failing to line up,
    /// the difference has gone away and the fix above is no longer load bearing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Measured))]
    public void text_elements_would_have_offered_positions_the_page_has_not(
        string text, int[] clusterStarts, int[] expected)
    {
        var elements = TextElementStops(text);

        Assert.True(
            elements.Count > expected.Length,
            $"text elements ({elements.Count}) no longer outnumber the page's positions "
            + $"({expected.Length}) for this word");

        // At least one of them stands inside a cluster the page draws whole.
        Assert.Contains(elements, at => IsInsideACluster(at, text, clusterStarts));
    }

    /// <summary>
    /// Once the reader has typed, the page no longer draws what the buffer
    /// holds, so the page's positions stop being the authority.
    /// </summary>
    [Fact]
    public void a_changed_line_falls_back_to_text_elements()
    {
        var buffer = BufferFor(Putra, new[] { 0, 2 }, caret: Putra.Length);
        buffer.Insert("x");

        // The prefix is still the page's, so the walk over it is unchanged.
        buffer.MoveHome();
        buffer.MoveRight();
        Assert.Equal(2, buffer.Caret);

        // Past it, the text is ours and the text engine lays it out, so a text
        // element is the honest boundary.
        buffer.MoveEnd();
        buffer.MoveLeft();
        Assert.Equal(Putra.Length, buffer.Caret);
    }

    /// <summary>A buffer nobody told anything keeps exactly its old behaviour.</summary>
    [Fact]
    public void without_stops_the_buffer_moves_by_text_elements_as_before()
    {
        var buffer = new LineEditBuffer(Dharmakshetra, 0);

        var visited = new List<int> { 0 };
        for (int guard = 0; guard < 64 && buffer.Caret < Dharmakshetra.Length; guard++)
        {
            buffer.MoveRight();
            visited.Add(buffer.Caret);
        }

        Assert.Equal(TextElementStops(Dharmakshetra), visited);
    }

    // ---------------- the stop list itself ----------------

    [Fact]
    public void a_stop_is_made_for_every_cluster_and_one_for_the_end()
    {
        var stops = CaretStops.Of(GlyphsFor(Putra, new[] { 0, 2 }), Putra.Length);

        Assert.Equal(new[] { 0, 2, 5 }, stops.Select(s => s.Offset).ToArray());
        Assert.Equal(new[] { 0d, 10d, 20d }, stops.Select(s => s.X).ToArray());
    }

    /// <summary>
    /// ⚠️ DIRECTION LIVES HERE AND NOWHERE ELSE. No right-to-left file is
    /// supported yet and none is being added; what is being established is that
    /// "before this cluster" is a question the geometry answers, so that a
    /// right-to-left run needs no second code path in the caret, the arrows, the
    /// click or the buffer.
    /// </summary>
    [Fact]
    public void a_right_to_left_run_takes_the_other_edge_of_every_cluster()
    {
        var glyphs = GlyphsFor(Putra, new[] { 0, 2 });

        var ltr = CaretStops.Of(glyphs, Putra.Length, TextDirection.LeftToRight);
        var rtl = CaretStops.Of(glyphs, Putra.Length, TextDirection.RightToLeft);

        // The same offsets: what the text says has not changed.
        Assert.Equal(ltr.Select(s => s.Offset), rtl.Select(s => s.Offset));

        // The other edge of each: where the page puts them has.
        Assert.Equal(new[] { 0d, 10d, 20d }, ltr.Select(s => s.X).ToArray());
        Assert.Equal(new[] { 10d, 20d, 10d }, rtl.Select(s => s.X).ToArray());
    }

    /// <summary>
    /// ⚠️ LOGICAL ORDER AND VISUAL ORDER ARE KEPT APART ON PURPOSE. For Latin,
    /// Devanagari and Burmese they agree, so nothing is lost; for a right-to-left
    /// run they do not, and the arrow keys must walk the visual one while
    /// backspace uses the logical one.
    /// </summary>
    [Fact]
    public void visual_order_is_not_assumed_to_be_logical_order()
    {
        // Two clusters of a right-to-left run: the FIRST character is drawn on
        // the right.
        var glyphs = new List<EditGlyph>
        {
            new(Left: 10, Right: 20, Bottom: 0, Offset: 0, PointSize: 10, FontName: "f", ColorHex: "#000"),
            new(Left: 0, Right: 10, Bottom: 0, Offset: 1, PointSize: 10, FontName: "f", ColorHex: "#000"),
        };

        var stops = CaretStops.Of(glyphs, 2, TextDirection.RightToLeft);
        var visual = CaretStops.InVisualOrder(stops);

        // Logical: offsets ascend. Visual: x ascends, and the offsets do not.
        Assert.Equal(new[] { 0, 1, 2 }, stops.Select(s => s.Offset).ToArray());
        Assert.Equal(new[] { 2, 1, 0 }, visual.Select(s => s.Offset).ToArray());
        Assert.Equal(new[] { 0d, 10d, 20d }, visual.Select(s => s.X).ToArray());
    }

    [Fact]
    public void a_click_resolves_to_the_nearest_stop()
    {
        var stops = CaretStops.Of(GlyphsFor(Putra, new[] { 0, 2 }), Putra.Length);
        var visual = CaretStops.InVisualOrder(stops);

        Assert.Equal(0, visual[CaretStops.NearestTo(visual, 1)].Offset);
        Assert.Equal(2, visual[CaretStops.NearestTo(visual, 9)].Offset);

        // ⚠️ THE FAR EDGE OF THE LAST CLUSTER IS A POSITION IN ITS OWN RIGHT.
        // A caret after the final letter is where anyone clicks to add to the
        // end of a word.
        Assert.Equal(5, visual[CaretStops.NearestTo(visual, 21)].Offset);
    }

    /// <summary>
    /// The pieces of a visual line concatenate, which is what lets the caret
    /// cross a word without the recovery or the writer being told anything.
    /// </summary>
    [Fact]
    public void pieces_of_one_visual_line_concatenate_in_the_order_they_draw()
    {
        var first = CaretStops.Of(GlyphsFor("ab", new[] { 0, 1 }, at: 0), 2, piece: 0);
        var second = CaretStops.Of(GlyphsFor("cd", new[] { 0, 1 }, at: 40), 2, piece: 1);

        var line = CaretStops.InVisualOrder(first.Concat(second).ToList());

        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1 }, line.Select(s => s.Piece).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, line.Select(s => s.Offset).ToArray());

        // Each piece still counts its own offsets from zero, because each is
        // still a whole unit to the writer.
        Assert.Equal(3, CaretStops.IndexOf(line, piece: 1, offset: 0));
    }

    [Fact]
    public void a_piece_with_no_clusters_offers_no_positions_at_all()
    {
        Assert.Empty(CaretStops.Of(new List<EditGlyph>(), 5));
        Assert.Equal(-1, CaretStops.NearestTo(new List<CaretStop>(), 0));
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// A cluster box per measured cluster start, ten units wide and in order.
    /// The widths are arbitrary; only their order and the offsets they carry
    /// are under test here.
    /// </summary>
    private static List<EditGlyph> GlyphsFor(string text, int[] clusterStarts, double at = 0)
    {
        var glyphs = new List<EditGlyph>(clusterStarts.Length);
        for (int i = 0; i < clusterStarts.Length; i++)
        {
            glyphs.Add(new EditGlyph(
                Left: at + (i * 10),
                Right: at + ((i + 1) * 10),
                Bottom: 0,
                Offset: clusterStarts[i],
                PointSize: 10,
                FontName: "face",
                ColorHex: "#000000"));
        }
        return glyphs;
    }

    private static LineEditBuffer BufferFor(string text, int[] clusterStarts, int caret)
    {
        var buffer = new LineEditBuffer(text, caret);
        buffer.SetPlaceableOffsets(
            CaretStops.OffsetsOf(CaretStops.Of(GlyphsFor(text, clusterStarts), text.Length)));
        return buffer;
    }

    private static List<int> TextElementStops(string text)
    {
        var stops = new List<int> { 0 };
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            stops.Add(e.ElementIndex + ((string)e.Current).Length);
        }
        return stops;
    }

    /// <summary>Whether an offset stands strictly inside a cluster.</summary>
    private static bool IsInsideACluster(int at, string text, int[] clusterStarts)
    {
        for (int i = 0; i < clusterStarts.Length; i++)
        {
            int from = clusterStarts[i];
            int to = i + 1 < clusterStarts.Length ? clusterStarts[i + 1] : text.Length;
            if (at > from && at < to) { return true; }
        }
        return false;
    }

    private static void AssertWholeClusters(int at, string text, int[] clusterStarts) =>
        Assert.False(
            IsInsideACluster(at, text, clusterStarts),
            $"caret at {at} splits a cluster of \u201C{text}\u201D, so the page would draw "
            + "the two halves separately and the conjunct would come apart");
}
