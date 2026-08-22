using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Gradient row's arithmetic, both ways.
///
/// The row speaks two colours and a direction; the model speaks two colours and
/// two endpoints, because that is what both renderers and the PDF shading take.
/// Everything a person can set has to survive the trip out and back, or the row
/// shows one thing and the shape is painted another.
/// </summary>
public class GradientPanelTests
{
    private static readonly RenderColor Red = new(0xFF, 0xFF, 0x00, 0x00);
    private static readonly RenderColor Blue = new(0xFF, 0x00, 0x00, 0xFF);

    // ---------------- what a shape with no gradient shows ----------------

    [Fact]
    public void the_defaults_are_switched_off_but_worth_switching_on()
    {
        var d = GradientPanel.Defaults;

        Assert.False(d.Enabled);
        Assert.NotEqual(d.StartHex, d.EndHex);
        Assert.Equal(0, d.AngleDeg);
    }

    [Fact]
    public void no_gradient_shows_the_defaults_and_a_row_switched_off_is_no_gradient()
    {
        Assert.Equal(GradientPanel.Defaults, GradientPanel.From(null));
        Assert.Null(GradientPanel.ToGradient(GradientPanel.Defaults));
    }

    // ---------------- the direction ----------------

    [Theory]
    [InlineData(0, 0.0, 0.5, 1.0, 0.5)]      // left to right
    [InlineData(90, 0.5, 0.0, 0.5, 1.0)]     // top to bottom
    [InlineData(180, 1.0, 0.5, 0.0, 0.5)]    // right to left
    [InlineData(270, 0.5, 1.0, 0.5, 0.0)]    // bottom to top
    public void a_square_angle_runs_edge_to_edge(
        int angle, double x0, double y0, double x1, double y1)
    {
        // The four a person actually names. At each of them the ramp has to
        // reach both edges exactly, or "left to right" leaves a flat band at
        // each end.
        var g = GradientPanel.AtAngle(Red, Blue, angle);

        Assert.Equal(x0, g.X0, 6);
        Assert.Equal(y0, g.Y0, 6);
        Assert.Equal(x1, g.X1, 6);
        Assert.Equal(y1, g.Y1, 6);
    }

    [Fact]
    public void the_angle_turns_the_way_it_looks_like_it_should()
    {
        // y runs DOWN, so a growing angle sweeps clockwise on screen. Getting
        // this backwards makes every direction other than horizontal wrong, and
        // nothing else in the suite would notice.
        var g = GradientPanel.AtAngle(Red, Blue, 90);

        Assert.True(g.Y1 > g.Y0, "90 degrees should run DOWN the shape");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(225)]
    [InlineData(270)]
    [InlineData(315)]
    public void every_step_the_slider_offers_survives_the_trip_out_and_back(int angle)
    {
        Assert.Equal(angle, GradientPanel.AngleOf(GradientPanel.AtAngle(Red, Blue, angle)));
    }

    [Fact]
    public void a_direction_from_somewhere_else_shows_at_the_nearest_step()
    {
        // The model takes any two endpoints and the row offers eight
        // directions. A gradient made by a later build still has to put the
        // thumb somewhere on the track.
        var odd = new GradientFill(Red, Blue, 0, 0, 1, 0.1);

        Assert.Equal(0, GradientPanel.AngleOf(odd));
    }

    [Fact]
    public void a_direction_that_reads_as_negative_is_shown_the_long_way_round()
    {
        // -45 is 315, and a slider has no room for a negative.
        var up = new GradientFill(Red, Blue, 0, 1, 1, 0);

        Assert.Equal(315, GradientPanel.AngleOf(up));
    }

    [Fact]
    public void every_direction_produces_a_gradient_that_can_actually_be_drawn()
    {
        // Two endpoints in the same place have no direction, and both renderers
        // refuse one. The row must not be able to reach that state.
        for (int angle = 0; angle <= GradientPanel.MaxAngleDeg; angle += GradientPanel.AngleStepDeg)
        {
            Assert.True(
                GradientPanel.AtAngle(Red, Blue, angle).IsDrawable,
                $"{angle} degrees produced a gradient of no length");
        }
    }

    // ---------------- and the whole row ----------------

    [Fact]
    public void a_row_switched_on_becomes_the_gradient_it_describes()
    {
        var g = GradientPanel.ToGradient(
            new GradientControls(true, "#FFD13438", "#FF0078D4", 90));

        Assert.NotNull(g);
        Assert.Equal(new RenderColor(0xFF, 0xD1, 0x34, 0x38), g!.Value.From);
        Assert.Equal(new RenderColor(0xFF, 0x00, 0x78, 0xD4), g.Value.To);
        Assert.Equal(90, GradientPanel.AngleOf(g.Value));
    }

    [Fact]
    public void the_row_survives_a_trip_out_and_back()
    {
        var before = new GradientControls(true, "#FF112233", "#FF445566", 225);

        Assert.Equal(before, GradientPanel.From(GradientPanel.ToGradient(before)));
    }

    // ---------------- the stage's one constraint ----------------

    [Fact]
    public void a_stop_can_never_come_out_of_the_row_translucent()
    {
        // THE WRITER REFUSES A TRANSLUCENT STOP rather than flattening it, so a
        // translucent stop must not be able to reach a tag: the shape would
        // draw on screen and save unfilled. Enforced here, at the one door
        // between the controls and the model, rather than trusted to a picker.
        var g = GradientPanel.ToGradient(
            new GradientControls(true, "#00D13438", "#800078D4", 0));

        Assert.Equal(0xFF, g!.Value.From.A);
        Assert.Equal(0xFF, g.Value.To.A);

        // And the colour itself is untouched: only the alpha is forced.
        Assert.Equal(0xD1, g.Value.From.R);
        Assert.Equal(0xD4, g.Value.To.B);
    }

    [Fact]
    public void the_row_always_reports_its_colours_opaque()
    {
        var c = GradientPanel.From(new GradientFill(
            new RenderColor(0x40, 0x11, 0x22, 0x33),
            new RenderColor(0x80, 0x44, 0x55, 0x66),
            0, 0.5, 1, 0.5));

        Assert.Equal("#FF112233", c.StartHex);
        Assert.Equal("#FF445566", c.EndHex);
    }
}
