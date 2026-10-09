using System;
using System.IO;
using System.Text.Json;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Help menu's links to the project, and the one-time tip that points a
/// newcomer at Edit.
/// </summary>
public class HelpAndEditTipTests
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

    private static int IndexIn(string text, string part)
    {
        int at = text.IndexOf(part, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{part}' was not found");
        return at;
    }

    [Fact]
    public void help_opens_the_projects_releases_and_a_new_issue()
    {
        Assert.Equal("https://github.com/aungkokomm/AyaanPDF/releases", HelpLinks.WhatsNew);
        Assert.Equal("https://github.com/aungkokomm/AyaanPDF/issues/new", HelpLinks.ReportProblem);

        string xaml = Read("PdfEditorApp", "MainPage.xaml");
        int help = IndexIn(xaml, "<MenuBarItem Title=\"Help\"");
        Assert.True(IndexIn(xaml, "Text=\"What's new\" Click=\"WhatsNew_Click\"") > help);
        Assert.True(IndexIn(xaml, "Text=\"Report a problem\" Click=\"ReportProblem_Click\"") > help);

        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        IndexIn(code, "OpenProjectPage(HelpLinks.WhatsNew)");
        IndexIn(code, "OpenProjectPage(HelpLinks.ReportProblem)");
    }

    [Fact]
    public void the_edit_tip_waits_for_its_first_showing()
    {
        Assert.False(new AppSettings().EditTipShown);

        var read = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(new AppSettings { EditTipShown = true }))!;
        Assert.True(read.EditTipShown);

        // A settings file from before the tip has no such key, and reads as
        // not shown, so a reader updating from an older version sees it once.
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}")!.EditTipShown);
    }

    [Fact]
    public void the_edit_tip_is_offered_when_a_document_arrives_and_recorded_before_it_opens()
    {
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");

        int chrome = IndexIn(code, "private void UpdateChromeForDocument()");
        int offered = code.IndexOf("MaybeShowEditTip();", chrome, StringComparison.Ordinal);
        Assert.True(offered > chrome);

        int tip = IndexIn(code, "private void MaybeShowEditTip()");
        int skip = code.IndexOf("SettingsStore.Current.EditTipShown", tip, StringComparison.Ordinal);
        int record = code.IndexOf("EditTipShown = true", tip, StringComparison.Ordinal);
        int open = code.IndexOf("EditTip.IsOpen = true", tip, StringComparison.Ordinal);
        Assert.True(tip < skip && skip < record && record < open);

        // Only the tab being looked at, and never over a full screen document.
        int active = code.IndexOf("window.ActivePage != this", tip, StringComparison.Ordinal);
        int full = code.IndexOf("IsFullScreen: false", tip, StringComparison.Ordinal);
        Assert.True(active > tip && active < record);
        Assert.True(full > tip && full < record);
    }
}
