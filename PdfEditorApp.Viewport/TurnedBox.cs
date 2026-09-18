namespace PdfEditorApp.Viewport;

/// <summary>
/// A turned mark's own upright box, put where its reported rectangle now is.
/// </summary>
/// <remarks>
/// A turned mark reports the axis-aligned box AROUND its rotated content,
/// which is larger than the mark. A writer that takes the upright box (a text
/// box's re-layout, a shape's resize) grows the mark if it is handed that
/// larger box instead, and the growth compounds with every move. Both boxes
/// share a centre, because a mark turns about its own centre, so the upright
/// box can be carried to wherever the reported one was moved.
/// </remarks>
public static class TurnedBox
{
    /// <summary>
    /// The upright box (<paramref name="box"/>'s size) centred on the centre of
    /// <paramref name="at"/>. Given the upright box itself, returns it unchanged.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) Recentre(
        (double Left, double Top, double Right, double Bottom) box,
        (double Left, double Top, double Right, double Bottom) at)
    {
        double halfW = (box.Right - box.Left) / 2;
        double halfH = (box.Bottom - box.Top) / 2;
        double cx = (at.Left + at.Right) / 2;
        double cy = (at.Top + at.Bottom) / 2;
        return (cx - halfW, cy - halfH, cx + halfW, cy + halfH);
    }
}
