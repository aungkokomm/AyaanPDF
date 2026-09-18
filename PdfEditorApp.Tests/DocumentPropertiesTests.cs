using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// File > Document properties, and the stamp every save now puts on a file.
/// </summary>
/// <remarks>
/// The core half (lopdf appends Info and XMP, PDFium reads it back) is proved
/// in render_core/src/docinfo.rs, including every real file on the test drive.
/// These hold the app half: what is read, what is sent, and that saving sends
/// it at the right moment.
/// </remarks>
public class DocumentPropertiesTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    // ---------------- PDF dates ----------------

    [Theory]
    [InlineData("D:20260918153000+06'30'", 2026, 9, 18, 15, 30, 0, 390)]
    [InlineData("D:20260810165530Z00'00'", 2026, 8, 10, 16, 55, 30, 0)]
    [InlineData("D:20260810165530Z", 2026, 8, 10, 16, 55, 30, 0)]
    [InlineData("D:20090930152135+05'30'", 2009, 9, 30, 15, 21, 35, 330)]
    [InlineData("D:19981223195200-08'00'", 1998, 12, 23, 19, 52, 0, -480)]
    [InlineData("20260918153000+06'30", 2026, 9, 18, 15, 30, 0, 390)]
    public void pdf_dates_are_read_with_their_offset(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes)
    {
        var when = PdfDate.Parse(text);
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)), when);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), when!.Value.Offset);
    }

    [Fact]
    public void a_date_with_no_offset_is_local_and_a_short_one_fills_in()
    {
        // Ghostscript 8.15 wrote "D:20130928174633" with no offset.
        var local = PdfDate.Parse("D:20130928174633")!.Value;
        Assert.Equal(new DateTime(2013, 9, 28, 17, 46, 33), local.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(local.DateTime), local.Offset);

        Assert.Equal(new DateTime(2020, 1, 1), PdfDate.Parse("D:2020")!.Value.DateTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("D:20261345000000Z")]
    [InlineData("D:202")]
    public void a_date_that_cannot_be_read_is_null(string text) => Assert.Null(PdfDate.Parse(text));

    [Fact]
    public void dates_are_written_in_both_forms_and_read_back()
    {
        var when = new DateTimeOffset(2026, 9, 18, 15, 30, 5, TimeSpan.FromMinutes(390));
        Assert.Equal("D:20260918153005+06'30'", PdfDate.Format(when));
        Assert.Equal("2026-09-18T15:30:05+06:30", PdfDate.FormatXmp(when));
        Assert.Equal(when, PdfDate.Parse(PdfDate.Format(when)));

        var utc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal("D:20260102030405Z", PdfDate.Format(utc));

        var west = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5));
        Assert.Equal("D:20260102030405-05'00'", PdfDate.Format(west));
    }

    // ---------------- What the dialog changed ----------------

    private static DocumentInfo Info(string title = "Old", string author = "Me", string subject = "", string keywords = "") =>
        new(title, author, subject, keywords, "Word", "Word", null, null, "1.7", 3, 612, 792, false, -1, uint.MaxValue);

    [Fact]
    public void only_what_changed_is_an_edit_and_a_cleared_field_removes_it()
    {
        var edits = InfoEdits.Between(Info(), " Old ", "", "About it", "");
        Assert.Null(edits.Title);
        Assert.Equal(string.Empty, edits.Author);
        Assert.Equal("About it", edits.Subject);
        Assert.Null(edits.Keywords);
        Assert.True(edits.HasChanges);

        Assert.False(InfoEdits.Between(Info(), "Old", "Me", "", "").HasChanges);
    }

    [Fact]
    public void a_second_round_of_edits_keeps_the_first_where_it_says_nothing()
    {
        var first = new InfoEdits("A", "B", null, null);
        var second = new InfoEdits(null, "C", "D", null);
        Assert.Equal(new InfoEdits("A", "C", "D", null), first.Then(second));
    }

    [Fact]
    public void the_dialog_shows_edits_waiting_to_be_written_over_the_files_own()
    {
        var shown = Info().With(new InfoEdits("New", null, null, "k"));
        Assert.Equal("New", shown.Title);
        Assert.Equal("Me", shown.Author);
        Assert.Equal("k", shown.Keywords);
    }

    // ---------------- What a save sends ----------------

    [Fact]
    public void every_save_stamps_producer_creator_and_both_dates()
    {
        var now = new DateTimeOffset(2026, 9, 18, 15, 30, 0, TimeSpan.FromMinutes(390));
        var pairs = NulFields.Pairs(DocumentInfoStamp.Pairs(null, "Ayaan PDF 3.47.0", "Ayaan PDF", now));

        Assert.Equal("Ayaan PDF 3.47.0", pairs["Producer"]);
        Assert.Equal("Ayaan PDF", pairs["CreatorIfPdfium"]);
        Assert.Equal("D:20260918153000+06'30'", pairs["ModDate"]);
        Assert.Equal("2026-09-18T15:30:00+06:30", pairs["XmpDate"]);
        Assert.False(pairs.ContainsKey("Title"), "no edits, so no title is sent and the file's own stays");
    }

    [Fact]
    public void edits_go_with_the_stamp_including_a_removal_and_other_scripts()
    {
        var edits = new InfoEdits("गीता दर्शन", "အောင်ကိုကို", string.Empty, null);
        var pairs = NulFields.Pairs(DocumentInfoStamp.Pairs(edits, "p", "c", DateTimeOffset.Now));

        Assert.Equal("गीता दर्शन", pairs["Title"]);
        Assert.Equal("အောင်ကိုကို", pairs["Author"]);
        Assert.Equal(string.Empty, pairs["Subject"]);
        Assert.False(pairs.ContainsKey("Keywords"));
    }

    [Fact]
    public void a_nul_inside_a_value_cannot_shift_the_fields_after_it()
    {
        var fields = NulFields.Parse(NulFields.Join(new[] { "Title", "a\0b", "Author", "c" }));
        Assert.Equal(new[] { "Title", "ab", "Author", "c" }, fields);
    }

    // ---------------- What the core reports ----------------

    [Fact]
    public void the_cores_pairs_become_a_document_info()
    {
        byte[] buffer = NulFields.Join(new[]
        {
            "Title", "An Unlikely Prisoner", "Creator", "Microsoft Word",
            "CreationDate", "D:20250805143100+06'30'", "Version", "1.7", "Pages", "228",
            "PageWidth", "396.00", "PageHeight", "612.00", "Tagged", "1",
            "Security", "-1", "Permissions", "4294967295",
        });
        var info = DocumentInfo.FromPairs(NulFields.Pairs(buffer));

        Assert.Equal("An Unlikely Prisoner", info.Title);
        Assert.Equal(string.Empty, info.Author);
        Assert.Equal(228, info.PageCount);
        Assert.Equal(396, info.PageWidth);
        Assert.True(info.Tagged);
        Assert.False(info.IsEncrypted);
        Assert.Null(info.Modified);
        Assert.Equal(new DateTimeOffset(2025, 8, 5, 14, 31, 0, TimeSpan.FromMinutes(390)), info.Created);
    }

    // ---------------- How it is worded ----------------

    [Fact]
    public void sizes_read_the_way_explorer_says_them()
    {
        Assert.Equal("512 bytes", DocumentFacts.FileSize(512, English));
        Assert.Equal("840 KB", DocumentFacts.FileSize(840 * 1024, English));
        Assert.Equal("3.2 MB", DocumentFacts.FileSize((long)(3.2 * 1024 * 1024), English));
        Assert.Equal("197.9 MB", DocumentFacts.FileSize(207_512_000, English));
    }

    [Fact]
    public void a_standard_page_is_named()
    {
        Assert.Equal("8.27 × 11.69 in (A4)", DocumentFacts.PageSize(595.28, 841.89, English));
        Assert.Equal("11 × 8.5 in (Letter, landscape)", DocumentFacts.PageSize(792, 612, English));
        Assert.Equal("5.5 × 8.5 in", DocumentFacts.PageSize(396, 612, English));
        Assert.Equal(string.Empty, DocumentFacts.PageSize(0, 0, English));
    }

    [Fact]
    public void created_says_when_and_in_what()
    {
        var when = new DateTimeOffset(2025, 8, 5, 14, 31, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2025, 8, 5)));
        Assert.Equal("5 Aug 2025, 14:31 in Microsoft Word", DocumentFacts.Created(when, "Microsoft Word", English));
        Assert.Equal("5 Aug 2025, 14:31", DocumentFacts.Created(when, " ", English));
        Assert.Equal("In PDFium", DocumentFacts.Created(null, "PDFium", English));
        Assert.Equal("Unknown", DocumentFacts.Created(null, "", English));
    }

    [Fact]
    public void security_names_what_an_encrypted_file_forbids()
    {
        Assert.Equal("None", DocumentFacts.Security(-1, uint.MaxValue));
        Assert.Equal("Encrypted", DocumentFacts.Security(3, uint.MaxValue));

        uint noPrint = uint.MaxValue & ~(1u << 2);
        Assert.Equal("Encrypted. Printing is not allowed", DocumentFacts.Security(3, noPrint));

        uint none = uint.MaxValue & ~((1u << 2) | (1u << 3) | (1u << 4));
        Assert.Equal("Encrypted. Printing, copying and changes are not allowed", DocumentFacts.Security(4, none));
    }

    [Fact]
    public void fonts_are_read_four_fields_at_a_time_and_summed_up()
    {
        byte[] buffer = NulFields.Join(new[]
        {
            "Nirmala UI", "TrueType", "1", "1",
            "Helvetica", "Type 1", "0", "0",
            "Myanmar Text", "Composite", "1", "1",
        });
        var fonts = DocumentFacts.Fonts(buffer);

        Assert.Equal(3, fonts.Count);
        Assert.Equal("Nirmala UI (TrueType, embedded subset)", fonts[0].Describe());
        Assert.Equal("Helvetica (Type 1, not embedded)", fonts[1].Describe());
        Assert.Equal("3 fonts, 1 not embedded", DocumentFacts.FontsSummary(fonts));
        Assert.Equal("2 fonts, all embedded", DocumentFacts.FontsSummary(fonts.Where(f => f.Embedded).ToList()));
        Assert.Equal("1 font, not embedded", DocumentFacts.FontsSummary(fonts.Where(f => !f.Embedded).ToList()));
        Assert.Equal("None", DocumentFacts.FontsSummary(new List<FontFact>()));
    }

    [Fact]
    public void the_tab_choice_is_kept_per_file_whatever_the_case_of_its_path()
    {
        var on = TabTitles.Set(new List<string>(), @"D:\Books\Geeta.pdf", true);
        Assert.True(TabTitles.IsOn(on, @"d:\books\geeta.PDF"));
        Assert.False(TabTitles.IsOn(on, @"D:\Books\Other.pdf"));
        Assert.False(TabTitles.IsOn(on, null));

        Assert.Single(TabTitles.Set(on, @"D:\BOOKS\GEETA.PDF", true));
        Assert.Empty(TabTitles.Set(on, @"d:\books\geeta.pdf", false));
    }

    [Fact]
    public void fast_web_view_is_decided_by_the_first_kilobyte()
    {
        byte[] linearized = Encoding.ASCII.GetBytes("%PDF-1.6\n%\u00e2\n1 0 obj\n<</Linearized 1/L 2000>>\nendobj\n");
        Assert.True(FileFacts.IsLinearized(linearized));
        Assert.False(FileFacts.IsLinearized(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<</Type/Catalog>>")));

        byte[] late = new byte[2000];
        Encoding.ASCII.GetBytes("/Linearized").CopyTo(late, 1500);
        Assert.False(FileFacts.IsLinearized(late), "a mention deep in the file is not the linearization dictionary");
    }

    // ---------------- Alt+Enter ----------------

    [Fact]
    public void alt_enter_opens_the_properties_but_not_from_a_text_field()
    {
        Assert.Equal(EditorCommand.DocumentProperties,
            KeyboardCommands.Resolve(KeyboardCommands.KeyEnter, false, false, false, alt: true));
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyEnter, false, false, true, alt: true));
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyEnter, false, false, false, alt: false));

        // Ctrl+D, which Acrobat uses for this, stays Duplicate.
        Assert.Equal(EditorCommand.Duplicate,
            KeyboardCommands.Resolve(KeyboardCommands.KeyD, true, false, false, alt: false));
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
    public void the_file_menu_has_it_with_its_chord_and_the_chord_runs_it()
    {
        string xaml = Read("PdfEditorApp", "MainPage.xaml");
        int at = xaml.IndexOf("<MenuFlyoutItem x:Name=\"DocumentPropertiesItem\" Text=\"Document properties...\"", StringComparison.Ordinal);
        Assert.True(at > 0, "File > Document properties is gone");
        string item = xaml[at..xaml.IndexOf("</MenuFlyoutItem>", at, StringComparison.Ordinal)];
        Assert.Contains("Click=\"DocumentProperties_Click\"", item, StringComparison.Ordinal);
        Assert.Contains("<KeyboardAccelerator Modifiers=\"Menu\" Key=\"Enter\" />", item, StringComparison.Ordinal);

        int file = xaml.IndexOf("<MenuBarItem Title=\"File\"", StringComparison.Ordinal);
        int edit = xaml.IndexOf("<MenuBarItem Title=\"Edit\"", StringComparison.Ordinal);
        Assert.True(file < at && at < edit, "it belongs in the File menu");

        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        Assert.Contains("case EditorCommand.DocumentProperties: DocumentProperties_Click(this, null!); break;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void every_save_stamps_the_file_after_the_gradients_and_before_it_replaces_the_original()
    {
        // On the temporary copy, so an in-place save replaces the original
        // once, with everything already in it.
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        string save = Body(vm, "public bool SaveDocumentAs(string path, bool flatten)");

        int gradients = save.IndexOf("WriteGradientFills(writePath);", StringComparison.Ordinal);
        int stamp = save.IndexOf("WriteDocumentInfo(writePath);", StringComparison.Ordinal);
        int swap = save.IndexOf("File.Move(writePath, path, overwrite: true);", StringComparison.Ordinal);
        Assert.True(gradients > 0 && stamp > gradients && swap > stamp,
            $"gradients {gradients}, stamp {stamp}, swap {swap}");

        string write = Body(vm, "private void WriteDocumentInfo(string path)");
        Assert.Contains("DocumentInfoStamp.Pairs(_infoEdits, AppInfo.Producer, AppInfo.Name, DateTimeOffset.Now)", write, StringComparison.Ordinal);
        Assert.Contains("RenderCoreNative.write_document_info(path, data, (nuint)data.Length)", write, StringComparison.Ordinal);
    }

    [Fact]
    public void files_made_other_ways_are_stamped_too()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        Assert.Contains("StampWrittenFile(path);", Body(vm, "public bool ExtractPagesToFile("), StringComparison.Ordinal);

        string merge = Read("PdfEditorApp", "MergeFilesWindow.xaml.cs");
        int saved = merge.IndexOf("RenderCoreNative.save_document(merged, written)", StringComparison.Ordinal);
        int stamped = merge.IndexOf("ViewportViewModel.StampWrittenFile(written)", StringComparison.Ordinal);
        int outlined = merge.IndexOf("RenderCoreNative.write_outline(written, outlined", StringComparison.Ordinal);
        Assert.True(saved > 0 && stamped > saved && outlined > stamped, $"{saved} {stamped} {outlined}");
    }

    [Fact]
    public void edits_wait_for_a_save_and_belong_to_the_document_they_were_made_in()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        string apply = Body(vm, "public void ApplyInfoEdits(InfoEdits edits)");
        Assert.Contains("IsDirty = true;", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("write_document_info", apply, StringComparison.Ordinal);

        string open = vm[vm.IndexOf("public DocumentOpenOutcome OpenDocument(", StringComparison.Ordinal)..];
        open = open[..open.IndexOf("return DocumentOpenOutcome.Opened;", StringComparison.Ordinal)];
        Assert.Contains("_infoEdits = null;", open, StringComparison.Ordinal);
        Assert.Contains("_fileTitle = ReadDocumentInfo()?.Title;", open, StringComparison.Ordinal);

        Assert.Contains(".With(_infoEdits)", Body(vm, "public DocumentInfo? ReadDocumentInfo()"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_dialog_writes_nothing_itself_and_an_encrypted_file_is_read_only()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        string dialog = Body(code, "private async void DocumentProperties_Click(");

        Assert.Contains("bool locked = info.IsEncrypted;", dialog, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly = locked", dialog, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ApplyInfoEdits(InfoEdits.Between(info, title.Text, author.Text, subject.Text, keywords.Text));", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("write_document_info", dialog, StringComparison.Ordinal);
        Assert.Contains("TabTitles.Set(s.TitleInTabPaths, path, on)", dialog, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ListFontsAsync()", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void the_tab_shows_the_title_only_where_asked_and_only_when_there_is_one()
    {
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        Assert.Contains("TitleInTab && (_infoEdits?.Title ?? _fileTitle) is { Length: > 0 } title ? title : DocumentTitle;", vm, StringComparison.Ordinal);

        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        Assert.Contains("ViewModel.TitleInTab = TabTitles.IsOn(SettingsStore.Current.TitleInTabPaths, ViewModel.DocumentPath);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_core_calls_are_declared_as_the_core_exports_them()
    {
        string interop = Read("PdfEditorApp", "Interop", "RenderCoreNative.cs");
        Assert.Contains("public static extern ByteBuffer get_document_properties(ulong docHandle);", interop, StringComparison.Ordinal);
        Assert.Contains("public static extern int write_document_info(", interop, StringComparison.Ordinal);
        Assert.Contains("public static extern ByteBuffer list_document_fonts(", interop, StringComparison.Ordinal);

        string core = Read("render_core", "src", "docinfo.rs");
        Assert.Contains("pub extern \"C\" fn get_document_properties(doc_handle: u64) -> ByteBuffer", core, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn write_document_info(path: *const c_char, data: *const u8, len: usize) -> i32", core, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn list_document_fonts(path: *const c_char) -> ByteBuffer", core, StringComparison.Ordinal);
    }
}
