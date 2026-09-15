using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// File > Merge files, as wired: PDFs and pictures in the order listed, into a
/// new file built in memory, written, given its bookmarks, and opened in a tab.
/// </summary>
/// <remarks>
/// The window cannot be loaded here. The core half, a new document taking
/// chosen pages and pictures and saving them, is proved in render_core's
/// a_new_document_takes_chosen_pages_from_several_places_and_saves_them and
/// a_picture_becomes_a_page_of_its_own_drawn_as_large_as_fits.
/// </remarks>
public class MergeFilesWiringTests
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

    private static string Code() => Read("PdfEditorApp", "MergeFilesWindow.xaml.cs");
    private static string Xaml() => Read("PdfEditorApp", "MergeFilesWindow.xaml");

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
    public void merge_files_is_on_the_file_menu_and_opens_its_own_window()
    {
        string menu = Read("PdfEditorApp", "MainPage.xaml");
        int file = IndexIn(menu, "<MenuBarItem Title=\"File\"");
        int item = IndexIn(menu, "<MenuFlyoutItem Text=\"Merge files...\" Click=\"MergeFiles_Click\" />");
        int next = menu.IndexOf("<MenuBarItem ", file + 1, StringComparison.Ordinal);
        Assert.True(file < item && item < next, "Merge files is not under File");

        Assert.Contains("new MergeFilesWindow()",
            MethodBody(Read("PdfEditorApp", "MainPage.xaml.cs"), "private void MergeFiles_Click("), StringComparison.Ordinal);
        Assert.Contains("class MergeFilesWindow : Window", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("<ContentDialog", Xaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void pdfs_and_pictures_come_in_by_picking_or_by_dropping()
    {
        Assert.Contains("ImagePages.Extensions", MethodBody(Code(), "private async void AddFiles_Click("), StringComparison.Ordinal);
        Assert.Contains("PickMultipleFilesAsync()", MethodBody(Code(), "private async void AddFiles_Click("), StringComparison.Ordinal);
        Assert.Contains("Drop=\"Files_Drop\"", Xaml(), StringComparison.Ordinal);
        Assert.Contains("AddPaths(", MethodBody(Code(), "private async void Files_Drop("), StringComparison.Ordinal);
    }

    [Fact]
    public void a_file_that_needs_attention_stops_the_merge_before_anything_is_written()
    {
        string merge = MethodBody(Code(), "private async void Merge_Click(");

        int asked = IndexIn(merge, "PickSaveFileAsync()");
        Assert.True(IndexIn(merge, "i.HasProblem") < asked);
        Assert.True(IndexIn(merge, "PageSelection.TryParse(") < asked);
    }

    [Fact]
    public void the_merged_file_is_built_new_written_given_bookmarks_then_moved_into_place()
    {
        string build = MethodBody(Code(), "private static async Task<string?> MergeAsync(");

        Assert.True(IndexIn(build, "RenderCoreNative.create_document()") < IndexIn(build, "RenderCoreNative.insert_pages_from_document("));
        Assert.True(IndexIn(build, "RenderCoreNative.save_document(") < IndexIn(build, "RenderCoreNative.write_outline("));
        Assert.True(IndexIn(build, "RenderCoreNative.write_outline(") < IndexIn(build, "File.Move("));
        Assert.Contains("MergePlan.Outline(", build, StringComparison.Ordinal);

        Assert.True(IndexIn(MethodBody(Code(), "private async void Merge_Click("), "await MergeAsync(")
            < IndexIn(MethodBody(Code(), "private async void Merge_Click("), "AddDocumentTab(file.Path)"));
    }

    [Fact]
    public void a_jpeg_goes_in_as_its_own_bytes_only_when_it_needs_no_turning()
    {
        string add = MethodBody(Code(), "private static async Task<bool> AddPicturePageAsync(");

        Assert.True(IndexIn(add, "picture.IsUprightJpeg") < IndexIn(add, "RenderCoreNative.insert_jpeg_page("));
        Assert.True(IndexIn(add, "RenderCoreNative.insert_jpeg_page(") < IndexIn(add, "ImagePages.DecodeAsync("));
        Assert.Contains("ExifOrientationMode.RespectExifOrientation", Read("PdfEditorApp", "ImagePages.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_window_cannot_be_closed_out_from_under_a_merge()
    {
        string ctor = MethodBody(Code(), "public MergeFilesWindow(");

        Assert.True(IndexIn(ctor, "AppWindow.Closing +=") < IndexIn(ctor, "args.Cancel = true;"));
        Assert.Contains("item.Dispose()", ctor, StringComparison.Ordinal);
    }

    [Fact]
    public void choose_pages_reuses_the_page_picker_and_sort_numbers_names_the_way_people_do()
    {
        Assert.Contains("PagePickerWindow.ForChoosing(", MethodBody(Code(), "private void ChoosePages_Click("), StringComparison.Ordinal);
        Assert.Contains("NaturalOrder.Instance", MethodBody(Code(), "private void SortByName_Click("), StringComparison.Ordinal);
    }
}
