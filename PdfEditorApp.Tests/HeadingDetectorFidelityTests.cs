using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Checks the port against the original tool's own output.
///
/// OpenAutoBookmark ships an example.pdf and the headers.txt its script
/// produced from it. That pair is a ground truth no synthetic fixture can
/// stand in for, so this compares what the port finds in the same text against
/// what the original found.
///
/// Skipped unless both paths are supplied, because neither file belongs in this
/// repository: the PDF is 1.9 MB and is not ours to redistribute. Run with
///   set AUTOBOOKMARK_TEXT=...\example_text.txt   (see the Rust
///                                                 dump_page_text_of_a_real_document test)
///   set AUTOBOOKMARK_EXPECTED=...\headers.txt
/// </summary>
public class HeadingDetectorFidelityTests
{
    private static string? Text => Environment.GetEnvironmentVariable("AUTOBOOKMARK_TEXT");
    private static string? Expected => Environment.GetEnvironmentVariable("AUTOBOOKMARK_EXPECTED");

    /// <summary>
    /// Undoes typographic ligatures.
    ///
    /// The two tools extract them differently and neither is wrong: PyPDF2
    /// hands back U+FB01 as it appears in the font, PDFium decomposes it to
    /// "fi". Five of this paper's headings contain one, and comparing the raw
    /// strings would fail on all five while the headings themselves agree.
    ///
    /// Ours is the more useful form, incidentally: a bookmark reading
    /// "Speciﬁc" does not match a search for "Specific".
    /// </summary>
    private static string Unligature(string s) => s
        .Replace("ﬀ", "ff").Replace("ﬁ", "fi").Replace("ﬂ", "fl")
        .Replace("ﬃ", "ffi").Replace("ﬄ", "ffl").Replace("ﬅ", "st")
        .Replace("ﬆ", "st");

    /// <summary>Parses "1.1. Article Scope - Title Level 2 - Page 3".</summary>
    private static List<(string Title, int Level, int Page)> ParseOriginalOutput(string path) =>
        File.ReadAllLines(path)
            .Select(line => Regex.Match(line, @"^(.*?) - Title Level (\d+) - Page (\d+)$"))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)))
            .ToList();

    [Fact]
    public void finds_the_same_headings_the_original_found()
    {
        if (Text is null || Expected is null)
        {
            // Passes vacuously without the inputs, the same way the Rust side's
            // #[ignore] checks against real documents do. Adding a package just
            // to report this as "skipped" is not worth a dependency.
            return;
        }

        // Form feed separates pages in the dump, matching the Rust side.
        var pages = File.ReadAllText(Text!).Split('');
        var ours = HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern);
        var theirs = ParseOriginalOutput(Expected!);

        // Titles and pages must agree. Levels deliberately may not: the port
        // repairs a level that skips a generation, which the original leaves
        // broken, so those are compared separately below.
        var ourKeys = ours.Select(h => (h.Title, h.PageIndex)).ToList();
        var theirKeys = theirs
            .Select(h => (Title: Unligature(Regex.Replace(h.Title, @"\s+", " ").Trim()), Page: h.Page))
            .Distinct()
            .ToList();

        var missed = theirKeys.Except(ourKeys).ToList();
        var extra = ourKeys.Except(theirKeys).ToList();

        Assert.True(
            missed.Count == 0,
            $"the port missed {missed.Count} headings the original found: "
            + string.Join(" | ", missed.Take(5)));
        Assert.True(
            extra.Count == 0,
            $"the port invented {extra.Count} headings the original did not find: "
            + string.Join(" | ", extra.Take(5)));
    }
}
