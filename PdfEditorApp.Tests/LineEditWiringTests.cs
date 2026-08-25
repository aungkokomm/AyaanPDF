using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The call sites that carry a line edit.
///
/// Read out of the source, because they live in the WinUI project and a net10.0
/// test assembly cannot load one; the same bargain every other wiring test here
/// makes. What they hold down is the handful of rules that are easy to break by
/// accident and expensive to notice.
/// </summary>
public class LineEditWiringTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string ViewModel() =>
        Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    /// <summary>One method's body, comment lines dropped so that commenting a
    /// call out counts as removing it.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = code.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = code[at..(next > at ? next : code.Length)];

        return string.Join('\n', Array.FindAll(
            body.Split('\n'), l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    // ---------------- one edit, one undo ----------------

    [Fact]
    public void a_line_edit_is_one_history_entry_and_not_several()
    {
        // A line edit empties every text object in its range and shifts what
        // follows. The reader pressed one key, so Ctrl+Z has to put it all back
        // in one step.
        string body = Body(ViewModel(), "public bool EditSelectedLine(");

        Assert.Contains("BeginEdit(", body, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new LineTextRecord(", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "CommitEdit()"));
    }

    [Fact]
    public void a_refusal_leaves_no_history_entry_behind()
    {
        // The core puts the page back as it found it when it refuses, so there
        // is nothing to undo. Committing anyway would leave a Ctrl+Z that does
        // nothing, which reads as the app having lost the edit.
        string body = Body(ViewModel(), "public bool EditSelectedLine(");

        int refused = body.IndexOf("status != RenderStatus.OkPdfium", StringComparison.Ordinal);
        int abandon = body.IndexOf("AbandonEdit()", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitEdit()", StringComparison.Ordinal);

        Assert.True(refused > 0, "the refusal is not checked");
        Assert.True(abandon > refused, "the entry is not abandoned on refusal");
        Assert.True(commit > abandon, "the commit is not after the refusal branch");
    }

    [Fact]
    public void a_line_the_core_would_refuse_is_never_sent_to_it()
    {
        // Being told before typing is the difference between a limitation and a
        // bug, and both ends check: the editor will not open on a refused line,
        // and the edit will not send one even if it somehow did.
        string body = Body(ViewModel(), "public bool EditSelectedLine(");
        Assert.Contains("!line.CanEdit", body, StringComparison.Ordinal);

        string open = Body(Page(), "private bool OpenUnitEditor(int caretAt)");
        Assert.Contains("!unit.CanEdit", open, StringComparison.Ordinal);
        Assert.Contains("unit.RefusalReason", open, StringComparison.Ordinal);
    }

    [Fact]
    public void too_wide_says_something_the_reader_can_act_on()
    {
        // ⚠️ It is the one refusal with a remedy: the same edit with fewer words
        // goes through. Reporting it as "unsupported" would be telling the user
        // the feature is broken.
        string body = Body(ViewModel(), "public bool EditSelectedLine(");

        Assert.Contains("RenderStatus.TooWide", body, StringComparison.Ordinal);
        Assert.Contains("too long to fit", body, StringComparison.Ordinal);
    }

    // ---------------- undo ----------------

    [Fact]
    public void undo_finds_the_line_by_what_it_says_and_not_by_where_it_was()
    {
        // ⚠️ THE DIFFERENCE FROM A WORD. Retyping a line changes how many words
        // it has and can change how many objects draw it, so after the edit the
        // object range is not what the record recorded. The text is the only
        // handle that survives the operation being undone.
        string body = Body(ViewModel(), "private void ApplyLineText(LineTextRecord record, bool backwards)");

        Assert.Contains("l.Text.Trim() == expected.Trim()", body, StringComparison.Ordinal);
        Assert.Contains("line.FirstObject", body, StringComparison.Ordinal);
    }

    [Fact]
    public void undo_refuses_rather_than_overwriting_text_it_was_never_about()
    {
        string body = Body(ViewModel(), "private void ApplyLineText(LineTextRecord record, bool backwards)");

        int missing = body.IndexOf("line is null", StringComparison.Ordinal);
        int write = body.IndexOf("LineGateway.Write(", StringComparison.Ordinal);

        Assert.True(missing > 0, "undo does not check the page still says it");
        Assert.True(missing < write, "undo writes before it checks");
    }

    [Fact]
    public void the_history_knows_about_a_line_record()
    {
        string code = ViewModel();
        Assert.Contains("case LineTextRecord n:", code, StringComparison.Ordinal);
        Assert.Contains("ApplyLineText(n, backwards);", code, StringComparison.Ordinal);
    }

    // ---------------- the caches ----------------

    [Fact]
    public void the_lines_are_dropped_wherever_the_words_are()
    {
        // ⚠️ They are read from the same content and a line carries an object
        // RANGE, which an edit renumbers. A cached line outliving its page hands
        // stale indices to the next write; the core re-derives and refuses, so
        // this is about showing the reader the truth.
        string code = ViewModel();

        Assert.Equal(Count(code, "_clustersByPage.Clear()"), Count(code, "_linesByPage.Clear()"));
        Assert.Contains("_linesByPage.Remove(pageIndex);", code, StringComparison.Ordinal);
    }

    // ---------------- the gesture ----------------

    [Fact]
    public void the_word_edit_core_is_untouched()
    {
        // The gesture that reaches it has now changed twice; the write behind
        // it has not changed at all, and this is what says so.
        string vm = ViewModel();
        Assert.Contains("public bool EditSelectedWord(", vm, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new WordTextRecord(", vm, StringComparison.Ordinal);
    }

    // ---------------- the editor ----------------

    [Fact]
    public void the_line_editor_commits_the_way_every_in_place_rename_does()
    {
        string page = Page();

        Assert.Contains("private void UnitEditor_LostFocus(object sender, RoutedEventArgs e) => CommitUnitEdit();",
                        page, StringComparison.Ordinal);

        string keys = Body(page, "private void UnitEditor_KeyDown(");
        Assert.Contains("VirtualKey.Enter", keys, StringComparison.Ordinal);
        Assert.Contains("CommitUnitEdit()", keys, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Escape", keys, StringComparison.Ordinal);
        Assert.Contains("CancelUnitEdit()", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void the_editor_never_grows_wider_than_the_page()
    {
        // It is sized to the line plus room to type past its end, and a long
        // line near the right margin would otherwise put the box off screen.
        string body = Body(Page(), "private bool OpenUnitEditor(int caretAt)");

        Assert.Contains("Math.Min(scale,", body, StringComparison.Ordinal);
        Assert.Contains("scale = ViewModel.OverlayScale", body, StringComparison.Ordinal);
    }
}
