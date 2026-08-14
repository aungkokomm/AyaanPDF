using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The order a document-wide search reads its pages in.
///
/// Search used to cover the forty pages nearest the viewport and report the
/// result as though it had covered the document. Covering everything means the
/// scan takes real time on a long book, and that makes the ORDER a visible
/// decision rather than an implementation detail: results stream in as pages
/// are read, so whichever pages are read first are the ones the user sees
/// first. Reading from page zero would mean someone at page 3000 of 3352 waits
/// for 3000 pages of scanning before their first useful hit.
/// </summary>
public class SearchSweepOrderTests
{
    private static int[] Order(int current, int count) =>
        SearchSweepOrder.PagesFrom(current, count).ToArray();

    [Fact]
    public void reading_starts_where_the_user_is_and_wraps()
    {
        // The whole point: the pages near where they are reading come first,
        // and the ones before it are still covered, at the end.
        Assert.Equal([3, 4, 0, 1, 2], Order(3, 5));
    }

    [Fact]
    public void from_the_first_page_it_is_simply_document_order()
    {
        Assert.Equal([0, 1, 2, 3, 4], Order(0, 5));
    }

    [Fact]
    public void from_the_last_page_it_wraps_to_the_beginning()
    {
        Assert.Equal([4, 0, 1, 2, 3], Order(4, 5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(3, 5)]
    [InlineData(0, 40)]
    [InlineData(17, 40)]
    [InlineData(39, 40)]
    [InlineData(3000, 3352)]
    public void every_page_is_read_exactly_once(int current, int count)
    {
        // A wrap that drops or repeats a page is the failure that would make
        // the total count quietly wrong, and a wrong total is worse than a
        // slow search: nothing on screen says the number cannot be trusted.
        var order = Order(current, count);

        Assert.Equal(count, order.Length);
        Assert.Equal(count, order.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, count), order.OrderBy(p => p));
    }

    [Fact]
    public void a_one_page_document_reads_that_page()
    {
        Assert.Equal([0], Order(0, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void a_document_with_no_pages_reads_nothing(int current)
    {
        // Reached when a query is still in the box as the last document closes.
        Assert.Empty(Order(current, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(5)]
    [InlineData(500)]
    public void a_starting_page_outside_the_document_still_reads_all_of_it(int current)
    {
        // CurrentPageIndex can lag a page deletion by a moment. Clamping keeps
        // the sweep complete; throwing or returning nothing would lose the
        // search over a transient.
        var order = Order(current, 5);

        Assert.Equal(5, order.Length);
        Assert.Equal(Enumerable.Range(0, 5), order.OrderBy(p => p));
    }

    [Fact]
    public void pages_are_yielded_lazily_so_a_cancelled_sweep_stops_early()
    {
        // The sweep reads one page's text per step and can be cancelled at any
        // point. Materialising the whole order up front would be harmless but
        // pointless; what matters is that taking three pages of a 3352-page
        // document costs three, which is what a caller breaking out of the
        // loop relies on.
        var firstThree = SearchSweepOrder.PagesFrom(3000, 3352).Take(3).ToArray();

        Assert.Equal([3000, 3001, 3002], firstThree);
    }
}

/// <summary>
/// The results of a document-wide search, and where the reader is inside them.
///
/// The whole reason this is a type rather than three fields on the view model
/// is that results ARRIVE WHILE IT IS BEING USED. Pages are read from where the
/// reader is, forward, then round to the start, so matches earlier in the
/// document turn up last, after the reader may already be stepping through the
/// ones near them. Every rule below exists to keep that from being visible as
/// anything worse than a number going up.
/// </summary>
public class SearchIndexTests
{
    private static SearchMatch M(int page, int start = 0, int length = 3) =>
        new(page, start, length);

    /// <summary>A finished search over one batch, which is the ordinary case.</summary>
    private static SearchIndex Completed(int startPage, params SearchMatch[] matches)
    {
        var index = new SearchIndex(startPage);
        index.Add(matches);
        index.MarkComplete();
        return index;
    }

    private static int[] Pages(SearchIndex index) => index.Matches.Select(m => m.PageIndex).ToArray();

    // ---------------- Nothing found yet ----------------

    [Fact]
    public void a_search_that_has_not_found_anything_yet_says_nothing_at_all()
    {
        // NOT "No matches": the sweep has barely started and there may be four
        // hundred. The spinner is what says work is happening; the counter
        // stays quiet rather than reporting a result it does not have.
        var index = new SearchIndex(0);

        Assert.Equal(0, index.Total);
        Assert.Equal(0, index.Ordinal);
        Assert.Null(index.Current);
        Assert.False(index.Complete);
        Assert.Equal(string.Empty, index.Status);
    }

    [Fact]
    public void a_finished_search_that_found_nothing_says_so()
    {
        var index = new SearchIndex(0);
        index.MarkComplete();

        Assert.True(index.Complete);
        Assert.Equal("No matches", index.Status);
    }

    [Fact]
    public void navigation_on_an_empty_index_does_nothing_rather_than_throwing()
    {
        var index = new SearchIndex(0);

        Assert.Null(index.Next());
        Assert.Null(index.Previous());
        Assert.Null(index.SelectFirstAtOrAfter(3));
        Assert.Equal(0, index.Ordinal);
    }

    // ---------------- The count ----------------

    [Fact]
    public void the_count_reads_as_position_of_total()
    {
        var index = Completed(0, Enumerable.Range(0, 431).Select(i => M(i)).ToArray());
        for (int i = 0; i < 11; i++) { index.Next(); }

        Assert.Equal("12 of 431", index.Status);
        Assert.Equal(12, index.Ordinal);
        Assert.Equal(431, index.Total);
    }

    [Fact]
    public void the_count_is_shown_while_the_search_is_still_running()
    {
        // The counter has to stay meaningful mid-sweep. Only the spinner says
        // the total is still climbing, which is why the text carries no "+"
        // or ellipsis of its own.
        var index = new SearchIndex(0);
        index.Add([M(1), M(2)]);

        Assert.False(index.Complete);
        Assert.Equal("1 of 2", index.Status);
    }

    // ---------------- Document order from streamed batches ----------------

    [Fact]
    public void batches_arriving_out_of_order_end_up_in_document_order()
    {
        // This IS the arrival pattern: the sweep runs from the reader's page to
        // the end and then wraps, so the last batches belong at the front. Next
        // walking the document backwards for the second half of a search would
        // be the visible symptom.
        var index = new SearchIndex(0);
        index.Add([M(10), M(11)]);
        index.Add([M(0), M(1)]);
        index.Add([M(5)]);

        Assert.Equal([0, 1, 5, 10, 11], Pages(index));
    }

    [Fact]
    public void matches_on_one_page_are_ordered_by_position_on_that_page()
    {
        var index = new SearchIndex(0);
        index.Add([M(4, start: 90), M(4, start: 10), M(4, start: 50)]);

        Assert.Equal([10, 50, 90], index.Matches.Select(m => m.Start).ToArray());
    }

    [Fact]
    public void a_batch_that_belongs_entirely_in_front_merges_in_front()
    {
        var index = new SearchIndex(0);
        index.Add([M(50), M(60)]);
        index.Add([M(1), M(2), M(3)]);

        Assert.Equal([1, 2, 3, 50, 60], Pages(index));
    }

    [Fact]
    public void interleaved_batches_merge_rather_than_append()
    {
        var index = new SearchIndex(0);
        index.Add([M(1), M(4), M(7)]);
        index.Add([M(2), M(5), M(8)]);
        index.Add([M(3), M(6)]);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Pages(index));
    }

    [Fact]
    public void an_empty_batch_changes_nothing()
    {
        var index = new SearchIndex(0);
        index.Add([M(1)]);
        index.Add([]);

        Assert.Equal(1, index.Total);
        Assert.Equal(M(1), index.Current);
    }

    // ---------------- Where the first result comes from ----------------

    [Fact]
    public void the_first_result_is_the_one_nearest_where_the_reader_was()
    {
        // The sweep starts at their page, so the first batch already contains
        // the nearest hits; picking the document's first match instead would
        // fling them to page 1 of a book they were reading at page 300.
        var index = new SearchIndex(startPage: 10);
        index.Add([M(10), M(11), M(12)]);

        Assert.Equal(M(10), index.Current);
        Assert.Equal(1, index.Ordinal);
    }

    [Fact]
    public void a_reader_past_every_match_is_taken_to_the_first_one()
    {
        var index = new SearchIndex(startPage: 900);
        index.Add([M(3), M(7)]);

        Assert.Equal(M(3), index.Current);
    }

    [Fact]
    public void select_first_at_or_after_skips_earlier_pages()
    {
        var index = Completed(0, M(1), M(5), M(9));

        Assert.Equal(M(5), index.SelectFirstAtOrAfter(4));
        Assert.Equal(M(5), index.SelectFirstAtOrAfter(5));
        Assert.Equal(M(9), index.SelectFirstAtOrAfter(6));
        Assert.Equal(M(1), index.SelectFirstAtOrAfter(0));
    }

    [Fact]
    public void the_selection_is_only_chosen_automatically_once()
    {
        // Otherwise every batch during a long sweep would yank the reader back
        // to whatever it happened to contain.
        var index = new SearchIndex(startPage: 0);
        index.Add([M(1), M(2)]);
        index.Next();
        Assert.Equal(M(2), index.Current);

        index.Add([M(3), M(4)]);

        Assert.Equal(M(2), index.Current);
    }

    // ---------------- Navigation ----------------

    [Fact]
    public void next_and_previous_step_one_match_at_a_time()
    {
        var index = Completed(0, M(1), M(2), M(3));

        Assert.Equal(M(2), index.Next());
        Assert.Equal(M(3), index.Next());
        Assert.Equal(M(2), index.Previous());
        Assert.Equal(2, index.Ordinal);
    }

    [Fact]
    public void next_wraps_from_the_last_match_to_the_first()
    {
        var index = Completed(0, M(1), M(2));
        index.Next();

        Assert.Equal(M(1), index.Next());
        Assert.Equal(1, index.Ordinal);
    }

    [Fact]
    public void previous_wraps_from_the_first_match_to_the_last()
    {
        var index = Completed(0, M(1), M(2), M(3));

        Assert.Equal(M(3), index.Previous());
        Assert.Equal(3, index.Ordinal);
    }

    [Fact]
    public void a_single_match_stays_put_and_keeps_its_number()
    {
        var index = Completed(0, M(7));

        Assert.Equal(M(7), index.Next());
        Assert.Equal(M(7), index.Previous());
        Assert.Equal("1 of 1", index.Status);
    }

    [Fact]
    public void stepping_all_the_way_round_returns_to_the_start()
    {
        var index = Completed(0, M(1), M(2), M(3), M(4));
        for (int i = 0; i < 4; i++) { index.Next(); }

        Assert.Equal(M(1), index.Current);
        Assert.Equal(1, index.Ordinal);
    }

    // ---------------- Navigating while results are still arriving ----------------

    [Fact]
    public void a_batch_arriving_later_never_moves_the_reader_to_a_different_match()
    {
        // The one that matters. The reader is looking at a match; pages before
        // it are still being read. What they are looking at must not change
        // under them.
        var index = new SearchIndex(startPage: 10);
        index.Add([M(10), M(11), M(12)]);
        index.Next();
        Assert.Equal(M(11), index.Current);

        index.Add([M(0), M(1)]);

        Assert.Equal(M(11), index.Current);
    }

    [Fact]
    public void an_earlier_batch_renumbers_the_selection_honestly()
    {
        // Its POSITION does change, and should: it really is the fourth of five
        // now. Freezing the ordinal to avoid the jump would mean the counter
        // stops describing the document. Selection is held by identity, and the
        // number is derived from the list.
        var index = new SearchIndex(startPage: 10);
        index.Add([M(10), M(11), M(12)]);
        index.Next();
        Assert.Equal("2 of 3", index.Status);

        index.Add([M(0), M(1)]);

        Assert.Equal("4 of 5", index.Status);
    }

    [Fact]
    public void navigation_after_a_late_batch_steps_through_the_merged_order()
    {
        var index = new SearchIndex(startPage: 10);
        index.Add([M(10), M(11)]);
        index.Add([M(0), M(1)]);

        // Sitting on M(10), which is third of four. Previous is M(1), not M(11).
        Assert.Equal(M(10), index.Current);
        Assert.Equal(M(1), index.Previous());
    }

    // ---------------- Feeding the highlight layer ----------------

    [Fact]
    public void the_matches_on_one_page_can_be_asked_for_on_their_own()
    {
        // Rectangles are built for the current match's page and no other, so
        // this is the only slice of the index the UI ever draws.
        var index = Completed(0, M(1, 10), M(4, 20), M(4, 60), M(9, 30));

        Assert.Equal([20, 60], index.OnPage(4).Select(m => m.Start).ToArray());
        Assert.Empty(index.OnPage(2));
    }
}

/// <summary>
/// Turning one page's matches into something to draw.
///
/// The count in the find bar says WHICH of 431 you are on; this is what says
/// WHERE. Without the distinction, a page with six hits highlights all six
/// identically and Next appears to do nothing at all.
/// </summary>
public class SearchHighlightTests
{
    /// <summary>Two lines of five characters, "AB CD" over "ab cd", each glyph
    /// ten wide and twenty tall. Line two starts at Top 30.</summary>
    private static PageTextLayer TwoLines()
    {
        var chars = new List<CharGlyph>();
        for (int i = 0; i < 5; i++) { chars.Add(new CharGlyph(i * 10, 0, (i * 10) + 10, 20, "AB CD"[i])); }
        for (int i = 0; i < 5; i++) { chars.Add(new CharGlyph(i * 10, 30, (i * 10) + 10, 50, "ab cd"[i])); }
        return new PageTextLayer(chars);
    }

    private static SearchMatch M(int page, int start, int length = 2) => new(page, start, length);

    [Fact]
    public void every_match_on_the_page_is_drawn()
    {
        var rects = SearchHighlight.RectsFor(TwoLines(), [M(0, 0), M(0, 3), M(0, 8)], null);

        Assert.Equal(3, rects.Count);
    }

    [Fact]
    public void the_selected_match_is_coloured_differently_from_the_rest()
    {
        var selected = M(0, 3);
        var rects = SearchHighlight.RectsFor(TwoLines(), [M(0, 0), selected, M(0, 8)], selected);

        Assert.Equal(SearchHighlight.MatchHex, rects[0].ColorHex);
        Assert.Equal(SearchHighlight.SelectedHex, rects[1].ColorHex);
        Assert.Equal(SearchHighlight.MatchHex, rects[2].ColorHex);
    }

    [Fact]
    public void the_two_colours_are_not_the_same()
    {
        // The entire point of the layer. A refactor that collapsed them would
        // leave everything else here passing.
        Assert.NotEqual(SearchHighlight.MatchHex, SearchHighlight.SelectedHex);
    }

    [Fact]
    public void with_nothing_selected_every_match_is_an_ordinary_one()
    {
        var rects = SearchHighlight.RectsFor(TwoLines(), [M(0, 0), M(0, 3)], null);

        Assert.All(rects, r => Assert.Equal(SearchHighlight.MatchHex, r.ColorHex));
    }

    [Fact]
    public void a_selection_on_another_page_colours_nothing_here()
    {
        // Reached whenever the reader's match is elsewhere. Matching on page
        // position alone, rather than on the whole match, would light up an
        // unrelated hit at the same offset on this page.
        var rects = SearchHighlight.RectsFor(TwoLines(), [M(4, 0), M(4, 3)], new SearchMatch(9, 0, 2));

        Assert.All(rects, r => Assert.Equal(SearchHighlight.MatchHex, r.ColorHex));
    }

    [Fact]
    public void a_match_that_wraps_across_a_line_is_drawn_in_one_piece()
    {
        // GetRangeRects splits it by line; both halves have to carry the same
        // colour or the selected match comes out half-lit.
        var selected = M(0, 3, length: 5);   // "CD" + "ab " across the break
        var rects = SearchHighlight.RectsFor(TwoLines(), [selected], selected);

        Assert.Equal(2, rects.Count);
        Assert.All(rects, r => Assert.Equal(SearchHighlight.SelectedHex, r.ColorHex));
    }

    [Fact]
    public void rectangles_come_back_in_the_order_the_matches_were_given()
    {
        var rects = SearchHighlight.RectsFor(TwoLines(), [M(0, 0), M(0, 3)], null);

        Assert.Equal(0, rects[0].Rect.Left);
        Assert.Equal(30, rects[1].Rect.Left);
    }

    [Fact]
    public void a_page_with_no_text_layer_draws_nothing()
    {
        // An image-only page extracts to null, and a search can still be
        // selected on a neighbouring page.
        Assert.Empty(SearchHighlight.RectsFor(null, [M(0, 0)], null));
    }

    [Fact]
    public void no_matches_means_no_rectangles()
    {
        Assert.Empty(SearchHighlight.RectsFor(TwoLines(), [], null));
    }
}

/// <summary>
/// Whether a jump to a match should scroll, and where to.
///
/// Navigating by page put the top of the page on screen and left the reader to
/// find the hit. Navigating by match has to actually show it. The interesting
/// half is the other one: stepping between two matches that are BOTH already on
/// screen must not move the page at all, or every press of Enter jolts a view
/// that was already showing the answer.
///
/// All coordinates are slot-space DIPs, the space the text layer is extracted
/// in, measured down the whole page stack.
/// </summary>
public class MatchRevealTests
{
    private const double ViewHeight = 1000;

    [Fact]
    public void a_match_already_on_screen_does_not_move_the_page()
    {
        // Viewport covers 0..1000; the match sits in the middle of it.
        Assert.Null(MatchReveal.OffsetFor(400, 420, viewTop: 0, viewportHeight: ViewHeight));
    }

    [Fact]
    public void a_match_below_the_viewport_is_brought_in()
    {
        Assert.NotNull(MatchReveal.OffsetFor(1500, 1520, viewTop: 0, viewportHeight: ViewHeight));
    }

    [Fact]
    public void a_match_above_the_viewport_is_brought_in()
    {
        Assert.NotNull(MatchReveal.OffsetFor(200, 220, viewTop: 1000, viewportHeight: ViewHeight));
    }

    [Theory]
    [InlineData(0, 20)]        // flush against the top edge
    [InlineData(985, 1005)]    // straddling the bottom edge
    [InlineData(990, 1010)]    // mostly below the bottom edge
    public void a_match_crowding_an_edge_counts_as_not_visible(double top, double bottom)
    {
        // "Visible" is not "one pixel of it is on screen". A hit touching the
        // very edge of the window reads as cut off, and the reader cannot see
        // the line it belongs to.
        Assert.NotNull(MatchReveal.OffsetFor(top, bottom, viewTop: 0, viewportHeight: ViewHeight));
    }

    [Fact]
    public void a_match_just_inside_the_margin_is_left_alone()
    {
        // The boundary itself. One DIP further in and it is comfortable.
        double top = MatchReveal.Margin;
        double bottom = ViewHeight - MatchReveal.Margin;

        Assert.Null(MatchReveal.OffsetFor(top, bottom, viewTop: 0, viewportHeight: ViewHeight));
    }

    [Fact]
    public void a_revealed_match_lands_a_quarter_of_the_way_down()
    {
        // Not flush against the top: a hit at the very top edge has no context
        // above it, and the line it is on reads as the first line of the page
        // rather than as a place in a paragraph. It is also a FIXED place, so
        // repeated jumps put the answer where the eye already is.
        double? offset = MatchReveal.OffsetFor(5000, 5020, viewTop: 0, viewportHeight: ViewHeight);

        Assert.Equal(5000 - (ViewHeight * MatchReveal.RevealFraction), offset!.Value, 6);
    }

    [Fact]
    public void a_match_near_the_top_of_the_document_never_scrolls_past_the_start()
    {
        // Would otherwise ask the scroller for a negative offset.
        double? offset = MatchReveal.OffsetFor(10, 30, viewTop: 900, viewportHeight: ViewHeight);

        Assert.NotNull(offset);
        Assert.True(offset >= 0, $"offset {offset} is before the start of the document");
    }

    [Fact]
    public void a_match_taller_than_the_viewport_is_shown_from_its_top()
    {
        // A long query wrapping over many lines cannot be framed comfortably,
        // so the top of it wins: that is where reading starts.
        double? offset = MatchReveal.OffsetFor(2000, 4000, viewTop: 0, viewportHeight: ViewHeight);

        Assert.NotNull(offset);
        Assert.True(offset <= 2000, "the top of the match is off screen above");
    }

    [Fact]
    public void the_reveal_position_leaves_room_above_the_match()
    {
        // Guards the fraction being turned into zero, which would silently make
        // every jump flush to the top edge again.
        Assert.True(MatchReveal.RevealFraction > 0 && MatchReveal.RevealFraction < 0.5);
    }
}
