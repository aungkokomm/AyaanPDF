using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Define: which selections count as one English word, and how a word on the
/// page is found in the dictionary.
/// </summary>
/// <remarks>
/// ⚠️ ENGLISH ONLY. Ayaan's readers select Hindi and Burmese as often as
/// English, and the dictionary is English. Those selections must be refused
/// outright, never trimmed down to some Latin fragment and looked up.
/// </remarks>
public class DefineWordTests
{
    private const char Us = '';

    private static WordDefinitions Sample() => WordDefinitions.Load(new StringReader(string.Join("\n",
        "# sample",
        $"D\tbook\tn\ta written work or composition that has been published{Us}physical objects consisting of a number of pages bound together",
        "D\tbook\tv\tengage for a performance",
        $"D\trun\tv\tmove fast by using one's feet{Us}flee",
        "D\trun\tn\ta score in baseball",
        "D\trunning\tn\tthe act of running",
        "D\tgo\tv\tchange location",
        "D\tchild\tn\ta young person",
        "D\tstop\tv\tcome to a halt",
        "D\tbig\ta\tabove average in size",
        "D\thappy\ta\tenjoying well-being and contentment",
        "D\tquickly\tr\twith rapid movements",
        "D\tbox\tn\ta container",
        "D\twell-being\tn\ta contented state",
        "X\tv\twent\tgo",
        "X\tn\tchildren\tchild",
        "X\ta\thappier\thappy")));

    // ---------------- What counts as one English word ----------------

    [Theory]
    [InlineData("book", "book")]
    [InlineData("Book.", "Book")]
    [InlineData("  book  ", "book")]
    [InlineData("“book”", "book")]
    [InlineData("(running),", "running")]
    [InlineData("don't", "don't")]
    [InlineData("don’t", "don't")]
    [InlineData("well-being", "well-being")]
    [InlineData("e-mail-", "e-mail")]
    [InlineData("end…", "end")]
    public void a_single_english_word_is_accepted_with_its_punctuation_trimmed(string selected, string expected)
    {
        Assert.True(EnglishWord.TryNormalize(selected, out string word));
        Assert.Equal(expected, word);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("book\nshelf")]
    [InlineData("123")]
    [InlineData("b00k")]
    [InlineData("...")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmno")]
    public void anything_but_one_english_word_is_refused(string? selected)
    {
        Assert.False(EnglishWord.TryNormalize(selected, out _));
    }

    [Theory]
    [InlineData("नमस्ते")]
    [InlineData("पुस्तक")]
    [InlineData("မင်္ဂလာပါ")]
    [InlineData("စာအုပ်")]
    [InlineData("bookनमस्ते")]
    [InlineData("စာbook")]
    [InlineData("café")]
    public void hindi_myanmar_and_other_scripts_are_refused_not_trimmed_to_latin(string selected)
    {
        Assert.False(EnglishWord.TryNormalize(selected, out string word));
        Assert.Equal(string.Empty, word);
    }

    // ---------------- Finding the word in the dictionary ----------------

    [Fact]
    public void a_headword_returns_every_part_of_speech_in_file_order()
    {
        var found = Sample().Lookup("book");

        Assert.NotNull(found);
        Assert.Equal("book", found!.Word);
        Assert.Equal(2, found.Senses.Count);
        Assert.Equal(("book", "noun"), (found.Senses[0].Headword, found.Senses[0].PartOfSpeech));
        Assert.Equal(2, found.Senses[0].Definitions.Count);
        Assert.Equal("a written work or composition that has been published", found.Senses[0].Definitions[0]);
        Assert.Equal("verb", found.Senses[1].PartOfSpeech);
    }

    [Theory]
    [InlineData("Books", "book", "noun")]
    [InlineData("boxes", "box", "noun")]
    [InlineData("went", "go", "verb")]
    [InlineData("children", "child", "noun")]
    [InlineData("stopped", "stop", "verb")]
    [InlineData("bigger", "big", "adjective")]
    [InlineData("happier", "happy", "adjective")]
    [InlineData("quickly", "quickly", "adverb")]
    [InlineData("book's", "book", "noun")]
    [InlineData("book’s", "book", "noun")]
    [InlineData("well-being", "well-being", "noun")]
    public void a_word_as_written_on_the_page_finds_its_dictionary_form(string written, string headword, string pos)
    {
        var found = Sample().Lookup(written);

        Assert.NotNull(found);
        Assert.Equal((headword, pos), (found!.Senses[0].Headword, found.Senses[0].PartOfSpeech));
    }

    [Fact]
    public void a_word_that_is_itself_a_headword_comes_before_the_form_it_derives_from()
    {
        var found = Sample().Lookup("running");

        Assert.NotNull(found);
        Assert.Equal(("running", "noun"), (found!.Senses[0].Headword, found.Senses[0].PartOfSpeech));
        Assert.Contains(found.Senses, s => s.Headword == "run" && s.PartOfSpeech == "verb");
    }

    [Theory]
    [InlineData("xyzzy")]
    [InlineData("")]
    [InlineData("runn")]
    public void a_word_the_dictionary_does_not_know_returns_nothing(string word)
    {
        Assert.Null(Sample().Lookup(word));
    }

    [Fact]
    public void a_popup_never_shows_more_than_three_parts_of_speech()
    {
        var dictionary = WordDefinitions.Load(new StringReader(string.Join("\n",
            "D\tset\tn\ta group", "D\tset\tv\tput", "D\tset\ta\tfixed", "D\tset\tr\tsettled")));

        Assert.Equal(WordDefinitions.MaxSenses, dictionary.Lookup("set")!.Senses.Count);
    }

    // ---------------- Words that only look defined ----------------

    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("Me")]
    [InlineData("at")]
    [InlineData("who")]
    public void grammar_words_have_no_definition_whatever_the_file_holds(string word)
    {
        // Each of these IS in WordNet, as a symbol or an abbreviation.
        var dictionary = WordDefinitions.Load(new StringReader(string.Join("\n",
            "D\ta\tn\ta metric unit of length", "D\tme\tn\ta state in New England",
            "D\tat\tn\ta radioactive element", "D\twho\tn\ta United Nations agency")));

        Assert.Null(dictionary.Lookup(word));
    }

    [Fact]
    public void a_suffix_rule_never_leaves_an_abbreviation_sized_noun()
    {
        var dictionary = WordDefinitions.Load(new StringReader(string.Join("\n",
            "D\tha\tn\tthe angular distance of a celestial point", "D\tgo\tv\tchange location")));

        // "has" is not "ha", but "goes" is still "go": verbs keep two letters.
        Assert.Null(dictionary.Lookup("has"));
        Assert.Equal(("go", "verb"), (dictionary.Lookup("goes")!.Senses[0].Headword, dictionary.Lookup("goes")!.Senses[0].PartOfSpeech));
    }

    [Fact]
    public void an_example_travels_with_its_part_of_speech_and_is_optional()
    {
        var dictionary = WordDefinitions.Load(new StringReader(string.Join("\n",
            "D\tordinary\ta\tnot exceptional in any way\tan ordinary day",
            "D\tordinary\tn\ta judge of a probate court\t",
            "D\tbook\tn\ta written work")));

        var ordinary = dictionary.Lookup("ordinary")!;
        Assert.Equal("an ordinary day", ordinary.Senses[0].Example);
        Assert.Null(ordinary.Senses[1].Example);
        Assert.Null(dictionary.Lookup("book")!.Senses[0].Example);
    }

    // ---------------- The word under the pointer ----------------

    /// <summary>One line of text, every character ten units wide and twenty tall.</summary>
    private static PageTextLayer LineOf(string text) =>
        new(text.Select((c, i) => new CharGlyph(i * 10, 0, i * 10 + 10, 20, c)).ToList());

    [Theory]
    [InlineData("the ordinary sense", 55, 4, 8)]
    [InlineData("the ordinary sense", 45, 4, 8)]
    [InlineData("don't stop", 15, 0, 5)]
    [InlineData("well-being now", 75, 0, 10)]
    [InlineData("end- next", 5, 0, 3)]
    [InlineData("religion.", 75, 0, 8)]
    public void a_click_on_a_letter_finds_the_whole_word(string text, double x, int start, int length)
    {
        Assert.Equal((start, length), LineOf(text).WordAt(x, 10));
    }

    [Theory]
    [InlineData(35, 10)]
    [InlineData(55, 30)]
    [InlineData(500, 10)]
    public void a_click_on_a_space_below_the_line_or_past_its_end_finds_no_word(double x, double y)
    {
        Assert.Null(LineOf("the ordinary sense").WordAt(x, y));
    }

    [Theory]
    [InlineData("पढ़ो किताब")]
    [InlineData("စာအုပ် ဖတ်")]
    public void a_hindi_or_myanmar_word_comes_back_whole_and_is_then_refused(string text)
    {
        var layer = LineOf(text);
        int firstWord = text.IndexOf(' ');

        // A click on the LAST character of the first word, a vowel sign or
        // mark, still reaches back to the start.
        var range = layer.WordAt((firstWord - 1) * 10 + 5, 10);

        Assert.Equal((0, firstWord), range);
        Assert.False(EnglishWord.TryNormalize(layer.Text.Substring(0, firstWord), out _));
    }
}
