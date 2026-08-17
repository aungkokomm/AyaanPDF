namespace PdfEditorApp.Viewport;

/// <summary>
/// Slot DIPs onto a viewport-sized surface, in device pixels.
///
/// The second half of the chain. <see cref="OverlayProjection"/> takes
/// normalized page-local coordinates to slot DIPs and remains the canonical
/// step; this takes slot DIPs to pixels on a surface that covers only what is
/// on screen. Normalized page-local is still where geometry lives: nothing here
/// is stored on an object, and this is rebuilt from the scroller every frame.
///
/// It exists because a Skia surface is not a Canvas. The XAML overlay spans the
/// whole document stack for free, since a Canvas allocates no pixels of its
/// own, and the ScrollView's compositor zoom scales its vector children while
/// keeping them crisp. A Skia surface allocates every pixel it covers and
/// rasterises once, so a stack-sized one would allocate the whole document and
/// a zoomed one inside the scroller would be magnified into blur. The surface
/// therefore sits OUTSIDE the scroller at viewport size, and zoom and scroll
/// arrive here as numbers instead of as a compositor transform.
///
/// No view rotation HERE, and that is not the same as nowhere. The turn belongs
/// to a page, so it is applied per page in <see cref="OverlayProjection"/> using
/// that page's <see cref="PageTransform"/>, before anything reaches this. By the
/// time coordinates arrive they are slot DIPs and the whole stack shares them,
/// which is exactly why zoom and scroll can be three numbers.
/// </summary>
/// <param name="Zoom">The scroller's zoom factor.</param>
/// <param name="DeviceScale">Device pixels per DIP.</param>
/// <param name="OriginXDips">
/// Where slot (0,0) sits in the viewport, in viewport DIPs. Negative once
/// scrolled: it is the content origin having moved off the top-left. Carries
/// the scroll offset, the centring and the padding as one number, because
/// downstream nothing needs them apart.
/// </param>
/// <param name="OriginYDips">The same, vertically.</param>
public readonly record struct ViewportProjection(
    double Zoom,
    double DeviceScale,
    double OriginXDips,
    double OriginYDips)
{
    /// <summary>A slot-space point in device pixels on the surface.</summary>
    public (double X, double Y) SlotToDevice(double slotX, double slotY) =>
        (((slotX * Zoom) + OriginXDips) * DeviceScale,
         ((slotY * Zoom) + OriginYDips) * DeviceScale);

    /// <summary>
    /// A device-pixel point back in slot space. The inverse is what decides
    /// which shapes are worth drawing, and getting it wrong culls something
    /// that should have been on screen.
    /// </summary>
    public (double X, double Y) DeviceToSlot(double deviceX, double deviceY)
    {
        double z = Zoom > 0 ? Zoom : 1.0;
        double d = DeviceScale > 0 ? DeviceScale : 1.0;

        return (((deviceX / d) - OriginXDips) / z,
                ((deviceY / d) - OriginYDips) / z);
    }

    /// <summary>
    /// A length in slot DIPs as a length in device pixels. Stroke widths take
    /// this: a stroke thickens with zoom, exactly as the compositor thickens
    /// the overlay's.
    /// </summary>
    public double SlotToDeviceLength(double slotLength) => slotLength * Zoom * DeviceScale;

    /// <summary>
    /// The slot-space rectangle a surface of this pixel size can show.
    ///
    /// Grown by <paramref name="padSlot"/> on every side so a shape whose
    /// centre is just off screen, but whose stroke or arrowhead reaches onto
    /// it, is still drawn. Culling exactly to the edge is how a mark disappears
    /// a moment before it should.
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) VisibleSlotBounds(
        int surfaceWidthPx, int surfaceHeightPx, double padSlot = 0)
    {
        var (left, top) = DeviceToSlot(0, 0);
        var (right, bottom) = DeviceToSlot(surfaceWidthPx, surfaceHeightPx);

        return (left - padSlot, top - padSlot, right + padSlot, bottom + padSlot);
    }
}
