using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The pointer path for links: hover, click, and what must not disturb either.
///
/// ⚠️ WRITTEN AFTER A REAL FAILURE, and shaped by it. The overlay drew
/// correctly and every link test passed, yet clicking a link selected text
/// instead. The cause was that opening a document silently set Show Links back
/// to false, so the guard on the click path was false while the boxes from
/// before were still on screen. Nothing in the feature's own tests could see
/// that, because the bug was in a method belonging to document loading.
///
/// Read out of the source, because these live in the WinUI project and a
/// net10.0 test assembly cannot load one. Green here is NOT proof the pointer
/// behaves; it is proof the wiring that carries it is still in place.
/// </summary>
public class LinkInteractionWiringTests
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

    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = code.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        return code[at..(next > at ? next : code.Length)];
    }

    // ---------------- The regression itself ----------------

    [Fact]
    public void opening_a_document_does_not_switch_show_links_off()
    {
        // ⚠️ THE BUG. ClearLoadedAnnotations runs on a document open, on a page
        // structure change and on two undo paths. Setting the toggle there
        // turned the feature off underneath a reader who had asked for it, and
        // the only visible symptom was that clicking a link selected text.
        //
        // Show Links is a VIEW setting. Only the reader turns it off.
        string body = Body(ViewModel(), "private void ClearLoadedAnnotations()");

        Assert.DoesNotContain("ShowLinks = false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void opening_a_document_still_forgets_the_links_it_read_from_the_last_one()
    {
        // The cache must go even though the toggle stays: a link's handle is an
        // annotation index, and one held over would aim a delete at whatever now
        // sits at that position.
        string body = Body(ViewModel(), "private void ClearLoadedAnnotations()");

        Assert.Contains("_linksByPage.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("ClearSelectedLink()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_rebuilt_layout_puts_the_link_outlines_back()
    {
        // The page cards are new objects after a relayout, so anything drawn
        // ONTO a card rather than derived from the document has to be redrawn.
        // Without this, a document reopened with Show Links still on comes up
        // with the menu ticked and no boxes.
        string code = ViewModel();

        int distribute = code.IndexOf("DistributeAnnotationsToSlots();", StringComparison.Ordinal);
        int rebuilt = code.IndexOf("LayoutRebuilt?.Invoke();", StringComparison.Ordinal);
        int refresh = code.IndexOf("        RefreshLinkOutlines();\n\n        OnPropertyChanged(nameof(ContentWidth));",
                                   StringComparison.Ordinal);

        Assert.True(distribute > 0 && rebuilt > distribute, "the layout path moved");
        Assert.InRange(refresh, distribute, rebuilt);
    }

    // ---------------- Hover ----------------

    [Fact]
    public void the_pointer_shows_a_hand_over_a_link()
    {
        // Nothing in the PDF draws a link, so without this the pointer says
        // "select text" over the one place a click will not select text.
        string body = Body(Page(), "private InputSystemCursorShape? HoverCursor(");

        Assert.Contains("ViewModel.ShowLinks", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.LinkAt(content.Page, nx, ny)", body, StringComparison.Ordinal);
        Assert.Contains("return InputSystemCursorShape.Hand;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_hover_cursor_asks_the_same_hit_test_the_click_does()
    {
        // Two copies of the geometry would be two chances to disagree, and the
        // disagreement would show up as a hand over something that does not
        // open, or no hand over something that does.
        string page = Page();

        Assert.Contains("ViewModel.LinkAt(content.Page, nx, ny)", Body(page, "private InputSystemCursorShape? HoverCursor("),
                        StringComparison.Ordinal);
        Assert.Contains("ViewModel.LinkAt(content.Page, nx, ny)", Body(page, "private void ViewportHost_PointerPressed("),
                        StringComparison.Ordinal);

        // And nothing anywhere compares against a link's edges by hand.
        Assert.DoesNotContain("link.Left <=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("clicked.Left", page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_hand_appears_exactly_where_a_click_follows_the_link()
    {
        // The cursor has to say what a click will actually do: a hand in View
        // mode, and in Edit mode only while Show Links is on, because there a
        // click on a link with the overlay off selects text as it always has.
        string body = Body(Page(), "private InputSystemCursorShape? HoverCursor(");

        int gate = body.IndexOf("(ViewModel.ShowLinks || !ViewModel.IsEditMode)", StringComparison.Ordinal);
        int hand = body.IndexOf("return InputSystemCursorShape.Hand;", gate, StringComparison.Ordinal);

        Assert.True(gate > 0 && hand > gate, "the hand is not behind the same gate as the click");
    }

    [Fact]
    public void in_view_mode_a_link_is_followed_whether_or_not_links_are_shown()
    {
        // ⚠️ WRITTEN AFTER A REAL FAILURE. A book whose first page is a table
        // of contents made of link buttons did nothing when they were clicked:
        // the log said "IGNORED, Show Links is off" four times. A reader does
        // not turn links on before following one.
        string body = Body(Page(), "private void ViewportHost_PointerPressed(");

        Assert.Contains("bool followsLinks = ViewModel.ShowLinks || !ViewModel.IsEditMode;", body, StringComparison.Ordinal);
        Assert.Contains("if (followsLinks\n                    && ViewModel.LinkAt(content.Page, nx, ny) is { } clicked)",
            body, StringComparison.Ordinal);
        Assert.Contains("if (!followsLinks\n                    && ViewModel.LinkAt(content.Page, nx, ny) is not null)",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_grip_still_wins_over_a_link_underneath_it()
    {
        // A grip belongs to something the reader has already selected and is
        // drawn over the page. Offering a hand there would advertise following
        // a link when the drag would resize.
        string body = Body(Page(), "private InputSystemCursorShape? HoverCursor(");

        int grip = body.IndexOf("var grip = ViewModel.GripUnder(", StringComparison.Ordinal);
        int link = body.IndexOf("ViewModel.LinkAt(", StringComparison.Ordinal);

        Assert.True(grip > 0 && link > grip, "the link check runs before the grip is known");
        Assert.Contains("grip == LoadedAnnotationPicker.Grip.None", body, StringComparison.Ordinal);
    }

    // ---------------- Click ----------------

    [Fact]
    public void a_link_is_tested_before_anything_else_the_select_tool_would_do()
    {
        // ⚠️ ORDER IS THE BEHAVIOUR. SelectAnnotationAt also runs the page-text
        // pick, so a link test placed after it loses every click on a link to
        // text selection. That is exactly what the real failure looked like.
        string body = Body(Page(), "private void ViewportHost_PointerPressed(");

        int link = body.IndexOf("ViewModel.LinkAt(content.Page, nx, ny) is { } clicked",
                                StringComparison.Ordinal);
        int annotation = body.IndexOf("ViewModel.SelectAnnotationAt(content.Page, nx, ny)",
                                      StringComparison.Ordinal);
        int text = body.IndexOf("ViewModel.BeginTextSelection(", StringComparison.Ordinal);

        Assert.True(link > 0, "the press path does not test for a link");
        Assert.True(link < annotation, "a link is tested after object selection");
        Assert.True(link < text, "a link is tested after text selection");
    }

    [Fact]
    public void a_click_on_a_link_ends_the_gesture_rather_than_falling_through()
    {
        // Marking it handled and breaking is what stops the same press also
        // starting a text selection, a marquee or a drag.
        string body = Body(Page(), "private void ViewportHost_PointerPressed(");

        int link = body.IndexOf("is { } clicked", StringComparison.Ordinal);
        int follow = body.IndexOf("FollowLinkAsync(clicked)", link, StringComparison.Ordinal);
        int handled = body.IndexOf("e.Handled = true;", follow, StringComparison.Ordinal);
        int brk = body.IndexOf("break;", handled, StringComparison.Ordinal);
        int annotation = body.IndexOf("ViewModel.SelectAnnotationAt(", StringComparison.Ordinal);

        Assert.True(follow > link, "a link press does not follow the link");
        Assert.True(handled > follow && handled < annotation, "a link press is not marked handled");
        Assert.True(brk > handled && brk < annotation, "a link press does not end the gesture");
    }

    [Fact]
    public void a_link_press_does_not_capture_the_pointer()
    {
        // Capturing would arm the drag machinery for a gesture that is over the
        // moment it starts, and leave the capture to be released by a branch
        // that never runs.
        string body = Body(Page(), "private void ViewportHost_PointerPressed(");

        int link = body.IndexOf("is { } clicked", StringComparison.Ordinal);
        int brk = body.IndexOf("break;", link, StringComparison.Ordinal);

        Assert.DoesNotContain("CapturePointer", body[link..brk], StringComparison.Ordinal);
    }

    [Fact]
    public void a_press_on_a_link_says_so_in_the_log_either_way()
    {
        // How the real failure was found, and how the next one will be. From
        // outside, "the link did nothing" and "Show Links is off" look the same.
        string body = Body(Page(), "private void ViewportHost_PointerPressed(");

        Assert.Contains("link press p", body, StringComparison.Ordinal);
        Assert.Contains("IGNORED, Show Links is off", body, StringComparison.Ordinal);
    }
}
