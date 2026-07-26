using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class TextBoxPlacementTests
{
    [Fact]
    public void the_click_is_the_top_left_corner()
    {
        // Clicking to start typing puts the top-left of the text there, the way
        // a text cursor works, not the centre.
        var (left, top, _, _) = TextBoxPlacement.Compute(0.3, 0.4, "hi", 0.03);
        Assert.Equal(0.3, left, 9);
        Assert.Equal(0.4, top, 9);
    }

    [Fact]
    public void the_box_grows_down_and_right_from_the_click()
    {
        var (left, top, right, bottom) = TextBoxPlacement.Compute(0.2, 0.2, "hello", 0.03);
        Assert.True(right > left);
        Assert.True(bottom > top);
    }

    [Fact]
    public void a_longer_line_makes_a_wider_box()
    {
        var shortBox = TextBoxPlacement.Compute(0.1, 0.1, "hi", 0.03);
        var longBox = TextBoxPlacement.Compute(0.1, 0.1, "a much longer line of text", 0.03);
        Assert.True(longBox.Right - longBox.Left > shortBox.Right - shortBox.Left);
    }

    [Fact]
    public void more_lines_make_a_taller_box()
    {
        var one = TextBoxPlacement.Compute(0.1, 0.1, "one", 0.03);
        var three = TextBoxPlacement.Compute(0.1, 0.1, "one\ntwo\nthree", 0.03);
        Assert.True(three.Bottom - three.Top > one.Bottom - one.Top);
    }

    [Fact]
    public void a_bigger_font_makes_a_bigger_box()
    {
        var small = TextBoxPlacement.Compute(0.1, 0.1, "text", 0.02);
        var big = TextBoxPlacement.Compute(0.1, 0.1, "text", 0.05);
        Assert.True(big.Bottom - big.Top > small.Bottom - small.Top);
        Assert.True(big.Right - big.Left > small.Right - small.Left);
    }

    [Fact]
    public void an_empty_string_still_makes_a_real_rectangle()
    {
        // PDFium rejects a zero-size box, and an empty editor should still be a
        // place the cursor can sit rather than nothing at all.
        var (left, top, right, bottom) = TextBoxPlacement.Compute(0.5, 0.5, "", 0.03);
        Assert.True(right > left);
        Assert.True(bottom > top);
    }

    [Fact]
    public void the_box_is_measured_from_the_longest_line_not_the_last()
    {
        // A wide first line followed by a short one must not shrink the box to
        // the short one.
        var box = TextBoxPlacement.Compute(0.1, 0.1, "a very wide first line\nx", 0.03);
        var wideOnly = TextBoxPlacement.Compute(0.1, 0.1, "a very wide first line", 0.03);
        Assert.Equal(wideOnly.Right, box.Right, 6);
    }

    [Fact]
    public void every_preset_size_is_named_and_positive()
    {
        Assert.All(TextBoxPresets.Sizes, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.True(s.Value > 0);
        });
    }

    [Fact]
    public void the_preset_sizes_increase()
    {
        var values = TextBoxPresets.Sizes.Select(s => s.Value).ToList();
        for (int i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] > values[i - 1], $"size {i} is not larger than {i - 1}");
        }
    }
}
