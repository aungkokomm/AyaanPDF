using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Putting a line back together out of the pieces a document broke it into.
///
/// Measured on the book that prompted this: 4765 runs across seven pages, a
/// median length of three characters, and not one of them holding more than a
/// single word. Bookmarking it by style offered five thousand one-word
/// bookmarks. A heading is a line, not a word.
/// </summary>
public class StyledRunMergeTests
{
    private static readonly TextStyle Body = new("F2", 10.6, 0);
    private static readonly TextStyle Other = new("F4", 10.6, 0);

    private static StyledRun Run(int line, int start, TextStyle style, string text) =>
        new(0, line, start, text.Length, style, text);

    [Fact]
    public void words_of_one_line_set_the_same_way_become_one_run()
    {
        // The gap of one is the space that fell between them: the document set
        // it in another font, so it became a whitespace run and was dropped.
        var runs = new[]
        {
            Run(0, 0, Body, "यह"),
            Run(0, 3, Body, "मनुष्य"),
            Run(0, 10, Body, "की"),
        };

        var joined = StyledRunMerge.JoinLines(runs);

        var only = Assert.Single(joined);
        Assert.Equal("यह मनुष्य की", only.Text);
        Assert.Equal(0, only.CharStart);
        Assert.Equal(12, only.CharEnd);
    }

    [Fact]
    public void pieces_with_nothing_between_them_are_joined_with_nothing()
    {
        // No separator existed in the document, so inventing one would break a
        // word in half. It matters for Devanagari especially: a vowel sign and
        // its consonant can fall either side of a font change, and a space
        // between them would put the sign beyond repair.
        var runs = new[]
        {
            Run(0, 0, Body, "पिपा"),
            Run(0, 4, Body, "सा"),
        };

        Assert.Equal("पिपासा", StyledRunMerge.JoinLines(runs)[0].Text);
    }

    [Fact]
    public void the_next_line_is_not_this_line()
    {
        // A heading that wrapped and two headings in a row look identical from
        // here, so joining across lines is a separate question with its own
        // answer, and it is the reader's to give.
        var runs = new[]
        {
            Run(0, 0, Body, "First line"),
            Run(1, 11, Body, "Second line"),
        };

        Assert.Equal(2, StyledRunMerge.JoinLines(runs).Count);
    }

    [Fact]
    public void something_said_in_between_keeps_them_apart()
    {
        // A differently styled WORD between two pieces is a real interruption:
        // a term set in italics, a name in small capitals, a footnote mark.
        // Punctuation is not, which is the case below this one.
        var runs = new[]
        {
            Run(0, 0, Body, "before"),
            Run(0, 7, Other, "aside"),
            Run(0, 13, Body, "after"),
        };

        var joined = StyledRunMerge.JoinLines(runs);

        Assert.Equal(3, joined.Count);
        Assert.Equal(["before", "aside", "after"], joined.Select(r => r.Text));
    }

    [Fact]
    public void punctuation_in_another_font_does_not_break_a_heading()
    {
        // The measured case, from the real book: a chapter title whose two
        // halves are set at 12pt in one subset font with the COLON between them
        // at 12.5pt in another. Nothing changed voice, a mark was set, and
        // letting it split the line gives two bookmarks where the page shows
        // one heading.
        var title = new TextStyle("F2", 12, 0);
        var mark = new TextStyle("F4", 12.5, 0);

        var runs = new[]
        {
            Run(0, 0, title, "Ůवचन १"),
            Run(0, 6, mark, ": "),
            Run(0, 8, title, "विचारवान अजुŊन"),
        };

        var only = Assert.Single(StyledRunMerge.JoinLines(runs));

        Assert.Equal("Ůवचन १: विचारवान अजुŊन", only.Text);

        // And it is the words that say what the style is. Taking the colon's
        // 12.5pt would file the heading under a style with one member.
        Assert.Equal(title, only.Style);
    }

    [Fact]
    public void every_separator_on_one_line_is_bridged()
    {
        // The running header of that same book, which is one line of text set
        // as four words in one font with three dashes in another between them.
        var runs = new[]
        {
            Run(0, 0, Body, "ओशो"),
            Run(0, 3, Other, " – "),
            Run(0, 6, Body, "गीता"),
            Run(0, 10, Other, "-"),
            Run(0, 11, Body, "दशŊन"),
        };

        var only = Assert.Single(StyledRunMerge.JoinLines(runs));

        Assert.Equal("ओशो – गीता-दशŊन", only.Text);
    }

    [Fact]
    public void a_separator_that_bridges_nothing_stays_where_it_was()
    {
        // It only stops being its own run when the SAME style resumes on the
        // far side. With nothing after it, or with something else after it,
        // there is nothing for it to be in the middle of.
        var runs = new[]
        {
            Run(0, 0, Body, "words"),
            Run(0, 6, Other, ":"),
            Run(1, 8, Body, "the next line"),
        };

        var joined = StyledRunMerge.JoinLines(runs);

        Assert.Equal(3, joined.Count);
        Assert.Equal([Body, Other, Body], joined.Select(r => r.Style));
    }

    [Fact]
    public void a_row_of_leader_dots_is_not_a_separator()
    {
        // A contents line carries the eye from a title to a page number with
        // dots, and the two ends of it are not one heading. A separator is
        // short: a colon, a dash, a bullet.
        var runs = new[]
        {
            Run(0, 0, Body, "Chapter One"),
            Run(0, 11, Other, ".............."),
            Run(0, 25, Body, "17"),
        };

        Assert.Equal(3, StyledRunMerge.JoinLines(runs).Count);
    }

    [Fact]
    public void a_wide_gap_is_not_the_same_piece_of_text()
    {
        // Two columns, or a heading and a page number at the far side of the
        // line. The characters between them went somewhere.
        var runs = new[]
        {
            Run(0, 0, Body, "Chapter One"),
            Run(0, 400, Body, "17"),
        };

        Assert.Equal(2, StyledRunMerge.JoinLines(runs).Count);
    }

    [Fact]
    public void a_different_style_on_the_same_line_stays_its_own_run()
    {
        // Which font and size a piece is set in is the whole signal the styles
        // list works from. Merging across that would erase it.
        var runs = new[]
        {
            Run(0, 0, Body, "plain"),
            Run(0, 6, Other, "different"),
        };

        Assert.Equal(2, StyledRunMerge.JoinLines(runs).Count);
    }

    [Fact]
    public void a_well_behaved_document_is_not_disturbed()
    {
        // The common case: one run per line already. Nothing to join, and the
        // runs come back exactly as they went in.
        var runs = new[]
        {
            Run(0, 0, Body, "Chapter One"),
            Run(1, 12, Body, "Some body text follows here."),
            Run(2, 41, Body, "And more of it."),
        };

        Assert.Equal(runs, StyledRunMerge.JoinLines(runs));
    }

    [Fact]
    public void joining_is_idempotent()
    {
        // It runs on every page of a scan, and a page's runs must not keep
        // growing if anything ever calls it twice.
        var runs = new[] { Run(0, 0, Body, "one"), Run(0, 4, Body, "two") };

        var once = StyledRunMerge.JoinLines(runs);
        Assert.Equal(once, StyledRunMerge.JoinLines(once));
    }

    [Fact]
    public void a_page_of_one_word_runs_becomes_a_page_of_lines()
    {
        // The shape of the real document: ten words a line, each its own run,
        // over five lines. Fifty runs in, five out.
        var runs = new List<StyledRun>();
        int at = 0;
        for (int line = 0; line < 5; line++)
        {
            for (int word = 0; word < 10; word++)
            {
                runs.Add(Run(line, at, Body, $"w{word}"));
                at += 3;
            }
            at += 2; // the line break
        }

        var joined = StyledRunMerge.JoinLines(runs);

        Assert.Equal(5, joined.Count);
        Assert.All(joined, r => Assert.Equal(10, r.Text.Split(' ').Length));
    }

    [Fact]
    public void nothing_at_all_comes_back_as_nothing()
    {
        Assert.Empty(StyledRunMerge.JoinLines([]));
    }
}
