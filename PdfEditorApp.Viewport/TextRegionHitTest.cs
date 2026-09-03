using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Turns a point on the page into a place in the page's own text.
/// </summary>
/// <remarks>
/// ⚠️ THIS IS THE GEOMETRY IN-PLACE EDITING STANDS ON. A settled product
/// requirement: editing has to happen where the text already is, so the caret
/// goes at a real position among real glyphs and not at the start of a box that
/// appeared on top of them. Answering "which character, and which side of it"
/// from the drawn glyph bounds is the whole of what makes that possible, and it
/// is deliberately separated from anything that draws or types so it can be
/// proved on its own.
///
/// ⚠️ NO CARET AND NO TYPING LIVE HERE, on purpose. This says WHERE. What to do
/// there is a later phase's business.
///
/// Coordinates are the model's own: top-left origin, both axes divided by the
/// page WIDTH, exactly as <see cref="TextCharacter"/> reports them.
/// </remarks>
public static class TextRegionHitTest
{
    /// <summary>
    /// How far outside a line the pointer may sit and still be taken to mean
    /// that line, as a fraction of the line's height.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE GAP BETWEEN LINES BELONGS TO SOMEBODY. A line's box stops at its
    /// glyphs, so leading is dead space, and a reader aiming between two lines
    /// of body text with a mouse would otherwise hit nothing at all. Half a
    /// line's height reaches about to the middle of ordinary leading, so the
    /// nearer line wins and nothing is claimed twice.
    /// </remarks>
    private const double LineReachFactor = 0.5;

    /// <summary>
    /// The line under <paramref name="x"/>, <paramref name="y"/>, or null.
    /// </summary>
    /// <param name="offerableOnly">
    /// When true, lines the model refuses are invisible to the pointer. That is
    /// what a caller placing a caret wants; a caller explaining a refusal wants
    /// the opposite.
    /// </param>
    public static TextLine? LineAt(
        IReadOnlyList<TextRegion>? regions, double x, double y, bool offerableOnly = true)
    {
        if (regions is null || regions.Count == 0) { return null; }

        TextLine? best = null;
        double bestDistance = double.MaxValue;

        foreach (var region in regions)
        {
            foreach (var line in region.Lines)
            {
                if (offerableOnly && !line.CanOffer) { continue; }
                if (line.Right <= line.Left || line.Bottom <= line.Top) { continue; }

                // Horizontally the line's own extent, and nothing wider: two
                // columns sit side by side and the space between them is not a
                // place to put a caret.
                if (x < line.Left || x > line.Right) { continue; }

                double reach = (line.Bottom - line.Top) * LineReachFactor;
                if (y < line.Top - reach || y > line.Bottom + reach) { continue; }

                // Inside the glyphs beats near them, and nearer beats further:
                // measured from the line's middle so that a point sitting in the
                // leading between two lines goes to the one it is closer to.
                double middle = (line.Top + line.Bottom) / 2;
                double distance = Math.Abs(y - middle);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = line;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The character <paramref name="x"/> falls on, or null when it falls in a
    /// gap between glyphs or outside the line.
    /// </summary>
    public static TextCharacter? CharacterAt(TextLine? line, double x)
    {
        if (line is null) { return null; }

        foreach (var c in line.Characters)
        {
            if (x >= c.Left && x <= c.Right) { return c; }
        }

        return null;
    }

    /// <summary>
    /// Where a caret put at <paramref name="x"/> would sit in the line's text:
    /// an offset BETWEEN characters, from 0 to the line's length.
    /// </summary>
    /// <remarks>
    /// ⚠️ AN INSERTION POINT, NOT A CHARACTER INDEX. The two differ by one at
    /// the right-hand half of every glyph, which is the difference between
    /// typing before a letter and after it. Each glyph's own midpoint decides,
    /// which is what every text editor does and what makes clicking at the end
    /// of a word put the caret after its last letter rather than inside it.
    ///
    /// Returns the line's own text offsets, so the answer is what an edit is
    /// addressed by rather than a position in some flattened copy.
    /// </remarks>
    public static int CaretOffsetAt(TextLine? line, double x)
    {
        if (line is null) { return 0; }

        var characters = line.Characters.ToList();
        if (characters.Count == 0) { return 0; }

        foreach (var c in characters)
        {
            double middle = (c.Left + c.Right) / 2;
            if (x < middle) { return c.Offset; }
        }

        // Past the last glyph: after it, not on it.
        var last = characters[^1];
        return Math.Min(line.Text.Length, last.Offset + last.Text.Length);
    }

    /// <summary>
    /// Where on the page a caret sitting at <paramref name="offset"/> in this
    /// line's ORIGINAL text belongs, as a normalized x.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE ANSWER COMES FROM THE DRAWN GLYPHS, NOT FROM MEASURING A STRING.
    /// The caret has to sit exactly where the page's own type sits, and the
    /// page was set by a different engine in a font this app may not even have.
    /// Asking a text layout engine where the sixth character starts would put
    /// the caret near the right place and drift further along every line.
    ///
    /// ⚠️ ONLY VALID FOR TEXT THE PAGE STILL DRAWS. Once the reader has typed,
    /// everything from the first changed character onwards is no longer at a
    /// position the page knows, and only the unchanged prefix can be answered
    /// this way. See <see cref="LineEditBuffer.UnchangedPrefix"/>.
    /// </remarks>
    public static double CaretXFor(TextLine? line, int offset)
    {
        if (line is null) { return 0; }
        if (offset <= 0) { return line.Left; }

        // The first glyph at or after this offset starts where the caret goes.
        // At or AFTER, because offsets can skip: the two readers disagree about
        // whitespace, so not every offset in the line's text has a glyph of its
        // own drawn for it.
        foreach (var c in line.Characters)
        {
            if (c.Offset >= offset) { return c.Left; }
        }

        return line.Right;
    }

    /// <summary>
    /// Everything a click resolved to: the line, the glyph under the pointer if
    /// there is one, and where a caret would go.
    /// </summary>
    public static TextHit? Resolve(IReadOnlyList<TextRegion>? regions, double x, double y)
    {
        var line = LineAt(regions, x, y);
        if (line is null) { return null; }

        return new TextHit(line, CharacterAt(line, x), CaretOffsetAt(line, x));
    }
}

/// <summary>What a point on the page resolved to in the page's own text.</summary>
/// <param name="Character">
/// The glyph under the pointer, or null when it landed in the space between two
/// of them. The caret offset is still meaningful in that case, which is why the
/// two are separate.
/// </param>
public sealed record TextHit(TextLine Line, TextCharacter? Character, int CaretOffset);
