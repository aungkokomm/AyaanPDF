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
/// SIZE IS BOUNDED BY THE VIEWPORT. The XAML ink overlay is a Canvas spanning
/// the whole document stack, which costs nothing because a Canvas allocates no
/// pixels for its own size, and the scroller's compositor zoom scales its
/// vector children while keeping them crisp. A Skia surface is the opposite on
/// both counts: it allocates every pixel it covers, and it rasterises once, so
/// a stack-sized one would allocate the whole document and one inside the
/// scroller would be magnified into blur as you zoom in.
///
/// So this sits OUTSIDE the scroller, covers only the visible viewport, and
/// receives zoom and scroll as numbers in a <see cref="ViewportProjection"/>
/// rather than as a compositor transform. Rasterising at final device
/// resolution every frame is what keeps it sharp at any zoom.
///
/// NO VIEW ROTATION, on purpose. The XAML overlay does not rotate with the
/// view: the rotation transform is bound inside each page card, and the ink
/// layer is a sibling of the page stack that nothing ever turns. Matching that
/// is the only option that keeps rendering the single variable. Making Skia
/// rotate would be a behaviour change and would also make it disagree with the
/// renderer it is being measured against. Left undecided rather than called
/// passed or failed.
/// </summary>
internal sealed partial class SkiaShapeLayer : SKXamlCanvas
{
    private IReadOnlyList<ShapeRenderItem> _items = [];
    private double _scale;
    private Func<int, double> _pageTop = _ => 0;
    private ViewportProjection _projection = new(1, 1, 0, 0);

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

    /// <summary>How far outside the surface a mark is still worth drawing.</summary>
    private const double CullPadSlotDips = 64;

    /// <summary>Hands over one frame and asks for a repaint.</summary>
    public void Show(
        IReadOnlyList<ShapeRenderItem> items,
        double scale,
        Func<int, double> pageTop,
        ViewportProjection projection)
    {
        _items = items;
        _scale = scale;
        _pageTop = pageTop;
        _projection = projection;
        Invalidate();
    }

    /// <summary>How many of the last frame's items survived culling.</summary>
    public int LastDrawnCount { get; private set; }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;

        // Transparent, not a colour: this sits over the rendered page and must
        // not paint out what PDFium drew.
        canvas.Clear(SKColors.Transparent);
        LastDrawnCount = 0;

        if (_items.Count == 0 || _scale <= 0)
        {
            return;
        }

        var visible = ShapeCulling.Visible(
            _items,
            _projection.VisibleSlotBounds(e.Info.Width, e.Info.Height, CullPadSlotDips),
            _scale,
            _pageTop);

        LastDrawnCount = visible.Count;
        if (visible.Count == 0)
        {
            return;
        }

        // The matrix and the painting both live in the rendering project, where
        // they are covered by tests this host cannot be. Nothing is computed
        // here.
        ShapeSkiaPainter.PaintViewport(canvas, visible, _scale, _pageTop, _projection);
    }
}
