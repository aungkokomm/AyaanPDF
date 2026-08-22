using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the Drop Shadow row is actually connected to a shape.
///
/// DropShadowPanelTests proves the arithmetic both ways. It cannot prove the
/// controls call it, or that what they produce reaches the core, because
/// MainPage is a WinUI class the test assembly cannot load. So these read the
/// source, the technique AppWiringTests uses and for the same reason: what is
/// left in the view is otherwise only ever checked by running the app and
/// looking, which is how a control ships doing nothing.
/// </summary>
public class DropShadowRowWiringTests
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

    private static string ViewModel() =>
        FileFromRepo("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    /// <summary>The Drop Shadow row's markup, so a check cannot be satisfied by
    /// something elsewhere on the page that happens to match.</summary>
    private static string Row()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"EffectRows\"", StringComparison.Ordinal);
        Assert.True(at > 0, "EffectRows has been renamed; this test needs updating");

        int end = xaml.IndexOf("ShadowNoSelectionHint", at, StringComparison.Ordinal);
        Assert.True(end > at, "could not find the end of the row");

        return xaml[at..end];
    }

    // ---------------- the controls the design asked for ----------------

    [Theory]
    [InlineData("ShadowToggle", "the on/off switch")]
    [InlineData("ShadowAngleBox", "the precise angle")]
    [InlineData("ShadowDistanceSlider", "distance")]
    [InlineData("ShadowBlurSlider", "blur")]
    [InlineData("ShadowOpacitySlider", "opacity")]
    [InlineData("ShadowSwatch", "the colour swatch")]
    public void every_control_the_row_needs_is_there(string name, string what)
    {
        Assert.True(
            Row().Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal),
            what + " is missing from the Drop Shadow row");
    }

    [Fact]
    public void the_eight_compass_directions_are_all_offered()
    {
        string row = Row();

        var angles = Regex
            .Matches(row, @"Tag=""(\d+)""\s+Click=""ShadowDirection_Click""")
            .Select(m => int.Parse(m.Groups[1].Value))
            .OrderBy(a => a)
            .ToArray();

        Assert.Equal(new[] { 0, 45, 90, 135, 180, 225, 270, 315 }, angles);
    }

    [Fact]
    public void the_colour_presets_reuse_the_existing_picker_markup()
    {
        // Same "#AARRGGBB" tags the fill presets carry, so this is the pattern
        // that already exists rather than a new one.
        Assert.True(
            Regex.Matches(Row(), @"Tag=""#[0-9A-F]{8}""\s+Click=""ShadowColor_Click""").Count >= 4,
            "the colour row does not reuse the existing preset-button markup");
    }

    [Fact]
    public void spread_is_not_offered_anywhere_in_the_row()
    {
        // It is stored and round-tripped and drawn by nothing. A control that
        // moves a value while the picture stays still is worse than no control.
        Assert.DoesNotContain("Spread", Row(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- it reads the shape, not a tool state ----------------

    [Fact]
    public void the_row_is_filled_in_from_the_selected_shapes_own_shadow()
    {
        Assert.Contains("ViewModel.SelectedShapeShadow", Code(), StringComparison.Ordinal);
        Assert.Contains("DropShadowPanel.From(", Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_row_is_refilled_whenever_the_bar_is_laid_out()
    {
        // Otherwise it would show the last shape's shadow on the next one.
        Assert.Contains("if (sections.Effects) { SyncDropShadow(); }", Code(), StringComparison.Ordinal);
    }

    [Fact]
    public void filling_the_row_in_does_not_count_as_an_edit()
    {
        // Setting a slider raises ValueChanged. Without the guard, selecting a
        // shape would immediately write its own shadow back to it and push a
        // history entry for having looked at it.
        Assert.Contains("private bool _syncingShadow;", Code(), StringComparison.Ordinal);

        int at = Code().IndexOf("private void PushDropShadow()", StringComparison.Ordinal);
        Assert.True(at > 0, "PushDropShadow has been renamed; this test needs updating");

        int end = Code().IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.Contains("if (_syncingShadow)", Code()[at..end], StringComparison.Ordinal);
    }

    // ---------------- and writes through the override ----------------

    [Fact]
    public void changing_a_control_applies_the_row_to_the_shape()
    {
        string code = Code();

        Assert.Contains("DropShadowPanel.ToShadow(ShadowRowNow(), pageWpt)", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ApplyShadowToSelectedShape(", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Shadow_Toggled")]
    [InlineData("Shadow_ValueChanged")]
    [InlineData("ShadowAngle_ValueChanged")]
    [InlineData("ShadowDirection_Click")]
    [InlineData("ShadowColor_Click")]
    public void every_control_that_changes_the_shadow_pushes_it(string handler)
    {
        // A control wired to a handler that does not apply anything is exactly
        // the failure the source-reading tests exist to catch.
        int at = Code().IndexOf("private void " + handler, StringComparison.Ordinal);
        Assert.True(at > 0, handler + " does not exist");

        int end = Code().IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = end > at ? Code()[at..end] : Code()[at..];

        Assert.Contains("PushDropShadow()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_view_model_goes_through_the_shadow_override()
    {
        // Not a delete and re-add of its own: the override is what preserves
        // the rotation, colour, width, fill and corners.
        //
        // ONE OVERRIDE FOR EVERY EFFECT. It used to be a shadow-shaped entry
        // point, and a glow would have needed its own twin; the effects cross
        // as text now, so this is the only one there will be.
        Assert.Contains(
            "RenderCoreNative.restyle_shape_effects_annotation(",
            ViewModel(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_interop_declaration_exists_for_it()
    {
        Assert.Contains(
            "public static extern int restyle_shape_effects_annotation(",
            FileFromRepo("PdfEditorApp", "Interop", "RenderCoreNative.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void switching_the_row_off_clears_the_shadow_rather_than_hiding_it()
    {
        // A null shadow goes straight into With, which REMOVES the drop shadow
        // from the list rather than writing an invisible one. Hiding it instead
        // would leave a shadow reserving room in the annotation's rectangle for
        // ever.
        Assert.Contains(
            "(SelectedShapeEffects ?? new ShapeEffects()).With(shadow)",
            ViewModel(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_row_sends_the_whole_list_and_not_just_its_own_effect()
    {
        // THE ONE THAT MATTERS once there is more than one effect. The core
        // replaces the list wholesale, so an edit that builds a list containing
        // only its own effect deletes every other one: moving this row's slider
        // would take a glow off the shape, and either row would take off an
        // effect written by a later build.
        //
        // Starting from SelectedShapeEffects is what prevents that, and it is
        // asserted on the source because the write path lives in a WinUI class
        // no test assembly can load.
        string source = ViewModel();

        foreach (string row in new[] { "shadow", "glow" })
        {
            Assert.Contains(
                $"(SelectedShapeEffects ?? new ShapeEffects()).With({row})",
                source,
                StringComparison.Ordinal);
        }

        // And exactly ONE place actually writes them, so there is one place to
        // get this right rather than one per row.
        Assert.Equal(
            1,
            source.Split("RenderCoreNative.restyle_shape_effects_annotation(").Length - 1);
    }
}
