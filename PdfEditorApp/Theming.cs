using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PdfEditorApp.Viewport;
using Windows.UI;

namespace PdfEditorApp;

/// <summary>
/// Applies a theme to the window.
///
/// Light, Dark and System are Fluent's own and cost nothing but an
/// ElementTheme. Sepia and Dark blue do not exist in Fluent, so they are built
/// as a base theme plus a handful of overridden brushes: Sepia is LIGHT with
/// warm paper, Dark blue is DARK with a blue cast. Getting that base wrong
/// paints light text on a light background, which is why AppSettings.IsDark
/// decides it rather than a guess at the call site.
///
/// Only a few brushes are overridden, deliberately. Restyling every Fluent
/// brush would mean owning a whole theme and re-checking it on every WinUI
/// update; overriding the surfaces the eye actually reads gives most of the
/// effect and keeps every control's own states intact.
/// </summary>
internal static class Theming
{
    public static void Apply(Window window, AppTheme theme)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = BaseTheme(theme);
        }
    }

    /// <summary>
    /// The Fluent theme a given choice sits on. Sepia is a LIGHT theme with
    /// warm paper; Dark blue is a DARK theme with a blue cast. Getting this
    /// backwards paints light text on a light background.
    /// </summary>
    public static ElementTheme BaseTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light or AppTheme.Sepia => ElementTheme.Light,
        AppTheme.Dark or AppTheme.DarkBlue => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// The surface the pages sit on.
    ///
    /// Always clearly darker than a white page, whatever the theme: a sheet
    /// reads as a sheet because of the contrast with what is behind it, so
    /// even the light themes keep a deep surround rather than going pale.
    /// </summary>
    public static Brush CanvasBrush(AppTheme theme) => new SolidColorBrush(theme switch
    {
        AppTheme.Sepia => Rgb(0x5C, 0x4B, 0x33),
        AppTheme.DarkBlue => Rgb(0x1B, 0x3A, 0x78),
        _ => Rgb(0x3A, 0x3A, 0x3D),
    });

    /// <summary>
    /// Panels and bars: the tool rail, the property bar, the floating status
    /// bar. Slightly lighter than the window so they read as surfaces sitting
    /// on it.
    /// </summary>
    public static Brush? ChromeBrush(AppTheme theme) => theme switch
    {
        AppTheme.Sepia => new SolidColorBrush(Rgb(0xF2, 0xE8, 0xD2)),
        AppTheme.DarkBlue => new SolidColorBrush(Rgb(0x14, 0x28, 0x59)),
        // Light and Dark keep Fluent's own materials, including the acrylic
        // the rail uses. Only the invented themes need painting by hand.
        _ => null,
    };

    /// <summary>The title strip and tab row, the deepest surface of the three.</summary>
    public static Brush? WindowBrush(AppTheme theme) => theme switch
    {
        AppTheme.Sepia => new SolidColorBrush(Rgb(0xE4, 0xD5, 0xB4)),
        AppTheme.DarkBlue => new SolidColorBrush(Rgb(0x0D, 0x1C, 0x40)),
        _ => null,
    };

    /// <summary>
    /// Tick marks on the rulers.
    ///
    /// Chosen here rather than pulled from Application.Current.Resources,
    /// which is the APP dictionary and does not follow a theme set on the root
    /// element. Reading TextFillColorSecondaryBrush from it in dark mode
    /// returned the LIGHT theme's dark grey and drew near-black ticks on a dark
    /// ruler. Callers pass the element's ActualTheme, which resolves System to
    /// a real answer.
    /// </summary>
    public static Brush RulerTickBrush(bool dark) =>
        new SolidColorBrush(dark ? Rgb(0xA8, 0xA8, 0xAE) : Rgb(0x5A, 0x5A, 0x60));

    /// <summary>Ruler numbers, brighter than the ticks so they stay readable at 11px.</summary>
    public static Brush RulerTextBrush(bool dark) =>
        new SolidColorBrush(dark ? Rgb(0xE8, 0xE8, 0xED) : Rgb(0x20, 0x20, 0x24));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
}
