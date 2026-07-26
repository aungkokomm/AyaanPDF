using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class InkPresetsTests
{
    [Fact]
    public void parses_argb_hex()
    {
        Assert.Equal((0xFF, 0xE0, 0x00, 0x00), InkPresets.ParseHex("#FFE00000"));
    }

    [Fact]
    public void parses_rgb_hex_using_the_supplied_alpha()
    {
        Assert.Equal((0x88, 0x12, 0x34, 0x56), InkPresets.ParseHex("#123456", defaultAlpha: 0x88));
    }

    [Fact]
    public void a_malformed_colour_does_not_throw()
    {
        // A bad colour must not take the app down in the middle of a stroke.
        Assert.Equal((0xFF, 0, 0, 0), InkPresets.ParseHex("#ZZZZZZ"));
        Assert.Equal((0xFF, 0, 0, 0), InkPresets.ParseHex(""));
        Assert.Equal((0xFF, 0, 0, 0), InkPresets.ParseHex("nonsense"));
    }

    [Fact]
    public void highlight_colours_are_all_translucent()
    {
        // An opaque highlighter would hide the text it marks.
        foreach (var c in InkPresets.HighlightColors)
        {
            var (a, _, _, _) = InkPresets.ParseHex(c.Hex);
            Assert.True(a < 0xFF, $"{c.Name} is opaque and would cover the text");
        }
    }

    [Fact]
    public void pen_colours_are_all_opaque()
    {
        foreach (var c in InkPresets.Colors)
        {
            var (a, _, _, _) = InkPresets.ParseHex(c.Hex);
            Assert.Equal(0xFF, a);
        }
    }

    [Fact]
    public void widths_are_ordered_and_normalized()
    {
        var values = InkPresets.Widths.Select(w => w.Value).ToList();
        Assert.Equal(values.OrderBy(v => v), values);

        // Normalized: a fraction of page width, never a pixel count.
        Assert.All(values, v => Assert.InRange(v, 0.0001, 0.1));
    }

    [Fact]
    public void defaults_come_from_the_offered_lists()
    {
        Assert.Contains(InkPresets.DefaultColor, InkPresets.Colors);
        Assert.Contains(InkPresets.DefaultWidth, InkPresets.Widths);
        Assert.Contains(InkPresets.DefaultHighlightColor, InkPresets.HighlightColors);
    }

    [Fact]
    public void every_pen_colour_maps_to_a_translucent_highlighter()
    {
        // The two lists are different lengths, so index pairing ran off the
        // end for the last pen colour and left the highlighter unchanged.
        // Every pen colour must yield a usable highlighter.
        foreach (var pen in InkPresets.Colors)
        {
            var h = InkPresets.HighlightFor(pen);
            Assert.Contains(h, InkPresets.HighlightColors);

            var (alpha, _, _, _) = InkPresets.ParseHex(h.Hex);
            Assert.True(alpha < 0xFF, $"{pen.Name} mapped to an opaque highlighter");
        }
    }

    [Fact]
    public void matching_pen_and_highlighter_names_pair_up()
    {
        var lime = InkPresets.Colors.Single(c => c.Name == "Lime");
        Assert.Equal("Lime", InkPresets.HighlightFor(lime).Name);
    }

    [Fact]
    public void the_four_highlighter_colours_are_the_requested_set()
    {
        Assert.Equal(
            new[] { "Lime", "Orange", "Pink", "Red" },
            InkPresets.HighlightColors.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void a_pen_colour_with_no_highlighter_falls_back_to_the_default()
    {
        // Black has no sensible highlighter equivalent.
        var black = InkPresets.Colors.Single(c => c.Name == "Black");
        Assert.Equal(InkPresets.DefaultHighlightColor, InkPresets.HighlightFor(black));
    }

    [Fact]
    public void the_highlighter_offers_the_highlighter_palette()
    {
        // The gap this closes: the four highlighter colours were correct, and
        // the test above proved it, while the picker on screen stayed bound to
        // the PEN list. Arming the highlighter showed opaque pen swatches, so
        // the requested colours were never reachable. Being right and being
        // reachable are different things, and only one of them was tested.
        Assert.Equal(InkPresets.HighlightColors, InkPresets.PaletteFor(highlighting: true));
        Assert.Equal(InkPresets.Colors, InkPresets.PaletteFor(highlighting: false));
    }

    [Fact]
    public void every_highlighter_colour_is_translucent()
    {
        // A highlighter that is opaque hides the text it is marking. The pen
        // colours are deliberately the opposite.
        foreach (var c in InkPresets.HighlightColors)
        {
            var (a, _, _, _) = InkPresets.ParseHex(c.Hex);
            Assert.True(a < 0xFF, $"{c.Name} is opaque ({c.Hex}) and would cover the text");
        }

        foreach (var c in InkPresets.Colors)
        {
            var (a, _, _, _) = InkPresets.ParseHex(c.Hex);
            Assert.Equal(0xFF, a);
        }
    }

    [Fact]
    public void every_preset_is_named()
    {
        Assert.All(InkPresets.Colors, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.All(InkPresets.Widths, w => Assert.False(string.IsNullOrWhiteSpace(w.Name)));
        Assert.All(InkPresets.HighlightColors, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    // ---- Opacity, as carried by the colour's alpha ----

    [Fact]
    public void opacity_changes_only_the_alpha()
    {
        // The RGB must survive untouched, or the opacity slider would also be
        // a colour picker.
        string half = InkPresets.WithOpacity("#FFE00000", 0.5);
        var (a, r, g, b) = InkPresets.ParseHex(half);

        Assert.Equal(0xE0, r);
        Assert.Equal(0x00, g);
        Assert.Equal(0x00, b);
        Assert.InRange(a, 127, 128);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.53)]
    [InlineData(1.0)]
    public void opacity_survives_a_round_trip(double opacity)
    {
        // The slider reads the value back out of the colour every time the tool
        // changes, so a lossy round trip would make it drift on each switch.
        string hex = InkPresets.WithOpacity("#FF7CB800", opacity);
        Assert.Equal(opacity, InkPresets.OpacityOf(hex), 2);
    }

    [Theory]
    [InlineData(-3.0, 0.0)]
    [InlineData(7.5, 1.0)]
    public void opacity_outside_the_range_is_clamped(double given, double expected)
    {
        // Out of range must clamp rather than wrap: a byte cast of 7.5*255
        // would come out near-transparent, i.e. the exact opposite of asked.
        string hex = InkPresets.WithOpacity("#FF1A1A1A", given);
        Assert.Equal(expected, InkPresets.OpacityOf(hex), 2);
    }

    [Fact]
    public void a_preset_reports_the_opacity_it_was_authored_with()
    {
        Assert.Equal(1.0, InkPresets.OpacityOf(InkPresets.DefaultColor.Hex), 2);
        Assert.True(InkPresets.OpacityOf(InkPresets.DefaultHighlightColor.Hex) < 1.0);
    }

    [Fact]
    public void the_result_is_always_a_full_eight_digit_colour()
    {
        // The overlay and the annotation writer both parse these back, and a
        // six-digit result would silently be read as fully opaque.
        foreach (var c in InkPresets.Colors)
        {
            string hex = InkPresets.WithOpacity(c.Hex, 0.4);
            Assert.Equal(9, hex.Length);
            Assert.StartsWith("#", hex);
        }
    }
}
