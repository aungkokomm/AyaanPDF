using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>One page's worth of styled runs, and how much text it had at all.</summary>
/// <param name="CharCount">
/// Every character the page held, including the ones no run kept. A page with
/// characters and no runs is a document whose fonts carry no /ToUnicode map,
/// which is a different problem from a page with no text at all, and the two
/// need opposite advice.
/// </param>
public readonly record struct StyledRunPage(
    int PageIndex, int CharCount, IReadOnlyList<StyledRun> Runs)
{
    public static StyledRunPage Empty(int pageIndex) => new(pageIndex, 0, []);
}

/// <summary>
/// Parses the buffer <c>get_page_text_runs</c> returns: a run count, the page's
/// total character count, then per run a character start and length, a font
/// size, a packed colour, and two length-prefixed UTF-8 strings.
///
/// Shaped like <see cref="BookmarkReader"/>, and for the same reason: a
/// self-describing buffer crosses the FFI once, rather than a struct with two
/// string pointers whose lifetimes both sides would have to agree about.
/// </summary>
public static class StyledRunReader
{
    /// <summary>
    /// Parses a page's runs. A well-formed, possibly empty buffer throws
    /// nothing; a truncated one throws <see cref="ArgumentException"/> rather
    /// than reading out of bounds.
    /// </summary>
    public static StyledRunPage Parse(ReadOnlySpan<byte> bytes, int pageIndex)
    {
        var runs = new List<StyledRun>();
        if (bytes.Length < 8)
        {
            // A page with no text layer is a legitimate empty answer.
            return StyledRunPage.Empty(pageIndex);
        }

        int p = 0;
        uint count = ReadU32(bytes, ref p);
        int charCount = (int)ReadU32(bytes, ref p);

        for (uint i = 0; i < count; i++)
        {
            int start = ReadI32(bytes, ref p);
            int chars = ReadI32(bytes, ref p);
            int line = ReadI32(bytes, ref p);
            float size = ReadF32(bytes, ref p);
            int color = unchecked((int)(ReadU32(bytes, ref p) & 0xFF_FF_FF));
            string font = ReadString(bytes, ref p);
            string text = ReadString(bytes, ref p);

            runs.Add(new StyledRun(pageIndex, line, start, chars, new TextStyle(font, size, color), text));
        }

        return new StyledRunPage(pageIndex, charCount, runs);
    }

    private static void Require(ReadOnlySpan<byte> bytes, int p, int need)
    {
        if (p + need > bytes.Length)
        {
            throw new ArgumentException("styled run buffer is truncated");
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

    private static float ReadF32(ReadOnlySpan<byte> b, ref int p) =>
        BitConverter.UInt32BitsToSingle(ReadU32(b, ref p));

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
