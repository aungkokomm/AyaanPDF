using System;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// View and Edit.
///
/// ⚠️ NOT ANOTHER GESTURE LAYER, and it exists to remove one. A single pointer
/// chain was answering two questions at once: a press on the page had to be a
/// reader's text selection AND a possible object pick AND a possible text-unit
/// selection, and every capability made it longer. The mode splits it, so each
/// half only decides among things that belong together.
/// </summary>
public class AppModeTests
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

    // ---------------- which tools a mode offers ----------------

    [Fact]
    public void reading_keeps_every_tool_that_marks_a_page_up()
    {
        // ⚠️ READING IS NOT READ-ONLY, and this is the test that says so. The
        // first cut of the mode left a reader holding the hand and Select,
        // which made highlighting a sentence something you had to leave
        // reading to do. Marking a page up is part of reading it.
        var tools = ToolCatalog.ForMode(AppMode.View).Select(t => t.Mode).ToList();

        Assert.Equal(
            new[]
            {
                ToolMode.Hand, ToolMode.Select, ToolMode.Highlight, ToolMode.Draw,
                ToolMode.Shape, ToolMode.Note, ToolMode.Stamp,
            },
            tools);
    }

    [Fact]
    public void edit_offers_every_tool_there_is()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.ForMode(AppMode.Edit).Count);
    }

    [Theory]
    [InlineData(ToolMode.Highlight)]
    [InlineData(ToolMode.Draw)]
    [InlineData(ToolMode.Shape)]
    [InlineData(ToolMode.Note)]
    [InlineData(ToolMode.Stamp)]
    public void a_tool_that_marks_the_page_is_offered_in_both_modes(ToolMode tool)
    {
        // None of these touch the document's own content. They lay a mark over
        // it, which is what a reader does to a page they are reading.
        Assert.True(ToolCatalog.Offers(AppMode.View, tool));
        Assert.True(ToolCatalog.Offers(AppMode.Edit, tool));
    }

    [Theory]
    [InlineData(ToolMode.Text)]
    [InlineData(ToolMode.Link)]
    public void only_the_tools_that_change_the_document_are_edit_only(ToolMode tool)
    {
        // The text box, because only Edit can reopen one once it is placed, so
        // a reader offered it would end up with a box they cannot retype. The
        // hyperlink, because that is document structure and not a mark.
        Assert.False(ToolCatalog.Offers(AppMode.View, tool));
        Assert.True(ToolCatalog.Offers(AppMode.Edit, tool));
    }

    [Fact]
    public void the_rail_keeps_its_order_in_both_modes()
    {
        // The rail is muscle memory. A mode that reshuffled it would make the
        // two look like different applications.
        var edit = ToolCatalog.ForMode(AppMode.Edit).Select(t => t.Mode).ToList();
        var view = ToolCatalog.ForMode(AppMode.View).Select(t => t.Mode).ToList();

        Assert.Equal(view, edit.Where(view.Contains).ToList());
    }

    // ---------------- the keyboard cannot get round it ----------------

    [Theory]
    [InlineData('T')]
    [InlineData('L')]
    public void a_shortcut_cannot_arm_a_tool_the_rail_is_hiding(char key)
    {
        // ⚠️ THE WORST OF BOTH OTHERWISE: the rail shows one set of tools and a
        // keystroke arms one that is not in it, with nothing on screen saying
        // so. The hiding is now down to two, but the rule is the same rule.
        Assert.Null(ToolCatalog.ForShortcut(key, AppMode.View));
        Assert.NotNull(ToolCatalog.ForShortcut(key, AppMode.Edit));
    }

    [Theory]
    [InlineData('H', ToolMode.Hand)]
    [InlineData('V', ToolMode.Select)]
    [InlineData('U', ToolMode.Highlight)]
    [InlineData('D', ToolMode.Draw)]
    [InlineData('R', ToolMode.Shape)]
    [InlineData('N', ToolMode.Note)]
    [InlineData('S', ToolMode.Stamp)]
    public void a_reading_tool_keeps_its_shortcut_in_both_modes(char key, ToolMode tool)
    {
        // The other half of the rule above: a tool the rail IS showing must be
        // reachable from the keyboard in the mode showing it.
        Assert.Equal(tool, ToolCatalog.ForShortcut(key, AppMode.View)?.Mode);
        Assert.Equal(tool, ToolCatalog.ForShortcut(key, AppMode.Edit)?.Mode);
    }

    [Fact]
    public void a_key_that_is_no_tool_is_still_no_tool()
    {
        Assert.Null(ToolCatalog.ForShortcut('Z', AppMode.Edit));
        Assert.Null(ToolCatalog.ForShortcut('Z', AppMode.View));
    }

    [Fact]
    public void the_key_handler_goes_through_the_mode_and_not_round_it()
    {
        // The modeless lookup still exists and would happily arm a hidden tool.
        string page = Page();

        Assert.Contains("ViewModel.ToolForShortcut((char)e.Key)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolCatalog.ForShortcut((char)e.Key)", page, StringComparison.Ordinal);
    }

    // ---------------- what the mode does when it changes ----------------

    [Fact]
    public void every_document_opens_in_view()
    {
        // ⚠️ Carrying Edit across an open would hand the next reader a file
        // already armed for changes they did not ask to make.
        string body = Body(ViewModel(), "public DocumentOpenOutcome OpenDocument(");

        Assert.Contains("Mode = AppMode.View;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void entering_edit_always_arms_select()
    {
        // The last tool used is whatever they left armed a document ago, and
        // arriving in a drawing tool is how a stray click becomes an ink stroke
        // nobody asked for.
        string body = Body(ViewModel(), "public AppMode Mode");

        int elseArm = body.LastIndexOf("ActiveTool = ToolMode.Select;", StringComparison.Ordinal);
        Assert.True(elseArm > 0, "entering Edit does not arm Select");
        Assert.Equal(2, body.Split("ActiveTool = ToolMode.Select;").Length - 1);
    }

    [Fact]
    public void leaving_edit_takes_every_editing_selection_with_it()
    {
        // A selection that outlived the mode would still be drawn, still be
        // moved by the arrow keys and still be deleted by Backspace, none of
        // which View mode can undo or even show.
        string body = Body(ViewModel(), "public AppMode Mode");

        Assert.Contains("ClearTextUnitSelection()", body, StringComparison.Ordinal);
        Assert.Contains("ClearAnnotationSelection()", body, StringComparison.Ordinal);
        Assert.Contains("ClearGuideSelection()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void switching_mode_commits_what_was_being_typed()
    {
        // Leaving Edit is not the same as pressing Escape: the reader typed it.
        string body = Body(Page(), "private void SetMode(AppMode mode)");

        int commit = body.IndexOf("ViewModel.CommitInPlaceEdit()", StringComparison.Ordinal);
        int set = body.IndexOf("ViewModel.Mode = mode;", StringComparison.Ordinal);

        Assert.True(commit > 0 && commit < set, "the mode changes before the editor is committed");
        Assert.Contains("CommitTextEdit()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_rail_is_bound_to_the_modes_tools_and_not_the_whole_catalog()
    {
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.Tools}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{x:Bind viewport:ToolCatalog.All}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_control_says_which_mode_it_is_in()
    {
        // Two labelled halves with one lit, not a toggle: a toggle says
        // "pressed" or "not pressed" and the reader has to know which is which.
        string xaml = Source("PdfEditorApp", "MainPage.xaml");
        string page = Page();

        Assert.Contains("x:Name=\"ViewModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditModeButton\"", xaml, StringComparison.Ordinal);

        string body = Body(page, "private void ApplyModeVisuals()");
        Assert.Contains("ViewModeButton.Background", body, StringComparison.Ordinal);
        Assert.Contains("EditModeButton.Background", body, StringComparison.Ordinal);
        Assert.Contains("IsEditMode", body, StringComparison.Ordinal);
    }

    // ---------------- what the control draws ----------------

    /// <summary>The two mode buttons, as the markup writes them.</summary>
    private static string ModeControl()
    {
        string xaml = Source("PdfEditorApp", "MainPage.xaml");
        int at = xaml.IndexOf("x:Name=\"ViewModeButton\"", StringComparison.Ordinal);
        Assert.True(at > 0, "there is no View button");

        int end = xaml.IndexOf("</StackPanel>", at, StringComparison.Ordinal);
        return xaml[at..(end > at ? end : xaml.Length)];
    }

    [Fact]
    public void the_reading_half_is_drawn_as_a_book()
    {
        // ⚠️ THE ICON IS THE LABEL. There is no room for a word beside it in a
        // 36-wide rail, so whatever it draws is the whole of what the half
        // says. It used to be E890, an eye, which says "look at something" and
        // could as easily have meant preview, presentation or a page layout.
        string control = ModeControl();
        string xaml = Source("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("{StaticResource OpenBookPath}", control, StringComparison.Ordinal);
        Assert.Contains("<x:String x:Key=\"OpenBookPath\">", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE890;", control, StringComparison.Ordinal);
    }

    [Fact]
    public void the_edit_half_is_drawn_as_a_page_with_a_pencil_on_it()
    {
        // A BARE PENCIL WAS THE WRONG PICTURE. This app has a pen, a
        // highlighter and shapes of its own, so a pencil on the mode control
        // read as one more drawing tool rather than as "change this document".
        string control = ModeControl();

        Assert.Contains("&#xE932;", control, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE70F;", control, StringComparison.Ordinal);
    }

    // ---------------- what a reader can and cannot do ----------------

    [Fact]
    public void a_readers_click_never_picks_up_one_of_our_marks()
    {
        // ⚠️ MOST OF WHAT THE MODE IS FOR. A reader who cannot move a highlight
        // also cannot move one by accident.
        string select = SelectCase();

        int pick = select.IndexOf("ViewModel.SelectAnnotationAt(", StringComparison.Ordinal);
        Assert.True(pick > 0);

        string upTo = select[..pick];
        Assert.EndsWith("ViewModel.IsEditMode && ", upTo, StringComparison.Ordinal);
    }

    [Fact]
    public void a_readers_click_never_opens_the_text_editor()
    {
        string select = SelectCase();

        int box = select.IndexOf("TextUnitBoxContains(", StringComparison.Ordinal);
        int gate = select.IndexOf("if (ViewModel.IsEditMode)", StringComparison.Ordinal);

        Assert.True(gate > 0 && gate < box, "the text-unit box is reachable in View mode");
    }

    [Fact]
    public void a_reader_cannot_marquee_or_drop_a_signature_or_drag_a_guide()
    {
        string page = Page();

        Assert.Contains("ViewModel.IsEditMode && shiftDown", page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsEditMode && TryPlacePendingSignature(", page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsEditMode && ViewModel.PickGuideAt(", page, StringComparison.Ordinal);
    }

    [Fact]
    public void a_reader_cannot_reopen_one_of_our_text_boxes()
    {
        string body = Body(Page(), "private void ViewportHost_DoubleTapped(");

        Assert.Contains("if (!ViewModel.IsEditMode) { return; }", body, StringComparison.Ordinal);
    }

    // ---------------- what a reader KEEPS ----------------

    [Fact]
    public void a_reader_still_selects_and_copies_text()
    {
        // The oldest behaviour in the app, and the mode must not have touched
        // it: the drag runs whatever mode it is in.
        string select = SelectCase();

        int text = select.IndexOf("ViewModel.BeginTextSelection(", StringComparison.Ordinal);
        Assert.True(text > 0);

        // No mode test anywhere between the start of the reader half and here.
        int gate = select.IndexOf("if (ViewModel.IsEditMode)", StringComparison.Ordinal);
        int lastGate = select.LastIndexOf("ViewModel.IsEditMode", text - 1, StringComparison.Ordinal);
        Assert.True(lastGate < text, "the reader selection sits behind a mode test");
        Assert.True(gate < text);
    }

    [Fact]
    public void a_reader_still_follows_links_and_operates_forms()
    {
        // Both are reading a document, not changing one, and both were reachable
        // before the mode existed.
        string select = SelectCase();

        int link = select.IndexOf("ViewModel.LinkAt(content.Page, nx, ny)", StringComparison.Ordinal);
        int form = select.IndexOf("ViewModel.FillableFieldAt(content.Page, nx, ny)", StringComparison.Ordinal);

        Assert.True(link > 0, "a reader cannot follow a link");
        Assert.True(form > 0, "a reader cannot operate a form field");

        // Neither is behind a mode test.
        foreach (int at in new[] { link, form })
        {
            int lineStart = select.LastIndexOf('\n', at) + 1;
            string line = select[lineStart..at];
            Assert.DoesNotContain("IsEditMode", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void filling_a_form_still_needs_no_mode_of_its_own()
    {
        // ⚠️ THE MODE THIS REPLACED. Form fill mode intercepted the pointer
        // whatever tool was armed, which made filling a form something a reader
        // had to discover and switch into. Retiring it was the point.
        string page = Page();
        string vm = ViewModel();

        Assert.DoesNotContain("FormFillMode", page, StringComparison.Ordinal);
        Assert.DoesNotContain("FormFillMode", vm, StringComparison.Ordinal);
        Assert.Contains("public partial bool ShowFormFields", vm, StringComparison.Ordinal);
    }
}
