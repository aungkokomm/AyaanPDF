using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// PDFium annotation subtypes, mirroring ANNOT_* in render_core and
/// <c>AnnotSubtype</c> in the app's interop layer.
///
/// Repeated here because this library cannot reference the WinUI app project,
/// and the model has to be able to tell a stamp from an ink stroke when the
/// annotation carries no tag of ours.
/// </summary>
public static class PdfAnnotationSubtype
{
    public const int Other = 0;
    public const int Text = 1;
    public const int Highlight = 2;
    public const int Ink = 3;
    public const int Stamp = 4;
    public const int Square = 5;
    public const int FreeText = 6;
    public const int Underline = 7;
    public const int Strikeout = 8;
    public const int Squiggly = 9;
}

/// <summary>What an object in the model is.</summary>
public enum DocumentObjectKind
{
    /// <summary>One of our editable shapes, fully described by its tag.</summary>
    Shape,

    /// <summary>One of our text boxes. Modelled as opaque: the text pipeline
    /// owns its representation and this stage does not touch it.</summary>
    TextBox,

    /// <summary>An image stamp. Opaque: its content is pixels, not a tag.</summary>
    Stamp,

    /// <summary>A freehand ink stroke.</summary>
    Ink,

    /// <summary>A text highlight.</summary>
    Highlight,

    /// <summary>An annotation this app did not create, or one it cannot
    /// describe. Present in the model so that Z-ORDER is accurate; a page whose
    /// foreign annotations were dropped would report the wrong stacking for
    /// everything above them.</summary>
    Unknown,

    /// <summary>
    /// A piece of the document's own text, out of the page's CONTENT stream
    /// rather than its annotation list.
    ///
    /// The only kind here that is not an annotation, which is why it is last:
    /// every value before it can be handed to code that acts on annotations by
    /// index, and this one cannot.
    /// </summary>
    PageText,
}

/// <summary>
/// One object on a page.
///
/// READ-ONLY, and deliberately NOT authoritative: the PDF remains the source of
/// truth for every behaviour in the app. This is a faithful snapshot taken from
/// the annotations at load time so that later stages have something to reason
/// about that is not a string being re-parsed on demand.
///
/// Bounds are normalized with a top-left origin and BOTH axes divided by the
/// page WIDTH, which is the convention render_core reports and the overlay
/// draws in. They come from the annotation's /Rect, so for a shape they include
/// the stroke padding the writer added; that is the same rectangle the move and
/// resize paths already operate on, and matching it is more useful here than
/// inventing a tighter one.
/// </summary>
public abstract record DocumentObject
{
    /// <summary>Stable identity, carried on the annotation itself. Empty only
    /// for a mark that has never been stamped.</summary>
    public Guid Id { get; init; }

    public int PageIndex { get; init; }

    /// <summary>Position in the page's annotation list, which IS the paint
    /// order: higher is nearer the front.</summary>
    public int ZOrder { get; init; }

    public TextRect Bounds { get; init; }

    /// <summary>
    /// The page's width in PDF points, or 0 when it was not supplied.
    ///
    /// Carried on the object because the tags store stroke width, corner radius
    /// and upright size in POINTS while everything else here is normalized, and
    /// the page width is the only bridge between the two. Zero means "unknown",
    /// and every derivation that needs it falls back to the untouched /Rect
    /// rather than guessing at a scale.
    /// </summary>
    public double PageWidthPts { get; init; }

    /// <summary>Clockwise rotation about the object's own centre, in screen
    /// degrees. Zero for anything upright and for anything whose kind does not
    /// record an angle.</summary>
    public double RotationDeg { get; init; }

    /// <summary>
    /// The object's own UPRIGHT box, normalized: what it would occupy at zero
    /// degrees, with no stroke padding.
    ///
    /// <see cref="Bounds"/> is the annotation's /Rect, and /Rect is not the
    /// object. For a shape it is inflated by the stroke pad the writer added so
    /// PDFium would not clip the stroke; for anything TURNED it is the
    /// axis-aligned box of the rotated content, which is larger in both axes.
    /// Hit-testing against /Rect is therefore hit-testing against something
    /// bigger than what the user can see, which is the whole reason this
    /// property exists.
    ///
    /// Falls back to <see cref="Bounds"/> whenever the object does not record
    /// enough to do better, so it is always safe to read.
    /// </summary>
    public TextRect UprightBounds
    {
        // Backed by a nullable so the fallback is structural. An init-only
        // property left unset would report an empty rect at the origin, and a
        // hit test against that is not a degraded answer, it is a wrong one.
        get => _uprightBounds ?? Bounds;
        init => _uprightBounds = value;
    }

    private readonly TextRect? _uprightBounds;

    /// <summary>The annotation-level opacity PDFium reports. Note that a shape
    /// also carries alpha inside its stroke and fill colours, which is where
    /// the app's own opacity controls actually write.</summary>
    public double Opacity { get; init; }

    /// <summary>The raw /Contents tag, kept so a later stage can write the
    /// object back out without having to reconstruct a representation the
    /// model does not fully model yet.</summary>
    public string? RawTag { get; init; }

    public DocumentObjectKind Kind { get; init; }

    /// <summary>
    /// The group this object belongs to, or empty when it belongs to none.
    /// Stored on the annotation itself, so a group survives the file closing.
    /// </summary>
    public Guid GroupId { get; init; }

    /// <summary>
    /// Whether this object can be removed and put back faithfully.
    ///
    /// Reordering is a run of removals and re-adds, because appending is the
    /// only ordering primitive PDFium has, so an object that cannot be rebuilt
    /// cannot be reordered past either: the operation would destroy it. A shape
    /// and a text box are fully described by their tags, and a stamp carries its
    /// own pixels, which the core can read back out. A mark from another editor
    /// would come back as something else or not at all.
    ///
    /// Ink is decided by <see cref="OpaqueObject"/>, which is the only thing
    /// that knows whether a given stroke carries its own points.
    /// </summary>
    public virtual bool IsRebuildable => Kind
        is DocumentObjectKind.Shape
        or DocumentObjectKind.TextBox
        or DocumentObjectKind.Stamp;
}

/// <summary>
/// One of our editable shapes, fully described.
///
/// Every property here is recovered from the tag plus the annotation's bounds,
/// which is the same pair the existing resize and restyle paths rebuild a shape
/// from. Nothing is inferred or defaulted beyond what those paths already do.
/// </summary>
public sealed record ShapeObject : DocumentObject
{
    public ShapeKind ShapeKind { get; init; }

    /// <summary>
    /// The shape's geometry in the app's own terms, reusing <see cref="ShapeDraft"/>
    /// rather than a parallel type. Carries the drag DIRECTION, restored from
    /// the tag's flip flags, so an arrow in the model points the way it points
    /// on the page.
    ///
    /// Spans the annotation's /Rect, so it INCLUDES the stroke pad and, for a
    /// turned shape, the enlargement rotation caused. That is the same rectangle
    /// the move and resize paths operate on, which is why it is kept as it is.
    /// Anything measuring the shape itself wants <see cref="UprightGeometry"/>.
    /// </summary>
    public ShapeDraft Geometry { get; init; }

    /// <summary>
    /// The same drag, spanning <see cref="DocumentObject.UprightBounds"/>: the
    /// shape as DRAWN, with the stroke pad removed and any rotation undone.
    ///
    /// This is the geometry to measure against. Rotate it back about the centre
    /// of <see cref="DocumentObject.Bounds"/> by
    /// <see cref="DocumentObject.RotationDeg"/> to get where it sits on the page.
    /// Equal to <see cref="Geometry"/> when the page width was not supplied, so
    /// a caller that has no scale is no worse off than before.
    /// </summary>
    public ShapeDraft UprightGeometry
    {
        get => _uprightGeometry ?? Geometry;
        init => _uprightGeometry = value;
    }

    private readonly ShapeDraft? _uprightGeometry;

    /// <summary>Stroke colour as "#AARRGGBB", alpha included.</summary>
    public string StrokeHex { get; init; } = string.Empty;

    /// <summary>Stroke width in PDF points, as stored in the tag.</summary>
    public double StrokeWidthPts { get; init; }

    /// <summary>Fill as "#AARRGGBB", or null for a stroke-only shape.</summary>
    public string? FillHex { get; init; }

    /// <summary>Corner radius in PDF points. Zero for every kind but a rounded
    /// rectangle, and for a rounded rectangle drawn square.</summary>
    public double CornerRadiusPts { get; init; }

    /// <summary>
    /// The shape's gradient, in its own shape-relative fractions, or null for
    /// every shape without one.
    ///
    /// SEPARATE FROM <see cref="FillHex"/> rather than folded into it, because
    /// the two live in different halves of the tag and a shape must never
    /// carry both. The positional field is the solid; the tail carries this.
    ///
    /// Here because the builder has already parsed the tag this comes out of,
    /// so knowing which shapes on a page have a gradient costs nothing beyond
    /// the model that was going to be built anyway. That is what makes the live
    /// gradient overlay affordable: PDFium cannot draw a shading it has not
    /// been given one for, and finding the shapes that need standing in for
    /// must not mean reading the document again.
    /// </summary>
    public GradientFill? Gradient { get; init; }
}

/// <summary>
/// An object the model records but does not describe: a text box, a stamp, an
/// ink stroke, a highlight, or a foreign annotation.
///
/// Deliberately shallow. Modelling a text box's words or a stamp's pixels would
/// mean duplicating the text and image pipelines, which this stage is explicitly
/// not touching. What matters is that the object EXISTS at a known place in the
/// z-order with a known identity, so the model's picture of a page is complete.
/// </summary>
public sealed record OpaqueObject : DocumentObject
{
    /// <summary>The PDFium subtype, for callers that need to tell one opaque
    /// object from another without re-reading the document.</summary>
    public int Subtype { get; init; }

    /// <summary>
    /// A freehand stroke's CONTROL points, normalized, in drawing order. Empty
    /// for every other kind, and for ink this app did not write.
    ///
    /// These are the thinned points the tag stores, not the fitted curve that is
    /// drawn from them. The fit passes through every one of them and the gaps
    /// are short by construction, so measuring distance to this polyline agrees
    /// with the drawn stroke to well inside the stroke's own width. Storing the
    /// fitted curve instead would multiply the point count on a model that is
    /// rebuilt after every edit, to move an answer that is already inside the
    /// tolerance.
    ///
    /// An ink stroke is the one object whose shape is genuinely a point cloud,
    /// so this is the only way to know where it actually is. Its /Rect says
    /// nothing: a diagonal scribble fills very little of its own box.
    /// </summary>
    public IReadOnlyList<(double X, double Y)> InkPoints { get; init; } = [];

    /// <summary>The stroke's width in normalized units, as its tag records it.
    /// Zero for anything that is not our ink.</summary>
    public double InkStrokeWidth { get; init; }

    /// <summary>
    /// A stroke this app drew CAN be rebuilt, and so can be reordered.
    ///
    /// Its control points are its complete description, which is the same basis
    /// on which a shape qualifies. The base rule predates that: it was written
    /// when a stroke really was an opaque point cloud, and refusing meant a page
    /// with any drawing on it could not have its z-order changed at all, with a
    /// message blaming a mark this app did not create.
    ///
    /// Ink from another editor genuinely has nothing to rebuild from, and its
    /// refusal stands: <see cref="InkPoints"/> is empty for exactly those.
    /// </summary>
    public override bool IsRebuildable =>
        base.IsRebuildable || (Kind == DocumentObjectKind.Ink && InkPoints.Count > 0);
}

/// <summary>One page's objects, in paint order.</summary>
public sealed record PageModel
{
    public int PageIndex { get; init; }

    /// <summary>The page's width in PDF points, or 0 when it was not supplied.
    /// The scale every points-valued field on this page's objects is converted
    /// through.</summary>
    public double WidthPts { get; init; }

    /// <summary>Ordered back to front: the last entry is on top.</summary>
    public IReadOnlyList<DocumentObject> Objects { get; init; } = [];

    /// <summary>Just the editable shapes, in paint order.</summary>
    public IEnumerable<ShapeObject> Shapes => Objects.OfType<ShapeObject>();

    /// <summary>The document's own text objects, in paint order.</summary>
    public IEnumerable<PageTextObject> PageTexts => Objects.OfType<PageTextObject>();

    /// <summary>
    /// Everything that IS an annotation, in paint order.
    ///
    /// The list every existing caller means when it says "objects". A page's
    /// annotations are indexed by <see cref="DocumentObject.ZOrder"/>, and that
    /// index is passed straight to calls that move, restyle and delete them; a
    /// page text object carries no such index and must never reach them.
    /// </summary>
    public IEnumerable<DocumentObject> Annotations =>
        Objects.Where(o => o.Kind != DocumentObjectKind.PageText);

    /// <summary>The object with this identity, or null.</summary>
    public DocumentObject? ById(Guid id) =>
        id == Guid.Empty ? null : Objects.FirstOrDefault(o => o.Id == id);
}

/// <summary>
/// The document as a set of pages of objects.
///
/// A SNAPSHOT, not a live model. It is built from whatever pages have been
/// loaded and goes stale the moment the document is edited; nothing in the app
/// reads it for behaviour. Making it authoritative is a later stage, and doing
/// so before it is proven accurate would put a second source of truth in a
/// codebase whose bugs have mostly come from having two.
/// </summary>
public sealed record DocumentModel
{
    public IReadOnlyList<PageModel> Pages { get; init; } = [];

    public PageModel? Page(int pageIndex) =>
        Pages.FirstOrDefault(p => p.PageIndex == pageIndex);

    public IEnumerable<DocumentObject> AllObjects => Pages.SelectMany(p => p.Objects);

    /// <summary>The object with this identity anywhere in the snapshot.</summary>
    public DocumentObject? ById(Guid id) =>
        id == Guid.Empty ? null : AllObjects.FirstOrDefault(o => o.Id == id);
}
