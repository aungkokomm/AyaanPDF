using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decides what resolution each tier renders at, and when a page is worth
/// re-rendering.
///
/// Two tiers. The BASE tier is a modest, cached render that exists for every
/// page in the keep window; it is what shows instantly while scrolling and
/// what a page falls back to when it stops being near the viewport. The SHARP
/// tier is an uncached, DPI- and zoom-aware render for the handful of pages
/// actually on screen, so text stays crisp at deep zoom without the base tier
/// having to be enormous.
///
/// Every limit here exists because a page bitmap is width * height * 4 bytes
/// and grows with the SQUARE of the render width: at 4000px wide an A4 page is
/// about 82MB. A width cap alone is not enough, because a tall or panoramic
/// page blows the same budget at a legal width, so the area cap is what
/// actually bounds memory.
/// </summary>
public sealed class RenderBudget
{
    /// <summary>Width of the cached base render, in pixels.</summary>
    public int BaseWidth { get; }

    /// <summary>Hard ceiling on a sharp render's width, in pixels.</summary>
    public int MaxSharpWidth { get; }

    /// <summary>
    /// Ceiling on a sharp render's total pixels. Binds tall pages, which a
    /// width cap alone would let through at ruinous heights.
    /// </summary>
    public long MaxSharpPixels { get; }

    /// <summary>
    /// How much sharper a candidate must be before it justifies a re-render.
    /// Without this margin a slow zoom would re-rasterize on every frame for a
    /// difference nobody can see.
    ///
    /// Kept at 1.2 because of the single most common case: a 150% display at
    /// zoom 1.0 wants 1.5x the base width, and after the base tier's own
    /// headroom that lands around a 1.33x gain. A higher threshold blocks it,
    /// so a high-DPI screen would sit on the soft base render at the default
    /// zoom, which is exactly when the page is most looked at. 1.2 still
    /// leaves plenty of hysteresis: each sharpen raises the bar, so a slow
    /// zoom needs another 20% before it re-renders again.
    /// </summary>
    public double ResharpenRatio { get; }

    /// <summary>
    /// Above this zoom, only the page you are looking at is sharpened.
    ///
    /// At a normal zoom several pages are on screen and all deserve to be
    /// crisp. Zoomed deep, one page fills the viewport, so spending the budget
    /// on its neighbours buys nothing visible and forces the per-page cap down
    /// to keep total memory sane. Concentrating on one page is what allows the
    /// caps below to be as high as they are.
    /// </summary>
    public const double SoloSharpenZoom = 2.0;

    /// <summary>
    /// How far a whole-page render may be upscaled before tiles take over.
    ///
    /// Purely a dead zone around the switch: without it, a zoom parked exactly
    /// on the boundary would flip between the two paths. A tenth of a pixel per
    /// pixel is not visible; the flicker would be.
    /// </summary>
    private const double TileSwitchSlack = 1.1;

    /// <summary>
    /// Ceiling on a single region render's width, the primitive tiles are
    /// built on. Generous, because a region is viewport-sized rather than
    /// page-sized: this bounds a pathological request, not normal use.
    /// </summary>
    public const int MaxRegionWidth = 6000;

    /// <summary>
    /// Whether this page has outgrown a single bitmap and should be drawn from
    /// the tile pyramid instead.
    ///
    /// The test is not a zoom threshold but the caps themselves: a whole-page
    /// render is pixel-exact right up until <see cref="SharpWidthFor"/> has to
    /// clamp it, and below that point one render is both exact and far cheaper
    /// than a grid of them. Past it, no amount of spending makes the page
    /// sharper, because the result is being upscaled to fit the screen. That is
    /// the soft text every viewer shows at high magnification, and it is the
    /// moment to switch to tiles, whose cost is about one viewport at any zoom
    /// and which stay exact however far in the user goes.
    ///
    /// Deriving it this way also means it tracks the caps: raise
    /// <see cref="MaxSharpWidth"/> and the switch moves out on its own.
    /// </summary>
    public bool NeedsTiles(double slotWidth, double zoom, double rasterizationScale, double aspect)
    {
        if (slotWidth <= 0)
        {
            return false;
        }

        double scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        double ideal = slotWidth * Math.Max(0.01, zoom) * scale;
        int capped = SharpWidthFor(slotWidth, zoom, rasterizationScale, aspect);

        return ideal > capped * TileSwitchSlack;
    }

    public RenderBudget(
        int baseWidth = 900,
        int maxSharpWidth = 4200,
        long maxSharpPixels = 26_000_000,
        double resharpenRatio = 1.2)
    {
        BaseWidth = baseWidth;
        MaxSharpWidth = maxSharpWidth;
        MaxSharpPixels = maxSharpPixels;
        ResharpenRatio = resharpenRatio;
    }

    /// <summary>
    /// The pixel width to rasterize a page at so it is pixel-exact on screen:
    /// its on-screen size in DIPs, times the display scale.
    /// </summary>
    /// <param name="slotWidth">Page width in slot space (unzoomed DIPs).</param>
    /// <param name="zoom">Current viewport zoom factor.</param>
    /// <param name="rasterizationScale">Display scale, e.g. 1.5 at 150%.</param>
    /// <param name="aspect">Page height divided by width, for the area cap.</param>
    public int SharpWidthFor(double slotWidth, double zoom, double rasterizationScale, double aspect)
    {
        if (slotWidth <= 0)
        {
            return BaseWidth;
        }

        double scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        double ideal = slotWidth * Math.Max(0.01, zoom) * scale;

        // Never ask for less than the base tier: a sharp pass should only ever
        // improve on what is already there.
        double width = Math.Max(BaseWidth, ideal);
        width = Math.Min(width, MaxSharpWidth);

        // Area cap. Height is width * aspect, so the widest legal width is
        // sqrt(MaxSharpPixels / aspect).
        double safeAspect = aspect > 0 ? aspect : 1.0;
        double areaLimited = Math.Sqrt(MaxSharpPixels / safeAspect);
        width = Math.Min(width, areaLimited);

        // Floor, not round: when the AREA cap is what binds, rounding up can
        // push the result back over the budget it was just clamped to. The
        // sub-pixel difference is invisible; exceeding the cap is not.
        return (int)Math.Max(1, Math.Floor(width));
    }

    /// <summary>
    /// Whether a page rendered at <paramref name="currentWidth"/> should be
    /// re-rendered at <paramref name="desiredWidth"/>. Only upward moves
    /// qualify: letting a page render DOWN as you zoom out would throw away a
    /// good bitmap for a worse one and flicker on the way back in.
    /// </summary>
    public bool ShouldResharpen(int currentWidth, int desiredWidth)
    {
        if (currentWidth <= 0)
        {
            return true;
        }

        return (double)desiredWidth / currentWidth >= ResharpenRatio;
    }

    /// <summary>
    /// Clamps an inclusive page range widened by <paramref name="margin"/> to
    /// the document's bounds. Returns (-1, -1) when the input range is empty.
    /// </summary>
    public static (int From, int To) Widen(int first, int last, int margin, int pageCount)
    {
        if (first < 0 || last < 0 || pageCount <= 0)
        {
            return (-1, -1);
        }

        return (Math.Max(0, first - margin), Math.Min(pageCount - 1, last + margin));
    }
}
