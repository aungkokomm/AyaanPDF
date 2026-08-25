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
    public IReadOnlyList<(int Start, int Length)> FindMatches(string query) =>
        FindMatches(query, new SearchOptions());

    /// <summary>
    /// Every occurrence of <paramref name="query"/> under
    /// <paramref name="options"/>, allowing overlaps. Empty for a null/empty
    /// query or no matches.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> FindMatches(string query, SearchOptions options)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(query) || _chars.Length == 0)
        {
            return matches;
        }

        int searchFrom = 0;
        while (searchFrom <= Text.Length - query.Length)
        {
            int index = Text.IndexOf(query, searchFrom, options.Comparison);
            if (index < 0)
            {
                break;
            }

            if (!options.WholeWord || IsWholeWordAt(index, query.Length))
            {
                matches.Add((index, query.Length));
            }

            // Advances past the START of the hit, not past its end, which is
            // what allows overlaps. It is also why a rejected whole-word
            // candidate cannot end the scan: "cat" inside "concatenate" has to
            // be stepped over so the real one later on the page is still found.
            searchFrom = index + 1;
        }

        return matches;
    }

    /// <summary>
    /// Whether the range at <paramref name="start"/> stands alone as a word.
    ///
    /// A boundary is anything that is not a letter or a digit, and the ends of
    /// the page count as boundaries too, or the first and last words on every
    /// page would be unfindable.
    ///
    /// Note that an underscore reads as a boundary, since it is neither a
    /// letter nor a digit. In prose, which is what this searches, the case
    /// barely arises; a hand-rolled word-character class would be a larger
    /// thing to be wrong about than this is.
    /// </summary>
    private bool IsWholeWordAt(int start, int length)
    {
        if (start > 0 && char.IsLetterOrDigit(Text[start - 1]))
        {
            return false;
        }

        int after = start + length;
        return after >= Text.Length || !char.IsLetterOrDigit(Text[after]);
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
    /// Where the caret goes when a reader clicks at <paramref name="x"/> on the
    /// line lying between <paramref name="top"/> and <paramref name="bottom"/>:
    /// the number of that line's characters sitting to the left of the click.
    ///
    /// ⚠️ GEOMETRIC, AND THAT IS THE POINT. It would be easier to hit-test a
    /// character and use its index, but the index would be into THIS layer's
    /// string, and the editor is opened on the text a WORD or LINE reported,
    /// which is assembled from the page objects instead. The two agree on the
    /// visible characters and disagree about the separators: a line built from
    /// several objects has its doubled space collapsed, so an index carried
    /// across lands one place out for every join. Counting what is to the left
    /// needs no correspondence between the two strings at all.
    ///
    /// A character counts as on the line when its vertical centre is inside the
    /// band, so the tall and short glyphs of one line all belong to it, and as
    /// left of the click when its own centre is. Using the centre rather than an
    /// edge is what puts the caret on the nearer side of the letter clicked.
    ///
    /// Everything is in the space this layer was built in, which is the same
    /// space the overlay draws in and the same one a pointer arrives in.
    /// </summary>
    public int CaretOffsetOnLine(double x, double top, double bottom)
    {
        if (bottom < top)
        {
            (top, bottom) = (bottom, top);
        }

        int before = 0;
        foreach (var c in _chars)
        {
            double centreY = (c.Top + c.Bottom) / 2.0;
            if (centreY < top || centreY > bottom)
            {
                continue;
            }

            // A line break carries a rect of its own and is not a character the
            // reader can put a caret in front of.
            if (c.Character == '\n' || c.Character == '\r')
            {
                continue;
            }

            if ((c.Left + c.Right) / 2.0 < x)
            {
                before++;
            }
        }

        return before;
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
