using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// One About, under Help.
/// </summary>
/// <remarks>
/// There used to be two: Help > About showed the version and the render
/// core, and an About tab in Settings showed the version again with the
/// credits and the diagnostic log link. Each had something the other lacked.
/// </remarks>
public class AboutDialogWiringTests
{
    private static string Code()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "MainPage.xaml.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        int end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return code[at..end];
    }

    [Fact]
    public void settings_no_longer_has_an_about_tab()
    {
        string code = Code();
        string settings = Body(code, "private async void Settings_Click(");

        Assert.DoesNotContain("Pivot", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"About\"", settings, StringComparison.Ordinal);
        Assert.Contains("BuildViewSettings()", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAboutPane", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Render core")]
    [InlineData("Renders with PDFium. Text shaping by rustybuzz. ")]
    [InlineData("PDF access through a patched copy of pdfium-render.")]
    [InlineData("Open the diagnostic log")]
    [InlineData("\"diag.log\"")]
    public void help_about_has_everything_both_had(string text)
    {
        Assert.Contains(text, Body(Code(), "private async void About_Click("), StringComparison.Ordinal);
    }

    [Fact]
    public void about_links_to_the_website_through_the_launcher()
    {
        string about = Body(Code(), "private async void About_Click(");

        Assert.Contains("Content = \"aungkokomm.github.io/ayaanpdf\"", about, StringComparison.Ordinal);
        Assert.Contains("Launcher.LaunchUriAsync(new Uri(\"https://aungkokomm.github.io/ayaanpdf/\"))", about, StringComparison.Ordinal);
    }

    [Fact]
    public void the_log_link_is_offered_once()
    {
        Assert.Single(Regex.Matches(Code(), "Open the diagnostic log"));
    }
}
