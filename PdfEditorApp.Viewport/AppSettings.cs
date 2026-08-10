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

/// <summary>
/// Where the floating status bar sits.
///
/// A fixed set of anchors rather than a free position: a bar dropped in the
/// middle of the canvas would cover the page, and a remembered pixel position
/// would have to be re-validated against every window size and monitor change.
/// </summary>
public enum BarDock
{
    BottomCentre,
    BottomLeft,
    BottomRight,
    TopCentre,
    TopLeft,
    TopRight,
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

    public BarDock StatusBarDock { get; init; } = BarDock.BottomCentre;

    /// <summary>
    /// Which anchor a point in the viewport is nearest, for dropping the bar.
    ///
    /// Thirds horizontally and halves vertically: the middle third is wide
    /// enough that "centre" is easy to hit deliberately, which is where the bar
    /// belongs by default.
    /// </summary>
    public static BarDock NearestDock(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return BarDock.BottomCentre;
        }

        bool top = y < height / 2;
        double third = width / 3;

        if (x < third) { return top ? BarDock.TopLeft : BarDock.BottomLeft; }
        if (x > third * 2) { return top ? BarDock.TopRight : BarDock.BottomRight; }
        return top ? BarDock.TopCentre : BarDock.BottomCentre;
    }

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
        StatusBarDock = Enum.IsDefined(StatusBarDock) ? StatusBarDock : BarDock.BottomCentre,
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
