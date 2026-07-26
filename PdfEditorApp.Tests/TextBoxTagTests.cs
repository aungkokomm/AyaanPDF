using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class TextBoxTagTests
{
    // Builds a tag the way render_core's textbox_tag does, so the parser is
    // tested against the real on-disk format rather than its own idea of it.
    private static string Tag(string text, double sizePx, string rrggbbaa)
    {
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        return $"AyaanText:{sizePx.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}:{rrggbbaa}:{b64}";
    }

    [Fact]
    public void it_reads_back_the_words_the_size_and_the_colour()
    {
        Assert.True(TextBoxTagReader.TryParse(Tag("Hello", 20.0, "1A2B3CFF"), out var tag));
        Assert.Equal("Hello", tag.Text);
        Assert.Equal(20.0 / 1000.0, tag.FontSizeNorm, 6);
        Assert.Equal("#FF1A2B3C", tag.ColorHex);
    }

    [Theory]
    [InlineData("a:b:c")]
    [InlineData("line\nbreak")]
    [InlineData("10:30 meeting\nroom: 4B")]
    [InlineData("unicode ✓ é")]
    [InlineData("")]
    public void colons_and_newlines_in_the_text_survive(string text)
    {
        // The reason the text is base64 in the tag: a colon in the words must
        // never be read as a field separator.
        Assert.True(TextBoxTagReader.TryParse(Tag(text, 14.0, "000000FF"), out var tag));
        Assert.Equal(text, tag.Text);
    }

    [Fact]
    public void a_plain_comment_is_not_a_text_box()
    {
        // Someone else's annotation, or a note, must not be mistaken for one of
        // ours and re-edited as text.
        Assert.False(TextBoxTagReader.TryParse("Please review this", out _));
        Assert.False(TextBoxTagReader.TryParse("", out _));
        Assert.False(TextBoxTagReader.TryParse(null, out _));
    }

    [Fact]
    public void a_malformed_tag_is_rejected_rather_than_guessed()
    {
        Assert.False(TextBoxTagReader.TryParse("AyaanText:", out _));
        Assert.False(TextBoxTagReader.TryParse("AyaanText:14", out _));            // no colour or text
        Assert.False(TextBoxTagReader.TryParse("AyaanText:14:GGGGGGGG:aGk=", out _)); // bad hex
        Assert.False(TextBoxTagReader.TryParse("AyaanText:0:FFFFFFFF:aGk=", out _));  // zero size
        Assert.False(TextBoxTagReader.TryParse("AyaanText:14:FFFFFF:aGk=", out _));   // 6-digit colour
        Assert.False(TextBoxTagReader.TryParse("AyaanText:14:FFFFFFFF:not base64!", out _));
    }

    [Fact]
    public void the_size_comes_back_normalized_to_page_width()
    {
        // Stored at capture width 1000; the annotation layer works in fractions
        // of page width, so 30px must read as 0.03.
        Assert.True(TextBoxTagReader.TryParse(Tag("x", 30.0, "000000FF"), out var tag));
        Assert.Equal(0.030, tag.FontSizeNorm, 6);
    }
}
