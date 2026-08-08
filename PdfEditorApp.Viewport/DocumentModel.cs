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

    /// <summary>The annotation-level opacity PDFium reports. Note that a shape
    /// also carries alpha inside its stroke and fill colours, which is where
    /// the app's own opacity controls actually write.</summary>
    public double Opacity { get; init; }

    /// <summary>The raw /Contents tag, kept so a later stage can write the
    /// object back out without having to reconstruct a representation the
    /// model does not fully model yet.</summary>
    public string? RawTag { get; init; }

    public DocumentObjectKind Kind { get; init; }
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
    /// </summary>
    public ShapeDraft Geometry { get; init; }

    /// <summary>Stroke colour as "#AARRGGBB", alpha included.</summary>
    public string StrokeHex { get; init; } = string.Empty;

    /// <summary>Stroke width in PDF points, as stored in the tag.</summary>
    public double StrokeWidthPts { get; init; }

    /// <summary>Fill as "#AARRGGBB", or null for a stroke-only shape.</summary>
    public string? FillHex { get; init; }

    /// <summary>Clockwise rotation about the shape's centre, in screen degrees.</summary>
    public double RotationDeg { get; init; }

    /// <summary>Corner radius in PDF points. Zero for every kind but a rounded
    /// rectangle, and for a rounded rectangle drawn square.</summary>
    public double CornerRadiusPts { get; init; }
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
}

/// <summary>One page's objects, in paint order.</summary>
public sealed record PageModel
{
    public int PageIndex { get; init; }

    /// <summary>Ordered back to front: the last entry is on top.</summary>
    public IReadOnlyList<DocumentObject> Objects { get; init; } = [];

    /// <summary>Just the editable shapes, in paint order.</summary>
    public IEnumerable<ShapeObject> Shapes => Objects.OfType<ShapeObject>();

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
