using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A named colour the ink and highlight tools can use.</summary>
public readonly record struct InkColor(string Name, string Hex);

/// <summary>A named stroke thickness, in normalized page units.</summary>
public readonly record struct InkWidth(string Name, double Value);

/// <summary>
/// The colours and widths offered to the drawing tools.
///
/// Widths are NORMALIZED (a fraction of page width), not pixels, for the same
/// reason annotation positions are: a pixel width would mean something
/// different on every page size and at every zoom, so the same stroke would
/// come out thicker on a small page than a large one.
/// </summary>
public static class InkPresets
{
    public static IReadOnlyList<InkColor> Colors { get; } =
    [
        new("Red", "#FFE00000"),
        new("Yellow", "#FFFFD400"),
        new("Green", "#FF00A650"),
        new("Blue", "#FF0072C6"),
        new("Black", "#FF1A1A1A"),
    ];

    public static IReadOnlyList<InkWidth> Widths { get; } =
    [
        new("Fine", 0.0015),
        new("Medium", 0.003),
        new("Thick", 0.006),
        new("Marker", 0.012),
    ];

    /// <summary>Highlighter colours carry alpha, so text stays readable underneath.</summary>
    public static IReadOnlyList<InkColor> HighlightColors { get; } =
    [
        new("Yellow", "#88FFFF00"),
        new("Green", "#8800FF66"),
        new("Blue", "#8800AAFF"),
        new("Pink", "#88FF66CC"),
    ];

    public static InkColor DefaultColor => Colors[0];

    public static InkWidth DefaultWidth => Widths[1];

    public static InkColor DefaultHighlightColor => HighlightColors[0];

    /// <summary>
    /// Parses "#AARRGGBB" or "#RRGGBB" into components, falling back to the
    /// supplied alpha when none is given.
    /// </summary>
    public static (byte A, byte R, byte G, byte B) ParseHex(string hex, byte defaultAlpha = 0xFF)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return (defaultAlpha, 0, 0, 0);
        }

        string s = hex.TrimStart('#');

        try
        {
            return s.Length switch
            {
                8 => (Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                      Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16)),
                6 => (defaultAlpha, Convert.ToByte(s[..2], 16),
                      Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16)),
                _ => (defaultAlpha, 0, 0, 0),
            };
        }
        catch (FormatException)
        {
            // A malformed colour must not take the app down mid-stroke.
            return (defaultAlpha, 0, 0, 0);
        }
    }
}
