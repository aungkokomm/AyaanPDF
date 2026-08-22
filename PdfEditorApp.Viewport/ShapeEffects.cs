using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A drop shadow, named the way a person sets one.
///
/// A VIEW over an <see cref="EffectSpec"/> of kind
/// <see cref="EffectKind.DropShadow"/>, not a thing of its own. The renderers
/// stopped reading this: they walk the list and hand each spec to the recipe.
/// It is kept because the shadow's own UI, its tag conversion and its FFI call
/// are all shadow-shaped and have no reason not to be, and because naming the
/// four numbers a shadow uses is worth more at those edges than a spec's
/// generality is.
///
/// See <see cref="EffectSpec"/> for what each number means. Nothing is repeated
/// here, because a second copy of the explanation is a second thing to keep
/// true.
/// </summary>
/// <param name="Softness">
/// The blur RADIUS. Zero is a hard shadow, which stays a vector path in the
/// file and is crisp at any zoom; anything above it is rasterised by Skia and
/// carried in the annotation as a picture, because PDF has no blur for a path.
/// </param>
/// <param name="Spread">
/// RESERVED. Stored, persisted and round-tripped, and drawn by nothing. Spread
/// needs the silhouette dilated before the blur runs, which is a filter Skia
/// has and this pipeline does not use yet. Carrying it now means a file written
/// today keeps the value when that lands rather than silently losing it.
/// </param>
public readonly record struct DropShadow(
    double AngleDeg,
    double Distance,
    RenderColor Color,
    double Softness = 0,
    double Spread = 0)
{
    /// <summary>This shadow as the generic pipeline carries it.</summary>
    public EffectSpec ToSpec() => new(
        EffectKind.DropShadow, Color,
        Blur: Softness, Distance: Distance, AngleDeg: AngleDeg, Amount: Spread);

    /// <summary>The inverse, for reading one back out of a list.</summary>
    public static DropShadow FromSpec(EffectSpec spec) => new(
        spec.AngleDeg, spec.Distance, spec.Color, spec.Blur, spec.Amount);

    /// <summary>
    /// How far the shadow sits from its shape, in normalized page units.
    /// Derived by <see cref="EffectSpec"/>, so a shadow cannot be thrown one
    /// way by the preview and the other way by anything reading the list.
    /// </summary>
    public double OffsetX => ToSpec().OffsetX;

    /// <inheritdoc cref="OffsetX"/>
    public double OffsetY => ToSpec().OffsetY;
}

/// <summary>
/// An outer glow, named the way a person sets one.
///
/// A VIEW over an <see cref="EffectSpec"/> of kind <see cref="EffectKind.Glow"/>,
/// on exactly the terms <see cref="DropShadow"/> is one. Two numbers, because a
/// glow is a shadow with nowhere to fall: no angle, no distance, and nothing to
/// spread yet.
/// </summary>
/// <param name="Softness">
/// The blur RADIUS, normalized like every other length. Zero draws nothing at
/// all: an unblurred glow is the shape's own outline directly behind the shape.
/// </param>
public readonly record struct Glow(RenderColor Color, double Softness)
{
    /// <summary>This glow as the generic pipeline carries it.</summary>
    public EffectSpec ToSpec() => new(EffectKind.Glow, Color, Blur: Softness);

    /// <summary>The inverse, for reading one back out of a list.</summary>
    public static Glow FromSpec(EffectSpec spec) => new(spec.Color, spec.Blur);
}

/// <summary>
/// The visual effects on one mark, beyond its colour and its weight.
///
/// AN ORDERED LIST, walked in order, first at the bottom. It used to be a slot
/// per effect, and that is what made the second effect expensive: a glow meant a
/// property here, a branch in the painter, a branch in the bounds, a field pair
/// in the core's spec struct, an element in the tag's tuple and an override
/// entry point of its own, for something that is a drop shadow with the offset
/// set to nothing.
///
/// The argument against a list was an allocation and a virtual call per mark.
/// That was an argument against a list of an IEffect INTERFACE, and it does not
/// reach this: the specs are structs in an array held by the object, walked once
/// per object rather than once per mark, and the object's paint is measured in
/// milliseconds against which a walk of two or three entries does not register.
///
/// NULL IS THE DEFAULT AND NULL COSTS NOTHING. A mark with no effects carries a
/// null reference, every renderer skips it in one branch, and the pixels are
/// exactly what they were before this type existed.
///
/// IN ONE CANONICAL ORDER, whichever way round they arrive: the drop shadow
/// underneath, the glow above it. Fixed for the effects that exist rather than
/// worked out from anything, because "add a glow to a shadowed shape" and "add
/// a shadow to a glowing one" have to reach the same document and nothing more
/// general than that is needed yet. Within one kind the order given is kept.
///
/// PERSISTED, as self-describing fields on the shape's tag. See
/// <see cref="ShapeEffectsTag"/> for the conversion and
/// <see cref="ShapeTagReader"/> for the format.
///
/// AND WHAT IT CANNOT NAME, IT CARRIES. A field written by a later build is
/// held verbatim in <see cref="Carried"/>: not drawn, not interpreted, and
/// written back out untouched when the user edits the effect beside it. The
/// core is the authority on what an effect is; this type only needs enough
/// structure to edit the ones it can draw.
/// </summary>
public sealed record ShapeEffects
{
    private readonly EffectSpec[] _specs;
    private readonly string[] _carried;

    /// <summary>The effects on this mark, bottom first. Empty for none.</summary>
    public ShapeEffects(params EffectSpec[] specs)
        : this(specs, Array.Empty<string>())
    {
    }

    /// <summary>The effects this build can draw, and the fields it cannot.</summary>
    public ShapeEffects(IReadOnlyList<EffectSpec>? specs, IReadOnlyList<string>? carried)
    {
        _specs = specs is null || specs.Count == 0
            ? Array.Empty<EffectSpec>()
            : specs.Count == 1
                ? new[] { specs[0] }
                // OrderBy is stable, which is what keeps two effects of one
                // kind in the order they were given.
                : specs.OrderBy(RankOf).ToArray();

        _carried = carried is null || carried.Count == 0
            ? Array.Empty<string>()
            : carried.ToArray();
    }

    /// <summary>
    /// Where a kind sits in the stack, bottom first.
    ///
    /// A fixed rank per kind and deliberately nothing more. An effect this
    /// build does not know is carried rather than ranked, so there is no case
    /// here for it to fall into.
    /// </summary>
    private static int RankOf(EffectSpec spec) => spec.Kind switch
    {
        EffectKind.DropShadow => 0,
        EffectKind.Glow => 1,
        _ => 2,
    };

    /// <summary>
    /// One drop shadow, or none. The shape every existing caller uses, kept so
    /// that going through the list is a change to the renderers and not to
    /// everything that ever set a shadow.
    /// </summary>
    public ShapeEffects(DropShadow? shadow)
        : this(shadow is { } s ? new[] { s.ToSpec() } : Array.Empty<EffectSpec>())
    {
    }

    /// <summary>What is drawn, in order, underneath the object.</summary>
    public IReadOnlyList<EffectSpec> Specs => _specs;

    /// <summary>
    /// The tag fields this build could not turn into a spec, verbatim, with
    /// their lengths in the model's normalized units like everything else here.
    ///
    /// A later build's effect, or one whose text does not read at all. Neither
    /// is drawn and neither is thrown away: the core preserves them, so this
    /// side has to as well, or the next edit would send a list without them and
    /// the core would faithfully record the loss.
    /// </summary>
    public IReadOnlyList<string> Carried => _carried;

    /// <summary>
    /// Whether there is anything to DRAW. A carried field is not, so a shape
    /// carrying only those paints exactly as it would with no effects, and the
    /// bounds and the painter both take the cheap path.
    /// </summary>
    public bool IsEmpty => _specs.Length == 0;

    /// <summary>Whether there is anything to WRITE, carried fields included.</summary>
    public bool HasNothing => _specs.Length == 0 && _carried.Length == 0;

    /// <summary>
    /// These effects with the drop shadow replaced, added or taken away, and
    /// everything else left exactly as it is.
    ///
    /// THE WHOLE LIST IS WHAT GETS SENT to the core, so an edit to one effect
    /// has to be expressed as a new list rather than as an instruction. Doing
    /// that by hand at each call site is how the glow would get dropped every
    /// time somebody moved the shadow's slider.
    /// </summary>
    public ShapeEffects With(DropShadow? shadow) =>
        Replacing(EffectKind.DropShadow, shadow?.ToSpec());

    /// <inheritdoc cref="With(DropShadow?)"/>
    public ShapeEffects With(Glow? glow) =>
        Replacing(EffectKind.Glow, glow?.ToSpec());

    private ShapeEffects Replacing(EffectKind kind, EffectSpec? replacement)
    {
        var kept = new List<EffectSpec>(_specs.Length + 1);
        foreach (var spec in _specs)
        {
            if (spec.Kind != kind)
            {
                kept.Add(spec);
            }
        }

        if (replacement is { } added)
        {
            kept.Add(added);
        }

        return new ShapeEffects(kept, _carried);
    }

    /// <summary>
    /// The first drop shadow in the list, named, or null for none.
    ///
    /// READ OUT OF THE LIST rather than stored beside it. Two places recording
    /// the same shadow is how the preview and the file come to disagree, and a
    /// derived view cannot drift from what the renderers actually draw.
    /// </summary>
    public DropShadow? Shadow
    {
        get
        {
            foreach (var spec in _specs)
            {
                if (spec.Kind == EffectKind.DropShadow)
                {
                    return DropShadow.FromSpec(spec);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The first glow in the list, named, or null for none. Derived from the
    /// list on the same terms as <see cref="Shadow"/>.
    /// </summary>
    public Glow? Glow
    {
        get
        {
            foreach (var spec in _specs)
            {
                if (spec.Kind == EffectKind.Glow)
                {
                    return new Glow(spec.Color, spec.Blur);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// How far the widest of these effects reaches past the silhouette, in
    /// normalized page units.
    ///
    /// THE WIDEST, NOT THE SUM. They are all drawn from the same silhouette into
    /// the same box, so a second effect does not push the first one further out.
    /// Adding them up would grow the room reserved on every edit, which is a bug
    /// this pipeline has already had once.
    /// </summary>
    public double Reach
    {
        get
        {
            double reach = 0;
            foreach (var spec in _specs)
            {
                reach = Math.Max(reach, spec.Reach);
            }

            return reach;
        }
    }

    /// <summary>
    /// Value equality over the specs. Spelled out because the array field the
    /// record would otherwise compare by reference makes two identical sets of
    /// effects unequal, which is not what any caller of a record means.
    /// </summary>
    public bool Equals(ShapeEffects? other) =>
        other is not null
        && _specs.AsSpan().SequenceEqual(other._specs)
        && _carried.AsSpan().SequenceEqual(other._carried);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var spec in _specs)
        {
            hash.Add(spec);
        }

        foreach (string field in _carried)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }
}
