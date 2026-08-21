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
/// PERSISTED, as self-describing fields on the shape's tag. See
/// <see cref="ShapeEffectsTag"/> for the conversion and
/// <see cref="ShapeTagReader"/> for the format.
/// </summary>
public sealed record ShapeEffects
{
    private readonly EffectSpec[] _specs;

    /// <summary>The effects on this mark, bottom first. Empty for none.</summary>
    public ShapeEffects(params EffectSpec[] specs) =>
        _specs = specs ?? Array.Empty<EffectSpec>();

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
    /// Whether this is worth carrying at all. An empty list paints exactly like
    /// no effects at all, so the bounds and the painter can both take the cheap
    /// path.
    /// </summary>
    public bool IsEmpty => _specs.Length == 0;

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
        other is not null && _specs.AsSpan().SequenceEqual(other._specs);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var spec in _specs)
        {
            hash.Add(spec);
        }

        return hash.ToHashCode();
    }
}
