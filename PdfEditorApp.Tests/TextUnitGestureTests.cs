using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The gesture that reaches the document's own text.
///
/// One click selects a unit and boxes it; the next click, inside that box, opens
/// the editor with the caret where it landed. Read out of the source, because
/// these live in the WinUI project and a net10.0 test assembly cannot load one.
/// </summary>
public class TextUnitGestureTests
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

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    private static string ViewModel() =>
        Source("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string Xaml() => Source("PdfEditorApp", "MainPage.xaml");

    /// <summary>One method's code, comment lines dropped so that commenting a
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

    private static string SelectCase()
    {
        string page = Page();
        int at = page.IndexOf("case ToolMode.Select:", StringComparison.Ordinal);
        Assert.True(at > 0, "the Select tool no longer has a case");

        int next = page.IndexOf("\n            case ToolMode.", at + 10, StringComparison.Ordinal);
        return page[at..(next > at ? next : page.Length)];
    }

    // ---------------- click versus drag ----------------

    [Fact]
    public void a_press_that_never_moved_selects_a_unit_and_one_that_moved_does_not()
    {
        // ⚠️ THE WHOLE DISAMBIGUATION. Both gestures start with the same press
        // on the same pixel, so they can only be told apart on release. A drag
        // is the reader selecting text to copy or highlight, which is older than
        // this feature and must keep working untouched.
        string released = Body(Page(), "private void ViewportHost_PointerReleased(");

        int selecting = released.IndexOf("_isSelectingText", StringComparison.Ordinal);
        Assert.True(selecting > 0, "the text-selection release is gone");

        int moved = released.IndexOf("MovedSincePress(e)", selecting, StringComparison.Ordinal);
        int select = released.IndexOf("SelectTextUnitAt(", selecting, StringComparison.Ordinal);

        Assert.True(moved > 0, "a click and a drag are not told apart");
        Assert.True(select > moved, "the unit is selected without asking whether it was a drag");
        Assert.Contains("!MovedSincePress(e)", released, StringComparison.Ordinal);
    }

    [Fact]
    public void the_reader_selection_still_runs_for_the_whole_drag()
    {
        // Begin on press, update on move, end on release, exactly as before.
        string page = Page();

        Assert.Contains("ViewModel.BeginTextSelection(content.Page, content.X, content.Y);",
                        page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UpdateTextSelection(content.Page, content.X, content.Y);",
                        page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.EndTextSelection();", page, StringComparison.Ordinal);
    }

    [Fact]
    public void a_click_drops_the_empty_reader_selection_it_just_started()
    {
        // The press began one, so without this a click leaves a collapsed blue
        // selection sitting under the box it just drew.
        string released = Body(Page(), "private void ViewportHost_PointerReleased(");

        int clear = released.IndexOf("ClearReaderTextSelection()", StringComparison.Ordinal);
        int select = released.IndexOf("SelectTextUnitAt(", StringComparison.Ordinal);

        Assert.True(clear > 0 && clear < select);
    }

    [Fact]
    public void the_click_threshold_is_a_few_pixels_and_not_zero()
    {
        // A hand resting on a mouse moves a pixel or two between press and
        // release, so an exact-match test would make every click a drag.
        string page = Page();

        Assert.Contains("ClickSlopDip", page, StringComparison.Ordinal);
        string body = Body(page, "private bool MovedSincePress(");
        Assert.Contains("ClickSlopDip", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_press_position_is_recorded_where_the_press_happens()
    {
        // Measured against the same element on both ends, or the comparison is
        // between two different coordinate spaces.
        string select = SelectCase();

        Assert.Contains("_textPressAt = current.Position;", select, StringComparison.Ordinal);
        Assert.Contains("_textPressPage = content.Page;", select, StringComparison.Ordinal);

        Assert.Contains("e.GetCurrentPoint(ViewportHost).Position",
                        Body(Page(), "private bool MovedSincePress("), StringComparison.Ordinal);
    }

    [Fact]
    public void only_the_select_tool_in_edit_mode_turns_a_click_into_a_unit()
    {
        // Two conditions, both load-bearing. The highlight tool selects text
        // too, and a click with it means "make a highlight", not "edit this
        // line". And a reader in View mode selecting text to copy must not end
        // up with an editable box round it.
        string released = Body(Page(), "private void ViewportHost_PointerReleased(");

        Assert.Contains("ViewModel.IsEditMode", released, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ActiveTool == ToolMode.Select", released, StringComparison.Ordinal);
        Assert.Contains("!MovedSincePress(e)", released, StringComparison.Ordinal);
    }

    // ---------------- the box cannot swallow the second click ----------------

    [Fact]
    public void the_selection_box_is_not_hit_testable()
    {
        // ⚠️ THIS IS THE BUG THE OLD GESTURE HAD. The triple click's second
        // click opened an editor over the word, so the third landed on that
        // control and never reached the page, and a whole second interception
        // path existed only to get it back. The box must never take the pointer:
        // the viewport is the only code that knows what a click inside it means.
        string xaml = Xaml();

        int box = xaml.IndexOf("ItemsSource=\"{x:Bind PageTextOutline}\"", StringComparison.Ordinal);
        Assert.True(box > 0, "the text-unit box is not bound in the XAML");

        int close = xaml.IndexOf('>', box);
        string tag = xaml[box..close];
        Assert.Contains("IsHitTestVisible=\"False\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_the_editing_gesture_draws_takes_the_pointer()
    {
        // Every overlay the page stacks over the text is declared the same way,
        // so a new one added without the flag is the regression to catch.
        string xaml = Xaml();

        foreach (string bound in new[] { "PageTextOutline", "LinkOutlines", "SelectionRects" })
        {
            int at = xaml.IndexOf($"ItemsSource=\"{{x:Bind {bound}}}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"{bound} is not bound");
            Assert.Contains("IsHitTestVisible=\"False\"",
                            xaml[at..xaml.IndexOf('>', at)], StringComparison.Ordinal);
        }
    }

    // ---------------- the second click ----------------

    [Fact]
    public void a_click_inside_the_box_is_taken_before_anything_else_a_press_could_mean()
    {
        // Every check below it would treat the second click as an ordinary
        // press: a link followed, a form field operated, an object picked up, a
        // reader selection started.
        string select = SelectCase();

        int inBox = select.IndexOf("TextUnitBoxContains(", StringComparison.Ordinal);
        int link = select.IndexOf("ViewModel.LinkAt(", StringComparison.Ordinal);
        int form = select.IndexOf("ViewModel.FillableFieldAt(", StringComparison.Ordinal);
        int annotation = select.IndexOf("ViewModel.SelectAnnotationAt(", StringComparison.Ordinal);
        int text = select.IndexOf("ViewModel.BeginTextSelection(", StringComparison.Ordinal);

        Assert.True(inBox > 0, "a click inside the box is not recognised at all");
        Assert.True(inBox < link, "a link is followed first");
        Assert.True(inBox < form, "a form field is operated first");
        Assert.True(inBox < annotation, "an object is picked up first");
        Assert.True(inBox < text, "a reader selection is started first");
    }

    [Fact]
    public void the_second_click_carries_where_it_landed()
    {
        // Opening the editor without it would put the caret wherever the last
        // one happened to be, and the point of the second click is to say where.
        string select = SelectCase();

        int inBox = select.IndexOf("TextUnitBoxContains(", StringComparison.Ordinal);
        int open = select.IndexOf("BeginInPlaceEdit(", inBox, StringComparison.Ordinal);

        Assert.True(open > inBox);

        // ⚠️ THE POINT ITSELF, NOT AN OFFSET COMPUTED FROM IT. The caret is
        // resolved against the page's own glyphs inside the view model, so the
        // click has to arrive there as a place on the page.
        Assert.Contains("BeginInPlaceEdit(content.Page, nx, ny)",
                        select, StringComparison.Ordinal);
    }

    [Fact]
    public void the_click_places_a_caret_and_does_not_select_everything()
    {
        // ⚠️ Select-all would throw away what the click just said and make the
        // next keystroke delete the line. There is nothing to select any more:
        // the click produces one caret offset, taken from the glyph it landed
        // on, and that is the whole of what starting an edit does.
        string body = Body(ViewModel(), "public bool BeginInPlaceEdit(");

        Assert.Contains("new LineEditBuffer(unit.Text, CaretOffsetIn(glyphs, unit.Text, normX))",
                        body, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectAll()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_click_anywhere_else_drops_the_box()
    {
        string select = SelectCase();

        int inBox = select.IndexOf("TextUnitBoxContains(", StringComparison.Ordinal);
        int clear = select.IndexOf("ClearTextUnitSelection()", inBox, StringComparison.Ordinal);
        int link = select.IndexOf("ViewModel.LinkAt(", StringComparison.Ordinal);

        Assert.True(clear > inBox, "the box is never cleared by a press");
        Assert.True(clear < link, "the box outlives a click that went elsewhere");
    }

    [Fact]
    public void escape_drops_the_box_too()
    {
        string page = Page();
        int at = page.IndexOf("case VirtualKey.Escape:", StringComparison.Ordinal);
        Assert.True(at > 0);

        string body = page[at..page.IndexOf("break;", at, StringComparison.Ordinal)];
        Assert.Contains("ClearTextUnitSelection()", body, StringComparison.Ordinal);
    }

    // ---------------- what was removed ----------------

    [Fact]
    public void there_is_no_triple_click_left_anywhere()
    {
        string page = Page();

        Assert.DoesNotContain("TakeTripleClick", page, StringComparison.Ordinal);
        Assert.DoesNotContain("RememberDoubleTap", page, StringComparison.Ordinal);
        Assert.DoesNotContain("EscalateToLine", page, StringComparison.Ordinal);
        Assert.DoesNotContain("TripleClickWindow", page, StringComparison.Ordinal);
    }

    [Fact]
    public void a_double_click_no_longer_edits_the_documents_own_text()
    {
        string body = Body(Page(), "private void ViewportHost_DoubleTapped(");

        Assert.DoesNotContain("SelectPageTextAt(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenWordEditor(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_double_click_still_reopens_one_of_our_own_text_boxes()
    {
        // ⚠️ A DIFFERENT OPERATION ON A DIFFERENT THING. Our text boxes are
        // annotations we placed, and re-editing one is the Add Text path. Only
        // the document's own text moved to the new gesture.
        string body = Body(Page(), "private void ViewportHost_DoubleTapped(");

        Assert.Contains("ViewModel.HitLoadedTextBox(", body, StringComparison.Ordinal);
        Assert.Contains("OpenTextBoxEditor(target)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void there_is_no_editor_over_the_documents_text_at_all()
    {
        // ⚠️ A SETTLED PRODUCT REQUIREMENT. The word editor and the line
        // editor were duplicates that drifted, and were merged into one; that
        // one has now been removed outright. Editing the document's own text
        // happens ON the page, so no TextBox may be floated over it again.
        string page = Page();

        Assert.DoesNotContain("_wordEditor", page, StringComparison.Ordinal);
        Assert.DoesNotContain("_lineEditor", page, StringComparison.Ordinal);
        Assert.DoesNotContain("_unitEditor", page, StringComparison.Ordinal);

        // The caret and the redrawn tail are what replaced it.
        Assert.Contains("BeginInPlaceEdit(", page, StringComparison.Ordinal);
        Assert.Contains("RenderInPlaceEdit()", page, StringComparison.Ordinal);
    }

    // ---------------- the unit rule ----------------

    [Fact]
    public void the_line_is_tried_first_and_the_word_is_the_fallback()
    {
        // ⚠️ LOAD-BEARING, NOT TIDY. A justified line refuses by design, and so
        // do a mixed-style line and two labels sharing a baseline, but every
        // word on those lines is editable. A line-only rule would make a typo in
        // justified body text uncorrectable.
        string body = Body(ViewModel(), "public bool SelectTextUnitAt(");

        int line = body.IndexOf("line is { CanEdit: true }", StringComparison.Ordinal);
        int word = body.IndexOf("word is { CanEdit: true }", StringComparison.Ordinal);

        Assert.True(line > 0, "the line is not tried");
        Assert.True(word > line, "the word is not the fallback");
    }

    [Fact]
    public void a_unit_nothing_can_edit_is_still_selected()
    {
        string body = Body(ViewModel(), "public bool SelectTextUnitAt(");

        Assert.Contains("line is not null ? TextUnitSelection.From(pageIndex, line)",
                        body, StringComparison.Ordinal);
        Assert.Contains("word is not null ? TextUnitSelection.From(pageIndex, word)",
                        body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_commit_goes_through_the_existing_cores_and_not_a_third_one()
    {
        // The one thing this interaction change must not become is a new way to
        // write a document's text.
        string body = Body(ViewModel(), "public bool CommitTextUnit(");

        Assert.Contains("EditSelectedLine(newText)", body, StringComparison.Ordinal);
        Assert.Contains("EditSelectedWord(newText)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_word_caret_is_measured_from_the_start_of_the_word()
    {
        // ⚠️ A word's box covers part of its line, and the page layer counts
        // from the start of the LINE. Without subtracting what came before it,
        // clicking the first letter of the last word on a line would put the
        // caret far past the end of the text the editor holds.
        string body = Body(ViewModel(), "public int CaretOffsetFor(");

        Assert.Contains("TextUnitKind.Word", body, StringComparison.Ordinal);
        Assert.Contains("at -= leading;", body, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_box_is_the_frame_the_page_already_drew_and_not_a_second_one()
    {
        // ⚠️ A selected word was ALREADY framed here, and that frame carries a
        // padding rule that exists because a tight box round the glyphs draws
        // its rule straight through the feet of the type. A second overlay for
        // the unit would have been unpadded and would have done exactly that.
        string body = Body(ViewModel(), "private void RefreshSelectionOutlineCore()");

        Assert.Contains("_selectedTextUnit is { } word", body, StringComparison.Ordinal);
        Assert.Contains("PageTextOutline.Add(", body, StringComparison.Ordinal);
        Assert.Contains("word.CanEdit ? EditableUnitColor : RefusedUnitColor",
                        body, StringComparison.Ordinal);
    }

    [Fact]
    public void only_the_selected_unit_is_ever_outlined()
    {
        // Boxing every line a page contains would be drawing over someone's
        // document, which is why the link overlay is off unless asked for.
        string body = Body(ViewModel(), "private void RefreshSelectionOutlineCore()");

        Assert.Contains("slot.PageTextOutline.Clear()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var line in", body, StringComparison.Ordinal);
    }
}
