using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where back and forward are hooked in.
///
/// The history itself is covered by NavigationHistoryTests. This covers the
/// three things only the app can get wrong: recording at a point where the
/// "from" position still exists, not recording the moves it makes itself, and
/// returning to a place rather than to the top of a page.
/// </summary>
public class NavigationHistoryWiringTests
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
    public void the_position_before_a_jump_is_tracked_as_the_reader_moves()
    {
        // THE ordering trap. GoToPage sets the current page BEFORE asking for
        // the scroll, so by the time a jump is heard about, the page it came
        // from is already gone and CurrentReadingPosition would report the
        // destination. The "from" has to be tracked continuously instead.
        string body = MethodBody(PageCode(), "private void PageScroller_ViewChanged");

        Assert.Contains("_navHere = new NavigationPoint(", body, StringComparison.Ordinal);
        Assert.Contains("_navigation.NoteCurrent(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_jump_is_recorded_where_every_jump_passes_through()
    {
        // Bookmarks, the page box, thumbnails, search matches and Home/End all
        // reach the viewport through this one request.
        string body = MethodBody(PageCode(), "private void OnScrollToPageRequested");

        Assert.Contains("NavigationHistory.IsWorthRecording(_navHere.PageIndex, pageIndex)", body, StringComparison.Ordinal);
        Assert.Contains("_navigation.Record(_navHere", body, StringComparison.Ordinal);
    }

    [Fact]
    public void going_back_is_not_itself_recorded_as_a_jump()
    {
        // Otherwise Back pushes a new entry, Forward has nowhere to go, and
        // pressing Back twice returns to where it started.
        string request = MethodBody(PageCode(), "private void OnScrollToPageRequested");
        Assert.Contains("!_navigatingHistory", request, StringComparison.Ordinal);

        string apply = MethodBody(PageCode(), "private void ApplyHistoryPoint");
        Assert.Contains("_navigatingHistory = true", apply, StringComparison.Ordinal);
        Assert.Contains("_navigatingHistory = false", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void the_tracked_position_is_not_updated_while_history_is_moving()
    {
        // The scroll that Back performs would otherwise overwrite the very
        // entry it is travelling to.
        Assert.Contains("!_navigatingHistory", MethodBody(PageCode(), "private void PageScroller_ViewChanged"), StringComparison.Ordinal);
    }

    [Fact]
    public void back_returns_to_a_place_not_to_the_top_of_a_page()
    {
        // Landing at the top of page 1500 is not where the reader was, and
        // after a long book that is the difference between getting back and
        // starting to look again. So it scrolls by fraction rather than
        // leaving GoToPage to put the page top at the top.
        string body = MethodBody(PageCode(), "private void ApplyHistoryPoint");

        Assert.Contains("target.PageFraction", body, StringComparison.Ordinal);
        Assert.Contains("PageScroller.ScrollTo(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void history_starts_over_for_each_document()
    {
        // Going "back" into a file that is no longer open is nonsense, and the
        // page numbers would not even mean the same thing.
        string body = MethodBody(PageCode(), "private void OnLayoutRebuilt");

        Assert.Contains("_navigation.Reset(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void both_directions_are_dispatched()
    {
        string run = MethodBody(PageCode(), "private void Run(EditorCommand command)");

        Assert.Contains("case EditorCommand.NavigateBack:", run, StringComparison.Ordinal);
        Assert.Contains("case EditorCommand.NavigateForward:", run, StringComparison.Ordinal);
    }

    [Fact]
    public void the_key_handler_passes_alt_through()
    {
        // Without it the resolver can never see Alt, the arrows fall through to
        // the nudge cases below, and every test above still passes.
        string code = PageCode();

        Assert.Contains("IsTextInputFocused, IsAltDown())", code, StringComparison.Ordinal);
        Assert.Contains("private static bool IsAltDown()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void alt_is_read_live_rather_than_tracked()
    {
        // Ctrl is tracked in a field because it is held across other events.
        // Alt activates the menu bar, so the window can take focus mid-chord
        // and a tracked flag would be left stuck down.
        string body = MethodBody(PageCode(), "private static bool IsAltDown()");

        Assert.Contains("GetKeyStateForCurrentThread(VirtualKey.Menu)", body, StringComparison.Ordinal);
    }
}
