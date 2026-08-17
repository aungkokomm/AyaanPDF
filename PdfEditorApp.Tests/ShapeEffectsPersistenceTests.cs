using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Effects surviving the trip into a file and back.
///
/// A shape is stored as a tag in its annotation's /Contents, written field by
/// field in render_core and read here. The format grows by APPENDING optional
/// trailing fields, each reading as its historic default when absent, and this
/// is where the C# half of that bargain is held to it.
///
/// Three things have to be true at once and they pull against each other: an
/// old file must still load, a new file must carry the shadow, and a shape with
/// no shadow must write exactly the bytes it always wrote. The last is the one
/// that is easy to lose, because it costs nothing to always emit the longest
/// form and it would quietly change every file the app touches.
/// </summary>
public class ShapeEffectsPersistenceTests
{
    private const double PageWidthPts = 612;

    private static ShapeEffects Shadow(double dx, double dy, byte a, byte r, byte g, byte b) =>
        new(new DropShadow(dx, dy, new RenderColor(a, r, g, b)));

    // ---------------- old tags still load ----------------

    [Theory]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:40FF0000:0.0000")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:40FF0000:0.0000:100.0000:50.0000")]
    public void every_tag_written_before_effects_existed_loads_with_none(string contents)
    {
        // The compatibility requirement from the reading end: a tag that stops
        // before the shadow fields is not a broken tag, it is an older one.
        Assert.True(ShapeTagReader.TryParse(contents, out var tag));

        Assert.Null(tag.ShadowHex);
        Assert.Equal(0, tag.ShadowDxPts);
        Assert.Equal(0, tag.ShadowDyPts);
        Assert.Null(ShapeEffectsTag.From(tag, PageWidthPts));
    }

    [Fact]
    public void an_id_prefixed_old_tag_also_loads_with_no_effects()
    {
        // /Contents may carry the stable-identity prefix in front of the tag,
        // and the reader has to strip it before any of this applies.
        Assert.True(ShapeTagReader.TryParse(
            "ID:0123456789abcdef0123456789abcdef|AyaanShape:0:FF0000FF:2.0000:1:1", out var tag));

        Assert.Null(ShapeEffectsTag.From(tag, PageWidthPts));
    }

    // ---------------- new tags carry the shadow ----------------

    [Fact]
    public void a_shadow_survives_the_tag()
    {
        // The full rung, as render_core writes it: everything positional before
        // the shadow, then offset x, offset y and colour.
        Assert.True(ShapeTagReader.TryParse(
            "AyaanShape:0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000"
            + ":12.2400:-6.1200:80336699",
            out var tag));

        Assert.Equal("#80336699", tag.ShadowHex);
        Assert.Equal(12.24, tag.ShadowDxPts, 4);
        Assert.Equal(-6.12, tag.ShadowDyPts, 4);

        var effects = ShapeEffectsTag.From(tag, PageWidthPts);

        Assert.NotNull(effects);
        var shadow = effects!.Shadow!.Value;

        // Points back into normalized units, which is the object model's space.
        Assert.Equal(12.24 / PageWidthPts, shadow.OffsetX, 9);
        Assert.Equal(-6.12 / PageWidthPts, shadow.OffsetY, 9);
        Assert.Equal(new RenderColor(0x80, 0x33, 0x66, 0x99), shadow.Color);
    }

    [Fact]
    public void a_negative_offset_survives_where_a_length_would_not()
    {
        // Every other length in the tag is rejected when negative, because a
        // negative width or radius is meaningless. A shadow cast up and to the
        // left is not, so these two fields are signed and this says so.
        Assert.True(ShapeTagReader.TryParse(
            "AyaanShape:0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000"
            + ":-4.0000:-8.0000:FF000000",
            out var tag));

        Assert.Equal(-4.0, tag.ShadowDxPts, 4);
        Assert.Equal(-8.0, tag.ShadowDyPts, 4);
    }

    // ---------------- the round trip ----------------

    [Theory]
    [InlineData(0.02, 0.015, 0xFF, 0x00, 0x00, 0x00)]
    [InlineData(-0.03, 0.0, 0x80, 0x33, 0x66, 0x99)]
    [InlineData(0.0, -0.025, 0x40, 0xFF, 0xFF, 0xFF)]
    [InlineData(0.0, 0.0, 0xC0, 0x12, 0x34, 0x56)]
    public void effects_out_and_back_are_the_effects_that_went_in(
        double dx, double dy, byte a, byte r, byte g, byte b)
    {
        // Save then reload, expressed without a PDF: the object model's effects
        // become the tag's fields, and the tag's fields become the object
        // model's effects again.
        var original = Shadow(dx, dy, a, r, g, b);

        string hex = ShapeEffectsTag.HexOf(original)!;
        double dxPts = dx * PageWidthPts;
        double dyPts = dy * PageWidthPts;

        var tag = new ShapeTag(
            ShapeKind.Rectangle, "#FF0000FF", 2.0, true, true, 0, null, 0, 0, 0,
            dxPts, dyPts, hex);

        var reloaded = ShapeEffectsTag.From(tag, PageWidthPts);

        Assert.NotNull(reloaded);
        var shadow = reloaded!.Shadow!.Value;

        Assert.Equal(dx, shadow.OffsetX, 9);
        Assert.Equal(dy, shadow.OffsetY, 9);
        Assert.Equal(original.Shadow!.Value.Color, shadow.Color);
    }

    // ---------------- what counts as "no shadow" ----------------

    [Fact]
    public void no_effects_writes_no_colour_and_therefore_no_shadow()
    {
        Assert.Equal(0u, ShapeEffectsTag.RgbaOf(null));
        Assert.Equal(0u, ShapeEffectsTag.RgbaOf(new ShapeEffects()));
        Assert.Null(ShapeEffectsTag.HexOf(null));
    }

    [Fact]
    public void a_fully_transparent_shadow_is_not_a_shadow()
    {
        // It would paint nothing, and recording it would push the shape onto
        // the longest tag rung to say so. The colour is the flag, so a zero
        // alpha means there is nothing to flag.
        Assert.Equal(0u, ShapeEffectsTag.RgbaOf(Shadow(0.02, 0.02, 0x00, 0x11, 0x22, 0x33)));
    }

    [Fact]
    public void a_shadow_at_no_offset_is_still_a_shadow()
    {
        // The other side of the rule. Directly under its shape is a legitimate
        // place for a shadow to be, so the OFFSET must not be what decides
        // whether one exists.
        Assert.NotEqual(0u, ShapeEffectsTag.RgbaOf(Shadow(0, 0, 0xFF, 0, 0, 0)));
    }

    [Fact]
    public void a_page_with_no_width_yields_no_effects_rather_than_infinity()
    {
        // Converting points to normalized units divides by the page width, and
        // a zero would produce infinities that then travel into the geometry.
        var tag = new ShapeTag(
            ShapeKind.Rectangle, "#FF0000FF", 2.0, true, true, 0, null, 0, 0, 0,
            10, 10, "#FF000000");

        Assert.Null(ShapeEffectsTag.From(tag, 0));
    }
}
