using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A gradient fill as the Fill flyout's controls hold it.
///
/// TWO COLOURS, A DIRECTION AND A LENGTH. The colours are "#AARRGGBB" like
/// every other colour the app passes about, and their alpha is always opaque:
/// the save-time writer refuses a translucent stop rather than flattening it,
/// so offering one here would be offering something the file cannot keep.
/// </summary>
/// <param name="AngleDeg">
/// Where the ramp RUNS TO, in degrees clockwise from due east on screen: 0 runs
/// left to right, 90 top to bottom, 180 right to left, 270 bottom to top.
/// </param>
/// <param name="SpreadPercent">
/// How long the ramp is, as a percentage of the distance it would run at 100.
/// Below 100 the ramp is shorter than the shape and each end's colour fills the
/// rest flat; above 100 the shape shows only the middle of a longer ramp.
/// </param>
public readonly record struct GradientControls(
    bool Enabled,
    string StartHex,
    string EndHex,
    int AngleDeg,
    int SpreadPercent = GradientPanel.FullSpreadPercent);

/// <summary>
/// The Gradient controls: what they show for a shape, and what a shape gets
/// when they change.
///
/// Pure, and here rather than in the view for the reason every other layout
/// rule is: the app is a WinUI project the test assembly cannot load, so
/// anything left in the view is only ever checked by running the app and
/// looking. That is how a control ships doing nothing.
///
/// THE ANGLE AND THE SPREAD ARE DERIVED, NOT STORED. A gradient is two
/// endpoints, because that is what both renderers take and what the PDF shading
/// wants; a direction and a length are how a person says it. Both conversions
/// live side by side here so a shape read from a file shows the controls that
/// would have produced it.
/// </summary>
public static class GradientPanel
{
    /// <summary>
    /// The finest step the direction slider offers, in degrees.
    ///
    /// ONE, so the ramp can run any way at all. It used to be 45, which made
    /// every gradient in the app one of eight, and the endpoints have always
    /// been free: nothing in the model, the tag, the shader or the PDF shading
    /// ever cared about the step, so this was a limit the controls invented.
    ///
    /// The four square angles are still the ones that run exactly edge to edge,
    /// and they are still reachable exactly, because they are whole degrees.
    /// </summary>
    public const int AngleStepDeg = 1;

    /// <summary>The last step, so the slider wraps rather than repeating 0.</summary>
    public const int MaxAngleDeg = 359;

    /// <summary>
    /// The spread at which the ramp runs the length it always used to: through
    /// the centre, half a box each way.
    /// </summary>
    public const int FullSpreadPercent = 100;

    /// <summary>
    /// The shortest ramp the slider offers.
    ///
    /// Not zero, and not near it: at zero the two endpoints coincide, which is
    /// no direction at all, and <see cref="GradientFill.IsDrawable"/> rejects
    /// it. Ten percent is a hard edge with a visible seam rather than a step.
    /// </summary>
    public const int MinSpreadPercent = 10;

    /// <summary>
    /// The longest, at which the shape shows the middle fifth of the ramp and
    /// the two ends are far outside it. Past this the fill is barely a gradient.
    /// </summary>
    public const int MaxSpreadPercent = 500;

    /// <summary>
    /// What the controls show for a shape with no gradient yet, so switching
    /// one on gives something worth looking at rather than a flat rectangle.
    /// </summary>
    public static GradientControls Defaults => new(
        Enabled: false,
        StartHex: "#FF0078D4",
        EndHex: "#FFFFFFFF",
        AngleDeg: 0,
        SpreadPercent: FullSpreadPercent);

    /// <summary>The controls for a shape's actual gradient, or the defaults switched off.</summary>
    public static GradientControls From(GradientFill? gradient)
    {
        if (gradient is not { } g)
        {
            return Defaults;
        }

        return new GradientControls(
            Enabled: true,
            StartHex: Hex(g.From),
            EndHex: Hex(g.To),
            AngleDeg: AngleOf(g),
            SpreadPercent: SpreadOf(g));
    }

    /// <summary>The gradient the controls describe, or null when switched off.</summary>
    public static GradientFill? ToGradient(GradientControls c)
    {
        if (!c.Enabled)
        {
            return null;
        }

        return AtAngle(Opaque(c.StartHex), Opaque(c.EndHex), c.AngleDeg, c.SpreadPercent);
    }

    /// <summary>
    /// The controls with their two colours exchanged.
    ///
    /// The direction and the length are left alone deliberately. Swapping is
    /// how a person says "the other way round" about the COLOURS; turning the
    /// ramp 180 degrees as well would put it back where it started.
    /// </summary>
    public static GradientControls Swapped(GradientControls c) =>
        c with { StartHex = c.EndHex, EndHex = c.StartHex };

    /// <summary>
    /// The two endpoints a direction and a length mean, as fractions of the
    /// shape's own upright box.
    ///
    /// THROUGH THE CENTRE, half a box each way at full spread, so the ramp is
    /// the same length whatever the angle and the middle colour is always in
    /// the middle. At the four square angles that puts the ends exactly on
    /// opposite edges. Between them the box's corners fall outside the ramp and
    /// take the nearer end's colour, which is what clamping is for and what
    /// both renderers already do.
    ///
    /// SPREAD SCALES THAT HALF-BOX, and needs nothing from anyone else: past
    /// either end the paint is that end's own colour, which is Skia's
    /// <c>Clamp</c> and the shading's <c>/Extend [true true]</c>. Both were
    /// always there; nothing but this method ever decided the ramp had to be
    /// exactly one box long.
    /// </summary>
    public static GradientFill AtAngle(
        RenderColor from, RenderColor to, double angleDeg,
        double spreadPercent = FullSpreadPercent)
    {
        double radians = angleDeg * Math.PI / 180.0;
        double half = Reach(spreadPercent) / 2;

        // y DOWN, like every other coordinate in the model, so a positive angle
        // turns the way it looks like it should on screen.
        double dx = Math.Cos(radians) * half;
        double dy = Math.Sin(radians) * half;

        return new GradientFill(from, to, 0.5 - dx, 0.5 - dy, 0.5 + dx, 0.5 + dy);
    }

    /// <summary>
    /// Which direction a gradient's endpoints run, to the nearest whole degree.
    ///
    /// EXACT for anything the slider made, because the slider's steps are whole
    /// degrees. Still a rounding for a gradient made elsewhere, and still a
    /// rounding of the DIRECTION only: a gradient whose endpoints do not run
    /// through the centre reports the direction of the line between them, and
    /// editing it re-centres it. Recovering an off-centre line needs endpoint
    /// handles on the shape, not another slider.
    /// </summary>
    public static int AngleOf(GradientFill gradient)
    {
        double degrees =
            Math.Atan2(gradient.Y1 - gradient.Y0, gradient.X1 - gradient.X0) * 180.0 / Math.PI;

        int rounded = (int)Math.Round(degrees);

        // Into 0..359 the long way round, so -45 shows as 315 rather than as a
        // negative the slider has no room for.
        return ((rounded % 360) + 360) % 360;
    }

    /// <summary>
    /// How long a gradient's ramp is, as the slider's percentage.
    ///
    /// The inverse of the scaling <see cref="AtAngle"/> applies, clamped to what
    /// the slider can show so a gradient from elsewhere puts the thumb on the
    /// track rather than off the end of it.
    /// </summary>
    public static int SpreadOf(GradientFill gradient)
    {
        double dx = gradient.X1 - gradient.X0;
        double dy = gradient.Y1 - gradient.Y0;

        int percent = (int)Math.Round(Math.Sqrt((dx * dx) + (dy * dy)) * FullSpreadPercent);

        return Math.Clamp(percent, MinSpreadPercent, MaxSpreadPercent);
    }

    /// <summary>
    /// The end-to-end length a spread means, in box fractions, with the
    /// slider's own limits applied.
    ///
    /// Clamped HERE as well as at the control, because a caller is not the
    /// slider: a zero-length ramp is two coincident endpoints, which is not a
    /// gradient and which the model refuses to draw.
    /// </summary>
    private static double Reach(double spreadPercent) =>
        Math.Clamp(spreadPercent, MinSpreadPercent, MaxSpreadPercent) / (double)FullSpreadPercent;

    /// <summary>
    /// A colour from the controls, with its alpha forced OPAQUE.
    ///
    /// The one place the stage's constraint is enforced rather than assumed.
    /// The writer refuses a translucent stop, so a translucent stop must not be
    /// able to reach a tag: a picker that somehow produced one would otherwise
    /// make a shape that draws on screen and saves unfilled.
    /// </summary>
    private static RenderColor Opaque(string hex)
    {
        var (_, r, g, b) = InkPresets.ParseHex(hex);

        return new RenderColor(0xFF, r, g, b);
    }

    private static string Hex(RenderColor c) => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
}
