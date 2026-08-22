using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The C# side understanding an effect LIST rather than a shadow.
///
/// render_core carries a shape's effects as text and does not model them, which
/// is what makes a new effect free there. This side has to do a little more: it
/// draws the effects it knows and it edits them, so it needs them typed. What it
/// must NOT do is become a second generic effect engine, and it must not lose
/// what it cannot type.
///
/// So the split is deliberately narrow. A field whose letter this build knows
/// becomes an <see cref="EffectSpec"/>. Everything else is CARRIED verbatim:
/// not drawn, not interpreted, and not dropped when the user edits the effect
/// next to it. The core is the authority on what an effect is; this is only
/// enough structure to edit the two we can draw.
/// </summary>
public class EffectModelTagTests
{
    /// <summary>The fixture page, so a point is a fiftieth of the width.</summary>
    private const double PageWidthPts = 200;

    private static ShapeTag Parse(string tail)
    {
        Assert.True(
            ShapeTagReader.TryParse(
                "AyaanShape:0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000:" + tail,
                out var tag),
            "the tag did not parse: " + tail);

        return tag;
    }

    private const string ShadowField = "s(a=135.00,d=6.0000,b=2.0000,p=1.0000,c=80112233)";
    private const string GlowField = "g(a=0.00,d=0.0000,b=4.0000,p=0.0000,c=FF00FF00)";
    private const string StrangeField = "q(b=3.0000,c=FF0000FF,zz=7)";

    // ---------------- the letters this build knows ----------------

    [Fact]
    public void a_glow_in_the_tag_becomes_a_glow_spec()
    {
        var effects = ShapeEffectsTag.From(Parse(GlowField), PageWidthPts);

        var spec = Assert.Single(effects!.Specs);
        Assert.Equal(EffectKind.Glow, spec.Kind);
        Assert.Equal(4.0 / PageWidthPts, spec.Blur, 9);
        Assert.Equal(new RenderColor(0xFF, 0x00, 0xFF, 0x00), spec.Color);
    }

    [Fact]
    public void a_glow_alone_is_effects_rather_than_nothing()
    {
        // The rule used to be "no shadow colour means no effects", which is the
        // one thing that cannot survive a second effect existing.
        Assert.NotNull(ShapeEffectsTag.From(Parse(GlowField), PageWidthPts));
        Assert.Null(ShapeEffectsTag.From(Parse(GlowField), PageWidthPts)!.Shadow);
    }

    [Fact]
    public void a_shadow_and_a_glow_both_come_back()
    {
        var effects = ShapeEffectsTag.From(Parse(ShadowField + ":" + GlowField), PageWidthPts);

        Assert.Equal(2, effects!.Specs.Count);
        Assert.NotNull(effects.Shadow);
        Assert.NotNull(effects.Glow);
        Assert.Equal(135, effects.Shadow!.Value.AngleDeg);
        Assert.Equal(4.0 / PageWidthPts, effects.Glow!.Value.Softness, 9);
    }

    [Fact]
    public void every_letter_this_build_writes_is_a_letter_it_can_read()
    {
        // The two directions live side by side precisely so they cannot drift,
        // and this is what says so. A kind that writes a letter nothing reads
        // would save and then vanish on the next load.
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var one = new ShapeEffects(new EffectSpec(kind, new RenderColor(0xFF, 1, 2, 3), Blur: 0.02));
            string text = ShapeEffectsTag.TextOf(one, PageWidthPts);

            Assert.False(string.IsNullOrEmpty(text), $"{kind} wrote no field");

            var back = ShapeEffectsTag.From(Parse(text), PageWidthPts);
            Assert.Equal(kind, Assert.Single(back!.Specs).Kind);
        }
    }

    // ---------------- the order they end up in ----------------

    [Fact]
    public void shadow_then_glow_and_glow_then_shadow_are_the_same_thing()
    {
        // ONE CANONICAL ORDER, fixed for the effects we have: the shadow
        // underneath, the glow above it. Whichever way round they are handed
        // over, the model, the painter and the file all see the same list, so
        // "add a glow to a shadowed shape" and "add a shadow to a glowing one"
        // cannot produce two different documents.
        var shadow = new EffectSpec(
            EffectKind.DropShadow, new RenderColor(0x80, 0, 0, 0), Blur: 0.01, Distance: 0.03, AngleDeg: 135);
        var glow = new EffectSpec(EffectKind.Glow, new RenderColor(0xFF, 0, 0xFF, 0), Blur: 0.02);

        var one = new ShapeEffects(shadow, glow);
        var other = new ShapeEffects(glow, shadow);

        Assert.Equal(one, other);
        Assert.Equal(
            ShapeEffectsTag.TextOf(one, PageWidthPts),
            ShapeEffectsTag.TextOf(other, PageWidthPts));
        Assert.Equal(EffectKind.DropShadow, one.Specs[0].Kind);
        Assert.Equal(EffectKind.Glow, one.Specs[1].Kind);
    }

    [Fact]
    public void two_effects_of_one_kind_keep_the_order_they_were_given()
    {
        // The ordering is by KIND and nothing else. Sorting within a kind would
        // be an ordering framework, which is more than is needed and more than
        // anything can currently produce.
        var first = new EffectSpec(EffectKind.DropShadow, new RenderColor(0xFF, 1, 0, 0), Distance: 0.01);
        var second = new EffectSpec(EffectKind.DropShadow, new RenderColor(0xFF, 2, 0, 0), Distance: 0.02);

        Assert.Equal(new[] { first, second }, new ShapeEffects(first, second).Specs);
        Assert.Equal(new[] { second, first }, new ShapeEffects(second, first).Specs);
    }

    // ---------------- and the ones it cannot name ----------------

    [Fact]
    public void an_effect_this_build_cannot_name_is_carried_rather_than_typed()
    {
        var effects = ShapeEffectsTag.From(Parse(StrangeField), PageWidthPts);

        Assert.Empty(effects!.Specs);
        Assert.Single(effects.Carried);
        Assert.True(effects.IsEmpty, "there is nothing to draw, so nothing should be drawn");
    }

    [Fact]
    public void a_carried_effect_is_written_back_out_with_its_lengths_intact()
    {
        var effects = ShapeEffectsTag.From(Parse(StrangeField), PageWidthPts);
        string text = ShapeEffectsTag.TextOf(effects, PageWidthPts);

        Assert.Contains("q(", text, StringComparison.Ordinal);
        Assert.Contains("b=3.0000", text, StringComparison.Ordinal);
        Assert.Contains("zz=7", text, StringComparison.Ordinal);
        Assert.Contains("c=FF0000FF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void a_carried_effect_survives_a_trip_through_the_cores_own_space()
    {
        // The model measures in fractions of the page's width and the core
        // takes capture pixels, so a carried field is rescaled twice on the way
        // out and back. Getting that wrong would shrink somebody else's effect
        // by a factor of five every time this build touched the shape.
        const int CaptureWidth = 1000;

        var effects = ShapeEffectsTag.From(Parse(StrangeField), PageWidthPts);
        string asPixels = ShapeEffectsTag.TextOf(effects, CaptureWidth);

        // 3 points on a 200pt page against a 1000px capture is 15 pixels.
        Assert.Contains("b=15.0000", asPixels, StringComparison.Ordinal);
    }

    [Fact]
    public void editing_a_known_effect_does_not_discard_an_unknown_one()
    {
        // THE ONE THAT MATTERS. The core preserves a field it cannot read; this
        // side has to as well, or the next shadow edit sends a list without it
        // and the core faithfully records the loss.
        var loaded = ShapeEffectsTag.From(Parse(ShadowField + ":" + StrangeField), PageWidthPts);

        Assert.Single(loaded!.Specs);
        Assert.Single(loaded.Carried);

        var edited = loaded.With(new DropShadow(90, 0.05, new RenderColor(0xFF, 0, 0, 0)));
        string text = ShapeEffectsTag.TextOf(edited, PageWidthPts);

        Assert.Contains("a=90.00", text, StringComparison.Ordinal);
        Assert.Contains("q(", text, StringComparison.Ordinal);
        Assert.Contains("zz=7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void removing_a_known_effect_does_not_discard_an_unknown_one()
    {
        var loaded = ShapeEffectsTag.From(Parse(ShadowField + ":" + StrangeField), PageWidthPts);

        string text = ShapeEffectsTag.TextOf(loaded!.With((DropShadow?)null), PageWidthPts);

        Assert.DoesNotContain("s(", text, StringComparison.Ordinal);
        Assert.Contains("q(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void adding_a_glow_keeps_the_shadow_and_the_unknown_alike()
    {
        var loaded = ShapeEffectsTag.From(Parse(ShadowField + ":" + StrangeField), PageWidthPts);

        string text = ShapeEffectsTag.TextOf(
            loaded!.With(new Glow(new RenderColor(0xFF, 0, 0xFF, 0), 0.02)), PageWidthPts);

        Assert.Contains("s(", text, StringComparison.Ordinal);
        Assert.Contains("g(", text, StringComparison.Ordinal);
        Assert.Contains("q(", text, StringComparison.Ordinal);
    }

    // ---------------- reading every field, not just the shadow ----------------

    [Fact]
    public void the_reader_returns_every_field_in_order()
    {
        var fields = ShapeTagReader.EffectsIn(ShadowField + ":" + GlowField + ":" + StrangeField);

        Assert.Equal(3, fields.Count);
        Assert.Equal(new[] { 's', 'g', 'q' }, fields.Select(f => f.Kind).ToArray());
        Assert.Equal("#80112233", fields[0].Hex);
        Assert.Equal(4.0, fields[1].BlurPts);
        Assert.Equal(StrangeField, fields[2].Field);
    }

    [Fact]
    public void the_named_shadow_reader_is_the_same_answer_one_field_narrower()
    {
        var named = ShapeTagReader.ShadowIn(GlowField + ":" + ShadowField);

        Assert.Equal(135, named.AngleDeg);
        Assert.Equal(6.0, named.DistancePts);
        Assert.Equal("#80112233", named.Hex);
    }
}
