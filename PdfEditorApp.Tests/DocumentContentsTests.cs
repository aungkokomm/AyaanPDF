using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Document properties' second round: old Hindi and Burmese fonts, the page
/// check, what the file holds, attached files, and the dialog's tabs.
/// </summary>
/// <remarks>
/// The core half (the page check, the counts, attachments listed, saved and
/// removed with a control that keeps them) is proved in
/// render_core/src/docinfo.rs, and against real books by the ignored
/// what_the_page_check_finds_in_real_files.
/// </remarks>
public class DocumentContentsTests
{
    // ---------------- Old fonts ----------------

    [Theory]
    // Names exactly as the census of the test machine's PDFs found them.
    [InlineData("KrutiDev010", "Kruti Dev", "hin")]
    [InlineData("KrutiDev020", "Kruti Dev", "hin")]
    [InlineData("Walkman-Chanakya-901", "Walkman Chanakya", "hin")]
    [InlineData("WalkmanChanakya901Normal", "Walkman Chanakya", "hin")]
    [InlineData("DV-TTRadhika-Bold", "DV-TT", "hin")]
    [InlineData("BRHDevanagari", "BRH Devanagari", "hin")]
    [InlineData("Zawgyi-One", "Zawgyi", "mya")]
    [InlineData("WinInnwa", "Win Innwa", "mya")]
    [InlineData("Win Innwa,Bold", "Win Innwa", "mya")]
    [InlineData("WinInnwa-Identity-H", "Win Innwa", "mya")]
    [InlineData("Win_Researcher", "Win Research", "mya")]
    [InlineData("WinYadanapon", "Win Yadanapon", "mya")]
    [InlineData("Win Taungyi", "Win Taungyi", "mya")]
    [InlineData("WinAmaraPura", "Win Amarapura", "mya")]
    [InlineData("Wwin_Burmese", "Wwin Burmese", "mya")]
    [InlineData("Wwin_Bur_Am_Normal", "Wwin Burmese", "mya")]
    [InlineData("Wwin_Tagaung", "Wwin", "mya")]
    public void an_old_font_is_known_by_the_name_a_pdf_gives_it(string pdfName, string name, string ocr)
    {
        var font = LegacyFonts.Recognise(pdfName);
        Assert.NotNull(font);
        Assert.Equal(name, font!.Name);
        Assert.Equal(ocr, font.OcrLanguage);
    }

    [Theory]
    // Unicode fonts, some named after the old ones, and the Win lookalikes.
    [InlineData("ChanakyaBBTUni")]
    [InlineData("ChanakyaBBTUni-Bold")]
    [InlineData("MMAL3Uni")]
    [InlineData("Wingdings")]
    [InlineData("Wingdings-Regular")]
    [InlineData("Wingbex")]
    [InlineData("NirmalaUI")]
    [InlineData("Mangal")]
    [InlineData("NotoSansDevanagari")]
    [InlineData("MyanmarText")]
    [InlineData("Pyidaungsu")]
    [InlineData("Padauk-Regular")]
    [InlineData("ArialMT")]
    [InlineData("")]
    public void a_unicode_font_is_not_an_old_one(string pdfName) => Assert.Null(LegacyFonts.Recognise(pdfName));

    [Fact]
    public void the_warning_says_what_happens_to_the_text_in_plain_words()
    {
        Assert.Equal(
            "Text in Kruti Dev, an old Hindi font that isn't Unicode, looks right, but search and copy see English letters.",
            LegacyFonts.Recognise("KrutiDev010")!.Warning);

        // Zawgyi's codes are Burmese ones, used its own way.
        Assert.Contains("a search in Unicode Burmese won't find it", LegacyFonts.Recognise("Zawgyi-One")!.Warning, StringComparison.Ordinal);

        // Two weights of one font are one warning.
        Assert.Single(LegacyFonts.In(["KrutiDev010", "KrutiDev020", "Verdana"]));
    }

    // ---------------- The page check ----------------

    private static byte[] Fields(params string[] fields) => NulFields.Join(fields);

    [Fact]
    public void the_cores_page_check_is_read_page_by_page()
    {
        var pages = PageSurvey.Parse(Fields(
            "0", "1", "0", "2", "KrutiDev020", "Verdana",
            "1", "0", "0", "0",
            "2", "1", "1", "1", "WalkmanChanakya901Normal"));

        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { "KrutiDev020", "Verdana" }, pages[0].Fonts);
        Assert.False(pages[1].HasText);
        Assert.Empty(pages[1].Fonts);
        Assert.True(pages[2].Recognised);

        // A truncated answer stops at the last whole page rather than guessing.
        Assert.Single(PageSurvey.Parse(Fields("0", "1", "0", "1", "Arial", "1", "1", "0", "3", "A")));
        Assert.Empty(PageSurvey.Parse([]));
    }

    [Fact]
    public void the_findings_say_which_pages_have_no_text_and_which_are_in_old_fonts()
    {
        var pages = new List<PageFacts>
        {
            new(0, true, false, ["KrutiDev020", "Verdana"]),
            new(1, true, true, ["WalkmanChanakya901Normal"]),
            new(2, false, false, []),
            new(3, true, false, ["WalkmanChanakya901Normal", "KrutiDev010"]),
            new(4, false, false, []),
        };
        var findings = PageSurvey.Summarise(pages, 5);

        Assert.Equal(new[] { 2, 4 }, findings.WithoutText);
        Assert.Equal("2 of 5 pages have no text: 3, 5.", findings.TextLine);

        var kruti = findings.Legacy.Single(g => g.Font.Name == "Kruti Dev");
        Assert.Equal(new[] { 0, 3 }, kruti.Pages);
        Assert.Equal("Kruti Dev on 2 pages.", PageFindings.LegacyLine(kruti));
        var walkman = findings.Legacy.Single(g => g.Font.Name == "Walkman Chanakya");
        Assert.Equal(new[] { 1, 3 }, walkman.Pages);
        Assert.Equal(new[] { 3 }, walkman.NotRecognised);
        Assert.Equal("Walkman Chanakya on 2 pages, 1 of them with recognised text.", PageFindings.LegacyLine(walkman));

        // Both Hindi fonts go to one run, the page recognised already left out.
        var request = findings.LegacyRequest();
        Assert.NotNull(request);
        Assert.Equal(new[] { 0, 3 }, request!.Pages);
        Assert.Equal("Pages in Kruti Dev and Walkman Chanakya", request.What);
        Assert.Equal(new[] { "hin", "eng" }, request.Languages);
        Assert.True(request.ReadPagesWithText, "their text is the old font's, so skipping pages with text would skip them all");

        var scanned = OcrRequest.ForPagesWithoutText(findings.WithoutText);
        Assert.Null(scanned.Languages);
        Assert.False(scanned.ReadPagesWithText);
    }

    [Fact]
    public void when_every_page_in_an_old_font_is_recognised_there_is_nothing_left_to_read()
    {
        var findings = PageSurvey.Summarise([new(0, true, true, ["Zawgyi-One"]), new(1, true, true, ["Zawgyi-One"])], 2);
        Assert.Null(findings.LegacyRequest());
        Assert.Equal("Zawgyi on 2 pages, all with recognised text.", PageFindings.LegacyLine(findings.Legacy[0]));
        Assert.Equal("Every page has text.", findings.TextLine);
    }

    [Fact]
    public void a_book_with_hindi_and_burmese_old_fonts_reads_the_bigger_one_first()
    {
        // Hindi and Myanmar cannot be recognised in one pass.
        var findings = PageSurvey.Summarise(
            [new(0, true, false, ["KrutiDev010"]), new(1, true, false, ["Zawgyi-One"]), new(2, true, false, ["WinInnwa"])], 3);
        var request = findings.LegacyRequest()!;
        Assert.Equal(new[] { 1, 2 }, request.Pages);
        Assert.Equal(new[] { "mya" }, request.Languages);
        Assert.Equal("Pages in Zawgyi and Win Innwa", request.What);
    }

    [Fact]
    public void a_scan_says_so_and_a_long_page_list_is_cut_short()
    {
        var scan = PageSurvey.Summarise(Enumerable.Range(0, 4).Select(p => new PageFacts(p, false, false, [])).ToList(), 4);
        Assert.Equal("None of the 4 pages has text. It is probably a scan.", scan.TextLine);

        var scattered = Enumerable.Range(0, 20).Select(i => i * 2).ToList();
        Assert.Equal("1, 3, 5, 7, 9, 11 and 14 more", PageSurvey.ShortList(scattered, 40));
        Assert.Equal("12-40, 88", PageSurvey.ShortList([.. Enumerable.Range(11, 29), 87], 100));
    }

    // ---------------- What the file holds ----------------

    [Fact]
    public void what_a_file_holds_reads_as_one_line()
    {
        var contents = DocumentContents.FromPairs(NulFields.Pairs(Fields(
            "Bookmarks", "48", "Comments", "12", "Links", "0", "FormFields", "9", "Signatures", "1", "Attachments", "3")));
        Assert.Equal("48 bookmarks, 12 comments, a form with 9 fields, 1 for a signature, 3 attached files", contents.Describe());

        Assert.Equal("1 bookmark, 1 link", new DocumentContents(1, 0, 1, 0, 0, 0).Describe());
        Assert.Equal("Only pages", new DocumentContents(0, 0, 0, 0, 0, 0).Describe());
    }

    [Fact]
    public void attached_files_are_read_with_their_place_in_the_list()
    {
        var files = AttachedFile.FromBuffer(Fields("salaries.xlsx", "25", "नोट्स.txt", "1024"));
        Assert.Equal(new AttachedFile(0, "salaries.xlsx", 25), files[0]);
        Assert.Equal(new AttachedFile(1, "नोट्स.txt", 1024), files[1]);
    }

    [Fact]
    public void attached_files_go_only_with_personal_info()
    {
        var alone = NulFields.Pairs(DocumentInfoStamp.Pairs(null, "p", "c", DateTimeOffset.Now, removeAttachments: true));
        Assert.False(alone.ContainsKey("RemoveAttachments"), "it rides the whole rewrite that personal info removal does");

        var with = NulFields.Pairs(DocumentInfoStamp.Pairs(null, "p", "c", DateTimeOffset.Now, removePersonal: true, removeAttachments: true));
        Assert.Equal("1", with["RemoveAttachments"]);
    }

    // ---------------- The wiring ----------------

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

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return code[at..code.IndexOf("\n    }\n", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void the_dialog_is_three_tabs_that_keep_one_height()
    {
        string dialog = Body(Read("PdfEditorApp", "MainPage.xaml.cs"), "private async void DocumentProperties_Click(");
        Assert.Contains("var tabs = new SelectorBar();", dialog, StringComparison.Ordinal);
        Assert.Contains("(new SelectorBarItem { Text = \"Description\", IsSelected = true }, descriptionTab),", dialog, StringComparison.Ordinal);
        Assert.Contains("(new SelectorBarItem { Text = \"Opening\" }, openingTab),", dialog, StringComparison.Ordinal);
        Assert.Contains("(new SelectorBarItem { Text = \"File\" }, fileTab),", dialog, StringComparison.Ordinal);
        Assert.Contains("host.MinHeight = host.ActualHeight;", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void the_dialog_warns_of_old_fonts_checks_pages_and_hands_them_to_recognize_text()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        string dialog = Body(code, "private async void DocumentProperties_Click(");

        // The old-font warning comes from the fonts in the file as saved, read
        // without writing a copy first.
        Assert.Contains("var fileFonts = ViewModel.ListFileFontsAsync();", dialog, StringComparison.Ordinal);
        Assert.Contains("LegacyFonts.In(fonts.Select(f => f.Name))", dialog, StringComparison.Ordinal);

        // One page check a visit, stopped when the dialog closes.
        Assert.Contains("survey ??= ViewModel.SurveyPagesAsync(", dialog, StringComparison.Ordinal);
        Assert.Contains("closing.Cancel();", dialog, StringComparison.Ordinal);
        Assert.Contains("Recognize(OcrRequest.ForPagesWithoutText(findings.WithoutText))", dialog, StringComparison.Ordinal);
        Assert.Contains("findings.LegacyRequest()", dialog, StringComparison.Ordinal);

        // Recognize text keeps the dialog's changes and opens once it has gone.
        Assert.Contains("if (result == ContentDialogResult.Primary || recognize is not null)", dialog, StringComparison.Ordinal);
        Assert.True(
            dialog.IndexOf("RootGrid.Focus(FocusState.Programmatic);", StringComparison.Ordinal)
            < dialog.IndexOf("RecognizeText(recognize);", StringComparison.Ordinal),
            "Recognize text opens after the dialog is gone");

        string window = Read("PdfEditorApp", "OcrWindow.xaml.cs");
        Assert.Contains("_chosenPages = request?.Pages ?? chosenPages;", window, StringComparison.Ordinal);
        Assert.Contains("if (request is { ReadPagesWithText: true })", window, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_check_goes_a_few_pages_at_a_time_and_stops_when_asked()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        string survey = Body(vm, "public async Task<List<PageFacts>?> SurveyPagesAsync(");
        Assert.Contains("const int PagesPerStep = 16;", survey, StringComparison.Ordinal);
        Assert.Contains("if (token.IsCancellationRequested || handle != _documentHandle)", survey, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => TakeBytes(RenderCoreNative.survey_pages(handle, from, PagesPerStep)))", survey, StringComparison.Ordinal);
    }

    [Fact]
    public void attached_files_are_listed_saved_and_removed_through_the_pending_properties()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        Assert.Contains("removePersonal && removeAttachments);", Body(vm, "public void ApplyDocumentProperties("), StringComparison.Ordinal);
        Assert.Contains("_removeAttachments = state.RemoveAttachments;", Body(vm, "private void SetPendingProperties("), StringComparison.Ordinal);
        Assert.Contains("_removePersonal, _removeDates, _catalogEdits, _removeAttachments)", Body(vm, "private SavePlan? BeginSave("), StringComparison.Ordinal);

        string dialog = Body(Read("PdfEditorApp", "MainPage.xaml.cs"), "private async void DocumentProperties_Click(");
        Assert.Contains("removeAttachments.IsChecked == true && attachedCount > 0);", dialog, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.SaveAttachmentAsync(attached.Index, target.Path)", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void the_core_calls_are_declared_as_the_core_exports_them()
    {
        string interop = Read("PdfEditorApp", "Interop", "RenderCoreNative.cs");
        string core = Read("render_core", "src", "docinfo.rs");

        Assert.Contains("public static extern ByteBuffer survey_pages(ulong docHandle, int first, int count);", interop, StringComparison.Ordinal);
        Assert.Contains("pub extern \"C\" fn survey_pages(doc_handle: u64, first: i32, count: i32) -> ByteBuffer", core, StringComparison.Ordinal);
        Assert.Contains("public static extern ByteBuffer document_contents(", interop, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn document_contents(path: *const c_char) -> ByteBuffer", core, StringComparison.Ordinal);
        Assert.Contains("public static extern ByteBuffer list_attachments(ulong docHandle);", interop, StringComparison.Ordinal);
        Assert.Contains("pub extern \"C\" fn list_attachments(doc_handle: u64) -> ByteBuffer", core, StringComparison.Ordinal);
        Assert.Contains("public static extern int save_attachment(", interop, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn save_attachment(doc_handle: u64, index: i32, path: *const c_char) -> i32", core, StringComparison.Ordinal);
    }
}
