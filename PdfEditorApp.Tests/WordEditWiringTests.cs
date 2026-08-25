using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The call sites that carry a word edit.
///
/// Read out of the source, because they live in the WinUI project and a net10.0
/// test assembly cannot load one; that is the same bargain every other wiring
/// test here makes. What they hold down is the handful of rules that are easy
/// to break by accident and expensive to notice.
/// </summary>
public class WordEditWiringTests
{
    private static string Source(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string ViewModel() =>
        Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    [Fact]
    public void an_edit_is_one_history_entry_and_not_several()
    {
        // A word edit can touch a dozen PDF objects: the first takes the new
        // text, the rest are emptied, and the remainder of the line shifts. The
        // reader pressed one key, so Ctrl+Z has to put it all back in one step.
        string code = ViewModel();

        int edit = code.IndexOf("public bool EditSelectedWord(", StringComparison.Ordinal);
        Assert.True(edit > 0, "there is no word edit");

        int next = code.IndexOf("\n    /// <summary>", edit, StringComparison.Ordinal);
        string body = code[edit..(next > edit ? next : code.Length)];

        Assert.Contains("BeginEdit(", body, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new WordTextRecord(", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "CommitEdit()"));
    }

    [Fact]
    public void a_refusal_leaves_no_history_entry_behind()
    {
        // The core puts the page back as it found it when it refuses, so there
        // is nothing to undo. Committing anyway would leave a Ctrl+Z that does
        // nothing, which reads as the app having lost the edit.
        string code = ViewModel();

        int edit = code.IndexOf("public bool EditSelectedWord(", StringComparison.Ordinal);
        int next = code.IndexOf("\n    /// <summary>", edit, StringComparison.Ordinal);
        string body = code[edit..next];

        int refused = body.IndexOf("!= RenderStatus.OkPdfium", StringComparison.Ordinal);
        int abandon = body.IndexOf("AbandonEdit()", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitEdit()", StringComparison.Ordinal);

        Assert.True(refused > 0 && abandon > refused, "a refusal does not abandon the batch");
        Assert.True(abandon < commit, "the batch is committed before the refusal is handled");
    }

    [Fact]
    public void undo_checks_what_the_page_says_before_it_writes()
    {
        // ⚠️ THE ONE REAL HAZARD. Every other record is keyed by a Guid that
        // finds its annotation wherever it moved to. A word has no identity we
        // put there and is keyed by where it sits in the page's content, so an
        // unverified undo could overwrite text it was never about.
        string code = ViewModel();

        int applier = code.IndexOf("private void ApplyWordText(", StringComparison.Ordinal);
        Assert.True(applier > 0, "there is no word undo");

        int next = code.IndexOf("\n    /// <summary>", applier, StringComparison.Ordinal);
        string body = code[applier..(next > applier ? next : code.Length)];

        int check = body.IndexOf("expected.Trim()", StringComparison.Ordinal);
        int write = body.IndexOf("WordClusterGateway.Write", StringComparison.Ordinal);

        Assert.True(check > 0, "undo does not check what the page currently says");
        Assert.True(write > check, "undo writes before it checks");
        Assert.Contains("return;", body[check..write], StringComparison.Ordinal);
    }

    [Fact]
    public void the_words_of_a_page_are_dropped_when_that_page_changes()
    {
        // A cached word carries object indices that an edit may have moved.
        string code = ViewModel();

        int invalidate = code.IndexOf(
            "private void InvalidateAnnotationCache(", StringComparison.Ordinal);
        Assert.True(invalidate > 0);

        int next = code.IndexOf("\n    private ", invalidate + 10, StringComparison.Ordinal);
        Assert.Contains("_clustersByPage.Remove(pageIndex);",
            code[invalidate..next], StringComparison.Ordinal);
    }

    [Fact]
    public void a_double_click_tries_our_own_marks_before_the_documents_words()
    {
        // Annotations are painted OVER the finished page, so anything of ours
        // under the pointer is on top of the words and is what the click meant.
        string code = Page();

        int handler = code.IndexOf(
            "private void ViewportHost_DoubleTapped(", StringComparison.Ordinal);
        Assert.True(handler > 0);

        int ours = code.IndexOf("HitLoadedTextBox(", handler, StringComparison.Ordinal);
        int theirs = code.IndexOf("SelectPageTextAt(", handler, StringComparison.Ordinal);

        Assert.True(ours > 0 && theirs > ours,
            "the document's words are tried before our own text boxes");
    }

    [Fact]
    public void a_word_that_cannot_be_edited_is_refused_before_the_editor_opens()
    {
        // Being told before typing is the difference between a limitation and a
        // bug. Rotated text, mixed styling and out-of-order scripts are all real
        // cases measured on real documents.
        string code = Page();

        int open = code.IndexOf("private bool OpenWordEditor(", StringComparison.Ordinal);
        Assert.True(open > 0);

        int next = code.IndexOf("\n    /// <summary>", open, StringComparison.Ordinal);
        string body = code[open..next];

        int guard = body.IndexOf("!word.CanEdit", StringComparison.Ordinal);
        int build = body.IndexOf("new TextBox", StringComparison.Ordinal);

        Assert.True(guard > 0 && guard < build, "the editor is built before the word is checked");
    }

    [Fact]
    public void closing_the_editor_cannot_re_enter_the_commit()
    {
        // Removing a focused TextBox raises LostFocus, and LostFocus commits.
        // Unhooking after removal would run the commit a second time, on an
        // editor that is already being torn down.
        string code = Page();

        int tear = code.IndexOf("private void TearDownWordEditor()", StringComparison.Ordinal);
        Assert.True(tear > 0);

        string body = code[tear..(tear + 900)];
        int unhook = body.IndexOf("LostFocus -= WordEditor_LostFocus", StringComparison.Ordinal);
        int remove = body.IndexOf("EditCanvas.Children.Remove", StringComparison.Ordinal);

        Assert.True(unhook > 0 && remove > unhook,
            "the editor is removed before its handlers are unhooked");
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }
        return n;
    }
}
