using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Glow row's arithmetic, both ways.
///
/// The row speaks points and percent; the model speaks fractions of the page's
/// width and a colour whose alpha is the opacity. Everything a person can set
/// has to survive the trip out and back, or the row shows one thing and the
/// page draws another.
/// </summary>
public class GlowPanelTests
{
    private const double PageWidthPts = 612;

    // ---------------- what a shape with no glow shows ----------------

    [Fact]
    public void the_defaults_are_switched_off_but_worth_switching_on()
    {
        var d = GlowPanel.Defaults;

        Assert.False(d.Enabled);
        Assert.True(d.BlurPts >= GlowPanel.MinBlurPts, "a default that draws nothing is no default");
        Assert.True(d.OpacityPercent >= GlowPanel.MinOpacityPercent);
        Assert.Equal("#FFD400", d.ColorHex);
    }

    [Fact]
    public void no_glow_and_no_page_both_show_the_defaults()
    {
        Assert.Equal(GlowPanel.Defaults, GlowPanel.From(null, PageWidthPts));
        Assert.Equal(
            GlowPanel.Defaults,
            GlowPanel.From(new Glow(new RenderColor(0xFF, 1, 2, 3), 0.01), 0));
    }

    // ---------------- and what a shape with one shows ----------------

    [Fact]
    public void a_glow_reports_its_blur_in_points_and_its_alpha_as_a_percentage()
    {
        var c = GlowPanel.From(
            new Glow(new RenderColor(0x80, 0x11, 0x22, 0x33), 8.0 / PageWidthPts), PageWidthPts);

        Assert.True(c.Enabled);
        Assert.Equal(8.0, c.BlurPts, 6);
        Assert.Equal(50, c.OpacityPercent);
        Assert.Equal("#112233", c.ColorHex);
    }

    [Fact]
    public void a_blur_from_somewhere_else_is_shown_at_the_nearest_end_of_the_slider()
    {
        // The renderer takes any blur; the row offers a range. A file made
        // elsewhere still has to appear somewhere on the slider rather than
        // pushing the thumb off it.
        var wide = GlowPanel.From(new Glow(new RenderColor(0xFF, 0, 0, 0), 0.5), PageWidthPts);
        var none = GlowPanel.From(new Glow(new RenderColor(0xFF, 0, 0, 0), 0), PageWidthPts);

        Assert.Equal(GlowPanel.MaxBlurPts(PageWidthPts), wide.BlurPts, 6);
        Assert.Equal(GlowPanel.MinBlurPts, none.BlurPts, 6);
    }

    // ---------------- the way back ----------------

    [Fact]
    public void a_row_switched_off_is_no_glow_at_all()
    {
        Assert.Null(GlowPanel.ToGlow(GlowPanel.Defaults, PageWidthPts));
        Assert.Null(GlowPanel.ToGlow(
            GlowPanel.Defaults with { Enabled = true }, 0));
    }

    [Fact]
    public void a_row_switched_on_becomes_the_glow_it_describes()
    {
        var glow = GlowPanel.ToGlow(
            new GlowControls(Enabled: true, BlurPts: 12, OpacityPercent: 40, ColorHex: "#FF00FF"),
            PageWidthPts);

        Assert.NotNull(glow);
        Assert.Equal(12.0 / PageWidthPts, glow!.Value.Softness, 9);
        Assert.Equal(new RenderColor(102, 0xFF, 0x00, 0xFF), glow.Value.Color);
    }

    [Fact]
    public void the_row_survives_a_trip_out_and_back()
    {
        var before = new GlowControls(
            Enabled: true, BlurPts: 9, OpacityPercent: 60, ColorHex: "#3B82F6");

        var after = GlowPanel.From(GlowPanel.ToGlow(before, PageWidthPts), PageWidthPts);

        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Equal(before.BlurPts, after.BlurPts, 6);
        Assert.Equal(before.OpacityPercent, after.OpacityPercent);
        Assert.Equal(before.ColorHex, after.ColorHex);
    }

    [Fact]
    public void a_blur_the_row_cannot_reach_is_capped_rather_than_refused()
    {
        var glow = GlowPanel.ToGlow(
            GlowPanel.Defaults with { Enabled = true, BlurPts = 500 }, PageWidthPts);

        Assert.Equal(DropShadowPanel.MaxBlurNormalized, glow!.Value.Softness, 9);
    }

    [Fact]
    public void a_glow_can_never_come_out_of_the_row_with_no_blur()
    {
        // THE ONE WAY A GLOW DIFFERS FROM A SHADOW here. A shadow at no distance
        // still draws; a glow with no blur is the shape's own outline directly
        // behind the shape, which is nothing, and a switch that says on over a
        // page that shows nothing is the worst state a row can be in.
        foreach (double asked in new[] { 0.0, -5.0, double.NaN })
        {
            var glow = GlowPanel.ToGlow(
                GlowPanel.Defaults with { Enabled = true, BlurPts = asked }, PageWidthPts);

            Assert.True(
                glow!.Value.Softness >= GlowPanel.MinBlurPts / PageWidthPts,
                $"a blur of {asked} produced a glow that draws nothing");
        }
    }

    [Fact]
    public void the_limits_are_the_shadows_and_not_a_second_set()
    {
        // Two numbers meaning the same thing is two numbers to keep in step.
        Assert.Equal(DropShadowPanel.MaxBlurPts(PageWidthPts), GlowPanel.MaxBlurPts(PageWidthPts));
        Assert.Equal(DropShadowPanel.MinOpacityPercent, GlowPanel.MinOpacityPercent);
    }
}
