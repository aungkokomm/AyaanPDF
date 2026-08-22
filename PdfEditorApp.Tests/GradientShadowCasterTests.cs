using System;
using System.Linq;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using Xunit;
using Xunit.Abstractions;

namespace PdfEditorApp.Tests;

/// <summary>
/// What casts a gradient-filled shape's shadow.
///
/// REPORTED, after the box fix: with a gradient on the shape the drop shadow is
/// "not obvious anymore, literally non existent", while the same shadow on the
/// same shape without a gradient works perfectly.
///
/// The caster asked <c>tag.FillHex is not null</c>, which is the POSITIONAL
/// fill field. A gradient lives on the tag's tail and deliberately clears that
/// field, because a shape must never carry both. So a gradient-filled shape
/// read as hollow and cast the shadow of a wire frame: a thin soft band instead
/// of a solid one. That is the same regression the caster's own comment records
/// having fixed once already, arriving by a different door.
///
/// Asserted on PIXELS, with a control, because the symptom is visual and a
/// count of items would have passed for the wrong reason.
/// </summary>
public class GradientShadowCasterTests
{
    private const double PageW = 600;

    private readonly ITestOutputHelper _out;

    public GradientShadowCasterTests(ITestOutputHelper output) => _out = output;

    private const string Gradient =
        "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)";

    /// <summary>A shape tag with a chosen positional fill and tail.</summary>
    private static ShapeTag TagFor(string fillHex, string tail)
    {
        string text =
            $"AyaanShape:0:000000FF:2.0000:0:0:0:{fillHex}:0.0000:0.0000:0.0000"
            + (tail.Length > 0 ? ":" + tail : "");

        Assert.True(ShapeTagReader.TryParse(text, out var tag), "the fixture tag does not parse");
        return tag;
    }

    private static ShapeTag Hollow => TagFor("0", "");
    private static ShapeTag Solid => TagFor("FF3B82F6", "");
    private static ShapeTag Gradiented => TagFor("0", Gradient);

    private static readonly EffectSpec[] SoftShadow =
    [
        new EffectSpec(
            EffectKind.DropShadow,
            new RenderColor(0xFF, 0, 0, 0),
            Blur: 12.0 / PageW,
            Distance: 24.0 / PageW,
            AngleDeg: 135),
    ];

    private static (double L, double T, double R, double B) Box => (0.2, 0.2, 0.6, 0.5);

    private static System.Collections.Generic.IReadOnlyList<ShapeRenderItem> Caster(ShapeTag tag)
    {
        var (l, t, r, b) = Box;
        return ShadowRasterizer.CasterItemsFor(tag, l, t, r, b, PageW);
    }

    // ---------------- what casts it ----------------

    [Fact]
    public void a_gradient_filled_shape_casts_from_a_solid_silhouette()
    {
        var items = Caster(Gradiented);

        Assert.Equal(2, items.Count);
        Assert.Equal(RenderStyle.Filled, items[0].Style);
    }

    [Fact]
    public void a_solid_filled_shape_still_does_too()
    {
        var items = Caster(Solid);

        Assert.Equal(2, items.Count);
        Assert.Equal(RenderStyle.Filled, items[0].Style);
    }

    [Fact]
    public void a_shape_with_no_fill_at_all_is_still_correctly_hollow()
    {
        // The guard exists for a reason and the fix must not throw it away: a
        // stroke-only rectangle casts the shadow of a rectangle's OUTLINE,
        // because that is all the ink it has.
        var items = Caster(Hollow);

        Assert.Single(items);
        Assert.Equal(RenderStyle.Stroked, items[0].Style);
    }

    // ---------------- and what it actually looks like ----------------

    /// <summary>
    /// The shadow's alpha at the middle of the picture, 0 to 255.
    ///
    /// The middle is under the body of the shape, which is exactly where a
    /// hollow caster leaves a hole and a solid one does not.
    /// </summary>
    private int MiddleAlpha(ShapeTag tag)
    {
        var raster = ShadowRasterizer.Rasterize(Caster(tag), SoftShadow, PageW);
        Assert.NotNull(raster);

        var r = raster!.Value;
        int x = r.PixelWidth / 2;
        int y = r.PixelHeight / 2;
        int at = ((y * r.PixelWidth) + x) * 4;

        // BGRA, so alpha is the fourth byte.
        return r.Bgra[at + 3];
    }

    [Fact]
    public void the_gradient_shape_casts_the_same_solid_shadow_the_solid_one_does()
    {
        // THE CONTROL AND THE CASE. Identical geometry, identical shadow, one
        // filled with a colour and one with a gradient. A difference here is
        // the reported bug.
        int solid = MiddleAlpha(Solid);
        int gradient = MiddleAlpha(Gradiented);

        _out.WriteLine($"solid fill: alpha {solid}");
        _out.WriteLine($"gradient fill: alpha {gradient}");

        Assert.Equal(solid, gradient);
    }

    [Fact]
    public void that_shadow_is_actually_solid_and_not_a_faint_band()
    {
        // Against the HOLLOW one, which is what the bug turned every gradient
        // shape into. Without a control this test would pass on any number at
        // all; the whole complaint was that the shadow had become faint.
        int hollow = MiddleAlpha(Hollow);
        int gradient = MiddleAlpha(Gradiented);

        _out.WriteLine($"hollow: alpha {hollow}");
        _out.WriteLine($"gradient fill: alpha {gradient}");

        Assert.True(
            gradient > hollow + 40,
            $"a gradient shape's shadow is {gradient} where a hollow one is {hollow}, "
            + "which is the wire-frame shadow the bug produced");

        Assert.True(gradient > 200, $"the shadow's middle is only {gradient} of 255");
    }
}
