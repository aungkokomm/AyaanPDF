using System;

namespace PdfEditorApp.Viewport;

/// <summary>How a text box's lines sit. Mirrors ALIGN_* in render_core.</summary>
public enum TextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
    Justify = 3,
}

/// <summary>
/// What a text box records in its <c>/Contents</c>, decoded: the words, the
/// font size, the colour, and the styling needed to re-open the editor on it
/// looking the same.
///
/// <paramref name="FillHex"/> and <paramref name="OutlineHex"/> are empty when
/// the box has no fill or no outline. Colours are "#AARRGGBB".
///
/// <paramref name="FontPath"/> is the OS path to the font FILE the box was drawn
/// in (empty for the default), so re-opening it re-embeds the same font instead
/// of falling back to the default and breaking a complex script. It is empty on
/// a box written before the round-trip existed. <paramref name="Underline"/> and
/// <paramref name="Strikethrough"/> are that box's text decorations.
/// </summary>
/// <para>
/// <paramref name="RotationDeg"/> is the box's clockwise rotation in degrees.
/// <paramref name="HasBoxRect"/> says whether the box recorded its own upright
/// rect (<paramref name="BoxLeft"/>..<paramref name="BoxBottom"/>, normalized): a
/// rotated box's annotation bounds are its enlarged bounding box, so the overlay
/// needs this to draw the tight frame and spin the exact box. Absent on a box
/// written before rotation existed, which is always upright anyway.
/// </para>
public readonly record struct TextBoxTag(
    string Text, double FontSizeNorm, string ColorHex,
    TextAlign Align, string FillHex, string OutlineHex, double OutlineWidthNorm,
    string FontPath, bool Underline, bool Strikethrough,
    double RotationDeg, bool HasBoxRect,
    double BoxLeft, double BoxTop, double BoxRight, double BoxBottom);

/// <summary>
/// Reads the tag a text box stores, the C# side of <c>parse_textbox_tag</c> in
/// render_core. The format is <c>AyaanText:sizePx:RRGGBBAA:base64(text)</c>.
///
/// This is the one piece that decides whether a mark under the pointer is an
/// editable text box or something else, so it has to agree with the writer
/// exactly. It lives here, tested, rather than inline in the view model, for the
/// same reason every other parse in this app was moved out: a mismatch is
/// invisible until a box silently refuses to edit or edits as garbage.
/// </summary>
public static class TextBoxTagReader
{
    private const string Prefix = "AyaanText:";
    private const string StyledPrefix = "AyaanTextB:";

    /// <summary>
    /// The capture width the size is stored in, matching the writer. The size in
    /// the tag is <c>fontSizeNorm * CaptureWidth</c>, so dividing recovers the
    /// normalized size the rest of the annotation layer uses.
    /// </summary>
    public const double CaptureWidth = 1000.0;

    public static bool TryParse(string? contents, out TextBoxTag tag)
    {
        tag = default;

        if (contents is null)
        {
            return false;
        }

        return contents.StartsWith(StyledPrefix, StringComparison.Ordinal)
            ? TryParseStyled(contents.Substring(StyledPrefix.Length), out tag)
            : contents.StartsWith(Prefix, StringComparison.Ordinal)
                && TryParsePlain(contents.Substring(Prefix.Length), out tag);
    }

    // size:textRGBA:base64  — the original boxes, left-aligned, no fill/outline.
    private static bool TryParsePlain(string rest, out TextBoxTag tag)
    {
        tag = default;

        int firstColon = rest.IndexOf(':');
        int secondColon = firstColon < 0 ? -1 : rest.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
        {
            return false;
        }

        if (!TrySize(rest.Substring(0, firstColon), out double sizeNorm)
            || !TryColor(rest.Substring(firstColon + 1, secondColon - firstColon - 1), out string colorHex)
            || !TryText(rest.Substring(secondColon + 1), out string text))
        {
            return false;
        }

        tag = new TextBoxTag(text, sizeNorm, colorHex, TextAlign.Left, "", "", 0, "", false, false,
            0, false, 0, 0, 0, 0);
        return true;
    }

    // size:textRGBA:align:fillRGBA:outlineRGBA:outlineW:[flags:base64(font):]base64(text)
    private static bool TryParseStyled(string rest, out TextBoxTag tag)
    {
        tag = default;

        // The words are ALWAYS the last field (colon-free base64). The six fixed
        // fields come first; an optional flags + base64 font pair sits between
        // them and the text, present only on boxes written since the round-trip
        // was added. Every field but the words is colon-free, so a full split is
        // unambiguous, and a six-field box from before still parses.
        string[] parts = rest.Split(':');
        if (parts.Length < 7)
        {
            return false;
        }

        if (!TrySize(parts[0], out double sizeNorm) || !TryColor(parts[1], out string colorHex))
        {
            return false;
        }

        if (!int.TryParse(parts[2], out int alignRaw))
        {
            return false;
        }

        TextAlign align = alignRaw is >= 0 and <= 3 ? (TextAlign)alignRaw : TextAlign.Left;
        string fill = OptionalColor(parts[3]);
        string outline = OptionalColor(parts[4]);

        if (!double.TryParse(parts[5], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double outlineWpx))
        {
            outlineWpx = 0;
        }

        // The round-trip extras, present when there are more than the six fixed
        // fields plus the words: a decorations flag and the base64 font path
        // (>=9 fields), the rotation (>=10), and the box's own rect (>=14).
        string fontPath = "";
        bool underline = false, strikethrough = false;
        double rotation = 0;
        bool hasRect = false;
        double bl = 0, bt = 0, br = 0, bb = 0;
        if (parts.Length >= 9)
        {
            if (int.TryParse(parts[6], out int flags))
            {
                underline = (flags & 1) != 0;
                strikethrough = (flags & 2) != 0;
            }
            TryText(parts[7], out fontPath); // sets "" on a malformed value
        }
        if (parts.Length >= 10)
        {
            rotation = ParseDouble(parts[8]);
        }
        if (parts.Length >= 14)
        {
            hasRect = true;
            bl = ParseDouble(parts[9]);
            bt = ParseDouble(parts[10]);
            br = ParseDouble(parts[11]);
            bb = ParseDouble(parts[12]);
        }

        if (!TryText(parts[^1], out string text))
        {
            return false;
        }

        tag = new TextBoxTag(text, sizeNorm, colorHex, align, fill, outline,
                             outlineWpx / CaptureWidth, fontPath, underline, strikethrough,
                             rotation, hasRect, bl, bt, br, bb);
        return true;
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : 0;

    private static bool TrySize(string s, out double sizeNorm)
    {
        sizeNorm = 0;
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double px)
            || !double.IsFinite(px) || px <= 0)
        {
            return false;
        }

        sizeNorm = px / CaptureWidth;
        return true;
    }

    // Text colour is forced opaque, the pen palette it feeds back into being
    // opaque too.
    private static bool TryColor(string rgba, out string colorHex)
    {
        colorHex = "";
        if (rgba.Length != 8 || !IsHex(rgba))
        {
            return false;
        }

        colorHex = $"#FF{rgba.Substring(0, 6)}";
        return true;
    }

    // A fill or outline: RRGGBBAA, with alpha 0 (or a malformed value) meaning
    // "none", returned as an empty string.
    private static string OptionalColor(string rgba)
    {
        if (rgba.Length != 8 || !IsHex(rgba))
        {
            return "";
        }

        string aa = rgba.Substring(6, 2);
        if (string.Equals(aa, "00", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        // Stored RRGGBBAA; the app uses #AARRGGBB.
        return $"#{aa}{rgba.Substring(0, 6)}";
    }

    private static bool TryText(string base64, out string text)
    {
        text = "";
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
