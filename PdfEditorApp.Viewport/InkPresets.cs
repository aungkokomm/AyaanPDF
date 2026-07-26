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
    /// <summary>
    /// Pen colours. The first four deliberately share NAMES with the
    /// highlighter set, because picking a colour maps pen to highlighter by
    /// name; a pen colour with no namesake falls back to the default.
    /// </summary>
    public static IReadOnlyList<InkColor> Colors { get; } =
    [
        new("Black", "#FF1A1A1A"),
        new("Red", "#FFE00000"),
        new("Orange", "#FFFF7A00"),
        new("Yellow", "#FFF5B800"),
        new("Green", "#FF2E9E3B"),
        new("Blue", "#FF1565C0"),
        new("Purple", "#FF7B1FA2"),
        new("Pink", "#FFE0308A"),
        new("Gray", "#FF8A8A8A"),
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
        new("Yellow", "#88FFE000"),
        new("Green", "#8869E86A"),
        new("Cyan", "#8840D0E0"),
        new("Orange", "#88FF9500"),
        new("Pink", "#88FF4FA3"),
        new("Purple", "#88B388FF"),
        new("Red", "#88FF2D2D"),
        new("Blue", "#886FA8FF"),
    ];

    /// <summary>
    /// The highlighter colour matching a pen colour, by NAME.
    ///
    /// These two lists are different lengths (a highlighter has no useful
    /// "Black"), so pairing them by index silently mismatched: picking the
    /// fifth pen colour indexed past the end of the highlight list and left
    /// the highlighter on whatever it was. Matching by name pairs only the
    /// colours that genuinely correspond and falls back to the default
    /// otherwise, so adding a colour to either list cannot reintroduce it.
    /// </summary>
    public static InkColor HighlightFor(InkColor pen)
    {
        foreach (var h in HighlightColors)
        {
            if (string.Equals(h.Name, pen.Name, StringComparison.OrdinalIgnoreCase))
            {
                return h;
            }
        }

        return DefaultHighlightColor;
    }

    /// <summary>
    /// The palette a tool should OFFER.
    ///
    /// This exists because the highlighter palette being correct is not the
    /// same as it being reachable. The colours below were right, and a test
    /// asserted they were right, while the picker on screen stayed bound to
    /// the pen list the whole time: you armed the highlighter and were shown
    /// opaque pen swatches. Which list a tool offers is now a decision that
    /// can be tested rather than a binding nobody looks at.
    /// </summary>
    public static IReadOnlyList<InkColor> PaletteFor(bool highlighting) =>
        highlighting ? HighlightColors : Colors;

    public static InkColor DefaultColor => Colors[0];

    public static InkWidth DefaultWidth => Widths[1];

    public static InkColor DefaultHighlightColor => HighlightColors[0];

    /// <summary>
    /// The same colour at a different opacity, as "#AARRGGBB".
    ///
    /// Opacity is stored in the colour rather than alongside it because that is
    /// how it reaches the file: highlight and ink annotations carry an alpha
    /// component all the way down to the Rust side, so a separate opacity value
    /// would be a second source of truth that has to be recombined at every
    /// call site, and forgetting once means an edit that looks right on the
    /// overlay and comes out wrong in the saved PDF.
    /// </summary>
    /// <param name="opacity">0 to 1; clamped.</param>
    public static string WithOpacity(string hex, double opacity)
    {
        var (_, r, g, b) = ParseHex(hex);
        byte a = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>The opacity a colour already carries, 0 to 1.</summary>
    public static double OpacityOf(string hex) => ParseHex(hex).A / 255.0;

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
