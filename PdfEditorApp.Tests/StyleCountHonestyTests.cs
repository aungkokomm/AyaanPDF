using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Whether the reach a style row reports is the reach ticking it produces.
///
/// Reported from a real document: a row reading "9 places on 6 pages" was
/// ticked, on its own, and the preview came back with 4996 bookmarks. Both
/// numbers were computed honestly and they disagree by five hundred times,
/// which makes the list actively misleading: its whole job is to let a reader
/// tell a heading style from body text by how rare it is.
/// </summary>
public class StyleCountHonestyTests
{
    private static StyledRun Run(int page, int start, TextStyle style, string text) =>
        new(page, LineIndex: 0, start, text.Length, style, text);

    [Fact]
    public void the_reach_a_row_reports_is_the_reach_ticking_it_gets()
    {
        // The survey groups by the EXACT style, colour included. The matcher is
        // told to ignore colour by default. So a document that sets the same
        // font and size in two colours is split into two rare-looking rows,
        // and ticking either matches both.
        var black = new TextStyle("F3", 24, 0x000000);
        var offBlack = new TextStyle("F3", 24, 0x010101);

        var runs = new List<StyledRun> { Run(0, 0, black, "A heading") };
        runs.AddRange(Enumerable.Range(0, 40)
            .Select(i => Run(i, 100, offBlack, $"body line {i}")));

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);
        var row = survey.First(t => t.Style == black);

        var found = StyleBookmarker.Detect(
            runs, [row.Style], StyleMatch.Default, allowMultiline: false);

        Assert.Equal(found.Count, row.Occurrences);
    }

    [Fact]
    public void a_size_that_only_nearly_matches_is_counted_in_too()
    {
        // The matcher allows a quarter point either way, because a size arrives
        // from a matrix multiply. The survey keys on the exact value, so 24.0
        // and 23.9 are two rows that match each other.
        var exact = new TextStyle("F3", 24, 0);
        var nearly = new TextStyle("F3", 23.9, 0);

        var runs = new List<StyledRun> { Run(0, 0, exact, "One") };
        runs.AddRange(Enumerable.Range(0, 12).Select(i => Run(i, 50, nearly, $"more {i}")));

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Equal(13, survey.First(t => t.Style == exact).Occurrences);
    }

    [Fact]
    public void requiring_colour_separates_them_again()
    {
        // And the count has to follow the setting, not a fixed rule: with
        // colour required, the two really are different styles and the rare one
        // really is rare.
        var black = new TextStyle("F3", 24, 0x000000);
        var red = new TextStyle("F3", 24, 0xFF0000);

        var runs = new List<StyledRun> { Run(0, 0, black, "A heading") };
        runs.AddRange(Enumerable.Range(0, 30).Select(i => Run(i, 100, red, $"body {i}")));

        var strict = new StyleMatch(FontName: true, FontSize: true, Color: true);
        var survey = StyleSurvey.Survey(runs, strict);

        Assert.Equal(1, survey.First(t => t.Style == black).Occurrences);
        Assert.Equal(30, survey.First(t => t.Style == red).Occurrences);
    }

    [Fact]
    public void pages_are_counted_the_same_way_as_places()
    {
        // "9 places on 6 pages" has to be true of both halves, or the second
        // number goes wrong in the same way the first did.
        var head = new TextStyle("F3", 24, 0x000000);
        var same = new TextStyle("F3", 24, 0x020202);

        var runs = new List<StyledRun>
        {
            Run(0, 0, head, "One"),
            Run(5, 0, same, "Two"),
            Run(5, 50, same, "Three"),
        };

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Equal(3, survey.First(t => t.Style == head).Occurrences);
        Assert.Equal(2, survey.First(t => t.Style == head).PageCount);
    }

    [Fact]
    public void the_rows_are_still_the_documents_real_styles()
    {
        // Counting under the match rules must not MERGE the rows: which font
        // and size a heading is set in is what the reader is choosing between,
        // and two rows reading "24.0pt F3" and "24.0pt F5" are a real choice
        // even when the counts agree.
        var one = new TextStyle("F3", 24, 0);
        var two = new TextStyle("F5", 24, 0);

        var runs = new List<StyledRun> { Run(0, 0, one, "First"), Run(1, 0, two, "Second") };

        Assert.Equal(2, StyleSurvey.Survey(runs, StyleMatch.Default).Count);
    }
}
