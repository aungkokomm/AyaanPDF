using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The threading and staleness rules of the background search, pinned by
/// reading the view model's source.
///
/// Not the way anyone would choose to test this. The rules live in
/// ViewportViewModel, which is in the WinUI project and cannot be loaded by
/// this assembly, and each of them is invisible until it fails in a way that is
/// very hard to attribute: a dictionary corrupted by two threads, or a set of
/// results pointing into a document that was closed. The repository already
/// takes this approach for the same reason in <c>AppWiringTests</c> and
/// <c>FloatingChromeLayeringTests</c>.
///
/// Each assertion below is a rule that would otherwise rot silently.
/// </summary>
public class SearchWiringTests
{
    private static string ViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string relative = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>Up to <paramref name="length"/> characters from
    /// <paramref name="start"/>, clamped to the end of the file. The last method
    /// in the file has less source after it than the window asks for.</summary>
    private static string Section(string source, int start, int length) =>
        source[start..Math.Min(source.Length, start + length)];

    /// <summary>
    /// A whole method, bounded by where the next one starts.
    ///
    /// Section takes a character count, which is a guess about how long a
    /// method happens to be today. That guess expired: adding a dozen lines to
    /// ApplySettings pushed the assertion below past the window and failed a
    /// test about find options, for a change that had nothing to do with them.
    /// A window measured in characters fails for the wrong reason eventually.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    /// <summary>The body of StartSearchSweep, which is where the background
    /// work is declared.</summary>
    private static string SweepBody()
    {
        string source = ViewModelSource();
        int start = source.IndexOf("private void StartSearchSweep()", StringComparison.Ordinal);
        Assert.True(start >= 0, "StartSearchSweep is gone; this test needs rewriting to match");

        int end = source.IndexOf("private void PublishSearchBatch", start, StringComparison.Ordinal);
        Assert.True(end > start, "PublishSearchBatch no longer follows StartSearchSweep");

        return source[start..end];
    }

    // ---------------- Thread safety ----------------

    [Fact]
    public void the_sweep_never_touches_the_ui_threads_text_layer_cache()
    {
        // _textLayers is a plain Dictionary owned by the UI thread. A background
        // sweep reading or writing it is a data race that shows up as a
        // corrupted dictionary or an infinite loop inside a lookup, months
        // later, on somebody else's machine.
        Assert.DoesNotContain("_textLayers", SweepBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_sweep_loads_text_layers_directly_rather_than_through_the_cache()
    {
        // TextLayerFor is the cached accessor and writes _textLayers, so using
        // it here would be the same race by another name.
        string body = SweepBody();

        Assert.Contains("TextLayerLoader.Load(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TextLayerFor(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_sweep_checks_for_cancellation_on_every_page()
    {
        // Reading a page parses it. Without a check inside the loop, cancelling
        // a search on a 3352-page book would go on parsing to the end.
        Assert.Contains("token.IsCancellationRequested", SweepBody(), StringComparison.Ordinal);
    }

    // ---------------- Staleness ----------------

    [Fact]
    public void a_batch_arriving_from_a_superseded_search_is_dropped()
    {
        // The token stops the sweep but cannot recall a batch already on its way
        // to the UI thread, and that batch carries page indices a page delete or
        // a document switch may have invalidated. The generation check is the
        // only thing standing between that and results pointing at the wrong
        // pages of the wrong document.
        string source = ViewModelSource();
        int publish = source.IndexOf("private void PublishSearchBatch", StringComparison.Ordinal);
        Assert.True(publish >= 0);

        string body = Section(source, publish, 1200);

        Assert.Contains("generation != _searchGeneration", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CloseCurrentDocument")]
    [InlineData("ReloadAfterPageStructureChange")]
    public void the_document_lifecycle_stops_a_running_search(string method)
    {
        // Both invalidate the page indices a sweep is producing.
        string source = ViewModelSource();
        int start = source.IndexOf($"private void {method}()", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} is gone; this test needs rewriting to match");

        string body = Section(source, start, 900);

        Assert.True(
            body.Contains("CancelSearch()", StringComparison.Ordinal)
            || body.Contains("RestartSearch()", StringComparison.Ordinal),
            $"{method} leaves a search running over page indices it just invalidated");
    }

    // ---------------- Highlighting one page ----------------

    [Fact]
    public void moving_the_selection_touches_two_slots_at_most()
    {
        // The page being left and the page being entered. Walking PageSlots
        // would be 3352 collection touches per press of Enter on the book this
        // feature exists for, and every one of them raises a change
        // notification into the UI.
        string source = ViewModelSource();
        int start = source.IndexOf("private void RefreshSearchHighlights()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RefreshSearchHighlights is gone; this test needs rewriting to match");

        string body = Section(source, start, 1500);

        Assert.Contains("_highlightedSearchPage", body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var slot in PageSlots)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void distributing_annotations_no_longer_wipes_the_search_highlight()
    {
        // It clears every slot's overlay collections and runs on every page
        // change while scrolling. That was harmless while the search was
        // recomputed in the same breath, and became a bug the moment it was not:
        // scrolling wiped the highlight and nothing put it back.
        string source = ViewModelSource();
        int start = source.IndexOf("private void DistributeAnnotationsToSlots()", StringComparison.Ordinal);
        Assert.True(start >= 0);

        string body = Section(source, start, 700);

        Assert.DoesNotContain("slot.SearchMatchRects.Clear()", body, StringComparison.Ordinal);
    }

    // ---------------- Scrolling to a match ----------------

    private static string MainPageSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string relative = Path.Combine("PdfEditorApp", "MainPage.xaml.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Fact]
    public void scrolling_to_a_match_asks_whether_it_needs_to_scroll_at_all()
    {
        // The decision lives in MatchReveal so it can be tested; the view's job
        // is the zoom conversion and honouring a "no" by not scrolling. A view
        // that ignored the null would jolt the page on every Enter even when
        // the next match was already on screen.
        string source = MainPageSource();
        int start = source.IndexOf("private void ScrollToPage(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ScrollToPage is gone; this test needs rewriting to match");

        string body = Section(source, start, 2200);

        Assert.Contains("MatchReveal.OffsetFor", body, StringComparison.Ordinal);
        Assert.Contains("is null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void navigation_that_is_not_a_search_still_goes_to_the_page_top()
    {
        // Bookmarks, the page-jump box and thumbnail clicks mean the PAGE. Only
        // search carries a band, and the parameter defaults to none so those
        // paths keep the behaviour they have always had.
        string source = ViewModelSource();

        Assert.Contains(
            "public void GoToPage(int pageIndex, bool animate = true, PageBand? reveal = null)",
            source,
            StringComparison.Ordinal);
    }

    // ---------------- The find bar ----------------

    private static string MainPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string relative = Path.Combine("PdfEditorApp", "MainPage.xaml");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Theory]
    [InlineData("MatchCaseToggle", "SearchMatchCase")]
    [InlineData("WholeWordToggle", "SearchWholeWord")]
    public void each_find_option_is_bound_two_way_to_the_view_model(string toggle, string property)
    {
        // OneWay would leave the menu showing a state the search does not have.
        string xaml = MainPageXaml();
        int at = xaml.IndexOf($"x:Name=\"{toggle}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{toggle} is missing from the find bar");

        string block = Section(xaml, at, 400);

        Assert.Contains($"ViewModel.{property}, Mode=TwoWay", block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SearchMatchCase")]
    [InlineData("SearchWholeWord")]
    public void changing_a_find_option_restarts_the_search(string property)
    {
        // Turning Match case on with a query already in the box has to re-run
        // it. Without this the option appears to do nothing until the next
        // keystroke, which reads as the option being broken.
        string source = ViewModelSource();
        int at = source.IndexOf($"partial void On{property}Changed(", StringComparison.Ordinal);
        Assert.True(at >= 0, $"nothing reacts to {property} changing");

        // Bounded at the NEXT handler. These sit one after another, and a
        // window that runs past the end of this one finds the neighbour's
        // RestartSearch and reports a handler that does nothing as wired up.
        string rest = Section(source, at, 400);
        int next = rest.IndexOf("partial void On", 10, StringComparison.Ordinal);
        string body = next > 0 ? rest[..next] : rest;

        Assert.Contains("RestartSearch()", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SearchMatchCase")]
    [InlineData("SearchWholeWord")]
    public void each_find_option_is_both_stored_and_restored(string property)
    {
        // Two halves, and one without the other is worse than neither: saving
        // without restoring looks like the setting is ignored, restoring
        // without saving means it is always off.
        string source = MainPageSource();

        int save = source.IndexOf("private void SearchOption_Click", StringComparison.Ordinal);
        Assert.True(save >= 0, "nothing stores the find options");
        Assert.Contains(property, Section(source, save, 800), StringComparison.Ordinal);

        Assert.Contains(
            $"ViewModel.{property} = s.{property}",
            MethodBody(source, "private void ApplySettings()"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_spinner_follows_the_sweep_and_nothing_else()
    {
        // IsActive bound to IsSearching, and IsSearching false both when the
        // sweep completes (Complete) and when it is cancelled (no index at all).
        // Binding Visibility instead would shuffle every control to its right
        // twice per search.
        Assert.Contains(
            "IsActive=\"{x:Bind ViewModel.IsSearching, Mode=OneWay}\"",
            MainPageXaml(),
            StringComparison.Ordinal);

        Assert.Contains(
            "public bool IsSearching => _searchIndex is { Complete: false };",
            ViewModelSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_counter_is_wide_enough_for_a_long_book()
    {
        // "12 of 431" is the ordinary case and "12345 of 67890" the extreme.
        // Too narrow and the buttons beside it shuffle sideways every time the
        // total climbs, which during a sweep is constantly.
        string xaml = MainPageXaml();
        int at = xaml.IndexOf("ViewModel.SearchStatus", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string block = Section(xaml, at, 400);
        var width = System.Text.RegularExpressions.Regex.Match(block, @"MinWidth=""(\d+)""");

        Assert.True(width.Success, "the search counter has no MinWidth, so it will resize as it counts");
        Assert.True(
            int.Parse(width.Groups[1].Value) >= 80,
            $"MinWidth {width.Groups[1].Value} is too narrow for a five-digit count");
    }

    // ---------------- The old windowed search is gone ----------------

    [Theory]
    [InlineData("SearchPageBudget", "the forty-page window")]
    [InlineData("RecomputeSearchMatches", "the synchronous recompute")]
    [InlineData("_matchPages", "page-level match tracking")]
    [InlineData("EnsureTextLayer", "the dead current-page refresh")]
    public void the_windowed_search_machinery_is_fully_removed(string symbol, string what)
    {
        // Left behind, any of these would be a second search implementation
        // sitting beside the real one, which is how the app came to have two
        // sources of truth for a selection twice before.
        Assert.DoesNotContain(symbol, ViewModelSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void scrolling_no_longer_recomputes_the_search()
    {
        // This ran on every page boundary crossed, re-extracting and re-scanning
        // up to forty text layers on the UI thread. It existed only to re-centre
        // a window that no longer exists.
        string source = ViewModelSource();
        int start = source.IndexOf("private void OnCurrentPageChangedByScroll()", StringComparison.Ordinal);
        Assert.True(start >= 0);

        string body = Section(source, start, 400);

        Assert.DoesNotContain("Search", body, StringComparison.Ordinal);
    }
}
