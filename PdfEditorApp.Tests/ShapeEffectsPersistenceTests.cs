using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Effects surviving the trip into a file and back.
///
/// A shape is stored as a tag in its annotation's /Contents, written field by
/// field in render_core and read here. The shadow is ONE self-describing field
/// on the end, carrying named keys rather than positions, and this is where the
/// C# half of that format is held to it.
///
/// Three things have to be true at once and they pull against each other: an
/// old file must still load, a new file must carry the whole shadow including
/// the parts nothing draws yet, and a shape with no shadow must write exactly
/// the bytes it always wrote. The last is the one that is easy to lose, because
/// it costs nothing to always emit the field and it would quietly change every
/// file the app touches.
/// </summary>
public class ShapeEffectsPersistenceTests
{
    private const double PageWidthPts = 612;

    private static string TagWith(string shadowField) =>
        "AyaanShape:0:FF0000FF:2.0000:1:1:0.00:00000000:0.0000:0.0000:0.0000:" + shadowField;

    // ---------------- old tags still load ----------------

    [Theory]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:40FF0000:0.0000")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:40FF0000:0.0000:100.0000:50.0000")]
    public void every_tag_written_before_effects_existed_loads_with_none(string contents)
    {
        // The compatibility requirement from the reading end: a tag that stops
        // before the shadow field is not a broken tag, it is an older one.
        Assert.True(ShapeTagReader.TryParse(contents, out var tag));

        Assert.Null(tag.ShadowHex);
        Assert.Equal(0, tag.ShadowAngleDeg);
        Assert.Equal(0, tag.ShadowDistancePts);
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

    // ---------------- new tags carry the whole shadow ----------------

    [Fact]
    public void a_shadow_survives_the_tag()
    {
        Assert.True(ShapeTagReader.TryParse(
            TagWith("s(a=135.00,d=12.2400,b=6.1200,p=3.0600,c=80336699)"), out var tag));

        Assert.Equal("#80336699", tag.ShadowHex);
        Assert.Equal(135, tag.ShadowAngleDeg, 4);
        Assert.Equal(12.24, tag.ShadowDistancePts, 4);

        var effects = ShapeEffectsTag.From(tag, PageWidthPts);

        Assert.NotNull(effects);
        var shadow = effects!.Shadow!.Value;

        // The angle is not a length and crosses untouched; everything else
        // comes back as a fraction of the page's width, the space the object
        // model works in.
        Assert.Equal(135, shadow.AngleDeg, 9);
        Assert.Equal(12.24 / PageWidthPts, shadow.Distance, 9);
        Assert.Equal(new RenderColor(0x80, 0x33, 0x66, 0x99), shadow.Color);
    }

    [Fact]
    public void the_reserved_fields_survive_even_though_nothing_draws_them()
    {
        // The entire reason they are written now rather than later. A file
        // saved today must keep its softness when blurring arrives, and a
        // field that is never persisted is one the user set once and lost.
        Assert.True(ShapeTagReader.TryParse(
            TagWith("s(a=135.00,d=12.2400,b=6.1200,p=3.0600,c=80336699)"), out var tag));

        Assert.Equal(6.12, tag.ShadowSoftnessPts, 4);
        Assert.Equal(3.06, tag.ShadowSpreadPts, 4);

        var shadow = ShapeEffectsTag.From(tag, PageWidthPts)!.Shadow!.Value;

        Assert.Equal(6.12 / PageWidthPts, shadow.Softness, 9);
        Assert.Equal(3.06 / PageWidthPts, shadow.Spread, 9);
    }

    [Fact]
    public void a_key_from_a_later_build_is_skipped_rather_than_fatal()
    {
        // Why the field carries names instead of positions. A glow written by
        // a future build must not take this build's shadow down with it.
        Assert.True(ShapeTagReader.TryParse(
            TagWith("s(a=135.00,d=12.2400,b=0.0000,p=0.0000,c=80336699,z=99)"), out var tag));

        Assert.Equal("#80336699", tag.ShadowHex);
        Assert.Equal(135, tag.ShadowAngleDeg, 4);
    }

    [Theory]
    [InlineData("s(a=135.00,d=6.0000,b=0.0000,p=0.0000)")]
    [InlineData("s(a=135.00,d=6.0000,c=00000000)")]
    [InlineData("s(a=abc,d=6.0000,c=FF000000)")]
    [InlineData("s(a=135.00,d=-6.0000,c=FF000000)")]
    [InlineData("s(a=135.00,d=6.0000,b=-1.0000,c=FF000000)")]
    [InlineData("s(a=135.00,d=6.0000,c=FF000000")]
    [InlineData("135.00,6.0000,FF000000")]
    public void a_shadow_field_that_makes_no_sense_reads_as_no_shadow(string field)
    {
        // Never a half-read one. The SHAPE still parses, because a tag we
        // cannot fully trust still describes a rectangle; it just describes one
        // without a shadow, which is a thing that exists.
        Assert.True(ShapeTagReader.TryParse(TagWith(field), out var tag));

        Assert.Null(tag.ShadowHex);
        Assert.Equal(0, tag.ShadowDistancePts);
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
        // It would paint nothing, and recording it would put the shape on the
        // longer tag rung to say so. The colour is the flag, so a zero alpha
        // means there is nothing to flag.
        Assert.Equal(0u, ShapeEffectsTag.RgbaOf(
            new ShapeEffects(new DropShadow(135, 0.02, new RenderColor(0x00, 0x11, 0x22, 0x33)))));
    }

    [Fact]
    public void a_shadow_at_no_distance_is_still_a_shadow()
    {
        // The other side of the rule. Directly under its shape is a legitimate
        // place for a shadow to be, so the DISTANCE must not be what decides
        // whether one exists.
        Assert.NotEqual(0u, ShapeEffectsTag.RgbaOf(
            new ShapeEffects(new DropShadow(135, 0, new RenderColor(0xFF, 0, 0, 0)))));
    }

    [Fact]
    public void a_page_with_no_width_yields_no_effects_rather_than_infinity()
    {
        // Converting points to normalized units divides by the page width, and
        // a zero would produce infinities that then travel into the geometry.
        // The EFFECTS TEXT is what From reads, so the tag has to carry one or
        // this asserts nothing: a tag with no effects comes back null whatever
        // the page width is, and the guard could then be deleted unnoticed.
        var tag = new ShapeTag(
            ShapeKind.Rectangle, "#FF0000FF", 2.0, true, true, 0, null, 0, 0, 0,
            135, 10, 0, 0, "#FF000000",
            "s(a=135.00,d=10.0000,b=0.0000,p=0.0000,c=FF000000)");

        Assert.NotNull(ShapeEffectsTag.From(tag, 200));
        Assert.Null(ShapeEffectsTag.From(tag, 0));
    }
}
