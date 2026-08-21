using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;

namespace ShadowLab;

/// <summary>How a scene's shadow is produced.</summary>
public enum Approach
{
    /// <summary>The shipping renderer, called directly.</summary>
    Current,

    /// <summary>SKImageFilter.CreateDropShadow on the object's layer.</summary>
    ImageFilter,

    /// <summary>SKImageFilter.CreateDropShadowOnly, painted under the object.</summary>
    ImageFilterOnly,

    /// <summary>The same filter with no layer bounds, to price them.</summary>
    Unbounded,

    /// <summary>No shadow at all, as a control.</summary>
    None,
}

/// <summary>
/// The three ways of getting a shadow out of the same Ayaan objects.
///
/// Every approach is handed the SAME render items the app builds, so the only
/// variable is how the shadow is made. The geometry, the projection and the
/// marks are the production ones throughout.
/// </summary>
public static class Casters
{
    public const double Scale = 800;
    public const double ContentH = 1000;

    private static readonly ViewportProjection Plain = new(1, 1, 0, 0);
    private static readonly PageTransform View = PageTransform.For(Scale, ContentH, 0, Scale);

    public static void Paint(
        SKCanvas canvas, IReadOnlyList<ShapeRenderItem> items, Approach approach)
    {
        switch (approach)
        {
            case Approach.Current:
                ShapeSkiaPainter.PaintViewport(canvas, items, Scale, _ => 0, _ => View, Plain);
                break;

            case Approach.None:
                ShapeSkiaPainter.PaintViewport(
                    canvas, Stripped(items), Scale, _ => 0, _ => View, Plain);
                break;

            case Approach.ImageFilter:
                PaintThroughFilter(canvas, items, only: false);
                break;

            case Approach.Unbounded:
                PaintThroughFilter(canvas, items, only: false, bounded: false);
                break;

            case Approach.ImageFilterOnly:
                PaintThroughFilter(canvas, items, only: true);
                break;
        }
    }

    /// <summary>The same objects with their effects removed.</summary>
    private static IReadOnlyList<ShapeRenderItem> Stripped(IReadOnlyList<ShapeRenderItem> items) =>
        items.Select(i => i with { Effects = null }).ToList();

    /// <summary>
    /// The candidate. Each OBJECT is drawn into a layer whose paint carries a
    /// drop-shadow image filter, so Skia derives the shadow from the object's
    /// own alpha: no silhouette to build, no offset copy to draw, no blur to
    /// implement, and multi-part objects are unioned before the filter runs
    /// because the whole object is inside one layer.
    ///
    /// CreateDropShadow draws the shadow AND the object. CreateDropShadowOnly
    /// draws the shadow alone, which needs the object painted again on top, and
    /// is the version that can put a shadow under something it must not tint.
    /// </summary>
    private static void PaintThroughFilter(
        SKCanvas canvas, IReadOnlyList<ShapeRenderItem> items, bool only, bool bounded = true)
    {
        int at = 0;
        while (at < items.Count)
        {
            int end = at + 1;
            if (items[at].ObjectId != Guid.Empty)
            {
                while (end < items.Count && items[end].ObjectId == items[at].ObjectId)
                {
                    end++;
                }
            }

            var group = items.Skip(at).Take(end - at).ToList();
            var shadow = items[at].Effects?.Shadow;

            if (shadow is null)
            {
                ShapeSkiaPainter.PaintViewport(canvas, group, Scale, _ => 0, _ => View, Plain);
            }
            else
            {
                PaintOneObject(canvas, group, shadow.Value, only, bounded);
            }

            at = end;
        }
    }

    private static void PaintOneObject(
        SKCanvas canvas, IReadOnlyList<ShapeRenderItem> group, DropShadow shadow, bool only,
        bool bounded)
    {
        // The shadow's lengths are normalized to the page width, the same as
        // every other length in the model, so they are projected exactly the
        // way a stroke width is.
        float dx = (float)(shadow.OffsetX * Scale);
        float dy = (float)(shadow.OffsetY * Scale);
        float sigma = (float)(OverlayProjection.BlurSigmaOf(shadow, Scale, View));

        var color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A);

        using var filter = only
            ? SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color)
            : SKImageFilter.CreateDropShadow(dx, dy, sigma, sigma, color);

        using var paint = new SKPaint { ImageFilter = filter };

        // BOUNDED, by the production rule: OverlayProjection already knows how
        // far a blur reaches. An unbounded SaveLayer lets Skia allocate a layer
        // the size of the whole canvas for every object on every frame, which
        // is measured separately in the benchmark rather than assumed.
        if (bounded)
        {
            canvas.SaveLayer(LayerBounds(group), paint);
        }
        else
        {
            canvas.SaveLayer(paint);
        }
        ShapeSkiaPainter.PaintViewport(
            canvas, Strip(group), Scale, _ => 0, _ => View, Plain);
        canvas.Restore();

        if (only)
        {
            ShapeSkiaPainter.PaintViewport(
                canvas, Strip(group), Scale, _ => 0, _ => View, Plain);
        }
    }

    private static IReadOnlyList<ShapeRenderItem> Strip(IReadOnlyList<ShapeRenderItem> group) =>
        group.Select(i => i with { Effects = null }).ToList();

    /// <summary>Room for the object and everything its shadow can reach.</summary>
    private static SKRect LayerBounds(IReadOnlyList<ShapeRenderItem> group)
    {
        double l = double.MaxValue, t = double.MaxValue;
        double r = double.MinValue, b = double.MinValue;

        foreach (var item in group)
        {
            var (il, it, ir, ib) = OverlayProjection.SlotBoundsOf(item, Scale, 0, View);
            l = Math.Min(l, il);
            t = Math.Min(t, it);
            r = Math.Max(r, ir);
            b = Math.Max(b, ib);
        }

        return new SKRect((float)l, (float)t, (float)r, (float)b);
    }
}
