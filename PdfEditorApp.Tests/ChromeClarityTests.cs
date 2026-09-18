using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Items 5 to 8 of the 2026-09-18 UI review, and the fit-width scroll bar the
/// user found alongside them: the chosen tool reads as chosen, the opacity
/// sliders say what they change, rulers belong to Edit, the page layout icon
/// is not "layers", and a page fitted to the width has no sideways scroll.
/// </summary>
public class ChromeClarityTests
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
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Xaml() => Read("PdfEditorApp", "MainPage.xaml");
    private static string Code() => Read("PdfEditorApp", "MainPage.xaml.cs");

    /// <summary>From a member's signature to the end of its body.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return code[at..code.IndexOf("\n    }\n", at, StringComparison.Ordinal)];
    }

    /// <summary>An expression-bodied member, to its semicolon.</summary>
    private static string Statement(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"{signature} is gone");
        return code[at..code.IndexOf(";\n", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void the_chosen_tool_is_tinted_with_the_accent_in_light_and_dark()
    {
        // The stock look was a faint grey square, while the Reading and Edit
        // buttons are solid accent: the mode looked more "on" than the tool.
        string xaml = Xaml();
        int list = xaml.IndexOf("<ListView x:Name=\"ToolRailList\"", StringComparison.Ordinal);
        Assert.True(list > 0, "the tool rail list is gone");
        string rail = xaml[list..xaml.IndexOf("</ListView>", list, StringComparison.Ordinal)];

        foreach (string theme in new[] { "Light", "Dark" })
        {
            int at = rail.IndexOf($"<ResourceDictionary x:Key=\"{theme}\">", StringComparison.Ordinal);
            Assert.True(at > 0, $"no {theme} dictionary on the tool rail");
            string dictionary = rail[at..rail.IndexOf("</ResourceDictionary>", at, StringComparison.Ordinal)];
            Assert.Matches(
                @"<SolidColorBrush x:Key=""ListViewItemBackgroundSelected"" Color=""\{ThemeResource SystemAccentColor\w*\}""",
                dictionary);
        }
        Assert.Contains("<ResourceDictionary x:Key=\"HighContrast\" />", rail, StringComparison.Ordinal);
        Assert.DoesNotContain("ListViewItemSelectionIndicatorVisualEnabled", rail, StringComparison.Ordinal);
    }

    [Fact]
    public void the_opacity_sliders_say_opacity()
    {
        // "Stroke 100%" beside four line widths read as a thickness.
        string xaml = Xaml();
        int section = xaml.IndexOf("<StackPanel x:Name=\"OpacitySection\"", StringComparison.Ordinal);
        string opacity = xaml[section..xaml.IndexOf("<StackPanel x:Name=\"CornerRadiusSection\"", section, StringComparison.Ordinal)];

        Assert.Contains("<TextBlock Text=\"Opacity\"", opacity, StringComparison.Ordinal);
        Assert.Contains("<TextBlock Text=\"Fill opacity\"", opacity, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Opacity\"", opacity, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Stroke\"", opacity, StringComparison.Ordinal);
        Assert.DoesNotContain("Stroke opacity", opacity, StringComparison.Ordinal);
    }

    [Fact]
    public void rulers_show_only_in_edit_mode_and_the_toggles_say_so()
    {
        // Reading has nothing to place; the two strips go back to the page.
        string code = Code();
        Assert.Contains("RulersToggle.IsChecked && ViewModel.PageCount > 0 && ViewModel.IsEditMode",
            Statement(code, "private void ApplyRulerVisibility() =>"), StringComparison.Ordinal);

        // Opening a document drops back to View without passing SetMode, so
        // the change is heard from the view model.
        Assert.Matches(
            @"if \(args\.PropertyName == nameof\(ViewModel\.IsEditMode\)\)\s*\{\s*ApplyRulerVisibility\(\);",
            code);

        string xaml = Xaml();
        Assert.Contains("<ToggleMenuFlyoutItem x:Name=\"RulersToggle\" Text=\"Rulers in Edit mode\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToggleMenuFlyoutItem x:Name=\"BarRulersToggle\" Text=\"Rulers in Edit mode\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header = \"Rulers in Edit mode\",", code, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_layout_button_does_not_show_the_layers_glyph()
    {
        // E81E is Fluent's "map layers". Continuous scrolling is drawn instead:
        // the tail of one page above a whole one.
        string xaml = Xaml();
        string code = Code();
        Assert.DoesNotContain("&#xE81E;", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("uE81E", code, StringComparison.Ordinal);

        Assert.Contains("<x:String x:Key=\"ContinuousPagesPath\">F0 ", xaml, StringComparison.Ordinal);
        Assert.Contains("<PathIcon x:Name=\"ContinuousPagesIcon\" Data=\"{StaticResource ContinuousPagesPath}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ContinuousPagesIcon.Visibility = ViewModel.IsSinglePageView ? Visibility.Collapsed : Visibility.Visible;", code, StringComparison.Ordinal);
        Assert.Contains("PageModeBarIcon.Visibility = ViewModel.IsSinglePageView ? Visibility.Visible : Visibility.Collapsed;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void fit_width_divides_by_the_padding_that_zooms_with_the_pages()
    {
        // The canvas padding is inside the zoomed content. Subtracting it from
        // the viewport instead overshot by padding x (zoom - 1): 11 DIP at
        // 123%, and a horizontal scroll bar under a fitted page.
        string code = Code();
        string fit = Body(code, "private double FitZoom()");
        Assert.Contains("ViewModel.FitWidthZoom(PageScroller.ViewportWidth - 0.5,", fit, StringComparison.Ordinal);
        Assert.Contains("ViewportHost.Padding.Left + ViewportHost.Padding.Right", fit, StringComparison.Ordinal);

        // The fit and the check that notices the user zooming away from it
        // must ask the same question, or the fit switches itself off.
        Assert.Contains("FitZoom(),", Body(code, "private void FitToWidth("), StringComparison.Ordinal);
        Assert.Contains("Math.Abs(PageScroller.ZoomFactor - FitZoom()) > 0.005", code, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(code, @"ViewModel\.FitWidthZoom\("));
    }
}
