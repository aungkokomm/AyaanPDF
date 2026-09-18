using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where the running build's version is shown, and where it comes from.
///
/// It was in the title bar from the time a whole manual test cycle was spent on
/// an installed app two days behind the repository. On 2026-09-18 the user
/// chose to have it in Help > About only. What guards against a stale install
/// now is the deploy check, which reads the installed exe's FileVersion after
/// every deploy.
/// </summary>
public class VersionVisibilityTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "MainWindow.xaml.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The source with its comment lines dropped, so commenting a line
    /// out fails the test rather than passing it.</summary>
    private static string Source(params string[] parts)
    {
        string text = File.ReadAllText(Path.Combine(Root(), Path.Combine(parts)));
        var kept = text
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join("\n", kept);
    }

    [Fact]
    public void the_version_is_in_help_about_and_not_the_title_bar()
    {
        Assert.DoesNotContain("AppInfo.Version", Source("PdfEditorApp", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("TitleText.Text =", Source("PdfEditorApp", "MainWindow.xaml.cs"), StringComparison.Ordinal);

        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int about = page.IndexOf("private async void About_Click(", StringComparison.Ordinal);
        Assert.True(about > 0, "Help > About is gone");
        Assert.Contains("Text = $\"Version {informational}\"", page[about..(about + 2500)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_version_is_read_from_the_assembly_and_not_written_out_again()
    {
        // ⚠️ ONE HOME FOR THE NUMBER. The csproj is what the build stamps onto
        // the exe and what the installer reads back off it. A copy in C# would
        // be a second place to forget, and About would then confidently
        // display the wrong build, which is worse than displaying none.
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int about = page.IndexOf("private async void About_Click(", StringComparison.Ordinal);
        string head = page[about..(about + 600)];

        Assert.Contains("Assembly.GetExecutingAssembly()", head, StringComparison.Ordinal);
        Assert.Contains(".GetName().Version", head, StringComparison.Ordinal);
        Assert.DoesNotContain("const string Version", Source("PdfEditorApp", "AppInfo.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_csproj_declares_exactly_one_three_part_version()
    {
        string csproj = File.ReadAllText(
            Path.Combine(Root(), "PdfEditorApp", "PdfEditorApp.csproj"));

        var found = System.Text.RegularExpressions.Regex.Matches(
            csproj, @"<Version>(?<v>[^<]+)</Version>");

        Assert.Single(found);
        Assert.Matches(@"^\d+\.\d+\.\d+$", found[0].Groups["v"].Value);
    }
}
