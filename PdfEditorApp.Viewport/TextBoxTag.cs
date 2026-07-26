using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What a text box records in its <c>/Contents</c>, decoded: the words, the
/// font size and the colour needed to re-open the editor on it.
/// </summary>
public readonly record struct TextBoxTag(string Text, double FontSizeNorm, string ColorHex);

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

    /// <summary>
    /// The capture width the size is stored in, matching the writer. The size in
    /// the tag is <c>fontSizeNorm * CaptureWidth</c>, so dividing recovers the
    /// normalized size the rest of the annotation layer uses.
    /// </summary>
    public const double CaptureWidth = 1000.0;

    public static bool TryParse(string? contents, out TextBoxTag tag)
    {
        tag = default;

        if (contents is null || !contents.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = contents.Substring(Prefix.Length);

        // Exactly the writer's split: size, colour, then the rest is the base64
        // text, which may itself contain no colons because it is encoded.
        int firstColon = rest.IndexOf(':');
        if (firstColon < 0)
        {
            return false;
        }

        int secondColon = rest.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
        {
            return false;
        }

        string sizePart = rest.Substring(0, firstColon);
        string rgbaPart = rest.Substring(firstColon + 1, secondColon - firstColon - 1);
        string textPart = rest.Substring(secondColon + 1);

        if (!double.TryParse(sizePart, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double sizePx)
            || !double.IsFinite(sizePx) || sizePx <= 0)
        {
            return false;
        }

        if (rgbaPart.Length != 8 || !IsHex(rgbaPart))
        {
            return false;
        }

        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(textPart));
        }
        catch (FormatException)
        {
            return false;
        }

        // Alpha forced opaque: text colour carries no transparency, and the pen
        // palette this feeds back into is opaque too.
        string colorHex = $"#FF{rgbaPart.Substring(0, 6)}";

        tag = new TextBoxTag(text, sizePx / CaptureWidth, colorHex);
        return true;
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
