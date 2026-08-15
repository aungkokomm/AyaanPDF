using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Every asset the XAML loads at runtime is actually put beside the exe.
///
/// This app is UNPACKAGED, so ms-appx resolves to the folder the exe is in. An
/// asset marked Content but not copied there is packaged perfectly and present
/// nowhere: the Image renders nothing, no exception is raised, no log line
/// appears, and the build is green.
///
/// It has now happened twice. The csproj comment on AppIcon.ico records the
/// first time ("the title bar could not load it either"), and the welcome
/// screen's logo went invisible the same way the day it was added.
/// </summary>
public class AssetDeploymentTests
{
    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string Csproj() => RepoFile("PdfEditorApp", "PdfEditorApp.csproj");

    /// <summary>Every ms-appx asset referenced from any XAML in the app.</summary>
    public static TheoryData<string> ReferencedAssets()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in new[] { "MainPage.xaml", "MainWindow.xaml", "App.xaml" })
        {
            string xaml;
            try
            {
                xaml = RepoFile("PdfEditorApp", file);
            }
            catch (Xunit.Sdk.XunitException)
            {
                continue;   // that view does not exist
            }

            foreach (Match m in Regex.Matches(xaml, @"ms-appx:///(Assets/[^""']+)"))
            {
                found.Add(m.Groups[1].Value.Replace('/', '\\'));
            }
        }

        var data = new TheoryData<string>();
        foreach (string asset in found)
        {
            data.Add(asset);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ReferencedAssets))]
    public void an_asset_the_xaml_loads_is_copied_next_to_the_exe(string asset)
    {
        string csproj = Csproj();

        // The Content ITEM, not the first mention of the path. AppIcon.ico also
        // appears as <ApplicationIcon> and in two comments, and matching those
        // made this test fail against a csproj that was perfectly correct.
        int at = csproj.IndexOf($"Include=\"{asset}\"", StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, $"{asset} is loaded by XAML but has no Content item in the csproj");

        // The copy directive has to be on THIS item, so the window is bounded
        // by the end of the element rather than by a character count that could
        // wander into the next one.
        int close = csproj.IndexOf("</Content>", at, StringComparison.Ordinal);
        int selfClosing = csproj.IndexOf("/>", at, StringComparison.Ordinal);

        bool copied =
            close >= 0
            && (selfClosing < 0 || close < selfClosing)
            && csproj[at..close].Contains("CopyToOutputDirectory", StringComparison.Ordinal);

        Assert.True(
            copied,
            $"{asset} is loaded by ms-appx at runtime but has no CopyToOutputDirectory, "
            + "so in an unpackaged build it will not exist and the element will render nothing");
    }

    [Fact]
    public void the_scan_actually_found_something()
    {
        // A regex that silently matches nothing would make every case above
        // pass by not running.
        Assert.NotEmpty(ReferencedAssets());
    }
}
