using System.Collections.Generic;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Effects across the boundary between the object model and the file.
///
/// Two conversions, kept together because they are one contract read from both
/// ends: what goes into a shape's tag, and what comes back out of it. Split
/// across the view model and the reader they would drift, and a shadow that
/// saves as one colour and loads as another is the kind of thing nobody notices
/// until a file has been through several rounds.
///
/// THE TWO SPACES. The object model measures in NORMALIZED page-local units,
/// where a length is a fraction of the page's width, because that is what keeps
/// a mark the same size on any page at any zoom. The tag measures in POINTS,
/// because that is what a PDF is written in and what every other length in the
/// tag already uses. The page width is what converts between them, and it is
/// the caller's to supply: a tag does not carry one.
///
/// ZERO MEANS NO EFFECT, decided by the COLOUR and not by the offsets. A shadow
/// directly under its shape, offset by nothing, is a legitimate thing to ask
/// for; one in no colour is not. The same rule the fill already uses.
/// </summary>
public static class ShapeEffectsTag
{
    /// <summary>
    /// The shadow's colour as the tag and the core carry it, 0xAARRGGBB, or 0
    /// when there is no shadow to draw.
    /// </summary>
    public static uint RgbaOf(ShapeEffects? effects)
    {
        if (effects?.Shadow is not { } shadow)
        {
            return 0;
        }

        var c = shadow.Color;

        // Fully transparent is not a shadow. Returning its channels would set
        // the "there is a shadow" flag for something that paints nothing, and
        // the shape would then take the longer tag rung for no reason.
        if (c.A == 0)
        {
            return 0;
        }

        return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    /// <summary>
    /// The effects a loaded shape has, or null when its tag records none.
    ///
    /// Null rather than an empty <see cref="ShapeEffects"/>, so a shape read
    /// from a file that predates effects is indistinguishable from one that has
    /// none, which it is.
    ///
    /// EVERY FIELD IS ACCOUNTED FOR. One whose letter this build knows becomes
    /// a typed spec; anything else is carried verbatim, so that editing the
    /// shadow on a shape written by a later build does not quietly delete what
    /// that build put there.
    /// </summary>
    /// <param name="pageWidthPts">
    /// The width of the page the shape is on, in points. What converts the
    /// tag's lengths back into the normalized units the object model works in.
    /// </param>
    public static ShapeEffects? From(ShapeTag tag, double pageWidthPts)
    {
        if (pageWidthPts <= 0)
        {
            return null;
        }

        var fields = ShapeTagReader.EffectsIn(tag.EffectsText);
        if (fields.Count == 0)
        {
            return null;
        }

        var specs = new List<EffectSpec>(fields.Count);
        var carried = new List<string>();

        foreach (var field in fields)
        {
            // THE FILL'S FIELD IS NOT AN EFFECT. It shares the tail and nothing
            // else: it is paint, it reserves no room, and ShapeFillTag reads
            // it. Carrying it here as well would write it a second time on the
            // next edit. A malformed one does NOT read as a gradient and so
            // falls through to be carried verbatim, which is what keeps it from
            // being dropped by the build that could not understand it.
            if (ShapeFillTag.Read(field.Field) is not null)
            {
                continue;
            }

            if (field.Hex is null || KindOf(field.Kind) is not { } kind)
            {
                // Not ours to understand. It keeps its own text and its lengths
                // come into the model's units with everything else, so that
                // writing it back out is the exact inverse.
                carried.Add(ScaleFields(field.Field, 1.0 / pageWidthPts));
                continue;
            }

            var (a, r, g, b) = InkPresets.ParseHex(field.Hex);

            specs.Add(new EffectSpec(
                kind,
                new RenderColor(a, r, g, b),
                // The angle is not a length and needs no conversion; everything
                // else does, because the tag speaks points and the model speaks
                // fractions of the page's width.
                Blur: field.BlurPts / pageWidthPts,
                Distance: field.DistancePts / pageWidthPts,
                AngleDeg: field.AngleDeg,
                Amount: field.SpreadPts / pageWidthPts));
        }

        // A tag whose only tail field was the fill records no effects, which
        // is null and not an empty list, exactly as a tag with no tail at all
        // does.
        if (specs.Count == 0 && carried.Count == 0)
        {
            return null;
        }

        return new ShapeEffects(specs, carried);
    }

    /// <summary>
    /// The letter a kind is written as, and the kind a letter reads as.
    ///
    /// SIDE BY SIDE, because they are one mapping and a kind that writes a
    /// letter nothing reads would save and then vanish on the next load. An
    /// unknown letter is not an error: it is somebody else's effect, and it is
    /// carried rather than typed.
    /// </summary>
    private static char LetterOf(EffectKind kind) => kind switch
    {
        EffectKind.DropShadow => 's',
        EffectKind.Glow => 'g',
        _ => '\0',
    };

    /// <inheritdoc cref="LetterOf"/>
    private static EffectKind? KindOf(char letter) => letter switch
    {
        's' => EffectKind.DropShadow,
        'g' => EffectKind.Glow,
        _ => null,
    };

    /// <summary>
    /// The same effect fields with every LENGTH multiplied, and everything else
    /// copied exactly.
    ///
    /// The C# half of <c>scale_effect_lengths</c> in render_core, and needed for
    /// the same reason: a carried field's lengths are in whatever space its
    /// holder is in, and this side moves between points, normalized units and
    /// capture pixels. Which keys are lengths is a property of the FORMAT rather
    /// than of any effect, and a key this build does not recognise is copied
    /// verbatim rather than dropped or reformatted.
    /// </summary>
    public static string ScaleFields(string fields, double factor)
    {
        if (string.IsNullOrEmpty(fields))
        {
            return string.Empty;
        }

        var built = new System.Text.StringBuilder(fields.Length + 8);

        string[] parts = fields.Split(':');
        for (int at = 0; at < parts.Length; at++)
        {
            if (at > 0)
            {
                built.Append(':');
            }

            string field = parts[at];
            int open = field.IndexOf('(');
            if (open < 0 || !field.EndsWith(")", StringComparison.Ordinal))
            {
                built.Append(field);
                continue;
            }

            built.Append(field, 0, open + 1);

            string[] pairs = field[(open + 1)..^1].Split(',');
            for (int n = 0; n < pairs.Length; n++)
            {
                if (n > 0)
                {
                    built.Append(',');
                }

                string pair = pairs[n];
                int eq = pair.IndexOf('=');
                string key = eq > 0 ? pair[..eq] : string.Empty;

                if ((key is "d" or "b" or "p")
                    && double.TryParse(
                        pair[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value)
                    && double.IsFinite(value))
                {
                    built.Append(key).Append('=');
                    built.Append((value * factor).ToString("F4", CultureInfo.InvariantCulture));
                }
                else
                {
                    built.Append(pair);
                }
            }

            built.Append(')');
        }

        return built.ToString();
    }

    /// <summary>
    /// These effects as the TAG and the core spell them: one self-describing
    /// <c>kind(key=value,...)</c> field each, joined by colons, and an empty
    /// string for none.
    ///
    /// THE ONE PLACE A KIND'S LETTER IS NAMED on this side. Everything else
    /// about an effect crosses as the six numbers every effect shares, which is
    /// what lets the core carry effects it knows nothing about.
    ///
    /// An effect with no colour paints nothing, so it is not written: the same
    /// bargain the fill makes, and what lets "has a field" and "has an effect"
    /// stay the same statement.
    /// </summary>
    /// <param name="scale">
    /// What turns the model's normalized lengths into the space the caller
    /// wants: the capture width for the core, the page width in points for the
    /// tag itself.
    /// </param>
    public static string TextOf(ShapeEffects? effects, double scale)
    {
        if (effects is null || effects.HasNothing)
        {
            return string.Empty;
        }

        var fields = new List<string>(effects.Specs.Count + effects.Carried.Count);

        foreach (var spec in effects.Specs)
        {
            if (spec.Color.A == 0)
            {
                continue;
            }

            char kind = LetterOf(spec.Kind);

            if (kind == '\0')
            {
                continue;
            }

            uint rgba = ((uint)spec.Color.A << 24) | ((uint)spec.Color.R << 16)
                | ((uint)spec.Color.G << 8) | spec.Color.B;

            fields.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{kind}(a={spec.AngleDeg:F2},d={spec.Distance * scale:F4},b={spec.Blur * scale:F4},p={spec.Amount * scale:F4},c={rgba:X8})"));
        }

        // THE CARRIED FIELDS GO LAST, and out they go untouched but for their
        // lengths. Their place in the order is not recoverable and would not
        // mean anything if it were: this build cannot draw them, so it has no
        // opinion about what they sit above.
        foreach (string field in effects.Carried)
        {
            fields.Add(ScaleFields(field, scale));
        }

        return string.Join(":", fields);
    }

    /// <summary>
    /// The colour as the tag spells it, "#AARRGGBB", or null for no shadow.
    /// The inverse of the hex half of <see cref="From"/>, for the tests that
    /// have to state a round trip without going through the core.
    /// </summary>
    public static string? HexOf(ShapeEffects? effects)
    {
        uint rgba = RgbaOf(effects);

        return rgba == 0
            ? null
            : "#" + rgba.ToString("X8", CultureInfo.InvariantCulture);
    }
}
