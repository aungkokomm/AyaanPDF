using System;

namespace PdfEditorApp.Viewport;

/// <summary>An opaque colour, as plain bytes. No XAML types, so it is testable.</summary>
public readonly record struct ThemeColor(byte R, byte G, byte B)
{
    /// <summary>
    /// Perceived brightness, 0 to 255, on the usual weighted sum.
    ///
    /// Only ever used to COMPARE two surfaces of the same theme, which is what
    /// the "canvas is never lighter than the chrome" rule needs. It is not
    /// meant as a contrast measure.
    /// </summary>
    public double Luminance => (0.299 * R) + (0.587 * G) + (0.114 * B);
}

/// <summary>The three surfaces a theme paints, deepest last.</summary>
/// <param name="Canvas">Behind the pages.</param>
/// <param name="Chrome">Tool rail, property bar, status bar, thumbnails.</param>
/// <param name="Window">Title strip and tab row.</param>
public readonly record struct SurfaceColors(
    ThemeColor Canvas,
    ThemeColor Chrome,
    ThemeColor Window);

/// <summary>
/// The colours behind every theme.
///
/// Here rather than beside the XAML because theming has now gone wrong twice in
/// ways a test would have caught: a theme that returned "no colour" left the
/// previous theme's paint on screen, and a canvas lighter than its own chrome
/// read as a hole cut in the app. Both are properties of the numbers alone, so
/// the numbers live where they can be asserted on.
/// </summary>
public static class ThemePalette
{
    /// <summary>
    /// Midnight blue, #0F1A3C. Every other blue in the theme derives from it.
    ///
    /// Was #060866, which is an electric indigo: almost no red or green against
    /// a lot of blue, so a full window of it glowed rather than receded. This
    /// is darker overall and much less saturated, which is what "midnight"
    /// means and what a surface a page sits on has to do.
    ///
    /// Deliberately not CSS's named midnightblue (#191970), which is lighter
    /// and more violet than the colour it is named after.
    /// </summary>
    public static readonly ThemeColor MidnightBlueBase = new(0x0F, 0x1A, 0x3C);

    /// <summary>
    /// The surfaces for a theme.
    ///
    /// EVERY theme answers, including Light and Dark. There is deliberately no
    /// "leave it to the framework" case: a theme that declines to state a
    /// colour is a theme that inherits the last one's.
    /// </summary>
    /// <param name="systemIsDark">
    /// What System resolves to right now. Passed in rather than read here, so
    /// this stays free of any dependency on a running application.
    /// </param>
    /// <param name="intensity">
    /// -50 to +50, 0 being the theme's own colours. Applied to all three
    /// surfaces EQUALLY and last, so it shifts the whole theme without
    /// disturbing the relationships between its surfaces.
    /// </param>
    public static SurfaceColors For(AppTheme theme, bool systemIsDark, int intensity)
    {
        var s = Base(theme, systemIsDark);
        if (intensity == 0)
        {
            return s;
        }

        // Shift is per-channel affine, c -> c(1-a) + 255a for lighten and
        // c -> c(1-a) for darken. Both are monotonic in c, and luminance is a
        // weighted sum whose weights come to 1, so an equal shift cannot
        // reorder two surfaces: whatever was darker stays darker. That is what
        // lets the slider exist without a way to break the canvas-versus-chrome
        // rule.
        return new SurfaceColors(Shift(s.Canvas, intensity), Shift(s.Chrome, intensity), Shift(s.Window, intensity));
    }

    /// <summary>The theme's own colours, before any intensity shift.</summary>
    public static SurfaceColors For(AppTheme theme, bool systemIsDark) =>
        For(theme, systemIsDark, 0);

    /// <summary>
    /// Moves a colour by an intensity setting: positive lightens, negative
    /// darkens, and +/-50 reaches half way to white or to black.
    /// </summary>
    public static ThemeColor Shift(ThemeColor c, int intensity)
    {
        double amount = Math.Abs(intensity) / 100.0;
        return intensity > 0 ? Lighten(c, amount) : Darken(c, amount);
    }

    private static SurfaceColors Base(AppTheme theme, bool systemIsDark) => theme switch
    {
        AppTheme.Sepia => new SurfaceColors(
            Canvas: new(0x4E, 0x3F, 0x2A),
            Chrome: new(0xF2, 0xE8, 0xD2),
            Window: new(0xE4, 0xD5, 0xB4)),

        // One family, generated from the base, so the three cannot drift apart
        // and changing the base moves all of them together.
        AppTheme.MidnightBlue => new SurfaceColors(
            Canvas: MidnightBlueBase,
            Chrome: Lighten(MidnightBlueBase, 0.14),
            Window: Darken(MidnightBlueBase, 0.35)),

        _ => IsDark(theme, systemIsDark)
            ? new SurfaceColors(
                Canvas: new(0x25, 0x25, 0x28),
                Chrome: new(0x2C, 0x2C, 0x30),
                Window: new(0x1F, 0x1F, 0x22))
            : new SurfaceColors(
                // The light themes still keep a DARK surround: a sheet reads as
                // a sheet because of the contrast behind it, and a white page on
                // a white canvas has no edges.
                Canvas: new(0x3A, 0x3A, 0x3D),
                Chrome: new(0xF3, 0xF3, 0xF3),
                Window: new(0xEA, 0xEA, 0xEA)),
    };

    /// <summary>
    /// Whether what is on screen is a dark theme, with System resolved by the
    /// answer the caller supplies.
    /// </summary>
    public static bool IsDark(AppTheme theme, bool systemIsDark) =>
        theme == AppTheme.System ? systemIsDark : AppSettings.IsDark(theme);

    public static ThemeColor Lighten(ThemeColor c, double amount) => new(
        (byte)(c.R + ((255 - c.R) * amount)),
        (byte)(c.G + ((255 - c.G) * amount)),
        (byte)(c.B + ((255 - c.B) * amount)));

    public static ThemeColor Darken(ThemeColor c, double amount) => new(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}
