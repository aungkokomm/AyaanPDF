namespace PdfEditorApp.Viewport;

/// <summary>
/// Normalized page-local coordinates into slot DIPs.
///
/// This is the app's canonical projection and it stays that way. A renderer may
/// compose it into whatever matrix type it wants, but it does not get to define
/// it: geometry is authored, edited, hit-tested and persisted in normalized
/// units, and nothing downstream is allowed to become the origin of truth for
/// where a mark is.
///
/// Normalized means top-left origin with BOTH axes divided by the page WIDTH,
/// which is what render_core reports and what the overlay has always drawn in.
/// Dividing both by the width rather than each by its own extent is what keeps
/// a circle circular on a page that is not square.
///
/// Stage 0 covers only the two steps the existing XAML overlay performs. View
/// rotation and DPI are applied further out and are a later stage; putting them
/// here now would be inventing a transform nothing yet asks for.
/// </summary>
public static class OverlayProjection
{
    /// <summary>
    /// The hairline an arrow's filled head is outlined with, in DIPs.
    ///
    /// Named because it is the one number in the overlay that is NOT scaled: the
    /// XAML polygon sets StrokeThickness to a literal 0.5 while everything
    /// around it is multiplied by the overlay scale. Reproducing the overlay
    /// exactly means reproducing that too, and a magic 0.5 buried in a paint
    /// call is how a parity difference goes unexplained.
    /// </summary>
    public const double HeadHairlineDips = 0.5;

    /// <summary>
    /// A normalized point in the page's own frame, placed in the continuous
    /// stack: scaled by the overlay scale, then dropped to the page's top.
    /// </summary>
    /// <param name="scale">The fixed content-box width the overlay works in.</param>
    /// <param name="pageTop">Where this page's slot starts in the stack.</param>
    public static (double X, double Y) ToSlot(
        (double X, double Y) point, double scale, double pageTop) =>
        (point.X * scale, (point.Y * scale) + pageTop);

    /// <summary>
    /// A normalized stroke width in slot DIPs.
    ///
    /// Widths are normalized for the same reason positions are: a width in
    /// pixels would mean something different on every page size and at every
    /// zoom, so the same stroke would thicken and thin as the view moved.
    /// </summary>
    public static double ToSlotThickness(double strokeWidth, double scale) =>
        strokeWidth * scale;
}
