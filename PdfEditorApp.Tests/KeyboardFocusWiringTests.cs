using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Who owns the keyboard, and when.
///
/// The canvas shortcuts are dropped whenever a text field has focus, and that
/// guard is correct: without it, Backspace typed into the find box deleted the
/// selected annotation, silently, while someone was correcting a typo. What was
/// missing was a way BACK. Nothing took focus off the find box, so after using
/// Find, Ctrl+C, Ctrl+V and Delete were dead on the canvas until the user
/// happened to press Escape.
///
/// Focus lives entirely in the view, which this assembly cannot load, so these
/// read the source. That is weaker than exercising it, and it is the strongest
/// thing available for a UI-only path; the repository already does the same in
/// <c>AppWiringTests</c> and <c>SearchWiringTests</c>. Every rule below is one
/// whose breakage is invisible until someone loses work to it.
/// </summary>
public class KeyboardFocusWiringTests
{
    private static string MainPageSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string relative = Path.Combine("PdfEditorApp", "MainPage.xaml.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>The body of a method, bounded by the next method declaration so
    /// a window can never run on into a neighbour and find what it was looking
    /// for there.</summary>
    private static string Body(string signature, int limit = 2500)
    {
        string source = MainPageSource();
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        string rest = source[at..Math.Min(source.Length, at + limit)];
        int next = rest.IndexOf("\n    private ", 10, StringComparison.Ordinal);
        return next > 0 ? rest[..next] : rest;
    }

    // ---------------- The way back to the canvas ----------------

    [Fact]
    public void clicking_the_page_takes_the_keyboard_back_from_a_text_field()
    {
        // The fix. Without it the only exit from the find box is Escape, and
        // nothing on screen says so.
        string body = Body("private void ViewportHost_PointerPressed(");

        Assert.Contains("IsTextInputFocused", body, StringComparison.Ordinal);
        Assert.Contains("RootGrid.Focus(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void but_not_while_an_in_place_text_editor_is_open()
    {
        // That editor IS the text field being used. Taking its focus would
        // commit the edit on the very click meant to place the caret, so a
        // second click inside your own text box would end the edit.
        string body = Body("private void ViewportHost_PointerPressed(");

        Assert.Contains("_textEditor is null", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_reclaim_happens_before_the_pointer_type_guard()
    {
        // The handler returns early for touch and pen, letting the ScrollView
        // pan. Putting the reclaim after that return would fix the mouse and
        // leave a touch user stuck in the find box.
        string body = Body("private void ViewportHost_PointerPressed(");

        int reclaim = body.IndexOf("RootGrid.Focus(", StringComparison.Ordinal);
        int guard = body.IndexOf("PointerDeviceType.Mouse", StringComparison.Ordinal);

        Assert.True(reclaim >= 0 && guard >= 0);
        Assert.True(reclaim < guard, "the focus reclaim sits after the early return for touch and pen");
    }

    // ---------------- What must not be "fixed" instead ----------------

    [Fact]
    public void the_key_handler_still_refuses_to_act_while_a_text_field_has_focus()
    {
        // The tempting shortcut is to delete this guard. It exists because
        // Backspace in the find box used to delete the selected annotation and
        // mark the event handled, so it did not even edit the text.
        // 1900: the refusal while saving sits just above it.
        string body = Body("private void RootGrid_KeyDown(", limit: 1900);

        Assert.Contains("if (IsTextInputFocused)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void escape_still_leaves_a_text_field()
    {
        // The keyboard-only way out, and the one this fix does not replace.
        string body = Body("private void RootGrid_KeyDown(", limit: 1600);

        Assert.Contains("VirtualKey.Escape", body, StringComparison.Ordinal);
    }

    [Fact]
    public void pressing_enter_in_the_find_box_does_not_move_focus()
    {
        // Enter and Shift+Enter step through matches and have to stay
        // repeatable, which is why the fix is on the CLICK and not here. Every
        // find bar works this way.
        string body = Body("private void SearchBox_KeyDown(", limit: 900);

        Assert.DoesNotContain("RootGrid.Focus(", body, StringComparison.Ordinal);
        Assert.Contains("StepSearchMatch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void every_kind_of_text_field_still_counts_as_one()
    {
        // A field missing from this list is a field where the canvas shortcuts
        // fire while you type into it, which is the data-loss direction.
        string source = MainPageSource();
        int at = source.IndexOf("private bool IsTextInputFocused", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = source[at..Math.Min(source.Length, at + 300)];

        foreach (string control in new[] { "TextBox", "RichEditBox", "AutoSuggestBox", "PasswordBox" })
        {
            Assert.Contains(control, body, StringComparison.Ordinal);
        }
    }

    // ---------------- The three shortcuts themselves ----------------

    [Theory]
    [InlineData("case VirtualKey.C when _isCtrlDown:", "CopySelectedAnnotations")]
    [InlineData("case VirtualKey.V when _isCtrlDown:", "PasteAnnotations")]
    [InlineData("case VirtualKey.Delete:", "DeleteSelectedAnnotation")]
    public void each_shortcut_still_reaches_the_command_it_names(string chord, string command)
    {
        // The chords were never the broken part, and a "fix" that rewrote them
        // would be fixing the wrong thing. This pins what they run so the next
        // person reads the focus path instead.
        string source = MainPageSource();
        int at = source.IndexOf(chord, StringComparison.Ordinal);
        Assert.True(at >= 0, $"nothing handles {chord}");

        // The window was 400 and the Delete case outgrew it when a selected
        // text unit became the first thing Delete reaches. The chord still runs
        // the command; it is simply further down the case now.
        Assert.Contains(command, source[at..Math.Min(source.Length, at + 1200)], StringComparison.Ordinal);
    }
}
