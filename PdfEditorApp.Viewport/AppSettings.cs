using System;

namespace PdfEditorApp.Viewport;

/// <summary>How the app is coloured.</summary>
public enum AppTheme
{
    /// <summary>Follow Windows.</summary>
    System,
    Light,
    Dark,
    /// <summary>Warm paper, easier for long reading than white.</summary>
    Sepia,
    /// <summary>Dark, but blue rather than neutral grey.</summary>
    DarkBlue,
}

/// <summary>What the view does when a document opens.</summary>
public enum DefaultView
{
    /// <summary>The whole page, so the layout is visible at a glance.</summary>
    FitPage,
    /// <summary>Full width, which is what reading continuous text wants.</summary>
    FitWidth,
    /// <summary>100%, for work that needs true size.</summary>
    ActualSize,
}

/// <summary>
/// Everything the app remembers between runs.
///
/// A single record rather than scattered keys: every setting is written and
/// read in one place, so adding one cannot leave half the app looking for a
/// value that was never saved. Reading tolerates anything, because the file is
/// the user's and may be hand-edited, half-written, or from a future version.
/// </summary>
public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// Fit page by default: it shows the shape of a document before anything
    /// is read, and is the safer answer for a file whose pages might be any
    /// size.
    /// </summary>
    public DefaultView DefaultView { get; init; } = DefaultView.FitPage;

    public bool ShowRulers { get; init; } = true;

    /// <summary>Matches the RulerUnit tags the menu already uses.</summary>
    public string RulerUnit { get; init; } = "Inches";

    /// <summary>Entries kept in the recent-files list.</summary>
    public int RecentLimit { get; init; } = 10;

    /// <summary>
    /// Brings anything unrecognised back to a sane value.
    ///
    /// Applied on LOAD, not on save, so a file written by a newer version, or
    /// edited by hand, degrades to something usable instead of leaving the app
    /// in a state no menu can represent.
    /// </summary>
    public AppSettings Sanitised() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        DefaultView = Enum.IsDefined(DefaultView) ? DefaultView : DefaultView.FitPage,
        RulerUnit = IsKnownUnit(RulerUnit) ? RulerUnit : "Inches",
        RecentLimit = Math.Clamp(RecentLimit, 1, 50),
    };

    private static bool IsKnownUnit(string unit) => unit is
        "Inches" or "Centimeters" or "Millimeters" or "Points" or "Picas";

    /// <summary>
    /// Whether this theme is a dark one, which decides the base the custom
    /// tints are built on: Sepia is a light theme with warm paper, Dark blue is
    /// a dark theme with a blue cast.
    /// </summary>
    public static bool IsDark(AppTheme theme) => theme is AppTheme.Dark or AppTheme.DarkBlue;
}
