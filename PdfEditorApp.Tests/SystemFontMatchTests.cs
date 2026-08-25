using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The installed font that stands in for a PDF's own.
///
/// Needed because real documents SUBSET their fonts: a page's Arial holds only
/// the glyphs that page already uses, so typing a genuinely new word into one
/// was measured to silently drop the letters it lacks. Where the document's own
/// font cannot spell a replacement, the core rebuilds the word in the font this
/// finds.
///
/// It is not a substitute in the usual sense. The PDF names the typeface it was
/// set in and this returns THAT typeface from the machine's own copy, which is
/// why replacing words with themselves through it was measured pixel-identical
/// in six of seven typefaces.
/// </summary>
public class SystemFontMatchTests
{
    [Theory]
    [InlineData("AAAAAA+ArialMT", "arial.ttf")]
    [InlineData("BAAAAA+Arial-BoldMT", "arialbd.ttf")]
    [InlineData("CAAAAA+Arial-ItalicMT", "ariali.ttf")]
    [InlineData("DAAAAA+Arial-BoldItalicMT", "arialbi.ttf")]
    [InlineData("EAAAAA+TimesNewRomanPSMT", "times.ttf")]
    [InlineData("FAAAAA+TimesNewRomanPS-BoldMT", "timesbd.ttf")]
    [InlineData("GAAAAA+TimesNewRomanPS-ItalicMT", "timesi.ttf")]
    [InlineData("HAAAAA+CourierNewPSMT", "cour.ttf")]
    [InlineData("IAAAAA+ComicSansMS", "comic.ttf")]
    [InlineData("JAAAAA+Consolas", "consola.ttf")]
    [InlineData("KAAAAA+Georgia-Italic", "georgiai.ttf")]
    public void the_named_typeface_is_matched_through_its_subset_tag(string baseFont, string expected)
    {
        Assert.Equal(expected, SystemFontMatch.FileNameFor(baseFont));
    }

    [Fact]
    public void the_narrow_cut_is_not_swallowed_by_plain_arial()
    {
        // ORDER TRAP. "arialnarrow" contains "arial", so a table that tested for
        // Arial first would hand back the wrong width for every narrow word.
        Assert.Equal("ARIALN.TTF", SystemFontMatch.FileNameFor("AAAAAA+ArialNarrow"));
        Assert.Equal("ARIALNB.TTF", SystemFontMatch.FileNameFor("AAAAAA+ArialNarrow-Bold"));
    }

    [Fact]
    public void a_font_with_no_subset_tag_still_matches()
    {
        // Standard-14 fonts are not embedded and carry no tag at all.
        Assert.Equal("arial.ttf", SystemFontMatch.FileNameFor("Helvetica"));
        Assert.Equal("arialbd.ttf", SystemFontMatch.FileNameFor("Helvetica-Bold"));
        Assert.Equal("times.ttf", SystemFontMatch.FileNameFor("Times-Roman"));
    }

    [Fact]
    public void a_symbolic_font_matches_nothing()
    {
        // MEASURED, not assumed: rewriting a word set in Wingdings read back
        // EMPTY. Its glyphs are pictures and the letters of a replacement do not
        // map onto them, so there is no stand-in that would be honest.
        Assert.Null(SystemFontMatch.FileNameFor("FAAAAA+Wingdings-Regular"));
        Assert.Null(SystemFontMatch.FileNameFor("Webdings"));
    }

    [Fact]
    public void a_font_this_app_cannot_place_matches_nothing()
    {
        // Refusing is the point. A near-enough face would change how the
        // reader's document looks for reasons they never asked for.
        Assert.Null(SystemFontMatch.FileNameFor("AAAAAA+SomeFoundryDisplay-Ultra"));
        Assert.Null(SystemFontMatch.FileNameFor(""));
        Assert.Null(SystemFontMatch.FileNameFor(null));
        Assert.Null(SystemFontMatch.FileNameFor("   "));
    }

    [Fact]
    public void a_font_with_no_name_at_all_matches_nothing()
    {
        // Measured on a real document: PDFium reports an empty font name for
        // some objects, which leaves nothing to match against.
        Assert.Null(SystemFontMatch.PathFor(""));
        Assert.Null(SystemFontMatch.PathFor(null));
    }

    [Fact]
    public void a_matched_font_resolves_to_a_file_that_is_really_there()
    {
        // The tables name the files a normal Windows carries; a machine missing
        // one must refuse the edit rather than hand the core an unreadable path.
        string? path = SystemFontMatch.PathFor("AAAAAA+Arial-BoldMT");

        Assert.NotNull(path);
        Assert.True(System.IO.File.Exists(path));
    }
}
