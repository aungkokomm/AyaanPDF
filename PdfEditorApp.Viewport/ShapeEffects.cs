using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A drop shadow, described the way a person sets one rather than the way a
/// renderer draws one.
///
/// ANGLE AND DISTANCE ARE THE SOURCE OF TRUTH, and the x/y offset is derived
/// from them. Not the other way round: an angle cannot be recovered from an
/// offset of zero length, so storing x/y would throw the direction away the
/// moment somebody dragged the distance down to nothing and back.
///
/// <paramref name="AngleDeg"/> is where the LIGHT is, in degrees
/// counter-clockwise from due east, so 90 is lit from directly above and the
/// shadow falls straight down. The shadow always falls the opposite way, which
/// is the convention every drawing tool uses and the one a person will expect.
///
/// EVERY LENGTH IS NORMALIZED, like the rest of the model: a fraction of the
/// page's width. A shadow in pixels would sit a different distance away on
/// every page size and slide as the view zoomed. Normalized means the shadow
/// belongs to the OBJECT, so rotating the page turns it with the shape and
/// zooming scales it, none of which needs a line of code, because the derived
/// offset is applied to the points before the existing projection sees them.
///
/// OPACITY IS THE COLOUR'S ALPHA. There is deliberately no separate opacity
/// field: two ways to say how solid a shadow is would need a rule about which
/// one wins, and the alpha is already what decides whether there is a shadow at
/// all.
/// </summary>
/// <param name="Softness">
/// RESERVED. Stored, persisted and round-tripped, and DELIBERATELY NOT DRAWN by
/// any renderer. Blurring is not a parameter here, it is a second rendering
/// path: PDF has no blur primitive for a path object, so a soft shadow has to
/// be rasterised, which is its own piece of work. Carrying the value now means
/// a file written today keeps its softness when that lands rather than silently
/// losing it. Anything that appeared to honour it before then would be an
/// invention, so nothing does.
/// </param>
/// <param name="Spread">
/// RESERVED on the same terms as <paramref name="Softness"/>: stored and
/// round-tripped, never drawn. Spread needs path dilation, which neither PDFium
/// nor lopdf offers, so it goes the same rasterised route.
/// </param>
public readonly record struct DropShadow(
    double AngleDeg,
    double Distance,
    RenderColor Color,
    double Softness = 0,
    double Spread = 0)
{
    /// <summary>
    /// How far the shadow sits from its shape horizontally, in normalized page
    /// units. Derived, never stored.
    /// </summary>
    public double OffsetX => -Distance * Math.Cos(AngleDeg * Math.PI / 180.0);

    /// <summary>
    /// The vertical half, in SCREEN sense: positive is DOWN the page, which is
    /// the direction normalized page-local units run. render_core's
    /// <c>shadow_offset_pts</c> is the same derivation in PDF space, where y
    /// runs the other way, and both are checked against the same table of
    /// angles so one cannot be changed without the other failing.
    /// </summary>
    public double OffsetY => Distance * Math.Sin(AngleDeg * Math.PI / 180.0);
}

/// <summary>
/// The visual effects on one mark, beyond its colour and its weight.
///
/// A record with a slot per effect, and one slot so far. That is the extension
/// point: a glow or an outer stroke becomes another nullable property here, read
/// by the painter next to this one, and every existing call site keeps compiling
/// because it never mentioned effects in the first place.
///
/// Deliberately NOT a list of an IEffect interface. The paint loop runs on every
/// pointer move and a list means an allocation and a virtual call per mark to
/// express something that is a fixed, small, known set. Named slots also let the
/// bounds calculation ask "is there a shadow" without walking anything.
///
/// NULL IS THE DEFAULT AND NULL COSTS NOTHING. A mark with no effects carries a
/// null reference, every renderer skips it in one branch, and the pixels are
/// exactly what they were before this type existed. That is what keeps the
/// parity capture byte for byte identical.
///
/// PERSISTED, as one self-describing field on the shape's tag. See
/// <see cref="ShapeEffectsTag"/> for the conversion and
/// <see cref="ShapeTagReader"/> for the format. The field carries named keys
/// rather than positions precisely so a second effect can be added without
/// disturbing this one, and a key written by a later build is skipped rather
/// than fatal.
/// </summary>
public sealed record ShapeEffects(DropShadow? Shadow = null)
{
    /// <summary>
    /// Whether this is worth carrying at all. An effects object with every slot
    /// empty paints exactly like no effects object, so the bounds and the
    /// painter can both take the cheap path.
    /// </summary>
    public bool IsEmpty => Shadow is null;
}
