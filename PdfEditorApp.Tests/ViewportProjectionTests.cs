using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The second half of the coordinate chain: slot DIPs onto a viewport-sized
/// surface, at whatever zoom, scroll and DPI the window is currently at.
///
/// Pure arithmetic, deliberately. The Skia layer applies this as a canvas
/// matrix and ShapeSkiaMatrixTests proves the composition agrees with it, so
/// everything below can be reasoned about without a renderer or a UI thread.
///
/// NO ROTATION. The XAML overlay does not turn with the view, so neither does
/// this, and there is nothing here that pretends to. That is recorded as an
/// open question, not as a passing test.
/// </summary>
public class ViewportProjectionTests
{
    /// <summary>The four scales a Windows display actually reports.</summary>
    public static TheoryData<double> Dpis => new() { 1.0, 1.25, 1.5, 2.0 };

    [Theory]
    [MemberData(nameof(Dpis))]
    public void at_rest_a_slot_point_is_itself_times_the_device_scale(double dpi)
    {
        // Nothing scrolled, nothing zoomed: the only thing between slot DIPs
        // and pixels is the display.
        var p = new ViewportProjection(Zoom: 1, DeviceScale: dpi, OriginXDips: 0, OriginYDips: 0);

        var (x, y) = p.SlotToDevice(100, 250);

        Assert.Equal(100 * dpi, x, precision: 6);
        Assert.Equal(250 * dpi, y, precision: 6);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void zoom_multiplies_before_the_display_does(double dpi)
    {
        // Order matters and is easy to get backwards. Zoom is applied in DIP
        // space, where the content lives; the display scale is applied last, to
        // everything. Swapping them happens to agree when the origin is zero,
        // which is exactly why the origin below is not.
        var p = new ViewportProjection(Zoom: 2.5, DeviceScale: dpi, OriginXDips: 40, OriginYDips: -70);

        var (x, y) = p.SlotToDevice(100, 250);

        Assert.Equal(((100 * 2.5) + 40) * dpi, x, precision: 6);
        Assert.Equal(((250 * 2.5) - 70) * dpi, y, precision: 6);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void a_scrolled_origin_is_negative_and_moves_content_up(double dpi)
    {
        // Scrolling down does not move the shapes' coordinates; it moves where
        // slot (0,0) has got to, which goes negative.
        var atRest = new ViewportProjection(1, dpi, 0, 0);
        var scrolled = new ViewportProjection(1, dpi, 0, -500);

        Assert.Equal(atRest.SlotToDevice(0, 800).Y - (500 * dpi),
                     scrolled.SlotToDevice(0, 800).Y, precision: 6);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void a_point_survives_the_round_trip(double dpi)
    {
        // The inverse is what culling depends on, so it has to be the real
        // inverse and not merely something that looks like one.
        var p = new ViewportProjection(Zoom: 1.75, DeviceScale: dpi, OriginXDips: -220, OriginYDips: -3100);

        var (dx, dy) = p.SlotToDevice(640, 12345);
        var (sx, sy) = p.DeviceToSlot(dx, dy);

        Assert.Equal(640, sx, precision: 6);
        Assert.Equal(12345, sy, precision: 6);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void a_length_takes_zoom_and_the_display_but_not_the_origin(double dpi)
    {
        // A stroke thickens with zoom, exactly as the compositor thickens the
        // overlay's, and is unaffected by where the content has scrolled to.
        var p = new ViewportProjection(Zoom: 3, DeviceScale: dpi, OriginXDips: -900, OriginYDips: -900);

        Assert.Equal(3.2 * 3 * dpi, p.SlotToDeviceLength(3.2), precision: 6);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void the_visible_slot_region_is_the_surface_mapped_back(double dpi)
    {
        // What the surface can show, in the space the shapes are in. At higher
        // zoom the same surface shows LESS of the document, which is the whole
        // reason a viewport-sized surface is affordable.
        int wPx = (int)(1200 * dpi), hPx = (int)(800 * dpi);
        var p = new ViewportProjection(Zoom: 2, DeviceScale: dpi, OriginXDips: -100, OriginYDips: -400);

        var (left, top, right, bottom) = p.VisibleSlotBounds(wPx, hPx);

        Assert.Equal(50, left, precision: 4);           // (0/dpi + 100) / 2
        Assert.Equal(200, top, precision: 4);           // (0/dpi + 400) / 2
        Assert.Equal((1200 + 100) / 2.0, right, precision: 4);
        Assert.Equal((800 + 400) / 2.0, bottom, precision: 4);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void the_visible_region_can_be_padded_so_nothing_pops(double dpi)
    {
        var p = new ViewportProjection(Zoom: 1, DeviceScale: dpi, OriginXDips: 0, OriginYDips: 0);

        var tight = p.VisibleSlotBounds(1000, 1000);
        var padded = p.VisibleSlotBounds(1000, 1000, padSlot: 64);

        Assert.Equal(tight.Left - 64, padded.Left, precision: 6);
        Assert.Equal(tight.Bottom + 64, padded.Bottom, precision: 6);
    }

    [Fact]
    public void zooming_in_shows_less_of_the_document_not_more()
    {
        // The sanity check that catches an inverted zoom, which otherwise looks
        // plausible in every single-value assertion above.
        var outAt1 = new ViewportProjection(1, 1.5, 0, 0).VisibleSlotBounds(1200, 800);
        var inAt4 = new ViewportProjection(4, 1.5, 0, 0).VisibleSlotBounds(1200, 800);

        Assert.True(inAt4.Right - inAt4.Left < outAt1.Right - outAt1.Left,
                    "zoomed in but the visible slot region did not shrink");
    }

    [Fact]
    public void a_degenerate_zoom_or_scale_does_not_divide_by_zero()
    {
        // These arrive from a control during teardown and from a settings file.
        var p = new ViewportProjection(Zoom: 0, DeviceScale: 0, OriginXDips: 10, OriginYDips: 10);

        var (x, y) = p.DeviceToSlot(100, 100);

        Assert.True(double.IsFinite(x) && double.IsFinite(y), $"got ({x}, {y})");
    }
}
