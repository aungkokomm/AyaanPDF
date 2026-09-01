using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The running build says which build it is, without being asked.
///
/// ⚠️ THIS IS A REGRESSION, NOT A NICETY. A whole manual test cycle was spent
/// on an installed app two days behind the repository, and every conclusion
/// drawn from it was about code that was not running. The version had a home
/// (the About dialog) and that was no help: nobody opens a dialog to check
/// something they have no reason to doubt. It has to be visible on sight.
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
    public void the_title_bar_shows_the_running_version()
    {
        // The title bar is on screen in every state, with or without a
        // document open, which is what "verifiable on sight" means here.
        Assert.Contains(
            "TitleText.Text = $\"{AppInfo.Name} {AppInfo.Version}\";",
            Source("PdfEditorApp", "MainWindow.xaml.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_version_is_read_from_the_assembly_and_not_written_out_again()
    {
        // ⚠️ ONE HOME FOR THE NUMBER. The csproj is what the build stamps onto
        // the exe and what the installer reads back off it. A copy in C# would
        // be a second place to forget, and the title bar would then confidently
        // display the wrong build, which is worse than displaying none.
        string info = Source("PdfEditorApp", "AppInfo.cs");

        Assert.Contains("Assembly.GetExecutingAssembly()", info, StringComparison.Ordinal);
        Assert.Contains(".GetName().Version", info, StringComparison.Ordinal);
        Assert.DoesNotContain("public const string Version", info, StringComparison.Ordinal);
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
