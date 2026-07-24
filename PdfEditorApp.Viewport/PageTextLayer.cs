using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>One character's position (render-pixel space, top-left origin) and codepoint.</summary>
public readonly record struct CharGlyph(double Left, double Top, double Right, double Bottom, char Character);

public readonly record struct TextRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>
/// Pure text-layer logic for one page: search and hit-testing over a flat
/// array of character positions + codepoints (from render_core::get_page_chars).
/// No WinUI dependency — unit-testable without a UI thread.
/// </summary>
public sealed class PageTextLayer
{
    private readonly CharGlyph[] _chars;

    public string Text { get; }

    public int CharCount => _chars.Length;

    public PageTextLayer(IReadOnlyList<CharGlyph> chars)
    {
        _chars = chars.ToArray();
        Text = new string(_chars.Select(c => c.Character).ToArray());
    }

    /// <summary>Every case-insensitive occurrence of <paramref name="query"/>, allowing overlaps. Empty for a null/empty query or no matches.</summary>
    public IReadOnlyList<(int Start, int Length)> FindMatches(string query)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(query) || _chars.Length == 0)
        {
            return matches;
        }

        int searchFrom = 0;
        while (searchFrom <= Text.Length - query.Length)
        {
            int index = Text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            matches.Add((index, query.Length));
            searchFrom = index + 1;
        }

        return matches;
    }

    /// <summary>Index of the character containing (x, y), or the nearest one if none does exactly. -1 if there are no characters.</summary>
    public int HitTestNearest(double x, double y)
    {
        if (_chars.Length == 0)
        {
            return -1;
        }

        int best = 0;
        double bestDistanceSquared = double.MaxValue;

        for (int i = 0; i < _chars.Length; i++)
        {
            var c = _chars[i];
            if (x >= c.Left && x <= c.Right && y >= c.Top && y <= c.Bottom)
            {
                return i;
            }

            double dx = Math.Max(0, Math.Max(c.Left - x, x - c.Right));
            double dy = Math.Max(0, Math.Max(c.Top - y, y - c.Bottom));
            double distanceSquared = dx * dx + dy * dy;

            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// One rect per visual line covered by [start, start + length): contiguous
    /// characters are grouped by row (near-equal Top), so a selection or
    /// match wrapping across lines highlights as separate per-line
    /// rectangles instead of one rect spanning the gap between them.
    /// </summary>
    public IReadOnlyList<TextRect> GetRangeRects(int start, int length)
    {
        var rects = new List<TextRect>();
        if (length <= 0 || start < 0 || start >= _chars.Length)
        {
            return rects;
        }

        const double lineTolerance = 2.0;
        int end = Math.Min(start + length, _chars.Length);

        double left = 0, top = 0, right = 0, bottom = 0;
        bool open = false;

        void Flush()
        {
            if (open)
            {
                rects.Add(new TextRect(left, top, right, bottom));
                open = false;
            }
        }

        for (int i = start; i < end; i++)
        {
            var c = _chars[i];
            bool sameLine = open && Math.Abs(c.Top - top) <= lineTolerance;

            if (!open)
            {
                (left, top, right, bottom) = (c.Left, c.Top, c.Right, c.Bottom);
                open = true;
            }
            else if (sameLine)
            {
                left = Math.Min(left, c.Left);
                top = Math.Min(top, c.Top);
                right = Math.Max(right, c.Right);
                bottom = Math.Max(bottom, c.Bottom);
            }
            else
            {
                Flush();
                (left, top, right, bottom) = (c.Left, c.Top, c.Right, c.Bottom);
                open = true;
            }
        }

        Flush();
        return rects;
    }
}
