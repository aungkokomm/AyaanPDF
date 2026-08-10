using System;
using System.Collections.Generic;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Parsing the buffer get_bookmarks returns. The format is asserted from the
/// C# side as well as the Rust side, because the two halves are compiled
/// separately and nothing but a shared test would catch them drifting apart.
/// </summary>
public class BookmarkReaderTests
{
    private static byte[] Buffer(params (int depth, int page, string title)[] entries)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)entries.Length));
        foreach (var (depth, page, title) in entries)
        {
            bytes.AddRange(BitConverter.GetBytes(depth));
            bytes.AddRange(BitConverter.GetBytes(page));
            var utf8 = Encoding.UTF8.GetBytes(title);
            bytes.AddRange(BitConverter.GetBytes((uint)utf8.Length));
            bytes.AddRange(utf8);
        }
        return bytes.ToArray();
    }

    [Fact]
    public void reads_depth_page_and_title_in_order()
    {
        var marks = BookmarkReader.Parse(Buffer(
            (0, 0, "Chapter One"),
            (1, 4, "Section 1.1"),
            (0, 9, "Chapter Two")));

        Assert.Equal(3, marks.Count);
        Assert.Equal(new Bookmark(0, 0, "Chapter One"), marks[0]);
        Assert.Equal(new Bookmark(1, 4, "Section 1.1"), marks[1]);
        Assert.Equal(new Bookmark(0, 9, "Chapter Two"), marks[2]);
    }

    [Fact]
    public void an_empty_or_absent_outline_is_not_an_error()
    {
        // The overwhelmingly common case: most PDFs have no outline at all.
        Assert.Empty(BookmarkReader.Parse(Buffer()));
        Assert.Empty(BookmarkReader.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void an_entry_with_no_target_is_listed_but_goes_nowhere()
    {
        // Part of the author's outline, so hiding it would misrepresent the
        // document. It just must not offer to navigate.
        var mark = BookmarkReader.Parse(Buffer((0, -1, "Preface")))[0];

        Assert.False(mark.HasTarget);
        Assert.Equal("Preface", mark.DisplayTitle);
    }

    [Fact]
    public void an_untitled_entry_still_says_something()
    {
        // A blank row reads as a rendering fault. Naming the page it points at
        // is at least true, and "Untitled" is honest when it points nowhere.
        Assert.Equal("Page 8", BookmarkReader.Parse(Buffer((0, 7, "")))[0].DisplayTitle);
        Assert.Equal("Untitled", BookmarkReader.Parse(Buffer((0, -1, "   ")))[0].DisplayTitle);
    }

    [Fact]
    public void a_non_ascii_title_survives_the_boundary()
    {
        // The point of a UTF-8 length-prefixed title. A Devanagari or Burmese
        // outline is exactly the case a byte count would get wrong if anyone
        // ever swapped it for a character count.
        const string title = "अध्याय एक";
        var marks = BookmarkReader.Parse(Buffer((0, 2, title), (0, 3, "နောက်ဆုံး")));

        Assert.Equal(title, marks[0].Title);
        Assert.Equal("နောက်ဆုံး", marks[1].Title);
    }

    [Fact]
    public void an_absurd_depth_cannot_push_a_row_off_the_panel()
    {
        // Depth drives an indent. A corrupt outline claiming depth 9000 would
        // scroll every title out of sight, so it is clamped rather than trusted.
        Assert.Equal(
            BookmarkReader.MaxDepth,
            BookmarkReader.Parse(Buffer((9000, 0, "Runaway")))[0].Depth);
        Assert.Equal(0, BookmarkReader.Parse(Buffer((-5, 0, "Negative")))[0].Depth);
    }

    [Fact]
    public void a_truncated_buffer_throws_instead_of_reading_past_the_end()
    {
        // The buffer comes from native memory. Reading past its end would be an
        // out-of-bounds read, so a short or lying buffer has to fail loudly.
        var good = Buffer((0, 0, "Chapter One"));

        Assert.Throws<ArgumentException>(() => BookmarkReader.Parse(good.AsSpan(0, good.Length - 3)));

        // A count that promises more entries than the bytes contain.
        var lying = Buffer((0, 0, "One"));
        BitConverter.GetBytes((uint)5).CopyTo(lying, 0);
        Assert.Throws<ArgumentException>(() => BookmarkReader.Parse(lying));
    }
}
