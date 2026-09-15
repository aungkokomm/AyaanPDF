using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Every button says what it is to a screen reader.
/// </summary>
/// <remarks>
/// ⚠️ A TOOLTIP IS NOT A NAME. Measured before this test: 135 buttons in the
/// app's XAML and 55 accessible names, so the title bar's Open and Save, the
/// rail's Menu and Settings, page navigation, zoom, find and every colour swatch
/// were announced as "button" and nothing else.
/// </remarks>
public class AccessibleNameTests
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

    [Theory]
    [InlineData("MainPage.xaml")]
    [InlineData("MainWindow.xaml")]
    [InlineData("BookmarkWindow.xaml")]
    public void every_button_says_what_it_is_to_a_screen_reader(string file)
    {
        string xaml = Source("PdfEditorApp", file);
        var unnamed = new List<string>();

        var buttons = Regex.Matches(
            xaml, @"<(Button|ToggleButton|DropDownButton|SplitButton|RepeatButton)(?=[\s>/])[^>]*>",
            RegexOptions.Singleline);
        foreach (Match button in buttons)
        {
            string tag = button.Value;
            if (tag.Contains("AutomationProperties.Name=", StringComparison.Ordinal)) { continue; }

            // Plain text content is its own name. A glyph ("&#xE70E;") or a
            // binding is not.
            var content = Regex.Match(tag, "\\bContent=\"([^\"]*)\"");
            if (content.Success
                && !content.Groups[1].Value.StartsWith("&#x", StringComparison.Ordinal)
                && !content.Groups[1].Value.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }

            int line = xaml[..button.Index].Count(c => c == '\n') + 1;
            unnamed.Add($"{file}:{line}");
        }

        Assert.True(unnamed.Count == 0,
            $"{unnamed.Count} buttons have no accessible name: {string.Join(", ", unnamed)}");
    }
}
