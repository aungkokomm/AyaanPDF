using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What styles a document is made of, which is the question the reader
/// actually has.
///
/// "Find more text like this one" needs a sample to point at, and pointing at
/// one means already knowing the headings are 18pt bold. The document can
/// answer that about itself: the style used forty times is a heading and the
/// one used four thousand times is the body text.
/// </summary>
public class StyleSurveyTests
{
    private static readonly TextStyle Big = new("Helvetica-Bold", 18, 0);
    private static readonly TextStyle Medium = new("Helvetica-Bold", 14, 0);
    private static readonly TextStyle Body = new("Helvetica", 10, 0);

    private static StyledRun Run(int page, int start, TextStyle style, string text) =>
        new(page, LineIndex: 0, start, text.Length, style, text);

    [Fact]
    public void the_biggest_type_comes_first()
    {
        // A heading is nearly always set larger than what it heads, so the
        // order is the answer to "which of these are my headings".
        var runs = new[]
        {
            Run(0, 0, Body, "body"),
            Run(0, 10, Big, "BIG"),
            Run(0, 20, Medium, "medium"),
        };

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Equal([18, 14, 10], survey.Select(t => t.Style.SizePoints));
    }

    [Fact]
    public void at_the_same_size_the_rarer_style_comes_first()
    {
        // Where a document sets its headings at body size and separates them by
        // font alone, frequency is the only signal left, and the rare one is
        // the heading.
        var rare = new TextStyle("Helvetica-Bold", 10, 0);
        var runs = Enumerable.Range(0, 40).Select(i => Run(i, 0, Body, $"body {i}"))
            .Concat([Run(0, 500, rare, "Heading"), Run(1, 500, rare, "Another")])
            .ToList();

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Equal(rare, survey[0].Style);
        Assert.Equal(2, survey[0].Occurrences);
    }

    [Fact]
    public void a_style_is_counted_by_places_and_by_pages()
    {
        // Both, because they say different things: forty places on one page is
        // a table, and forty places on forty pages is a running header.
        var runs = new[]
        {
            Run(0, 0, Big, "one"),
            Run(0, 10, Big, "two"),
            Run(1, 0, Big, "three"),
        };

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Equal(3, survey[0].Occurrences);
        Assert.Equal(2, survey[0].PageCount);
    }

    [Fact]
    public void every_style_carries_a_sample_of_its_own_text()
    {
        // A row reading "18.0pt F2" tells a reader nothing about their
        // document. The first thing set in it does.
        var runs = new[] { Run(0, 0, Big, "Chapter One"), Run(1, 0, Big, "Chapter Two") };

        Assert.Equal("Chapter One", StyleSurvey.Survey(runs, StyleMatch.Default)[0].Sample);
    }

    [Fact]
    public void whitespace_is_not_a_style()
    {
        // The gaps between things a document sets are not things it sets.
        var runs = new[] { Run(0, 0, Big, "   "), Run(0, 10, Body, "real") };

        var survey = StyleSurvey.Survey(runs, StyleMatch.Default);

        Assert.Single(survey);
        Assert.Equal(Body, survey[0].Style);
    }

    [Fact]
    public void a_wildly_styled_document_does_not_produce_a_list_nobody_can_read()
    {
        // Varying the size by a fraction of a point across a paragraph makes
        // hundreds of distinct styles. The order above means the cut falls on
        // the ones not worth seeing.
        var runs = Enumerable.Range(0, StyleSurvey.MaxStyles + 100)
            .Select(i => Run(i, 0, new TextStyle("F1", 6 + (i * 0.01), 0), $"line {i}"))
            .ToList();

        Assert.Equal(StyleSurvey.MaxStyles, StyleSurvey.Survey(runs, StyleMatch.Default).Count);
    }

    [Fact]
    public void the_style_under_a_selected_character_is_the_one_that_covers_it()
    {
        // How "use the style of the text I selected" is answered: the selection
        // is a character range and a run knows which characters it covers, so
        // this is a lookup rather than a second kind of measurement.
        var runs = new[] { Run(0, 0, Big, "Chapter"), Run(0, 40, Body, "text") };

        Assert.Equal(Big, StyleSurvey.StyleAt(runs, 0, 3));
        Assert.Equal(Body, StyleSurvey.StyleAt(runs, 0, 41));

        // Between two runs is nowhere: that is the whitespace a run never
        // covered, and guessing at a neighbour would capture the wrong style.
        Assert.Null(StyleSurvey.StyleAt(runs, 0, 20));

        // And a page that was never scanned is not the same page.
        Assert.Null(StyleSurvey.StyleAt(runs, 1, 3));
    }
}
