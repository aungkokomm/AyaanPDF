using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A shape's paint, out to the tag and back.
///
/// The gradient shares the tag's tail with the effects and is not one of them,
/// which is a claim with two halves: it has to survive everything the tail
/// survives, and it must never be handled as an effect. The Rust side proves
/// the first half against the real core; these prove the second, and prove the
/// arithmetic of the field itself.
/// </summary>
public class ShapeFillTagTests
{
    private static readonly RenderColor Red = new(0xFF, 0xFF, 0x00, 0x00);
    private static readonly RenderColor Blue = new(0xFF, 0x00, 0x00, 0xFF);

    /// <summary>Left to right across the shape's box, red to blue.</summary>
    private static GradientFill Across => new(Red, Blue, 0, 0.5, 1, 0.5);

    private static ShapeTag TagWith(string? fillHex = null, string effects = "") =>
        new(ShapeKind.Rectangle, "#FF000000", 1, false, false, 0, fillHex, 0,
            EffectsText: effects);

    // ---------------- what a shape is filled with ----------------

    [Fact]
    public void a_shape_with_no_fill_and_no_tail_is_filled_with_nothing()
    {
        var fill = ShapeFillTag.From(TagWith());

        Assert.True(fill.IsEmpty);
        Assert.Null(fill.Solid);
        Assert.Null(fill.Gradient);
    }

    [Fact]
    public void a_solid_fill_still_comes_from_the_field_it_always_came_from()
    {
        // The positional field, untouched. A shape written before gradients
        // existed has to read exactly as it did.
        var fill = ShapeFillTag.From(TagWith(fillHex: "#803B82F6"));

        Assert.Equal(new RenderColor(0x80, 0x3B, 0x82, 0xF6), fill.Solid);
        Assert.Null(fill.Gradient);
    }

    [Fact]
    public void a_gradient_comes_off_the_tail_with_both_stops_and_all_four_numbers()
    {
        var fill = ShapeFillTag.From(TagWith(
            effects: "f(c=FFFF0000,c2=FF0000FF,x0=0.2500,y0=0.0000,x1=0.7500,y1=1.0000)"));

        Assert.NotNull(fill.Gradient);
        var g = fill.Gradient!.Value;

        Assert.Equal(Red, g.From);
        Assert.Equal(Blue, g.To);
        Assert.Equal(0.25, g.X0, 6);
        Assert.Equal(0.00, g.Y0, 6);
        Assert.Equal(0.75, g.X1, 6);
        Assert.Equal(1.00, g.Y1, 6);
    }

    [Fact]
    public void a_gradient_wins_over_a_solid_field_and_is_never_averaged_with_it()
    {
        // We never write both. A file may still arrive with both, and the more
        // specific paint is the one meant. There is deliberately no third
        // answer where the solid stands in for the gradient.
        var fill = ShapeFillTag.From(TagWith(
            fillHex: "#FF808080",
            effects: "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)"));

        Assert.NotNull(fill.Gradient);
        Assert.Null(fill.Solid);
    }

    // ---------------- and what it writes ----------------

    [Fact]
    public void nothing_and_a_solid_colour_both_write_no_tail_field()
    {
        // A solid fill lives in the positional field. Writing it twice would be
        // two places to keep in step and a shape that could disagree with
        // itself.
        Assert.Equal("", ShapeFillTag.FieldOf(ShapeFill.None));
        Assert.Equal("", ShapeFillTag.FieldOf(ShapeFill.Of(Red)));
    }

    [Fact]
    public void a_gradient_survives_the_trip_out_and_back()
    {
        var before = ShapeFill.Of(new GradientFill(
            new RenderColor(0x80, 0x11, 0x22, 0x33),
            new RenderColor(0xFF, 0x44, 0x55, 0x66),
            -0.25, 0.125, 1.5, 0.875));

        var after = ShapeFillTag.From(TagWith(effects: ShapeFillTag.FieldOf(before)));

        Assert.Equal(before.Gradient, after.Gradient);
    }

    [Fact]
    public void an_endpoint_outside_the_box_is_kept_rather_than_clamped()
    {
        // A gradient may begin before the shape and end after it, which is how
        // a partial ramp across a shape is expressed. Clamping would quietly
        // change the paint.
        var g = new GradientFill(Red, Blue, -1, 0, 2, 0);

        Assert.Equal(g, ShapeFillTag.From(TagWith(
            effects: ShapeFillTag.FieldOf(ShapeFill.Of(g)))).Gradient);
    }

    [Fact]
    public void a_gradient_of_no_length_is_not_a_gradient()
    {
        // Two endpoints in the same place have no direction. PDF divides by
        // that length and Skia degenerates, so it is refused at the model's own
        // door rather than at either renderer's.
        Assert.True(ShapeFill.Of(new GradientFill(Red, Blue, 0.4, 0.4, 0.4, 0.4)).IsEmpty);
        Assert.Equal("", ShapeFillTag.FieldOf(
            ShapeFill.Of(new GradientFill(Red, Blue, 0.4, 0.4, 0.4, 0.4))));
    }

    // ---------------- the core's rule about the colour key ----------------

    [Fact]
    public void a_gradient_starting_from_nothing_is_written_the_other_way_round()
    {
        // render_core reads a field's colour key as the thing that says the
        // field exists, and REFUSES THE WHOLE EDIT when a field will not parse.
        // A c=00000000 would therefore not be ignored: it would make every
        // shadow and glow edit on that shape fail silently. Reversing a
        // gradient is the identical paint, so nothing is lost by doing it.
        var transparent = new RenderColor(0, 0, 0, 0);
        var written = ShapeFillTag.FieldOf(
            ShapeFill.Of(new GradientFill(transparent, Blue, 0, 0, 1, 0)));

        Assert.DoesNotContain("c=00000000", written);
        Assert.StartsWith("f(c=FF0000FF,", written);

        // And it reads back as the same gradient, described from the other end.
        var read = ShapeFillTag.From(TagWith(effects: written)).Gradient!.Value;

        Assert.Equal(Blue, read.From);
        Assert.Equal(transparent, read.To);
        Assert.Equal(1.0, read.X0, 6);
        Assert.Equal(0.0, read.X1, 6);
    }

    [Fact]
    public void a_gradient_from_nothing_to_nothing_writes_no_field_at_all()
    {
        var nothing = new RenderColor(0, 0, 0, 0);

        Assert.Equal("", ShapeFillTag.FieldOf(
            ShapeFill.Of(new GradientFill(nothing, nothing, 0, 0, 1, 0))));
    }

    // ---------------- it is not an effect ----------------

    [Fact]
    public void the_fill_letter_is_not_one_an_effect_uses()
    {
        // One tail, two families of field. A collision would make a gradient
        // read as a shadow and be drawn as one.
        string tail = ShapeEffectsTag.TextOf(
            new ShapeEffects(
                new DropShadow(135, 0.02, Red, 0.01).ToSpec(),
                new Glow(Blue, 0.01).ToSpec()),
            612);

        foreach (string field in tail.Split(':'))
        {
            Assert.NotEqual(ShapeFillTag.Letter, field[0]);
        }
    }

    [Fact]
    public void the_effects_reader_does_not_also_carry_the_fill_field()
    {
        // THE DOUBLE-WRITE THIS PREVENTS. If the effects reader carried the
        // gradient as a field it did not understand, the next edit would emit
        // it once from the fill and once from the carried list.
        var tag = TagWith(effects:
            "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)"
            + ":s(a=135.00,d=6.0000,b=0.0000,p=0.0000,c=FF000000)");

        var effects = ShapeEffectsTag.From(tag, 612);

        Assert.NotNull(effects);
        Assert.Empty(effects!.Carried);
        Assert.NotNull(effects.Shadow);

        string tail = ShapeFillTag.TailOf(ShapeFillTag.From(tag), effects, 612);
        string[] fields = tail.Split(':');

        Assert.Equal(2, fields.Length);
        Assert.Single(fields, f => f[0] == ShapeFillTag.Letter);
    }

    [Fact]
    public void a_tag_whose_only_tail_field_is_the_fill_records_no_effects()
    {
        Assert.Null(ShapeEffectsTag.From(
            TagWith(effects: "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000)"),
            612));
    }

    [Fact]
    public void an_f_field_that_does_not_read_is_carried_rather_than_dropped()
    {
        // It is our letter, but not something this build can make sense of. It
        // is not a gradient, so nothing writes it back from the fill, and the
        // only way it survives is for the effects reader to carry it verbatim
        // like anybody else's field.
        var tag = TagWith(effects: "f(c=FFFF0000,zz=1)");

        Assert.Null(ShapeFillTag.From(tag).Gradient);

        var effects = ShapeEffectsTag.From(tag, 612);

        Assert.NotNull(effects);
        Assert.Equal(new[] { "f(c=FFFF0000,zz=1)" }, effects!.Carried);
    }

    [Fact]
    public void a_key_from_a_later_build_is_skipped_rather_than_fatal()
    {
        var fill = ShapeFillTag.From(TagWith(effects:
            "f(c=FFFF0000,c2=FF0000FF,x0=0.0000,y0=0.5000,x1=1.0000,y1=0.5000,radial=1)"));

        Assert.Equal(Across, fill.Gradient);
    }

    // ---------------- and its numbers are not lengths ----------------

    [Fact]
    public void the_gradients_numbers_are_fractions_and_are_never_rescaled()
    {
        // The C# mirror of the same claim the Rust suite makes against the real
        // core. Every length in the tail is converted between points,
        // normalized units and capture pixels; a fraction of the SHAPE's own
        // box is none of those, and a gradient whose endpoints were scaled by a
        // page width would collapse into the shape's left edge.
        string field = ShapeFillTag.FieldOf(ShapeFill.Of(Across));

        Assert.Equal(field, ShapeEffectsTag.ScaleFields(field, 1000));
        Assert.Equal(field, ShapeEffectsTag.ScaleFields(field, 1.0 / 612));
    }

    // ---------------- the whole tail ----------------

    [Fact]
    public void the_tail_is_the_fill_then_the_effects_and_either_may_be_absent()
    {
        var effects = new ShapeEffects(new Glow(Blue, 0.01).ToSpec());
        string glow = ShapeEffectsTag.TextOf(effects, 612);
        string paint = ShapeFillTag.FieldOf(ShapeFill.Of(Across));

        Assert.Equal(paint + ":" + glow, ShapeFillTag.TailOf(ShapeFill.Of(Across), effects, 612));
        Assert.Equal(paint, ShapeFillTag.TailOf(ShapeFill.Of(Across), null, 612));
        Assert.Equal(glow, ShapeFillTag.TailOf(ShapeFill.None, effects, 612));
        Assert.Equal("", ShapeFillTag.TailOf(ShapeFill.None, null, 612));
    }

    [Fact]
    public void a_solid_fill_never_reaches_the_tail_even_beside_an_effect()
    {
        var effects = new ShapeEffects(new Glow(Blue, 0.01).ToSpec());

        Assert.Equal(
            ShapeEffectsTag.TextOf(effects, 612),
            ShapeFillTag.TailOf(ShapeFill.Of(Red), effects, 612));
    }
}
