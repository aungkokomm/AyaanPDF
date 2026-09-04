using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Picking the document's own text up and putting it down.
///
/// ⚠️ CHECKED IN THE SOURCE because the app is a WinUI project a test assembly
/// cannot load. What can be checked is that the gestures are ordered the way
/// they have to be, and that the commit does the things a whole-document
/// rewrite has to do.
/// </summary>
public class TextMoveWiringTests
{
    private static string Vm() => Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    // ---------------- the gesture ----------------

    /// <summary>
    /// ⚠️ THE PRESS DECIDES NOTHING, AND THAT IS THE WHOLE FIX. It used to arm
    /// the move AND begin the edit, so a drag had to take the caret back with
    /// CancelInPlaceEdit, which CLEARS THE TEXT SELECTION. The selection is
    /// what the move is committed against, so the commit found nothing to move
    /// and moved nothing, every single time, without a word.
    /// </summary>
    [Fact]
    public void the_press_only_arms_and_does_not_begin_an_edit_of_its_own()
    {
        string page = Page();

        int armed = page.IndexOf("ViewModel.BeginTextUnitMove(content.Page, nx, ny)", StringComparison.Ordinal);
        Assert.True(armed > 0, "nothing arms a move");

        // ⚠️ ELSE. A press that armed a move must NOT also begin an edit; the
        // bare `if` here is the whole defect, because the caret it puts in has
        // to be taken back the moment the pointer travels.
        Assert.Contains("else if (ViewModel.BeginInPlaceEdit(content.Page, nx, ny))",
            page, StringComparison.Ordinal);

        // And so nothing needs taking back: the pointer movement that carries
        // the text must not cancel an edit, because that call clears the text
        // selection the move is committed against.
        int carrying = page.IndexOf("ViewModel.UpdateTextUnitMove(mx, my)", StringComparison.Ordinal);
        Assert.True(carrying > 0);
        string moving = page[carrying..Math.Min(page.Length, carrying + 400)];
        Assert.DoesNotContain("CancelInPlaceEdit", moving, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ AND CARRYING COMES BEFORE SELECTING. A second press, once there is a
    /// caret in the line, drags to select; without this ordering the pointer
    /// movement that carries the text would be read the same way.
    /// </summary>
    [Fact]
    public void moving_the_text_is_tested_before_selecting_through_it()
    {
        string page = Page();

        int carrying = page.IndexOf("ViewModel.UpdateTextUnitMove(mx, my)", StringComparison.Ordinal);
        int selecting = page.IndexOf("_inPlaceDragging && ViewModel.IsEditingInPlace", StringComparison.Ordinal);

        Assert.True(carrying > 0, "nothing updates a move");
        Assert.True(selecting > 0);
        Assert.True(carrying < selecting,
            "dragging would select through the text instead of carrying it");
    }

    /// <summary>
    /// ⚠️ THE CLICK STILL HAS TO HAPPEN, on the way up, at the point the button
    /// went DOWN. It is how a caret gets into a line and therefore how the
    /// reader edits anything at all, and the keyboard has to follow it.
    /// </summary>
    [Fact]
    public void a_press_that_never_travelled_still_puts_a_caret_in_the_line()
    {
        string body = Method(Vm(), "public bool ReleaseTextUnitPress(", 900);

        Assert.Contains("_textMove.IsDragging", body, StringComparison.Ordinal);
        Assert.Contains("CommitTextUnitMove(oneLineOnly)", body, StringComparison.Ordinal);

        // ⚠️ FROM THE PRESS, NOT FROM THE RELEASE. A pointer that has drifted
        // a couple of points would otherwise put the caret at a different
        // character than the one the reader aimed at.
        Assert.Contains("_textMove.FromX, _textMove.FromY", body, StringComparison.Ordinal);
        Assert.Contains("BeginInPlaceEdit(page, x, y)", body, StringComparison.Ordinal);

        Assert.Contains("RootGrid.Focus(FocusState.Programmatic)",
            Method(Page(), "ViewModel.ReleaseTextUnitPress(IsAltDown())", 200),
            StringComparison.Ordinal);
    }

    /// <summary>The held modifier is what asks for one line instead of the block.</summary>
    [Fact]
    public void releasing_commits_the_move_and_alt_asks_for_one_line()
    {
        string page = Page();
        Assert.Contains("ViewModel.ReleaseTextUnitPress(IsAltDown())", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ CAPTURE CAN BE LOST WITHOUT A RELEASE, and a move is only real once
    /// the button comes up. Without this the frame is left floating at an
    /// offset over text that never went anywhere.
    /// </summary>
    [Fact]
    public void losing_the_pointer_puts_the_text_back()
    {
        string body = Method(Page(), "private void ResetPointerInteraction()", 3200);
        Assert.Contains("ViewModel.CancelTextUnitMove();", body, StringComparison.Ordinal);
    }

    // ---------------- what the reader can see ----------------

    /// <summary>
    /// ⚠️ THE POINTER IS THE AFFORDANCE. The frame says WHICH text; only the
    /// cursor changing as it crosses the rule says the text can be picked up at
    /// all, and it says so before the reader has committed to anything.
    ///
    /// Asked of the same predicate the press uses, so a pointer promising a
    /// move where a press would not make one is impossible by construction.
    /// </summary>
    [Fact]
    public void the_pointer_offers_the_move_before_the_reader_tries_it()
    {
        string body = Method(Page(), "InputSystemCursorShape? HoverCursor(", 3000);

        int text = body.IndexOf("ViewModel.CanMoveTextUnitAt(content.Page, nx, ny)", StringComparison.Ordinal);
        int grips = body.IndexOf("ViewModel.GripUnder(content.Page, nx, ny)", StringComparison.Ordinal);

        Assert.True(text > 0, "the framed text offers no cursor of its own");
        Assert.True(grips > 0);
        Assert.True(text < grips,
            "an annotation grip would answer for text the press takes first");

        // ⚠️ AND IT KEEPS IT. Carrying text takes the pointer out of the box it
        // was picked up in almost at once, and asking where the pointer is NOW
        // would put the I-beam back halfway through the move.
        Assert.Contains("ViewModel.IsMovingTextUnit", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ AND WHERE IT CAME FROM, while it is being carried. A frame under the
    /// pointer says where the text is going and nothing about how far that is
    /// from where the text still is.
    /// </summary>
    [Fact]
    public void a_ghost_stays_behind_at_the_place_the_text_is_leaving()
    {
        string vm = Vm();
        Assert.Contains("GhostUnitColor", vm, StringComparison.Ordinal);

        // Added BEFORE the frame that is being carried, because the overlay is
        // a Grid and what goes in first goes underneath.
        int ghost = vm.IndexOf("GhostUnitColor));", StringComparison.Ordinal);
        int carried = vm.IndexOf("tl, tt, frameRight - tl, frameBottom - tt, frameColor));", StringComparison.Ordinal);
        Assert.True(ghost > 0 && carried > ghost, "the ghost is drawn over the frame it belongs behind");
    }

    /// <summary>
    /// ⚠️ CORNER MARKS ONLY WHILE THE BOX IS AN OBJECT, which is exactly while
    /// a drag would move it. Once there is a caret in the line a drag selects
    /// through the text, and a mark saying "pick me up" would be advertising a
    /// gesture that is no longer on offer.
    /// </summary>
    [Fact]
    public void the_frame_wears_corner_marks_only_when_it_can_be_picked_up()
    {
        string vm = Vm();
        int at = vm.IndexOf("textSlot.PageTextHandles.Add(", StringComparison.Ordinal);
        Assert.True(at > 0, "the frame has no corner marks");

        string guard = vm[Math.Max(0, at - 700)..at];
        Assert.Contains("IsEditMode && !IsEditingInPlace", guard, StringComparison.Ordinal);
    }

    // ---------------- the commit ----------------

    /// <summary>
    /// ⚠️ CAPTURED BEFORE, PUSHED AFTER, exactly as a form edit and a recovered
    /// retype do it. The core changes nothing when it refuses, and an entry
    /// pushed anyway would be a Ctrl+Z that appears to do nothing.
    /// </summary>
    [Fact]
    public void a_refused_move_leaves_no_undo_step_behind()
    {
        string body = Method(Vm(), "public bool CommitTextUnitMove(", 2600);

        int captured = body.IndexOf("Capture(HistoryScope.Document", StringComparison.Ordinal);
        int wrote = body.IndexOf("ShiftGateway.Move(", StringComparison.Ordinal);
        int refused = body.IndexOf("Status = \"This text could not be moved.\";", StringComparison.Ordinal);
        int pushed = body.IndexOf("_history.Push(before)", StringComparison.Ordinal);

        Assert.True(captured > 0 && wrote > captured, "the step is not captured before the write");
        Assert.True(pushed > wrote, "the step is not pushed after the write");
        Assert.True(refused > 0 && refused < pushed, "a refusal still pushes a step");
    }

    /// <summary>
    /// ⚠️ EVERY PER-PAGE CACHE, AND THE READING TOO. This replaces the whole
    /// document behind a new handle, so the lines, the words and the regions all
    /// describe a file that no longer exists.
    /// </summary>
    [Fact]
    public void a_move_throws_away_everything_it_invalidated()
    {
        string body = Method(Vm(), "public bool CommitTextUnitMove(", 2600);

        Assert.Contains("RestoreDocumentBytes(bytes);", body, StringComparison.Ordinal);
        Assert.Contains("_linesByPage.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("_clustersByPage.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("_textRegions.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("prepare_recovery(_documentHandle, page)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ ASKED ONCE, WHEN THE DRAG BEGINS. Which lines come along cannot change
    /// while the pointer is held down, and asking on every pointer move would put
    /// a whole-document parse inside the drag.
    /// </summary>
    [Fact]
    public void the_lines_that_will_move_are_asked_for_once()
    {
        string body = Method(Vm(), "public bool UpdateTextUnitMove(", 1400);

        int started = body.IndexOf("if (started)", StringComparison.Ordinal);
        int asked = body.IndexOf("ShiftGateway.BlockBaselines(", StringComparison.Ordinal);

        Assert.True(started > 0 && asked > started,
            "the block is asked for outside the branch that runs once");
    }

    /// <summary>
    /// A move cannot begin while there is already a caret in the line, or there
    /// would be no way left to select anything.
    /// </summary>
    [Fact]
    public void a_move_does_not_begin_while_the_reader_is_typing()
    {
        string body = Method(Vm(), "public bool CanMoveTextUnitAt(", 300);

        Assert.Contains("IsEditingInPlace", body, StringComparison.Ordinal);
        Assert.Contains("TextUnitBoxContains(pageIndex, normX, normY)", body, StringComparison.Ordinal);

        // And the press asks that, rather than repeating it.
        Assert.Contains("if (!CanMoveTextUnitAt(pageIndex, normX, normY)) { return false; }",
            Method(Vm(), "public bool BeginTextUnitMove(", 300), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ ONLY THE FRAME MOVES WHILE DRAGGING. The page's own glyphs stay where
    /// the file still draws them until the move is committed, so what the reader
    /// carries is an outline over their unaltered document, which is the truth:
    /// nothing has changed yet.
    /// </summary>
    [Fact]
    public void the_frame_follows_the_pointer_and_so_do_the_lines_coming_with_it()
    {
        string vm = Vm();

        Assert.Contains("_textMove.IsDragging\n                ? (_textMove.Dx, _textMove.Dy)",
            vm.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("_movingBaselines.Count > 1", vm, StringComparison.Ordinal);
    }

    // ---------------- helpers ----------------

    private static string Method(string source, string signature, int length)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return source[at..Math.Min(source.Length, at + length)];
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Path.Combine(parts))))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts)));
    }
}
