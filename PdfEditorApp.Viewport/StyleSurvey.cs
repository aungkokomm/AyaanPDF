using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>One style the document uses, and how much use it gets.</summary>
/// <param name="Occurrences">How many separate places it appears.</param>
/// <param name="PageCount">How many pages it appears on.</param>
/// <param name="Sample">The first thing set in it, so the style has a face.</param>
public readonly record struct StyleTally(
    TextStyle Style, int Occurrences, int PageCount, string Sample)
{
    /// <summary>
    /// "18.0pt Helvetica-Bold", the way the list shows it.
    ///
    /// Size first, because that is what the eye sorts by and what tells a
    /// heading from body text at a glance.
    /// </summary>
    public string Describe() =>
        $"{Style.SizePoints:0.0}pt {Style.FontName}";

    /// <summary>"12 places on 12 pages", or the singular of either.</summary>
    public string DescribeReach() =>
        $"{Occurrences} {(Occurrences == 1 ? "place" : "places")} " +
        $"on {PageCount} {(PageCount == 1 ? "page" : "pages")}";
}

/// <summary>
/// What styles a document is actually made of.
///
/// The tool this was modelled on can only be told "here is an example, find
/// more like it", which means knowing in advance that the headings are 18pt
/// bold and being able to find one to point at. A document answers that
/// question about itself: the handful of styles used a few dozen times are the
/// headings, and the one used ten thousand times is the body text.
///
/// So the dialog offers the list and the reader ticks, rather than hunting for
/// a sample to select. Pointing at a sample still works, and lands on the same
/// list.
/// </summary>
public static class StyleSurvey
{
    /// <summary>
    /// How many styles the list will show.
    ///
    /// A document that varies its size by a fraction of a point across a
    /// paragraph can produce hundreds of distinct styles, and no reader is
    /// going to look past the first screenful. The order below puts the ones
    /// worth seeing at the top, so the cut falls on the ones that are not.
    /// </summary>
    public const int MaxStyles = 60;

    /// <summary>
    /// Groups runs by style, most likely to be a heading first.
    ///
    /// Largest first, because a heading is nearly always set larger than what
    /// it heads. Ties go to the RARER style: at the same size, the one used
    /// forty times is a heading and the one used four thousand times is the
    /// body.
    ///
    /// ⚠️ The counts are worked out under <paramref name="match"/>, the same
    /// rules the matching itself will use, and NOT by exact style. Reported
    /// from a real document: a row reading "9 places on 6 pages" was ticked on
    /// its own and produced 4996 bookmarks. Both numbers were honest and they
    /// disagreed five hundred times over, because the survey keyed on the exact
    /// style while the matcher was ignoring colour and allowing a quarter point
    /// of size. A count that does not predict what ticking the row does is
    /// worse than no count: rarity is the whole signal a reader picks by.
    ///
    /// The ROWS stay one per exact style, because which font and size a heading
    /// is set in is what the reader is choosing between.
    /// </summary>
    public static IReadOnlyList<StyleTally> Survey(IEnumerable<StyledRun> runs, StyleMatch match)
    {
        // One pass to find the document's distinct styles and a sample of each.
        var samples = new Dictionary<TextStyle, string>();
        var kept = new List<StyledRun>();

        foreach (var run in runs)
        {
            string text = run.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            kept.Add(run);
            if (!samples.ContainsKey(run.Style))
            {
                samples[run.Style] = text;
            }
        }

        // Then, per style, what ticking it would actually reach. Quadratic in
        // the number of STYLES, which is capped, and linear in runs.
        var tallies = new List<StyleTally>(samples.Count);

        foreach (var (style, sample) in samples)
        {
            int occurrences = 0;
            var pages = new HashSet<int>();

            foreach (var run in kept)
            {
                if (match.Matches(style, run.Style))
                {
                    occurrences++;
                    pages.Add(run.PageIndex);
                }
            }

            tallies.Add(new StyleTally(style, occurrences, pages.Count, sample));
        }

        return tallies
            .OrderByDescending(t => t.Style.SizePoints)
            .ThenBy(t => t.Occurrences)
            .ThenBy(t => t.Style.FontName, StringComparer.Ordinal)
            .Take(MaxStyles)
            .ToList();
    }

    /// <summary>
    /// The style at a character on a page, for "use the style of the text I
    /// selected".
    ///
    /// The selection is a character range, and a run knows which characters it
    /// covers, so this is a lookup rather than a second kind of measurement.
    /// Returns null when the selection landed somewhere no run covers, which a
    /// selection that is all whitespace does.
    /// </summary>
    public static TextStyle? StyleAt(IEnumerable<StyledRun> runs, int pageIndex, int charIndex)
    {
        foreach (var run in runs)
        {
            if (run.PageIndex == pageIndex && charIndex >= run.CharStart && charIndex < run.CharEnd)
            {
                return run.Style;
            }
        }

        return null;
    }
}
