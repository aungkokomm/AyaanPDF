using System;
using System.Collections.Generic;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A shape's fill across the boundary between the object model and the file.
///
/// TWO PLACES, because a solid fill and a gradient are stored differently and
/// deliberately so:
///
/// <list type="bullet">
/// <item>A SOLID fill stays exactly where it has always been, in the tag's
/// positional fill field, "AARRGGBB". Nothing about it changes, and a shape
/// written before gradients existed reads identically.</item>
/// <item>A GRADIENT is one self-describing field on the tag's TAIL, beside the
/// effect fields but not one of them: <c>f(c=,c2=,x0=,y0=,x1=,y1=)</c>.</item>
/// </list>
///
/// The tail is the only part of the tag that extends, which is why the gradient
/// lives there rather than as a twelfth positional field: everything from
/// position ten on is the tail. It shares the tail's format so that render_core
/// carries it with no line added, and it obeys the tail's two rules:
///
/// <list type="bullet">
/// <item>THE KIND IS ONE LETTER, and 'f' is not one an effect uses. render_core
/// rejects a field whose name is longer.</item>
/// <item>THE COLOUR KEY MUST BE NON-ZERO. render_core reads <c>c</c> as the
/// thing that says a field exists at all, and refuses an entire edit that
/// contains a field it cannot parse. <see cref="FieldOf"/> keeps that true by
/// writing the gradient the other way round when it has to; see there.</item>
/// </list>
///
/// THE COORDINATE KEYS ARE NOT LENGTHS. render_core's
/// <c>scale_effect_lengths</c> and its C# mirror
/// <see cref="ShapeEffectsTag.ScaleFields"/> rescale exactly <c>d</c>, <c>b</c>
/// and <c>p</c> when the tail moves between points, normalized units and
/// capture pixels. <c>x0</c>, <c>y0</c>, <c>x1</c> and <c>y1</c> are none of
/// those, so they cross every boundary untouched, which is what a fraction of
/// the shape's own box should do.
/// </summary>
public static class ShapeFillTag
{
    /// <summary>
    /// The letter a gradient field is written under.
    ///
    /// NOT ONE AN EFFECT USES, and it cannot become one: it is checked against
    /// the effect letters by a test, because the two families share one tail
    /// and a collision would make a gradient read as an effect and be drawn as
    /// a shadow.
    /// </summary>
    public const char Letter = 'f';

    /// <summary>
    /// What a loaded shape is filled with.
    ///
    /// The gradient WINS over the positional fill field when a tag somehow
    /// carries both. We never write that combination, because a gradient is
    /// written with the positional field cleared, but a file may arrive with
    /// one and the more specific paint is the one meant. Never both, and never
    /// an average of the two.
    /// </summary>
    public static ShapeFill From(ShapeTag tag)
    {
        foreach (var field in ShapeTagReader.EffectsIn(tag.EffectsText))
        {
            if (Read(field.Field) is { } gradient)
            {
                return ShapeFill.Of(gradient);
            }
        }

        if (tag.FillHex is null)
        {
            return ShapeFill.None;
        }

        var (a, r, g, b) = InkPresets.ParseHex(tag.FillHex);

        return ShapeFill.Of(new RenderColor(a, r, g, b));
    }

    /// <summary>
    /// One tail field read as a gradient, or null for anything that is not one:
    /// an effect, a field from a later build, or an <c>f</c> field this build
    /// cannot make sense of.
    ///
    /// NULL RATHER THAN A GUESS, and the caller's job is then to carry the text
    /// verbatim rather than to drop it. Half a gradient with invented numbers
    /// would be a shape that paints something nobody asked for.
    /// </summary>
    public static GradientFill? Read(string? field)
    {
        if (field is null || field.Length < 3 || field[0] != Letter || field[1] != '('
            || !field.EndsWith(")", StringComparison.Ordinal))
        {
            return null;
        }

        RenderColor? from = null, to = null;
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        bool haveX0 = false, haveY0 = false, haveX1 = false, haveY1 = false;

        foreach (string pair in field[2..^1].Split(','))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) { return null; }

            string key = pair[..eq];
            string value = pair[(eq + 1)..];

            switch (key)
            {
                case "c": if (Colour(value) is not { } f) { return null; } from = f; break;
                case "c2": if (Colour(value) is not { } t) { return null; } to = t; break;
                case "x0": if (!Number(value, out x0)) { return null; } haveX0 = true; break;
                case "y0": if (!Number(value, out y0)) { return null; } haveY0 = true; break;
                case "x1": if (!Number(value, out x1)) { return null; } haveX1 = true; break;
                case "y1": if (!Number(value, out y1)) { return null; } haveY1 = true; break;

                // Forward compatibility, exactly as an effect field has it: a
                // key from a later build is skipped, not fatal.
                default: break;
            }
        }

        if (from is null || to is null || !haveX0 || !haveY0 || !haveX1 || !haveY1)
        {
            return null;
        }

        var gradient = new GradientFill(from.Value, to.Value, x0, y0, x1, y1);

        return gradient.IsDrawable ? gradient : null;
    }

    /// <summary>
    /// The tail field a fill is written as: one <c>f(...)</c> for a gradient,
    /// and NOTHING for a solid or for no fill at all, which are both carried by
    /// the tag's positional field instead.
    ///
    /// WRITTEN THE OTHER WAY ROUND WHEN IT HAS TO BE. render_core reads a
    /// field's <c>c</c> as the thing that says the field exists, and refuses the
    /// whole edit when a field does not parse, so <c>c=00000000</c> would make
    /// every shadow and glow edit on that shape fail. A gradient whose first
    /// stop is transparent black is written reversed instead: stops swapped and
    /// endpoints swapped is the SAME paint, exactly, so nothing is lost or
    /// approximated by doing it.
    ///
    /// Both stops transparent black is not a gradient at all, and takes the
    /// same "no colour, no field" exit every effect takes.
    /// </summary>
    public static string FieldOf(ShapeFill fill)
    {
        if (fill.Gradient is not { } gradient || !gradient.IsDrawable)
        {
            return string.Empty;
        }

        if (Packed(gradient.From) == 0)
        {
            if (Packed(gradient.To) == 0)
            {
                return string.Empty;
            }

            gradient = gradient.Reversed();
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Letter}(c={Packed(gradient.From):X8},c2={Packed(gradient.To):X8},x0={gradient.X0:F4},y0={gradient.Y0:F4},x1={gradient.X1:F4},y1={gradient.Y1:F4})");
    }

    /// <summary>
    /// The shape tag's WHOLE tail: its fill field, then its effect fields.
    ///
    /// ONE FUNCTION, and every write site goes through it, for the same reason
    /// <c>ApplyEffectsToSelectedShape</c> sends the whole effect list rather
    /// than one effect. The core replaces the tail wholesale, so a site that
    /// composed only the half it was interested in would delete the other:
    /// nudging the shadow's slider would take the gradient off the shape.
    ///
    /// The fill leads because it is the shape's own paint and the effects are
    /// made from it. Nothing depends on the order, and the core preserves it.
    /// </summary>
    /// <param name="scale">
    /// What the EFFECTS' lengths are wanted in; see
    /// <see cref="ShapeEffectsTag.TextOf"/>. The fill field has no length in it
    /// and is unaffected.
    /// </param>
    public static string TailOf(ShapeFill fill, ShapeEffects? effects, double scale)
    {
        var fields = new List<string>(2);

        string paint = FieldOf(fill);
        if (paint.Length > 0)
        {
            fields.Add(paint);
        }

        string marks = ShapeEffectsTag.TextOf(effects, scale);
        if (marks.Length > 0)
        {
            fields.Add(marks);
        }

        return string.Join(":", fields);
    }

    private static uint Packed(RenderColor c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static RenderColor? Colour(string value)
    {
        if (value.Length != 8) { return null; }

        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c)) { return null; }
        }

        var (a, r, g, b) = InkPresets.ParseHex("#" + value);

        return new RenderColor(a, r, g, b);
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
