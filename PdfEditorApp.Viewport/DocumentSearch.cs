using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// How a search compares text.
///
/// Both default to off, which is what search has always done and what a reader
/// wants nine times in ten. They exist because the tenth time there was no way
/// to say otherwise: looking for a name, or for "IT" in a technical document,
/// returned every "it" on every page.
/// </summary>
public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false)
{
    /// <summary>
    /// ORDINAL either way, never culture-aware.
    ///
    /// A culture-sensitive comparison ties what a search finds to the machine's
    /// locale: under a Turkish culture "I" stops matching "i", on one user's
    /// machine only, and nobody else can reproduce it. Ordinal is also what the
    /// insensitive path has always used, so turning the option off leaves
    /// existing behaviour exactly where it was.
    /// </summary>
    public StringComparison Comparison =>
        MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// One occurrence of the query: which page, and where in that page's text.
///
/// Deliberately NOT a rectangle. A book can hold thousands of these, and they
/// are built while the reader is looking at the document, so a match has to be
/// cheap enough to keep every one of them. Rectangles are derived from the
/// page's own text layer, for one page at a time, only when something is
/// actually drawn.
/// </summary>
public readonly record struct SearchMatch(int PageIndex, int Start, int Length);

/// <summary>
/// The order a document-wide search reads its pages in: from where the user is,
/// forward to the end, then round to the pages before it.
///
/// Reading every page of a long book takes real time, and results are published
/// as they are found, so the order decides which hits appear first. Plain
/// document order would make someone at page 3000 of 3352 wait for 3000 pages
/// of scanning before seeing a hit anywhere near them, and Next goes forward,
/// so forward from here is also the direction they are about to travel.
///
/// Yielded lazily. The sweep reads one page's text per step and can be
/// cancelled between any two, so a cancelled search costs the pages it actually
/// read rather than the whole document.
/// </summary>
public static class SearchSweepOrder
{
    /// <summary>
    /// Every page index exactly once, starting at <paramref name="currentPage"/>.
    ///
    /// A start outside the document is clamped rather than rejected:
    /// CurrentPageIndex can lag a page deletion by a moment, and losing the
    /// whole search over a transient would be a worse answer than starting a
    /// page early.
    /// </summary>
    public static IEnumerable<int> PagesFrom(int currentPage, int pageCount)
    {
        if (pageCount <= 0)
        {
            yield break;
        }

        int start = Math.Clamp(currentPage, 0, pageCount - 1);
        for (int i = 0; i < pageCount; i++)
        {
            yield return (start + i) % pageCount;
        }
    }
}

/// <summary>
/// The vertical band a match occupies on its own page, in slot-space DIPs
/// measured from the page's top edge. Handed to the view so a jump can show the
/// match rather than the top of the page it happens to be on.
/// </summary>
public readonly record struct PageBand(double Top, double Bottom);

/// <summary>
/// Whether jumping to a match should scroll at all, and where to.
///
/// Stepping by page used to put the top of the page on screen and leave the
/// reader to find the hit somewhere below. Stepping by match has to show the
/// match. The other half matters just as much: two matches on the same screen
/// must not move the page between them, or every press of Enter jolts a view
/// that was already showing the answer.
///
/// Everything here is slot-space DIPs measured down the whole page stack, which
/// is the space the text layers are extracted in. Turning that into a scroll
/// offset is the view's job, since only the view knows the zoom.
/// </summary>
public static class MatchReveal
{
    /// <summary>Clear air a match needs above and below it to count as
    /// comfortably visible. A hit flush against the window edge reads as cut
    /// off and carries none of the line it belongs to.</summary>
    public const double Margin = 24;

    /// <summary>Where a revealed match is placed, as a fraction of the viewport
    /// height below its top edge.</summary>
    public const double RevealFraction = 0.25;

    /// <summary>
    /// The scroll position that reveals a match, or null when it is already
    /// comfortably on screen and the page should not move.
    /// </summary>
    public static double? OffsetFor(
        double matchTop, double matchBottom, double viewTop, double viewportHeight)
    {
        double viewBottom = viewTop + viewportHeight;

        if (matchTop >= viewTop + Margin && matchBottom <= viewBottom - Margin)
        {
            return null;
        }

        // Clamped at zero: a match in the first lines of the document would
        // otherwise ask the scroller for a position before the start of it.
        return Math.Max(0, matchTop - (viewportHeight * RevealFraction));
    }
}

/// <summary>
/// Turns the matches on ONE page into rectangles to draw, with the selected one
/// marked out from the rest.
///
/// One page, deliberately. A search over a book can find thousands of matches,
/// and every rectangle is a laid-out element in a panel; drawing them all would
/// cost thousands of elements to show what the reader cannot see anyway. The
/// page holding the selected match is the only one that is ever drawn, so this
/// takes the matches for that page and nothing else.
/// </summary>
public static class SearchHighlight
{
    /// <summary>The other matches on the page: the amber search has always
    /// used.</summary>
    public const string MatchHex = "#66FFA500";

    /// <summary>The one the reader is on. Deeper and much more opaque, so
    /// "which of the 431" is answered on the page and not only in the
    /// counter.</summary>
    public const string SelectedHex = "#CCFF6A00";

    /// <summary>
    /// A rectangle per visual line of every match, paired with its colour.
    ///
    /// One match can produce several rectangles: a hit that wraps across a line
    /// break is drawn as one rectangle per line, which is what
    /// <see cref="PageTextLayer.GetRangeRects"/> already works out. Every piece
    /// of one match carries that match's colour, so a wrapped selected match is
    /// highlighted as a whole rather than half-selected.
    ///
    /// Coordinates come straight from the layer, so they are in whatever space
    /// the layer was extracted in and the caller normalizes them.
    /// </summary>
    public static IReadOnlyList<(TextRect Rect, string ColorHex)> RectsFor(
        PageTextLayer? layer, IEnumerable<SearchMatch> matches, SearchMatch? selected)
    {
        var rects = new List<(TextRect, string)>();
        if (layer is null)
        {
            return rects;
        }

        foreach (var match in matches)
        {
            string hex = selected == match ? SelectedHex : MatchHex;
            foreach (var rect in layer.GetRangeRects(match.Start, match.Length))
            {
                rects.Add((rect, hex));
            }
        }

        return rects;
    }
}

/// <summary>
/// Every match in the document, in document order, and which one the reader is
/// looking at.
///
/// This is a type rather than a few fields on the view model because results
/// ARRIVE WHILE IT IS BEING USED. The sweep reads from the reader's page
/// forward and then wraps, so matches earlier in the document turn up last,
/// after they may already be stepping through the ones near them. Two rules
/// follow from that and everything else here is detail:
///
/// 1. Incoming batches are MERGED into document order, never appended. Next has
///    to walk the document forwards for the whole search, not forwards until
///    the sweep wrapped and then backwards.
/// 2. The selection is held as the MATCH ITSELF, never as a position in the
///    list. A batch of earlier matches shifts every position after it, so an
///    index would quietly start pointing at somebody else's hit.
/// </summary>
public sealed class SearchIndex
{
    private List<SearchMatch> _matches = [];

    /// <summary>
    /// The match the reader is on, held by value. Null only while nothing has
    /// been found: <see cref="Add"/> chooses one as soon as there is anything
    /// to choose, so any non-empty index has a selection.
    /// </summary>
    private SearchMatch? _current;

    private readonly int _startPage;

    /// <param name="startPage">
    /// Where the reader was when the search began. The first result is the
    /// first match at or after it, because the alternative is flinging someone
    /// reading page 300 back to page 1 the moment they type.
    /// </param>
    public SearchIndex(int startPage) => _startPage = startPage;

    /// <summary>Every match, in document order.</summary>
    public IReadOnlyList<SearchMatch> Matches => _matches;

    public int Total => _matches.Count;

    /// <summary>True once the sweep has read every page. Until then the total
    /// is still climbing, which is what the spinner beside the counter says.</summary>
    public bool Complete { get; private set; }

    public SearchMatch? Current => _current;

    /// <summary>The selection's 1-based place in the document, or 0 when there
    /// is no selection. Derived, never stored, so a late batch renumbers it
    /// rather than leaving it describing the wrong match.</summary>
    public int Ordinal => _current is { } c ? _matches.IndexOf(c) + 1 : 0;

    /// <summary>
    /// What the find bar shows: "12 of 431".
    ///
    /// Empty while a running search has found nothing yet, because "No matches"
    /// would be a claim the sweep has not earned. Once it finishes, the same
    /// state is a real answer and says so. The text carries no marker of its
    /// own for a search still in progress; the spinner does that, which keeps
    /// the count readable rather than "12 of 431+".
    /// </summary>
    public string Status =>
        _matches.Count > 0 ? $"{Ordinal} of {Total}"
        : Complete ? "No matches"
        : string.Empty;

    /// <summary>Every match on one page, which is the only slice the highlight
    /// layer ever draws.</summary>
    public IEnumerable<SearchMatch> OnPage(int pageIndex) =>
        _matches.Where(m => m.PageIndex == pageIndex);

    public void MarkComplete() => Complete = true;

    /// <summary>
    /// Merges a batch of matches into document order.
    ///
    /// A MERGE, not an append-and-sort. The batch is sorted first, which costs
    /// nothing at a batch's size, and the two ordered runs are then walked
    /// together in one pass. Re-sorting the whole list once per batch would be
    /// n log n against a list that grows all sweep, for an answer the merge
    /// gets in n.
    /// </summary>
    public void Add(IEnumerable<SearchMatch> batch)
    {
        var incoming = batch.ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        incoming.Sort(Compare);

        var merged = new List<SearchMatch>(_matches.Count + incoming.Count);
        int a = 0, b = 0;
        while (a < _matches.Count && b < incoming.Count)
        {
            merged.Add(Compare(_matches[a], incoming[b]) <= 0 ? _matches[a++] : incoming[b++]);
        }
        while (a < _matches.Count) { merged.Add(_matches[a++]); }
        while (b < incoming.Count) { merged.Add(incoming[b++]); }
        _matches = merged;

        // Only the first time. Doing it per batch would yank the reader back to
        // whatever the newest batch happened to contain, all sweep long.
        if (_current is null)
        {
            SelectFirstAtOrAfter(_startPage);
        }
    }

    /// <summary>
    /// Selects the first match at or after a page, falling back to the first
    /// match in the document when every match is behind it. Returns the
    /// selection, or null when there is nothing to select.
    /// </summary>
    public SearchMatch? SelectFirstAtOrAfter(int pageIndex)
    {
        if (_matches.Count == 0)
        {
            return null;
        }

        int i = _matches.FindIndex(m => m.PageIndex >= pageIndex);
        _current = i >= 0 ? _matches[i] : _matches[0];
        return _current;
    }

    /// <summary>The next match, wrapping to the first. Null when there are none.</summary>
    public SearchMatch? Next() => Step(1);

    /// <summary>The previous match, wrapping to the last. Null when there are none.</summary>
    public SearchMatch? Previous() => Step(-1);

    private SearchMatch? Step(int direction)
    {
        if (_matches.Count == 0)
        {
            return null;
        }

        // A non-empty index always has a selection, because Add makes one, and
        // the selection always came out of this list, so the lookup lands.
        int index = _matches.IndexOf(_current!.Value);
        int next = ((index + direction) % _matches.Count + _matches.Count) % _matches.Count;

        _current = _matches[next];
        return _current;
    }

    /// <summary>Document order: page, then position on the page, then length so
    /// the order is total and a merge is deterministic.</summary>
    private static int Compare(SearchMatch x, SearchMatch y)
    {
        int byPage = x.PageIndex.CompareTo(y.PageIndex);
        if (byPage != 0) { return byPage; }

        int byStart = x.Start.CompareTo(y.Start);
        return byStart != 0 ? byStart : x.Length.CompareTo(y.Length);
    }
}
