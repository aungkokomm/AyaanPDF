using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Builds an outline out of a document's own headings, two ways.
///
/// A WINDOW rather than a dialog, and not for looks. A ContentDialog will not
/// grow past its ContentDialogMaxWidth of 548, so the wide two-column layout
/// this needs was simply clipped: the preview column, half the options and the
/// closing note all fell off the right-hand edge. A window resizes, and being
/// modeless it also leaves the document reachable, which is what makes "tick
/// the style of the text I selected" usable rather than a thing you had to set
/// up before opening.
/// </summary>
public sealed partial class BookmarkWindow : Window
{
    /// <summary>
    /// Presets for the wording tab, in the order of the combo above it.
    ///
    /// Only the first is what the tool this was ported from offered, and it
    /// only suits papers and manuals. A book of chapters matches nothing under
    /// it, which looks like a broken feature rather than a mismatched pattern.
    /// </summary>
    private static readonly string[] AutoBookmarkPatterns =
    [
        HeadingDetector.NumberedPattern,

        // Chapter/Section/Part, numbered in digits or Roman numerals. No
        // hierarchy group, so these come out as one flat level, which is what a
        // list of chapters is.
        @"^\s*(Chapter|Section|Part|Adhyay|Adhyaya)\s+([0-9]+|[IVXLC]+)\b.*",

        // A whole line in capitals, four characters or more. Catches the
        // headings in documents that mark them by case alone.
        @"^[\p{Lu}][\p{Lu}\s\d\p{P}]{3,}$",
    ];

    private readonly ViewportViewModel _viewModel;
    private readonly Action _onOutlineWritten;

    /// <summary>
    /// Every styled run in the document, from the one scan.
    ///
    /// Held for the life of the window because both halves of the look tab work
    /// off it: the list of styles the document uses, and the matching that
    /// turns ticked styles into headings. Scanning again per tick would put the
    /// only slow part behind every checkbox.
    /// </summary>
    private IReadOnlyList<StyledRun> _styleRuns = [];

    private List<DetectedHeading> _styleFound = [];
    private List<DetectedHeading> _autoBookmarkFound = [];
    private CancellationTokenSource? _styleScan;
    private CancellationTokenSource? _autoBookmarkScan;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _styleFilterTimer;

    public BookmarkWindow(ViewportViewModel viewModel, Action onOutlineWritten)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _onOutlineWritten = onOutlineWritten;

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));

        AutoBookmarkPattern.Text = AutoBookmarkPatterns[0];

        // A scan left running would keep parsing pages nobody is waiting for.
        Closed += (_, _) =>
        {
            _styleScan?.Cancel();
            _autoBookmarkScan?.Cancel();
            _styleRuns = [];
        };
    }

    // ---------------- The two tabs ----------------

    /// <summary>
    /// Shows the tab that was picked, and puts ITS answer in the shared
    /// preview.
    ///
    /// Each tab keeps its own findings rather than clearing the other's: the
    /// point of one window is being able to try both and take whichever gave
    /// the better outline, which a tab switch that threw the last result away
    /// would defeat.
    /// </summary>
    private void BookmarkTab_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LookPanel is null || WordingPanel is null)
        {
            // Raised while the tree is still being built.
            return;
        }

        bool byLook = sender.SelectedItem == LookTab;
        LookPanel.Visibility = byLook ? Visibility.Visible : Visibility.Collapsed;
        WordingPanel.Visibility = byLook ? Visibility.Collapsed : Visibility.Visible;

        if (byLook)
        {
            RefreshStylePreview();
        }
        else
        {
            ShowBookmarkPreview(_autoBookmarkFound);
        }
    }

    private bool IsByWording => BookmarkTabs.SelectedItem == WordingTab;

    /// <summary>
    /// Puts a set of headings in the shared preview and decides whether there
    /// is anything to add.
    /// </summary>
    private void ShowBookmarkPreview(IReadOnlyList<DetectedHeading> found)
    {
        StylePreview.ItemsSource = found
            .Select(h => new BookmarkItem(new Bookmark(h.Level - 1, h.PageIndex, h.Title)))
            .ToList();

        StylePreviewHeader.Text = found.Count == 0
            ? "Bookmarks this makes: none"
            : $"Bookmarks this makes: {found.Count}";
        AddButton.IsEnabled = found.Count > 0;
    }

    // ---------------- By how they look ----------------

    private async void StyleScan_Click(object sender, RoutedEventArgs e)
    {
        _styleScan?.Cancel();
        var cts = new CancellationTokenSource();
        _styleScan = cts;

        StyleScanButton.IsEnabled = false;
        StyleScanProgress.Visibility = Visibility.Visible;
        StyleScanProgress.Value = 0;
        StyleScanSummary.Text = $"Reading {_viewModel.PageCount} pages...";

        var progress = new Progress<double>(v => StyleScanProgress.Value = v);

        try
        {
            var scanned = await _viewModel.ScanStyledRunsAsync(progress, cts.Token);
            _styleRuns = [.. scanned.SelectMany(p => p.Runs)];

            // Counted under the SAME rules the matching will use, or the reach
            // a row reports is not the reach ticking it gets.
            var survey = StyleSurvey.Survey(_styleRuns, CurrentMatch);
            StyleList.ItemsSource = survey.Select(t => new StyleChoice(t)).ToList();

            // The scan has been over every page, so it can say whether the
            // document is readable at all. Without this, a scanned book and a
            // working one that simply has no headings look identical: an empty
            // list and no explanation.
            StyleScanSummary.Text = ScanDiagnosis.Of(scanned, survey.Count).Describe();

            RefreshStylePreview();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan, or the window closed.
        }
        finally
        {
            StyleScanButton.IsEnabled = true;
            StyleScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Ticks the row for the style of whatever text is selected in the
    /// document.
    ///
    /// It selects in the list rather than adding an entry of its own: two
    /// routes to one place, so nothing can be in the list twice or be ticked in
    /// one view and not the other.
    /// </summary>
    private async void StyleFromSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectionStart is not (int page, int charIndex))
        {
            await ShowMessage(
                "Nothing is selected",
                "Select a word or two of a heading in the document, then press this again. "
                + "This window stays open, so the page behind it is still reachable.");
            return;
        }

        if (StyleList.ItemsSource is not List<StyleChoice> choices || choices.Count == 0)
        {
            StyleScanSummary.Text = "Scan the document first, then this can find the style you picked.";
            return;
        }

        if (StyleSurvey.StyleAt(_styleRuns, page, charIndex) is not { } style)
        {
            await ShowMessage(
                "Nothing to read there",
                "The selection starts on whitespace, which carries no style. Select a word or two of the heading itself.");
            return;
        }

        var row = choices.FirstOrDefault(c => c.Style == style);
        if (row is null)
        {
            await ShowMessage(
                "That style is not in the list",
                "The list shows the styles this document uses most; a style used once or twice may fall outside it.");
            return;
        }

        row.IsChosen = true;
        AssignStyleLevels();
        StyleList.ScrollIntoView(row);
        RefreshStylePreview();
    }

    private void StyleChoice_Changed(object sender, RoutedEventArgs e)
    {
        // A newly ticked style takes its level from its SIZE among the others
        // already ticked, which is the order a document sets them in. It stays
        // editable, because two heading styles at the same size can only be
        // told apart by the reader.
        AssignStyleLevels();
        RefreshStylePreview();
    }

    /// <summary>
    /// Only a level on a style that is actually making bookmarks matters.
    ///
    /// The guard is not tidiness: this fires as the list REALIZES each row, so
    /// without it, scrolling the styles would run the whole match again per row
    /// that scrolled into view, over every run in the document.
    /// </summary>
    private void StyleLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StyleChoice { IsChosen: true } })
        {
            RefreshStylePreview();
        }
    }

    /// <summary>What counts as the same style, as the boxes currently say.</summary>
    private StyleMatch CurrentMatch => new(
        FontName: StyleUseFont.IsChecked == true,
        FontSize: StyleUseSize.IsChecked == true,
        Color: StyleUseColor.IsChecked == true);

    private void StyleOption_Changed(object sender, RoutedEventArgs e)
    {
        // Loosening or tightening what counts as the same style changes what
        // every row REACHES, so the counts have to be worked out again. Cheap:
        // it is the runs already in hand, never the document.
        RecountStyles();
        RefreshStylePreview();
    }

    private void StyleOption_Changed(object sender, SelectionChangedEventArgs e) => RefreshStylePreview();

    /// <summary>
    /// Rebuilds the reach on every row under the current match settings,
    /// keeping whatever is ticked and whatever levels were chosen.
    /// </summary>
    private void RecountStyles()
    {
        if (StyleList?.ItemsSource is not List<StyleChoice> choices || _styleRuns.Count == 0)
        {
            return;
        }

        var chosen = choices.Where(c => c.IsChosen).ToDictionary(c => c.Style, c => c.LevelIndex);

        var recounted = StyleSurvey.Survey(_styleRuns, CurrentMatch)
            .Select(t =>
            {
                var choice = new StyleChoice(t);
                if (chosen.TryGetValue(t.Style, out int level))
                {
                    choice.IsChosen = true;
                    choice.LevelIndex = level;
                }
                return choice;
            })
            .ToList();

        StyleList.ItemsSource = recounted;
    }

    /// <summary>
    /// Debounced, because the match runs over every run in the document and
    /// this fires on every keystroke. A long book would type one character
    /// behind.
    /// </summary>
    private void StyleFilterText_Changed(object sender, TextChangedEventArgs e)
    {
        _styleFilterTimer ??= CreateStyleFilterTimer();
        _styleFilterTimer.Stop();
        _styleFilterTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateStyleFilterTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => RefreshStylePreview();
        return timer;
    }

    /// <summary>
    /// Gives every ticked style a level from its size, largest first, without
    /// disturbing one the reader has set by hand.
    /// </summary>
    private void AssignStyleLevels()
    {
        if (StyleList.ItemsSource is not List<StyleChoice> choices)
        {
            return;
        }

        var chosen = choices.Where(c => c.IsChosen)
                            .OrderByDescending(c => c.Style.SizePoints)
                            .ToList();

        for (int i = 0; i < chosen.Count; i++)
        {
            // Only where it is still at the default. Reassigning every time a
            // box is ticked would silently undo a level the reader chose.
            if (chosen[i].LevelIndex == 0 && i > 0)
            {
                chosen[i].LevelIndex = Math.Min(i, StyleBookmarker.MaxLevel - 1);
            }
        }
    }

    /// <summary>
    /// Recomputes the outline from what is ticked. Instant: it works off the
    /// runs the one scan collected, so nothing here touches the document.
    /// </summary>
    private void RefreshStylePreview()
    {
        if (StyleList is null || StylePreview is null)
        {
            // Raised while the tree is still being built.
            return;
        }

        // The other tab's findings stay on screen while it is the one showing.
        if (IsByWording)
        {
            return;
        }

        _styleFound = [];
        StylePreview.ItemsSource = null;
        AddButton.IsEnabled = false;

        if (StyleList.ItemsSource is not List<StyleChoice> choices)
        {
            return;
        }

        // Ordered by the level the reader gave them, because the detector reads
        // the list as the hierarchy: first entry is level one.
        var examples = choices.Where(c => c.IsChosen)
                              .OrderBy(c => c.LevelIndex)
                              .Select(c => c.Style)
                              .ToList();

        if (examples.Count == 0)
        {
            StylePreviewHeader.Text = "Bookmarks this makes";
            return;
        }

        StyleTextFilter filter;
        try
        {
            filter = StyleTextFilter.Create(SelectedFilterKind(), StyleFilterText.Text);
        }
        catch (ArgumentException ex)
        {
            // A pattern typed by hand, so an unbalanced bracket is ordinary.
            StylePreviewHeader.Text = "Bookmarks this makes";
            StyleScanSummary.Text = ex.Message;
            return;
        }

        var found = StyleBookmarker.Detect(
            InStylePageRange(_styleRuns),
            examples,
            new StyleMatch(
                FontName: StyleUseFont.IsChecked == true,
                FontSize: StyleUseSize.IsChecked == true,
                Color: StyleUseColor.IsChecked == true),
            allowMultiline: StyleJoinWrapped.IsChecked == true,
            filter);

        _styleFound = [.. found];
        ShowBookmarkPreview(found);
    }

    private TextFilterKind SelectedFilterKind() => StyleFilterKind.SelectedIndex switch
    {
        1 => TextFilterKind.Contains,
        2 => TextFilterKind.StartsWith,
        3 => TextFilterKind.DoesNotContain,
        4 => TextFilterKind.Matches,
        _ => TextFilterKind.Any,
    };

    /// <summary>
    /// The runs the page range allows through.
    ///
    /// Filtered here rather than by rescanning, so changing the range costs
    /// nothing and the scan stays the one slow thing that happens once.
    /// </summary>
    private IEnumerable<StyledRun> InStylePageRange(IEnumerable<StyledRun> runs)
    {
        int here = _viewModel.CurrentPageIndex;

        return StylePageRange.SelectedIndex switch
        {
            1 => runs.Where(r => r.PageIndex >= here),
            2 => runs.Where(r => r.PageIndex == here),
            _ => runs,
        };
    }

    // ---------------- By what they say ----------------

    private void AutoBookmarkPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The last entry is "Custom", which leaves whatever is in the box alone.
        int i = AutoBookmarkPreset.SelectedIndex;
        if (i >= 0 && i < AutoBookmarkPatterns.Length && AutoBookmarkPattern is not null)
        {
            AutoBookmarkPattern.Text = AutoBookmarkPatterns[i];
        }
    }

    private void AutoBookmarkPattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Editing the pattern invalidates the preview under it: offering to add
        // bookmarks that no longer match what the box says would be a lie.
        _autoBookmarkFound = [];

        if (IsByWording)
        {
            ShowBookmarkPreview([]);
        }
    }

    private async void AutoBookmarkScan_Click(object sender, RoutedEventArgs e)
    {
        _autoBookmarkScan?.Cancel();
        var cts = new CancellationTokenSource();
        _autoBookmarkScan = cts;

        AutoBookmarkScan.IsEnabled = false;
        AutoBookmarkProgress.Visibility = Visibility.Visible;
        AutoBookmarkProgress.Value = 0;
        AutoBookmarkSummary.Text = $"Reading {_viewModel.PageCount} pages...";

        var progress = new Progress<double>(v => AutoBookmarkProgress.Value = v);

        try
        {
            var found = await _viewModel.ScanForHeadingsAsync(
                AutoBookmarkPattern.Text, progress, cts.Token);

            _autoBookmarkFound = [.. found];
            ShowBookmarkPreview(found);

            AutoBookmarkSummary.Text = found.Count == 0
                ? "No headings matched. Try another pattern, or edit it above. "
                  + "If the other tab reports no text either, the document is a scan."
                : $"Found {found.Count} headings.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan, or the window closed. Nothing to say.
        }
        catch (ArgumentException ex)
        {
            // A pattern typed by hand, so an unbalanced bracket is ordinary.
            AutoBookmarkSummary.Text = ex.Message;
        }
        finally
        {
            AutoBookmarkScan.IsEnabled = true;
            AutoBookmarkProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- Writing it ----------------

    /// <summary>
    /// Writes whichever tab's findings are on screen.
    ///
    /// The preview is the contract: what the reader is looking at when they
    /// press the button is what gets written, so this reads the same flag the
    /// preview does rather than keeping its own idea of the active tab.
    /// </summary>
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ApplyOutline(IsByWording ? _autoBookmarkFound : _styleFound) is { } problem)
        {
            await ShowMessage("Could not add bookmarks", problem);
            return;
        }

        _onOutlineWritten();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ShowMessage(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
        }.ShowAsync();
    }
}
