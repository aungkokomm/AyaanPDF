using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Rebuilding a shape from its own tag: what paste, Ctrl+drag duplicate and
/// undoing a delete all do.
///
/// These exist because the view model has its own hand-rolled copy of the tag
/// parser which reads the first eight fields and drops the rest, and because
/// that copy is private to a WinUI class no test assembly can load. Three bugs
/// of one kind have come out of it so far, each found by a person: a duplicate
/// losing its fill, a duplicate losing its corner radius, and a duplicate
/// losing its drop shadow.
///
/// So the assertions below are mostly "this field survives". That is the whole
/// point: the failure mode is never a crash, it is a shape that comes back
/// subtly plainer than the one it was copied from.
///
/// Nothing here is wired into the app yet. This commit makes the canonical
/// parser reachable and proves it carries everything; moving the call sites
/// over is separate.
/// </summary>
public class ShapeRewriteSpecTests
{
    private const int Cap = 1000;

    /// <summary>The rectangle a rebuild targets, in normalized units.</summary>
    private const double L = 0.1, T = 0.2, R = 0.5, B = 0.6;

    private static ShapeRewriteSpec Parse(string contents)
    {
        Assert.True(
            ShapeWriter.TryForExistingShape(contents, L, T, R, B, Cap, out var spec),
            $"tag should have parsed: {contents}");
        return spec;
    }

    /// <summary>The longest rung: rotation, fill, radius, box, then a shadow.</summary>
    private const string FullTag =
        "AyaanShape:0:3B82F6FF:2.5000:1:1:45.00:40FF0000:6.0000:100.0000:50.0000"
        + ":12.0000:-8.0000:80112233";

    // ---------------- every field survives ----------------

    [Fact]
    public void the_kind_survives()
    {
        Assert.Equal(ShapeKind.Rectangle, Parse(FullTag).Geometry.Kind);
    }

    [Fact]
    public void the_stroke_colour_survives_and_is_unpacked_from_rrggbbaa()
    {
        // The tag stores the stroke as RRGGBBAA and the app speaks AARRGGBB.
        // Getting this backwards swaps the alpha and the red channel, which on
        // an opaque shape is invisible until somebody uses transparency.
        var style = Parse(FullTag).Style;

        Assert.Equal(0xFF, style.A);
        Assert.Equal(0x3B, style.R);
        Assert.Equal(0x82, style.G);
        Assert.Equal(0xF6, style.B);
    }

    [Fact]
    public void the_rotation_survives()
    {
        Assert.Equal(45f, Parse(FullTag).Geometry.RotationDeg);
    }

    [Fact]
    public void the_fill_survives()
    {
        // Bug one of three: a duplicate came back stroke-only.
        Assert.Equal(0x40FF0000u, Parse(FullTag).Style.FillRgba);
    }

    [Fact]
    public void the_corner_radius_survives()
    {
        // Bug two of three: a duplicated rounded rectangle came back square.
        Assert.Equal(6f, Parse(FullTag).Geometry.CornerRadiusPx);
    }

    [Fact]
    public void the_drop_shadow_survives()
    {
        // Bug three of three, and the one this commit exists for.
        var style = Parse(FullTag).Style;

        Assert.Equal(12f, style.ShadowDxPx);
        Assert.Equal(-8f, style.ShadowDyPx);
        Assert.Equal(0x80112233u, style.ShadowRgba);
    }

    // ---------------- absence is not a shadow, and not a fill ----------------

    [Fact]
    public void a_tag_with_no_shadow_rebuilds_without_one()
    {
        var style = Parse("AyaanShape:0:3B82F6FF:2.5000:1:1").Style;

        Assert.Equal(0u, style.ShadowRgba);
        Assert.Equal(0f, style.ShadowDxPx);
        Assert.Equal(0f, style.ShadowDyPx);
    }

    [Fact]
    public void a_tag_with_no_fill_rebuilds_stroke_only()
    {
        Assert.Equal(0u, Parse("AyaanShape:0:3B82F6FF:2.5000:1:1").Style.FillRgba);
    }

    [Theory]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00")]
    [InlineData("AyaanShape:0:FF0000FF:2.0000:1:1:45.00:40FF0000:0.0000")]
    public void every_older_tag_still_rebuilds(string contents)
    {
        // Each rung the writer has ever emitted. A shape drawn by an older
        // build must still paste and still undo.
        Assert.True(ShapeWriter.TryForExistingShape(contents, L, T, R, B, Cap, out _));
    }

    // ---------------- geometry, exactly as the existing code computes it ----------------

    [Fact]
    public void the_drag_direction_decides_which_corner_is_the_start()
    {
        // An arrow points where it was dragged. Rebuilding from min/max would
        // throw that away and flip half the arrows in a pasted selection.
        var forward = Parse("AyaanShape:3:FF0000FF:2.0000:1:1").Geometry;
        var reversed = Parse("AyaanShape:3:FF0000FF:2.0000:0:0").Geometry;

        Assert.Equal((float)(L * Cap), forward.X1);
        Assert.Equal((float)(R * Cap), forward.X2);
        Assert.Equal((float)(T * Cap), forward.Y1);
        Assert.Equal((float)(B * Cap), forward.Y2);

        Assert.Equal(forward.X2, reversed.X1);
        Assert.Equal(forward.X1, reversed.X2);
        Assert.Equal(forward.Y2, reversed.Y1);
        Assert.Equal(forward.Y1, reversed.Y2);
    }

    [Fact]
    public void points_are_used_as_pixels_exactly_as_the_existing_code_does()
    {
        // NOT a bug being preserved by accident. The tag stores lengths in
        // points, the core wants capture pixels, and the page width that
        // converts them is not available at these call sites. The existing code
        // passes the number through unchanged and this must keep doing the
        // same, or a duplicate changes size the day the call sites move over.
        var spec = Parse(FullTag);

        Assert.Equal(2.5f, spec.Geometry.StrokeWidthPx);
        Assert.Equal(6f, spec.Geometry.CornerRadiusPx);
        Assert.Equal(12f, spec.Style.ShadowDxPx);
    }

    // ---------------- refusals ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AyaanText:0:FF0000FF:2.0")]
    [InlineData("AyaanShape:0:GGGGGGGG:2.0:1:1")]
    [InlineData("AyaanShape:0:FF0000FF:abc:1:1")]
    public void a_tag_that_is_not_a_usable_shape_is_refused(string? contents)
    {
        Assert.False(ShapeWriter.TryForExistingShape(contents, L, T, R, B, Cap, out _));
    }

    [Fact]
    public void an_identity_prefixed_tag_is_accepted()
    {
        // A DIFFERENCE from the view model's copy, and an improvement: that one
        // tests StartsWith("AyaanShape:") and so rejects the prefixed form
        // outright. Recorded here because the call sites move over next.
        Assert.True(ShapeWriter.TryForExistingShape(
            "ID:0123456789abcdef0123456789abcdef|" + FullTag, L, T, R, B, Cap, out var spec));

        Assert.Equal(0x80112233u, spec.Style.ShadowRgba);
    }

    [Fact]
    public void a_shape_kind_that_does_not_exist_is_refused()
    {
        // The other DIFFERENCE: stricter than the copy, which accepts any
        // integer. render_core rejects an unknown kind on the way in, so
        // nothing the writer produces is affected.
        Assert.False(ShapeWriter.TryForExistingShape(
            "AyaanShape:99:FF0000FF:2.0000:1:1", L, T, R, B, Cap, out _));
    }
}
