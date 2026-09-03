using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One unit of the text being edited in place: where it sits on the page, and
/// where it is in the string the writer will be handed.
/// </summary>
/// <remarks>
/// ⚠️ A UNIT, NOT ALWAYS A CHARACTER. For text PDFium can read it is one
/// character; for a shaped script it is one CLUSTER, which may be several
/// characters that the page draws as a single mark. <see cref="Offset"/> is
/// where the unit STARTS in the text, so a caret placed at it always lands
/// between two things the page really does draw side by side.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </remarks>
public sealed record EditGlyph(
    double Left, double Right, double Bottom, int Offset,
    double PointSize, string FontName, string ColorHex);

/// <summary>
/// Turning what the core knows about a line into the units a caret moves
/// between.
/// </summary>
public static class EditGlyphs
{
    /// <summary>
    /// The units of a recovered line: one per cluster, in the order the page
    /// draws them.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE PAGE'S OWN MEASUREMENTS, NOT THE FONT'S NATURAL ONES. The core
    /// laid these out with the widths the FILE declares and the numbers written
    /// between the glyphs, which on a justified line is where the stretch
    /// lives. Re-measuring here with the font's own advances would drift
    /// further along the line, and the far end of a line is where a reader is
    /// most likely to click.
    ///
    /// ⚠️ AND NOTHING IS INVENTED. A line with no clusters gets no units and
    /// therefore no caret, which is what makes the caller refuse rather than
    /// put one somewhere plausible.
    /// </remarks>
    public static List<EditGlyph> Of(RecoveredLine line, string colorHex)
    {
        var out_ = new List<EditGlyph>(line.Clusters.Count);
        foreach (var c in line.Clusters)
        {
            out_.Add(new EditGlyph(
                Left: c.Left,
                Right: c.Right,
                Bottom: line.Baseline,
                Offset: c.From,
                PointSize: line.FontSizePts,
                FontName: line.FontName,
                ColorHex: colorHex));
        }
        return out_;
    }

    /// <summary>
    /// Which offset a click at <paramref name="x"/> means, by each unit's own
    /// midpoint.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE MIDPOINT, WHICH IS WHAT PUTS THE CARET ON THE NEARER SIDE. A
    /// click on the left half of a mark means before it and on the right half
    /// means after it, which is what every text editor does and what a reader's
    /// hand expects.
    /// </remarks>
    public static int OffsetAt(IReadOnlyList<EditGlyph> glyphs, string text, double x)
    {
        foreach (var g in glyphs)
        {
            if (x < (g.Left + g.Right) / 2) { return Math.Clamp(g.Offset, 0, text.Length); }
        }
        return text.Length;
    }

    /// <summary>Where on the page an offset in the UNCHANGED text sits.</summary>
    public static double XOf(IReadOnlyList<EditGlyph> glyphs, int offset)
    {
        if (glyphs.Count == 0) { return 0; }
        if (offset <= 0) { return glyphs[0].Left; }

        foreach (var g in glyphs)
        {
            if (g.Offset >= offset) { return g.Left; }
        }
        return glyphs[^1].Right;
    }

    /// <summary>The unit an offset falls in, or the last one.</summary>
    public static EditGlyph AtOrAfter(IReadOnlyList<EditGlyph> glyphs, int offset)
    {
        foreach (var g in glyphs)
        {
            if (g.Offset >= offset) { return g; }
        }
        return glyphs[^1];
    }
}
