using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PdfEditorApp.Viewport;

/// <summary>A heading found in the page text, and where it was found.</summary>
/// <param name="Level">1 for a top-level heading, 2 for its child, and so on.</param>
public readonly record struct DetectedHeading(string Title, int Level, int PageIndex);

/// <summary>
/// Finds headings in page text so they can be turned into bookmarks.
///
/// Ported from OpenAutoBookmark (MIT), which matches a numbering pattern at the
/// start of each line and takes the heading's depth from how many numbers it
/// has: "3." is level 1, "3.1." level 2, "3.2.1." level 3.
///
/// Three things are done differently, each because the original breaks on real
/// documents rather than because a different design was preferred:
///
///   * Depth comes from the number of captures of the numbering group, not
///     from counting '.' in the whole match. Same answer for the default
///     pattern, but a custom pattern whose title part contains a dot no longer
///     silently reports the wrong depth.
///   * Levels are REPAIRED so no heading is more than one deeper than the one
///     before it. The original assigns a level-2 heading to the last level-1 it
///     saw, and crashes outright if it never saw one, which is what a document
///     starting at "2.1." does.
///   * A title identical to the previous one is dropped. Running headers repeat
///     the current section on every page, and the original turns a 40-page
///     section into 40 identical bookmarks.
/// </summary>
public static class HeadingDetector
{
    /// <summary>
    /// The original's default: a line beginning with one to three dotted
    /// numbers, then the title.
    ///
    /// Matches "3. THE ACTIVITY RECOGNITION CHAIN", "3.1. Data Segmentation",
    /// "3.2.1. Sliding Window".
    /// </summary>
    public const string NumberedPattern = @"^(\d+\.){1,3}\s+([\w\s]+)";

    /// <summary>How deep the numbering scheme can go.</summary>
    public const int MaxLevel = 3;

    /// <summary>
    /// A runaway pattern (".*" say) would match every line of a 3000-page book
    /// and produce an outline nothing can display. Stopping is better than
    /// building it.
    /// </summary>
    public const int MaxHeadings = 5000;

    /// <summary>
    /// Finds headings across a document.
    /// </summary>
    /// <param name="pageTexts">One entry per page, in page order.</param>
    /// <param name="pattern">
    /// A .NET regular expression. Anchor it with ^ to match at the start of a
    /// line, as the default does.
    /// </param>
    /// <exception cref="ArgumentException">The pattern is not a valid regex.</exception>
    public static IReadOnlyList<DetectedHeading> Detect(IEnumerable<string> pageTexts, string pattern)
    {
        Regex regex;
        try
        {
            // Multiline so ^ means "start of a line" rather than "start of the
            // page", which is how the original gets its per-line behaviour from
            // splitting the text first.
            regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"Not a valid pattern: {e.Message}", nameof(pattern));
        }

        var found = new List<DetectedHeading>();
        int pageIndex = 0;

        foreach (string text in pageTexts)
        {
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var line in SplitLines(text))
                {
                    if (found.Count >= MaxHeadings)
                    {
                        return Repair(found);
                    }

                    var match = regex.Match(line);
                    if (match.Success && match.Length > 0)
                    {
                        found.Add(new DetectedHeading(Tidy(match.Value), LevelOf(match), pageIndex));
                    }
                }
            }
            pageIndex++;
        }

        return Repair(found);
    }

    /// <summary>
    /// The heading's depth: how many times the numbering group repeated.
    ///
    /// "3." captures once, "3.2.1." three times. A pattern with no such group
    /// is treated as one flat level, which is the only honest answer when the
    /// pattern expresses no hierarchy.
    /// </summary>
    private static int LevelOf(Match match)
    {
        if (match.Groups.Count > 1 && match.Groups[1].Captures.Count > 0)
        {
            return Math.Clamp(match.Groups[1].Captures.Count, 1, MaxLevel);
        }
        return 1;
    }

    /// <summary>
    /// Makes the levels describable as a tree.
    ///
    /// A heading may be at most one level deeper than the one before it, and
    /// the first is always top level. Without this a document whose numbering
    /// starts at "2.1." produces a level-2 heading with no parent, which in the
    /// original is a crash and in a PDF outline is an entry that cannot be
    /// written at all.
    ///
    /// Consecutive duplicates go at the same time: a running header repeats the
    /// section title on every page of it.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="StyleBookmarker"/> needs exactly this. Both
    /// detectors produce a level per heading and both can produce a sequence a
    /// PDF outline cannot express; the rule for fixing that belongs in one
    /// place, not copied into the second one.
    /// </remarks>
    public static IReadOnlyList<DetectedHeading> Repair(List<DetectedHeading> found)
    {
        var repaired = new List<DetectedHeading>(found.Count);
        int previousLevel = 0;

        foreach (var heading in found)
        {
            if (repaired.Count > 0 &&
                string.Equals(repaired[^1].Title, heading.Title, StringComparison.Ordinal))
            {
                continue;
            }

            int level = Math.Min(heading.Level, previousLevel + 1);
            repaired.Add(heading with { Level = level });
            previousLevel = level;
        }

        return repaired;
    }

    /// <summary>
    /// Splits page text into lines.
    ///
    /// PDFium reports line breaks as \r\n, \r or \n depending on the document,
    /// so all three are handled rather than assuming one.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

    /// <summary>
    /// Collapses runs of whitespace and trims.
    ///
    /// Extracted PDF text is full of stray spaces where the layout put gaps
    /// between glyphs, and "3.1.   Data    Segmentation" should not become a
    /// bookmark that reads that way.
    /// </summary>
    private static string Tidy(string title) =>
        Regex.Replace(title, @"\s+", " ").Trim();
}
