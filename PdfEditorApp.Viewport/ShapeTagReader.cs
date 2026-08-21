using System;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What one of our shape annotations records in its <c>/Contents</c>, decoded.
///
/// Colours are "#AARRGGBB" to match the rest of the app, NOT the order they are
/// stored in: the tag writes the stroke as RRGGBBAA and the fill as AARRGGBB,
/// which is a trap worth converting away from once, here, rather than at every
/// call site.
///
/// <paramref name="FillHex"/> is null for a stroke-only shape, which was the
/// original behaviour and is still what an all-zero fill field means.
///
/// <paramref name="FlipX"/> and <paramref name="FlipY"/> record which way the
/// drag ran. A rectangle does not care, but an arrow does: it points where it
/// was dragged, and a box rebuilt from min/max would have thrown that away.
///
/// <paramref name="StrokeWidthPts"/> and <paramref name="CornerRadiusPts"/> are
/// in PDF points, as stored. Converting them to normalized units needs the page
/// width, which a tag does not carry.
///
/// <paramref name="ShadowHex"/> is null when the shape casts no shadow, which
/// is every shape written before effects existed and every one that has not
/// been given one. It is the one field that decides whether there is a shadow
/// at all, exactly as <paramref name="FillHex"/> decides whether there is a
/// fill, so the rest are only meaningful when it is non-null.
///
/// The shadow is described by ANGLE AND DISTANCE, not by an x/y offset, because
/// an angle cannot be recovered from an offset of no length. The angle is where
/// the LIGHT is, in degrees counter-clockwise from due east; the shadow falls
/// the opposite way. <paramref name="ShadowSoftnessPts"/> is the blur RADIUS:
/// zero stays a vector path in the file, and anything above it is rasterised by
/// Skia into the annotation. <paramref name="ShadowSpreadPts"/> is RESERVED,
/// stored and round-tripped and drawn by nothing. See <see cref="DropShadow"/>.
///
/// <paramref name="EffectsText"/> is the tag's whole TAIL, verbatim: one
/// self-describing field per effect. It is what a rebuild hands back to the
/// core, which carries effects rather than modelling them, so a shape keeps an
/// effect this build cannot name. The named Shadow fields above are the same
/// data read for the one effect that has a UI.
///
/// <paramref name="BoxWidthPts"/> and <paramref name="BoxHeightPts"/> are the
/// shape's own UPRIGHT size, in points, and are written only by a shape that is
/// turned or rounded. Zero means "not recorded". They matter because a rotated
/// shape's /Rect is the axis-aligned box of the TURNED content: bigger than the
/// shape in both axes, and not invertible at 45 degrees, where infinitely many
/// boxes share one AABB. The centre of /Rect is exact at any angle, so centre
/// plus this size reconstructs the shape precisely. render_core writes both
/// fields and reads them back in <c>parse_shape_tag</c>; this reader used to
/// stop at the radius and drop them on the floor.
/// </summary>
public readonly record struct ShapeTag(
    ShapeKind Kind,
    string StrokeHex,
    double StrokeWidthPts,
    bool FlipX,
    bool FlipY,
    double RotationDeg,
    string? FillHex,
    double CornerRadiusPts,
    double BoxWidthPts = 0,
    double BoxHeightPts = 0,
    double ShadowAngleDeg = 0,
    double ShadowDistancePts = 0,
    double ShadowSoftnessPts = 0,
    double ShadowSpreadPts = 0,
    string? ShadowHex = null,
    string EffectsText = "");

/// <summary>
/// Reads the tag a shape stores, the C# side of <c>parse_shape_tag</c> in
/// render_core.
///
/// Format: <c>AyaanShape:kind:RRGGBBAA:widthPts:fx:fy[:rot[:fillAARRGGBB[:radiusPts[:boxWPts:boxHPts[:s(a=,d=,b=,p=,c=)]]]]]</c>
///
/// The shadow is ONE self-describing field rather than several positional ones.
/// Positional fields do not extend: a second effect would append more of them
/// and force every shadowed shape to emit intermediate zeros to reach them.
/// Named keys inside one token mean a later effect is another token, and a key
/// this build has never heard of is skipped instead of shifting everything
/// after it.
///
/// The trailing fields were appended over time and are absent from older tags,
/// so every one of them reads as its historic default rather than failing the
/// parse. A shape written before rotation existed is upright, not unreadable.
///
/// This exists because the same six fields were being picked out of the same
/// string by six separate private helpers in the view model, each re-splitting
/// it and each with its own idea of what a malformed value meant. One reader,
/// one set of defaults.
/// </summary>
public static class ShapeTagReader
{
    public const string Prefix = "AyaanShape:";

    // An annotation's /Contents may carry a stable-identity prefix in front of
    // the tag body: "ID:<32 lowercase hex>|". Mirrors ID_PREFIX / ID_SEPARATOR /
    // ID_HEX_LEN in render_core.
    private const string IdPrefix = "ID:";
    private const char IdSeparator = '|';
    private const int IdHexLength = 32;

    /// <summary>
    /// The tag body, with any identity prefix removed.
    ///
    /// Rust's <c>parse_shape_tag</c> strips this before parsing, and every
    /// reader has to. When only some of them did, every shape stopped being
    /// recognised as a shape the moment identity was introduced, and group
    /// moves silently fell through to a path that moved the rectangle without
    /// redrawing the shape. That cost a fortnight. A reader that only works on
    /// pre-stripped input is a reader waiting to be handed raw input.
    /// </summary>
    public static string? StripIdPrefix(string? contents)
    {
        if (contents is null || !contents.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return contents;
        }

        int sep = contents.IndexOf(IdSeparator, IdPrefix.Length);
        if (sep != IdPrefix.Length + IdHexLength) { return contents; }

        return contents[(sep + 1)..];
    }

    /// <summary>True if this /Contents is one of our shapes at all, with or
    /// without an identity prefix in front of it.</summary>
    public static bool IsShapeTag(string? contents) =>
        StripIdPrefix(contents) is string s && s.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Decodes a shape tag, or returns false for anything that is not one:
    /// another editor's annotation, one of our text boxes, or a truncated tag.
    ///
    /// A tag whose KIND is not one this build knows is rejected rather than
    /// guessed at. Rendering it as some other shape would be worse than not
    /// recognising it.
    /// </summary>
    public static bool TryParse(string? contents, out ShapeTag tag)
    {
        tag = default;
        string? body = StripIdPrefix(contents);
        if (body is null || !body.StartsWith(Prefix, StringComparison.Ordinal)) { return false; }

        string[] parts = body.Substring(Prefix.Length).Split(':');
        if (parts.Length < 5) { return false; }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kindNumber)
            || !Enum.IsDefined(typeof(ShapeKind), kindNumber))
        {
            return false;
        }

        string? stroke = StrokeHexFrom(parts[1]);
        if (stroke is null) { return false; }

        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double widthPts)
            || !double.IsFinite(widthPts) || widthPts <= 0)
        {
            return false;
        }

        var shadow = ShadowIn(parts.Length > 10 ? string.Join(":", parts[10..]) : "");

        tag = new ShapeTag(
            Kind: (ShapeKind)kindNumber,
            StrokeHex: stroke,
            StrokeWidthPts: widthPts,
            FlipX: parts[3] == "1",
            FlipY: parts[4] == "1",
            RotationDeg: OptionalNumber(parts, 5),
            FillHex: OptionalFill(parts, 6),
            CornerRadiusPts: Math.Max(0, OptionalNumber(parts, 7)),
            BoxWidthPts: OptionalPositive(parts, 8),
            BoxHeightPts: OptionalPositive(parts, 9),
            ShadowAngleDeg: shadow.AngleDeg,
            ShadowDistancePts: shadow.DistancePts,
            ShadowSoftnessPts: shadow.SoftnessPts,
            ShadowSpreadPts: shadow.SpreadPts,
            ShadowHex: shadow.Hex,
            EffectsText: parts.Length > 10 ? string.Join(":", parts[10..]) : "");
        return true;
    }

    /// <summary>
    /// The DROP SHADOW inside an effects string, named, or nothing when there
    /// is none: <c>s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000)</c>.
    ///
    /// The one effect with a UI, so the one that gets a named reader. The
    /// effects text itself is carried around whole, because nothing between
    /// here and the file needs to know what is in it.
    ///
    /// Anything malformed yields NO shadow, which is the same answer as a shape
    /// that never had one, and never a half-read one with invented values. The
    /// C# half of <c>shadow_effect_of</c> in render_core.
    /// </summary>
    public static (double AngleDeg, double DistancePts, double SoftnessPts,
                   double SpreadPts, string? Hex) ShadowIn(string? effects)
    {
        var none = (0.0, 0.0, 0.0, 0.0, (string?)null);
        if (string.IsNullOrEmpty(effects)) { return none; }

        string? field = null;
        foreach (string candidate in effects.Split(':'))
        {
            if (candidate.StartsWith("s(", StringComparison.Ordinal))
            {
                field = candidate;
                break;
            }
        }

        if (field is null || !field.EndsWith(")", StringComparison.Ordinal))
        {
            return none;
        }

        double angle = 0, distance = 0, softness = 0, spread = 0;
        string? hex = null;

        foreach (string pair in field[2..^1].Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) { return none; }

            string key = pair[..eq];
            string value = pair[(eq + 1)..];

            switch (key)
            {
                case "a":
                    if (!Number(value, out angle)) { return none; }
                    break;
                case "d":
                    if (!NonNegative(value, out distance)) { return none; }
                    break;
                case "b":
                    if (!NonNegative(value, out softness)) { return none; }
                    break;
                case "p":
                    if (!NonNegative(value, out spread)) { return none; }
                    break;
                case "c":
                    if (value.Length != 8 || !IsHex(value)) { return none; }
                    hex = "#" + value;
                    break;
                // Forward compatibility: a key from a later build is not an error.
                default:
                    break;
            }
        }

        // The colour is what says a shadow exists, the same bargain the fill
        // makes, and one that paints nothing is not one.
        if (hex is null || hex == "#00000000") { return none; }

        return (angle, distance, softness, spread, hex);
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static bool NonNegative(string text, out double value) =>
        Number(text, out value) && value >= 0;

    /// <summary>Stroke is stored RRGGBBAA; the app speaks "#AARRGGBB".</summary>
    private static string? StrokeHexFrom(string rgba)
    {
        if (rgba.Length != 8 || !IsHex(rgba)) { return null; }
        return $"#{rgba.Substring(6, 2)}{rgba.Substring(0, 6)}";
    }

    /// <summary>Fill is already stored AARRGGBB. All zeroes means stroke-only,
    /// which is null rather than transparent black.</summary>
    private static string? OptionalFill(string[] parts, int at)
    {
        if (parts.Length <= at) { return null; }
        string fill = parts[at];
        if (fill.Length != 8 || !IsHex(fill)) { return null; }
        return string.Equals(fill, "00000000", StringComparison.OrdinalIgnoreCase) ? null : "#" + fill;
    }

    /// <summary>An absent or unparseable trailing number is 0, never a failure:
    /// a corrupt byte at the end of a tag must not turn a good shape into an
    /// unknown mark.</summary>
    private static double OptionalNumber(string[] parts, int at)
    {
        if (parts.Length <= at) { return 0; }
        return double.TryParse(parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
               && double.IsFinite(v)
            ? v
            : 0;
    }

    /// <summary>
    /// A trailing field that is only meaningful when it is POSITIVE, so absent,
    /// unparseable, zero and negative all read the same: not recorded. Mirrors
    /// the <c>filter(|v| v.is_finite() &amp;&amp; *v &gt; 0.0)</c> guard the Rust
    /// reader applies to the same two fields, so a shape either has a usable
    /// upright box or has none, with no third state to handle downstream.
    /// </summary>
    private static double OptionalPositive(string[] parts, int at)
    {
        double v = OptionalNumber(parts, at);
        return v > 0 ? v : 0;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c)) { return false; }
        }
        return true;
    }
}
