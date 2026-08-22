using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A glow as the property bar's controls hold it.
///
/// The shadow's row minus direction and distance, on exactly the same terms:
/// the controls speak in a switch, a length in POINTS and an opacity in
/// PERCENT, and <see cref="GlowPanel"/> is the conversion to and from the
/// model's normalized lengths and alpha-carrying colour.
///
/// <paramref name="ColorHex"/> is "#RRGGBB" with NO alpha, because opacity is a
/// control of its own and having it in two places would need a rule about which
/// wins.
/// </summary>
public readonly record struct GlowControls(
    bool Enabled,
    double BlurPts,
    int OpacityPercent,
    string ColorHex);

/// <summary>
/// The Glow row: what its controls show for a shape, and what a shape gets when
/// they change.
///
/// Pure, and here rather than in the view, for the reason every other layout
/// rule is: the app is a WinUI project and the test assembly cannot load it, so
/// anything left in the view is only ever checked by running the app and
/// looking. That is how a control ships doing nothing.
///
/// THE LIMITS ARE THE SHADOW'S. A glow's blur costs what a shadow's blur costs
/// and looks like what a shadow's blur looks like, so the maximum and the
/// faintest opacity are read from <see cref="DropShadowPanel"/> rather than
/// declared again. Two numbers meaning the same thing is two numbers to keep in
/// step.
/// </summary>
public static class GlowPanel
{
    /// <summary>
    /// The narrowest glow the controls offer, in points.
    ///
    /// NOT ZERO, which is the one way a glow differs from a shadow here. A
    /// shadow at no distance is a legitimate thing to ask for and still draws;
    /// a glow with no blur is the shape's own outline directly behind the
    /// shape, which is nothing at all. Allowing it would give a second way to
    /// switch the row off and a state where the switch says on and the page
    /// shows nothing.
    /// </summary>
    public const double MinBlurPts = 1;

    /// <summary>The widest, shared with the shadow's blur.</summary>
    public static double MaxBlurPts(double pageWidthPts) =>
        DropShadowPanel.MaxBlurPts(pageWidthPts);

    /// <summary>The faintest, shared with the shadow's opacity.</summary>
    public static int MinOpacityPercent => DropShadowPanel.MinOpacityPercent;

    /// <summary>
    /// What the controls show for a shape that has no glow yet, so switching
    /// one on gives something worth looking at.
    ///
    /// A warm yellow, which is what a glow is for: making a mark stand off the
    /// page. Black would be a shadow with the direction taken away.
    /// </summary>
    public static GlowControls Defaults => new(
        Enabled: false,
        BlurPts: 6,
        OpacityPercent: 75,
        ColorHex: "#FFD400");

    /// <summary>The controls for a shape's actual glow, or the defaults switched off.</summary>
    public static GlowControls From(Glow? glow, double pageWidthPts)
    {
        if (glow is not { } g || pageWidthPts <= 0)
        {
            return Defaults;
        }

        return new GlowControls(
            Enabled: true,
            // Clamped for DISPLAY only: a file made elsewhere may carry a blur
            // narrower or wider than this row offers, and the slider has to be
            // able to show it somewhere.
            BlurPts: Math.Clamp(
                g.Softness * pageWidthPts, MinBlurPts, MaxBlurPts(pageWidthPts)),
            OpacityPercent: (int)Math.Round(g.Color.A * 100.0 / 255.0),
            ColorHex: $"#{g.Color.R:X2}{g.Color.G:X2}{g.Color.B:X2}");
    }

    /// <summary>The glow the controls describe, or null when they are switched off.</summary>
    public static Glow? ToGlow(GlowControls c, double pageWidthPts)
    {
        if (!c.Enabled || pageWidthPts <= 0)
        {
            return null;
        }

        var (r, g, b) = DropShadowPanel.RgbOf(c.ColorHex);
        byte alpha = (byte)Math.Clamp(
            (int)Math.Round(c.OpacityPercent * 255.0 / 100.0), 0, 255);

        double pts = double.IsFinite(c.BlurPts) ? c.BlurPts : MinBlurPts;
        double blur = Math.Clamp(pts, MinBlurPts, MaxBlurPts(pageWidthPts)) / pageWidthPts;

        return new Glow(new RenderColor(alpha, r, g, b), blur);
    }
}
