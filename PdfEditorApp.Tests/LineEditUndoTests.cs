using System;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Ctrl+Z while a line of the page's own text is open takes back what was
/// typed into it.
/// </summary>
/// <remarks>
/// ⚠️ FROM THE READER'S OWN LOG. On a Pyidaungsu page they were fixing a line
/// with Backspace and Delete and pressed Ctrl+Z: the typing stayed, the
/// DOCUMENT went back one edit underneath the open line, and the page then
/// spent a minute re-reading itself. There was no undo for typing at all.
/// </remarks>
public class LineEditUndoTests
{
    /// <summary>Text from code points, so no editor can rewrite the escapes.</summary>
    private static string S(params int[] codePoints) =>
        string.Concat(codePoints.Select(c => (char)c));

    // ညတာ, from the reader's "ညတာသည်".
    private static readonly string Nyataa = S(0x100A, 0x1010, 0x102C);
    private const int E = 0x1031;  // ေ, typed before the consonant it follows
    private const int Ma = 0x1019; // မ

    private sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    [Fact]
    public void a_run_of_typing_comes_back_in_one_step()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer("Title Page", 5, clock.Now);

        buffer.Insert("d"); clock.Ms += 150;
        buffer.Insert("x"); clock.Ms += 150;
        buffer.Backspace();

        Assert.Equal("Titled Page", buffer.Text);
        Assert.True(buffer.Undo());

        Assert.Equal("Title Page", buffer.Text);
        Assert.Equal(5, buffer.Caret);
        Assert.False(buffer.CanUndo);
    }

    /// <summary>
    /// ⚠️ THE ONE THAT MATTERS FOR BURMESE. KeyMagic puts ေ after the consonant
    /// by taking it back and sending both again, a few milliseconds apart. One
    /// Ctrl+Z has to undo that whole correction, never stop half way through it
    /// at a spelling the reader never saw.
    /// </summary>
    [Fact]
    public void a_keymagic_correction_is_one_step()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer(Nyataa, Nyataa.Length, clock.Now);

        buffer.Insert(S(E));
        string afterFirstKey = buffer.Text;

        clock.Ms += 2000;                           // the reader pauses, then types မ
        buffer.BackspaceOneCodePoint(); clock.Ms += 2;
        buffer.Insert(S(Ma)); clock.Ms += 2;
        buffer.Insert(S(E));

        Assert.Equal(Nyataa + S(Ma, E), buffer.Text);

        buffer.Undo();
        Assert.Equal(afterFirstKey, buffer.Text);

        buffer.Undo();
        Assert.Equal(Nyataa, buffer.Text);
        Assert.False(buffer.IsChanged);
    }

    [Fact]
    public void a_pause_starts_a_new_step()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer("ab", 2, clock.Now);

        buffer.Insert("c");
        clock.Ms += LineEditBuffer.UndoPauseMs + 1;
        buffer.Insert("d");

        buffer.Undo();
        Assert.Equal("abc", buffer.Text);
        Assert.Equal(3, buffer.Caret);
    }

    [Fact]
    public void moving_the_caret_starts_a_new_step()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer("ab", 2, clock.Now);

        buffer.Insert("c");
        buffer.MoveHome();
        buffer.Insert("x");

        buffer.Undo();
        Assert.Equal("abc", buffer.Text);
    }

    /// <summary>
    /// An input method confirms the caret where its own rewrite left it, over
    /// and over while a word is composed. That is not a move.
    /// </summary>
    [Fact]
    public void an_input_method_confirming_the_caret_does_not_split_the_step()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer("", 0, clock.Now);

        buffer.ReplaceRange(0, 0, "k");
        buffer.PlaceCaret(buffer.Caret);
        buffer.ReplaceRange(0, 1, "ky");
        buffer.PlaceCaret(buffer.Caret);

        buffer.Undo();
        Assert.Equal("", buffer.Text);
    }

    [Fact]
    public void redo_puts_the_typing_back_and_new_typing_forgets_it()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer("ab", 2, clock.Now);

        buffer.Insert("c");
        buffer.Undo();
        Assert.True(buffer.CanRedo);

        Assert.True(buffer.Redo());
        Assert.Equal("abc", buffer.Text);
        Assert.Equal(3, buffer.Caret);

        buffer.Undo();
        buffer.Insert("z");
        Assert.False(buffer.CanRedo);
    }

    /// <summary>
    /// ⚠️ NOTHING TYPED MEANS NOTHING TO UNDO. The app relies on it: an open
    /// line with no steps is exactly what the page draws, so Ctrl+Z may close
    /// it and go on to the document's own last change without losing anything.
    /// </summary>
    [Fact]
    public void keys_that_change_no_text_leave_nothing_to_undo()
    {
        var buffer = new LineEditBuffer("abc", 0);

        buffer.Backspace();              // at the start: nothing to take
        buffer.MoveRight();
        buffer.SelectAll();
        buffer.ReplaceRange(0, 3, "abc");

        Assert.False(buffer.CanUndo);
        Assert.False(buffer.IsChanged);
    }

    [Fact]
    public void undoing_every_step_gives_back_the_line_the_page_draws()
    {
        var clock = new Clock();
        var buffer = new LineEditBuffer(Nyataa, 1, clock.Now);

        buffer.Delete(); clock.Ms += 5000;
        buffer.Insert(S(Ma)); clock.Ms += 5000;
        buffer.MoveEnd();
        buffer.BackspaceOneCodePoint();

        while (buffer.Undo()) { }

        Assert.Equal(Nyataa, buffer.Text);
        Assert.False(buffer.IsChanged);
        Assert.Equal(1, buffer.Caret);
    }

    // ---------------- the app's side ----------------

    private static string ViewModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    /// <summary>One method's body, comment lines dropped.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");
        int end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return string.Join("\n", code[at..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    /// <summary>
    /// ⚠️ THE OPEN LINE FIRST. The document's history is reached only once the
    /// line has nothing left to take back and has been closed.
    /// </summary>
    [Theory]
    [InlineData("public void Undo()", "edit.Undo()", "_history.Undo(")]
    [InlineData("public void Redo()", "edit.Redo()", "_history.Redo(")]
    public void undo_and_redo_ask_the_open_line_before_the_document(
        string method, string line, string document)
    {
        string body = Body(ViewModel(), method);

        int toLine = body.IndexOf(line, StringComparison.Ordinal);
        int close = body.IndexOf("CancelInPlaceEdit();", StringComparison.Ordinal);
        int toDocument = body.IndexOf(document, StringComparison.Ordinal);

        Assert.True(toLine > 0, $"{method} never asks the open line");
        Assert.True(close > toLine, $"{method} does not close the line before the document");
        Assert.True(toDocument > close, $"{method} reaches the document before the line");
    }

    /// <summary>
    /// A document redo would bury typing that was never committed.
    /// </summary>
    [Fact]
    public void redo_never_runs_over_uncommitted_typing()
    {
        string body = Body(ViewModel(), "public void Redo()");

        int guard = body.IndexOf("if (edit.IsChanged) { return; }", StringComparison.Ordinal);
        Assert.True(guard > 0);
        Assert.True(guard < body.IndexOf("CancelInPlaceEdit();", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠️ A DISABLED MENU ITEM SWALLOWS ITS SHORTCUT. Undo is enabled by
    /// CanUndo, so the open line has to count there or Ctrl+Z never arrives.
    /// </summary>
    [Fact]
    public void the_menu_counts_the_open_line_and_follows_the_typing()
    {
        string vm = ViewModel();

        Assert.Contains("public bool CanUndo => _lineEdit is { CanUndo: true } || _history.CanUndo;", vm, StringComparison.Ordinal);
        Assert.Contains("NotifyHistoryChanged();", Body(vm, "private void Changed(Action act)"), StringComparison.Ordinal);
        Assert.Contains("NotifyHistoryChanged();", Body(vm, "private void EndInPlaceEdit()"), StringComparison.Ordinal);
    }
}
