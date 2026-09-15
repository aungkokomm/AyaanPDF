using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The menu bar across the top of a document.
/// </summary>
/// <remarks>
/// ⚠️ EVERY COMMAND USED TO SIT BEHIND ONE "Menu" BUTTON at the foot of the
/// tool rail, opening sideways, which is not where anyone looks for File or
/// Edit. These hold the move down: the bar is in its own row, nothing else is
/// in that row, the old button is gone, and no command was lost on the way.
/// </remarks>
public class MenuBarWiringTests
{
    private static string Source(params string[] relative)
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

    private static string Xaml() => Source("PdfEditorApp", "MainPage.xaml");

    private static string Code() => Source("PdfEditorApp", "MainPage.xaml.cs");

    private static string MenuBar()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("<MenuBar x:Name=\"AppMenuBar\"", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no menu bar");
        int end = xaml.IndexOf("</MenuBar>", at, StringComparison.Ordinal);
        return xaml[at..end];
    }

    [Fact]
    public void the_bar_is_the_top_row_across_every_column()
    {
        string xaml = Xaml();
        string bar = MenuBar();

        Assert.Contains("Grid.Row=\"0\"", bar[..bar.IndexOf('>')], StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"4\"", bar[..bar.IndexOf('>')], StringComparison.Ordinal);

        int root = xaml.IndexOf("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal);
        int rows = xaml.IndexOf("<Grid.RowDefinitions>", root, StringComparison.Ordinal);
        Assert.True(rows > root && rows < xaml.IndexOf("<MenuBar", StringComparison.Ordinal),
            "the root grid has no rows for the bar to sit in");
        string defs = xaml[rows..xaml.IndexOf("</Grid.RowDefinitions>", rows, StringComparison.Ordinal)];
        Assert.Contains("<RowDefinition Height=\"Auto\" />", defs, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"*\" />", defs, StringComparison.Ordinal);
    }

    [Fact]
    public void the_menus_are_the_ones_people_look_for_in_order()
    {
        var titles = Regex.Matches(MenuBar(), "<MenuBarItem Title=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(new[] { "File", "Edit", "View", "Page", "Help" }, titles);
    }

    /// <summary>
    /// ⚠️ A GRID CHILD WITH NO ROW IS IN ROW ZERO, which is now the bar's.
    /// The rail, the rulers, the page and the status bar would all have been
    /// squeezed into a row the height of a menu.
    /// </summary>
    [Fact]
    public void everything_else_on_the_page_is_in_the_content_row()
    {
        string[] lines = Xaml().Split('\n');
        int root = Array.FindIndex(lines, l => l.Contains("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal));
        Assert.True(root > 0);

        var stray = lines
            .Select((line, i) => (line, i))
            .Skip(root + 1)
            .Where(x => Regex.IsMatch(x.line, @"^        <[A-Za-z][A-Za-z0-9:]*(?=[\s>/]|$)"))
            .Where(x => !Regex.IsMatch(x.line, @"^        <[A-Za-z][A-Za-z0-9:]*\."))
            .Where(x => !x.line.Contains("<MenuBar ", StringComparison.Ordinal))
            .Where(x => !x.line.Contains("Grid.Row=\"1\"", StringComparison.Ordinal))
            .Select(x => $"line {x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(stray.Count == 0, "not in the content row: " + string.Join("; ", stray));
    }

    [Fact]
    public void the_rail_no_longer_hides_the_menu()
    {
        Assert.DoesNotContain("ToolTipService.ToolTip=\"Menu\"", Xaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("<MenuFlyout Placement=\"Right\">", Xaml(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("New_Click")]
    [InlineData("OpenFile_Click")]
    [InlineData("Save_Click")]
    [InlineData("SaveAs_Click")]
    [InlineData("FlattenSaveAs_Click")]
    [InlineData("Print_Click")]
    [InlineData("Settings_Click")]
    [InlineData("CloseDocument_Click")]
    [InlineData("Exit_Click")]
    [InlineData("Undo_Click")]
    [InlineData("Redo_Click")]
    [InlineData("FindToggle_Click")]
    [InlineData("Group_Click")]
    [InlineData("Ungroup_Click")]
    [InlineData("ToggleThumbnails_Click")]
    [InlineData("ToggleBookmarks_Click")]
    [InlineData("ZoomFitPage_Click")]
    [InlineData("FullScreen_Click")]
    [InlineData("NightModeToggle_Click")]
    [InlineData("RotateViewCw_Click")]
    [InlineData("ResetViewRotation_Click")]
    [InlineData("ContinuousView_Click")]
    [InlineData("RulersToggle_Click")]
    [InlineData("RulerUnit_Click")]
    [InlineData("InsertFromFile_Click")]
    [InlineData("InsertBlankPage_Click")]
    [InlineData("ExtractPagesMenu_Click")]
    [InlineData("RotatePage_Click")]
    [InlineData("RotatePagesMenu_Click")]
    [InlineData("DeletePage_Click")]
    [InlineData("About_Click")]
    public void no_command_was_lost_on_the_way(string handler)
    {
        Assert.Contains($"Click=\"{handler}\"", MenuBar(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_submenus_filled_from_code_are_still_there_once()
    {
        string bar = MenuBar();
        Assert.Contains("<MenuFlyoutSubItem x:Name=\"RecentMenu\"", bar, StringComparison.Ordinal);
        Assert.Contains("<MenuFlyoutSubItem x:Name=\"SignatureMenu\"", bar, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(Xaml(), "x:Name=\"RecentMenu\"").Count);
        Assert.Equal(1, Regex.Matches(Xaml(), "x:Name=\"SignatureMenu\"").Count);
    }

    [Fact]
    public void full_screen_takes_the_bar_away_and_the_theme_paints_it()
    {
        string code = Code();

        int presenting = code.IndexOf("public void SetPresenting(bool presenting)", StringComparison.Ordinal);
        Assert.True(presenting > 0, "SetPresenting is gone");
        Assert.Contains("AppMenuBar.Visibility = presenting ? Visibility.Collapsed : Visibility.Visible;",
            code[presenting..(presenting + 2000)], StringComparison.Ordinal);

        Assert.Contains("AppMenuBar.Background = chrome;", code, StringComparison.Ordinal);
    }
}
