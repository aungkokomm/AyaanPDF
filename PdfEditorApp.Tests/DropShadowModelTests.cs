using PdfEditorApp.Viewport;
using System.Linq;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Drop Shadow model: what a person sets, and what a renderer gets.
///
/// A person sets a DIRECTION and a DISTANCE. A renderer needs an x/y offset.
/// Deriving the second from the first is one line of trigonometry with two
/// chances to be wrong, and this is where both are pinned down.
///
/// THE TWO THINGS A REASONABLE PERSON GETS BACKWARDS. The angle names where the
/// LIGHT is, so the shadow falls the OPPOSITE way; and these units run y DOWN
/// the page, so a shadow cast downwards has a POSITIVE y. render_core's
/// shadow_offset_pts is the same derivation in PDF space, where y runs the
/// other way, and it is checked against the same table in
/// a_shadow_falls_opposite_the_light. Change one without the other and both
/// fail.
/// </summary>
public class DropShadowModelTests
{
    private static DropShadow At(double angleDeg, double distance) =>
        new(angleDeg, distance, new RenderColor(0xFF, 0, 0, 0));

    [Theory]
    // angle,  offset x,  offset y   (normalized units, y DOWN)
    [InlineData(0.0, -10.0, 0.0)]     // lit from the right, shadow to the left
    [InlineData(90.0, 0.0, 10.0)]     // lit from above, shadow straight down
    [InlineData(180.0, 10.0, 0.0)]    // lit from the left, shadow to the right
    [InlineData(270.0, 0.0, -10.0)]   // lit from below, shadow straight up
    [InlineData(135.0, 7.0711, 7.0711)]  // lit upper-left, shadow lower-right
    [InlineData(45.0, -7.0711, 7.0711)]  // lit upper-right, shadow lower-left
    public void a_shadow_falls_opposite_the_light(double angleDeg, double x, double y)
    {
        // Four cardinals and two diagonals, so a formula that is merely
        // rotated, mirrored or negated cannot pass by accident.
        var shadow = At(angleDeg, 10);

        Assert.Equal(x, shadow.OffsetX, 3);
        Assert.Equal(y, shadow.OffsetY, 3);
    }

    [Fact]
    public void distance_scales_the_offset_without_turning_it()
    {
        var near = At(135, 1);
        var far = At(135, 5);

        Assert.Equal(near.OffsetX * 5, far.OffsetX, 9);
        Assert.Equal(near.OffsetY * 5, far.OffsetY, 9);
    }

    [Fact]
    public void a_shadow_at_no_distance_sits_exactly_under_its_shape()
    {
        var shadow = At(217.5, 0);

        Assert.Equal(0, shadow.OffsetX, 9);
        Assert.Equal(0, shadow.OffsetY, 9);
    }

    [Fact]
    public void a_shadow_at_no_distance_still_knows_where_the_light_is()
    {
        // Why the model stores angle and distance rather than an x/y offset.
        // An angle cannot be recovered from an offset of zero length, so a
        // person who drags the distance to nothing and back would otherwise
        // find their direction reset to due east.
        Assert.Equal(217.5, At(217.5, 0).AngleDeg);
    }

    [Fact]
    public void a_full_turn_of_the_light_comes_back_to_where_it_started()
    {
        var at135 = At(135, 10);
        var at495 = At(495, 10);

        Assert.Equal(at135.OffsetX, at495.OffsetX, 6);
        Assert.Equal(at135.OffsetY, at495.OffsetY, 6);
    }

    // ---------------- reserved, and inert ----------------

    [Fact]
    public void softness_and_spread_default_to_nothing()
    {
        // Every shadow built before these existed, and every one built by a
        // caller that does not mention them, must be exactly the hard shadow
        // it was.
        var shadow = At(135, 10);

        Assert.Equal(0, shadow.Softness);
        Assert.Equal(0, shadow.Spread);
    }

    [Fact]
    public void softness_and_spread_move_nothing_that_is_drawn()
    {
        // They are stored and persisted; they are drawn by nothing. If either
        // ever reached the derived offset it would move the shadow, which is
        // not what softening or spreading one means.
        var hard = At(135, 10);
        var reserved = hard with { Softness = 4, Spread = 2 };

        Assert.Equal(hard.OffsetX, reserved.OffsetX, 9);
        Assert.Equal(hard.OffsetY, reserved.OffsetY, 9);
    }

    [Fact]
    public void opacity_is_the_colours_alpha_and_there_is_no_second_way_to_say_it()
    {
        // Guards the decision rather than the arithmetic. A separate opacity
        // field would need a rule about which of the two wins, and the alpha is
        // already what decides whether there is a shadow at all.
        Assert.DoesNotContain(
            "Opacity",
            typeof(DropShadow).GetProperties().Select(p => p.Name));
    }
}
