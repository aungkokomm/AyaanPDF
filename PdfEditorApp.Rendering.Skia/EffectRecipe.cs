using PdfEditorApp.Viewport;
using SkiaSharp;

namespace PdfEditorApp.Rendering.Skia;

/// <summary>
/// One effect spec, as an SKImageFilter.
///
/// THE ONE PLACE AN EFFECT IS DEFINED. Every effect worth having is a
/// composition of filters Skia already ships: a drop shadow is
/// CreateDropShadowOnly, a glow is the same with nowhere to fall, a spread is
/// CreateDilate in front of the blur, an erode is CreateErode, a tint is
/// CreateColorFilter, and two at once is CreateMerge. So the renderer's job is
/// to name the primitive and hand it the numbers, not to implement an
/// image-processing algorithm. Nothing here does arithmetic on pixels.
///
/// BOTH RENDERERS COME HERE. The preview paints the object into a bounded layer
/// with this filter on it; the committed picture is rasterised into a bitmap
/// with the same filter and put in the annotation underneath the shape. That is
/// what makes the preview and the saved page the same picture, and it is only
/// true while there is one function producing the filter.
///
/// THE PROJECTION IS APPLIED HERE, once, for the same reason. An effect's offset
/// is a PAGE-LOCAL vector, so on a turned page it has to go where the page says
/// down-and-right is; the page's turn lives in
/// <see cref="OverlayProjection"/> rather than in a canvas matrix, and scaling
/// the offset's components instead left the shadow pointing the wrong way at
/// 90, 180 and 270. A caller that has to remember to do that is a caller that
/// will not.
/// </summary>
public static class EffectRecipe
{
    /// <summary>
    /// The filter for one effect, or null when this build has nothing to draw
    /// it with.
    ///
    /// NULL RATHER THAN AN EXCEPTION for a kind it does not know: a shape
    /// carrying an effect from a later build has to open, with the parts that
    /// are understood still drawn. The same rule the tag already follows for a
    /// key it does not recognise.
    /// </summary>
    /// <param name="scale">The overlay scale, the fixed content-box width.</param>
    /// <param name="view">How the page the object is on is turned.</param>
    public static SKImageFilter? FilterFor(EffectSpec spec, double scale, PageTransform view)
    {
        // Measured as the difference between two projected points, so the zoom,
        // the display scale and the turn all reach it through exactly the code
        // every mark already goes through. The page's top cancels, which is why
        // it is not asked for.
        var origin = OverlayProjection.ToSlot((0, 0), scale, 0, view);
        var thrown = OverlayProjection.ToSlot((spec.OffsetX, spec.OffsetY), scale, 0, view);

        float dx = (float)(thrown.X - origin.X);
        float dy = (float)(thrown.Y - origin.Y);

        // Sigma is a length and has no direction, so it takes the ordinary
        // rule. It rides the canvas matrix, so the blur scales with the zoom.
        float sigma = (float)OverlayProjection.BlurSigmaOf(spec, scale, view);

        var color = new SKColor(spec.Color.R, spec.Color.G, spec.Color.B, spec.Color.A);

        return spec.Kind switch
        {
            // CreateDropShadowOnly rather than CreateDropShadow: the object is
            // painted again afterwards through the ordinary path, so it keeps
            // its own colours and its own compositing, and the filter is left
            // doing only the part that is hard. Measured in the prototype at
            // about forty per cent cheaper than letting the filter draw the
            // object too.
            EffectKind.DropShadow => SKImageFilter.CreateDropShadowOnly(dx, dy, sigma, sigma, color),
            _ => null,
        };
    }
}
