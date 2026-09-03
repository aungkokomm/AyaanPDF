using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Everything needed to draw a line of the page's own text while it is being
/// edited in place.
/// </summary>
/// <remarks>
/// ⚠️ THE TAIL ONLY, NEVER THE WHOLE LINE. Editing must feel like editing the
/// page's own text, so the page's own glyphs are left alone wherever they are
/// still correct. They are the real type, at the real position, in the real
/// font, and nothing drawn on top could match them. Only from the first changed
/// character onwards is the page covered and redrawn, so an edit that has
/// changed nothing yet covers nothing at all: the reader sees their document,
/// unaltered, with a caret in it.
///
/// ⚠️ AND THE CARET IS PART OF THE TEXT, not a thing floating over it. When the
/// caret sits inside the tail the tail is handed over already split at it, so
/// the layout engine puts the caret between two runs of real text and it lands
/// exactly where the next character will appear. Nothing has to be measured,
/// and there is nothing to drift.
///
/// Everything here is in NORMALIZED page units, both axes divided by the page
/// width, the way the rest of the overlay works. The one exception is
/// <see cref="FontSizePts"/>, which is an absolute point size, because that is
/// what the page says and mixing the two once filled a window with two enormous
/// letters.
/// </remarks>
/// <param name="SelectFrom">
/// Where the selection starts inside the tail, or -1 when none of it is here.
/// </param>
/// <param name="SelectTo">Where it ends. Offsets into <see cref="Text"/>.</param>
public sealed record LiveTextTail(
    string Before,
    string After,
    bool CaretInTail,
    int SelectFrom,
    int SelectTo,
    double Left,
    double Top,
    double Bottom,
    double Baseline,
    double CoverRight,
    string FontFamily,
    double FontSizePts,
    bool Bold,
    bool Italic,
    string ColorHex,
    string CoverColorHex)
{
    /// <summary>The tail as it now reads, caret or no caret.</summary>
    public string Text => Before + After;
}

/// <summary>
/// The font to DRAW a page's text in while it is being edited.
/// </summary>
/// <remarks>
/// ⚠️ NOT THE SAME QUESTION AS <see cref="SystemFontMatch"/>. That one picks a
/// file to EMBED in the document, and refuses when nothing matches, because
/// writing the wrong font into a PDF is a corruption the reader keeps. This one
/// picks something to put on screen for the few seconds an edit is open, so a
/// near miss is better than nothing and it never refuses.
///
/// ⚠️ AND IT ONLY EVER AFFECTS THE TAIL. Everything before the first changed
/// character is still the page's own pixels, so a font that is close rather
/// than exact shows up only in the text the reader is actively typing.
/// </remarks>
public static class DisplayFontMatch
{
    /// <summary>A WinUI font family for a PDF base font name.</summary>
    public static string FamilyFor(string? baseFont)
    {
        if (string.IsNullOrWhiteSpace(baseFont)) { return "Segoe UI"; }

        // "AAAAAA+LiberationSerif" -> "liberationserif"
        int plus = baseFont.LastIndexOf('+');
        string name = (plus >= 0 ? baseFont[(plus + 1)..] : baseFont).ToLowerInvariant();

        // ⚠️ THE LIBERATION FACES ARE METRIC CLONES, which is exactly what is
        // wanted here: Liberation Serif carries the same advance widths as
        // Times New Roman, so the redrawn tail lines up with the page's own
        // type instead of drifting away from it along the line. The Harari book
        // this was measured on is set in Liberation Serif throughout.
        if (name.Contains("liberationserif")) { return "Times New Roman"; }
        if (name.Contains("liberationsans")) { return "Arial"; }
        if (name.Contains("liberationmono")) { return "Courier New"; }
        if (name.Contains("nimbusroman")) { return "Times New Roman"; }
        if (name.Contains("nimbussans")) { return "Arial"; }
        if (name.Contains("dejavuserif")) { return "Times New Roman"; }
        if (name.Contains("dejavusansmono")) { return "Courier New"; }
        if (name.Contains("dejavusans")) { return "Arial"; }

        if (name.Contains("timesnewroman") || name.Contains("times")) { return "Times New Roman"; }
        if (name.Contains("couriernew") || name.Contains("courier")) { return "Courier New"; }
        if (name.Contains("arialnarrow")) { return "Arial Narrow"; }
        if (name.Contains("arial") || name.Contains("helvetica")) { return "Arial"; }
        if (name.Contains("georgia")) { return "Georgia"; }
        if (name.Contains("verdana")) { return "Verdana"; }
        if (name.Contains("tahoma")) { return "Tahoma"; }
        if (name.Contains("calibri")) { return "Calibri"; }
        if (name.Contains("cambria")) { return "Cambria"; }
        if (name.Contains("consol")) { return "Consolas"; }
        if (name.Contains("comic")) { return "Comic Sans MS"; }
        if (name.Contains("trebuchet")) { return "Trebuchet MS"; }
        if (name.Contains("impact")) { return "Impact"; }
        if (name.Contains("segoe")) { return "Segoe UI"; }

        // Nothing recognised: guess from what the name says about itself, which
        // is still better than putting a sans face under a serif line.
        if (name.Contains("serif") || name.Contains("roman") || name.Contains("book"))
        {
            return "Times New Roman";
        }
        if (name.Contains("mono")) { return "Courier New"; }
        return "Arial";
    }

    /// <summary>Whether the page's own name for the font says it is bold.</summary>
    public static bool IsBold(string? baseFont)
    {
        string name = (baseFont ?? string.Empty).ToLowerInvariant();
        return name.Contains("bold") || name.Contains("demi") || name.Contains("black");
    }

    /// <summary>Whether the page's own name for the font says it is italic.</summary>
    public static bool IsItalic(string? baseFont)
    {
        string name = (baseFont ?? string.Empty).ToLowerInvariant();
        return name.Contains("italic") || name.Contains("oblique");
    }
}
