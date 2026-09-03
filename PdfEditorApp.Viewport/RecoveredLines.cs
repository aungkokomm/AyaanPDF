using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What a page's lines are once the core has managed to read a script PDFium
/// could not.
/// </summary>
public static class RecoveredLines
{
    /// <summary>
    /// The lines the app should work with: PDFium's, with everything it refused
    /// as complex script replaced by what recovery actually read.
    /// </summary>
    /// <remarks>
    /// ⚠️ REPLACED, NOT ADDED TO. Measured on a Word-produced Burmese page,
    /// PDFium reports ONE HUNDRED AND FIFTY lines where the page has eighteen:
    /// each one is a single placement, most of them a syllable or two, and the
    /// text they carry is in visual order with the wrong characters, so
    /// "ကျွန်တော့်" comes back as five fragments none of which spells anything.
    /// Leaving those in beside the recovered lines would mean a reader clicking
    /// one of eighteen real lines and hitting one of a hundred and fifty
    /// fragments that happens to lie on top of it.
    ///
    /// ⚠️ AND ONLY THE COMPLEX-SCRIPT ONES. A page can hold a Burmese
    /// paragraph and an English heading, and PDFium reads the heading perfectly
    /// well. Dropping every line would take the heading's editability away to
    /// fix the paragraph.
    ///
    /// ⚠️ A LINE RECOVERY COULD NOT READ CHANGES NOTHING. Its fragments stay
    /// exactly as they were, still refused, still saying why. Silently removing
    /// them would leave a strip of the page that answered no click at all.
    /// </remarks>
    public static IReadOnlyList<LineSnapshot> Merge(
        IReadOnlyList<LineSnapshot>? lines,
        IReadOnlyList<RecoveredLine>? recovered)
    {
        lines ??= Array.Empty<LineSnapshot>();
        if (recovered is null || recovered.Count == 0) { return lines; }

        var read = recovered.Where(r => r.WasRead).ToList();
        if (read.Count == 0) { return lines; }

        var kept = new List<LineSnapshot>(lines.Count + read.Count);
        foreach (var line in lines)
        {
            if (line.Refusal == LineRefusal.ComplexScript && CoveredBy(read, line))
            {
                continue;
            }
            kept.Add(line);
        }

        foreach (var line in read)
        {
            kept.Add(SnapshotOf(line));
        }
        return kept;
    }

    /// <summary>
    /// Whether a recovered line stands where this fragment does.
    /// </summary>
    /// <remarks>
    /// ⚠️ BY BASELINE, WHICH IS WHAT A FRAGMENT AND ITS LINE SHARE. They do
    /// not share a left edge, a width or a word count: a fragment is a piece of
    /// the line, so the only thing that identifies it as belonging is that it
    /// sits on the same line of type. The tolerance is a fraction of the page
    /// width, which at A4 is about a point.
    /// </remarks>
    private static bool CoveredBy(IReadOnlyList<RecoveredLine> read, LineSnapshot line)
    {
        foreach (var r in read)
        {
            if (Math.Abs(r.Baseline - line.Baseline) < BaselineTolerance) { return true; }
        }
        return false;
    }

    /// <summary>
    /// How close two baselines must be to be the same line of type, as a
    /// fraction of the page width. About a point on A4.
    /// </summary>
    private const double BaselineTolerance = 0.002;

    /// <summary>
    /// A recovered line as the app's own line type.
    /// </summary>
    /// <remarks>
    /// ⚠️ NO OBJECT RANGE, AND -1 SO THAT NOTHING CAN PRETEND OTHERWISE. The
    /// two writers addressed by objects must never be handed one of these, and
    /// an index of -1 is refused by the core rather than quietly writing to
    /// object zero. What routes it away from them is
    /// <see cref="LineSnapshot.Recovered"/> being set; this is the belt to that
    /// pair of braces.
    ///
    /// ⚠️ AND NO REFUSAL. The complex-script code is PDFium's verdict on
    /// PDFium's own reading. This text was proven against the font by reshaping
    /// it and demanding the page's glyph ids back identically, which is a
    /// stronger guarantee than any line PDFium reads without complaint.
    /// </remarks>
    private static LineSnapshot SnapshotOf(RecoveredLine line) =>
        new(FirstObject: -1,
            LastObject: -1,
            PrefixChars: 0,
            Words: WordsIn(line.Text),
            Left: line.Left,
            Top: line.Top,
            Right: line.Right,
            Bottom: line.Bottom,
            Baseline: line.Baseline,
            FontSizePts: line.FontSizePts,
            ColorRgb: 0,
            Refusal: LineRefusal.None,
            Text: line.Text,
            FontName: line.FontName,
            Recovered: line);

    private static int WordsIn(string text)
    {
        int words = 0;
        bool inside = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { inside = false; }
            else if (!inside) { inside = true; words++; }
        }
        return words;
    }
}
