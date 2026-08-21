using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using SkiaSharp;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// One recipe turns a spec into an SKImageFilter, and one rule says how far its
/// ink reaches.
///
/// THE RECIPE. Every effect worth having is a composition of filters Skia
/// already ships: a drop shadow is CreateDropShadowOnly, a glow is the same with
/// nowhere to fall, a spread is CreateDilate, and so on. So the renderer's job
/// is to name the primitive, not to implement one. An effect this build has
/// never heard of draws nothing rather than throwing, which is the same rule the
/// tag already follows for a key it does not know: a file written by a later
/// build opens, minus the part that is not understood.
///
/// THE REACH. Blurred ink lands well outside the geometry that produced it, and
/// three places have to agree about how far: the preview's layer and dirty
/// region, the box the committed picture is cropped to, and the rectangle
/// render_core reserves in the annotation. They agreed before by each carrying
/// the same arithmetic, which is agreement until somebody edits one of them.
/// Here it is one number the others are derived from.
/// </summary>
public class EffectRecipeTests
{
    private const double Scale = 800;

    private static readonly PageTransform View = PageTransform.For(Scale, 1000, 0, Scale);
    private static readonly RenderColor Black = new(0xFF, 0, 0, 0);

    private static EffectSpec Shadow(double blur, double distance = 0.05) =>
        new(EffectKind.DropShadow, Black, Blur: blur, Distance: distance, AngleDeg: 135);

    private static ShapeRenderItem Mark(ShapeEffects? effects) =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.5, 0.5),
                    "#FF000000", 0.004) { Effects = effects },
            })[0];

    private static IReadOnlyList<ShapeRenderItem> Caster() =>
        ShapeRenderList.From(
            Array.Empty<InkStrokeAnnotation>(),
            new[]
            {
                new ShapeAnnotation(
                    0, new ShapeDraft(ShapeKind.Rectangle, 0.2, 0.2, 0.5, 0.5),
                    "#FF000000", 0.004),
            });

    // ---------------- the recipe ----------------

    [Fact]
    public void a_drop_shadow_spec_becomes_a_filter()
    {
        using var filter = EffectRecipe.FilterFor(Shadow(0.02), Scale, View);

        Assert.NotNull(filter);
    }

    [Fact]
    public void an_effect_this_build_does_not_know_draws_nothing()
    {
        // Not an exception. A shape carrying an effect from a later build has to
        // open, and the part that is understood has to still draw.
        var unknown = new EffectSpec((EffectKind)999, Black, Blur: 0.02, Distance: 0.05);

        Assert.Null(EffectRecipe.FilterFor(unknown, Scale, View));
    }

    // ---------------- the reach ----------------

    [Fact]
    public void the_reach_is_three_halves_of_the_blur()
    {
        // Sigma is half the radius a person sets and the ink is spent by three
        // of them. The one statement of it, in normalized units, which every
        // other reach in the app is derived from.
        Assert.Equal(0.02 * 1.5, Shadow(0.02).Reach, 12);
        Assert.Equal(0.0, Shadow(0).Reach);
    }

    [Fact]
    public void the_preview_and_the_committed_picture_reserve_the_same_reach()
    {
        // THE ONE THAT MATTERS. The preview opens its layer by the reach in slot
        // DIPs; the rasteriser opens its crop by the reach in normalized units.
        // Short on either side the blur is not softened at the edge, it is cut
        // off in a straight line, and the two boxes are drawn in different
        // spaces so nothing but a test notices when they part company.
        var spec = Shadow(0.02);

        var tight = ShadowRasterizer.BoundsOf(Caster(), Shadow(0));
        var box = ShadowRasterizer.BoundsOf(Caster(), spec);

        double committed = tight.L - box.L;
        double preview = OverlayProjection.BlurReachOf(spec, Scale, View);

        Assert.Equal(preview, committed * Scale, 6);
    }

    [Fact]
    public void the_room_the_effects_need_is_the_widest_of_them()
    {
        // Both are drawn from the same silhouette into the same box, so a second
        // effect does not push the first one further out. Adding the reaches up
        // would grow the annotation on every edit, which is the exact shape of a
        // bug this pipeline has already had once.
        var wide = Shadow(0.02);
        var narrow = Shadow(0.005);

        var one = OverlayProjection.SlotBoundsOf(Mark(new ShapeEffects(wide)), Scale, 0, View);
        var both = OverlayProjection.SlotBoundsOf(
            Mark(new ShapeEffects(wide, narrow)), Scale, 0, View);

        Assert.Equal(one.L, both.L, 9);
        Assert.Equal(one.R, both.R, 9);
    }

    [Fact]
    public void an_effect_with_no_blur_asks_for_no_room()
    {
        var hard = OverlayProjection.SlotBoundsOf(Mark(new ShapeEffects(Shadow(0))), Scale, 0, View);
        var none = OverlayProjection.SlotBoundsOf(Mark(null), Scale, 0, View);

        // The offset still counts, so only the sides away from the shadow can
        // be compared: at 135 degrees the shadow falls down and to the right.
        Assert.Equal(none.L, hard.L, 9);
        Assert.Equal(none.T, hard.T, 9);
    }
}
