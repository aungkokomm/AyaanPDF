using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A named font size, as a fraction of page width.</summary>
public readonly record struct FontSize(string Name, double Value);

/// <summary>
/// The font sizes the text tool offers.
///
/// Normalized to page width, like every other measurement the annotation layer
/// stores, so a size means the same thing on a small page and a large one, and
/// at every zoom. Roughly 12, 18, 24 and 36 point on a US-Letter page.
/// </summary>
public static class TextBoxPresets
{
    public static IReadOnlyList<FontSize> Sizes { get; } =
    [
        new("Small", 0.020),
        new("Medium", 0.030),
        new("Large", 0.040),
        new("Huge", 0.060),
    ];

    public static FontSize DefaultSize => Sizes[1];
}

/// <summary>
/// Where a text box lands and how big it starts, from the click point.
///
/// Pure and here rather than in the view model, because the last several
/// geometry bugs in this app were all in code the test project could not reach.
/// The click is the box's TOP-LEFT corner, which is what a person expects when
/// they click to start typing.
/// </summary>
public static class TextBoxPlacement
{
    /// <summary>Line spacing as a multiple of the font size. Matches the core.</summary>
    public const double LineHeight = 1.3;

    /// <summary>Inset of the text from the box edge, in multiples of the size. Matches the core.</summary>
    public const double Padding = 0.35;

    /// <summary>
    /// Rough width of a character as a fraction of the font size, for Helvetica.
    /// Only used to size the starting box; the saved text is what is measured on
    /// reopen, so a loose estimate is fine, but it must be generous enough that
    /// the box does not clip the text before it is committed.
    /// </summary>
    public const double CharWidth = 0.62;

    /// <summary>
    /// The box for a piece of text at a click point, in normalized page
    /// coordinates. Returns (left, top, right, bottom).
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) Compute(
        double clickX, double clickY, string text, double fontSizeNorm)
    {
        var lines = (text ?? string.Empty).Split('\n');

        int longest = 0;
        foreach (var line in lines)
        {
            if (line.Length > longest)
            {
                longest = line.Length;
            }
        }

        // At least one character wide and one line tall, so an empty or
        // whitespace box is still a real rectangle rather than a zero-size one
        // PDFium would reject.
        double pad = fontSizeNorm * Padding;
        double textWidth = Math.Max(1, longest) * fontSizeNorm * CharWidth;
        double textHeight = Math.Max(1, lines.Length) * fontSizeNorm * LineHeight;

        double left = clickX;
        double top = clickY;
        double right = left + textWidth + (2 * pad);
        double bottom = top + textHeight + (2 * pad);

        return (left, top, right, bottom);
    }
}
