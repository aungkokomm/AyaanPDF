using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the Gradient row is actually connected to a shape.
///
/// GradientPanelTests proves the arithmetic both ways. It cannot prove the
/// controls call it, or that what they produce reaches the core, because
/// MainPage is a WinUI class the test assembly cannot load. So these read the
/// source, the technique the effect rows already use and for the same reason:
/// what is left in the view is otherwise only ever checked by running the app
/// and looking, which is how a control ships doing nothing.
/// </summary>
public class GradientRowWiringTests
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

    /// <summary>The Gradient row's markup alone, so a check cannot be satisfied
    /// by something elsewhere on the page that happens to match.</summary>
    private static string Row()
    {
        string xaml = Xaml();
        int at = xaml.IndexOf("x:Name=\"GradientSection\"", StringComparison.Ordinal);
        Assert.True(at > 0, "GradientSection has been renamed; this test needs updating");

        int end = xaml.IndexOf("GradientNoSelectionHint", at, StringComparison.Ordinal);
        Assert.True(end > at, "could not find the end of the row");

        return xaml[at..end];
    }

    // ---------------- where it lives ----------------

    [Fact]
    public void the_row_is_in_the_fill_flyout_and_not_in_effects()
    {
        // A gradient is what the inside of the shape is PAINTED with. A shadow
        // and a glow are marks made beside it. Putting the gradient in Effects
        // would be filing it under the one thing it is not.
        string xaml = Xaml();

        int fill = xaml.IndexOf("x:Name=\"FillFlyout\"", StringComparison.Ordinal);
        int gradient = xaml.IndexOf("x:Name=\"GradientSection\"", StringComparison.Ordinal);
        int effects = xaml.IndexOf("x:Name=\"EffectRows\"", StringComparison.Ordinal);

        Assert.True(fill > 0 && gradient > fill, "the gradient row is not inside the fill flyout");
        Assert.True(gradient < effects, "the gradient row ended up in the effects flyout");
    }

    // ---------------- the controls the row needs ----------------

    [Theory]
    [InlineData("GradientToggle", "the on/off switch")]
    [InlineData("GradientStartSwatch", "the start swatch")]
    [InlineData("GradientEndSwatch", "the end swatch")]
    [InlineData("GradientAngleSlider", "the direction slider")]
    [InlineData("GradientAngleReadout", "the direction readout")]
    [InlineData("GradientPreview", "the preview strip")]
    public void every_control_the_row_needs_is_there(string name, string what)
    {
        Assert.True(
            Row().Contains("x:Name=\"" + name + "\"", StringComparison.Ordinal),
            what + " is missing from the gradient row");
    }

    [Fact]
    public void the_row_offers_no_transparency()
    {
        // THE STAGE'S ONE CONSTRAINT, in the markup as well as in the model.
        // The save-time writer refuses a translucent stop rather than
        // flattening it, so offering one here would be offering something the
        // file cannot keep.
        string row = Row();

        Assert.DoesNotContain("IsAlphaEnabled=\"True\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("GradientOpacity", row, StringComparison.Ordinal);

        foreach (string tag in new[] { "Tag=\"#00", "Tag=\"#80", "Tag=\"#40" })
        {
            Assert.DoesNotContain(tag, row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_direction_slider_covers_the_whole_turn_one_degree_at_a_time()
    {
        // It used to step in 45s, which made every gradient in the app one of
        // eight. The endpoints were always free; the step was a limit the
        // controls invented.
        string row = Row();

        Assert.Contains("Minimum=\"0\"", row, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"359\"", row, StringComparison.Ordinal);
        Assert.Contains("StepFrequency=\"1\"", row, StringComparison.Ordinal);

        Assert.DoesNotContain("StepFrequency=\"45\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public void the_spread_slider_is_on_the_row_and_cannot_reach_zero()
    {
        // Zero is two endpoints in the same place, which is not a gradient.
        string row = Row();

        Assert.Contains("GradientSpreadSlider", row, StringComparison.Ordinal);
        Assert.Contains("GradientSpreadReadout", row, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"10\"", row, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"500\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public void the_swap_button_is_on_the_row()
    {
        string row = Row();

        Assert.Contains("GradientSwapButton", row, StringComparison.Ordinal);
        Assert.Contains("GradientSwap_Click", row, StringComparison.Ordinal);
    }

    // ---------------- and they are wired ----------------

    [Theory]
    [InlineData("Gradient_Toggled")]
    [InlineData("Gradient_ValueChanged")]
    [InlineData("GradientStart_Click")]
    [InlineData("GradientEnd_Click")]
    [InlineData("GradientSwap_Click")]
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

        foreach (string handler in new[]
        {
            "Gradient_Toggled", "Gradient_ValueChanged", "GradientStart_Click", "GradientEnd_Click",
        })
        {
            int at = code.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
            Assert.True(at > 0, handler + " is missing");

            int end = code.IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
            string body = end > at ? code[at..end] : code[at..];

            Assert.Contains("PushGradient()", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_row_goes_through_the_view_model_and_the_panel()
    {
        Assert.Contains(
            "ViewModel.ApplyGradientToSelectedShape(GradientPanel.ToGradient(GradientRowNow()))",
            Code(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_row_is_filled_in_when_the_fill_flyout_opens()
    {
        // Otherwise it shows whatever the last shape had, which is the bug the
        // effect rows' own syncs exist to prevent. The fill button is not part
        // of the property bar's section sync, so the flyout's own opening is
        // the signal.
        int at = Code().IndexOf(
            "private void FillFlyout_Opening(", StringComparison.Ordinal);
        Assert.True(at > 0, "FillFlyout_Opening is missing");

        int end = Code().IndexOf("\n    private ", at + 1, StringComparison.Ordinal);

        Assert.Contains("SyncGradient();", Code()[at..end], StringComparison.Ordinal);
    }

    [Fact]
    public void filling_the_row_in_does_not_count_as_an_edit()
    {
        // Setting a slider raises ValueChanged. Without the guard, opening the
        // flyout on a shape would immediately write its own gradient back to
        // it, turning every look into an edit and every edit into a history
        // entry.
        int at = Code().IndexOf("private void SyncGradient()", StringComparison.Ordinal);
        Assert.True(at > 0, "SyncGradient is missing");

        int end = Code().IndexOf("\n    private ", at + 1, StringComparison.Ordinal);
        string body = Code()[at..end];

        Assert.Contains("_syncingGradient = true;", body, StringComparison.Ordinal);
        Assert.Contains("_syncingGradient = false;", body, StringComparison.Ordinal);
        Assert.Contains("if (_syncingGradient", Code(), StringComparison.Ordinal);
    }

    // ---------------- and the write is a whole paint, not half of one ----------------

    [Fact]
    public void applying_a_gradient_sends_the_whole_tail_including_the_effects()
    {
        // The core replaces the tail wholesale, so a write that sent only the
        // gradient would take the shape's shadow and glow off with it.
        string body = MethodBody("public void ApplyGradientToSelectedShape(");

        Assert.Contains("SelectedShapeEffects", body, StringComparison.Ordinal);
        Assert.Contains("WriteShapePaint(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_gradient_and_a_solid_are_never_both_left_set()
    {
        // PDFium paints the positional fill as part of the appearance it
        // generates, so a leftover solid would be painted straight over the
        // shading the writer puts underneath it. Both fields are written
        // together, in one place.
        string body = MethodBody("private void WriteShapePaint(");

        Assert.Contains("restyle_shape_effects_annotation(", body, StringComparison.Ordinal);
        Assert.Contains("restyle_shape_fill_annotation(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void switching_a_fill_is_one_undo_and_not_two()
    {
        // Two writes, one history push. A person who changed a fill did one
        // thing and expects one press of undo to put it back.
        string body = MethodBody("private void WriteShapePaint(");

        int pushes = 0;
        for (int at = 0; (at = body.IndexOf("PushHistory(", at, StringComparison.Ordinal)) >= 0; at++)
        {
            pushes++;
        }

        Assert.Equal(1, pushes);
    }

    [Fact]
    public void picking_a_solid_colour_takes_a_gradient_off()
    {
        // The gradient is the more specific paint and wins wherever both are
        // recorded, so picking a colour on a gradient-filled shape would
        // otherwise appear to do nothing at all.
        string body = MethodBody("public void ApplyFillToSelectedShape(");

        Assert.Contains("SelectedShapeFill.Gradient is not null", body, StringComparison.Ordinal);
        Assert.Contains("ShapeFill.None", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string signature)
    {
        string code = ViewModel();
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " is missing");

        int end = code.IndexOf("\n    /// <summary>", at + 1, StringComparison.Ordinal);
        int alt = code.IndexOf("\n    public ", at + 1, StringComparison.Ordinal);
        if (alt > at && (end < at || alt < end))
        {
            end = alt;
        }

        return end > at ? code[at..end] : code[at..];
    }
}
