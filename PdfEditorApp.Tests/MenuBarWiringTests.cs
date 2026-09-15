using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The menu bar, File to Help, in the window's title bar.
/// </summary>
/// <remarks>
/// ⚠️ EVERY COMMAND USED TO SIT BEHIND ONE "Menu" BUTTON at the foot of the
/// tool rail, opening sideways, which is not where anyone looks for File or
/// Edit. It then spent one build as a row under the tab, which made the app's
/// menu look like part of one document. These hold it in the title bar beside
/// the icon: declared per page, shown by the window for the tab in front, and
/// handing the keyboard back to the page once used.
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

    private static string WindowXaml() => Source("PdfEditorApp", "MainWindow.xaml");

    private static string WindowCode() => Source("PdfEditorApp", "MainWindow.xaml.cs");

    private static string MenuBar()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("<MenuBar x:Name=\"AppMenuBar\"", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no menu bar");
        int end = xaml.IndexOf("</MenuBar>", at, StringComparison.Ordinal);
        return xaml[at..end];
    }

    private static string MenuBarTag()
    {
        string bar = MenuBar();
        return bar[..bar.IndexOf('>')];
    }

    /// <summary>From a member's signature to the end of its body.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        int end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return code[at..end];
    }

    private static string Line(string code, string start)
    {
        int at = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at > 0, $"{start} is gone");
        return code[at..code.IndexOf('\n', at)];
    }

    [Fact]
    public void the_menu_is_shown_in_the_title_bar_beside_the_icon()
    {
        string window = WindowXaml();
        int bar = window.IndexOf("<Grid x:Name=\"AppTitleBar\"", StringComparison.Ordinal);
        int icon = window.IndexOf("ms-appx:///Assets/AppIcon.ico", StringComparison.Ordinal);
        int host = window.IndexOf("<Border x:Name=\"MenuHost\" Grid.Column=\"1\" />", StringComparison.Ordinal);
        int quick = window.IndexOf("<Button x:Name=\"QuickOpen\"", StringComparison.Ordinal);
        int drag = window.IndexOf("<Border x:Name=\"TitleDragArea\"", StringComparison.Ordinal);
        int tabs = window.IndexOf("<TabView x:Name=\"Tabs\"", StringComparison.Ordinal);

        Assert.True(bar > 0 && icon > bar && host > icon && quick > host && drag > quick && tabs > drag,
            "the title bar should read icon, menu, quick actions, then the drag area, all above the tabs");

        // ⚠️ Not inside the drag area: that region swallows clicks.
        string dragArea = window[drag..window.IndexOf("</Border>", drag, StringComparison.Ordinal)];
        Assert.DoesNotContain("MenuHost", dragArea, StringComparison.Ordinal);
        Assert.Contains("SetTitleBar(TitleDragArea);", WindowCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void each_page_declares_its_menu_and_hands_it_to_the_window()
    {
        string code = Code();
        int ctor = code.IndexOf("public MainPage()", StringComparison.Ordinal);
        Assert.True(ctor > 0);
        Assert.Contains("RootGrid.Children.Remove(AppMenuBar);", code[ctor..(ctor + 800)], StringComparison.Ordinal);
        Assert.Contains("public MenuBar Menu => AppMenuBar;", code, StringComparison.Ordinal);

        Assert.Contains("private void ShowMenuOf(MainPage? page) => MenuHost.Child = page?.Menu;",
            WindowCode(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ A MENU LEFT BEHIND ACTS ON THE WRONG DOCUMENT. Every place the front
    /// tab can change has to bring that tab's menu, and closing a tab must not
    /// leave its menu in the title bar.
    /// </summary>
    [Fact]
    public void whichever_tab_is_in_front_brings_its_own_menu()
    {
        string code = WindowCode();
        Assert.Contains("ShowMenuOf(ActivePage);", Body(code, "private void Tabs_SelectionChanged("), StringComparison.Ordinal);
        Assert.Contains("ShowMenuOf(page);", Body(code, "public MainPage AddDocumentTab("), StringComparison.Ordinal);
        Assert.Contains("ShowMenuOf(ActivePage);", Body(code, "private async Task CloseTab("), StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_keeps_no_row_for_it()
    {
        string xaml = Xaml();
        int root = xaml.IndexOf("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal);
        int menu = xaml.IndexOf("<MenuBar x:Name=\"AppMenuBar\"", StringComparison.Ordinal);
        Assert.True(root > 0 && menu > root);
        Assert.DoesNotContain("<Grid.RowDefinitions>", xaml[root..menu], StringComparison.Ordinal);

        string[] lines = xaml.Split('\n');
        int rootLine = Array.FindIndex(lines, l => l.Contains("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal));
        var rowed = lines
            .Select((line, i) => (line, i))
            .Skip(rootLine + 1)
            .Where(x => Regex.IsMatch(x.line, @"^        <[A-Za-z]"))
            .Where(x => x.line.Contains("Grid.Row=", StringComparison.Ordinal))
            .Select(x => $"line {x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(rowed.Count == 0, "still placed in a row: " + string.Join("; ", rowed));

        Assert.DoesNotContain("Grid.", MenuBarTag(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The stock bar asks for 40 DIP and the title bar is 34, and its own
    /// paint would sit as a block on the window's colour.
    /// </summary>
    [Fact]
    public void the_bar_fits_the_title_bar_and_lets_the_window_colour_through()
    {
        string tag = MenuBarTag();
        Assert.Contains("MinHeight=\"0\"", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("AppMenuBar.Background", Code(), StringComparison.Ordinal);

        Assert.Equal(5, Regex.Matches(MenuBar(), "<MenuBarItem Title=\"[^\"]+\" Margin=\"2,3,2,3\">").Count);
        Assert.Contains("private const double TitleBarHeight = 34;", WindowCode(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ OUTSIDE RootGrid, A FOCUSED MENU TITLE KEEPS EVERY KEY FROM THE PAGE.
    /// Inside the page the keys bubbled up to RootGrid_KeyDown anyway; in the
    /// title bar they do not, so after one click on File the shortcuts and
    /// typing into a line being edited would go dead.
    /// </summary>
    [Fact]
    public void using_the_menu_hands_the_keyboard_back_to_the_page()
    {
        Assert.Contains("GotFocus=\"AppMenuBar_GotFocus\"", MenuBarTag(), StringComparison.Ordinal);

        string body = Body(Code(), "private void AppMenuBar_GotFocus(");
        Assert.Contains("is MenuBarItem { FocusState: not FocusState.Keyboard }", body, StringComparison.Ordinal);
        Assert.Contains("RootGrid.Focus(FocusState.Programmatic);", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same starvation as the menu titles: a quick button in the title
    /// bar that takes focus on a click keeps every key from the page, so
    /// Ctrl+Z straight after clicking Undo did nothing.
    /// </summary>
    [Theory]
    [InlineData("QuickOpen")]
    [InlineData("QuickSave")]
    [InlineData("QuickUndo")]
    [InlineData("QuickRedo")]
    public void the_quick_buttons_leave_the_keyboard_with_the_page(string name)
    {
        string window = WindowXaml();
        int at = window.IndexOf($"<Button x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at > 0, $"{name} is gone");
        Assert.Contains("AllowFocusOnInteraction=\"False\"", window[at..window.IndexOf('>', at)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_menus_are_the_ones_people_look_for_in_order()
    {
        var titles = Regex.Matches(MenuBar(), "<MenuBarItem Title=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(new[] { "File", "Edit", "View", "Page", "Help" }, titles);
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
    [InlineData("Cut_Click")]
    [InlineData("Copy_Click")]
    [InlineData("Paste_Click")]
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
    [InlineData("ClearGuidesPage_Click")]
    [InlineData("ClearGuidesAll_Click")]
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

    /// <summary>
    /// ⚠️ TEXT ONLY. The chords are cases in the page's key handler; an
    /// accelerator on the item would go live once Edit had been opened and
    /// take Ctrl+C from every text field.
    /// </summary>
    [Theory]
    [InlineData("Cut", "Ctrl+X")]
    [InlineData("Copy", "Ctrl+C")]
    [InlineData("Paste", "Ctrl+V")]
    public void cut_copy_and_paste_are_in_edit_and_print_their_chord_without_taking_it(string text, string chord)
    {
        string bar = MenuBar();
        int at = bar.IndexOf($"<MenuFlyoutItem Text=\"{text}\" ", StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {text} in the menu");

        int edit = bar.IndexOf("<MenuBarItem Title=\"Edit\"", StringComparison.Ordinal);
        int view = bar.IndexOf("<MenuBarItem Title=\"View\"", StringComparison.Ordinal);
        Assert.True(at > edit && at < view, $"{text} is not under Edit");

        string item = bar[at..bar.IndexOf("</MenuFlyoutItem>", at, StringComparison.Ordinal)];
        Assert.Contains($"KeyboardAcceleratorTextOverride=\"{chord}\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("<KeyboardAccelerator ", item, StringComparison.Ordinal);
    }

    [Fact]
    public void cut_copy_and_paste_do_what_the_chords_do()
    {
        string code = Code();

        Assert.Contains("ViewModel.CutSelectedAnnotations()", Line(code, "private void Cut_Click("), StringComparison.Ordinal);

        Assert.Contains("if (!ViewModel.CopySelectedAnnotations()) { CopySelectedText(); }",
            Body(code, "private void Copy_Click("), StringComparison.Ordinal);

        string paste = Body(code, "private void Paste_Click(");
        Assert.Contains("ViewModel.IsEditingInPlace", paste, StringComparison.Ordinal);
        Assert.Contains("PasteIntoInPlaceEdit();", paste, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PasteAnnotations();", paste, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clearing guides was only on the ruler's right-click menu, which is out
    /// of reach while the rulers are hidden.
    /// </summary>
    [Fact]
    public void view_has_guides_with_both_ways_to_clear_them()
    {
        string bar = MenuBar();
        int view = bar.IndexOf("<MenuBarItem Title=\"View\"", StringComparison.Ordinal);
        int page = bar.IndexOf("<MenuBarItem Title=\"Page\"", StringComparison.Ordinal);
        int guides = bar.IndexOf("<MenuFlyoutSubItem Text=\"Guides\">", StringComparison.Ordinal);
        Assert.True(guides > view && guides < page, "there is no Guides submenu under View");

        string submenu = bar[guides..bar.IndexOf("</MenuFlyoutSubItem>", guides, StringComparison.Ordinal)];
        Assert.Contains("Click=\"ClearGuidesPage_Click\"", submenu, StringComparison.Ordinal);
        Assert.Contains("Click=\"ClearGuidesAll_Click\"", submenu, StringComparison.Ordinal);
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
    public void full_screen_takes_the_bar_away()
    {
        string code = Code();

        int presenting = code.IndexOf("public void SetPresenting(bool presenting)", StringComparison.Ordinal);
        Assert.True(presenting > 0, "SetPresenting is gone");
        Assert.Contains("AppMenuBar.Visibility = presenting ? Visibility.Collapsed : Visibility.Visible;",
            code[presenting..(presenting + 2000)], StringComparison.Ordinal);

        Assert.Contains("AppTitleBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;",
            WindowCode(), StringComparison.Ordinal);
    }
}
