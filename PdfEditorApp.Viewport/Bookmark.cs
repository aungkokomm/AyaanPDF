using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One entry in the document's own outline.
/// </summary>
/// <param name="Depth">
/// 0 for a top-level entry, 1 for its child, and so on. The list is flat and in
/// reading order, so this is what the panel indents by.
/// </param>
/// <param name="PageIndex">
/// Zero-based page, or -1 when the entry has no resolvable target. An outline
/// entry can carry a remote or URI action, or a named destination the file
/// never defines. Those are still LISTED, because they are part of the author's
/// outline, and simply do not navigate.
/// </param>
public readonly record struct Bookmark(int Depth, int PageIndex, string Title)
{
    /// <summary>Whether clicking this entry can go anywhere.</summary>
    public bool HasTarget => PageIndex >= 0;

    /// <summary>
    /// What to show when an entry has an empty title. A blank row would look
    /// like a rendering fault; naming the page it points at is at least true.
    /// </summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : HasTarget ? $"Page {PageIndex + 1}" : "Untitled";
}

/// <summary>
/// Parses the buffer <c>get_bookmarks</c> returns: a count, then per entry a
/// depth, a page index, and a UTF-8 title.
/// </summary>
public static class BookmarkReader
{
    /// <summary>
    /// Parses the whole buffer. A well-formed, possibly empty buffer throws
    /// nothing; a truncated one throws <see cref="ArgumentException"/> rather
    /// than reading out of bounds.
    /// </summary>
    public static IReadOnlyList<Bookmark> Parse(ReadOnlySpan<byte> bytes)
    {
        var marks = new List<Bookmark>();
        if (bytes.Length < 4)
        {
            // A document with no outline can legitimately return nothing at all.
            return marks;
        }

        int p = 0;
        uint count = ReadU32(bytes, ref p);
        for (uint i = 0; i < count; i++)
        {
            int depth = ReadI32(bytes, ref p);
            int pageIndex = ReadI32(bytes, ref p);
            string title = ReadString(bytes, ref p);

            // Depth is clamped rather than trusted. It drives an indent width,
            // and a corrupt outline claiming depth 9000 would push every row
            // off the side of the panel.
            marks.Add(new Bookmark(Math.Clamp(depth, 0, MaxDepth), pageIndex, title));
        }

        return marks;
    }

    /// <summary>Matches the walk limit the core applies.</summary>
    public const int MaxDepth = 32;

    private static void Require(ReadOnlySpan<byte> bytes, int p, int need)
    {
        if (p + need > bytes.Length)
        {
            throw new ArgumentException("bookmark buffer is truncated");
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, ref int p)
    {
        Require(b, p, 4);
        uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
        p += 4;
        return v;
    }

    private static int ReadI32(ReadOnlySpan<byte> b, ref int p) => unchecked((int)ReadU32(b, ref p));

    private static string ReadString(ReadOnlySpan<byte> b, ref int p)
    {
        int len = (int)ReadU32(b, ref p);
        if (len == 0)
        {
            return string.Empty;
        }
        Require(b, p, len);
        string s = System.Text.Encoding.UTF8.GetString(b.Slice(p, len));
        p += len;
        return s;
    }
}
