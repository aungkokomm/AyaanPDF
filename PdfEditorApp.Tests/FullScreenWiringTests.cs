using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// How full screen is reached and left.
///
/// The key rules are in PresentationKeysTests; this covers the view wiring that
/// this assembly cannot load. The thing most worth guarding is that Escape
/// still reaches the canvas when the window is not presenting.
/// </summary>
public class FullScreenWiringTests
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
    private static string WindowCode() => Read("PdfEditorApp", "MainWindow.xaml.cs");

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
    public void the_chord_goes_through_the_tested_resolver()
    {
        // Not a case in the switch. The Escape rule is subtle enough that it
        // belongs where a test can reach it.
        string handler = MethodBody(PageCode(), "private void RootGrid_KeyDown");

        Assert.Contains("PresentationKeys.Resolve(", handler, StringComparison.Ordinal);
        Assert.Contains("window.IsFullScreen", handler, StringComparison.Ordinal);
        Assert.Contains("IsTextInputFocused", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void escape_is_still_the_canvases_when_windowed()
    {
        // The regression this guards: the full-screen check runs BEFORE the
        // switch, so if it ever claimed Escape unconditionally, cancelling a
        // drag and clearing a selection would silently stop working.
        //
        // The resolver returns None while windowed, so the case below still
        // runs. This asserts the case is still there to run.
        string handler = MethodBody(PageCode(), "private void RootGrid_KeyDown");

        Assert.Contains("case VirtualKey.Escape:", handler, StringComparison.Ordinal);
        Assert.Contains("ClearAnnotationSelection()", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void the_window_switches_presenter_and_hides_its_own_chrome()
    {
        // The presenter takes the system caption and the taskbar. The title
        // strip and tab row are our own content and would otherwise stay.
        string body = MethodBody(WindowCode(), "public void SetFullScreen");

        Assert.Contains("AppWindowPresenterKind.FullScreen", body, StringComparison.Ordinal);
        Assert.Contains("AppWindowPresenterKind.Overlapped", body, StringComparison.Ordinal);
        Assert.Contains("AppTitleBar.Visibility", body, StringComparison.Ordinal);

        // The ROW as well as its content. Shell's first row is a fixed height,
        // so collapsing only the bar left a band of empty window above the page.
        Assert.Contains("Shell.RowDefinitions[0].Height", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_tab_strip_is_hidden_through_its_template_part()
    {
        // NOT by writing to Tabs.Resources. MainWindow.xaml already records why
        // that fails: a ThemeResource is resolved once when the template
        // expands and never looked up again, so assigning into that dictionary
        // at runtime does nothing. The first version of this feature did
        // exactly that and the tabs stayed on screen.
        string body = MethodBody(WindowCode(), "public void SetFullScreen");

        Assert.Contains("FindDescendant(Tabs, \"TabContainerGrid\")", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Tabs.Resources[", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_is_told_as_well_as_the_window()
    {
        // The tools and panels belong to the page; the window cannot reach
        // them.
        Assert.Contains(
            "ActivePage?.SetPresenting(",
            MethodBody(WindowCode(), "public void SetFullScreen"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void leaving_puts_back_what_was_there_rather_than_a_fixed_set()
    {
        // Restoring a default would turn rulers on for someone who had them off
        // and reopen a panel they had closed.
        string body = MethodBody(PageCode(), "public void SetPresenting");

        Assert.Contains("_chromeBeforePresenting", body, StringComparison.Ordinal);
        Assert.Contains("new ChromeState(", body, StringComparison.Ordinal);
        Assert.Contains("ChromeState.Hidden", body, StringComparison.Ordinal);
    }

    [Fact]
    public void presenting_hides_the_tools_not_only_the_panels()
    {
        // Reading is the point of the mode. A tool rail down the side of a
        // full-screen page is the thing you went full screen to get rid of.
        string body = MethodBody(PageCode(), "public void SetPresenting");

        Assert.Contains("ToolRail.Visibility", body, StringComparison.Ordinal);
        Assert.Contains("UpdatePropertyBarVisibility();", body, StringComparison.Ordinal);
        Assert.Contains("!IsPresenting", PageCode()[PageCode().IndexOf("private void UpdatePropertyBarVisibility()", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void the_floating_bar_is_not_taken_away_outright()
    {
        // It used to be collapsed for the duration, which took away the only
        // chrome full screen has: night mode, rotation, the page arrows and
        // the way back out were all keyboard-only in the one mode with no menu
        // to find them in. It is shown on arrival and then left to fade.
        string body = MethodBody(PageCode(), "public void SetPresenting");

        Assert.Contains("_barRevealed = presenting;", body, StringComparison.Ordinal);
        Assert.Contains("UpdateStatusBarVisibility()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusBar.Visibility = presenting", body, StringComparison.Ordinal);
    }

    [Fact]
    public void one_place_decides_whether_the_bar_is_on_screen()
    {
        // Two callers used to answer this separately, and they disagreed:
        // leaving full screen with no document open put the bar back over an
        // empty canvas.
        string code = PageCode();

        Assert.Contains("private void UpdateStatusBarVisibility() =>", code, StringComparison.Ordinal);

        // Exactly one assignment, and it is the one inside that method.
        int assignments = 0;
        for (int at = 0; (at = code.IndexOf("StatusBar.Visibility =", at, StringComparison.Ordinal)) >= 0; at++)
        {
            assignments++;
        }

        Assert.Equal(1, assignments);
    }

    [Fact]
    public void moving_the_pointer_brings_the_bar_back()
    {
        // The video-player pattern, because a bar that fades and cannot be
        // recalled is worse than one that was never there.
        string code = PageCode();

        Assert.Contains("private void RootGrid_PointerMoved", code, StringComparison.Ordinal);
        Assert.Contains("RevealStatusBar()", code, StringComparison.Ordinal);

        // handledEventsToo, for the same reason the wheel needed it: a drawing
        // tool marks pointer moves handled while it is tracking, and this must
        // still run.
        int at = code.IndexOf("UIElement.PointerMovedEvent", StringComparison.Ordinal);
        Assert.True(at >= 0, "the reveal handler is no longer registered");
        Assert.Contains("handledEventsToo: true", code[at..(at + 200)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_bar_does_not_fade_out_from_under_the_pointer_or_an_open_menu()
    {
        // Hiding it while a flyout of its own is open would leave the menu
        // floating over the page attached to nothing.
        string body = MethodBody(PageCode(), "private void RestartBarHideTimer");

        Assert.Contains("_pointerOverBar", body, StringComparison.Ordinal);
        Assert.Contains("AnyBarFlyoutOpen", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_fade_only_applies_to_full_screen()
    {
        // Windowed, the bar is permanent chrome. A reveal timer running there
        // would make it flicker in and out while the reader sat still.
        string reveal = MethodBody(PageCode(), "private void RevealStatusBar");
        Assert.Contains("if (!IsPresenting)", reveal, StringComparison.Ordinal);

        string visibility = MethodBody(PageCode(), "private void UpdateStatusBarVisibility");
        Assert.Contains("!IsPresenting || _barRevealed", visibility, StringComparison.Ordinal);
    }

    [Fact]
    public void the_way_out_is_said_out_loud()
    {
        // A full-screen app that hides its own chrome has to teach the way
        // back, or it reads as a hang.
        string body = MethodBody(PageCode(), "public void SetPresenting");

        Assert.Contains("Esc", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_canvas_takes_focus_so_the_exit_keys_arrive()
    {
        // F11 and Escape are handled on RootGrid. If focus were left on a
        // button in chrome that has just been collapsed, neither would reach
        // it and the only way out would be Alt+F4.
        Assert.Contains(
            "RootGrid.Focus(",
            MethodBody(PageCode(), "public void SetPresenting"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_advertises_the_chord_without_declaring_it()
    {
        // A MenuFlyoutItem's accelerator is dead until its flyout has been
        // opened once, which this menu documents at the top of the file.
        string xaml = Read("PdfEditorApp", "MainPage.xaml");
        int at = xaml.IndexOf("Text=\"Full screen\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "full screen is no longer offered in the menu");

        string item = xaml[at..Math.Min(xaml.Length, at + 220)];

        Assert.Contains("KeyboardAcceleratorTextOverride=\"F11\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("<KeyboardAccelerator ", item, StringComparison.Ordinal);
    }
}
