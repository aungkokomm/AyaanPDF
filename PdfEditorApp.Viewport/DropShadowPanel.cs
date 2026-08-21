using System;
using System.Collections.Generic;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A drop shadow as the property bar's controls hold it.
///
/// The controls speak in what a person sets: a switch, a direction, two lengths
/// in POINTS and an opacity in PERCENT. The model speaks in normalized lengths
/// and a colour whose alpha is the opacity. This record is the first of those,
/// and <see cref="DropShadowPanel"/> is the conversion between them.
///
/// Points rather than the model's normalized fractions, because "12pt of blur"
/// is a thing a person can picture and "0.02 of the page width" is not. The
/// page width is what converts, which is why every method here takes one.
///
/// <paramref name="ColorHex"/> is "#RRGGBB" with NO alpha: opacity is a control
/// of its own and having it in two places would need a rule about which wins.
/// </summary>
public readonly record struct DropShadowControls(
    bool Enabled,
    double AngleDeg,
    double DistancePts,
    double BlurPts,
    int OpacityPercent,
    string ColorHex);

/// <summary>
/// The Drop Shadow row: what its controls show for a shape, and what a shape
/// gets when they change.
///
/// Pure, and here rather than in the view, for the reason every other layout
/// rule is: the app is a WinUI project and the test assembly cannot load it, so
/// anything left in the view is only ever checked by running the app and
/// looking. That is how a control ships doing nothing.
///
/// THE UI RANGE IS NARROWER THAN THE MODEL'S, deliberately. The renderer
/// accepts any softness; the controls stop at <see cref="MaxBlurNormalized"/>
/// because a blur is the one effect whose cost grows with its size, and a
/// measured 3 percent of the page width is already softer than anybody asks
/// for. A file made elsewhere with a larger value still loads and still draws;
/// it simply cannot be dialled higher here.
/// </summary>
public static class DropShadowPanel
{
    /// <summary>
    /// The largest blur the controls offer, as a fraction of the page's width.
    ///
    /// About 18pt on Letter. Chosen from measurement rather than taste: the
    /// blur costs roughly 7ms a frame at this size and twice that again by 5
    /// percent, and design tools default to a quarter of it.
    /// </summary>
    public const double MaxBlurNormalized = 0.03;

    /// <summary>How far the shadow can be thrown, as a fraction of the page's width.</summary>
    public const double MaxDistanceNormalized = 0.05;

    /// <summary>
    /// The faintest shadow the controls offer. Not zero: a shadow at no opacity
    /// is no shadow, and the model already says so through the colour's alpha,
    /// so allowing it here would give two ways to switch one off.
    /// </summary>
    public const int MinOpacityPercent = 10;

    /// <summary>The eight preset directions, in degrees, as the light travels.</summary>
    public static IReadOnlyList<int> Directions { get; } =
        new[] { 0, 45, 90, 135, 180, 225, 270, 315 };

    public static double MaxBlurPts(double pageWidthPts) => MaxBlurNormalized * pageWidthPts;

    public static double MaxDistancePts(double pageWidthPts) => MaxDistanceNormalized * pageWidthPts;

    /// <summary>
    /// What the controls show for a shape that has no shadow yet, so switching
    /// one on gives something worth looking at rather than a black slab
    /// directly underneath.
    ///
    /// Lit from the upper left, which is where nearly every drawing tool puts
    /// its default light and where a reader expects it.
    /// </summary>
    public static DropShadowControls Defaults => new(
        Enabled: false,
        AngleDeg: 135,
        DistancePts: 4,
        BlurPts: 4,
        OpacityPercent: 50,
        ColorHex: "#000000");

    /// <summary>The controls for a shape's actual shadow, or the defaults switched off.</summary>
    public static DropShadowControls From(DropShadow? shadow, double pageWidthPts)
    {
        if (shadow is not { } s || pageWidthPts <= 0)
        {
            return Defaults;
        }

        return new DropShadowControls(
            Enabled: true,
            AngleDeg: s.AngleDeg,
            DistancePts: s.Distance * pageWidthPts,
            BlurPts: s.Softness * pageWidthPts,
            OpacityPercent: (int)Math.Round(s.Color.A * 100.0 / 255.0),
            ColorHex: $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}");
    }

    /// <summary>The shadow the controls describe, or null when they are switched off.</summary>
    public static DropShadow? ToShadow(DropShadowControls c, double pageWidthPts)
    {
        if (!c.Enabled || pageWidthPts <= 0)
        {
            return null;
        }

        var (r, g, b) = RgbOf(c.ColorHex);
        byte alpha = (byte)Math.Clamp(
            (int)Math.Round(c.OpacityPercent * 255.0 / 100.0), 0, 255);

        return new DropShadow(
            AngleDeg: c.AngleDeg,
            Distance: Normalized(c.DistancePts, pageWidthPts, MaxDistanceNormalized),
            Color: new RenderColor(alpha, r, g, b),
            Softness: Normalized(c.BlurPts, pageWidthPts, MaxBlurNormalized));

        // Spread is left at its default of nothing on purpose: the model stores
        // it and no renderer draws it, so the row does not offer it.
    }

    /// <summary>
    /// A length in points as the fraction of the page width the model wants,
    /// capped at what the row offers.
    ///
    /// Anything that is not a length reads as nothing rather than being
    /// rejected: these come from sliders and boxes a person can empty, and a
    /// shadow at no distance is a legitimate thing to ask for.
    /// </summary>
    private static double Normalized(double pts, double pageWidthPts, double maxNormalized) =>
        !double.IsFinite(pts) || pts <= 0
            ? 0
            : Math.Min(pts / pageWidthPts, maxNormalized);

    /// <summary>The nearest preset to an angle, for lighting the direction buttons.</summary>
    public static int NearestDirection(double angleDeg)
    {
        double wrapped = ((angleDeg % 360) + 360) % 360;
        int best = Directions[0];
        double closest = double.MaxValue;

        foreach (int preset in Directions)
        {
            // The SHORT way round: 359 degrees is one degree from zero, not
            // three hundred and fifty nine.
            double apart = Math.Abs(wrapped - preset);
            double gap = Math.Min(apart, 360 - apart);
            if (gap < closest) { closest = gap; best = preset; }
        }

        return best;
    }

    /// <summary>"#RRGGBB" from any of "#AARRGGBB", "#RRGGBB" or the bare forms.</summary>
    public static string RgbHexOf(string? hex)
    {
        string text = (hex ?? string.Empty).TrimStart('#');
        if (text.Length == 8) { text = text[2..]; }
        return text.Length == 6 ? "#" + text.ToUpperInvariant() : "#000000";
    }

    public static (byte R, byte G, byte B) RgbOf(string? hex)
    {
        string text = RgbHexOf(hex)[1..];
        return (
            byte.Parse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
