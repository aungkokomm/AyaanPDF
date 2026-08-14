namespace PdfEditorApp.Viewport;

/// <summary>
/// Whether an annotation can be redrawn at a new size.
///
/// The rule lives here, as a pure function of the two things that decide it,
/// rather than inline in the view model where no test can reach it. It gates
/// whether resize handles are drawn at all and whether a drag on one is read as
/// a resize, so getting it wrong is silent: the object simply has no handles
/// and nobody can say why.
/// </summary>
public static class AnnotationResize
{
    /// <summary>
    /// True when this annotation can be rebuilt at a different size.
    /// </summary>
    /// <param name="subtype">The PDFium subtype, as
    /// <see cref="PdfAnnotationSubtype"/> numbers them.</param>
    /// <param name="contents">The annotation's /Contents, with or without an
    /// identity prefix; every reader used here strips one.</param>
    public static bool CanResize(int subtype, string? contents)
    {
        // One of our shapes. Stored as a /Stamp underneath, and fully described
        // by its tag, so it can be drawn again at any size.
        if (ShapeTagReader.IsShapeTag(contents))
        {
            return true;
        }

        // One of our strokes. Its tag carries the control points, which is the
        // whole description a rebuild needs: InkTag.ScaleTo maps them onto any
        // rectangle, and that is exactly what a resize is.
        //
        // This is the case the rule used to get wrong. It refused EVERY /Ink
        // annotation, which was correct when it was written and stopped being
        // correct the moment strokes started carrying a tag. Drawings spent
        // that whole time as the one object that could be selected, moved,
        // grouped, reordered and deleted, but not resized.
        if (InkTag.TryParse(contents, out _, out _, out _))
        {
            return true;
        }

        // A stroke from another editor, or one of ours damaged beyond reading,
        // is still an arbitrary point cloud with nothing to rebuild from.
        // PDFium will not scale it and it cannot be recreated from outside.
        if (subtype == PdfAnnotationSubtype.Ink)
        {
            return false;
        }

        // Everything else either scales in place or can be rebuilt from its own
        // image: text boxes, image stamps, and marks from other editors.
        return true;
    }
}
