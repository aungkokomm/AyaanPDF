using System;
using System.Collections.Generic;

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
    MidnightBlue,
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

    /// <summary>
    /// How light or dark the theme's own colours are pushed, from -50 to +50,
    /// with 0 meaning exactly the colours the theme ships with.
    ///
    /// A shift applied on top of a theme rather than a set of extra themes:
    /// every surface moves by the same amount, so the relationships between
    /// them, which are what the theme actually is, survive the adjustment.
    /// </summary>
    public int ColorIntensity { get; init; }

    public bool ShowRulers { get; init; } = true;

    /// <summary>Matches the RulerUnit tags the menu already uses.</summary>
    public string RulerUnit { get; init; } = "Inches";

    /// <summary>Entries kept in the recent-files list.</summary>
    public int RecentLimit { get; init; } = 10;

    public BarDock StatusBarDock { get; init; } = BarDock.BottomCentre;

    /// <summary>
    /// Whether find distinguishes upper from lower case.
    ///
    /// Remembered because it is a property of how someone searches rather than
    /// of the document they are searching: a person looking for code or names
    /// wants it on every time, and having to set it again per session is the
    /// kind of small friction that makes an option not worth having.
    /// </summary>
    public bool SearchMatchCase { get; init; }

    /// <summary>Whether find requires the query to stand alone as a word.
    /// Remembered for the same reason.</summary>
    public bool SearchWholeWord { get; init; }

    /// <summary>
    /// Whether reopening a document returns you to where you stopped reading.
    ///
    /// On by default, because that is what every reader does and being returned
    /// to page 1 of a book you are half way through is the behaviour this was
    /// added to remove. Off is for anyone who wants every document to open the
    /// same way, and it stops positions being recorded at all rather than
    /// merely ignoring them.
    /// </summary>
    public bool RememberReadingPosition { get; init; } = true;

    /// <summary>
    /// Renders pages dark for night reading.
    ///
    /// Off by default: it changes how every document looks, and a reader who
    /// has not asked for it should see the page the way it was written. It is
    /// remembered because someone who reads at night reads at night.
    /// </summary>
    public bool NightMode { get; init; }

    /// <summary>
    /// Draws editable shapes with the Skia renderer rather than the XAML
    /// overlay.
    ///
    /// ON, as of Stage 4. It was off for as long as it was a candidate, and it
    /// turned on when the evidence was in rather than when it looked ready:
    /// 72 parity cells whose verdicts reproduce byte for byte across runs, no
    /// Skia rendering fault found in any of them, and a per-frame cost that
    /// stopped being proportional to the size of the window.
    ///
    /// The XAML overlay is still built and still correct, and setting this to
    /// false in settings.json restores it with no reload and no change to the
    /// document. Both layers exist and exactly one is shown, which is what
    /// makes this a switch rather than a migration.
    ///
    /// A STORED VALUE WINS. Settings are serialised in full, so a settings.json
    /// written before this changed carries an explicit false and keeps that
    /// machine on the XAML overlay. That is the right way round for something a
    /// person may have chosen, but it does mean this default governs fresh
    /// installs rather than existing ones.
    /// </summary>
    public bool UseSkiaShapeLayer { get; init; } = true;

    /// <summary>
    /// Whether the viewport shows the whole document as one scrolling stack or
    /// one page at a time.
    ///
    /// Continuous by default, which is what most PDFs are read as. Remembered
    /// because it describes how someone reads rather than which document they
    /// opened: a person who reads slides one at a time wants that next time
    /// too.
    /// </summary>
    public PageViewMode PageViewMode { get; init; } = PageViewMode.Continuous;

    /// <summary>
    /// Where reading was left in each document, keyed by upper-cased full path.
    ///
    /// A SEPARATE map rather than fields added to the recent-files list, which
    /// is a plain list of strings on disk. Changing that shape would mean
    /// migrating every existing settings file; an additional property simply
    /// deserialises to empty on one written by an older build, and an older
    /// build ignores it.
    /// </summary>
    public Dictionary<string, ReadingPosition> ReadingPositions { get; init; } = new();

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
    /// Which generation of this file's meaning the stored settings were
    /// written against.
    ///
    /// ZERO MEANS "BEFORE THIS EXISTED", which is the only reason the default
    /// is not the current version: a file written before versioning has no such
    /// key, deserialises to zero, and is therefore recognisable as old. A fresh
    /// AppSettings is zero too, and is migrated on its way to disk like any
    /// other, which costs nothing because every migration is already a no-op on
    /// a default value.
    ///
    /// Bumped only when a stored value's MEANING changes, not when a property
    /// is added. An added property deserialises to its default on an old file,
    /// which is already the right answer.
    /// </summary>
    public int SettingsVersion { get; init; }

    /// <summary>
    /// The generation this build writes. See <see cref="Migrated"/> for what
    /// each step does.
    /// </summary>
    public const int CurrentSettingsVersion = 1;

    /// <summary>
    /// Brings a file written by an older build up to what this one means.
    ///
    /// VERSION 1: <see cref="UseSkiaShapeLayer"/> stops being a stored answer
    /// and becomes the renderer.
    ///
    /// Settings are serialised in FULL, so every machine that ran the app while
    /// the Skia layer was still a candidate has an explicit false on disk. That
    /// false was never chosen: the flag has no UI and never had one, it was
    /// written out because every property is. Honouring it pinned those
    /// installs to the XAML overlay for good, and silently, which cost a whole
    /// diagnostic session to find: the gradient live preview draws through the
    /// Skia layer and could not appear at all.
    ///
    /// So a version 0 file is moved onto the current default and stamped. A
    /// file already stamped is left exactly as it is, which is what keeps the
    /// escape hatch working: setting the flag to false by hand AFTER this
    /// survives, because that one really is a choice.
    ///
    /// Applied on LOAD and then written back, so it happens once per install
    /// rather than once per launch.
    /// </summary>
    public AppSettings Migrated() => SettingsVersion >= CurrentSettingsVersion
        ? this
        : this with
        {
            UseSkiaShapeLayer = true,
            SettingsVersion = CurrentSettingsVersion,
        };

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
        ColorIntensity = Math.Clamp(ColorIntensity, MinIntensity, MaxIntensity),
        RulerUnit = IsKnownUnit(RulerUnit) ? RulerUnit : "Inches",
        RecentLimit = Math.Clamp(RecentLimit, 1, 50),
        StatusBarDock = Enum.IsDefined(StatusBarDock) ? StatusBarDock : BarDock.BottomCentre,
        PageViewMode = Enum.IsDefined(PageViewMode) ? PageViewMode : PageViewMode.Continuous,
    };

    private static bool IsKnownUnit(string unit) => unit is
        "Inches" or "Centimeters" or "Millimeters" or "Points" or "Picas";

    /// <summary>Full darkening of the theme's colours.</summary>
    public const int MinIntensity = -50;

    /// <summary>Full lightening of the theme's colours.</summary>
    public const int MaxIntensity = 50;

    /// <summary>
    /// Whether this theme is a dark one, which decides the base the custom
    /// tints are built on: Sepia is a light theme with warm paper, Dark blue is
    /// a dark theme with a blue cast.
    /// </summary>
    public static bool IsDark(AppTheme theme) => theme is AppTheme.Dark or AppTheme.MidnightBlue;
}
