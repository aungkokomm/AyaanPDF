using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The view controls on the floating bar.
///
/// They exist because every one of them used to be three interactions deep:
/// the rail's menu button, then View, then the item. And in full screen the
/// title row is collapsed entirely, so this bar is the nearest chrome there is.
/// </summary>
public class StatusBarViewControlsTests
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

    private static string Xaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string Code() => Read("PdfEditorApp", "MainPage.xaml.cs");

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

    [Theory]
    [InlineData("PrevPage_Click")]
    [InlineData("NextPage_Click")]
    [InlineData("NavBack_Click")]
    [InlineData("NavForward_Click")]
    [InlineData("PageModeBar_Click")]
    [InlineData("NightModeBar_Click")]
    [InlineData("RotateBar_Click")]
    [InlineData("FullScreen_Click")]
    [InlineData("FindToggle_Click")]
    public void every_new_button_is_wired_to_something(string handler)
    {
        // A Click= naming a handler that does not exist is a XAML-time failure,
        // but a button nobody wired at all just sits there.
        Assert.Contains($"Click=\"{handler}\"", Xaml(), StringComparison.Ordinal);
        Assert.Contains(handler, Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_whole_view_menu_is_reachable_from_the_bar()
    {
        // The point of the exercise. Every item the View menu offers is either
        // a button on the bar or one click into its flyouts.
        string xaml = Xaml();

        foreach (string reachable in new[]
        {
            "ZoomFitPage_Click",          // fit page, in the zoom dropdown
            "ZoomFitWidth_Click",         // fit width
            "ZoomActualSize_Click",       // actual size
            "FullScreen_Click",           // full screen
            "NightModeBar_Click",         // night mode
            "RotateBar_Click",            // rotate clockwise
            "RotateViewCcw_Click",        // rotate anticlockwise
            "ResetViewRotation_Click",    // reset rotation
            "ContinuousView_Click",       // continuous
            "SinglePageView_Click",       // single page
            "RulersToggle_Click",         // rulers
            "RulerUnit_Click",            // ruler units
        })
        {
            Assert.Contains($"Click=\"{reachable}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_bar_shows_which_modes_are_on()
    {
        // The reason these are ToggleButtons rather than buttons. Night mode
        // being on is a thing to SEE, not something to open a menu to check,
        // and a turned view is otherwise only detectable by the page looking
        // odd, since rotation is session-only and resets on reopen.
        string xaml = Xaml();

        Assert.Contains("<ToggleButton x:Name=\"NightModeBarButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToggleButton x:Name=\"RotateBarButton\"", xaml, StringComparison.Ordinal);

        string sync = MethodBody(Code(), "private void SyncBarViewState");
        Assert.Contains("NightModeBarButton.IsChecked = ViewModel.IsNightMode", sync, StringComparison.Ordinal);
        Assert.Contains("RotateBarButton.IsChecked = ViewModel.IsViewRotated", sync, StringComparison.Ordinal);
    }

    [Fact]
    public void showing_the_state_does_not_change_it()
    {
        // ⚠️ Setting IsChecked raises Click, and these handlers act. Without
        // the guard, syncing the bar to the current state would toggle it.
        // That exact trap already cost this app one bug, where restoring the
        // saved view mode wrote the other mode back over it.
        string sync = MethodBody(Code(), "private void SyncBarViewState");

        Assert.Contains("_applyingSettings = true", sync, StringComparison.Ordinal);
        Assert.Contains("_applyingSettings = false", sync, StringComparison.Ordinal);

        // And each handler the sync can trip has to honour it.
        foreach (string handler in new[]
        {
            "private void NightModeToggle_Click",
            "private void RulersToggle_Click",
            "private void ApplyPageViewMode",
        })
        {
            Assert.Contains("_applyingSettings", MethodBody(Code(), handler), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void find_is_collapsed_until_it_is_wanted()
    {
        // It held about 120 DIP of a bar that floats over the page, for
        // something done in bursts. That width is what the view controls are
        // made of.
        string xaml = Xaml();

        Assert.Contains("<StackPanel x:Name=\"FindPanel\"", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""FindPanel""[^>]*Visibility=""Collapsed""", xaml.Replace("\r\n", " ").Replace("\n", " "));
    }

    [Fact]
    public void ctrl_f_opens_find_before_reaching_for_the_box()
    {
        // Focusing a box inside a collapsed panel puts the caret somewhere
        // invisible and the keystrokes go nowhere.
        string code = Code();
        int at = code.IndexOf("case VirtualKey.F when _isCtrlDown:", StringComparison.Ordinal);
        Assert.True(at >= 0, "Ctrl+F is gone; this test needs rewriting");

        Assert.Contains("SetFindOpen(true)", code[at..(at + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void closing_find_keeps_the_query()
    {
        // So reopening resumes the same search rather than starting from
        // nothing, which is what makes collapsing it acceptable at all.
        string body = MethodBody(Code(), "private void SetFindOpen");

        Assert.DoesNotContain("SearchQuery", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchBox.Text = ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_zoom_readout_looks_like_something_you_can_click()
    {
        // A bare percentage reads as a label, and nobody clicks a label.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"ZoomMenuButton\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        Assert.Contains("<FontIcon", xaml[at..(at + 900)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_arrows_go_out_at_the_ends_of_the_document()
    {
        // They were always live: on page 1 the back arrow looked exactly as it
        // does on page 2 and pressing it did nothing. Bound rather than set in
        // a handler, so there is no path that can forget to update it.
        string xaml = Xaml();

        Assert.Contains(
            "IsEnabled=\"{x:Bind ViewModel.CanGoToPreviousPage, Mode=OneWay}\"",
            xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind ViewModel.CanGoToNextPage, Mode=OneWay}\"",
            xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_view_model_tells_the_truth_about_the_ends()
    {
        // This assembly cannot load the view model, so the properties are read
        // here instead. Both must be false with nothing open, which is the
        // state the bar starts in.
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

        Assert.Contains(
            "public bool CanGoToPreviousPage => PageCount > 0 && CurrentPageIndex > 0;",
            vm, StringComparison.Ordinal);
        Assert.Contains(
            "public bool CanGoToNextPage => PageCount > 0 && CurrentPageIndex < PageCount - 1;",
            vm, StringComparison.Ordinal);

        // Computed properties do not raise anything by themselves. Both the
        // page and the count have to say so, or the arrows would go grey once
        // and stay that way.
        foreach (string hook in new[]
        {
            "partial void OnCurrentPageIndexChanged",
            "partial void OnPageCountChanged",
        })
        {
            string body = MethodBody(vm, hook);
            Assert.Contains("nameof(CanGoToPreviousPage)", body, StringComparison.Ordinal);
            Assert.Contains("nameof(CanGoToNextPage)", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void back_has_a_forward_to_match()
    {
        // Back on its own was a one-way door: having used it, the only way to
        // undo it was Alt+Right, which nothing on screen mentioned.
        string xaml = Xaml();

        Assert.Contains("x:Name=\"NavForwardButton\"", xaml, StringComparison.Ordinal);

        // Both start disabled. Nothing is open at launch, so there is nowhere
        // to go, and the sync only runs once a document has loaded.
        foreach (string name in new[] { "NavBackButton", "NavForwardButton" })
        {
            int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.Contains("IsEnabled=\"False\"", xaml[at..(at + 120)], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_history_buttons_are_refreshed_wherever_the_history_moves()
    {
        // Three places change what back and forward can do: opening a
        // document, recording a jump, and following one. Miss any of them and
        // the buttons report a history that has moved on without them.
        string code = Code();

        foreach (string caller in new[]
        {
            "private void ApplyHistoryPoint",
            "private void OnScrollToPageRequested",
        })
        {
            Assert.Contains("SyncBarNavState()", MethodBody(code, caller), StringComparison.Ordinal);
        }

        // And on load, where the history is reset to the opened document.
        int reset = code.IndexOf("_navigation.Reset(_navHere);", StringComparison.Ordinal);
        Assert.True(reset >= 0, "the history is no longer reset on open");
        Assert.Contains("SyncBarNavState()", code[reset..(reset + 200)], StringComparison.Ordinal);
    }

    [Fact]
    public void the_bar_says_which_way_the_document_scrolls()
    {
        // It was reachable only inside the "..." menu, so the one thing a
        // reader would want to see at a glance, whether the wheel scrolls on or
        // turns a page, was the one thing the bar did not show.
        string xaml = Xaml();
        Assert.Contains("x:Name=\"PageModeBarButton\"", xaml, StringComparison.Ordinal);

        // Both glyphs are checked against the font that actually renders them:
        // E7C3 is a single sheet and E81E is a stack of them. A code that is
        // not in the font renders as an empty box, silently.
        string sync = MethodBody(Code(), "private void SyncBarViewState");
        Assert.Contains("\\uE7C3", sync, StringComparison.Ordinal);
        Assert.Contains("\\uE81E", sync, StringComparison.Ordinal);

        // An icon that reports the current mode cannot also advertise what
        // pressing it does, so the tooltip has to, and it has to change with
        // the mode rather than being fixed in the XAML.
        Assert.Contains("ToolTipService.SetToolTip(", sync, StringComparison.Ordinal);
        Assert.Contains("PageModeBarButton", sync, StringComparison.Ordinal);
    }

    [Fact]
    public void every_icon_on_the_bar_is_named_somewhere()
    {
        // An icon can only be guessed at. The "..." menu is where a reader
        // finds out that the moon is night mode, and it is where these
        // commands still live when the canvas is too narrow for their buttons.
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"ViewOptionsButton\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string flyout = xaml[at..];
        int end = flyout.IndexOf("</Button.Flyout>", StringComparison.Ordinal);
        Assert.True(end > 0);
        flyout = flyout[..end];

        foreach (string named in new[]
        {
            "Text=\"Back\"",
            "Text=\"Forward\"",
            "Text=\"Night mode\"",
            "Text=\"Full screen\"",
            "Text=\"Rotate clockwise\"",
            "Text=\"Single page\"",
            "Text=\"Continuous scrolling\"",
        })
        {
            Assert.Contains(named, flyout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void night_mode_agrees_with_itself_across_three_controls()
    {
        // The bar button, the View menu and the bar's own flyout all set it.
        // The rulers toggle already had to solve this; night mode gained a
        // third control and would otherwise have read the wrong one's state.
        string body = MethodBody(Code(), "private void NightModeToggle_Click");

        Assert.Contains("BarNightModeItem", body, StringComparison.Ordinal);
        Assert.Contains("_applyingSettings", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_fit_button_does_not_claim_to_be_only_one_of_the_two()
    {
        // Its handler toggles between fitting the page and filling the width,
        // so a tooltip naming just one of them made half its presses look like
        // a fault.
        string xaml = Xaml();
        int at = xaml.IndexOf("Click=\"ResetZoom_Click\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string button = xaml[at..(at + 320)];
        Assert.Contains("Fit page", button, StringComparison.Ordinal);
        Assert.Contains("fit width", button, StringComparison.Ordinal);
    }

    [Fact]
    public void the_droppable_groups_are_grouped()
    {
        // The overflow works by collapsing whole groups. Loose buttons cannot
        // be hidden as a unit, and a separator left behind by a hidden group
        // is a divider between one thing and nothing.
        string xaml = Xaml();

        Assert.Contains("<StackPanel x:Name=\"NavHistoryGroup\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<StackPanel x:Name=\"ViewModesGroup\"", xaml, StringComparison.Ordinal);

        int at = xaml.IndexOf("<StackPanel x:Name=\"ViewModesGroup\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</StackPanel>", at, StringComparison.Ordinal);
        string group = xaml[at..end];
        Assert.Contains("<Rectangle", group, StringComparison.Ordinal);
    }

    [Fact]
    public void the_overflow_measures_groups_only_while_they_are_showing()
    {
        // A hidden group is zero wide. Deciding from that would find that the
        // bar now fits, put the group back, find that it does not fit, and
        // take it away again, at the frame rate.
        string body = MethodBody(Code(), "private void ApplyBarOverflow");

        Assert.Contains("navShown && NavHistoryGroup.ActualWidth > 0", body, StringComparison.Ordinal);
        Assert.Contains("viewShown && ViewModesGroup.ActualWidth > 0", body, StringComparison.Ordinal);
        Assert.Contains("StatusBarOverflow.Decide(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_shadowed_z_order_case_is_gone()
    {
        // Ctrl+Shift+] was handled in the key switch AND now resolves through
        // KeyboardCommands, which runs first. Two declarations of one chord is
        // the shape that let Ctrl+Z sit dead for months.
        Assert.DoesNotContain("case (VirtualKey)0xDD when _isCtrlDown", Code(), StringComparison.Ordinal);
    }
}
