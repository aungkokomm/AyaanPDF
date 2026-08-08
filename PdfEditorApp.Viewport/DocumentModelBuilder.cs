using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One annotation as read back from the document, in the terms the builder
/// needs. A plain record so the builder stays in this library, where tests can
/// reach it, rather than in the WinUI project where the interop types live.
///
/// Bounds are normalized (both axes divided by the page WIDTH, top-left origin),
/// matching what render_core reports. <paramref name="Contents"/> is the
/// annotation's tag with any Id prefix already stripped, which is what
/// <c>ReadAnnotationContents</c> hands back.
/// </summary>
public readonly record struct AnnotationSnapshot(
    int Index,
    int Subtype,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Opacity,
    Guid Id,
    string? Contents);

/// <summary>
/// Turns annotations read out of a PDF into the read-only document model.
///
/// The classification order matters and mirrors what the view model already
/// does when deciding what a selected mark is: the TAG is what separates our
/// object types, not the PDFium subtype, because a shape, a text box and an
/// image stamp are all /Stamp annotations underneath.
/// </summary>
public static class DocumentModelBuilder
{
    /// <summary>
    /// Builds one page's model. Annotations are ordered by their index, which
    /// is the page's paint order, so the model's list is back to front.
    /// </summary>
    public static PageModel BuildPage(int pageIndex, IReadOnlyList<AnnotationSnapshot> annotations)
    {
        var objects = new List<DocumentObject>(annotations?.Count ?? 0);

        foreach (var a in (annotations ?? []).OrderBy(a => a.Index))
        {
            objects.Add(BuildObject(pageIndex, a));
        }

        return new PageModel { PageIndex = pageIndex, Objects = objects };
    }

    /// <summary>Assembles pages into a document snapshot, page order preserved.</summary>
    public static DocumentModel Build(IEnumerable<PageModel> pages) =>
        new() { Pages = (pages ?? []).OrderBy(p => p.PageIndex).ToList() };

    private static DocumentObject BuildObject(int pageIndex, AnnotationSnapshot a)
    {
        var bounds = new TextRect(a.Left, a.Top, a.Right, a.Bottom);

        if (ShapeTagReader.TryParse(a.Contents, out ShapeTag tag))
        {
            return new ShapeObject
            {
                Id = a.Id,
                PageIndex = pageIndex,
                ZOrder = a.Index,
                Bounds = bounds,
                Opacity = a.Opacity,
                RawTag = a.Contents,
                Kind = DocumentObjectKind.Shape,
                ShapeKind = tag.Kind,
                Geometry = GeometryFrom(tag, bounds),
                StrokeHex = tag.StrokeHex,
                StrokeWidthPts = tag.StrokeWidthPts,
                FillHex = tag.FillHex,
                RotationDeg = tag.RotationDeg,
                CornerRadiusPts = tag.CornerRadiusPts,
            };
        }

        return new OpaqueObject
        {
            Id = a.Id,
            PageIndex = pageIndex,
            ZOrder = a.Index,
            Bounds = bounds,
            Opacity = a.Opacity,
            RawTag = a.Contents,
            Kind = ClassifyOpaque(a),
            Subtype = a.Subtype,
        };
    }

    /// <summary>
    /// What a non-shape annotation is. Tag first, subtype second, for the same
    /// reason the selection code does it that way: our text boxes and image
    /// stamps are both /Stamp annotations, so the subtype alone cannot tell
    /// them apart.
    /// </summary>
    private static DocumentObjectKind ClassifyOpaque(AnnotationSnapshot a)
    {
        if (TextBoxTagReader.TryParse(a.Contents, out _)) { return DocumentObjectKind.TextBox; }

        if (a.Contents is not null
            && a.Contents.StartsWith("AyaanStamp:", StringComparison.Ordinal))
        {
            return DocumentObjectKind.Stamp;
        }

        return a.Subtype switch
        {
            PdfAnnotationSubtype.Stamp => DocumentObjectKind.Stamp,
            PdfAnnotationSubtype.Ink => DocumentObjectKind.Ink,
            PdfAnnotationSubtype.Highlight => DocumentObjectKind.Highlight,
            _ => DocumentObjectKind.Unknown,
        };
    }

    /// <summary>
    /// Rebuilds the shape's drag as a <see cref="ShapeDraft"/>.
    ///
    /// The tag's flip flags say which corner the drag STARTED at, so an arrow
    /// drawn right to left comes back pointing left. This is the same
    /// reconstruction the resize path performs; doing it identically here is
    /// what makes the model agree with what is on the page.
    ///
    /// The corner fraction is derived back from the stored radius so that a
    /// round-trip through the model does not quietly reset a shape to the
    /// default roundness. It cannot be exact for every box, because the radius
    /// is stored in points and the draft works in normalized units, so it is
    /// left at the default for kinds that have no radius.
    /// </summary>
    private static ShapeDraft GeometryFrom(ShapeTag tag, TextRect bounds)
    {
        double x1 = tag.FlipX ? bounds.Left : bounds.Right;
        double x2 = tag.FlipX ? bounds.Right : bounds.Left;
        double y1 = tag.FlipY ? bounds.Top : bounds.Bottom;
        double y2 = tag.FlipY ? bounds.Bottom : bounds.Top;

        return new ShapeDraft(tag.Kind, x1, y1, x2, y2);
    }
}
