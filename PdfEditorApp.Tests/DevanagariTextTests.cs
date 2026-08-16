using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Devanagari that came out of a PDF in drawing order, put back into reading
/// order.
///
/// Every case below with a "from the book" note is real: it was copied out of
/// the document that prompted this, so these are not invented examples of a
/// hypothetical fault.
/// </summary>
public class DevanagariTextTests
{
    [Theory]
    // From the book, all four of its misplaced signs.
    [InlineData("िपपासा", "पिपासा")]
    [InlineData("िवनाश", "विनाश")]
    [InlineData("िछपा", "छिपा")]
    [InlineData("िलए", "लिए")]
    public void a_sign_painted_before_its_consonant_goes_after_it(string drawn, string read)
    {
        Assert.Equal(read, DevanagariText.ToLogicalOrder(drawn));
    }

    [Fact]
    public void a_whole_sentence_from_the_book_comes_back_readable()
    {
        // The four words in their setting, so the repair is seen not to disturb
        // everything around them.
        const string drawn = "यह मनुष की गहरे में जो युद की िपपासा है";
        const string read = "यह मनुष की गहरे में जो युद की पिपासा है";

        Assert.Equal(read, DevanagariText.ToLogicalOrder(drawn));
    }

    [Theory]
    // ⚠️ The direction that matters most. In correct text the sign is ALSO
    // followed by a consonant, so a repair that swapped on that alone would
    // corrupt every properly encoded Hindi document it touched.
    [InlineData("किताब")]
    [InlineData("पिपासा")]
    [InlineData("विनाश")]
    [InlineData("हिंदी")]
    [InlineData("अधिकार")]
    public void correctly_written_text_is_left_exactly_as_it_is(string already)
    {
        Assert.Equal(already, DevanagariText.ToLogicalOrder(already));
    }

    [Fact]
    public void the_sign_clears_a_whole_conjunct_rather_than_landing_inside_it()
    {
        // Painted before "स्थ", it belongs after all of it. Landing after the
        // स would split the conjunct and make a different word.
        Assert.Equal("स्थिर", DevanagariText.ToLogicalOrder("िस्थर"));
    }

    [Fact]
    public void a_sign_with_no_consonant_behind_it_is_left_alone()
    {
        // Moving it somewhere would invent a word. Leaving it is the only
        // honest answer, and it keeps the repair from ever losing a character.
        Assert.Equal("ि", DevanagariText.ToLogicalOrder("ि"));
        Assert.Equal("ि ", DevanagariText.ToLogicalOrder("ि "));
        Assert.Equal("ि।", DevanagariText.ToLogicalOrder("ि।"));
    }

    [Fact]
    public void running_it_twice_changes_nothing_the_second_time()
    {
        // It is applied where titles are made, and a title can pass through
        // more than one of those places. Once a sign sits after its consonant
        // it no longer looks misplaced, which is what makes that safe.
        string once = DevanagariText.ToLogicalOrder("िपपासा और िवनाश");
        Assert.Equal(once, DevanagariText.ToLogicalOrder(once));
    }

    [Theory]
    [InlineData("Chapter One")]
    [InlineData("अध्याय एक")]
    [InlineData("အခန်း တစ်")]
    [InlineData("")]
    public void text_with_nothing_to_repair_comes_back_the_same_object(string untouched)
    {
        // Cheap as well as correct: the common case is every document not
        // written in this script, and it must not cost a rebuild of the string.
        Assert.Equal(untouched, DevanagariText.ToLogicalOrder(untouched));
    }

    [Fact]
    public void a_mark_stranded_after_a_word_space_moves_back_across_it()
    {
        // From the book: "नही ंजाता" for "नहीं जाता". Only the mark and the
        // space swap, so the space BETWEEN the words survives.
        Assert.Equal("नहीं जाता", DevanagariText.JoinStrandedMarks("नही ंजाता"));
    }

    [Fact]
    public void a_mark_with_no_word_in_front_of_the_space_is_left_alone()
    {
        // Nothing for it to belong to, so moving it would attach it to whatever
        // happened to be nearby.
        Assert.Equal(" ं", DevanagariText.JoinStrandedMarks(" ं"));
        Assert.Equal("x ं", DevanagariText.JoinStrandedMarks("x ं"));
    }

    [Fact]
    public void an_ordinary_space_between_words_is_not_disturbed()
    {
        Assert.Equal("यह मनुष की", DevanagariText.JoinStrandedMarks("यह मनुष की"));
    }

    [Fact]
    public void the_two_repairs_together_read_the_books_line_correctly()
    {
        Assert.Equal(
            "वह पिपासा है, नहीं जाता",
            DevanagariText.Repair("वह िपपासा है, नही ंजाता"));
    }

    [Fact]
    public void a_bookmark_named_after_a_selection_is_repaired_before_it_is_trimmed()
    {
        // Order matters: trimming a title to its first hundred characters could
        // cut between a stranded sign and its consonant, and then no later
        // repair could put them together again.
        Assert.Equal("पिपासा", OutlineEdits.TitleFrom("िपपासा", 0));
    }

    [Fact]
    public void the_private_use_characters_are_left_where_they_are()
    {
        // The other half of the same document's damage, and NOT this function's
        // to fix: a font that maps its conjuncts into private use space has not
        // recorded which characters they were. Dropping or guessing at them
        // would turn a title that is partly readable into one that is wrong.
        const string withPua = "धम\U00010014A\U000100150 में";

        Assert.Equal(withPua, DevanagariText.Repair(withPua));
    }
}
