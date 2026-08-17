using System;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Ties the renderer's matrix to the app's projection.
///
/// ViewportProjectionTests proves the arithmetic. This proves Skia performs
/// that arithmetic and not a near miss of it, which is the only claim that
/// matters: a matrix composed in the wrong order still produces a picture, and
/// at the origin the two orders agree, so the mistake hides until somebody
/// scrolls.
/// </summary>
public class ShapeSkiaMatrixTests
{
    public static TheoryData<double> Dpis => new() { 1.0, 1.25, 1.5, 2.0 };

    /// <summary>Scroll, zoom and an off-origin content position, all at once.</summary>
    private static ViewportProjection Busy(double dpi) =>
        new(Zoom: 1.75, DeviceScale: dpi, OriginXDips: -220.5, OriginYDips: -3100.25);

    [Theory]
    [MemberData(nameof(Dpis))]
    public void the_matrix_maps_a_point_where_the_projection_says(double dpi)
    {
        var p = Busy(dpi);
        var matrix = ShapeSkiaPainter.MatrixFor(p);

        foreach (var (sx, sy) in new[] { (0.0, 0.0), (640.0, 12345.0), (-80.0, 25.5) })
        {
            var mapped = matrix.MapPoint((float)sx, (float)sy);
            var (ex, ey) = p.SlotToDevice(sx, sy);

            Assert.Equal(ex, mapped.X, precision: 2);
            Assert.Equal(ey, mapped.Y, precision: 2);
        }
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void a_length_scales_by_zoom_and_the_display_together(double dpi)
    {
        var p = Busy(dpi);
        var matrix = ShapeSkiaPainter.MatrixFor(p);

        // Two points a known distance apart, mapped, and the distance measured.
        var a = matrix.MapPoint(0, 0);
        var b = matrix.MapPoint(100, 0);

        Assert.Equal(p.SlotToDeviceLength(100), b.X - a.X, precision: 2);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void a_rectangle_rasterises_where_the_projection_puts_it(double dpi)
    {
        // The end-to-end version: normalized geometry, through the slot
        // projection, through the viewport matrix, onto real pixels.
        var shape = new ShapeAnnotation(
            0, new ShapeDraft(ShapeKind.Rectangle, 0.1, 0.1, 0.4, 0.3), "#FF000000", 0.004);
        var items = ShapeRenderList.From([], [shape]);

        // Slot space: the rectangle's top edge is at y = 0.1 * 800 = 80.
        var p = new ViewportProjection(Zoom: 1.5, DeviceScale: dpi, OriginXDips: -40, OriginYDips: -60);
        var (_, expectedTopPx) = p.SlotToDevice(0, 80);
        var (expectedLeftPx, _) = p.SlotToDevice(80, 0);

        int w = 1200, h = 900;
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            ShapeSkiaPainter.PaintViewport(
                canvas, items, 800, _ => 0, _ => PageTransform.For(1, 1, 0, 1), p);
        }

        // The stroke's CENTRE down a column that crosses the top edge but
        // misses the left edge.
        //
        // Centre rather than first inked row, deliberately. The first row is a
        // discrete index and the edge is a continuous position, so comparing
        // them conflates the two; and Skia quantises stroke WIDTH to half a
        // device pixel, which moves both edges. The midpoint of the inked span
        // is unaffected by width and is what "is it in the right place" means.
        // Scanned in a window around the top edge only. A full-height column
        // also crosses the BOTTOM edge, and the midpoint of the two is the
        // centre of the rectangle rather than of a stroke.
        int probeX = (int)Math.Round(expectedLeftPx + (60 * p.Zoom * dpi));
        int from = Math.Max(0, (int)expectedTopPx - 15);
        int to = Math.Min(h - 1, (int)expectedTopPx + 15);

        int first = -1, last = -1;
        for (int y = from; y <= to; y++)
        {
            if (bitmap.GetPixel(probeX, y).Red < 250)
            {
                if (first < 0) { first = y; }
                last = y;
            }
        }

        Assert.True(first >= 0, "the rectangle did not rasterise at all");

        // +0.5 because a row index names a pixel whose centre is half a pixel
        // further down than its top edge.
        double inkCentre = ((first + last) / 2.0) + 0.5;
        Assert.Equal(expectedTopPx, inkCentre, tolerance: 0.75);
    }

    // ---------------- zoom x dpi x rotation ----------------
    //
    // Every test above holds the page transform at identity, so zoom and DPI
    // have never been exercised together with a turned page. Those are three
    // independent scales on one journey (the page's own, the scroller's, the
    // display's) and the way they go wrong is by being applied in the wrong
    // space, which no single-scale test can see.
    //
    // A compact fixture on purpose: at zoom 1.75 and DPI 2.0 a full-size shape
    // would need a 2000-pixel surface per cell, and there are thirty-six cells.

    private const double MatrixScale = 800;
    private const double MatrixContentH = 1000;
    private const double MatrixStroke = 0.006;
    private const int ZoomSurface = 800;

    private static readonly ShapeDraft SmallRect = new(ShapeKind.Rectangle, 0.1, 0.1, 0.3, 0.25);

    public static TheoryData<double, double, int> ZoomDpiRotation()
    {
        var cells = new TheoryData<double, double, int>();
        foreach (double zoom in new[] { 0.75, 1.0, 1.75 })
        {
            foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
            {
                foreach (int rotation in new[] { 0, 90, 180, 270 })
                {
                    cells.Add(zoom, dpi, rotation);
                }
            }
        }

        return cells;
    }

    /// <summary>The mark's slot-space box, from the shared projection alone.</summary>
    private static (double L, double T, double R, double B) SlotBounds(PageTransform view)
    {
        var item = ShapeRenderList.From([], [
            new ShapeAnnotation(0, SmallRect, "#FF000000", MatrixStroke)])[0];

        double reach = OverlayProjection.WidthOf(item, MatrixScale, view) / 2;
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;

        foreach (var p in item.Points)
        {
            var (x, y) = OverlayProjection.ToSlot(p, MatrixScale, 0, view);
            l = Math.Min(l, x - reach); t = Math.Min(t, y - reach);
            r = Math.Max(r, x + reach); b = Math.Max(b, y + reach);
        }

        return (l, t, r, b);
    }

    /// <summary>
    /// A viewport that brings the mark to a known spot near the surface's
    /// corner, whatever the zoom and DPI, so one modest bitmap serves every
    /// cell and nothing is measured while half off the edge.
    /// </summary>
    private static ViewportProjection Framing(
        double zoom, double dpi, (double L, double T, double R, double B) slot)
    {
        const double MarginPx = 40;

        return new ViewportProjection(
            zoom, dpi,
            (MarginPx / dpi) - (slot.L * zoom),
            (MarginPx / dpi) - (slot.T * zoom));
    }

    private static (int L, int T, int R, int B) PaintAndMeasure(
        double zoom, double dpi, int rotation)
    {
        var view = PageTransform.For(MatrixScale, MatrixContentH, rotation, MatrixScale);
        var items = ShapeRenderList.From([], [
            new ShapeAnnotation(0, SmallRect, "#FF000000", MatrixStroke)]);

        using var bitmap = new SKBitmap(
            ZoomSurface, ZoomSurface, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            ShapeSkiaPainter.PaintViewport(
                canvas, items, MatrixScale, _ => 0, _ => view,
                Framing(zoom, dpi, SlotBounds(view)));
        }

        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int y = 0; y < ZoomSurface; y++)
        {
            for (int x = 0; x < ZoomSurface; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red == 255 && c.Green == 255 && c.Blue == 255)
                {
                    continue;
                }

                l = Math.Min(l, x); t = Math.Min(t, y);
                r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        Assert.True(l <= r, $"nothing rasterised at zoom {zoom}, dpi {dpi}, rotation {rotation}");
        return (l, t, r, b);
    }

    [Theory]
    [MemberData(nameof(ZoomDpiRotation))]
    public void a_turned_page_rasterises_where_zoom_and_dpi_together_say(
        double zoom, double dpi, int rotation)
    {
        // The whole chain in one assertion: normalized, through the page's
        // turn, through the scroller's zoom and the display's scale, onto
        // pixels, against arithmetic that has been near neither renderer.
        var view = PageTransform.For(MatrixScale, MatrixContentH, rotation, MatrixScale);
        var slot = SlotBounds(view);
        var p = Framing(zoom, dpi, slot);

        var (wantL, wantT) = p.SlotToDevice(slot.L, slot.T);
        var (wantR, wantB) = p.SlotToDevice(slot.R, slot.B);

        var (l, t, r, b) = PaintAndMeasure(zoom, dpi, rotation);

        // Device pixels, so the slack does not grow with the scale: an edge is
        // spread about a pixel by antialiasing and moved up to a quarter by the
        // stroker's snap, whatever the zoom.
        Assert.InRange(l, wantL - 2.5, wantL + 2.5);
        Assert.InRange(t, wantT - 2.5, wantT + 2.5);
        Assert.InRange(r, wantR - 2.5, wantR + 2.5);
        Assert.InRange(b, wantB - 2.5, wantB + 2.5);
    }

    [Theory]
    [MemberData(nameof(ZoomDpiRotation))]
    public void the_marks_size_is_its_slot_size_times_zoom_times_dpi(
        double zoom, double dpi, int rotation)
    {
        // Position and SIZE are separate failures. A mark can be anchored
        // correctly and drawn at the wrong scale, which is what applying zoom
        // in the wrong space does, and the position check above would still
        // pass on its left and top edges.
        var view = PageTransform.For(MatrixScale, MatrixContentH, rotation, MatrixScale);
        var slot = SlotBounds(view);

        var (l, t, r, b) = PaintAndMeasure(zoom, dpi, rotation);

        Assert.Equal((slot.R - slot.L) * zoom * dpi, r - l, tolerance: 3.0);
        Assert.Equal((slot.B - slot.T) * zoom * dpi, b - t, tolerance: 3.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void doubling_the_zoom_doubles_the_mark(int rotation)
    {
        // A ratio, which needs no arithmetic expectation at all: whatever the
        // projection says, twice the zoom must be twice the mark. A scale
        // dropped on the floor gives 1.0 here and a scale applied twice 4.0.
        var (l1, t1, r1, b1) = PaintAndMeasure(0.75, 1.5, rotation);
        var (l2, t2, r2, b2) = PaintAndMeasure(1.5, 1.5, rotation);

        Assert.Equal(2.0, (double)(r2 - l2) / (r1 - l1), tolerance: 0.05);
        Assert.Equal(2.0, (double)(b2 - t2) / (b1 - t1), tolerance: 0.05);
    }

    [Theory]
    [MemberData(nameof(Dpis))]
    public void the_composition_order_is_not_interchangeable(double dpi)
    {
        // Guards the mistake this file exists for. With a non-zero origin the
        // right order and the wrong one must disagree; if they ever agree, the
        // test above has stopped proving anything.
        var p = Busy(dpi);

        var right = ShapeSkiaPainter.MatrixFor(p);
        var wrong = SKMatrix.CreateScale((float)p.Zoom, (float)p.Zoom)
            .PreConcat(SKMatrix.CreateTranslation((float)p.OriginXDips, (float)p.OriginYDips))
            .PreConcat(SKMatrix.CreateScale((float)p.DeviceScale, (float)p.DeviceScale));

        var a = right.MapPoint(640, 12345);
        var b = wrong.MapPoint(640, 12345);

        Assert.True(Math.Abs(a.X - b.X) > 0.5 || Math.Abs(a.Y - b.Y) > 0.5,
                    $"the two orders agreed at dpi {dpi}: {a} vs {b}");
    }
}
