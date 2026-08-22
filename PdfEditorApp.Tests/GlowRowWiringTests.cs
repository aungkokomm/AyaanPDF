using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the Glow row is actually connected to a shape.
///
/// GlowPanelTests proves the arithmetic both ways. It cannot prove the controls
/// call it, or that what they produce reaches the core, because MainPage is a
/// WinUI class the test assembly cannot load. So these read the source, the
/// technique the Drop Shadow row's tests already use and for the same reason:
/// what is left in the view is otherwise only ever checked by running the app
/// and looking, which is how a control ships doing nothing.
/// </summary>
public class GlowRowWiringTests
{
    private static string FileFromRepo(params string[] relative)
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

    private static string Xaml() => FileFromRepo("PdfEditorApp", "MainPage.xaml");

    private static string Code() => FileFromRepo("PdfEditorApp", "MainPage.xaml.cs");

    /// <summary>
    /// The Glow row's markup alone, so a check cannot be satisfied by something
    /// elsewhere on the page that happens to match.
    /// </summary>
    private static string Row()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"GlowToggle\"", StringComparison.Ordinal);
        Assert.True(at > 0, "GlowToggle has been renamed; this test needs updating");

        int end = xaml.IndexOf("GlowNoSelectionHint", at, StringComparison.Ordinal);
        Assert.True(end > at, "could not find the end of the row");

        return xaml[at..end];
    }

    // ---------------- where it lives ----------------

    [Fact]
    public void the_row_is_inside_the_effects_flyout_with_the_shadow()
    {
        // The Effects section was built as a CONTAINER for exactly this: a
        // second effect is another block inside it and nothing outside moves.
        string xaml = Xaml();

        int rows = xaml.IndexOf("x:Name=\"EffectRows\"", StringComparison.Ordinal);
        int shadow = xaml.IndexOf("x:Name=\"ShadowToggle\"", StringComparison.Ordinal);
        int glow = xaml.IndexOf("x:Name=\"GlowToggle\"", StringComparison.Ordinal);

        Assert.True(rows > 0 && shadow > rows, "the shadow row has moved");
        Assert.True(glow > shadow, "the glow row is not in the flyout after the shadow");
    }

    // ---------------- the controls the row needs ----------------

    [Theory]
    [InlineData("GlowToggle", "the on/off switch")]
    [InlineData("GlowBlurSlider", "blur")]
    [InlineData("GlowOpacitySlider", "opacity")]
    [InlineData("GlowSwatch", "the colour swatch")]
    [InlineData("GlowBlurReadout", "the blur readout")]
    [InlineData("GlowOpacityReadout", "the opacity readout")]
    public void every_control_the_row_needs_is_there(string name, string what)
    {
        Assert.True(
            Row().Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal),
            what + " is missing from the Glow row");
    }

    [Fact]
    public void the_row_offers_no_direction_or_distance()
    {
        // A glow has nowhere to fall. Offering an angle would be a control that
        // changes nothing, which is worse than not offering it.
        string row = Row();

        Assert.DoesNotContain("GlowAngle", row, StringComparison.Ordinal);
        Assert.DoesNotContain("GlowDistance", row, StringComparison.Ordinal);
    }

    [Fact]
    public void the_blur_slider_cannot_be_dragged_to_nothing()
    {
        // A glow with no blur draws nothing, so a switch saying on over a page
        // showing nothing would be reachable by dragging.
        Assert.Contains("Minimum=\"1\"", Row(), StringComparison.Ordinal);
    }

    // ---------------- and they are wired ----------------

    [Theory]
    [InlineData("Glow_Toggled")]
    [InlineData("Glow_ValueChanged")]
    [InlineData("GlowColor_Click")]
    public void every_handler_the_markup_names_exists(string handler)
    {
        Assert.Contains(handler + "\"", Row(), StringComparison.Ordinal);
        Assert.Contains(
            "private void " + handler + "(", Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void every_handler_pushes_the_row_to_the_shape()
    {
        // A handler that updates the controls and forgets to apply them is the
        // exact shape of a control that appears to work and does nothing.
        string code = Code();

        foreach (string handler in new[] { "Glow_Toggled", "Glow_ValueChanged", "GlowColor_Click" })
        {
            int at = code.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
            Assert.True(at > 0, handler + " is missing");

            int end = code.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
            string body = end > at ? code[at..end] : code[at..];

            Assert.Contains("PushGlow()", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_row_goes_through_the_view_model_and_the_panel()
    {
        // Through GlowPanel, so the arithmetic is the tested arithmetic, and
        // through the view model's glow entry point, so the whole effect list
        // is what reaches the core.
        Assert.Contains(
            "ViewModel.ApplyGlowToSelectedShape(GlowPanel.ToGlow(GlowRowNow(), pageWpt))",
            Code(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_row_is_filled_in_when_the_property_bar_syncs()
    {
        // Otherwise it shows whatever the last shape had, which is the bug the
        // shadow row's own sync exists to prevent.
        // In the SAME statement as the shadow's, so both rows are filled in
        // together and neither can be forgotten on its own.
        string code = Code();
        int at = code.IndexOf("if (sections.Effects)", StringComparison.Ordinal);
        Assert.True(at > 0, "the effects sync has moved; this test needs updating");

        string statement = code.Substring(at, Math.Min(120, code.Length - at));

        Assert.Contains("SyncDropShadow();", statement, StringComparison.Ordinal);
        Assert.Contains("SyncGlow();", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void filling_the_row_in_does_not_count_as_an_edit()
    {
        // Setting a slider raises ValueChanged. Without the guard, showing a
        // shape's glow would immediately write it back, turning every selection
        // into an edit and every edit into a history entry.
        int at = Code().IndexOf("private void SyncGlow()", StringComparison.Ordinal);
        Assert.True(at > 0, "SyncGlow is missing");

        int end = Code().IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = Code()[at..end];

        Assert.Contains("_syncingShadow = true;", body, StringComparison.Ordinal);
        Assert.Contains("_syncingShadow = false;", body, StringComparison.Ordinal);
        Assert.Contains("if (_syncingShadow)", Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_sliders_ends_come_from_the_panel_and_not_from_the_markup()
    {
        // The markup's Maximum is a starting value for a page nobody has picked
        // yet. Once a shape is selected the ends have to be that page's, or a
        // blur set on a wide page cannot be reached on a narrow one.
        int at = Code().IndexOf("private void SyncGlow()", StringComparison.Ordinal);
        int end = Code().IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = Code()[at..end];

        Assert.Contains("GlowBlurSlider.Maximum", body, StringComparison.Ordinal);
        Assert.Contains("GlowPanel.MaxBlurPts(", body, StringComparison.Ordinal);
    }
}
