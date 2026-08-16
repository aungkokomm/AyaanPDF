using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Bookmarking by example: "every line that looks like this one is a heading".
///
/// The pattern detector reads what the text SAYS and needs a numbering scheme
/// or an English word like "Chapter". This reads what it LOOKS LIKE, which is
/// the only handle on a book whose headings are simply bigger, and on any
/// document not written in English.
/// </summary>
public class StyleBookmarkerTests
{
    private static readonly TextStyle Heading = new("Helvetica-Bold", 18, 0x000000);
    private static readonly TextStyle Sub = new("Helvetica-Bold", 14, 0x000000);
    private static readonly TextStyle Body = new("Helvetica", 10, 0x000000);

    private static StyledRun Run(int page, int start, TextStyle style, string text) =>
        new(page, LineIndex: 0, start, text.Length, style, text);

    [Fact]
    public void only_the_runs_that_look_like_the_example_come_back()
    {
        var runs = new[]
        {
            Run(0, 0, Heading, "Chapter One"),
            Run(0, 20, Body, "Some ordinary text that goes on."),
            Run(1, 0, Heading, "Chapter Two"),
            Run(1, 20, Body, "More of it."),
        };

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: false);

        Assert.Equal(["Chapter One", "Chapter Two"], found.Select(h => h.Title));
        Assert.Equal([0, 1], found.Select(h => h.PageIndex));
    }

    [Fact]
    public void the_order_of_the_examples_is_the_hierarchy()
    {
        // A flat list of styles has no other way to say that one heading sits
        // under another, and it matches how a document is built: the chapter
        // title is set larger than the section title.
        var runs = new[]
        {
            Run(0, 0, Heading, "Chapter One"),
            Run(0, 20, Sub, "First section"),
            Run(0, 40, Body, "Text."),
            Run(1, 0, Sub, "Second section"),
        };

        var found = StyleBookmarker.Detect(runs, [Heading, Sub], StyleMatch.Default, allowMultiline: false);

        Assert.Equal([1, 2, 2], found.Select(h => h.Level));
    }

    [Fact]
    public void a_document_that_starts_at_a_sub_heading_still_makes_a_tree()
    {
        // The repair the pattern detector already does, reached from here. A
        // level-2 heading with no level-1 parent is an outline entry that
        // cannot be written at all.
        var runs = new[] { Run(0, 0, Sub, "Orphan"), Run(1, 0, Heading, "Chapter") };

        var found = StyleBookmarker.Detect(runs, [Heading, Sub], StyleMatch.Default, allowMultiline: false);

        Assert.Equal(1, found[0].Level);
    }

    [Fact]
    public void nothing_ticked_matches_nothing()
    {
        // Rather than everything. With no attribute to compare, every run in
        // the document is an example of every other, and the outline would be
        // the whole book. Refusing cannot be mistaken for a fault in the file.
        var runs = new[] { Run(0, 0, Heading, "Chapter One"), Run(0, 20, Body, "Text.") };
        var nothing = new StyleMatch(FontName: false, FontSize: false, Color: false);

        Assert.Empty(StyleBookmarker.Detect(runs, [Heading], nothing, allowMultiline: false));
    }

    [Fact]
    public void a_size_that_wobbles_is_still_the_same_size()
    {
        // Sizes arrive from a matrix multiply, so one 12pt heading reports
        // 11.98 on one character and 12.01 on the next. Comparing exactly would
        // match the sample and nothing else in the document.
        var example = new TextStyle("Helvetica-Bold", 18, 0);
        var wobbled = new TextStyle("Helvetica-Bold", 18.02, 0);

        Assert.True(StyleMatch.Default.Matches(example, wobbled));

        // But a real size difference is still a difference.
        Assert.False(StyleMatch.Default.Matches(example, new TextStyle("Helvetica-Bold", 17, 0)));
    }

    [Fact]
    public void colour_is_ignored_unless_it_is_asked_for()
    {
        var black = new TextStyle("Helvetica-Bold", 18, 0x000000);
        var red = new TextStyle("Helvetica-Bold", 18, 0xFF0000);

        Assert.True(StyleMatch.Default.Matches(black, red));
        Assert.False(new StyleMatch(true, true, Color: true).Matches(black, red));
    }

    [Fact]
    public void a_heading_that_wrapped_is_one_bookmark_when_that_is_allowed()
    {
        // The line ending is not in either run's text, so the two halves are
        // adjacent in character terms with a gap of one or two.
        var runs = new[]
        {
            Run(0, 0, Heading, "A Very Long Chapter Title"),
            Run(0, 26, Heading, "That Ran Onto Two Lines"),
        };

        var joined = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: true);
        Assert.Single(joined);
        Assert.Equal("A Very Long Chapter Title That Ran Onto Two Lines", joined[0].Title);

        var apart = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: false);
        Assert.Equal(2, apart.Count);
    }

    [Fact]
    public void two_headings_that_merely_look_alike_are_not_joined()
    {
        // The rule that keeps "allow multiline" from swallowing a document
        // whose headings are one after another: they are far apart in the text,
        // even though they are set identically.
        var runs = new[]
        {
            Run(0, 0, Heading, "Chapter One"),
            Run(0, 800, Heading, "Chapter Two"),
        };

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: true);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void a_heading_never_joins_across_a_page_break()
    {
        // A bookmark points at one page. Joining the last heading of one page
        // to the first of the next would produce a title that is on neither.
        var runs = new[]
        {
            Run(0, 900, Heading, "End of this page"),
            Run(1, 0, Heading, "Start of the next"),
        };

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: true);

        Assert.Equal(2, found.Count);
        Assert.Equal([0, 1], found.Select(h => h.PageIndex));
    }

    [Fact]
    public void body_text_between_two_halves_stops_them_joining()
    {
        // Even with the multiline option on, and even if the character gap were
        // small: something else was said in between.
        var runs = new[]
        {
            Run(0, 0, Heading, "First"),
            Run(0, 6, Body, "x"),
            Run(0, 8, Heading, "Second"),
        };

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: true);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void titles_come_back_tidied()
    {
        // Extracted PDF text is full of stray spaces where the layout left gaps
        // between glyphs, and a bookmark should not read that way.
        var runs = new[] { Run(0, 0, Heading, "  Chapter    One \n") };

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: false);

        Assert.Equal("Chapter One", found[0].Title);
    }

    [Fact]
    public void no_examples_finds_nothing_rather_than_everything()
    {
        var runs = new[] { Run(0, 0, Heading, "Chapter One") };

        Assert.Empty(StyleBookmarker.Detect(runs, [], StyleMatch.Default, allowMultiline: false));
    }

    [Fact]
    public void a_style_matching_the_whole_book_stops_rather_than_building_it()
    {
        // A runaway example would otherwise produce an outline no panel can
        // display and no file should carry.
        var runs = Enumerable.Range(0, StyleBookmarker.MaxHeadings + 500)
            .Select(i => Run(i, 0, Heading, $"Line {i}"))
            .ToList();

        var found = StyleBookmarker.Detect(runs, [Heading], StyleMatch.Default, allowMultiline: false);

        Assert.True(found.Count <= StyleBookmarker.MaxHeadings + 1,
                    $"produced {found.Count} headings");
    }

    [Fact]
    public void a_style_reads_as_the_dialog_shows_it()
    {
        // The list in the dialog is the only record of what was captured, so it
        // has to name the two things the reader can check against the page.
        string described = new TextStyle("F2", 12, 0).Describe();

        Assert.Contains("F2", described);
        Assert.Contains("12.0", described);
    }
}
