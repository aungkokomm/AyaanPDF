using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The dialog that makes bookmarks out of what the headings LOOK like.
///
/// The matching itself is covered by StyleBookmarkerTests and the survey by
/// StyleSurveyTests; this covers the view wiring, which this assembly cannot
/// load. What is most worth guarding is that the document is scanned ONCE and
/// that nothing is written before the reader has seen what it would write.
/// </summary>
public class StyleBookmarkWiringTests
{
    private static string Read(params string[] relative)
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

    // The builder is its own WINDOW. A ContentDialog will not grow past its
    // ContentDialogMaxWidth of 548, so the two-column layout was clipped: the
    // preview, half the options and the closing note fell off the right-hand
    // edge, and no amount of sizing the content brought them back.
    private static string Xaml() => Read("PdfEditorApp", "BookmarkWindow.xaml");
    private static string Code() => Read("PdfEditorApp", "BookmarkWindow.xaml.cs");
    private static string PageXaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

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

    [Theory]
    [InlineData("StyleScan_Click")]
    [InlineData("StyleFromSelection_Click")]
    [InlineData("StyleChoice_Changed")]
    [InlineData("StyleLevel_Changed")]
    [InlineData("StyleOption_Changed")]
    [InlineData("StyleFilterText_Changed")]
    public void every_control_is_wired_to_something(string handler)
    {
        Assert.Contains($"\"{handler}\"", Xaml(), StringComparison.Ordinal);
        Assert.Contains(handler, Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void one_button_opens_one_place_holding_both_ways_of_asking()
    {
        // They were two dialogs behind two buttons that looked alike and
        // answered the same question, so whichever you opened you had to close
        // it again to try the other. HOW to describe a heading is a choice
        // inside the window now, not a choice of which button to press.
        string page = PageXaml();

        Assert.Contains("Click=\"StyleBookmarks_Click\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"AutoBookmarks_Click\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoBookmarkDialog", page, StringComparison.Ordinal);

        // Both tabs, and both engines' controls, in the one window.
        string xaml = Xaml();
        Assert.Contains("x:Name=\"LookTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WordingTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StyleList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutoBookmarkPattern\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void it_is_a_window_and_not_a_dialog()
    {
        // ⚠️ The reason it moved. A ContentDialog is capped by its own
        // ContentDialogMaxWidth theme resource, 548 by default, and content
        // wider than that is CLIPPED rather than scrolled or scaled. That is
        // not fixable by sizing the content, which is what the first attempt
        // tried; the preview column simply was not on screen.
        string xaml = Xaml();

        Assert.Contains("<Window", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ContentDialog", xaml, StringComparison.Ordinal);
        Assert.Contains("class BookmarkWindow : Window", Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void only_one_builder_is_open_at_a_time()
    {
        // It is modeless, so nothing stops the button being pressed again. Two
        // windows would each hold a whole book's runs and each write a
        // different outline over the other.
        string body = MethodBody(PageCode(), "private async void StyleBookmarks_Click");

        Assert.Contains("_bookmarkWindow is not null", body, StringComparison.Ordinal);
        Assert.Contains("Activate()", body, StringComparison.Ordinal);
        Assert.Contains("_bookmarkWindow = null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void being_modeless_is_used_rather_than_merely_tolerated()
    {
        // The document stays reachable behind the window, so a style can be
        // picked by selecting a heading on the page WHILE it is open. In the
        // dialog the selection had to be made beforehand and remembered, and
        // the button was hidden when there was none.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"StyleFromSelectionButton\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the pick-from-selection button is gone");

        Assert.DoesNotContain("Visibility=\"Collapsed\"", xaml[at..(at + 200)], StringComparison.Ordinal);

        // And an empty selection now explains itself instead of the button
        // simply not being there.
        Assert.Contains("Nothing is selected",
                        MethodBody(Code(), "private async void StyleFromSelection_Click"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void one_preview_serves_both_tabs()
    {
        // The point of putting them together. The question is always "which
        // bookmarks would this make", so the answer belongs in the same place
        // whichever way it was asked, and there is only one of it.
        string xaml = Xaml();

        Assert.DoesNotContain("AutoBookmarkPreview", xaml, StringComparison.Ordinal);
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(xaml, @"x:Name=""StylePreview"""));

        // And the shared preview sits outside both panels, so switching tabs
        // cannot take it away.
        int preview = xaml.IndexOf("x:Name=\"StylePreview\"", StringComparison.Ordinal);
        int wordingPanelEnd = xaml.IndexOf("x:Name=\"WordingPanel\"", StringComparison.Ordinal);
        Assert.True(preview > wordingPanelEnd, "the preview is inside one of the tab panels");
    }

    [Fact]
    public void each_tab_keeps_its_own_findings()
    {
        // Being able to try both and take whichever gave the better outline is
        // most of the reason for one dialog. A tab switch that threw the last
        // result away would defeat it.
        string body = MethodBody(Code(), "private void BookmarkTab_Changed");

        Assert.Contains("RefreshStylePreview()", body, StringComparison.Ordinal);
        Assert.Contains("ShowBookmarkPreview(_autoBookmarkFound)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_tab_on_screen_is_the_one_that_gets_written()
    {
        // The preview is the contract: what the reader is looking at when they
        // press the button is what gets written.
        Assert.Contains(
            "IsByWording ? _autoBookmarkFound : _styleFound",
            MethodBody(Code(), "private async void Add_Click"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_inside_is_pinned_to_a_size_that_could_clip()
    {
        // The window resizes, so everything in it has to be free to. A fixed
        // height on either list would leave dead space when the window grew and
        // clip when it shrank, which is the fault this whole move was about.
        string xaml = Xaml();

        foreach (string list in new[] { "x:Name=\"StyleList\"", "x:Name=\"StylePreview\"" })
        {
            int at = xaml.IndexOf(list, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{list} is gone");
            Assert.DoesNotContain("Height=\"", xaml[at..(at + 140)], StringComparison.Ordinal);
        }

        // Both lists sit in a star row so they take the slack.
        Assert.Contains("<RowDefinition Height=\"*\" />", xaml, StringComparison.Ordinal);

        // And the columns have floors, so dragging the window narrow stops
        // rather than squeezing a column to nothing.
        Assert.Contains("MinWidth=\"380\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"240\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void it_opens_at_a_size_that_shows_both_columns()
    {
        // A window that opens too small to show what it is for teaches the
        // reader it is broken before they have resized it once.
        Assert.Contains("AppWindow.Resize(", MethodBody(Code(), "public BookmarkWindow"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void the_document_is_scanned_once_and_both_halves_use_it()
    {
        // The list of styles and the preview of the outline come from the same
        // runs. Scanning per tick would put the only slow part of this behind
        // every checkbox in the dialog.
        Assert.Contains("ScanStyledRunsAsync", MethodBody(Code(), "private async void StyleScan_Click"),
                        StringComparison.Ordinal);

        string refresh = MethodBody(Code(), "private void RefreshStylePreview");
        Assert.DoesNotContain("ScanStyledRunsAsync", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("await", refresh, StringComparison.Ordinal);
        Assert.Contains("StyleBookmarker.Detect(", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void changing_the_page_range_does_not_rescan()
    {
        // It filters runs already in hand. A range that triggered a rescan
        // would make the cheapest-looking control the slowest.
        string body = MethodBody(Code(), "private IEnumerable<StyledRun> InStylePageRange");

        Assert.Contains("r.PageIndex", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan", body, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_can_be_written_before_it_has_been_seen()
    {
        // Writing bookmarks rewrites the file. Whether a style suits a document
        // is not knowable in advance, so the answer has to be on screen before
        // anything is committed. The same reasoning as the pattern dialog's
        // preview, with more force: a style can match thousands of runs.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"AddButton\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        // Clamped: the button is the last thing in the file.
        Assert.Contains("IsEnabled=\"False\"",
                        xaml[at..Math.Min(xaml.Length, at + 300)], StringComparison.Ordinal);

        // And it is only ever enabled off a non-empty result, on either tab,
        // through the one method that fills the shared preview.
        Assert.Contains(
            "AddButton.IsEnabled = found.Count > 0;",
            MethodBody(Code(), "private void ShowBookmarkPreview"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void an_unsaved_document_is_turned_away_before_the_window_opens()
    {
        // The outline is written by rewriting the file, so there has to be one.
        string body = MethodBody(PageCode(), "private async void StyleBookmarks_Click");

        Assert.Contains("ViewModel.HasDocumentPath", body, StringComparison.Ordinal);
        Assert.Contains("Save first", body, StringComparison.Ordinal);
    }

    [Fact]
    public void closing_the_window_stops_the_scan_and_drops_what_it_read()
    {
        // A scan left running would keep parsing pages nobody is waiting for,
        // and a whole book's runs are not worth holding once it is shut.
        string body = MethodBody(Code(), "public BookmarkWindow");

        Assert.Contains("Closed +=", body, StringComparison.Ordinal);
        Assert.Contains("_styleScan?.Cancel();", body, StringComparison.Ordinal);
        Assert.Contains("_autoBookmarkScan?.Cancel();", body, StringComparison.Ordinal);
        Assert.Contains("_styleRuns = [];", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_level_combo_does_not_rerun_the_match_as_the_list_scrolls()
    {
        // SelectionChanged fires as each row is REALIZED. Without the guard,
        // scrolling the styles would run the whole match again per row that
        // came into view, over every run in the document.
        string body = MethodBody(Code(), "private void StyleLevel_Changed");

        Assert.Contains("IsChosen: true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void typing_a_filter_does_not_match_on_every_keystroke()
    {
        // The match runs over every run in the document. On a long book,
        // undebounced, the box would type a character behind.
        string body = MethodBody(Code(), "private void StyleFilterText_Changed");

        Assert.Contains("_styleFilterTimer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshStylePreview()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_hierarchy_is_the_level_the_reader_gave_each_style()
    {
        // The detector reads the example list as the hierarchy: first entry is
        // level one. So the list handed to it has to be in that order, not in
        // the order the boxes happened to be ticked.
        string body = MethodBody(Code(), "private void RefreshStylePreview");

        Assert.Contains("OrderBy(c => c.LevelIndex)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void picking_a_sample_ticks_the_row_rather_than_adding_a_second_kind_of_entry()
    {
        // Two routes to one place. A separate list of "styles from selection"
        // could hold the same style twice, or be ticked in one view and not the
        // other.
        string body = MethodBody(Code(), "private async void StyleFromSelection_Click");

        Assert.Contains("StyleSurvey.StyleAt(", body, StringComparison.Ordinal);
        Assert.Contains("row.IsChosen = true;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_style_rows_show_the_documents_own_words()
    {
        // A row reading "18.0pt F2" tells a reader nothing about their
        // document. The first thing set in that style does.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:DataType=\"viewmodels:StyleChoice\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the style rows are gone");

        string row = xaml[at..(at + 2400)];
        Assert.Contains("{x:Bind Sample}", row, StringComparison.Ordinal);
        Assert.Contains("{x:Bind Describe}", row, StringComparison.Ordinal);
        Assert.Contains("{x:Bind Reach}", row, StringComparison.Ordinal);

        // At the document's own size and colour, which is what makes the list
        // scannable rather than a table of numbers.
        Assert.Contains("FontSize=\"{x:Bind PreviewSize}\"", row, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{x:Bind PreviewBrush}\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public void colour_starts_off_and_the_other_two_start_on()
    {
        // Most documents print their headings in the same black as everything
        // else, so requiring colour rules nothing out and costs a reader a
        // confusing empty result.
        string xaml = Xaml();

        foreach (string on in new[] { "x:Name=\"StyleUseFont\"", "x:Name=\"StyleUseSize\"" })
        {
            int at = xaml.IndexOf(on, StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.Contains("IsChecked=\"True\"", xaml[at..(at + 160)], StringComparison.Ordinal);
        }

        int colour = xaml.IndexOf("x:Name=\"StyleUseColor\"", StringComparison.Ordinal);
        Assert.True(colour >= 0);
        Assert.DoesNotContain("IsChecked=\"True\"", xaml[colour..(colour + 160)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_scan_runs_off_the_ui_thread_with_progress_and_cancellation()
    {
        // Reading a page's text LOADS and parses that page, so a long book is
        // thousands of parses however this is written.
        string body = MethodBody(ViewModel(), "public Task<IReadOnlyList<StyledRunPage>> ScanStyledRunsAsync");

        Assert.Contains("Task.Run", body, StringComparison.Ordinal);
        Assert.Contains("token.ThrowIfCancellationRequested()", body, StringComparison.Ordinal);
        Assert.Contains("progress?.Report", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_scan_reports_per_page_so_an_empty_page_is_itself_a_finding()
    {
        // A flat list of runs cannot tell "this document is a scan" from "this
        // document has no headings": both are no runs. Keeping the pages is
        // what lets the dialog answer the question the reader actually has.
        string body = MethodBody(ViewModel(), "public Task<IReadOnlyList<StyledRunPage>> ScanStyledRunsAsync");
        Assert.Contains("StyledRunLoader.Load(handle, i)", body, StringComparison.Ordinal);

        Assert.Contains("ScanDiagnosis.Of(", MethodBody(Code(), "private async void StyleScan_Click"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void a_paragraph_is_not_a_heading()
    {
        // The cap that keeps the style list readable and a whole book's runs
        // worth holding. It lives with the feature, not in the core, which
        // stays a straight report of what the page contains.
        string loader = Read("PdfEditorApp", "Interop", "StyledRunLoader.cs");

        Assert.Contains("LongestHeading", loader, StringComparison.Ordinal);
        Assert.Contains("run.Text.Length > LongestHeading", loader, StringComparison.Ordinal);
        Assert.Contains("continue;", loader, StringComparison.Ordinal);
    }

    [Fact]
    public void the_native_buffer_is_freed_whatever_happens()
    {
        // Every one of these is a page's worth of allocation, and a scan makes
        // one per page.
        string loader = Read("PdfEditorApp", "Interop", "StyledRunLoader.cs");

        Assert.Contains("finally", loader, StringComparison.Ordinal);
        Assert.Contains("free_byte_buffer", loader, StringComparison.Ordinal);
    }
}
