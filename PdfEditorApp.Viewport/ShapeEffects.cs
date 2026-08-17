namespace PdfEditorApp.Viewport;

/// <summary>
/// A hard drop shadow: the same shape again, offset, in another colour, painted
/// underneath.
///
/// NO BLUR, and that is the whole definition rather than a stage on the way to
/// one. A blurred shadow needs a mask filter, a blur radius in a space somebody
/// has to choose, and a bleed that the dirty region and the culler would both
/// have to learn about. This is the shadow that needs none of that: it is the
/// item's own geometry, moved.
///
/// THE OFFSET IS NORMALIZED, like every other length in the model. A shadow in
/// pixels would sit a different distance away on every page size and would slide
/// as the view zoomed; normalized means the shadow belongs to the OBJECT and
/// behaves like part of it. Rotating the page turns the shadow with the shape,
/// zooming scales it, and none of that needs a line of code, because the offset
/// is applied to the points before the existing projection ever sees them.
///
/// Opacity lives in the colour's alpha rather than in a field of its own. There
/// is already exactly one way to say how solid something is in this model and
/// adding a second would mean deciding which one wins.
/// </summary>
public readonly record struct DropShadow(double OffsetX, double OffsetY, RenderColor Color);

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
/// NOT PERSISTED. Shapes are stored as a tag in the PDF annotation's /Contents,
/// written and parsed by hand in render_core, and nothing here is written to it.
/// An effect therefore lives as long as the object is in memory and is gone
/// after a save and reload. Persisting it means extending the tag format on both
/// sides of the FFI, which is a change to the file contract and belongs in its
/// own piece of work.
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
