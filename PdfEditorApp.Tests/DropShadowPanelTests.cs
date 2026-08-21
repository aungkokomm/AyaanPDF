using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Drop Shadow row: what its controls show for a shape, and what a shape
/// gets when they change.
///
/// Two conversions that have to be exact inverses, because a person will set a
/// value, click away, click back and expect to see what they set. They pull in
/// opposite directions: the controls work in POINTS and PERCENT, the model in
/// fractions of the page width and a colour whose alpha is the opacity.
///
/// The controls also refuse more than the model does. That is deliberate: a
/// blur is the one effect whose cost grows with its size, so the row stops
/// where the measurements said to, while a file made elsewhere with a larger
/// value still loads and still draws.
/// </summary>
public class DropShadowPanelTests
{
    private const double PageWidthPts = 612;

    private static DropShadowControls Controls(
        bool enabled = true, double angle = 135, double distance = 6,
        double blur = 4, int opacity = 50, string color = "#000000") =>
        new(enabled, angle, distance, blur, opacity, color);

    // ---------------- a shape's shadow becomes the controls ----------------

    [Fact]
    public void a_shape_with_no_shadow_shows_the_defaults_switched_off()
    {
        // Switching it on then has to give something worth looking at, rather
        // than a black slab sitting directly under the shape.
        var c = DropShadowPanel.From(null, PageWidthPts);

        Assert.False(c.Enabled);
        Assert.Equal(135, c.AngleDeg);
        Assert.True(c.DistancePts > 0, "a default shadow that is not thrown anywhere is invisible");
        Assert.True(c.OpacityPercent is > 0 and < 100);
    }

    [Fact]
    public void a_shape_with_a_shadow_shows_that_shadows_own_values()
    {
        // The requirement in one test: the row reflects THIS shape, not
        // whatever the controls were last left at.
        var shadow = new DropShadow(
            AngleDeg: 217.5,
            Distance: 12.0 / PageWidthPts,
            Color: new RenderColor(0x80, 0x33, 0x66, 0x99),
            Softness: 9.0 / PageWidthPts);

        var c = DropShadowPanel.From(shadow, PageWidthPts);

        Assert.True(c.Enabled);
        Assert.Equal(217.5, c.AngleDeg, 6);
        Assert.Equal(12.0, c.DistancePts, 6);
        Assert.Equal(9.0, c.BlurPts, 6);
        Assert.Equal(50, c.OpacityPercent);
        Assert.Equal("#336699", c.ColorHex);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x40, 25)]
    [InlineData(0x80, 50)]
    [InlineData(0xFF, 100)]
    public void opacity_reads_back_from_the_colours_alpha(byte alpha, int percent)
    {
        var shadow = new DropShadow(135, 0.01, new RenderColor(alpha, 0, 0, 0));

        Assert.Equal(percent, DropShadowPanel.From(shadow, PageWidthPts).OpacityPercent);
    }

    // ---------------- the controls become a shadow ----------------

    [Fact]
    public void switching_the_row_on_gives_the_shape_a_shadow()
    {
        var shadow = DropShadowPanel.ToShadow(Controls(enabled: true), PageWidthPts);

        Assert.NotNull(shadow);
        Assert.Equal(135, shadow!.Value.AngleDeg);
        Assert.Equal(6.0 / PageWidthPts, shadow.Value.Distance, 9);
        Assert.Equal(4.0 / PageWidthPts, shadow.Value.Softness, 9);
    }

    [Fact]
    public void switching_the_row_off_takes_it_away_completely()
    {
        // Not a transparent shadow, not a shadow at no distance: nothing. The
        // caller writes a zero colour from this, which is what clears the tag.
        Assert.Null(DropShadowPanel.ToShadow(Controls(enabled: false), PageWidthPts));
    }

    [Theory]
    [InlineData(10, 0x1A)]
    [InlineData(50, 0x80)]
    [InlineData(100, 0xFF)]
    public void opacity_becomes_the_colours_alpha(int percent, byte alpha)
    {
        var shadow = DropShadowPanel.ToShadow(Controls(opacity: percent), PageWidthPts);

        Assert.Equal(alpha, shadow!.Value.Color.A);
    }

    [Theory]
    [InlineData("#336699")]
    [InlineData("#FF336699")]
    [InlineData("336699")]
    public void the_colour_picker_may_hand_over_any_of_the_usual_forms(string hex)
    {
        // The existing preset buttons carry "#AARRGGBB" tags, so the row reuses
        // that markup unchanged and drops the alpha, which is the opacity
        // slider's job.
        var shadow = DropShadowPanel.ToShadow(Controls(color: hex), PageWidthPts);

        Assert.Equal(0x33, shadow!.Value.Color.R);
        Assert.Equal(0x66, shadow.Value.Color.G);
        Assert.Equal(0x99, shadow.Value.Color.B);
    }

    // ---------------- the row refuses more than the model would ----------------

    [Fact]
    public void blur_is_capped_at_the_range_the_measurements_justified()
    {
        double beyond = DropShadowPanel.MaxBlurPts(PageWidthPts) * 3;
        var shadow = DropShadowPanel.ToShadow(Controls(blur: beyond), PageWidthPts);

        Assert.Equal(DropShadowPanel.MaxBlurNormalized, shadow!.Value.Softness, 9);
    }

    [Fact]
    public void distance_is_capped_too()
    {
        double beyond = DropShadowPanel.MaxDistancePts(PageWidthPts) * 3;
        var shadow = DropShadowPanel.ToShadow(Controls(distance: beyond), PageWidthPts);

        Assert.Equal(DropShadowPanel.MaxDistanceNormalized, shadow!.Value.Distance, 9);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void a_length_that_is_not_one_reads_as_nothing(double bad)
    {
        var shadow = DropShadowPanel.ToShadow(Controls(blur: bad, distance: bad), PageWidthPts);

        Assert.Equal(0, shadow!.Value.Softness);
        Assert.Equal(0, shadow.Value.Distance);
    }

    [Fact]
    public void spread_is_not_offered_and_comes_back_as_nothing()
    {
        // It is stored and round-tripped by the model and drawn by nothing, so
        // the row does not pretend otherwise.
        Assert.Equal(0, DropShadowPanel.ToShadow(Controls(), PageWidthPts)!.Value.Spread);
    }

    // ---------------- and the trip is stable ----------------

    [Theory]
    [InlineData(0.0, 0.0, 10, "#000000")]
    [InlineData(135.0, 6.0, 50, "#336699")]
    [InlineData(270.0, 12.0, 100, "#FFFFFF")]
    public void what_the_row_shows_survives_being_applied_and_read_back(
        double angle, double distance, int opacity, string color)
    {
        // A person sets a value, clicks away, clicks back, and expects to see
        // what they set. Both conversions have to be exact inverses for that.
        var before = Controls(
            angle: angle, distance: distance, blur: 4, opacity: opacity, color: color);

        var after = DropShadowPanel.From(
            DropShadowPanel.ToShadow(before, PageWidthPts), PageWidthPts);

        Assert.Equal(before, after);
    }

    [Fact]
    public void the_eight_presets_are_the_eight_compass_points()
    {
        Assert.Equal(new[] { 0, 45, 90, 135, 180, 225, 270, 315 }, DropShadowPanel.Directions);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(20.0, 0)]
    [InlineData(30.0, 45)]
    [InlineData(137.0, 135)]
    [InlineData(350.0, 0)]   // ten degrees from zero, thirty five from 315
    [InlineData(359.0, 0)]
    [InlineData(-45.0, 315)]
    public void a_precise_angle_lights_the_preset_nearest_it(double angle, int expected)
    {
        Assert.Equal(expected, DropShadowPanel.NearestDirection(angle));
    }
}
