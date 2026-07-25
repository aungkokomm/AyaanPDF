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
    public void every_preset_is_named()
    {
        Assert.All(InkPresets.Colors, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.All(InkPresets.Widths, w => Assert.False(string.IsNullOrWhiteSpace(w.Name)));
        Assert.All(InkPresets.HighlightColors, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }
}
