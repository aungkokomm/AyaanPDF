using System;
using System.Linq;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Packing headings for the core's outline writer. The layout is shared with
/// <see cref="BookmarkReader"/>, so an outline written and read back has to
/// come out identical.
/// </summary>
public class BookmarkWriterTests
{
    [Fact]
    public void a_written_outline_reads_back_as_itself()
    {
        // The two sides of the boundary are separate code. Only a round trip
        // proves they agree about the layout.
        var headings = new[]
        {
            new DetectedHeading("Chapter One", 1, 0),
            new DetectedHeading("Section 1.1", 2, 4),
            new DetectedHeading("Chapter Two", 1, 9),
        };

        var read = BookmarkReader.Parse(BookmarkWriter.Serialise(headings));

        Assert.Equal(
            headings.Select(h => (h.Title, Depth: h.Level - 1, h.PageIndex)),
            read.Select(b => (b.Title, b.Depth, b.PageIndex)));
    }

    [Fact]
    public void level_one_becomes_depth_zero()
    {
        // Levels are 1-based, depths 0-based. Off by one here would put the
        // entire outline one level too deep, and the top level would have no
        // parent to hang from.
        var read = BookmarkReader.Parse(
            BookmarkWriter.Serialise([new DetectedHeading("Top", 1, 0)]));

        Assert.Equal(0, read[0].Depth);
    }

    [Fact]
    public void a_non_ascii_title_is_measured_in_bytes_not_characters()
    {
        // "अध्याय" is 6 characters but 18 bytes in UTF-8. A character count
        // here would truncate the title and leave the parser reading the next
        // entry from the middle of this one.
        const string title = "अध्याय एक";
        Assert.True(Encoding.UTF8.GetByteCount(title) > title.Length);

        var read = BookmarkReader.Parse(
            BookmarkWriter.Serialise([new DetectedHeading(title, 1, 2), new DetectedHeading("After", 1, 3)]));

        Assert.Equal(title, read[0].Title);
        Assert.Equal("After", read[1].Title);
    }

    [Fact]
    public void an_empty_outline_is_a_valid_buffer_that_clears_the_bookmarks()
    {
        var bytes = BookmarkWriter.Serialise(Array.Empty<DetectedHeading>());

        Assert.Equal(4, bytes.Length);
        Assert.Empty(BookmarkReader.Parse(bytes));
    }

    [Fact]
    public void a_detected_heading_survives_the_whole_pipeline()
    {
        // Detection through to the buffer the core receives, which is every
        // step the app takes between page text and a written bookmark.
        var pages = new[] { "1. INTRODUCTION\nbody\n1.1. Scope\n", "2. METHOD\n" };

        var read = BookmarkReader.Parse(BookmarkWriter.Serialise(
            [.. HeadingDetector.Detect(pages, HeadingDetector.NumberedPattern)]));

        Assert.Equal(
            new[] { ("1. INTRODUCTION", 0, 0), ("1.1. Scope", 1, 0), ("2. METHOD", 0, 1) },
            read.Select(b => (b.Title, b.Depth, b.PageIndex)).ToArray());
    }
}
