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
    /// ⚠️ THE CLICK IS THE ONE THAT MUST NOT BREAK. A press inside the box is
    /// how a caret gets into a line, and that is how the reader edits anything
    /// at all. Arming a move must not take it away, so the edit still begins on
    /// the very same press and only a pointer that travels changes its mind.
    /// </summary>
    [Fact]
    public void the_press_arms_a_move_and_still_begins_the_edit()
    {
        string page = Page();

        int armed = page.IndexOf("ViewModel.BeginTextUnitMove(content.Page, nx, ny)", StringComparison.Ordinal);
        int edited = page.IndexOf("ViewModel.BeginInPlaceEdit(content.Page, nx, ny)", StringComparison.Ordinal);

        Assert.True(armed > 0, "nothing arms a move");
        Assert.True(edited > 0, "the press no longer begins an edit");
        Assert.True(armed < edited, "the move is armed after the edit has already begun");
    }

    /// <summary>
    /// ⚠️ AND CARRYING COMES BEFORE SELECTING. The same press put a caret in
    /// the line, so without this the pointer movement that carries the text
    /// would be read as selecting through it.
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
    /// Once it turns out to be a move, the caret the press put in the line has
    /// no business being there.
    /// </summary>
    [Fact]
    public void a_press_that_becomes_a_move_takes_its_caret_back()
    {
        string page = Page();
        int at = page.IndexOf("ViewModel.UpdateTextUnitMove(mx, my)", StringComparison.Ordinal);
        Assert.True(at > 0);

        string body = page[at..Math.Min(page.Length, at + 400)];
        Assert.Contains("ViewModel.CancelInPlaceEdit();", body, StringComparison.Ordinal);
    }

    /// <summary>The held modifier is what asks for one line instead of the block.</summary>
    [Fact]
    public void releasing_commits_the_move_and_alt_asks_for_one_line()
    {
        string page = Page();
        Assert.Contains("ViewModel.CommitTextUnitMove(IsAltDown())", page, StringComparison.Ordinal);
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
        string body = Method(Vm(), "public bool BeginTextUnitMove(", 700);

        Assert.Contains("IsEditingInPlace", body, StringComparison.Ordinal);
        Assert.Contains("TextUnitBoxContains(pageIndex, normX, normY)", body, StringComparison.Ordinal);
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
