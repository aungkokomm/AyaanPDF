using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfEditorApp.Ocr;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Recognises the text on a document's pages and writes it into them as an
/// invisible layer, one page at a time as each is read.
/// </summary>
/// <remarks>
/// One undo step covers the whole run, recorded just before the first page is
/// written. Pages already written stay written when a run is stopped. A run
/// stops by itself if the document changes under it (closed, reopened by an
/// undo, pages added or removed), since the pages it is reading would no
/// longer be the pages it writes to.
/// </remarks>
public sealed partial class OcrWindow : Window
{
    private readonly ViewportViewModel _viewModel;
    private readonly IReadOnlyList<int> _chosenPages;
    private CancellationTokenSource? _running;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <param name="request">
    /// Pages Document properties found and wants read, with the language and
    /// the "skip" choice their reason decides. Null for Page > Recognize text.
    /// </param>
    public OcrWindow(ViewportViewModel viewModel, IReadOnlyList<int> chosenPages, OcrRequest? request = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _chosenPages = request?.Pages ?? chosenPages;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Sized for its choices at the screen's scale, and never taller than the screen.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(620 * scale), work.Width);
        int height = Math.Min((int)(720 * scale), (int)(work.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Theming.Apply(this, SettingsStore.Current.Theme);

        var settings = SettingsStore.Current;
        var languages = OcrPlan.ParseLanguages(settings.OcrLanguages);
        EnglishBox.IsChecked = languages.Contains("eng");
        HindiBox.IsChecked = languages.Contains("hin");
        MyanmarBox.IsChecked = languages.Contains("mya");
        FastChoice.IsChecked = settings.OcrFast;
        AccurateChoice.IsChecked = !settings.OcrFast;
        SkipTextPages.IsChecked = settings.OcrSkipPagesWithText;

        if (request?.Languages is { } asked)
        {
            EnglishBox.IsChecked = asked.Contains("eng");
            HindiBox.IsChecked = asked.Contains("hin");
            MyanmarBox.IsChecked = asked.Contains("mya");
            FastChoice.IsChecked = false;
            AccurateChoice.IsChecked = true;
        }
        if (request is { ReadPagesWithText: true })
        {
            SkipTextPages.IsChecked = false;
        }

        int pageCount = viewModel.PageCount;
        AllPages.Content = pageCount == 1 ? "All pages (1)" : $"All pages ({pageCount})";
        PageRangeBox.PlaceholderText = "For example 1-3, 8";
        if (request is not null)
        {
            ChosenPages.Content = $"{request.What} ({PageSurvey.ShortList(request.Pages, pageCount)})";
            ChosenPages.Visibility = Visibility.Visible;
            ChosenPages.IsChecked = true;
        }
        else if (chosenPages.Count > 1)
        {
            ChosenPages.Content = $"Pages chosen in the thumbnails ({PageSelection.Format(chosenPages, pageCount)})";
            ChosenPages.Visibility = Visibility.Visible;
            ChosenPages.IsChecked = true;
        }
        else
        {
            AllPages.IsChecked = true;
        }

        // The page being looked at can change while this window is open.
        Activated += (_, _) => CurrentPage.Content = $"Current page ({_viewModel.CurrentPageIndex + 1})";

        Validate();

        // A run is writing into the document, so closing asks it to stop
        // rather than leaving it running with no window to show it.
        AppWindow.Closing += (_, args) =>
        {
            if (_running is not null)
            {
                args.Cancel = true;
                _running.Cancel();
            }
        };
    }

    // ---------------- The choices ----------------

    private void Choice_Changed(object sender, RoutedEventArgs e) => Validate();

    private void PageRangeBox_GotFocus(object sender, RoutedEventArgs e) => PageRange.IsChecked = true;

    private List<string> ChosenLanguages()
    {
        var codes = new List<string>();
        if (EnglishBox.IsChecked == true) { codes.Add("eng"); }
        if (HindiBox.IsChecked == true) { codes.Add("hin"); }
        if (MyanmarBox.IsChecked == true) { codes.Add("mya"); }
        return codes;
    }

    /// <summary>Says what stops a run with the choices as they are, and allows Recognize only when nothing does.</summary>
    private void Validate()
    {
        var codes = ChosenLanguages();
        string? problem = OcrPlan.Problem(codes, FastChoice.IsChecked == true) ?? MissingModel(codes);
        ShowMessage(problem);
        StartButton.IsEnabled = problem is null && _running is null;
    }

    private static string? MissingModel(IReadOnlyCollection<string> codes)
    {
        foreach (var (code, name) in OcrPlan.Bundled)
        {
            if (codes.Contains(code) && !OcrAssets.HasTesseractLanguage(code))
            {
                return $"The {name} reading model is missing from this installation. Reinstalling Ayaan PDF puts it back.";
            }
        }

        return codes.Contains("mya") && !File.Exists(OcrAssets.MyanmarModel)
            ? "The Myanmar reading model is missing from this installation. Reinstalling Ayaan PDF puts it back."
            : null;
    }

    private bool TryChoosePages(int pageCount, out IReadOnlyList<int> pages, out string? problem)
    {
        problem = null;
        if (CurrentPage.IsChecked == true)
        {
            pages = new[] { Math.Clamp(_viewModel.CurrentPageIndex, 0, pageCount - 1) };
            return true;
        }

        if (ChosenPages.IsChecked == true)
        {
            pages = _chosenPages.Where(p => p >= 0 && p < pageCount).ToArray();
        }
        else if (PageRange.IsChecked == true)
        {
            if (!PageSelection.TryParse(PageRangeBox.Text, pageCount, out pages, out problem))
            {
                return false;
            }
        }
        else
        {
            pages = Enumerable.Range(0, pageCount).ToArray();
        }

        if (pages.Count == 0)
        {
            problem = "Choose the pages to read, for example 1-3, 8.";
            return false;
        }
        return true;
    }

    // ---------------- Reading ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ShowMessage(null);
        ShowSummary(null);

        ulong handle = _viewModel.DocumentHandleForRecognition;
        int pageCount = _viewModel.PageCount;
        if (handle == 0 || pageCount == 0)
        {
            ShowMessage("There's no document open to read.");
            return;
        }

        var codes = ChosenLanguages();
        bool fast = FastChoice.IsChecked == true;
        if ((OcrPlan.Problem(codes, fast) ?? MissingModel(codes)) is { } problem)
        {
            ShowMessage(problem);
            return;
        }

        if (!TryChoosePages(pageCount, out var pages, out string? pageProblem))
        {
            ShowMessage(pageProblem);
            return;
        }

        bool skip = SkipTextPages.IsChecked == true;
        SettingsStore.Update(s => s with
        {
            OcrLanguages = OcrPlan.StoreLanguages(codes),
            OcrFast = fast,
            OcrSkipPagesWithText = skip,
        });

        OcrPlan plan = OcrPlan.For(codes, fast);
        var jobs = pages.Select(p =>
        {
            var (width, height) = _viewModel.PageSizeForRecognition(p);
            return new OcrPageJob(p, width, height);
        }).ToList();

        _running = new CancellationTokenSource();
        SetRunning(true, jobs.Count);
        var watch = Stopwatch.StartNew();
        int done = 0, written = 0, words = 0, skipped = 0, failed = 0;
        bool recorded = false;
        string? stopped = null;

        try
        {
            await foreach (OcrPageResult result in OcrRunner.ReadAsync(plan, handle, jobs, skip, _running.Token))
            {
                if (_viewModel.DocumentHandleForRecognition != handle || _viewModel.PageCount != pageCount)
                {
                    stopped = "The document changed while its pages were being read, so reading stopped. Pages already done keep their text.";
                    _running.Cancel();
                    break;
                }

                if (result.Skipped)
                {
                    skipped++;
                }
                else if (result.Words is not { } found)
                {
                    failed++;
                }
                else
                {
                    if (!recorded)
                    {
                        _viewModel.BeginTextRecognition();
                        recorded = true;
                    }

                    int count = await Task.Run(() => OcrLayerWriter.Write(handle, result.Page, found));
                    if (count < 0)
                    {
                        failed++;
                    }
                    else
                    {
                        written++;
                        words += count;
                    }
                    _viewModel.TextRecognizedOnPage(result.Page);
                }

                done++;
                RunProgress.Value = done;
                ProgressText.Text = $"Read {done} of {jobs.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            stopped ??= "Stopped. Pages already done keep their text.";
        }
        catch (Exception ex)
        {
            Diag.Log($"ocr: failed: {ex}");
            stopped = $"Reading failed: {(ex.InnerException ?? ex).Message}";
        }
        finally
        {
            _running = null;
            SetRunning(false, 0);
        }

        Diag.Log($"ocr: {plan.Engine} {plan.TesseractLanguages} pages={jobs.Count} written={written} words={words} skipped={skipped} failed={failed} in {watch.ElapsedMilliseconds} ms{(stopped is null ? "" : " (stopped)")}");

        ShowSummary(Summary(written, words, skipped, failed, watch.Elapsed));
        ShowMessage(stopped);
        if (written > 0)
        {
            _viewModel.Status = written == 1 ? "Text recognised on 1 page." : $"Text recognised on {written} pages.";
        }
    }

    private static string Summary(int written, int words, int skipped, int failed, TimeSpan took)
    {
        var parts = new List<string>();
        if (written > 0)
        {
            string time = took.TotalSeconds < 60
                ? $"{Math.Max(1, (int)Math.Round(took.TotalSeconds))} s"
                : $"{(int)took.TotalMinutes} min {took.Seconds} s";
            parts.Add($"Done: {Pages(written)}, {words.ToString("N0", CultureInfo.CurrentCulture)} words, in {time}.");
        }
        if (skipped > 0)
        {
            parts.Add(written == 0 && failed == 0
                ? "Every page already has text, so there was nothing to read. Untick \"Skip pages that already have text\" to read them again."
                : $"{Pages(skipped)} already had text and {(skipped == 1 ? "was" : "were")} skipped.");
        }
        if (failed > 0)
        {
            parts.Add($"{Pages(failed)} couldn't be read.");
        }
        return string.Join(" ", parts);
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    private void SetRunning(bool running, int steps)
    {
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        foreach (Control choice in new Control[]
                 {
                     AllPages, CurrentPage, ChosenPages, PageRange, PageRangeBox,
                     EnglishBox, HindiBox, MyanmarBox, AccurateChoice, FastChoice, SkipTextPages,
                 })
        {
            choice.IsEnabled = !running;
        }

        RunProgress.Maximum = Math.Max(1, steps);
        RunProgress.Value = 0;
        ProgressText.Text = running ? "Starting" : string.Empty;
        if (!running)
        {
            Validate();
        }
    }

    private void ShowMessage(string? message)
    {
        MessageText.Text = message ?? string.Empty;
        MessageText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSummary(string? summary)
    {
        SummaryText.Text = summary ?? string.Empty;
        SummaryText.Visibility = string.IsNullOrEmpty(summary) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _running?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && e.OriginalSource is not TextBox && _running is null)
        {
            e.Handled = true;
            Close();
        }
    }
}
