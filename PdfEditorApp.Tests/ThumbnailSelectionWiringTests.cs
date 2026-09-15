using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Several pages chosen in the thumbnails at once, with Ctrl+click and
/// Shift+click, for the page menu and for dragging.
/// </summary>
/// <remarks>
/// The list allowed one page, and its selection was bound to the page being
/// read, so a Shift+click only jumped to the page clicked. Found by the user.
/// </remarks>
public class ThumbnailSelectionWiringTests
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
    private static string Xaml() => Read("PdfEditorApp", "MainPage.xaml");

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

    private static string ListTag()
    {
        var tag = Regex.Match(Xaml(), @"<ListView\s[^>]*x:Name=""ThumbnailList""[^>]*>", RegexOptions.Singleline);
        Assert.True(tag.Success, "ThumbnailList was not found in MainPage.xaml");
        return tag.Value;
    }

    [Fact]
    public void several_pages_can_be_chosen_in_the_thumbnails()
    {
        string tag = ListTag();

        Assert.Contains("SelectionMode=\"Extended\"", tag, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"ThumbnailList_SelectionChanged\"", tag, StringComparison.Ordinal);

        // A binding to the page being read would throw a choice of several away
        // on every scroll step.
        Assert.DoesNotContain("SelectedIndex=", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void the_list_follows_the_page_being_read_without_navigating_or_dropping_a_choice()
    {
        string sync = MethodBody(PageCode(), "private void SyncThumbnailSelection(");
        Assert.True(IndexIn(sync, "ThumbnailList.SelectedItems.Count > 1") < IndexIn(sync, "ThumbnailList.SelectedIndex = index"));
        Assert.True(IndexIn(sync, "_syncingThumbnailSelection = true") < IndexIn(sync, "ThumbnailList.SelectedIndex = index"));

        string changed = MethodBody(PageCode(), "private void ThumbnailList_SelectionChanged(");
        Assert.True(IndexIn(changed, "_syncingThumbnailSelection") < IndexIn(changed, "ViewModel.GoToPage("));
        Assert.Contains("ThumbnailList.SelectedItems.Count != 1", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void menu_commands_act_on_every_chosen_page_when_the_page_clicked_is_one_of_them()
    {
        Assert.Contains("chosen.Contains(page)", MethodBody(PageCode(), "private List<int> ChosenPagesWith("), StringComparison.Ordinal);

        Assert.Contains("ViewModel.RotatePages(pages, 90)", MethodBody(PageCode(), "private void PageRotate_Click("), StringComparison.Ordinal);
        Assert.Contains("ViewModel.DuplicatePages(pages)", MethodBody(PageCode(), "private void PageDuplicate_Click("), StringComparison.Ordinal);
        Assert.Contains("ViewModel.MovePages(pages, -1)", MethodBody(PageCode(), "private void PageMoveUp_Click("), StringComparison.Ordinal);
        Assert.Contains("ViewModel.MovePages(pages, 1)", MethodBody(PageCode(), "private void PageMoveDown_Click("), StringComparison.Ordinal);
        Assert.Contains("ViewModel.DeletePages(pages)", MethodBody(PageCode(), "private void PageDelete_Click("), StringComparison.Ordinal);
        Assert.Contains("ExtractChosenPages(pages)", MethodBody(PageCode(), "private async void PageExtract_Click("), StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_says_how_many_pages_it_will_act_on()
    {
        Assert.Contains("<MenuFlyout Opening=\"PageMenu_Opening\">", Xaml(), StringComparison.Ordinal);
        foreach (string command in new[] { "Rotate", "Duplicate", "Extract", "MoveUp", "MoveDown", "Delete" })
        {
            Assert.Contains($"Tag=\"{command}\"", Xaml(), StringComparison.Ordinal);
        }

        Assert.Contains("$\"Delete {n} pages\"", MethodBody(PageCode(), "private void PageMenu_Opening("), StringComparison.Ordinal);
    }
}
