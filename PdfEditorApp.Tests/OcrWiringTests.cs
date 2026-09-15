using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Page > Recognize text, as wired: pages read off the UI thread, each written
/// into the document as an invisible text layer as soon as it is read, all
/// under one undo step.
/// </summary>
/// <remarks>
/// The window cannot be loaded here. The layer itself is proved in
/// render_core's ocr tests (words land over the picture, survive saving,
/// replace an earlier layer, change nothing drawn, and follow a turned page),
/// the choice of recogniser in OcrPlanTests, and the Myanmar reading rules in
/// OcrTextLogicTests.
/// </remarks>
public class OcrWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Code() => Read("PdfEditorApp", "OcrWindow.xaml.cs");

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

    private static int IndexIn(string body, string text)
    {
        int at = body.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' was not found");
        return at;
    }

    [Fact]
    public void recognize_text_is_on_the_page_menu_and_opens_its_own_window()
    {
        string menu = Read("PdfEditorApp", "MainPage.xaml");
        int page = IndexIn(menu, "<MenuBarItem Title=\"Page\"");
        int item = IndexIn(menu, "<MenuFlyoutItem Text=\"Recognize text...\" Click=\"RecognizeText_Click\" />");
        int next = menu.IndexOf("<MenuBarItem ", page + 1, StringComparison.Ordinal);
        Assert.True(page < item && item < next, "Recognize text is not under Page");

        Assert.Contains("new OcrWindow(ViewModel, chosen)",
            MethodBody(Read("PdfEditorApp", "MainPage.xaml.cs"), "private void RecognizeText_Click("), StringComparison.Ordinal);
        Assert.Contains("class OcrWindow : Window", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("<ContentDialog", Read("PdfEditorApp", "OcrWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void one_undo_step_is_recorded_before_the_first_page_is_written()
    {
        string start = MethodBody(Code(), "private async void Start_Click(");

        Assert.Single(Regex.Matches(start, @"BeginTextRecognition\(\)"));
        Assert.True(IndexIn(start, "_viewModel.BeginTextRecognition();") < IndexIn(start, "OcrLayerWriter.Write("));
        Assert.True(IndexIn(start, "OcrLayerWriter.Write(") < IndexIn(start, "_viewModel.TextRecognizedOnPage(result.Page);"));

        string begin = MethodBody(Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"), "public void BeginTextRecognition(");
        Assert.Contains("PushHistory(HistoryScope.Document, \"Recognize text\");", begin, StringComparison.Ordinal);
    }

    [Fact]
    public void a_page_just_read_selects_as_text_straight_away_and_needs_saving()
    {
        string written = MethodBody(Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs"), "public void TextRecognizedOnPage(");

        Assert.Contains("InvalidateLoadedPage(pageIndex);", written, StringComparison.Ordinal);
        // ⚠️ The text layers are cached for the document's lifetime.
        Assert.Contains("ForgetTextLayers();", written, StringComparison.Ordinal);
        Assert.Contains("IsDirty = true;", written, StringComparison.Ordinal);
    }

    [Fact]
    public void a_run_stops_when_the_document_changes_under_it()
    {
        string start = MethodBody(Code(), "private async void Start_Click(");

        int check = IndexIn(start, "_viewModel.DocumentHandleForRecognition != handle || _viewModel.PageCount != pageCount");
        Assert.True(check < IndexIn(start, "OcrLayerWriter.Write("), "the document must be checked before a page is written");
    }

    [Fact]
    public void the_window_cannot_be_closed_out_from_under_a_run()
    {
        string ctor = MethodBody(Code(), "public OcrWindow(");

        Assert.True(IndexIn(ctor, "AppWindow.Closing +=") < IndexIn(ctor, "args.Cancel = true;"));
        Assert.Contains("_running.Cancel();", ctor, StringComparison.Ordinal);
    }

    [Fact]
    public void the_choices_are_remembered_for_next_time()
    {
        string start = MethodBody(Code(), "private async void Start_Click(");

        int update = IndexIn(start, "SettingsStore.Update(");
        Assert.True(update < IndexIn(start, "OcrLanguages = OcrPlan.StoreLanguages(codes)"));
        Assert.True(update < IndexIn(start, "OcrFast = fast"));
        Assert.True(update < IndexIn(start, "OcrSkipPagesWithText = skip"));
    }

    [Fact]
    public void the_models_and_their_engines_ship_with_the_app()
    {
        string project = Read("PdfEditorApp", "PdfEditorApp.csproj");
        Assert.Contains("<Content Include=\"Assets\\Ocr\\**\\*\">", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Tesseract\" Version=\"5.2.0\" />", project, StringComparison.Ordinal);
        Assert.Contains("tesseract\\5.2.0\\x64\\*.dll", project, StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Native\\*.dll\">", project, StringComparison.Ordinal);

        // Fetched and hash-checked on the build machine, not committed.
        Assert.Contains("PdfEditorApp/Assets/Ocr/tessdata/", Read(".gitignore"), StringComparison.Ordinal);
        Assert.Contains("CONVERTED_SHA", Read("tools", "ocr", "fetch_ocr_models.py"), StringComparison.Ordinal);
    }
}
