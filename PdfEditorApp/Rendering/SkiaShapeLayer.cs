using System;
using System.Collections.Generic;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using SkiaSharp.Views.Windows;

namespace PdfEditorApp.Rendering;

/// <summary>
/// The XAML host for the candidate shape renderer.
///
/// It owns no geometry and no state beyond the frame it was last handed. The
/// view model is not read here, nothing is computed here, and the painting
/// itself lives in PdfEditorApp.Rendering.Skia, which has no WinUI in it. This
/// class exists to put a Skia surface in the visual tree and hand it a list.
///
/// SIZE IS DELIBERATELY BOUNDED. The XAML ink overlay is a Canvas spanning the
/// whole document stack, which costs nothing because a Canvas allocates no
/// pixels for its own size. An SKXamlCanvas is the opposite: its surface IS its
/// size, so a stack-sized one on a long document would try to allocate hundreds
/// of megabytes every frame. Stage 2 therefore sizes this to one page to prove
/// the host works. Anchoring a viewport-sized surface to the scroll offset is
/// the real design and is a later stage.
/// </summary>
internal sealed partial class SkiaShapeLayer : SKXamlCanvas
{
    private IReadOnlyList<ShapeRenderItem> _items = [];
    private double _scale;
    private Func<int, double> _pageTop = _ => 0;

    public SkiaShapeLayer()
    {
        // Never eats a gesture, exactly like the ink overlay it stands in for.
        IsHitTestVisible = false;

        // The surface stays at DEVICE resolution and the DPI scale is applied
        // in OnPaintSurface, so the transform chain here is the same one the
        // off-screen parity tests exercise. Letting the control pre-scale would
        // put a second, invisible transform between the projection and the
        // pixels.
        IgnorePixelScaling = false;
    }

    /// <summary>Hands over one frame and asks for a repaint.</summary>
    public void Show(IReadOnlyList<ShapeRenderItem> items, double scale, Func<int, double> pageTop)
    {
        _items = items;
        _scale = scale;
        _pageTop = pageTop;
        Invalidate();
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;

        // Transparent, not a colour: this sits over the rendered page and must
        // not paint out what PDFium drew.
        canvas.Clear(SKColors.Transparent);

        if (_items.Count == 0 || _scale <= 0)
        {
            return;
        }

        canvas.Save();
        canvas.Scale((float)DeviceScale(e.Info.Width));
        ShapeSkiaPainter.Paint(canvas, _items, _scale, _pageTop);
        canvas.Restore();
    }

    /// <summary>
    /// Device pixels per DIP, taken from the surface actually handed over
    /// rather than from a DPI reported elsewhere, so the scale and the bitmap
    /// can never disagree by a rounding.
    /// </summary>
    private double DeviceScale(int surfaceWidthPx)
    {
        if (ActualWidth > 0)
        {
            return surfaceWidthPx / ActualWidth;
        }

        return XamlRoot?.RasterizationScale ?? 1.0;
    }
}
