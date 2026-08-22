using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A gradient between two colours, as the shape itself holds it.
///
/// A PAINT, NOT AN EFFECT. A drop shadow and a glow are marks made BESIDE the
/// shape from its silhouette, which is why they are rasterised and why they
/// reserve room outside its box. A gradient is what the inside of the shape is
/// painted with: it stays inside the path, it needs no room, and it is drawn as
/// real vector paint at every stage. Nothing here goes near
/// <see cref="ShapeEffects"/>.
///
/// THE COORDINATES ARE THE SHAPE'S OWN, normalized 0..1 inside its UPRIGHT box:
/// (0,0) is the box's top-left corner, (1,1) its bottom-right, y running DOWN
/// as it does everywhere else in the model. Not page-relative, and with no
/// transform of its own.
///
/// That choice decides three behaviours at once, and it is worth being explicit
/// that they are consequences rather than features that had to be written:
///
/// <list type="bullet">
/// <item>MOVING the shape moves the gradient with it, because the box moves and
/// nothing here refers to the page.</item>
/// <item>ROTATING the shape turns the gradient with it, because the box is the
/// upright one and the shape's own rotation is applied to the whole box. A
/// gradient needs no angle of its own on top of the shape's.</item>
/// <item>RESIZING the shape STRETCHES the gradient, because 0..1 spans whatever
/// the box currently is. A gradient that kept its absolute length would slide
/// out of a shape being dragged wider. This is deliberate and it is what every
/// drawing tool does.</item>
/// </list>
///
/// Endpoints outside 0..1 are legal: a gradient may begin before the box and
/// end after it, which is how a soft partial ramp is expressed. They are not
/// clamped. The two endpoints must DIFFER, because a gradient of no length has
/// no direction and is a solid colour with extra steps.
/// </summary>
/// <param name="From">The colour at the first endpoint, alpha included.</param>
/// <param name="To">The colour at the second.</param>
public readonly record struct GradientFill(
    RenderColor From,
    RenderColor To,
    double X0,
    double Y0,
    double X1,
    double Y1)
{
    /// <summary>
    /// Whether this describes a gradient that can actually be painted: two
    /// endpoints that differ, all four numbers finite.
    ///
    /// The colours are NOT checked here. Two identical stops are a legitimate
    /// thing to be halfway through setting up, and both stops transparent is
    /// decided where every other "paints nothing" is decided, on the way to the
    /// tag.
    /// </summary>
    public bool IsDrawable =>
        double.IsFinite(X0) && double.IsFinite(Y0)
        && double.IsFinite(X1) && double.IsFinite(Y1)
        && (X0 != X1 || Y0 != Y1);

    /// <summary>
    /// The same gradient written the other way round: stops swapped, endpoints
    /// swapped. Identical paint, and the one normalization the tag needs.
    /// </summary>
    public GradientFill Reversed() => new(To, From, X1, Y1, X0, Y0);
}

/// <summary>
/// What the inside of a shape is painted with: nothing, one colour, or a
/// gradient.
///
/// One type for what used to be a single nullable fill colour, so that every
/// place asking "what is this shape filled with" gets an answer that can be a
/// gradient rather than a colour or null. The solid case is exactly the
/// existing behaviour and is still stored exactly where it was stored, in the
/// tag's positional fill field; see <see cref="ShapeFillTag"/>.
///
/// A GRADIENT IS NEVER ALSO A SOLID. There is no average colour kept alongside
/// it and nothing anywhere may substitute one: a renderer that cannot draw the
/// gradient yet draws no fill, which is visibly missing rather than quietly
/// wrong. The consequence is that a shape whose gradient has not reached the
/// file yet appears unfilled in anything rendering the file directly, and that
/// is the intended failure mode.
/// </summary>
public readonly record struct ShapeFill
{
    private ShapeFill(RenderColor? solid, GradientFill? gradient)
    {
        Solid = solid;
        Gradient = gradient;
    }

    /// <summary>A stroke-only shape. The historic default.</summary>
    public static ShapeFill None => default;

    /// <summary>One flat colour, alpha included.</summary>
    public static ShapeFill Of(RenderColor colour) => new(colour, null);

    /// <summary>A gradient. Not drawable, not a fill.</summary>
    public static ShapeFill Of(GradientFill gradient) =>
        gradient.IsDrawable ? new(null, gradient) : None;

    /// <summary>The flat colour, or null when this is not a solid fill.</summary>
    public RenderColor? Solid { get; }

    /// <summary>The gradient, or null when this is not a gradient fill.</summary>
    public GradientFill? Gradient { get; }

    /// <summary>Whether the shape is stroke-only.</summary>
    public bool IsEmpty => Solid is null && Gradient is null;
}
