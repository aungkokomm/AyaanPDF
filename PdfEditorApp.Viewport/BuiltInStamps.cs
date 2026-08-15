using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>How a built-in stamp is drawn.</summary>
public enum StampStyle
{
    /// <summary>Rounded rectangle around a word. The default office stamp.</summary>
    Badge,

    /// <summary>Wider, square-cornered, thinner rule. For words that want to
    /// read as a banner across the page rather than a label on it.</summary>
    Banner,

    /// <summary>A mark and a word side by side, for SIGN HERE.</summary>
    Flag,

    /// <summary>A mark on its own, no text and no border.</summary>
    Mark,
}

/// <summary>
/// One built-in stamp.
/// </summary>
/// <param name="Id">Stable, lowercase, and part of the picker's identity. Never
/// shown to anyone.</param>
/// <param name="Label">What is printed. Empty for a <see cref="StampStyle.Mark"/>.</param>
/// <param name="Color">Ink colour. Both the border and the text take it.</param>
/// <param name="Diagonal">Drawn at a rising angle, the way DRAFT and VOID are
/// stamped across a page.</param>
/// <param name="DateSuffix">Prints today's date under the label. This is the
/// whole reason these are generated rather than shipped as images.</param>
/// <param name="MarkPath">SVG path data drawn beside or instead of the label.</param>
public sealed record BuiltInStamp(
    string Id,
    string Label,
    StampStyle Style,
    ThemeColor Color,
    bool Diagonal = false,
    bool DateSuffix = false,
    string MarkPath = "");

/// <summary>
/// Every number that decides how a stamp looks, in one place.
///
/// Gathered into a record rather than scattered through the renderer so the
/// look can be retuned, or the typeface swapped, without touching the drawing
/// code or anything that calls it. The ratios are all relative to the stamp's
/// HEIGHT, so a stamp placed at any size is the same design rather than the
/// same design with the border creeping.
/// </summary>
/// <param name="FontFamily">Family name as DirectWrite reports it.</param>
/// <param name="FontFile">File name under Assets\Fonts.</param>
public sealed record StampTheme(
    string FontFamily,
    string FontFile,
    double BadgeAspect,
    double BannerAspect,
    double FlagAspect,
    double BorderFraction,
    double CornerFraction,
    double InsetFraction,
    double CapHeightFraction,
    double DateHeightFraction,
    double DiagonalDegrees)
{
    /// <summary>
    /// Oswald, condensed and heavy: the shape everyone reads as a rubber stamp,
    /// and narrow enough that CONFIDENTIAL fits a badge without shrinking.
    ///
    /// Swapping the typeface is these two strings and nothing else, which is
    /// the point of this record existing.
    /// </summary>
    public static readonly StampTheme Default = new(
        FontFamily: "Oswald",
        FontFile: "Oswald-Bold.ttf",
        BadgeAspect: 3.0,
        BannerAspect: 4.2,
        FlagAspect: 3.4,
        BorderFraction: 0.075,
        CornerFraction: 0.18,
        InsetFraction: 0.16,
        CapHeightFraction: 0.46,
        DateHeightFraction: 0.24,
        DiagonalDegrees: -12);

    /// <summary>Width divided by height, for a style.</summary>
    public double AspectFor(StampStyle style) => style switch
    {
        StampStyle.Badge => BadgeAspect,
        StampStyle.Banner => BannerAspect,
        StampStyle.Flag => FlagAspect,
        StampStyle.Mark => 1.0,
        _ => BadgeAspect,
    };
}

/// <summary>
/// Converting what a GPU render target hands back into what PDFium wants.
///
/// Win2D draws PREMULTIPLIED: every colour channel already has the alpha
/// multiplied into it, so a half-transparent red is stored as half red.
/// StampLibrary.DecodeAsync goes out of its way to ask WIC for STRAIGHT alpha
/// instead, and records why: PDFium builds the image's soft mask from these
/// bytes, and premultiplied colour darkens every semi-transparent edge against
/// it. A generated stamp is nothing but semi-transparent edges, because every
/// letter is antialiased, so skipping this would fringe all of them.
/// </summary>
public static class StampAlpha
{
    /// <summary>
    /// Divides the alpha back out of each channel, in place.
    ///
    /// Fully transparent pixels are left alone rather than divided by zero;
    /// their colour is unused and any value is as good as another.
    /// </summary>
    public static void Unpremultiply(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a == 0 || a == 255)
            {
                continue;
            }

            bgra[i] = Divide(bgra[i], a);
            bgra[i + 1] = Divide(bgra[i + 1], a);
            bgra[i + 2] = Divide(bgra[i + 2], a);
        }
    }

    /// <summary>
    /// Clamped, because rounding in the original multiply can leave a channel
    /// fractionally larger than its own alpha, and dividing that gives 256.
    /// </summary>
    private static byte Divide(byte channel, byte alpha) =>
        (byte)Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha);
}

/// <summary>The geometry for drawing one stamp at one size, all in pixels.</summary>
public readonly record struct StampMetrics(
    int Width,
    int Height,
    double Border,
    double CornerRadius,
    double Inset,
    double LabelSize,
    double DateSize);

/// <summary>
/// The built-in stamp library: what exists, and the arithmetic for drawing it.
///
/// Everything here is pure, for the reason StampPlacement records: the geometry
/// bugs that reached the user were all in code a test could not run. The actual
/// pixels are Win2D's job, in StampRenderer, and this decides what it draws.
/// </summary>
public static class BuiltInStamps
{
    /// <summary>
    /// Marks the stamps draw, as SVG path data on a 24x24 box.
    ///
    /// From Tabler Icons (MIT, Copyright (c) 2020-2026 Pawel Kuna). Kept as
    /// path strings rather than files because five paths in a source file need
    /// no build step, no packaging, and cannot go missing from an install.
    /// </summary>
    public static class Marks
    {
        public const string Check = "M5 12l5 5l10 -10";
        public const string Cross = "M18 6l-12 12 M6 6l12 12";
        public const string Question =
            "M3 12a9 9 0 1 0 18 0a9 9 0 0 0 -18 0 M12 16v.01 "
            + "M12 13a2 2 0 0 0 .914 -3.782a1.98 1.98 0 0 0 -2.414 .483";
        public const string Star =
            "M8.243 7.34l-6.38 .925l-.113 .023a1 1 0 0 0 -.44 1.684l4.622 4.499l-1.09 6.355"
            + "l-.013 .11a1 1 0 0 0 1.464 .944l5.706 -3l5.693 3l.1 .046a1 1 0 0 0 1.352 -1.1"
            + "l-1.091 -6.355l4.624 -4.5l.078 -.085a1 1 0 0 0 -.633 -1.62l-6.38 -.926l-2.852 -5.78"
            + "a1 1 0 0 0 -1.794 0l-2.853 5.78z";
        public const string Arrow = "M5 12l14 0 M15 16l4 -4 M15 8l4 4";

        /// <summary>The star is a filled silhouette; the rest are strokes.</summary>
        public static bool IsFilled(string path) => path == Star;
    }

    // The conventional stamp colours: green approves, red refuses or warns,
    // blue is procedural, grey is a duplicate.
    private static readonly ThemeColor Green = new(0x1B, 0x7F, 0x3B);
    private static readonly ThemeColor Red = new(0xC6, 0x28, 0x28);
    private static readonly ThemeColor Blue = new(0x15, 0x65, 0xC0);
    private static readonly ThemeColor Grey = new(0x5A, 0x5A, 0x5A);
    private static readonly ThemeColor Amber = new(0xE0, 0x86, 0x00);

    /// <summary>
    /// Tier 1: the twelve words, then the five marks.
    ///
    /// Order is the picker's order, and it is deliberate rather than
    /// alphabetical: the ones reached for most often are first, and the marks
    /// come last because they are a different kind of thing.
    /// </summary>
    public static readonly IReadOnlyList<BuiltInStamp> All = new BuiltInStamp[]
    {
        new("approved", "APPROVED", StampStyle.Badge, Green),
        new("not-approved", "NOT APPROVED", StampStyle.Badge, Red),
        new("draft", "DRAFT", StampStyle.Banner, Blue, Diagonal: true),
        new("final", "FINAL", StampStyle.Badge, Green),
        new("confidential", "CONFIDENTIAL", StampStyle.Banner, Red),
        new("reviewed", "REVIEWED", StampStyle.Badge, Blue),
        new("received", "RECEIVED", StampStyle.Badge, Blue, DateSuffix: true),
        new("void", "VOID", StampStyle.Banner, Red, Diagonal: true),
        new("urgent", "URGENT", StampStyle.Badge, Red),
        new("copy", "COPY", StampStyle.Banner, Grey, Diagonal: true),
        new("paid", "PAID", StampStyle.Badge, Green, DateSuffix: true),
        new("sign-here", "SIGN HERE", StampStyle.Flag, Amber, MarkPath: Marks.Arrow),

        new("mark-check", "", StampStyle.Mark, Green, MarkPath: Marks.Check),
        new("mark-cross", "", StampStyle.Mark, Red, MarkPath: Marks.Cross),
        new("mark-question", "", StampStyle.Mark, Blue, MarkPath: Marks.Question),
        new("mark-star", "", StampStyle.Mark, Amber, MarkPath: Marks.Star),
        new("mark-arrow", "", StampStyle.Mark, Red, MarkPath: Marks.Arrow),
    };

    public static BuiltInStamp? ById(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// How a built-in is named where a stamp file name would go.
    ///
    /// The colon is what makes this safe: Windows forbids it in a file name, so
    /// a built-in id can never collide with a PNG in the user's library, and
    /// the "last used" file can hold either kind without ambiguity.
    /// </summary>
    public const string IdPrefix = "builtin:";

    public static string EntryId(BuiltInStamp stamp) => IdPrefix + stamp.Id;

    /// <summary>The stamp an entry id names, or null when it names a file.</summary>
    public static BuiltInStamp? FromEntryId(string? entryId) =>
        entryId is not null && entryId.StartsWith(IdPrefix, StringComparison.Ordinal)
            ? ById(entryId[IdPrefix.Length..])
            : null;

    /// <summary>
    /// The date line under a stamp that carries one.
    ///
    /// The CULTURE's short date, not a fixed pattern: this app is used in
    /// places where 15/08/2026 and 8/15/2026 mean different things, and a
    /// stamp that reads wrong is worse than one that reads unfamiliar.
    /// </summary>
    public static string DateLine(BuiltInStamp stamp, DateTime today, CultureInfo culture) =>
        stamp.DateSuffix ? today.ToString("d", culture) : "";

    /// <summary>
    /// The pixel geometry for a stamp drawn at a given width.
    ///
    /// Height comes from the style's aspect rather than being asked for,
    /// because the caller wants "a stamp this wide" and the proportions are the
    /// design. Everything else is a fraction of the height, so the same stamp
    /// at 200px and at 800px is the same picture.
    /// </summary>
    public static StampMetrics Measure(BuiltInStamp stamp, int width, StampTheme theme)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "a stamp needs a positive width");
        }

        int height = Math.Max(1, (int)Math.Round(width / theme.AspectFor(stamp.Style)));

        // A stamp carrying a date splits its box between two lines, so the word
        // gets less of it. Without this the label kept its full size and the
        // date was pushed through the bottom border.
        double labelFraction = stamp.DateSuffix
            ? theme.CapHeightFraction * 0.72
            : theme.CapHeightFraction;

        return new StampMetrics(
            Width: width,
            Height: height,
            Border: height * theme.BorderFraction,
            CornerRadius: stamp.Style == StampStyle.Badge ? height * theme.CornerFraction : 0,
            Inset: height * theme.InsetFraction,
            LabelSize: height * labelFraction,
            DateSize: stamp.DateSuffix ? height * theme.DateHeightFraction : 0);
    }

    /// <summary>
    /// Shrinks a font size until the text fits the space it has.
    ///
    /// Pure, and takes the MEASURED width rather than guessing one: only
    /// DirectWrite knows how wide "NOT APPROVED" is in this font at this size,
    /// but the rule for what to do about it is arithmetic and belongs where it
    /// can be tested. The renderer measures, this decides.
    /// </summary>
    public static double FitSize(double size, double measuredWidth, double availableWidth)
    {
        if (size <= 0 || measuredWidth <= 0 || availableWidth <= 0)
        {
            return size;
        }

        return measuredWidth <= availableWidth
            ? size
            : size * (availableWidth / measuredWidth);
    }

    /// <summary>
    /// Where to put a line of text so its INK is centred in a band.
    ///
    /// Centring the layout box is not the same thing and looks wrong. A font's
    /// line box is ascender plus descender, and Oswald's ascender is tall, so
    /// the box carries far more space above the capitals than below them.
    /// Centring the box therefore pushes the visible letters down, which is
    /// exactly how the first version looked: every stamp's word sat low.
    ///
    /// So the caller measures the INKED bounds and passes them here, and this
    /// works out where the layout's origin has to go for that ink to land in
    /// the middle. Font-independent, which matters because the typeface is
    /// meant to be swappable.
    /// </summary>
    /// <param name="bandStart">Where the space to centre in begins.</param>
    /// <param name="bandSize">How big that space is.</param>
    /// <param name="inkOffset">Ink's offset from the layout origin.</param>
    /// <param name="inkSize">How big the ink is.</param>
    public static double CenterOffset(
        double bandStart, double bandSize, double inkOffset, double inkSize) =>
        bandStart + ((bandSize - inkSize) / 2) - inkOffset;

    /// <summary>
    /// How wide the stamp strip may be, given the room the viewport has.
    ///
    /// A fixed cap was the wrong shape for this: seventeen built-ins want about
    /// 1300px, so a 320px strip scrolled constantly even on a maximised window,
    /// while a strip with no cap at all would push the whole property bar wider
    /// than the window and put its own labels out of reach.
    ///
    /// The reserve is the rest of the row: the section label, the separator,
    /// the add and open-folder buttons, and the bar's own padding and margins.
    /// The floor keeps a few tiles reachable on a narrow window rather than
    /// collapsing to nothing.
    /// </summary>
    public static double StripMaxWidth(double viewportWidth) =>
        Math.Max(MinStripWidth, viewportWidth - StripReserve);

    public const double StripReserve = 200;
    public const double MinStripWidth = 220;

    /// <summary>
    /// The box a stamp is rotated inside, when it is a diagonal one.
    ///
    /// A rotated rectangle needs a bigger canvas or its corners are clipped,
    /// and the amount is exactly how much the rotated bounds grow. Returned as
    /// the new pixel size so the renderer never has to work it out.
    /// </summary>
    public static (int Width, int Height) RotatedBounds(int width, int height, double degrees)
    {
        double r = Math.Abs(degrees) * Math.PI / 180.0;
        double cos = Math.Cos(r);
        double sin = Math.Sin(r);

        return (
            (int)Math.Ceiling((width * cos) + (height * sin)),
            (int)Math.Ceiling((width * sin) + (height * cos)));
    }
}
