using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Pure zoom/pan/rubber-band math for the page viewport. No WinUI types —
/// unit-testable without a UI thread. PdfEditorApp's ViewportViewModel owns
/// one of these and drives a CompositeTransform from <see cref="Scale"/>,
/// <see cref="PanX"/>, <see cref="PanY"/> each frame.
///
/// Coordinate convention: PanX/PanY are the on-screen position (in viewport
/// pixels) of the content's top-left corner; the content is ContentWidth *
/// Scale pixels wide on screen. All values are in viewport-local pixels.
/// </summary>
public sealed class PanZoomState
{
    public const double MinScale = 0.1;
    public const double MaxScale = 8.0;

    /// <summary>Higher = stiffer resistance once you drag past the content edge.</summary>
    private const double RubberBandConstant = 120.0;

    public double Scale { get; private set; } = 1.0;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>Unclamped drag target; PanX/PanY are its rubber-banded projection.</summary>
    private double _rawPanX;
    private double _rawPanY;

    public double ScaledContentWidth => ContentWidth * Scale;
    public double ScaledContentHeight => ContentHeight * Scale;

    /// <summary>
    /// The scale at which the content exactly fits the viewport width. This
    /// is the reference point the UI calls "100%", NOT a raw Scale of 1.0:
    /// once pages are rendered at device pixels, a bitmap on a 150% display
    /// is 1.5x wider than its on-screen size, so Scale 1.0 would show it
    /// overflowing by half the window.
    ///
    /// DERIVED, never stored. It was previously a field refreshed inside
    /// ResetView/ReplaceContentPreservingView, which meant it went stale the
    /// moment the window resized (nothing recomputed it) and drifted every
    /// time a re-render landed at a different pixel width. Computing it makes
    /// staleness structurally impossible.
    /// </summary>
    public double FitScale => ContentWidth > 0 && ViewportWidth > 0
        ? Math.Clamp(ViewportWidth / ContentWidth, MinScale, MaxScale)
        : 1.0;

    /// <summary>
    /// Zoom relative to fit-width, so 100 always means "the page spans the
    /// viewport". Reduces to ScaledContentWidth / ViewportWidth, i.e. exactly
    /// the fraction of the viewport the page covers, whatever pixel size it
    /// happens to be rendered at.
    /// </summary>
    public double ZoomRelativeToFit => FitScale > 0 ? Scale / FitScale : 1.0;

    /// <summary>True when the content is already fitted to the viewport width (within rounding).</summary>
    public bool IsAtFitWidth => Math.Abs(ZoomRelativeToFit - 1.0) < 0.01;

    /// <summary>Scales the content to exactly fit the viewport width, keeping it centered.</summary>
    public void FitToWidth()
    {
        Scale = FitScale;
        _rawPanX = CenteredOffset(ScaledContentWidth, ViewportWidth);
        _rawPanY = CenteredOffset(ScaledContentHeight, ViewportHeight);
        RecomputeClamped();
    }

    /// <summary>Sets content size and fits it to the viewport width, centered (a freshly opened/replaced page).</summary>
    public void ResetView(double contentWidth, double contentHeight)
    {
        ContentWidth = contentWidth;
        ContentHeight = contentHeight;
        Scale = FitScale;

        _rawPanX = CenteredOffset(ScaledContentWidth, ViewportWidth);
        _rawPanY = CenteredOffset(ScaledContentHeight, ViewportHeight);
        RecomputeClamped();
    }

    /// <summary>
    /// Swaps in a freshly re-rendered bitmap at a new pixel size (the
    /// debounced high-res re-render landing) without any visual jump: Scale
    /// is adjusted so the content's on-screen footprint — and PanX/PanY —
    /// stay exactly where they were.
    /// </summary>
    public void ReplaceContentPreservingView(double newContentWidth, double newContentHeight)
    {
        double onScreenWidth = ScaledContentWidth;
        ContentWidth = newContentWidth;
        ContentHeight = newContentHeight;
        Scale = newContentWidth > 0 ? onScreenWidth / newContentWidth : 1.0;

        // FitScale needs no adjustment: it is derived from ContentWidth and
        // ViewportWidth, both current by this point.
        RecomputeClamped();
    }

    /// <summary>Multiplies Scale by <paramref name="multiplicativeFactor"/>, keeping the content point under (cursorX, cursorY) fixed on screen.</summary>
    public void ZoomAtPoint(double multiplicativeFactor, double cursorX, double cursorY)
    {
        if (ContentWidth <= 0 || ContentHeight <= 0)
        {
            return;
        }

        double newScale = Math.Clamp(Scale * multiplicativeFactor, MinScale, MaxScale);
        double applied = newScale / Scale;
        if (applied == 1.0)
        {
            return;
        }

        _rawPanX = cursorX - (cursorX - _rawPanX) * applied;
        _rawPanY = cursorY - (cursorY - _rawPanY) * applied;
        Scale = newScale;
        RecomputeClamped();
    }

    /// <summary>Applies a raw drag delta; PanX/PanY reflect rubber-band resistance if this pushes past the content edge.</summary>
    public void PanBy(double dx, double dy)
    {
        _rawPanX += dx;
        _rawPanY += dy;
        RecomputeClamped();
    }

    /// <summary>
    /// Call once a drag/momentum/spring-back sequence has fully settled, so
    /// the next drag starts from the visually-displayed (possibly
    /// rubber-banded) position instead of the stretched-out raw one.
    /// </summary>
    public void SnapRawToDisplayed()
    {
        _rawPanX = PanX;
        _rawPanY = PanY;
    }

    /// <summary>
    /// If the raw (unclamped) position is out of bounds, returns the point
    /// it should spring back to and true; otherwise returns the current
    /// position and false. Used to drive a spring-back animation once a drag
    /// or momentum phase ends out of bounds.
    /// </summary>
    public bool TryGetSpringBackTarget(out double targetX, out double targetY)
    {
        targetX = ClampAxis(_rawPanX, ScaledContentWidth, ViewportWidth);
        targetY = ClampAxis(_rawPanY, ScaledContentHeight, ViewportHeight);
        return Math.Abs(targetX - _rawPanX) > 0.5 || Math.Abs(targetY - _rawPanY) > 0.5;
    }

    /// <summary>Forces PanX/PanY/raw accumulators directly to a point (used by the spring-back animation's per-tick update).</summary>
    public void SetPanDirect(double x, double y)
    {
        _rawPanX = x;
        _rawPanY = y;
        PanX = x;
        PanY = y;
    }

    private void RecomputeClamped()
    {
        PanX = RubberBand(_rawPanX, ScaledContentWidth, ViewportWidth);
        PanY = RubberBand(_rawPanY, ScaledContentHeight, ViewportHeight);
    }

    private static double CenteredOffset(double scaledContent, double viewport) =>
        scaledContent <= viewport ? (viewport - scaledContent) / 2 : 0;

    private static double ClampAxis(double raw, double scaledContent, double viewport)
    {
        if (scaledContent <= viewport)
        {
            return (viewport - scaledContent) / 2;
        }

        double min = viewport - scaledContent;
        return Math.Clamp(raw, min, 0);
    }

    private static double RubberBand(double raw, double scaledContent, double viewport)
    {
        double clamped = ClampAxis(raw, scaledContent, viewport);
        double overshoot = raw - clamped;
        if (overshoot == 0)
        {
            return clamped;
        }

        double resisted = overshoot * RubberBandConstant / (Math.Abs(overshoot) + RubberBandConstant);
        return clamped + resisted;
    }
}
