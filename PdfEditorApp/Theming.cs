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

    /// <summary>The surface the pages sit on.</summary>
    public static Brush CanvasBrush(AppTheme theme) => Fill(Surfaces(theme).Canvas);

    /// <summary>
    /// The tab in front, as a bare colour: the brush behind the tabs has to be
    /// RECOLOURED in place rather than replaced.
    ///
    /// It follows the CHROME, the same surface as the tool rail and the
    /// rulers. Following the canvas was the obvious-looking choice, since the
    /// canvas is what lies below, and it is wrong: the canvas is deliberately
    /// dark even in the light themes so that a white page has an edge, so a
    /// tab painted with it came out near-black in a light title bar. Chrome is
    /// lighter than the strip in every theme, which is what makes the front tab
    /// read as raised rather than as a hole.
    /// </summary>
    public static Color TabColor(AppTheme theme) => Opaque(Surfaces(theme).Chrome);

    /// <summary>
    /// Panels and bars: the tool rail, the property bar, the floating status
    /// bar, the thumbnails.
    /// </summary>
    public static Brush ChromeBrush(AppTheme theme) => Fill(Surfaces(theme).Chrome);

    /// <summary>The title strip and tab row, the deepest surface of the three.</summary>
    public static Brush WindowBrush(AppTheme theme) => Fill(Surfaces(theme).Window);

    /// <summary>
    /// The numbers come from ThemePalette, which is in the testable project so
    /// the rules about them can be asserted: every theme states all three
    /// surfaces, and the canvas is never lighter than its own chrome. Both were
    /// learned from bugs, and neither is visible from here.
    ///
    /// System is resolved against the APP's requested theme, which is the one
    /// Windows handed it at startup.
    ///
    /// The intensity comes from the store rather than from the caller: every
    /// call site wants the colours as the user has them set, and threading a
    /// second argument through all of them would only create the chance of one
    /// place forgetting it.
    /// </summary>
    private static SurfaceColors Surfaces(AppTheme theme) => ThemePalette.For(
        theme,
        Application.Current.RequestedTheme == ApplicationTheme.Dark,
        SettingsStore.Current.ColorIntensity);

    private static Brush Fill(ThemeColor c) => new SolidColorBrush(Opaque(c));

    private static Color Opaque(ThemeColor c) => Color.FromArgb(0xFF, c.R, c.G, c.B);

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
