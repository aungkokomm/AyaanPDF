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
