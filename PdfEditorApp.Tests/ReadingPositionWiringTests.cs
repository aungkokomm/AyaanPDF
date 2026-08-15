using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// How the remembered position reaches and leaves the viewport.
///
/// The arithmetic is covered by ReadingPositionsTests; this covers the parts
/// that only exist in view code, which this assembly cannot load. Same
/// technique and reason as AppWiringTests.
/// </summary>
public class ReadingPositionWiringTests
{
    private static string Read(params string[] relative)
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

    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    [Fact]
    public void the_position_is_restored_when_the_layout_is_built()
    {
        // This is where the bug was. The restore was first put in
        // ApplyDefaultView, whose name and comment both suggest it runs when a
        // document opens. It does not: its only caller is the Settings dialog's
        // default-view dropdown, so the restore fired when you changed a
        // setting and at no other time.
        //
        // OnLayoutRebuilt is the real moment: the pages now have sizes, so
        // there is somewhere to scroll to.
        Assert.Contains(
            "RestoreReadingPosition()",
            MethodBody(PageCode(), "private void OnLayoutRebuilt"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void editing_pages_does_not_yank_the_view_back()
    {
        // OnLayoutRebuilt also fires on page insert, delete and reorder.
        // Restoring on every rebuild would drag the view back to where reading
        // was left in the middle of editing, which is worse than not restoring.
        Assert.Contains(
            "_restoredForPath",
            MethodBody(PageCode(), "private void OnLayoutRebuilt"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_feature_can_be_switched_off()
    {
        // And switching it off stops positions being RECORDED, not merely
        // ignored on the way back in.
        Assert.Contains(
            "RememberReadingPosition",
            MethodBody(PageCode(), "private void SaveReadingPosition"),
            StringComparison.Ordinal);

        Assert.Contains(
            "RememberReadingPosition",
            MethodBody(PageCode(), "private bool RestoreReadingPosition"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_position_is_saved_when_the_document_closes()
    {
        // The debounce will not fire if the window is closing, and closing a
        // book is exactly when where you got to matters most.
        Assert.Contains(
            "SaveReadingPosition()",
            MethodBody(PageCode(), "public async Task<bool> ConfirmCloseAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void scrolling_queues_a_save_rather_than_writing_immediately()
    {
        // ViewChanged fires continuously through a pan. Writing the settings
        // file on each one would put a disk write inside the gesture.
        string body = MethodBody(PageCode(), "private void PageScroller_ViewChanged");

        Assert.Contains("QueueReadingPositionSave()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveReadingPosition()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_debounce_timer_does_not_repeat()
    {
        // A repeating timer would keep writing the settings file for as long as
        // the document stayed open.
        string body = MethodBody(PageCode(), "private void QueueReadingPositionSave");

        Assert.Contains("IsRepeating = false", body, StringComparison.Ordinal);
        Assert.Contains("Stop()", body, StringComparison.Ordinal);
        Assert.Contains("Start()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void an_unsaved_document_is_not_remembered()
    {
        // File > New has no path to key against, and inventing one would make
        // every blank document share a position.
        Assert.Contains(
            "HasDocumentPath",
            MethodBody(PageCode(), "private void SaveReadingPosition"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_position_is_captured_in_slot_space_not_screen_pixels()
    {
        // The ScrollView reports a zoomed offset. Storing that would restore
        // the wrong place at any other zoom, which is most of the time.
        string body = MethodBody(PageCode(), "private ReadingPosition? CurrentReadingPosition");

        Assert.Contains("/ PageScroller.ZoomFactor", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SlotTopOf(", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SlotHeightOf(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void restoring_refuses_a_page_the_document_no_longer_has()
    {
        // The file may have been edited elsewhere since it was last read, and
        // scrolling to a page past the end lands on a blank canvas.
        Assert.Contains(
            "position.PageIndex >= ViewModel.PageCount",
            MethodBody(PageCode(), "private bool RestoreReadingPosition"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void restoring_does_not_animate()
    {
        // A glide from page 1 to page 180 on open would be a long, pointless
        // journey past 179 pages the reader has already read.
        string body = MethodBody(PageCode(), "private bool RestoreReadingPosition");

        Assert.Contains("ScrollingAnimationMode.Disabled", body, StringComparison.Ordinal);
    }
}
