using System;
using System.Collections.Generic;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The subtraction the model was missing, and the two symptoms it caused.
///
/// REPORTED: put a gradient on a shape, then add a drop shadow. The shadow does
/// nothing, and every touch of a shadow control grows the shape a little more.
///
/// One cause. render_core grows an annotation's /Rect by the room the effects
/// need, so PDFium does not crop them, and its restyle takes the same four
/// numbers back off before reading the shape's own extent. The model only ever
/// took off the stroke pad, so UprightBounds was as big as the shadow.
///
/// Invisible until a gradient was involved, because the Skia overlay is the
/// only thing that DRAWS at the model's upright box: the painted gradient grew
/// with the shadow's room, and being opaque it covered the shadow that had
/// grown it.
/// </summary>
public class ShapeEffectsRoomTests
{
    private const double PageW = 600;

    /// <summary>A shape's tag with a given tail, at a known box.</summary>
    private static string Tag(string tail) =>
        "AyaanShape:0:FF000000:2.0000:0:0:0:0:0.0000:0.0000:0.0000"
        + (tail.Length > 0 ? ":" + tail : "");

    /// <summary>The shape's own rectangle, before anything is reserved.</summary>
    private static readonly TextRect Bare = new(0.2, 0.2, 0.6, 0.5);

    /// <summary>
    /// The annotation as render_core would have written it: the shape's own
    /// rectangle, OPENED by the room its effects need.
    ///
    /// This is the half the test has to supply, and getting it wrong is what
    /// made the first version of these tests fail against a correct fix: handing
    /// the same rectangle with and without a shadow asks the model to invert a
    /// growth that never happened, so of course the shadowed one comes back
    /// smaller. Growing here and subtracting there is the invariant under test:
    /// the two are the same four numbers, so the round trip is the identity.
    /// </summary>
    private static AnnotationSnapshot Snapshot(string tail)
    {
        var room = ShapeEffectsRoom.Of(tail, PageW);

        return new AnnotationSnapshot(
            0, 2,
            Bare.Left - room.Left, Bare.Top - room.Top,
            Bare.Right + room.Right, Bare.Bottom + room.Bottom,
            1.0, Guid.NewGuid(), Tag(tail));
    }

    private static ShapeObject ShapeFrom(string tail)
    {
        var page = DocumentModelBuilder.BuildPage(
            0, new List<AnnotationSnapshot> { Snapshot(tail) }, PageW);

        return Assert.IsType<ShapeObject>(Assert.Single(page.Objects));
    }

    /// <summary>
    /// A hard shadow thrown down and to the right.
    ///
    /// 135, not 45. The tag's angle is where the LIGHT comes FROM, so the mark
    /// is thrown the opposite way; 45 puts it down and to the LEFT. The same
    /// trap is recorded on the compass in the effects row.
    /// </summary>
    private const string Shadow = "s(a=135.0000,d=30.0000,b=0.0000,p=0.0000,c=80000000)";

    /// <summary>A soft one, whose blur opens all four sides.</summary>
    private const string SoftShadow = "s(a=135.0000,d=30.0000,b=24.0000,p=0.0000,c=80000000)";

    private const string Gradient =
        "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)";

    // ---------------- the room itself ----------------

    [Fact]
    public void a_shape_with_no_effects_asks_for_no_room()
    {
        Assert.True(ShapeEffectsRoom.Of("", PageW).IsEmpty);
        Assert.True(ShapeEffectsRoom.Of(null, PageW).IsEmpty);
    }

    [Fact]
    public void a_gradient_asks_for_no_room()
    {
        // It is paint. It is drawn inside the shape's own outline and reaches
        // nowhere, which is why render_core reserves nothing for it either.
        Assert.True(ShapeEffectsRoom.Of(Gradient, PageW).IsEmpty);
    }

    [Fact]
    public void a_hard_shadow_opens_only_the_sides_it_is_thrown_towards()
    {
        // Down and to the right, so the left and
        // the top are untouched. Opening all four would be the "inflate by the
        // reach on every side" shortcut, which is wrong here: this room has to
        // be the exact inverse of what the writer added.
        var room = ShapeEffectsRoom.Of(Shadow, PageW);

        Assert.True(room.Right > 0, "the shadow is thrown right");
        Assert.True(room.Bottom > 0, "the shadow is thrown down");
        Assert.Equal(0, room.Left, 9);
        Assert.Equal(0, room.Top, 9);
    }

    [Fact]
    public void a_blur_opens_all_four_sides()
    {
        var room = ShapeEffectsRoom.Of(SoftShadow, PageW);

        Assert.True(room.Left > 0 && room.Top > 0, "a blur spreads in every direction");
        Assert.True(room.Right > room.Left, "and the throw is still on top of it");
    }

    [Fact]
    public void two_effects_take_the_widest_and_never_the_sum()
    {
        // They are drawn from the same silhouette into the same picture, so a
        // second effect does not push the first further out. Adding them would
        // grow the shape on every edit, which is the bug this whole file is
        // about wearing a different hat.
        string glow = "g(b=24.0000,c=FF00FF00)";

        var alone = ShapeEffectsRoom.Of(SoftShadow, PageW);
        var both = ShapeEffectsRoom.Of(SoftShadow + ":" + glow, PageW);

        Assert.Equal(alone.Left, both.Left, 9);
        Assert.Equal(alone.Right, both.Right, 9);
    }

    [Fact]
    public void a_field_with_no_colour_reserves_nothing()
    {
        // It paints nothing, so it is not an effect. render_core's parse makes
        // the same bargain, and a model that disagreed would inset by more than
        // the writer added.
        Assert.True(ShapeEffectsRoom.Of("s(a=45.0000,d=30.0000,b=24.0000)", PageW).IsEmpty);
    }

    [Fact]
    public void an_effect_this_build_cannot_name_still_reserves_its_room()
    {
        // render_core reserves for one it has never heard of, so the model has
        // to as well, or it insets by less than the writer added.
        var room = ShapeEffectsRoom.Of("q(b=24.0000,c=FF00FF00)", PageW);

        Assert.False(room.IsEmpty, "a field from a later build was given no room");
    }

    // ---------------- and what it fixes ----------------

    [Fact]
    public void a_shadow_does_not_make_the_shape_bigger()
    {
        // THE REPORTED SYMPTOM. The Skia overlay draws a gradient at exactly
        // this box, so a box that grew with the shadow is a fill that grew with
        // the shadow.
        var plain = ShapeFrom(Gradient).UprightGeometry;
        var shadowed = ShapeFrom(Gradient + ":" + Shadow).UprightGeometry;

        Assert.Equal(plain.Left, shadowed.Left, 9);
        Assert.Equal(plain.Top, shadowed.Top, 9);
        Assert.Equal(plain.Right, shadowed.Right, 9);
        Assert.Equal(plain.Bottom, shadowed.Bottom, 9);
    }

    [Fact]
    public void a_bigger_shadow_does_not_make_the_shape_bigger_still()
    {
        // "Grows incrementally" is what dragging a blur slider looked like.
        var small = ShapeFrom(Gradient + ":s(a=45.0000,d=6.0000,b=4.0000,p=0.0000,c=80000000)");
        var large = ShapeFrom(Gradient + ":s(a=45.0000,d=60.0000,b=48.0000,p=0.0000,c=80000000)");

        Assert.Equal(
            small.UprightGeometry.Right - small.UprightGeometry.Left,
            large.UprightGeometry.Right - large.UprightGeometry.Left,
            9);
        Assert.Equal(
            small.UprightGeometry.Bottom - small.UprightGeometry.Top,
            large.UprightGeometry.Bottom - large.UprightGeometry.Top,
            9);
    }

    [Fact]
    public void a_shadow_does_not_shift_the_shape_either()
    {
        // A shadow thrown one way opens two sides, so a box read straight off
        // /Rect does not merely grow, its CENTRE moves.
        var plain = ShapeFrom(Gradient).UprightGeometry;
        var shadowed = ShapeFrom(Gradient + ":" + Shadow).UprightGeometry;

        Assert.Equal(
            (plain.Left + plain.Right) / 2, (shadowed.Left + shadowed.Right) / 2, 9);
        Assert.Equal(
            (plain.Top + plain.Bottom) / 2, (shadowed.Top + shadowed.Bottom) / 2, 9);
    }

    [Fact]
    public void the_gradient_the_overlay_paints_is_the_shape_and_not_the_shadow()
    {
        // End to end, through the thing that actually draws: the overlay's item
        // must span the shape, not the rectangle the shadow needed. This is the
        // opaque fill that was covering the shadow.
        var page = DocumentModelBuilder.BuildPage(
            0, new List<AnnotationSnapshot> { Snapshot(Gradient + ":" + SoftShadow) }, PageW);

        var item = Assert.Single(GradientOverlay.ItemsFor(page));

        double left = double.MaxValue, right = double.MinValue;
        foreach (var (x, _) in item.Points)
        {
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }

        var bare = ShapeFrom(Gradient).UprightGeometry;

        // Within the stroke's own half-width, which the outline legitimately
        // carries and the upright box does not.
        Assert.InRange(left, bare.Left - 0.01, bare.Left + 0.01);
        Assert.InRange(right, bare.Right - 0.01, bare.Right + 0.01);
    }

    [Fact]
    public void a_turned_shape_with_a_shadow_is_not_dragged_off_its_centre()
    {
        // The rotated branch reconstructs the shape about the CENTRE of /Rect,
        // and a shadow thrown one way moves that centre. Taking the room off
        // before the reconstruction is what keeps it honest.
        string turned = "AyaanShape:0:FF000000:2.0000:0:0:30.0000:0:0.0000:120.0000:90.0000";

        ShapeObject Read(string tail)
        {
            var room = ShapeEffectsRoom.Of(tail, PageW);
            var page = DocumentModelBuilder.BuildPage(
                0,
                new List<AnnotationSnapshot>
                {
                    new(0, 2,
                        Bare.Left - room.Left, Bare.Top - room.Top,
                        Bare.Right + room.Right, Bare.Bottom + room.Bottom,
                        1.0, Guid.NewGuid(), turned + ":" + tail),
                },
                PageW);

            return Assert.IsType<ShapeObject>(Assert.Single(page.Objects));
        }

        var plain = Read(Gradient).UprightGeometry;
        var shadowed = Read(Gradient + ":" + Shadow).UprightGeometry;

        Assert.Equal(
            (plain.Left + plain.Right) / 2, (shadowed.Left + shadowed.Right) / 2, 9);
        Assert.Equal(
            (plain.Top + plain.Bottom) / 2, (shadowed.Top + shadowed.Bottom) / 2, 9);
    }

    [Fact]
    public void a_blur_larger_than_the_shape_does_not_turn_the_box_inside_out()
    {
        // A tag can carry anything. A rectangle whose right is left of its left
        // is not a smaller shape, it is a broken one, and it would take
        // hit-testing and the renderer with it.
        var shape = ShapeFrom(Gradient + ":s(a=45.0000,d=0.0000,b=4000.0000,p=0.0000,c=80000000)");
        var box = shape.UprightGeometry;

        Assert.True(box.Right >= box.Left, $"right {box.Right} is left of left {box.Left}");
        Assert.True(box.Bottom >= box.Top, $"bottom {box.Bottom} is above top {box.Top}");
    }
}
