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
/// The projection is in two halves, and the difference between them matters.
/// Normalized to the page's own frame is the first; turning that frame with the
/// view is the second, and it is <see cref="PageTransform.ToCard"/>. A renderer
/// needs both. Stopping at the first is invisible at 0 degrees, because the turn
/// is the identity there, and wrong at every other rotation; that is exactly the
/// defect the Skia layer was built with and this is where it is now stated once
/// so both renderers can be held to it.
///
/// DPI and zoom are still applied further out, in <see cref="ViewportProjection"/>.
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
    ///
    /// HALF THE RULE, and correct only for a caller that applies the view's turn
    /// itself or has established there is none. Anything drawing onto the screen
    /// wants the overload below. Kept because the two diagnostic harnesses do
    /// apply the turn themselves, and separating the halves is the whole point
    /// of what they measure.
    /// </summary>
    /// <param name="scale">The fixed content-box width the overlay works in.</param>
    /// <param name="pageTop">Where this page's slot starts in the stack.</param>
    public static (double X, double Y) ToSlot(
        (double X, double Y) point, double scale, double pageTop) =>
        (point.X * scale, (point.Y * scale) + pageTop);

    /// <summary>
    /// A normalized point in slot DIPs, turned with the page it is on.
    ///
    /// The whole rule, and what a renderer drawing over the page stack wants.
    ///
    /// Two orderings in three lines, both load-bearing:
    ///
    /// <list type="bullet">
    /// <item>The overlay scale is applied BEFORE the turn, because
    /// <see cref="PageTransform.ToCard"/> reflects about the content box's own
    /// edges and its <c>ContentWidth</c>/<c>ContentHeight</c> are measured in
    /// those same scaled units. Turning first and scaling after reflects about
    /// the wrong edges.</item>
    /// <item>The page's top is added AFTER the turn, and to Y only. The turn
    /// happens inside one card; the stack offset is what puts that card in the
    /// column, and folding it in first would rotate the whole document's height
    /// into the page.</item>
    /// </list>
    /// </summary>
    public static (double X, double Y) ToSlot(
        (double X, double Y) point, double scale, double pageTop, PageTransform view)
    {
        var (cardX, cardY) = view.ToCard(point.X * scale, point.Y * scale);

        return (cardX, cardY + pageTop);
    }

    /// <summary>
    /// A normalized stroke width in slot DIPs, without the view's turn. The same
    /// half-a-rule caveat as the three-argument <see cref="ToSlot"/> above.
    ///
    /// Widths are normalized for the same reason positions are: a width in
    /// pixels would mean something different on every page size and at every
    /// zoom, so the same stroke would thicken and thin as the view moved.
    /// </summary>
    public static double ToSlotThickness(double strokeWidth, double scale) =>
        strokeWidth * scale;

    /// <summary>
    /// A normalized stroke width in slot DIPs on a page that may be turned.
    ///
    /// A length needs the scale even though <see cref="PageTransform.ToCard"/>
    /// already carries it for points, because a length has no coordinates to
    /// carry it in. A page on its side is scaled down to bring its other axis to
    /// the card's width, so a stroke that kept its weight would come out too
    /// heavy for the shape it outlines. At 0 and 180 the scale is 1 and this is
    /// the line above.
    /// </summary>
    public static double ToSlotThickness(double strokeWidth, double scale, PageTransform view) =>
        strokeWidth * scale * view.Scale;
}
