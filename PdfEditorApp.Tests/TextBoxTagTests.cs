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

    [Fact]
    public void a_plain_box_defaults_to_left_with_no_fill_or_outline()
    {
        Assert.True(TextBoxTagReader.TryParse(Tag("hi", 20.0, "000000FF"), out var tag));
        Assert.Equal(TextAlign.Left, tag.Align);
        Assert.Equal("", tag.FillHex);
        Assert.Equal("", tag.OutlineHex);
    }

    // Builds a styled tag the way render_core's textbox_tag_styled does:
    // AyaanTextB:size:textRGBA:align:fillRGBA:outlineRGBA:outlineW:base64
    private static string Styled(string text, double sizePx, string textRgba, int align,
                                 string fillRgba, string outlineRgba, double outlineW)
    {
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return $"AyaanTextB:{sizePx.ToString("0.0000", ci)}:{textRgba}:{align}:{fillRgba}:{outlineRgba}:{outlineW.ToString("0.00", ci)}:{b64}";
    }

    [Fact]
    public void a_styled_box_reads_back_its_alignment_fill_and_outline()
    {
        string tagStr = Styled("body", 22.0, "1A1A1AFF", 3, "FFF7C8FF", "1565C0FF", 2.0);
        Assert.True(TextBoxTagReader.TryParse(tagStr, out var tag));

        Assert.Equal("body", tag.Text);
        Assert.Equal(TextAlign.Justify, tag.Align);
        Assert.Equal("#FF1A1A1A", tag.ColorHex);
        // RRGGBBAA in the tag becomes #AARRGGBB in the app.
        Assert.Equal("#FFFFF7C8", tag.FillHex);
        Assert.Equal("#FF1565C0", tag.OutlineHex);
        Assert.Equal(2.0 / 1000.0, tag.OutlineWidthNorm, 6);
    }

    [Fact]
    public void a_zero_alpha_fill_or_outline_reads_as_none()
    {
        // The writer stores "no fill" as alpha 00; it must come back empty, not
        // as a fully transparent colour that then draws nothing but is treated
        // as present.
        string tagStr = Styled("x", 20.0, "000000FF", 0, "FFFFFF00", "00000000", 0.0);
        Assert.True(TextBoxTagReader.TryParse(tagStr, out var tag));

        Assert.Equal(TextAlign.Left, tag.Align);
        Assert.Equal("", tag.FillHex);
        Assert.Equal("", tag.OutlineHex);
    }

    [Theory]
    [InlineData(0, TextAlign.Left)]
    [InlineData(1, TextAlign.Center)]
    [InlineData(2, TextAlign.Right)]
    [InlineData(3, TextAlign.Justify)]
    public void every_alignment_number_maps_to_its_value(int raw, TextAlign expected)
    {
        // These numbers cross the FFI boundary, so the enum order must match the
        // core's ALIGN_* constants.
        string tagStr = Styled("x", 20.0, "000000FF", raw, "00000000", "00000000", 0.0);
        Assert.True(TextBoxTagReader.TryParse(tagStr, out var tag));
        Assert.Equal(expected, tag.Align);
    }

    [Fact]
    public void colons_in_a_styled_box_text_still_survive()
    {
        // The styled format has six fixed fields then the base64 text; a colon
        // in the words must not be read as a seventh separator.
        string tagStr = Styled("10:30 meeting\nroom: 4B", 18.0, "000000FF", 1, "FFFFFFFF", "00000000", 0.0);
        Assert.True(TextBoxTagReader.TryParse(tagStr, out var tag));
        Assert.Equal("10:30 meeting\nroom: 4B", tag.Text);
    }
}
