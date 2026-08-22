using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A gradient fill as the Fill flyout's controls hold it.
///
/// TWO COLOURS AND A DIRECTION, and nothing else yet. The colours are
/// "#AARRGGBB" like every other colour the app passes about, and their alpha is
/// always opaque: the save-time writer refuses a translucent stop rather than
/// flattening it, so offering one here would be offering something the file
/// cannot keep.
/// </summary>
/// <param name="AngleDeg">
/// Where the ramp RUNS TO, in degrees clockwise from due east on screen: 0 runs
/// left to right, 90 top to bottom, 180 right to left, 270 bottom to top.
/// </param>
public readonly record struct GradientControls(
    bool Enabled,
    string StartHex,
    string EndHex,
    int AngleDeg);

/// <summary>
/// The Gradient controls: what they show for a shape, and what a shape gets
/// when they change.
///
/// Pure, and here rather than in the view for the reason every other layout
/// rule is: the app is a WinUI project the test assembly cannot load, so
/// anything left in the view is only ever checked by running the app and
/// looking. That is how a control ships doing nothing.
///
/// THE ANGLE IS DERIVED, NOT STORED. A gradient is two endpoints, because that
/// is what both renderers take and what the PDF shading wants; an angle is how
/// a person says it. The two conversions live side by side here so a shape read
/// from a file shows the slider position that would have produced it.
/// </summary>
public static class GradientPanel
{
    /// <summary>
    /// The steps the direction slider offers, in degrees.
    ///
    /// Eight of them rather than a free angle. At every one of the four square
    /// steps the ramp runs exactly edge to edge, which is what a person means
    /// by "left to right", and there is no arithmetic to explain in between.
    /// </summary>
    public const int AngleStepDeg = 45;

    /// <summary>The last step, so the slider wraps rather than repeating 0.</summary>
    public const int MaxAngleDeg = 315;

    /// <summary>
    /// What the controls show for a shape with no gradient yet, so switching
    /// one on gives something worth looking at rather than a flat rectangle.
    /// </summary>
    public static GradientControls Defaults => new(
        Enabled: false,
        StartHex: "#FF0078D4",
        EndHex: "#FFFFFFFF",
        AngleDeg: 0);

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
            AngleDeg: AngleOf(g));
    }

    /// <summary>The gradient the controls describe, or null when switched off.</summary>
    public static GradientFill? ToGradient(GradientControls c)
    {
        if (!c.Enabled)
        {
            return null;
        }

        return AtAngle(Opaque(c.StartHex), Opaque(c.EndHex), c.AngleDeg);
    }

    /// <summary>
    /// The two endpoints a direction means, as fractions of the shape's own
    /// upright box.
    ///
    /// THROUGH THE CENTRE, half a box each way, so the ramp is the same length
    /// whatever the angle and the middle colour is always in the middle. At the
    /// four square steps that puts the ends exactly on opposite edges. Between
    /// them the box's corners fall outside the ramp and take the nearer end's
    /// colour, which is what clamping is for and what both renderers already do.
    /// </summary>
    public static GradientFill AtAngle(RenderColor from, RenderColor to, double angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;

        // y DOWN, like every other coordinate in the model, so a positive angle
        // turns the way it looks like it should on screen.
        double dx = Math.Cos(radians) / 2;
        double dy = Math.Sin(radians) / 2;

        return new GradientFill(from, to, 0.5 - dx, 0.5 - dy, 0.5 + dx, 0.5 + dy);
    }

    /// <summary>
    /// Which of the slider's steps a gradient's endpoints are nearest.
    ///
    /// The inverse of <see cref="AtAngle"/>, and NOT exact for a gradient made
    /// elsewhere: the model takes any two endpoints and the slider offers
    /// eight, so a shape from another build shows at the nearest step rather
    /// than pushing the thumb off the track. Editing it then snaps it, which is
    /// what a person dragging a slider expects.
    /// </summary>
    public static int AngleOf(GradientFill gradient)
    {
        double degrees =
            Math.Atan2(gradient.Y1 - gradient.Y0, gradient.X1 - gradient.X0) * 180.0 / Math.PI;

        int snapped = (int)Math.Round(degrees / AngleStepDeg) * AngleStepDeg;

        // Into 0..359 the long way round, so -45 shows as 315 rather than as a
        // negative the slider has no room for.
        return ((snapped % 360) + 360) % 360;
    }

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
