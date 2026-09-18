using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The status bar, docked along the bottom of the page instead of floating
/// over it, with View > Status bar to hide it, and find moved out of it.
/// </summary>
/// <remarks>
/// It was a pill over the canvas, draggable to six anchors, and a bar over the
/// page covers the page. The window cannot be loaded here, so these hold the
/// wiring that makes it a docked bar.
/// </remarks>
public class StatusBarDockTests
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

    private static string Xaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string Code() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string WindowXaml() => Read("PdfEditorApp", "MainWindow.xaml");
    private static string WindowCode() => Read("PdfEditorApp", "MainWindow.xaml.cs");
    private static string Settings() => Read("PdfEditorApp.Viewport", "AppSettings.cs");

    /// <summary>From a member's signature to the end of its body.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return code[at..code.IndexOf("\n    }\n", at, StringComparison.Ordinal)];
    }

    /// <summary>An expression-bodied member, to its semicolon.</summary>
    private static string Statement(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return code[at..code.IndexOf(";\n", at, StringComparison.Ordinal)];
    }

    private static string StatusBarBlock()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("<Border x:Name=\"StatusBar\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the status bar is gone");
        return xaml[at..xaml.IndexOf("\n        </Border>", at, StringComparison.Ordinal)];
    }

    private static string Tag(string source, int at) => source[at..source.IndexOf('>', at)];

    [Fact]
    public void the_status_bar_is_docked_along_the_bottom_across_every_column()
    {
        string xaml = Xaml();
        int columns = xaml.IndexOf("<ColumnDefinition x:Name=\"ThumbnailColumn\"", StringComparison.Ordinal);
        int rows = xaml.IndexOf("<Grid.RowDefinitions>", columns, StringComparison.Ordinal);
        Assert.True(columns > 0 && rows > columns, "the page grid has no rows");
        string definitions = xaml[rows..xaml.IndexOf("</Grid.RowDefinitions>", rows, StringComparison.Ordinal)];
        Assert.Contains("<RowDefinition Height=\"*\" />", definitions, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", definitions, StringComparison.Ordinal);

        string tag = Tag(StatusBarBlock(), 0);
        Assert.Contains("Grid.Row=\"2\"", tag, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"4\"", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_is_left_of_dragging_the_old_pill_around()
    {
        Assert.DoesNotContain("StatusGrip", Xaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("StatusGrip", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyStatusBarDock", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("StatusBarDock", Settings(), StringComparison.Ordinal);
    }

    [Fact]
    public void view_status_bar_hides_it_and_is_remembered()
    {
        string xaml = Xaml();
        int view = xaml.IndexOf("<MenuBarItem Title=\"View\"", StringComparison.Ordinal);
        string menu = xaml[view..xaml.IndexOf("</MenuBarItem>", view, StringComparison.Ordinal)];
        Assert.Contains("<ToggleMenuFlyoutItem x:Name=\"StatusBarToggle\" Text=\"Status bar\"", menu, StringComparison.Ordinal);
        Assert.Contains("Click=\"StatusBarToggle_Click\"", menu, StringComparison.Ordinal);

        string code = Code();
        string click = Body(code, "private void StatusBarToggle_Click(");
        Assert.Contains("_applyingSettings", click, StringComparison.Ordinal);
        Assert.Contains("ShowStatusBar = on", click, StringComparison.Ordinal);
        Assert.Contains("UpdateStatusBarVisibility();", click, StringComparison.Ordinal);

        Assert.Contains("SettingsStore.Current.ShowStatusBar",
            Statement(code, "private void UpdateStatusBarVisibility() =>"), StringComparison.Ordinal);
        Assert.Contains("StatusBarToggle.IsChecked = s.ShowStatusBar;",
            Body(code, "private void ApplySettings()"), StringComparison.Ordinal);
        Assert.Contains("public bool ShowStatusBar { get; init; } = true;", Settings(), StringComparison.Ordinal);
    }

    [Fact]
    public void in_full_screen_it_floats_over_the_page_instead_of_taking_a_row()
    {
        // A row that came and went with every reveal would resize the viewport
        // each time the pointer moved.
        Assert.Contains("Grid.SetRow(StatusBar, presenting ? 1 : 2);",
            Body(Code(), "public void SetPresenting("), StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", Tag(StatusBarBlock(), 0), StringComparison.Ordinal);
    }

    [Fact]
    public void find_is_no_longer_on_the_status_bar_but_over_the_page()
    {
        // Hiding the status bar must not take find with it.
        Assert.DoesNotContain("FindPanel", StatusBarBlock(), StringComparison.Ordinal);
        Assert.DoesNotContain("FindToggleButton", Xaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("FindToggleButton", Code(), StringComparison.Ordinal);

        string xaml = Xaml();
        string tag = Tag(xaml, xaml.IndexOf("<StackPanel x:Name=\"FindPanel\"", StringComparison.Ordinal));
        Assert.Contains("Grid.Column=\"2\"", tag, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", tag, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Top\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void the_title_bar_glass_opens_find_in_the_tab_in_front()
    {
        string window = WindowXaml();
        int at = window.IndexOf("<Button x:Name=\"QuickFind\"", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no find button in the title bar");
        string tag = Tag(window, at);
        Assert.Contains("Click=\"QuickFind_Click\"", tag, StringComparison.Ordinal);
        Assert.Contains("AllowFocusOnInteraction=\"False\"", tag, StringComparison.Ordinal);

        string code = WindowCode();
        Assert.Contains("private void QuickFind_Click(object sender, RoutedEventArgs e) => ActivePage?.ToggleFind();",
            code, StringComparison.Ordinal);
        Assert.Contains("QuickFind.IsEnabled = page?.CanFind ?? false;",
            Body(code, "private void RefreshQuickActions()"), StringComparison.Ordinal);
    }

    [Fact]
    public void a_long_message_in_the_middle_trims_instead_of_pushing_the_controls_off()
    {
        string bar = StatusBarBlock();
        int label = bar.IndexOf("ViewModel.TextAvailabilityLabel", StringComparison.Ordinal);
        Assert.True(label > 0, "the text availability message left the bar");
        int start = bar.LastIndexOf("<TextBlock", label, StringComparison.Ordinal);
        string element = bar[start..bar.IndexOf("/>", label, StringComparison.Ordinal)];
        Assert.Contains("Grid.Column=\"1\"", element, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", element, StringComparison.Ordinal);
    }
}
