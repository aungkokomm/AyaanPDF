using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which effect a spec describes.
///
/// The one place an effect is named. Everything else about it is the six
/// numbers on <see cref="EffectSpec"/>, so a new effect is a member here, a
/// case in the recipe and nothing else.
///
/// The values are PERSISTED, so they are pinned and never reordered: a shape
/// saved by one build is read by another.
/// </summary>
public enum EffectKind
{
    /// <summary>A copy of the object thrown by a light, blurred, behind it.</summary>
    DropShadow = 0,
}

/// <summary>
/// One effect on one object, flat.
///
/// SIX NUMBERS AND A KIND, and no effect gets fields of its own. That is the
/// whole trade: a drop shadow only uses four of them and a glow will only use
/// two, at the cost of a couple of unread doubles per object, and in exchange
/// the painter, the bounds, the rasteriser, the tag and the core all handle
/// EFFECTS rather than handling a shadow and then handling a glow. Adding an
/// effect the old way took thirteen files; the point of this shape is that it
/// takes a member of <see cref="EffectKind"/> and a case in the recipe.
///
/// EVERY LENGTH IS NORMALIZED, like the rest of the model: a fraction of the
/// page's WIDTH. An effect in pixels would sit a different distance away on
/// every page size and slide as the view zoomed. Normalized means the effect
/// belongs to the object, so rotating the page turns it and zooming scales it
/// with no further code, because the derived offset goes through the same
/// projection the object's own points do.
///
/// OPACITY IS THE COLOUR'S ALPHA. There is deliberately no separate opacity
/// field: two ways to say how solid a mark is need a rule about which wins.
/// </summary>
/// <param name="Color">What the effect is drawn in, alpha included.</param>
/// <param name="Blur">
/// The blur RADIUS. Sigma is half of it, which is the relation every drawing
/// tool uses and the one that makes a stated radius look like the distance an
/// edge takes to fade.
/// </param>
/// <param name="Distance">How far the mark is thrown from the object.</param>
/// <param name="AngleDeg">
/// Where the LIGHT is, in degrees counter-clockwise from due east, so 90 is lit
/// from directly above and the mark falls straight down. It always falls the
/// opposite way, which is the convention every drawing tool uses.
///
/// ANGLE AND DISTANCE ARE THE SOURCE OF TRUTH and the offset is derived, not the
/// other way round: an angle cannot be recovered from an offset of no length, so
/// storing x and y would throw the direction away the moment somebody dragged
/// the distance down to nothing and back.
/// </param>
/// <param name="Amount">
/// How far the silhouette is grown or eaten into before anything else happens,
/// for the effects that do that. Carried and round-tripped by the drop shadow's
/// spread, which nothing draws yet.
/// </param>
public readonly record struct EffectSpec(
    EffectKind Kind,
    RenderColor Color,
    double Blur = 0,
    double Distance = 0,
    double AngleDeg = 0,
    double Amount = 0)
{
    /// <summary>
    /// How far the mark sits from its object horizontally, in normalized page
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

    /// <summary>
    /// How far this effect's ink escapes the silhouette that produced it, in
    /// normalized page units, ignoring where it is thrown.
    ///
    /// THE ONE STATEMENT OF THE REACH. The preview's layer, the dirty region,
    /// the crop the committed picture is rasterised into and the rectangle
    /// render_core reserves in the annotation are all this number in their own
    /// units. They agreed before by each carrying the same arithmetic, which is
    /// agreement right up until one of them is edited.
    ///
    /// <see cref="OverlayProjection.BlurReachSigmas"/> is measured rather than
    /// assumed, and short of it a blur is not softened at the edge, it is cut
    /// off in a straight line, because every one of those boxes is a clip set
    /// before the paint rather than a promise to tidy up after.
    ///
    /// <see cref="Amount"/> is NOT counted, because nothing draws it yet. An
    /// effect that grows its silhouette reaches further by exactly that much,
    /// and this is where that goes when one lands.
    /// </summary>
    public double Reach => Blur * OverlayProjection.BlurReachSigmas / 2;
}
