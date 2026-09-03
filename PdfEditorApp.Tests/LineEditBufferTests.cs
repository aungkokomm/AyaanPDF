using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What a keystroke means while a line of the page's own text is being edited
/// in place.
///
/// ⚠️ THE UNCHANGED PREFIX IS THE ONE TO WATCH. Editing must feel like editing
/// the page itself, which means the page must not be painted over while it is
/// being edited. Everything before the first changed character is still exactly
/// what the page already draws, so it stays as real page pixels. If that count
/// is ever wrong, the page gets covered where it did not need to be, and the
/// reader sees drawn text where they should be seeing their own document.
/// </summary>
public class LineEditBufferTests
{
    [Fact]
    public void an_edit_that_has_changed_nothing_covers_nothing()
    {
        var buffer = new LineEditBuffer("Title Page", 4);

        Assert.False(buffer.IsChanged);
        Assert.Equal("Title Page", buffer.Text);
        Assert.Equal(4, buffer.Caret);

        // ⚠️ THE WHOLE LINE IS UNCHANGED, so nothing is drawn over the page at
        // the moment an edit begins. This is what makes clicking into text look
        // like nothing happened except a caret appearing.
        Assert.Equal("Title Page".Length, buffer.UnchangedPrefix);
    }

    [Fact]
    public void typing_puts_the_text_in_at_the_caret()
    {
        var buffer = new LineEditBuffer("Title Page", 5);
        buffer.Insert("d");

        Assert.Equal("Titled Page", buffer.Text);
        Assert.Equal(6, buffer.Caret);
        Assert.True(buffer.IsChanged);
    }

    /// <summary>
    /// ⚠️ TYPING AT THE END LEAVES THE WHOLE LINE ALONE. The prefix stays as
    /// page pixels right up to the insertion point, so the reader's own type is
    /// never replaced by a drawn imitation of it.
    /// </summary>
    [Fact]
    public void only_the_tail_from_the_change_stops_being_page_pixels()
    {
        var buffer = new LineEditBuffer("Title Page", 10);
        buffer.Insert("s");

        Assert.Equal("Title Pages", buffer.Text);
        Assert.Equal(10, buffer.UnchangedPrefix);

        var early = new LineEditBuffer("Title Page", 0);
        early.Insert("A");
        Assert.Equal("ATitle Page", early.Text);
        Assert.Equal(0, early.UnchangedPrefix);
    }

    [Fact]
    public void backspace_removes_the_character_before_the_caret()
    {
        var buffer = new LineEditBuffer("Title Page", 5);
        buffer.Backspace();

        Assert.Equal("Titl Page", buffer.Text);
        Assert.Equal(4, buffer.Caret);
        Assert.Equal(4, buffer.UnchangedPrefix);
    }

    [Fact]
    public void delete_removes_the_character_after_the_caret()
    {
        var buffer = new LineEditBuffer("Title Page", 0);
        buffer.Delete();

        Assert.Equal("itle Page", buffer.Text);
        Assert.Equal(0, buffer.Caret);
    }

    [Fact]
    public void the_ends_of_the_line_are_a_wall()
    {
        var start = new LineEditBuffer("abc", 0);
        start.Backspace();
        start.MoveLeft();
        Assert.Equal("abc", start.Text);
        Assert.Equal(0, start.Caret);

        var end = new LineEditBuffer("abc", 3);
        end.Delete();
        end.MoveRight();
        Assert.Equal("abc", end.Text);
        Assert.Equal(3, end.Caret);
    }

    [Fact]
    public void the_caret_walks_and_jumps()
    {
        var buffer = new LineEditBuffer("Title Page", 0);

        buffer.MoveRight();
        buffer.MoveRight();
        Assert.Equal(2, buffer.Caret);

        buffer.MoveLeft();
        Assert.Equal(1, buffer.Caret);

        buffer.MoveEnd();
        Assert.Equal(10, buffer.Caret);

        buffer.MoveHome();
        Assert.Equal(0, buffer.Caret);
    }

    [Fact]
    public void a_click_can_place_the_caret_and_cannot_place_it_outside()
    {
        var buffer = new LineEditBuffer("Title Page", 0);

        buffer.PlaceCaret(6);
        Assert.Equal(6, buffer.Caret);

        buffer.PlaceCaret(-4);
        Assert.Equal(0, buffer.Caret);

        buffer.PlaceCaret(9999);
        Assert.Equal(10, buffer.Caret);
    }

    [Fact]
    public void typing_back_what_was_removed_makes_the_line_unchanged_again()
    {
        var buffer = new LineEditBuffer("Title Page", 5);
        buffer.Backspace();
        Assert.True(buffer.IsChanged);

        buffer.Insert("e");
        Assert.False(buffer.IsChanged);
        Assert.Equal("Title Page", buffer.Text);

        // And the page goes back to being entirely its own pixels.
        Assert.Equal("Title Page".Length, buffer.UnchangedPrefix);
    }

    // ---------------- selecting ----------------
    //
    // ⚠️ WITHOUT THIS THE EDITOR IS NOT ONE. A caret and backspace alone means
    // the only way to change a word is to delete it letter by letter and retype
    // it, which is what the reader hit as soon as they tried to use it.

    [Fact]
    public void a_fresh_edit_has_a_caret_and_no_selection()
    {
        var buffer = new LineEditBuffer("Title Page", 4);

        Assert.False(buffer.HasSelection);
        Assert.Equal(0, buffer.SelectionLength);
        Assert.Equal(string.Empty, buffer.SelectedText);
        Assert.Equal(buffer.Caret, buffer.Anchor);
    }

    [Fact]
    public void holding_shift_drags_the_caret_and_leaves_the_anchor()
    {
        var buffer = new LineEditBuffer("Title Page", 0);

        buffer.MoveRight(extend: true);
        buffer.MoveRight(extend: true);
        buffer.MoveRight(extend: true);
        buffer.MoveRight(extend: true);
        buffer.MoveRight(extend: true);

        Assert.True(buffer.HasSelection);
        Assert.Equal(0, buffer.Anchor);
        Assert.Equal(5, buffer.Caret);
        Assert.Equal("Title", buffer.SelectedText);
    }

    /// <summary>
    /// ⚠️ SELECTING BACKWARDS IS THE SAME SELECTION. The caret ends up before
    /// the anchor, and everything that reads the selection has to see the same
    /// range either way or a backwards drag would delete the wrong text.
    /// </summary>
    [Fact]
    public void selecting_backwards_gives_the_same_range()
    {
        var buffer = new LineEditBuffer("Title Page", 5);

        buffer.MoveLeft(extend: true);
        buffer.MoveLeft(extend: true);

        Assert.Equal(3, buffer.SelectionStart);
        Assert.Equal(5, buffer.SelectionEnd);
        Assert.Equal("le", buffer.SelectedText);
        Assert.True(buffer.Caret < buffer.Anchor);
    }

    [Fact]
    public void shift_home_and_shift_end_reach_the_ends()
    {
        var buffer = new LineEditBuffer("Title Page", 6);

        buffer.MoveEnd(extend: true);
        Assert.Equal("Page", buffer.SelectedText);

        var back = new LineEditBuffer("Title Page", 5);
        back.MoveHome(extend: true);
        Assert.Equal("Title", back.SelectedText);
    }

    [Fact]
    public void select_all_takes_the_whole_line()
    {
        var buffer = new LineEditBuffer("Title Page", 3);
        buffer.SelectAll();

        Assert.Equal("Title Page", buffer.SelectedText);
        Assert.Equal(0, buffer.SelectionStart);
        Assert.Equal(10, buffer.SelectionEnd);
    }

    /// <summary>
    /// ⚠️ THE ONE THE READER ASKED FOR. Selecting a word and typing over it is
    /// how a word gets changed; deleting it letter by letter is not editing.
    /// </summary>
    [Fact]
    public void typing_replaces_what_is_selected()
    {
        var buffer = new LineEditBuffer("Title Page", 0);
        buffer.SelectWordAt(0);
        Assert.Equal("Title", buffer.SelectedText);

        buffer.Insert("Cover");

        Assert.Equal("Cover Page", buffer.Text);
        Assert.Equal(5, buffer.Caret);
        Assert.False(buffer.HasSelection);
    }

    [Fact]
    public void backspace_and_delete_take_the_selection_whole()
    {
        var back = new LineEditBuffer("Title Page", 0);
        back.SelectWordAt(0);
        back.Backspace();
        Assert.Equal(" Page", back.Text);
        Assert.Equal(0, back.Caret);
        Assert.False(back.HasSelection);

        var forward = new LineEditBuffer("Title Page", 0);
        forward.SelectWordAt(0);
        forward.Delete();
        Assert.Equal(" Page", forward.Text);
        Assert.False(forward.HasSelection);
    }

    [Fact]
    public void a_double_click_selects_the_word_under_it()
    {
        var buffer = new LineEditBuffer("Also by Yuval Noah Harari", 0);

        buffer.SelectWordAt(9);        // inside "Yuval"
        Assert.Equal("Yuval", buffer.SelectedText);

        buffer.SelectWordAt(0);
        Assert.Equal("Also", buffer.SelectedText);

        buffer.SelectWordAt(24);       // the last letter
        Assert.Equal("Harari", buffer.SelectedText);
    }

    /// <summary>
    /// An arrow key with a selection up collapses it to the side it moved
    /// towards, rather than moving one character from wherever the caret was.
    /// </summary>
    [Fact]
    public void an_arrow_key_collapses_a_selection_to_the_side_it_moves()
    {
        var left = new LineEditBuffer("Title Page", 0);
        left.SelectWordAt(0);
        left.MoveLeft();
        Assert.False(left.HasSelection);
        Assert.Equal(0, left.Caret);

        var right = new LineEditBuffer("Title Page", 0);
        right.SelectWordAt(0);
        right.MoveRight();
        Assert.False(right.HasSelection);
        Assert.Equal(5, right.Caret);
    }

    [Fact]
    public void a_click_clears_a_selection_and_a_shift_click_extends_it()
    {
        var buffer = new LineEditBuffer("Title Page", 0);
        buffer.SelectAll();

        buffer.PlaceCaret(3);
        Assert.False(buffer.HasSelection);
        Assert.Equal(3, buffer.Caret);

        buffer.PlaceCaret(8, extend: true);
        Assert.True(buffer.HasSelection);
        Assert.Equal(3, buffer.Anchor);
        Assert.Equal("le Pa", buffer.SelectedText);
    }

    /// <summary>
    /// Replacing a selection that starts mid-line still leaves everything
    /// before it as the page's own pixels.
    /// </summary>
    [Fact]
    public void replacing_a_selection_leaves_the_text_before_it_untouched()
    {
        var buffer = new LineEditBuffer("Title Page", 0);
        buffer.PlaceCaret(6);
        buffer.MoveEnd(extend: true);
        Assert.Equal("Page", buffer.SelectedText);

        buffer.Insert("Cover");

        Assert.Equal("Title Cover", buffer.Text);
        Assert.Equal(6, buffer.UnchangedPrefix);
    }

    // ---------------- complex scripts ----------------

    /// <summary>
    /// ⚠️ BACKSPACE DELETES A CHARACTER THE READER WOULD RECOGNISE, not one
    /// UTF-16 unit. This user writes Devanagari and Burmese, where a letter
    /// carrying combining marks is the ordinary case, and removing half of one
    /// leaves text that cannot be drawn at all.
    /// </summary>
    [Fact]
    public void backspace_takes_a_whole_combining_character()
    {
        // "ne" plus a combining acute: three UTF-16 units, two characters.
        var buffer = new LineEditBuffer("né", 3);
        buffer.Backspace();

        Assert.Equal("n", buffer.Text);
        Assert.Equal(1, buffer.Caret);
    }

    [Fact]
    public void backspace_takes_a_whole_surrogate_pair()
    {
        // One emoji: two UTF-16 units, one character.
        var buffer = new LineEditBuffer("a\U0001F600", 3);
        buffer.Backspace();

        Assert.Equal("a", buffer.Text);
        Assert.Equal(1, buffer.Caret);
    }

    [Fact]
    public void the_caret_steps_over_a_combining_character_in_one_move()
    {
        var buffer = new LineEditBuffer("néx", 1);

        buffer.MoveRight();
        Assert.Equal(3, buffer.Caret);   // past the e and its mark together

        buffer.MoveLeft();
        Assert.Equal(1, buffer.Caret);
    }

    [Fact]
    public void delete_forwards_takes_a_whole_combining_character()
    {
        var buffer = new LineEditBuffer("néx", 1);
        buffer.Delete();

        Assert.Equal("nx", buffer.Text);
        Assert.Equal(1, buffer.Caret);
    }
}
