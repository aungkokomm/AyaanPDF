using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The chrome around the document docked into the window instead of floating
/// over it: the tool options bar in its own row, the side panels flush against
/// the rail, rulers that stay inside their own edges, and tabs that a long
/// file name cannot stretch.
/// </summary>
/// <remarks>
/// Found in the user's screenshots of 3.46.2. The options bar covered the top
/// of the page and slid under the ruler, the panels floated as rounded cards,
/// and the left ruler drew its last label over the status bar. The window
/// cannot be loaded here, so these hold the declarations that fix them.
/// </remarks>
public class DockedChromeTests
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
    private static string WindowCode() => Read("PdfEditorApp", "MainWindow.xaml.cs");

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

    /// <summary>A start tag, to its closing angle bracket outside any quotes.</summary>
    private static string TagAt(string xaml, int at)
    {
        bool quoted = false;
        for (int i = at; i < xaml.Length; i++)
        {
            if (xaml[i] == '"') { quoted = !quoted; }
            else if (xaml[i] == '>' && !quoted) { return xaml[at..i]; }
        }
        throw new InvalidOperationException("unterminated tag");
    }

    private static string NamedTag(string xaml, string name)
    {
        var match = Regex.Match(xaml, @"<[\w:]+\s+x:Name=""" + name + @"""");
        Assert.True(match.Success, $"{name} is gone");
        return TagAt(xaml, match.Index);
    }

    /// <summary>Every direct child of the page grid, as its whole start tag.</summary>
    private static List<string> RootChildren()
    {
        string xaml = Xaml();
        int root = xaml.IndexOf("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("\n    </Grid>", root, StringComparison.Ordinal);
        Assert.True(root > 0 && end > root, "the page grid is gone");

        return Regex.Matches(xaml[..end], @"\n        <(?!Grid\.)[A-Za-z]")
            .Where(m => m.Index > root)
            .Select(m => TagAt(xaml, m.Index + 9))
            .ToList();
    }

    [Fact]
    public void both_rulers_are_clipped_to_their_own_edges()
    {
        // A Canvas does not clip. The left ruler's last tick and its "4.5"
        // label were drawn over the status bar.
        string xaml = Xaml();
        Assert.Contains("SizeChanged=\"Rulers_SizeChanged\"", NamedTag(xaml, "TopRuler"), StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"Rulers_SizeChanged\"", NamedTag(xaml, "LeftRuler"), StringComparison.Ordinal);

        string handler = Body(Code(), "private void Rulers_SizeChanged(");
        Assert.Contains(".Clip = new RectangleGeometry", handler, StringComparison.Ordinal);
        Assert.Contains("new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void the_options_bar_has_a_row_of_its_own_above_the_document()
    {
        string xaml = Xaml();
        int rows = xaml.IndexOf("<Grid.RowDefinitions>", xaml.IndexOf("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal), StringComparison.Ordinal);
        string definitions = xaml[rows..xaml.IndexOf("</Grid.RowDefinitions>", rows, StringComparison.Ordinal)];
        Assert.Equal(
            new[] { "Auto", "*", "Auto" },
            Regex.Matches(definitions, @"<RowDefinition Height=""([^""]+)"" />").Select(m => m.Groups[1].Value));

        // Docked, so none of what made it float.
        string bar = NamedTag(xaml, "PropertyBar");
        Assert.Contains("Grid.Column=\"2\"", bar, StringComparison.Ordinal);
        foreach (string floating in new[] { "Grid.Row", "Translation", "CornerRadius", "Margin", "ZIndex", "VerticalAlignment" })
        {
            Assert.DoesNotContain(floating, bar, StringComparison.Ordinal);
        }
        Assert.Contains("BorderThickness=\"0,0,0,1\"", bar, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"44\"", bar, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_but_the_options_bar_sits_in_the_row_above_the_document()
    {
        // Row 0 is the default, so an element that forgets Grid.Row lands in
        // the options bar's row. Only the bar belongs there; the rail and the
        // panels span it and the document row; the dialogs are popups; and the
        // menu bar is taken out and shown in the title bar.
        var allowed = new[] { "AppMenuBar", "PropertyBar", "ToolRail", "ThumbnailPanel", "BookmarkPanel" };
        var strays = RootChildren()
            .Where(tag => !tag.Contains("Grid.Row=", StringComparison.Ordinal))
            .Where(tag => !tag.StartsWith("<ContentDialog", StringComparison.Ordinal))
            .Where(tag => !allowed.Any(name => tag.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal)))
            .Select(tag => tag.Split('\n')[0].Trim())
            .ToList();
        Assert.True(strays.Count == 0, "in the options bar's row by default: " + string.Join("; ", strays));

        string xaml = Xaml();
        foreach (string name in new[] { "ToolRail", "ThumbnailPanel", "BookmarkPanel" })
        {
            Assert.Contains("Grid.RowSpan=\"2\"", NamedTag(xaml, name), StringComparison.Ordinal);
        }
        foreach (string name in new[] { "PageScroller", "TopRuler", "LeftRuler", "ObjectToolbar", "FindPanel", "NoticeBar" })
        {
            Assert.Contains("Grid.Row=\"1\"", NamedTag(xaml, name), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_bar_takes_its_row_only_when_it_has_something_to_show()
    {
        // 3.46.3 kept it up for every tool, and for Select that was a row
        // holding one word. The user: space should be taken only when a
        // toolbar comes.
        string code = Code();
        string decide = Statement(code, "private void UpdatePropertyBarVisibility() =>");
        Assert.Contains("_propertyBarWanted && ViewModel.PageCount > 0 && !IsPresenting", decide, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(code, @"PropertyBar\.Visibility\s*="));
        string rail = Body(code, "private void UpdateToolRail()");
        Assert.Contains("_propertyBarWanted = sections.Bar;", rail, StringComparison.Ordinal);
        Assert.True(
            rail.IndexOf("_propertyBarWanted = sections.Bar;", StringComparison.Ordinal)
                < rail.IndexOf("UpdatePropertyBarVisibility();", StringComparison.Ordinal),
            "the bar is decided before what it has to show is known");
        Assert.Contains("UpdatePropertyBarVisibility();", Body(code, "public void SetPresenting("), StringComparison.Ordinal);
        Assert.Contains("UpdatePropertyBarVisibility();", Body(code, "private void UpdateChromeForDocument()"), StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_reserves_room_at_the_top_of_the_page_for_the_bar()
    {
        // The bar can no longer cover the object toolbar, the Define popup or
        // the gradient panel, so they may use the whole viewport.
        string code = Code();
        Assert.DoesNotContain("topInset", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyBar.ActualHeight", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_bar_is_one_row_whenever_it_fits()
    {
        string xaml = Xaml();
        Assert.Contains("Orientation=\"Horizontal\"", NamedTag(xaml, "PropertyBarRows"), StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"PropertyBar_SizeChanged\"", NamedTag(xaml, "PropertyBar"), StringComparison.Ordinal);

        string code = Code();
        string fit = Body(code, "private void FitPropertyBar()");
        Assert.Contains("PropertyBarRows.Orientation =", fit, StringComparison.Ordinal);
        Assert.Contains("Orientation.Vertical", fit, StringComparison.Ordinal);
        Assert.Contains("FitPropertyBar();", Body(code, "private void UpdateToolRail()"), StringComparison.Ordinal);
        Assert.Contains("=> FitPropertyBar();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_side_panels_sit_flush_against_the_rail()
    {
        string xaml = Xaml();
        foreach (string name in new[] { "ThumbnailPanel", "BookmarkPanel" })
        {
            string tag = NamedTag(xaml, name);
            Assert.DoesNotContain("Margin", tag, StringComparison.Ordinal);
            Assert.DoesNotContain("CornerRadius", tag, StringComparison.Ordinal);
            Assert.Contains("BorderThickness=\"0,0,1,0\"", tag, StringComparison.Ordinal);
        }

        // Docked on the right, every divider moves to the edge facing the document.
        string dock = Body(Code(), "private void DockRail(");
        Assert.Contains("var divider = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);",
            dock, StringComparison.Ordinal);
        foreach (string name in new[] { "ToolRail", "ThumbnailPanel", "BookmarkPanel" })
        {
            Assert.Contains($"{name}.BorderThickness = divider;", dock, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("ThumbnailPanel.Margin", dock, StringComparison.Ordinal);
    }

    [Fact]
    public void the_rail_docked_right_does_not_share_a_column_with_the_panels()
    {
        // It used to: the rail and the pages panel both went to column 3, and
        // the panel was drawn over the rail.
        string xaml = Xaml();
        int columns = xaml.IndexOf("<Grid.ColumnDefinitions>", xaml.IndexOf("<Grid x:Name=\"RootGrid\"", StringComparison.Ordinal), StringComparison.Ordinal);
        string definitions = xaml[columns..xaml.IndexOf("</Grid.ColumnDefinitions>", columns, StringComparison.Ordinal)];
        Assert.Equal(
            new[] { "Auto", "Auto", "*", "Auto", "Auto" },
            Regex.Matches(definitions, @"<ColumnDefinition (?:x:Name=""\w+"" )?Width=""([^""]+)"" />").Select(m => m.Groups[1].Value));

        string dock = Body(Code(), "private void DockRail(");
        Assert.Contains("Grid.SetColumn(ToolRail, right ? 4 : 0);", dock, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(ThumbnailPanel, right ? 3 : 1);", dock, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(BookmarkPanel, right ? 3 : 1);", dock, StringComparison.Ordinal);
    }

    [Fact]
    public void a_long_file_name_cannot_stretch_its_tab()
    {
        string code = WindowCode();
        Assert.Contains("private const double TabTitleMaxWidth = 220;", code, StringComparison.Ordinal);

        string title = Body(code, "private static void SetTabTitle(");
        Assert.Contains("MaxWidth = TabTitleMaxWidth", title, StringComparison.Ordinal);
        Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis", title, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(item, title);", title, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(item, title);", title, StringComparison.Ordinal);

        // Both the new tab and every retitle go through it.
        string add = Body(code, "public MainPage AddDocumentTab(");
        Assert.Contains("SetTabTitle(item, System.IO.Path.GetFileName(path) ?? \"Welcome\");", add, StringComparison.Ordinal);
        Assert.Contains("SetTabTitle(item, p.TabTitle);", add, StringComparison.Ordinal);
        Assert.DoesNotContain("Header =", add, StringComparison.Ordinal);
    }
}
