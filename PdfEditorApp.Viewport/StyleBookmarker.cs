using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>What a piece of text is set in: the three things a reader can point at.</summary>
/// <param name="ColorRgb">Packed 0xRRGGBB, as <c>get_page_text_runs</c> reports it.</param>
public readonly record struct TextStyle(string FontName, double SizePoints, int ColorRgb)
{
    /// <summary>
    /// How the style reads in the list of examples. Named after what it is
    /// rather than what it matches, because the "use" boxes beside the list
    /// decide that and the description should not move when they change.
    /// </summary>
    public string Describe() =>
        $"Font: {FontName}, Size: {SizePoints.ToString("0.0", CultureInfo.InvariantCulture)}";
}

/// <summary>A stretch of same-styled text, and where on the page it was.</summary>
/// <param name="LineIndex">
/// Which line of the page it sits on, counted from the line breaks rather than
/// from coordinates. It is what tells "the next word on this line" from "the
/// first word of the next one".
/// </param>
public readonly record struct StyledRun(
    int PageIndex, int LineIndex, int CharStart, int CharCount, TextStyle Style, string Text)
{
    /// <summary>One past the last character, which is where the next run may join.</summary>
    public int CharEnd => CharStart + CharCount;
}

/// <summary>
/// Which parts of a style have to agree for text to count as an example of it.
///
/// Colour is off by default. A document that sets its headings in one font at
/// one size very often prints them in the same black as everything else, so
/// requiring the colour costs nothing and rules nothing out; but a document
/// that DOES colour its headings is exactly the case where it is the only thing
/// separating them from a same-sized run-in.
/// </summary>
public readonly record struct StyleMatch(bool FontName, bool FontSize, bool Color)
{
    public static readonly StyleMatch Default = new(FontName: true, FontSize: true, Color: false);

    /// <summary>
    /// How far apart two sizes can be and still be the same size.
    ///
    /// A size arrives from a matrix multiply, so one heading can report 11.98
    /// on one character and 12.01 on the next. A quarter of a point is far
    /// under the gap between any two sizes a document actually mixes.
    /// </summary>
    public const double SizeTolerance = 0.25;

    /// <summary>Whether <paramref name="candidate"/> counts as an example of <paramref name="example"/>.</summary>
    public bool Matches(TextStyle example, TextStyle candidate)
    {
        // Nothing ticked matches everything, which would bookmark every line in
        // the document. Refusing is the only answer that cannot be mistaken for
        // a fault in the document.
        if (!FontName && !FontSize && !Color)
        {
            return false;
        }

        if (FontName && !string.Equals(example.FontName, candidate.FontName, StringComparison.Ordinal))
        {
            return false;
        }

        if (FontSize && Math.Abs(example.SizePoints - candidate.SizePoints) > SizeTolerance)
        {
            return false;
        }

        return !Color || example.ColorRgb == candidate.ColorRgb;
    }
}

/// <summary>
/// Turns "bookmark everything that looks like this" into a list of headings.
///
/// The other detector, <see cref="HeadingDetector"/>, reads what the text SAYS:
/// it wants a numbering scheme or a word like "Chapter". This one reads what
/// the text LOOKS LIKE, which is the only handle on a book whose headings are
/// simply bigger, and on any document not written in English.
///
/// Depth comes from the ORDER the examples were given. The first style is level
/// one, the second its child, and so on: a flat list of styles has no other way
/// to express a hierarchy, and it matches how a document is built, where the
/// chapter title is set larger than the section title.
/// </summary>
public static class StyleBookmarker
{
    /// <summary>How deep the example list can go. Six is past any real document's
    /// heading hierarchy, and each level costs a column of indent in the panel.</summary>
    public const int MaxLevel = 6;

    /// <summary>
    /// A style matching most of a document would produce an outline nothing can
    /// display. Same limit, and the same reasoning, as the pattern detector.
    /// </summary>
    public const int MaxHeadings = 5000;

    /// <summary>
    /// How far apart two runs can be, in characters, and still be one heading
    /// wrapped over two lines.
    ///
    /// The gap is whatever the line ending cost: PDFium reports it as one or
    /// two characters that never reach a run. Anything wider is a different
    /// piece of text that happens to be set the same way, and joining those
    /// would run a heading into the paragraph under it.
    /// </summary>
    public const int MultilineGap = 3;

    /// <summary>
    /// Finds every run that matches one of the examples.
    /// </summary>
    /// <param name="runs">Every styled run in the document, in reading order.</param>
    /// <param name="examples">The styles to look for, outermost first.</param>
    /// <param name="allowMultiline">
    /// Whether consecutive matching runs of the same style join into one
    /// heading. A heading that wraps is one heading; a list of one-line
    /// headings set the same way is not.
    /// </param>
    /// <param name="filter">
    /// A second opinion on the text of a run that already matched a style, or
    /// null for none. Applied to the FINISHED title, so a heading that wrapped
    /// is tested as the one thing it is rather than as two halves, only one of
    /// which would carry the word being looked for.
    /// </param>
    public static IReadOnlyList<DetectedHeading> Detect(
        IEnumerable<StyledRun> runs,
        IReadOnlyList<TextStyle> examples,
        StyleMatch match,
        bool allowMultiline,
        StyleTextFilter? filter = null)
    {
        var found = new List<DetectedHeading>();
        if (examples.Count == 0)
        {
            return found;
        }

        StyledRun? pending = null;
        int pendingLevel = 0;

        foreach (var run in runs)
        {
            if (found.Count >= MaxHeadings)
            {
                break;
            }

            int level = LevelOf(run.Style, examples, match);
            if (level == 0)
            {
                Flush();
                continue;
            }

            if (allowMultiline && pending is { } open
                && open.PageIndex == run.PageIndex
                && pendingLevel == level
                && run.CharStart - open.CharEnd <= MultilineGap)
            {
                // A heading that wrapped. Joined with a space, because the line
                // ending that separated them is not in either run's text.
                pending = open with
                {
                    CharCount = run.CharEnd - open.CharStart,
                    Text = $"{open.Text.TrimEnd()} {run.Text.TrimStart()}",
                };
                continue;
            }

            Flush();
            pending = run;
            pendingLevel = level;
        }

        Flush();
        return HeadingDetector.Repair(found);

        void Flush()
        {
            if (pending is { } open)
            {
                string title = Tidy(open.Text);
                if (title.Length > 0 && (filter ?? StyleTextFilter.Any).Allows(title))
                {
                    found.Add(new DetectedHeading(title, pendingLevel, open.PageIndex));
                }
            }
            pending = null;
            pendingLevel = 0;
        }
    }

    /// <summary>
    /// The level a style earns, or 0 for one that matches no example.
    ///
    /// The FIRST match wins, so a reader who adds a broad style and then a
    /// narrow one gets the broad one's level. That is the order they chose.
    /// </summary>
    private static int LevelOf(TextStyle style, IReadOnlyList<TextStyle> examples, StyleMatch match)
    {
        for (int i = 0; i < examples.Count && i < MaxLevel; i++)
        {
            if (match.Matches(examples[i], style))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Collapses runs of whitespace and trims, as the pattern detector does.
    /// Extracted PDF text is full of stray spaces where the layout left gaps
    /// between glyphs.
    /// </summary>
    private static string Tidy(string title) =>
        string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
