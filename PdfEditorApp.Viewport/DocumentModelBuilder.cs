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
    string? Contents,
    Guid GroupId = default);

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
    /// <param name="pageWidthPts">
    /// The page's width in PDF points, which is the scale between the tags'
    /// points-valued fields (stroke width, corner radius, upright size) and the
    /// normalized units everything else is in. A page property, so it is passed
    /// once here rather than repeated on every snapshot.
    ///
    /// Zero is allowed and means "unknown": every derivation that needs the
    /// scale then falls back to the annotation's own /Rect, which is exactly
    /// what the model did before it had this at all.
    /// </param>
    public static PageModel BuildPage(
        int pageIndex,
        IReadOnlyList<AnnotationSnapshot> annotations,
        double pageWidthPts = 0,
        IReadOnlyList<PageTextSnapshot>? pageText = null)
    {
        var objects = new List<DocumentObject>(
            (annotations?.Count ?? 0) + (pageText?.Count ?? 0));

        // PAGE CONTENT FIRST, because that is the order it is painted in: a
        // page's own text is drawn as part of the page, and every annotation is
        // drawn over the finished page. So a highlight laid across a word is
        // above the word, and clicking where they overlap picks the highlight,
        // which is what the reader can see.
        foreach (var t in (pageText ?? []).OrderBy(t => t.ObjectIndex))
        {
            objects.Add(BuildPageText(pageIndex, t, pageWidthPts, objects.Count));
        }

        foreach (var a in (annotations ?? []).OrderBy(a => a.Index))
        {
            objects.Add(BuildObject(pageIndex, a, pageWidthPts));
        }

        return new PageModel
        {
            PageIndex = pageIndex,
            WidthPts = pageWidthPts,
            Objects = objects,
        };
    }

    /// <summary>
    /// One of the document's own text objects.
    ///
    /// ⚠️ ZOrder is its position in the MODEL's list, not an annotation index,
    /// and the real content index is kept separately on
    /// <see cref="PageTextObject.ObjectIndex"/>. Every other object here can
    /// have its ZOrder handed to an annotation call; this one cannot, and
    /// giving it a plausible-looking one is how it would be.
    ///
    /// No Id. Identity for page content is a later stage's problem, and an
    /// invented Guid would be a new one on every reload, which is exactly the
    /// bug the annotation loader was fixed for.
    /// </summary>
    private static DocumentObject BuildPageText(
        int pageIndex, PageTextSnapshot t, double pageWidthPts, int zOrder) =>
        new PageTextObject
        {
            PageIndex = pageIndex,
            ZOrder = zOrder,
            Bounds = new TextRect(t.Left, t.Top, t.Right, t.Bottom),
            PageWidthPts = pageWidthPts,
            ObjectIndex = t.ObjectIndex,
            Text = t.Text,
            FontName = t.FontName,
            FontSizePts = t.FontSizePts,
            ColorRgb = t.ColorRgb,
            IsFontEmbedded = t.IsFontEmbedded,
        };

    /// <summary>Assembles pages into a document snapshot, page order preserved.</summary>
    public static DocumentModel Build(IEnumerable<PageModel> pages) =>
        new() { Pages = (pages ?? []).OrderBy(p => p.PageIndex).ToList() };

    private static DocumentObject BuildObject(int pageIndex, AnnotationSnapshot a, double pageWidthPts)
    {
        var bounds = new TextRect(a.Left, a.Top, a.Right, a.Bottom);

        if (ShapeTagReader.TryParse(a.Contents, out ShapeTag tag))
        {
            var upright = UprightBoundsFor(tag, bounds, pageWidthPts);
            return new ShapeObject
            {
                Id = a.Id,
                PageIndex = pageIndex,
                ZOrder = a.Index,
                Bounds = bounds,
                PageWidthPts = pageWidthPts,
                UprightBounds = upright,
                Opacity = a.Opacity,
                RawTag = a.Contents,
                GroupId = a.GroupId,
                Kind = DocumentObjectKind.Shape,
                ShapeKind = tag.Kind,
                Geometry = GeometryFrom(tag, bounds),
                UprightGeometry = GeometryFrom(tag, upright),
                StrokeHex = tag.StrokeHex,
                StrokeWidthPts = tag.StrokeWidthPts,
                FillHex = tag.FillHex,
                RotationDeg = tag.RotationDeg,
                CornerRadiusPts = tag.CornerRadiusPts,
                Gradient = ShapeFillTag.From(tag).Gradient,
            };
        }

        var kind = ClassifyOpaque(a);
        var (rotation, uprightBounds) = OpaqueFrameFor(kind, a.Contents, bounds);
        var (inkPoints, inkWidth) = InkFor(kind, a.Contents);

        return new OpaqueObject
        {
            Id = a.Id,
            PageIndex = pageIndex,
            ZOrder = a.Index,
            Bounds = bounds,
            PageWidthPts = pageWidthPts,
            UprightBounds = uprightBounds,
            RotationDeg = rotation,
            Opacity = a.Opacity,
            RawTag = a.Contents,
            GroupId = a.GroupId,
            Kind = kind,
            Subtype = a.Subtype,
            InkPoints = inkPoints,
            InkStrokeWidth = inkWidth,
        };
    }

    /// <summary>
    /// Half a capture pixel, normalized: the floor a de-padding inset may not
    /// cross. Mirrors the <c>- 0.5</c> in render_core's resize path, which works
    /// in capture-space pixels. Without a floor, insetting a hairline shape by
    /// its own pad turns the box inside out.
    /// </summary>
    private const double MinHalfExtent = 0.5 / 1000.0;

    /// <summary>
    /// The shape's own upright box: /Rect with the writer's additions taken back
    /// off.
    ///
    /// This mirrors <c>resize_shape_annotation_inner</c> in render_core, which
    /// is the code that already has to undo the same two things in order to
    /// redraw a shape at a new size. Doing it identically here is what makes the
    /// model agree with what is on the page; doing it differently is how the
    /// model and the document start to disagree.
    ///
    /// Two cases, and they are not symmetrical:
    ///
    /// TURNED: /Rect is the axis-aligned box of the rotated content, so it
    /// cannot be inverted at all. At 45 degrees infinitely many boxes share one
    /// AABB. The CENTRE of /Rect is exact at every angle though, so centre plus
    /// the size the tag recorded reconstructs the shape precisely.
    ///
    /// UPRIGHT: /Rect is the drag's extent grown by width/2 + 1 on every side,
    /// so PDFium would not clip the stroke. That is invertible, and insetting by
    /// the same amount recovers the drag.
    ///
    /// Anything else, a turned shape written before the upright size was
    /// recorded, or a page whose width is unknown, keeps the padded /Rect. That
    /// is the fallback the core uses too, and it is no worse than what the model
    /// reported before.
    /// </summary>
    private static TextRect UprightBoundsFor(ShapeTag tag, TextRect bounds, double pageWidthPts)
    {
        if (pageWidthPts <= 0) { return bounds; }

        // THE EFFECTS COME OFF FIRST, which is the order render_core's own
        // inversion uses and the reason it is stated the same way here. /Rect is
        // grown by the room the effects asked for so PDFium does not crop a
        // shadow; reading those edges as the shape's own makes the shape as big
        // as its shadow.
        //
        // Before the ROTATED branch as well, and that is not a detail: a shadow
        // thrown down and to the right opens only two sides, so the CENTRE of
        // /Rect is not the shape's centre, and reconstructing a turned shape
        // about it puts the shape somewhere it is not.
        bounds = ShapeEffectsRoom.TakenOff(
            bounds, ShapeEffectsRoom.Of(tag.EffectsText, pageWidthPts));

        if (tag.RotationDeg != 0)
        {
            if (tag.BoxWidthPts <= 0 || tag.BoxHeightPts <= 0) { return bounds; }

            double cx = (bounds.Left + bounds.Right) / 2;
            double cy = (bounds.Top + bounds.Bottom) / 2;
            double halfW = tag.BoxWidthPts / pageWidthPts / 2;
            double halfH = tag.BoxHeightPts / pageWidthPts / 2;
            return new TextRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
        }

        double pad = (tag.StrokeWidthPts / 2 + 1) / pageWidthPts;
        double maxInset = Math.Max(
            0, Math.Min(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) / 2 - MinHalfExtent);
        double inset = Math.Min(pad, maxInset);

        return new TextRect(
            bounds.Left + inset, bounds.Top + inset,
            bounds.Right - inset, bounds.Bottom - inset);
    }

    /// <summary>
    /// A non-shape object's angle and upright box.
    ///
    /// Text boxes and image stamps both turn, and both record their own upright
    /// rectangle for the same reason a rotated shape does: their /Rect grows to
    /// contain the turned content, so it is not the object. Everything else has
    /// no angle to report and no better box than the one it already has.
    /// </summary>
    private static (double RotationDeg, TextRect Upright) OpaqueFrameFor(
        DocumentObjectKind kind, string? contents, TextRect bounds)
    {
        if (kind == DocumentObjectKind.TextBox && TextBoxTagReader.TryParse(contents, out var text))
        {
            return (text.RotationDeg,
                    text.HasBoxRect
                        ? new TextRect(text.BoxLeft, text.BoxTop, text.BoxRight, text.BoxBottom)
                        : bounds);
        }

        if (kind == DocumentObjectKind.Stamp && StampTagReader.TryParse(contents, out var stamp))
        {
            return (stamp.RotationDeg, stamp.Bounds);
        }

        return (0, bounds);
    }

    /// <summary>
    /// A freehand stroke's points, recovered from its tag, AS DRAWN ON THE PAGE.
    ///
    /// The tag stores the UPRIGHT points and an angle, because that is what lets
    /// a turned stroke be resized without shearing. What anyone measuring
    /// against the stroke needs is where it actually is, so the angle is applied
    /// here, once, and the model reports placed points.
    ///
    /// Handing the upright ones over instead is precisely the bug this replaced:
    /// a turned drawing could not be picked by clicking on it, while clicking
    /// empty page where it would have been unturned picked it up.
    ///
    /// Note the object deliberately reports NO rotation of its own. Rotating a
    /// point cloud moves its bounding box, so the centre of a stroke's /Rect is
    /// NOT the point it was turned about, and inverse-rotating a pointer about
    /// that centre would not undo the rotation. Placing the points here means
    /// nothing downstream has to know the angle at all.
    ///
    /// Only our own ink carries a tag. A stroke from another editor comes back
    /// empty and is left to its bounding box, which is all anyone knows about it.
    /// </summary>
    private static (IReadOnlyList<(double X, double Y)> Points, double Width) InkFor(
        DocumentObjectKind kind, string? contents)
    {
        if (kind != DocumentObjectKind.Ink) { return ([], 0); }

        if (!InkTag.TryParse(contents, out _, out double width, out var control, out double deg))
        {
            return ([], 0);
        }

        return (deg == 0 ? control : Geometry2D.RotateAboutCentre(control, deg), width);
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
            PdfAnnotationSubtype.Link => DocumentObjectKind.Link,
            PdfAnnotationSubtype.Widget => DocumentObjectKind.FormField,
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
