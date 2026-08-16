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
    public void the_shadowed_z_order_case_is_gone()
    {
        // Ctrl+Shift+] was handled in the key switch AND now resolves through
        // KeyboardCommands, which runs first. Two declarations of one chord is
        // the shape that let Ctrl+Z sit dead for months.
        Assert.DoesNotContain("case (VirtualKey)0xDD when _isCtrlDown", Code(), StringComparison.Ordinal);
    }
}
