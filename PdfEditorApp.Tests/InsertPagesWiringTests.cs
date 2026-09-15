using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Page > Insert > From file, as wired: a window that shows the file's pages
/// and inserts only the ones chosen, where they were asked to go.
/// </summary>
/// <remarks>
/// The window cannot be loaded here, so what is checked is the shape that
/// keeps it fast on a large book and honest about what it inserts.
/// </remarks>
public class InsertPagesWiringTests
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

    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string ViewModelCode() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string WindowCode() => Read("PdfEditorApp", "PagePickerWindow.xaml.cs");
    private static string WindowXaml() => Read("PdfEditorApp", "PagePickerWindow.xaml");

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
    public void insert_from_file_shows_the_pages_rather_than_inserting_all_of_them()
    {
        string handler = MethodBody(PageCode(), "private async void InsertFromFile_Click(");

        Assert.True(IndexIn(handler, "SourceDocument.OpenAsync(") < IndexIn(handler, "PagePickerWindow.ForInsert("));
        Assert.DoesNotContain("InsertPagesFromFile", PageCode(), StringComparison.Ordinal);
        Assert.DoesNotContain("InsertPagesFromFile", ViewModelCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_pages_are_copied_from_the_open_file_and_marks_below_move_down()
    {
        string insert = MethodBody(ViewModelCode(), "public bool InsertPagesFromDocument(");

        Assert.True(IndexIn(insert, "PushHistory(") < IndexIn(insert, "RenderCoreNative.insert_pages_from_document("));
        Assert.Contains("MapOverlayPages(", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void the_picker_is_a_window_that_resizes_not_a_dialog_that_stops_at_548()
    {
        Assert.Contains("class PagePickerWindow : Window", WindowCode(), StringComparison.Ordinal);
        Assert.DoesNotContain("<ContentDialog", WindowXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_window_is_sized_to_the_screen_so_its_buttons_are_never_below_the_taskbar()
    {
        string ctor = MethodBody(WindowCode(), "private PagePickerWindow(");

        Assert.True(IndexIn(ctor, "DisplayArea.GetFromWindowId(") < IndexIn(ctor, "AppWindow.MoveAndResize("));
        Assert.Contains("WorkArea", ctor, StringComparison.Ordinal);
    }

    [Fact]
    public void where_the_pages_go_is_asked_at_the_top_beside_the_preview()
    {
        string xaml = WindowXaml();

        // A dropdown in the bottom corner was not found in the first test.
        Assert.Contains("<RadioButtons x:Name=\"PositionChoice\"", xaml, StringComparison.Ordinal);
        Assert.True(IndexIn(xaml, "x:Name=\"PositionPanel\"") < IndexIn(xaml, "x:Name=\"PreviewImage\""));
        Assert.DoesNotContain("<ComboBox", xaml, StringComparison.Ordinal);

        Assert.Contains("PositionChoice.SelectedIndex switch", MethodBody(WindowCode(), "private void Confirm_Click("), StringComparison.Ordinal);
    }

    [Fact]
    public void thumbnails_are_drawn_off_the_ui_thread_only_for_pages_on_screen()
    {
        string realize = MethodBody(WindowCode(), "private void PageGrid_ContainerContentChanging(");
        Assert.Contains("args.InRecycleQueue", realize, StringComparison.Ordinal);
        Assert.Contains("page.Bitmap = null", realize, StringComparison.Ordinal);

        string render = MethodBody(WindowCode(), "private async void RenderThumbnail(");
        Assert.Contains("Task.Run(", render, StringComparison.Ordinal);
        Assert.Contains("page.IsRealized", render, StringComparison.Ordinal);

        // ⚠️ Every page's size, read up front, froze scrolling for 40 s on a book.
        Assert.DoesNotContain("get_page_sizes", WindowCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void typed_and_clicked_pages_go_through_one_reader_and_select_a_run_at_a_time()
    {
        Assert.Contains("SelectionMode=\"Extended\"", WindowXaml(), StringComparison.Ordinal);

        string select = MethodBody(WindowCode(), "private void SelectPages(");
        Assert.True(IndexIn(select, "PageSelection.Runs(") < IndexIn(select, "PageGrid.SelectRange("));
        Assert.True(IndexIn(select, "_syncing = true") < IndexIn(select, "PageGrid.SelectRange("));

        Assert.Contains("PageSelection.Format(", MethodBody(WindowCode(), "private void PageGrid_SelectionChanged("), StringComparison.Ordinal);
        Assert.Contains("PageSelection.TryParse(", MethodBody(WindowCode(), "private void Confirm_Click("), StringComparison.Ordinal);
    }

    [Fact]
    public void the_file_the_pages_came_from_is_closed_with_the_window()
    {
        Assert.Contains("_source.Dispose()", MethodBody(WindowCode(), "private PagePickerWindow("), StringComparison.Ordinal);
        Assert.Contains("ownsSource: true", WindowCode(), StringComparison.Ordinal);
    }
}
