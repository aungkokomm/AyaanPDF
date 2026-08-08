namespace PdfEditorApp.Viewport;

/// <summary>
/// Which shape controls the property bar should offer.
///
/// Pulled out of the view so the RULE can be asserted. The corner-radius slider
/// shipped invisible because its visibility was decided in a fill-flyout handler
/// instead of in the method that decides every other section, and nothing in the
/// suite could see the difference. Putting the decision here does not prove the
/// view calls it, but it does mean the decision itself is no longer guesswork.
/// </summary>
public static class ShapePropertyBar
{
    /// <summary>
    /// The corner slider is worth showing when it would act on something: a
    /// rounded rectangle is selected, or the shape tool is armed with that kind
    /// and nothing is selected, so the next drag will make one.
    ///
    /// It is deliberately hidden when the tool is armed for rounded rectangles
    /// but some OTHER object is selected: the slider would then appear to
    /// describe the selection while actually setting a default for later, which
    /// is worse than not offering it.
    /// </summary>
    public static bool ShouldShowCornerRadius(
        bool selectionIsRoundedRect,
        bool toolDrawsShapes,
        ShapeKind activeShapeKind,
        bool anythingSelected)
    {
        if (selectionIsRoundedRect) { return true; }
        return toolDrawsShapes
            && activeShapeKind == ShapeKind.RoundedRectangle
            && !anythingSelected;
    }
}
