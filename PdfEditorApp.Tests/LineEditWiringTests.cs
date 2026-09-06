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

    /// <summary>
    /// The whole edit path: the command, the background write, and what follows
    /// it back on the UI thread.
    /// </summary>
    ///
    /// <remarks>
    /// ⚠️ THE INVARIANTS BELOW ARE ABOUT THE PATH, NOT ABOUT ONE METHOD. The
    /// native write moved onto a background task and its aftermath moved with
    /// it, so a test reading only the command would now be reading the half
    /// that does the least, and would keep passing while the half that matters
    /// rotted.
    /// </remarks>
    private static string EditPath()
    {
        string vm = ViewModel();
        return Body(vm, "public bool EditSelectedLine(")
            + Body(vm, "private async void WriteLineAsync(")
            + Body(vm, "private void FinishLineWrite(");
    }

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
        string body = EditPath();

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
        string body = EditPath();

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
        string body = EditPath();
        Assert.Contains("!line.CanEdit", body, StringComparison.Ordinal);

        // The near end used to be a TextBox that refused to open. There is no
        // editor to open any more, so the same refusal now guards the caret:
        // a line the core will not rewrite never gets one.
        string open = Body(ViewModel(), "public bool BeginInPlaceEdit(");
        Assert.Contains("!unit.CanEdit", open, StringComparison.Ordinal);
        Assert.Contains("unit.RefusalReason", open, StringComparison.Ordinal);
    }

    [Fact]
    public void too_wide_says_something_the_reader_can_act_on()
    {
        // ⚠️ It is the one refusal with a remedy: the same edit with fewer words
        // goes through. Reporting it as "unsupported" would be telling the user
        // the feature is broken.
        string body = EditPath();

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

    // ---------------- the second writer ----------------

    [Fact]
    public void both_call_sites_hand_over_what_the_line_says()
    {
        // ⚠️ WITHOUT IT THE SECOND WRITER IS UNREACHABLE. `LineGateway.Write`
        // only tries the block writer when it is told what the line currently
        // says, because that is the anchor the core checks before splicing. A
        // call site that drops the argument silently loses every multi-piece
        // line again, and nothing else would notice.
        string edit = EditPath();
        // The trailing paren differs between the two: the edit call sits inside a
        // Task.Run lambda now. What matters is the argument, not the wrapping.
        Assert.Contains("line.FontName, newText, line.Text)", edit, StringComparison.Ordinal);

        string undo = Body(ViewModel(), "private void ApplyLineText(LineTextRecord record, bool backwards)");
        Assert.Contains("line.FontName, wanted, line.Text);", undo, StringComparison.Ordinal);
    }

    [Fact]
    public void undo_takes_the_same_route_the_edit_took()
    {
        // ⚠️ THE SAME DISPATCH, WRITTEN THE SAME WAY, in both places. Undo
        // reaching a writer the edit did not is how a line comes back subtly
        // different from the one that was there.
        string edit = EditPath();
        string undo = Body(ViewModel(), "private void ApplyLineText(LineTextRecord record, bool backwards)");

        foreach (string body in new[] { edit, undo })
        {
            Assert.Contains("line.Route == LineWriter.BlockWriter", body, StringComparison.Ordinal);
            Assert.Contains("Interop.LineGateway.WriteAsBlock(", body, StringComparison.Ordinal);
            Assert.Contains("Interop.LineGateway.Write(", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_object_writer_is_never_handed_a_block_routed_line()
    {
        // Its own rule set is what refused the line, so offering it one anyway
        // asks it to do the thing it declined. The ternary is the guarantee:
        // the block branch is the one that runs, and it does not fall through.
        string edit = EditPath();

        int test = edit.IndexOf("line.Route == LineWriter.BlockWriter", StringComparison.Ordinal);
        int block = edit.IndexOf("Interop.LineGateway.WriteAsBlock(", StringComparison.Ordinal);
        int old = edit.IndexOf("Interop.LineGateway.Write(", StringComparison.Ordinal);

        Assert.True(test > 0, "the edit does not ask which writer the line is routed to");
        Assert.True(test < block, "it writes before it asks");
        Assert.True(block < old, "the block branch is not the one the test selects");
    }

    [Fact]
    public void the_frame_and_the_writer_read_one_decision()
    {
        // ⚠️ THE WHOLE POINT OF THE ROUTING LAYER. The frame is painted from
        // CanEdit and the writer chosen from Route; if CanEdit carried a rule of
        // its own, the app could paint a line refused and write it anyway, which
        // is the bug this replaced. CanEdit is DERIVED, and this says so.
        string snapshot = Source("PdfEditorApp.Viewport", "LineSnapshot.cs");

        Assert.Contains("public bool CanEdit => Route != LineWriter.None;", snapshot,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CanEdit => Refusal ==", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void the_object_writer_is_still_asked_first()
    {
        // ⚠️ ORDER IS THE COMPATIBILITY GUARANTEE. Every line the existing
        // writer takes today it must still take, so the block writer is only
        // reached after that one has refused, and the first refusal is the one
        // reported when both decline.
        string gateway = Source("PdfEditorApp", "Interop", "LineGateway.cs");
        string body = Body(gateway, "public static int Write(");

        int first = body.IndexOf("RenderCoreNative.set_line_text(", StringComparison.Ordinal);
        int second = body.IndexOf("WriteAsBlock(", StringComparison.Ordinal);

        Assert.True(first > 0, "the object writer is not called at all");
        Assert.True(second > first, "the block writer is asked before the object writer");
        Assert.Contains("status == RenderStatus.OkPdfium || expected is null", body,
            StringComparison.Ordinal);
        Assert.Contains("spliced == RenderStatus.OkPdfium ? spliced : status", body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_block_writer_is_never_offered_a_stand_in_font()
    {
        // The object writer already offers one and runs first, so a line that
        // needs one has had its chance. Handing one here would open a second
        // way into Path B that nothing has measured.
        string gateway = Source("PdfEditorApp", "Interop", "LineGateway.cs");
        string body = Body(gateway, "public static int WriteAsBlock(");

        Assert.DoesNotContain("SystemFontMatch", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fontName", body, StringComparison.Ordinal);
    }

    // ---------------- the write runs off the UI thread ----------------
    //
    // ⚠️ STRUCTURAL, NOT TIMED. "Finishes within N ms" would pass on a small
    // document and fail on a loaded machine, and would say nothing about which
    // thread ran what. What matters is the shape: the native call is inside a
    // Task.Run, the command that starts it does not call the gateway at all,
    // and the guard is set before and cleared after whatever happens.

    [Fact]
    public void the_native_write_happens_inside_a_background_task()
    {
        // Measured at 931 ms on a 296-page book. On the UI thread that is a
        // frozen window, and the app looked hung on exactly the documents where
        // editing matters most.
        string body = Body(ViewModel(), "private async void WriteLineAsync(");

        int run = body.IndexOf("await Task.Run(", StringComparison.Ordinal);
        int block = body.IndexOf("Interop.LineGateway.WriteAsBlock(", StringComparison.Ordinal);
        int obj = body.IndexOf("Interop.LineGateway.Write(", StringComparison.Ordinal);

        Assert.True(run > 0, "the write is not handed to a background task");
        Assert.True(block > run, "the block writer is called outside the task");
        Assert.True(obj > run, "the object writer is called outside the task");
    }

    [Fact]
    public void the_command_itself_no_longer_touches_the_gateway()
    {
        // Everything synchronous that remains is the undo batch and the guard.
        string edit = Body(ViewModel(), "public bool EditSelectedLine(string newText)");

        Assert.DoesNotContain("Interop.LineGateway.", edit, StringComparison.Ordinal);
        Assert.Contains("BeginEdit(\"Edit line\");", edit, StringComparison.Ordinal);
        Assert.Contains("RecordEdit(new LineTextRecord(", edit, StringComparison.Ordinal);
        Assert.Contains("_lineWriteInFlight = true;", edit, StringComparison.Ordinal);
        Assert.Contains("WriteLineAsync(line, page, newText);", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void the_guard_is_cleared_whatever_the_write_does()
    {
        // ⚠️ IN THE FINALLY, NOT AFTER THE AWAIT. A throw that escaped would
        // leave the guard set for the rest of the session and every later edit
        // would refuse in silence.
        string body = Body(ViewModel(), "private async void WriteLineAsync(");

        int fin = body.IndexOf("finally", StringComparison.Ordinal);
        int clear = body.IndexOf("_lineWriteInFlight = false;", StringComparison.Ordinal);

        Assert.True(fin > 0, "there is no finally around the write");
        Assert.True(clear > fin, "the guard is cleared outside the finally");
        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(ViewModel(), "_lineWriteInFlight = false;"));
    }

    /// <summary>
    /// The guard comes before the first thing each command would otherwise do.
    /// </summary>
    ///
    /// <remarks>
    /// ⚠️ ANCHORED, NOT WINDOWED. "Within the first N characters" would pass
    /// or fail on how many comments sit above the check. What has to be true is
    /// an ordering: the guard is read before the batch is opened, and before
    /// the history is asked for anything.
    ///
    /// Undo finds its line BY TEXT, because retyping renumbers the objects and
    /// the recorded range goes stale. Asking a half-written document what it
    /// says would find the wrong line, or none at all.
    /// </remarks>
    [Theory]
    [InlineData("public bool EditSelectedLine(string newText)", "BeginEdit(")]
    [InlineData("public void Undo()", "_history.Undo(")]
    [InlineData("public void Redo()", "_history.Redo(")]
    public void nothing_may_start_while_a_write_is_in_flight(string signature, string firstWork)
    {
        string code = ViewModel();
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int guard = code.IndexOf("if (_lineWriteInFlight)", at, StringComparison.Ordinal);
        int work = code.IndexOf(firstWork, at, StringComparison.Ordinal);

        Assert.True(guard > 0, $"{signature} never checks the guard");
        Assert.True(work > 0, $"{signature} no longer contains {firstWork}");
        Assert.True(guard < work, $"{signature} starts work before checking the guard");
    }

    [Fact]
    public void what_followed_the_write_still_follows_it_in_the_same_order()
    {
        string body = Body(ViewModel(), "private void FinishLineWrite(");

        // The refusal path is unchanged: nothing to undo, and the same words.
        Assert.Contains("AbandonEdit();", body, StringComparison.Ordinal);
        Assert.Contains("RenderStatus.TooWide =>", body, StringComparison.Ordinal);
        Assert.Contains("RenderStatus.Unsupported when line.Route == LineWriter.BlockWriter =>",
            body, StringComparison.Ordinal);

        int commit = body.IndexOf("CommitEdit();", StringComparison.Ordinal);
        int invalidate = body.IndexOf("InvalidateLoadedPage(page);", StringComparison.Ordinal);
        int clear = body.IndexOf("ClearLineSelection();", StringComparison.Ordinal);
        int outline = body.IndexOf("RefreshSelectionOutline();", StringComparison.Ordinal);
        int render = body.IndexOf("RenderCurrentPage();", StringComparison.Ordinal);

        Assert.True(commit > 0, "the batch is never committed");
        Assert.True(invalidate > commit, "the cache is dropped before the batch commits");
        Assert.True(clear > invalidate, "the selection is cleared before the cache is dropped");
        Assert.True(outline > clear, "the outline is refreshed before the selection is cleared");
        Assert.True(render > outline, "the page is rendered before the outline is refreshed");
    }

    [Fact]
    public void the_caret_is_gone_before_the_write_ever_starts()
    {
        // ⚠️ THE REASON NOTHING CAN STAY STUCK ON THE PAGE. The edit is
        // ended BEFORE the commit is asked for, so no outcome of the write,
        // success, refusal or throw, can leave a caret or a redrawn tail
        // sitting over text that has moved on.
        string body = Body(ViewModel(), "public bool CommitInPlaceEdit()");

        int end = body.IndexOf("EndInPlaceEdit();", StringComparison.Ordinal);
        int commit = body.IndexOf("CommitTextUnit(typed)", StringComparison.Ordinal);

        Assert.True(end > 0, "the edit is never ended");
        Assert.True(commit > end, "the commit runs before the edit is ended");
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
        // Enter commits and Escape abandons, exactly as before. They are read
        // by the page's own key handler now rather than by a TextBox, because
        // there is no longer a TextBox to hold focus.
        string page = Page();
        int at = page.IndexOf("ViewModel.IsEditingInPlace && !IsTextInputFocused",
                              StringComparison.Ordinal);
        Assert.True(at > 0, "nothing routes keys to an in-place edit");

        string keys = page[at..Math.Min(page.Length, at + 2400)];
        Assert.Contains("VirtualKey.Enter", keys, StringComparison.Ordinal);
        Assert.Contains("CommitInPlaceEdit()", keys, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Escape", keys, StringComparison.Ordinal);
        Assert.Contains("CancelInPlaceEdit", keys, StringComparison.Ordinal);

        // ⚠️ AND CLICKING AWAY COMMITS, which is the half a keyboard test
        // would miss. Clearing the selection without committing would throw
        // away what the reader typed, silently.
        int away = page.IndexOf("Any other press drops the box", StringComparison.Ordinal);
        Assert.True(away > 0, "the click-away path is no longer where this test looks");
        // ⚠️ NO WINDOW. This sliced 900 characters and then looked inside
        // them, so adding a comment to the code under test pushed the clear out
        // of view and the test failed for seeing half the story. The claim is
        // an ORDER, and an order needs no window: the first commit after this
        // point must come before the first clear after it.
        int commits = page.IndexOf("CommitInPlaceEdit();", away, StringComparison.Ordinal);
        int clears = page.IndexOf("ClearTextUnitSelection();", away, StringComparison.Ordinal);
        Assert.True(commits > 0, "the click-away path no longer commits");
        Assert.True(clears > commits, "the selection is dropped before the typing is kept");
    }

    [Fact]
    public void nothing_is_painted_over_the_page_beyond_the_text_it_replaces()
    {
        // ⚠️ THE BOX THIS REPLACED WAS SIZED TO THE LINE PLUS ROOM TO TYPE,
        // so it covered the lines above and below. Nothing is sized that way
        // any more: the only thing painted over the page is the old tail's own
        // extent, and it stops exactly where the page stopped drawing.
        string body = Body(ViewModel(), "public LiveTextTail? InPlaceTail()");

        Assert.Contains("CoverRight: _lineEditGlyphs[^1].Right", body, StringComparison.Ordinal);

        // And when nothing has changed there is no tail at all, so the page is
        // untouched while the reader is only looking at it.
        Assert.Contains("if (!_lineEdit.IsChanged) { return null; }", body, StringComparison.Ordinal);
    }
}
