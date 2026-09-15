using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The floating chrome has to draw ON TOP of the pages it floats over.
///
/// A regression that shipped and survived a release: the object toolbar was
/// drawn BEHIND every page, visible only in the margins where it overhung the
/// paper onto the viewport background. The XAML looked right, because ZIndex
/// was declared correctly and simply does not apply here.
///
/// Each page card carries a ThemeShadow, which puts it on a raised ELEVATION in
/// the composition tree, and an elevated visual composites above an unelevated
/// one whatever their ZIndex says: ZIndex orders siblings within one panel's
/// render pass and does not reach across depth. The lever that works is
/// UIElement.Translation's Z component.
///
/// None of that is visible to a unit test, or to a green build, or to anything
/// short of looking at the screen with an object selected over a page. So what
/// is checkable is checked here: that the declarations still carry the Z that
/// makes them work, and that they still rank in the order the placement code
/// assumes. Anyone who removes one gets a red test instead of a bug report.
/// </summary>
public class FloatingChromeLayeringTests
{
    private static string MainPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "MainPage.xaml")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "PdfEditorApp", "MainPage.xaml"));
    }

    /// <summary>The Z of the named element's Translation, or null when it declares none.</summary>
    private static double? TranslationZOf(string xaml, string elementName)
    {
        var element = Regex.Match(
            xaml, @"<Border\s+x:Name=""" + Regex.Escape(elementName) + @"""[^>]*?>",
            RegexOptions.Singleline);
        Assert.True(element.Success, $"{elementName} was not found in MainPage.xaml");

        var translation = Regex.Match(
            element.Value, @"Translation=""\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*""");
        if (!translation.Success) { return null; }

        return double.Parse(translation.Groups[3].Value,
                            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("ObjectToolbar")]
    [InlineData("PropertyBar")]
    [InlineData("DefinitionPopup")]
    public void chrome_that_floats_over_a_page_is_lifted_above_it(string elementName)
    {
        // A page card is elevated by its shadow. Anything meant to float over
        // one has to be elevated further, or it is drawn behind the paper.
        double? z = TranslationZOf(MainPageXaml(), elementName);

        Assert.True(z is not null,
            $"{elementName} declares no Translation, so it will be drawn behind the page cards. "
            + "Canvas.ZIndex does not help here: the cards are raised by their ThemeShadow.");
        Assert.True(z > 0, $"{elementName} declares Translation Z {z}, which lifts it above nothing.");
    }

    [Fact]
    public void the_property_bar_stays_above_the_object_toolbar()
    {
        // ObjectToolbarPlacement reserves the property bar's height because the
        // property bar draws on top of the object toolbar. If that ever
        // inverted, the reservation would be protecting the wrong control and
        // the toolbar would be pushed away from a bar it now covers.
        string xaml = MainPageXaml();

        double? toolbar = TranslationZOf(xaml, "ObjectToolbar");
        double? propertyBar = TranslationZOf(xaml, "PropertyBar");

        Assert.NotNull(toolbar);
        Assert.NotNull(propertyBar);
        Assert.True(propertyBar > toolbar,
            $"the property bar (Z {propertyBar}) must sit above the object toolbar (Z {toolbar})");
    }

    [Fact]
    public void the_object_toolbar_is_not_moved_by_a_render_transform()
    {
        // The trap that made the first fix look like a no-op. RenderTransform
        // and Translation drive the SAME composition visual, and the transform
        // wins, taking the elevation with it. The toolbar is positioned through
        // Translation for that reason, so a RenderTransform reappearing here
        // would silently drop it back behind the pages.
        string xaml = MainPageXaml();
        var element = Regex.Match(
            xaml,
            @"<Border\s+x:Name=""ObjectToolbar"".*?</Border>",
            RegexOptions.Singleline);
        Assert.True(element.Success, "ObjectToolbar was not found in MainPage.xaml");

        Assert.DoesNotContain("RenderTransform", element.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void chrome_that_floats_over_paper_owns_its_pixels_in_every_theme()
    {
        // Being ON TOP of the page is only half of being visible on it. The
        // object toolbar used to paint itself with LayerFillColorAltBrush,
        // which is opaque white in Light and about five per cent white in Dark:
        // over a white page that is nothing, and the pale button text went with
        // it. The toolbar was flawless in the light theme and invisible in
        // every other one.
        //
        // The property bar and the tool rail already float over the document on
        // acrylic. Asserting they MATCH keeps the three from drifting apart
        // again, and keeps the next person from reaching for a layer fill
        // because it looked right on the theme they happened to be using.
        string xaml = MainPageXaml();

        Assert.Equal(BackgroundOf(xaml, "PropertyBar"), BackgroundOf(xaml, "ObjectToolbar"));
        Assert.Equal(BackgroundOf(xaml, "ToolRail"), BackgroundOf(xaml, "ObjectToolbar"));
        Assert.DoesNotContain("LayerFill", BackgroundOf(xaml, "ObjectToolbar"), StringComparison.Ordinal);
    }

    /// <summary>The brush named in the element's Background attribute.</summary>
    private static string BackgroundOf(string xaml, string elementName)
    {
        var element = Regex.Match(
            xaml, @"<Border\s+x:Name=""" + Regex.Escape(elementName) + @"""[^>]*?>",
            RegexOptions.Singleline);
        Assert.True(element.Success, $"{elementName} was not found in MainPage.xaml");

        var background = Regex.Match(element.Value, @"Background=""\{ThemeResource\s+([^}]+)\}""");
        Assert.True(background.Success, $"{elementName} declares no ThemeResource Background");
        return background.Groups[1].Value.Trim();
    }

    [Fact]
    public void the_toolbars_elevation_is_a_real_lift()
    {
        // The value the positioning code applies, kept beside the geometry it
        // belongs with rather than as a number typed into the view.
        Assert.True(PdfEditorApp.Viewport.ObjectToolbarPlacement.Elevation > 0);
    }

    [Fact]
    public void the_page_card_still_carries_the_shadow_this_all_compensates_for()
    {
        // If the card's ThemeShadow is ever removed, the elevations above stop
        // being necessary and this whole arrangement wants revisiting rather
        // than being carried forward as folklore.
        Assert.Contains("<ThemeShadow />", MainPageXaml(), StringComparison.Ordinal);
    }
}
