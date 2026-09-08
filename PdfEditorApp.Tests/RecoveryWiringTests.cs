using System;
using System.Collections.Generic;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The signal that says a page needs its text read back out of the glyphs, and
/// the one place that acts on it.
///
/// ⚠️ THE CORE CANNOT DECIDE THIS FOR ITSELF, which is why the app has to.
/// Reading a shaped page costs about seventeen seconds of work, and finding out
/// whether a document is worth it means either serialising the whole file or
/// asking PDFium for a page, and asking for a page parses it. Both were
/// measured and both cost more than they saved. The refusal the core already
/// sends back is free, so the decision lives on this side of the wire.
/// </summary>
public class RecoveryWiringTests
{
    private static LineSnapshot Line(LineRefusal refusal) => new(
        FirstObject: 0,
        LastObject: 1,
        PrefixChars: 0,
        Words: 2,
        Left: 0.1,
        Top: 0.1,
        Right: 0.5,
        Bottom: 0.12,
        Baseline: 0.11,
        FontSizePts: 12,
        ColorRgb: 0,
        Refusal: refusal,
        Text: "text",
        FontName: "Times");

    [Fact]
    public void a_page_with_a_shaped_line_asks_for_it_to_be_read()
    {
        Assert.True(LineReader.NeedsReshaping(new[] { Line(LineRefusal.ComplexScript) }));
    }

    /// <summary>
    /// ⚠️ ONE SHAPED LINE IS ENOUGH. A page is usually mostly ordinary text
    /// with the Burmese in it somewhere, and asking about the page is the
    /// question, not asking about every line on it.
    /// </summary>
    [Fact]
    public void one_shaped_line_among_many_is_enough_to_ask()
    {
        var lines = new[]
        {
            Line(LineRefusal.None),
            Line(LineRefusal.Justified),
            Line(LineRefusal.ComplexScript),
            Line(LineRefusal.None),
        };
        Assert.True(LineReader.NeedsReshaping(lines));
    }

    /// <summary>
    /// ⚠️ AND NO OTHER REFUSAL ASKS. Every one of these means a line cannot be
    /// retyped for some reason of its own, and none of them is a reason to
    /// spend seventeen seconds reading a font.
    /// </summary>
    [Theory]
    [InlineData(LineRefusal.None)]
    [InlineData(LineRefusal.NotUpright)]
    [InlineData(LineRefusal.MixedStyle)]
    [InlineData(LineRefusal.OutOfOrder)]
    [InlineData(LineRefusal.NoFontName)]
    [InlineData(LineRefusal.NoObjects)]
    [InlineData(LineRefusal.PartialSpan)]
    [InlineData(LineRefusal.Justified)]
    [InlineData(LineRefusal.Gapped)]
    [InlineData(LineRefusal.ForeignObject)]
    public void no_other_refusal_asks_for_seventeen_seconds_of_work(LineRefusal refusal)
    {
        Assert.False(LineReader.NeedsReshaping(new[] { Line(refusal) }));
    }

    [Fact]
    public void a_page_with_no_lines_at_all_asks_for_nothing()
    {
        Assert.False(LineReader.NeedsReshaping(Array.Empty<LineSnapshot>()));
        Assert.False(LineReader.NeedsReshaping(null));
    }

    /// <summary>
    /// The gateway is where the signal is acted on, because it is the one place
    /// a page's lines are read.
    ///
    /// ⚠️ CHECKED IN THE SOURCE because the call is a static extern into the
    /// native library, which a test assembly cannot stand in for. What can be
    /// checked is that the decision and the call are in the same place, and
    /// that the decision is the shared one rather than a second copy of it.
    /// </summary>
    [Fact]
    public void the_gateway_asks_when_the_lines_say_to()
    {
        string source = Source("PdfEditorApp", "Interop", "LineGateway.cs");

        Assert.Contains("LineReader.NeedsReshaping(lines)", source);
        Assert.Contains("RenderCoreNative.prepare_recovery(docHandle, pageIndex)", source);

        // The call sits INSIDE the question, not beside it.
        int asked = source.IndexOf("LineReader.NeedsReshaping(lines)", StringComparison.Ordinal);
        int called = source.IndexOf("RenderCoreNative.prepare_recovery", StringComparison.Ordinal);
        Assert.True(asked < called, "the page is prepared before anyone asks whether it needs it");
    }

    /// <summary>
    /// ⚠️ AND NEVER WAITS FOR IT. The gateway runs on the UI thread every time
    /// a page is looked at, and asking for the reading before it is ready blocks
    /// for the whole seventeen seconds. That freeze is precisely what
    /// prepare_recovery exists to prevent, so the readiness question comes
    /// first and an unready page simply keeps PDFium's lines for now.
    /// </summary>
    [Fact]
    public void the_gateway_never_waits_for_the_reading()
    {
        string source = Source("PdfEditorApp", "Interop", "LineGateway.cs");

        int asked = source.IndexOf("recovery_is_ready(docHandle, pageIndex)", StringComparison.Ordinal);
        int loaded = source.IndexOf("RecoveryGateway.Load(docHandle, pageIndex)", StringComparison.Ordinal);

        Assert.True(asked > 0, "nothing asks whether the reading is ready");
        Assert.True(loaded > 0, "nothing reads it");
        Assert.True(asked < loaded, "the reading is asked for before anyone checks it is ready");

        // And an unready page says so, or the caller would cache PDFium's
        // fragments and never look again. What it says is a context whose
        // Settled is false; that the flag really is false is asserted where it
        // can be run, in RecoveredLineTests.
        Assert.Contains("PageTextContext.Preparing(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ THE FRAGMENTS ARE REPLACED IN ONE PLACE. If any other path built the
    /// page's lines the reader would meet 150 scrambled fragments on one route
    /// and 18 real lines on another, depending on how they got there.
    /// </summary>
    [Fact]
    public void the_gateway_is_where_the_two_readings_are_merged()
    {
        string source = Source("PdfEditorApp", "Interop", "LineGateway.cs");
        Assert.Contains("RecoveredLines.Merge(lines,", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second gate: a recovered line brings its own positions, and the
    /// region model must not be consulted for it.
    /// </summary>
    /// <remarks>
    /// ⚠️ THIS IS WHERE THE READER WAS STOPPED. The region model is built from
    /// what PDFium reads, so nothing in it spells the recovered text, and the
    /// equality check that guards a caret refused every Burmese line with "This
    /// text cannot be edited in place yet" even after the core could read it
    /// perfectly. The branch has to come BEFORE the lookup, not after it.
    /// </remarks>
    [Fact]
    public void a_recovered_line_never_asks_the_reader_that_could_not_read_it()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int begin = vm.IndexOf("public bool BeginInPlaceEdit(", StringComparison.Ordinal);
        Assert.True(begin > 0);

        string body = vm[begin..(begin + 2500)];
        int branch = body.IndexOf("unit.Line?.Recovered is { } recovered", StringComparison.Ordinal);
        int lookup = body.IndexOf("TextRegionHitTest.LineAt(", StringComparison.Ordinal);

        Assert.True(branch > 0, "nothing notices a recovered line");
        Assert.True(lookup > 0);
        Assert.True(branch < lookup, "the region model is consulted before the recovered branch");
    }

    /// <summary>
    /// ⚠️ ROUTED, NOT GUESSED. A recovered line has no object range, so the two
    /// writers addressed by objects must never see one. The route says which
    /// writer takes it and everything derives from that.
    /// </summary>
    [Fact]
    public void a_recovered_line_commits_through_its_own_writer()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        Assert.Contains("line.Route == LineWriter.RecoveryWriter", vm, StringComparison.Ordinal);
        Assert.Contains("EditRecoveredLine(line, _selectedLinePage, newText)", vm, StringComparison.Ordinal);
        Assert.Contains("Interop.RecoveryGateway.Retype(", vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// One method's source, from its signature to the start of the next member.
    /// </summary>
    /// <remarks>
    /// ⚠️ NOT A FIXED NUMBER OF CHARACTERS. This was `vm[at..(at + 2600)]`, and
    /// adding seven lines of comment to the method under test silently moved
    /// its tail outside the window, so two tests failed for having nothing to
    /// look at rather than for anything being wrong.
    /// </remarks>
    private static string MethodBodyAt(string source, int at)
    {
        int end = source.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + 1, StringComparison.Ordinal);
        if (alt >= 0 && (end < 0 || alt < end)) { end = alt; }
        return end < 0 ? source[at..] : source[at..end];
    }

    /// <summary>
    /// ⚠️ ONE CLICK TO CARRY ON IN THE NEXT WORD, NOT TWO. Clicking about
    /// inside one word moves the caret with a single click; moving to the next
    /// word used to take two, one to frame it and one to put the caret in.
    /// That pair is right for ARRIVING at text and wrong for text a reader is
    /// already editing.
    /// </summary>
    [Fact]
    public void a_click_on_another_word_carries_the_edit_over_in_one_go()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int away = page.IndexOf("Any other press drops the box", StringComparison.Ordinal);
        Assert.True(away > 0, "the click-away path is no longer where this test looks");

        int moved = page.IndexOf("MoveInPlaceEditTo(", away, StringComparison.Ordinal);
        Assert.True(moved > 0, "the click-away path no longer carries the edit over");

        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int decl = vm.IndexOf("public bool MoveInPlaceEditTo(", StringComparison.Ordinal);
        Assert.True(decl > 0);
        string body = MethodBodyAt(vm, decl);

        // ⚠️ AND THE MOVE COMMITS FOR ITSELF, WHICH IS THE WHOLE BUG. The
        // commit used to be the CALLER'S, done just before the call, and
        // CommitInPlaceEdit ENDS the edit: this method then refused at a guard
        // on IsEditingInPlace, every single time, and the one-click move never
        // once happened though it was shipped and reported as working. The
        // guard it is allowed to keep is the mode, which a commit does not
        // change.
        Assert.DoesNotContain("if (!IsEditingInPlace) { return false; }", body);
        Assert.Contains("if (!IsEditMode) { return false; }", body, StringComparison.Ordinal);

        int commit = body.IndexOf("CommitInPlaceEdit();", StringComparison.Ordinal);
        int select = body.IndexOf("SelectTextUnitAt(pageIndex, normX, normY)", StringComparison.Ordinal);
        Assert.True(commit > 0, "the move no longer commits the edit it is leaving");
        Assert.True(
            select > commit,
            "the next word is selected before the one being left is committed, "
            + "which would write the typed text to the wrong unit");

        Assert.Contains("CanEdit: true", body, StringComparison.Ordinal);
        Assert.Contains("BeginInPlaceEdit(pageIndex, normX, normY)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ A CLICK INSIDE THE BOX MUST REACH THE OTHER WORDS IN IT. The box is
    /// the BLOCK'S and the edit is one LINE of it, so the in-box branch treated
    /// every click in a paragraph as a click on the line being edited and
    /// placed the caret among that line's glyphs however far off it landed. The
    /// caret could not leave the word it started in, and the reader had to
    /// click out of the box and back in to reach the next one.
    /// </summary>
    [Fact]
    public void a_click_elsewhere_in_the_box_carries_the_edit_to_that_word()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int inside = page.IndexOf(
            "if (ViewModel.TextUnitBoxContains(content.Page, nx, ny))",
            StringComparison.Ordinal);
        Assert.True(inside > 0, "the in-box press path is no longer where this test looks");

        // Deep inside a switch, so the lines are short and the indent is not:
        // 2,000 characters stopped one line above the caret this compares against
        // and the test failed for not being able to see it.
        string branch = page[inside..Math.Min(page.Length, inside + 3000)];

        int carried = branch.IndexOf("MoveInPlaceEditTo(", StringComparison.Ordinal);
        int caret = branch.IndexOf("PlaceInPlaceCaret(", StringComparison.Ordinal);
        Assert.True(carried > 0, "a click inside the box can no longer reach another word");
        Assert.True(
            caret > carried,
            "the caret is placed in the line being edited before the click is "
            + "given a chance to move the edit to the line it landed on");

        // And only when the click is NOT on the line being edited, or clicking
        // about inside one word would commit and reopen it on every click.
        Assert.Contains("!ViewModel.InPlaceEditCovers(content.Page, nx, ny)",
            branch, StringComparison.Ordinal);

        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int decl = vm.IndexOf("public bool InPlaceEditCovers(", StringComparison.Ordinal);
        Assert.True(decl > 0, "nothing says which line the edit is actually on");
        string body = vm[decl..Math.Min(vm.Length, decl + 400)];

        // The LINE being edited, not the block's box: the two differ by every
        // other line of the paragraph, which is the entire point.
        Assert.Contains("_lineEditLine", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TextUnitBoxContains", body);
    }

    /// <summary>
    /// ⚠️ AND THE ARROWS REACH THE OTHER LINES TOO. Clicking is not the only
    /// way anyone moves about in text. Left at the start of a line and Right at
    /// its end had nowhere to go, and Up and Down did nothing at all.
    /// </summary>
    [Fact]
    public void the_arrows_run_off_a_line_onto_the_next_one()
    {
        string page = Source("PdfEditorApp", "MainPage.xaml.cs");
        int keys = page.IndexOf(
            "ViewModel.IsEditingInPlace && !IsTextInputFocused", StringComparison.Ordinal);
        Assert.True(keys > 0);
        string table = page[keys..Math.Min(page.Length, keys + 2400)];

        foreach (string key in new[] { "Left", "Right", "Up", "Down" })
        {
            Assert.Contains(
                "VirtualKey." + key + " ? () => ViewModel.InPlaceArrow" + key + "(extend)",
                table, StringComparison.Ordinal);
        }

        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        // ⚠️ THE ARROWS ASK THE BUFFER FIRST AND CROSS ONLY WHEN IT WILL NOT
        // MOVE. That test is what makes the whole thing script-independent: the
        // buffer is walking the PAGE'S cluster boundaries, so "it did not move"
        // means the caret is genuinely at an end of its piece, whatever the
        // script decided a cluster was.
        foreach (string side in new[] { "Left", "Right" })
        {
            int at = vm.IndexOf(
                "public void InPlaceArrow" + side + "(", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("Arrow(", MethodBodyAt(vm, at), StringComparison.Ordinal);
        }

        string arrow = MethodBodyAt(vm, vm.IndexOf(
            "private void Arrow(int step, bool extend)", StringComparison.Ordinal));

        int moved = arrow.IndexOf("InPlaceMoveLeft(extend)", StringComparison.Ordinal);
        int crossed = arrow.IndexOf("CrossFromEdge(step)", StringComparison.Ordinal);
        Assert.True(moved > 0, "the arrows no longer ask the buffer");
        Assert.True(crossed > moved, "the edit is carried away before the buffer has been asked");

        // ⚠️ AND A COLLAPSING SELECTION IS NOT A CARET THAT WOULD NOT MOVE.
        // Left with a selection up puts the caret at its start, which may be
        // where it already was, and that would read as an edge and carry the
        // edit into the word next door.
        Assert.Contains("hadSelection", arrow, StringComparison.Ordinal);

        // Held shift never crosses, on any of the four.
        foreach (string key in new[] { "Left", "Right", "Up", "Down" })
        {
            int at = vm.IndexOf(
                "public void InPlaceArrow" + key + "(", StringComparison.Ordinal);
            string body = MethodBodyAt(vm, at);
            Assert.True(
                body.Contains("extend", StringComparison.Ordinal),
                "InPlaceArrow" + key + " no longer considers held shift");
        }
        Assert.Contains("if (extend || hadSelection", arrow, StringComparison.Ordinal);

        // ⚠️ WHERE THE CARET GOES IS ASKED OF THE STOPS, NOT OF A BASELINE.
        // The old crossing searched for the nearest line in a direction and so
        // could not reach the next WORD, because every word of a line shares its
        // baseline and was excluded by the tolerance.
        string cross = MethodBodyAt(vm, vm.IndexOf(
            "private bool CrossFromEdge(int step)", StringComparison.Ordinal));
        Assert.Contains("CaretStops.IndexOf(_caretStops", cross, StringComparison.Ordinal);
        Assert.Contains("at + step", cross, StringComparison.Ordinal);
        Assert.DoesNotContain("Baseline", cross);

        // Up and Down keep a column, and put it back after the move that clears
        // it: without that, passing through a short word loses the reader's
        // place in the paragraph for good.
        string vertical = MethodBodyAt(vm, vm.IndexOf(
            "private bool Vertical(int direction)", StringComparison.Ordinal));
        Assert.Contains("_caretDesiredX ?? CaretXOf", vertical, StringComparison.Ordinal);
        Assert.Contains("CaretStops.NearestTo(stops, column)", vertical, StringComparison.Ordinal);
        int move = vertical.IndexOf("MoveToStopOn(line", StringComparison.Ordinal);
        int keep = vertical.IndexOf("_caretDesiredX = column", StringComparison.Ordinal);
        Assert.True(move > 0 && keep > move, "the column is kept before the move clears it");

        // The neighbouring line is still found by baseline, and still aimed at
        // through the middle of its box, because a paragraph has stripes of
        // nothing between its lines that an edge falls into.
        string next = MethodBodyAt(vm, vm.IndexOf(
            "private List<LineSnapshot> NextVisualLine(int direction)", StringComparison.Ordinal));
        Assert.Contains("(line.Baseline - from) * direction", next, StringComparison.Ordinal);
        Assert.Contains("VisualLineAt(_lineEditPage, found)", next, StringComparison.Ordinal);

        string onto = MethodBodyAt(vm, vm.IndexOf(
            "private bool MoveToStopOn(", StringComparison.Ordinal));
        Assert.Contains("(piece.Top + piece.Bottom) / 2", onto, StringComparison.Ordinal);
        Assert.Contains("MoveInPlaceEditTo(_lineEditPage, stop.X", onto, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ HOME AND END MEAN THE VISUAL LINE. A recovered piece on the Hindi
    /// book is one WORD, so these reached the ends of a word and were very close
    /// to useless.
    /// </summary>
    [Fact]
    public void home_and_end_run_to_the_ends_of_the_visual_line()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        foreach (var (name, first) in new[] { ("Home", "true"), ("End", "false") })
        {
            int at = vm.IndexOf(
                "public void InPlaceMove" + name + "(bool extend = false)", StringComparison.Ordinal);
            Assert.True(at > 0, "InPlaceMove" + name + " is no longer where this test looks");

            string body = MethodBodyAt(vm, at);
            Assert.Contains("MoveToLineEdge(first: " + first + ")", body, StringComparison.Ordinal);

            // And held shift still selects within the piece, as it always did.
            Assert.Contains("_lineEdit!.Move" + name + "(extend)", body, StringComparison.Ordinal);
        }

        string edge = MethodBodyAt(vm, vm.IndexOf(
            "private bool MoveToLineEdge(bool first)", StringComparison.Ordinal));

        // ⚠️ AND IT STANDS DOWN ONCE THE READER HAS TYPED. The stops describe
        // the text the PAGE drew; past that the buffer's own answer is the
        // honest one.
        Assert.Contains("_lineEdit.IsChanged", edge, StringComparison.Ordinal);
        Assert.Contains("_caretStops[0]", edge, StringComparison.Ordinal);
        Assert.Contains("_caretStops[^1]", edge, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ THE CARET SPANS THE LINE, THE WRITER STILL TAKES ONE PIECE. This is
    /// the promise that keeps the recovery and the writer out of the caret
    /// change: a commit still goes back one recovered line at a time, with its
    /// identity handed over exactly as it came.
    /// </summary>
    [Fact]
    public void a_visual_line_is_joined_for_the_caret_and_not_for_the_writer()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        string visual = MethodBodyAt(vm, vm.IndexOf(
            "private List<LineSnapshot> VisualLineAt(", StringComparison.Ordinal));
        Assert.Contains("line.Recovered is null", visual, StringComparison.Ordinal);
        Assert.Contains("RecoveredWordTolerance", visual, StringComparison.Ordinal);
        Assert.Contains("a.Left.CompareTo(b.Left)", visual, StringComparison.Ordinal);

        // Each piece keeps its own offsets and its own identity: the stops carry
        // a piece number rather than being renumbered into one long string.
        string stops = MethodBodyAt(vm, vm.IndexOf(
            "private static List<CaretStop> StopsOf(", StringComparison.Ordinal));
        Assert.Contains("recovered.Text.Length", stops, StringComparison.Ordinal);
        Assert.Contains("TextDirection.LeftToRight", stops, StringComparison.Ordinal);
        Assert.Contains("CaretStops.InVisualOrder(all)", stops, StringComparison.Ordinal);

        // Nothing about how a line is written has been touched.
        Assert.Contains("Interop.RecoveryGateway.Retype(", vm, StringComparison.Ordinal);
        Assert.Contains("EditRecoveredLine(line, _selectedLinePage, newText)", vm, StringComparison.Ordinal);

        // ⚠️ AND THE ORDINARY PATH IS LEFT ALONE. PDFium's line already IS the
        // visual line, so there is nothing to join and the crossing stays inert.
        int plain = vm.IndexOf("public bool BeginInPlaceEdit(", StringComparison.Ordinal);
        Assert.Contains("ForgetCaretStops();", MethodBodyAt(vm, plain), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ CAPTURED BEFORE, PUSHED AFTER, exactly as a form edit does it. The
    /// core changes nothing when it refuses, and an entry pushed anyway would be
    /// a Ctrl+Z that appears to do nothing.
    /// </summary>
    [Fact]
    public void a_refused_retype_leaves_no_undo_step_behind()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = vm.IndexOf("private bool EditRecoveredLine(", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = MethodBodyAt(vm, at);

        int captured = body.IndexOf("Capture(HistoryScope.Document", StringComparison.Ordinal);
        int wrote = body.IndexOf("RecoveryGateway.Retype(", StringComparison.Ordinal);
        int pushed = body.IndexOf("_history.Push(before)", StringComparison.Ordinal);

        Assert.True(captured > 0 && wrote > captured && pushed > wrote,
            "the undo step is not captured before the write and pushed after it");

        // The refusal returns before the push.
        int refused = body.IndexOf("Status = \"This line could not be retyped.\";", StringComparison.Ordinal);
        Assert.True(refused > 0 && refused < pushed);
    }

    /// <summary>
    /// ⚠️ EVERY PER-PAGE CACHE, AND THE READING TOO. This replaces the whole
    /// document behind a NEW handle, so the lines, the words and the reading the
    /// core had for the old one all describe a document that no longer exists.
    /// The reading is started again here rather than at the reader's next click,
    /// which is the entire reason prepare_recovery exists.
    /// </summary>
    [Fact]
    public void a_retype_throws_away_everything_it_invalidated()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = vm.IndexOf("private bool EditRecoveredLine(", StringComparison.Ordinal);
        string body = MethodBodyAt(vm, at);

        Assert.Contains("RestoreDocumentBytes(bytes);", body, StringComparison.Ordinal);
        Assert.Contains("_linesByPage.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("_clustersByPage.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("prepare_recovery(_documentHandle, page)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ A WORD ON A RECOVERED BASELINE IS A FRAGMENT OF THAT LINE, AND THE
    /// LINE IS WHAT THE READER MEANT.
    ///
    /// RecoveredLines.Merge takes PDFium's refused fragments out of the LINE
    /// list, and nothing was doing the same for the WORD list. A recovered
    /// line's box is the FACE's height at its size rather than the leading the
    /// page was set with, so a paragraph has stripes of nothing between its
    /// lines: measured on a real file, five of one page's twenty-eight gaps are
    /// wider than the hit tolerance can close, the worst fifteen points. A click
    /// landing in one fell through to a scrambled fragment beginning with a
    /// vowel sign, which spells nothing, and was then refused as uneditable
    /// while the paragraph around it read perfectly.
    /// </summary>
    [Fact]
    public void a_click_on_a_fragment_of_a_recovered_line_gets_the_line()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int at = vm.IndexOf("public bool SelectTextUnitAt(\n        int pageIndex", StringComparison.Ordinal);
        if (at < 0)
        {
            at = vm.IndexOf("public bool SelectTextUnitAt(\r\n        int pageIndex", StringComparison.Ordinal);
        }
        Assert.True(at > 0, "the selecting method is gone");

        string body = vm[at..Math.Min(vm.Length, at + 3000)];

        int found = body.IndexOf("var word = WordAt(pageIndex, normX, normY);", StringComparison.Ordinal);
        int swapped = body.IndexOf("candidate.Recovered is null", StringComparison.Ordinal);
        int picked = body.IndexOf("TextUnitSelection? picked =", StringComparison.Ordinal);

        Assert.True(found > 0, "nothing looks for a word");
        Assert.True(swapped > found, "a fragment is never traded for its line");
        Assert.True(picked > swapped, "the trade happens after the unit has been chosen");

        // ⚠️ ONLY WHEN THE LINE ITSELF DID NOT ANSWER. A click that landed
        // squarely on an editable line must keep it; this exists for the ones
        // that missed.
        Assert.Contains("if (word is not null && line is not { CanEdit: true })",
            body, StringComparison.Ordinal);

        // And by baseline, which is the only thing a fragment and its line share.
        Assert.Contains("Math.Abs(candidate.Baseline - word.Baseline) >= RecoveredWordTolerance",
            body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ AN EDIT IS A NEW DOCUMENT, AND THE READING HAS TO GO WITH IT.
    ///
    /// Editing a recovered line hands back a whole new document, which the app
    /// opens under a NEW handle. The index is cached against the handle it was
    /// built for, so without handing it over, prepare_recovery builds the whole
    /// thing again after every single edit. Measured on a real Burmese file:
    /// 41 ms of writing, then 12,000 ms of rebuilding the same answer.
    ///
    /// ⚠️ AND BEFORE THE CLOSE. Closing a document drops its cached reading, so
    /// a hand-over after the close has nothing left to hand over.
    /// </summary>
    [Fact]
    public void the_reading_is_handed_to_the_document_that_replaces_it()
    {
        string vm = Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        int at = vm.IndexOf("private void RestoreDocumentBytes(", StringComparison.Ordinal);
        Assert.True(at > 0, "the restoring method is gone");

        string body = vm[at..Math.Min(vm.Length, at + 2000)];

        int opened = body.IndexOf("open_document_from_bytes", StringComparison.Ordinal);
        int handed = body.IndexOf("adopt_recovery(_documentHandle, restored)", StringComparison.Ordinal);
        int closed = body.IndexOf("CloseCurrentDocument()", StringComparison.Ordinal);

        Assert.True(opened > 0, "nothing opens the replacement");
        Assert.True(handed > opened,
            "the replacement is left to build the whole index over again");
        Assert.True(closed > handed,
            "the reading is handed over after the close, which has already dropped it");
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
