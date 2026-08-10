using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The ported half of OpenAutoBookmark: finding headings in page text.
/// </summary>
public class HeadingDetectorTests
{
    private static (string, int, int)[] Shape(System.Collections.Generic.IReadOnlyList<DetectedHeading> h) =>
        h.Select(x => (x.Title, x.Level, x.PageIndex)).ToArray();

    [Fact]
    public void finds_the_headings_the_original_documents_as_its_example()
    {
        // The four lines the original's own comment gives as what it matches,
        // with the depths it says they have.
        var pages = new[]
        {
            "3. THE ACTIVITY RECOGNITION CHAIN\nsome body text\n" +
            "3.1. Sensor Data Acquisition and Preprocessing\n" +
            "3.2. Data Segmentation\n" +
            "3.2.1. Sliding Window\n",
        };

        Assert.Equal(
            new[]
            {
                ("3. THE ACTIVITY RECOGNITION CHAIN", 1, 0),
                ("3.1. Sensor Data Acquisition and Preprocessing", 2, 0),
                ("3.2. Data Segmentation", 2, 0),
                ("3.2.1. Sliding Window", 3, 0),
            },
            Shape(HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern)));
    }

    [Fact]
    public void records_the_page_each_heading_was_found_on()
    {
        var pages = new[]
        {
            "1. INTRODUCTION\n",
            "nothing here\n",
            "2. METHOD\n2.1. Setup\n",
        };

        var found = HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern);

        Assert.Equal(0, found[0].PageIndex);
        Assert.Equal(2, found[1].PageIndex);
        Assert.Equal(2, found[2].PageIndex);
    }

    [Fact]
    public void a_document_that_starts_deep_does_not_produce_a_parentless_heading()
    {
        // The original crashes here with a NameError: it looks up the last
        // level-1 bookmark to use as the parent, and there has never been one.
        // A PDF outline cannot express a level-2 entry with no level-1 above it
        // either, so the levels are pulled up instead.
        var pages = new[] { "2.1. Orphan\n2.1.1. Deeper\n" };

        Assert.Equal(
            new[] { ("2.1. Orphan", 1, 0), ("2.1.1. Deeper", 2, 0) },
            Shape(HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern)));
    }

    [Fact]
    public void a_level_can_only_ever_deepen_by_one_at_a_time()
    {
        // Jumping from level 1 straight to level 3 leaves a gap no tree can
        // hold. Whatever the numbering says, the tree has to stay describable.
        var pages = new[] { "1. Top\n1.1.1. Skipped a level\n" };

        Assert.Equal(
            new[] { ("1. Top", 1, 0), ("1.1.1. Skipped a level", 2, 0) },
            Shape(HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern)));
    }

    [Fact]
    public void a_running_header_does_not_become_one_bookmark_per_page()
    {
        // A section title printed at the top of every page of that section. The
        // original turns a 40-page chapter into 40 identical bookmarks.
        var pages = new[]
        {
            "3. THE CHAIN\nbody\n",
            "3. THE CHAIN\nmore body\n",
            "3. THE CHAIN\nstill more\n",
            "4. RESULTS\n",
        };

        Assert.Equal(
            new[] { ("3. THE CHAIN", 1, 0), ("4. RESULTS", 1, 3) },
            Shape(HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern)));
    }

    [Fact]
    public void the_same_title_can_still_appear_twice_if_something_came_between()
    {
        // Only CONSECUTIVE repeats are running headers. A title that genuinely
        // recurs later in the document is a real heading and must survive.
        var pages = new[] { "1. Summary\n2. Detail\n1. Summary\n" };

        Assert.Equal(3, HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern).Count);
    }

    [Fact]
    public void layout_whitespace_does_not_end_up_in_the_bookmark()
    {
        // Extracted PDF text is full of gaps where the layout spaced glyphs out.
        var pages = new[] { "3.1.   Data    Segmentation   \n" };

        Assert.Equal("3.1. Data Segmentation", HeadingDetector.Detect(
            pages, HeadingDetector.NumberedPattern)[0].Title);
    }

    [Fact]
    public void handles_every_line_ending_a_pdf_might_use()
    {
        // PDFium reports \r\n, \r or \n depending on the document. Assuming one
        // would silently find nothing in the others.
        foreach (string eol in new[] { "\r\n", "\r", "\n" })
        {
            var pages = new[] { $"1. First{eol}2. Second{eol}" };
            Assert.Equal(2, HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern).Count);
        }
    }

    [Fact]
    public void a_pattern_with_no_numbering_group_gives_one_flat_level()
    {
        // A custom pattern that expresses no hierarchy should not have one
        // invented for it.
        var pages = new[] { "Chapter One\nbody\nChapter Two\n" };

        var found = HeadingDetector.Detect(pages, @"^Chapter .+");

        Assert.Equal(2, found.Count);
        Assert.All(found, h => Assert.Equal(1, h.Level));
    }

    [Fact]
    public void a_bad_pattern_is_reported_rather_than_thrown_raw()
    {
        // The pattern is typed by the user, so an unbalanced bracket is an
        // ordinary thing to happen and needs a message the dialog can show.
        var e = Assert.Throws<ArgumentException>(
            () => HeadingDetector.Detect(new[] { "text" }, "(unclosed"));

        Assert.Contains("Not a valid pattern", e.Message);
    }

    [Fact]
    public void a_runaway_pattern_cannot_build_an_endless_outline()
    {
        // ".+" matches every line. On a real book that is hundreds of thousands
        // of entries, and an outline nothing can open.
        var pages = Enumerable.Repeat(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}")), 200);

        Assert.True(HeadingDetector.Detect(pages, ".+").Count <= HeadingDetector.MaxHeadings);
    }

    [Fact]
    public void an_empty_document_finds_nothing_without_complaining()
    {
        Assert.Empty(HeadingDetector.Detect(Array.Empty<string>(), HeadingDetector.NumberedPattern));
        Assert.Empty(HeadingDetector.Detect(new[] { "", null! }, HeadingDetector.NumberedPattern));
    }
}
