using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// How the line being edited in place is drawn over the page.
///
/// Read out of the source, for the reason every wiring test here gives: the
/// view lives in the WinUI project, which a net10.0 test assembly cannot load.
/// The pixels behind these rules were measured on captures of the app at 145%.
/// </summary>
public class InPlaceOverlayWiringTests
{
    private static string Source(params string[] relative)
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

    private static string Page() => Source("PdfEditorApp", "MainPage.xaml.cs");

    private static string Xaml() => Source("PdfEditorApp", "MainPage.xaml");

    /// <summary>One method's body, comment lines dropped so that commenting a
    /// call out counts as removing it.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"there is no {signature}");

        int next = code.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = code[at..(next > at ? next : code.Length)];

        return string.Join('\n', Array.FindAll(
            body.Split('\n'), l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void the_line_being_edited_is_drawn_for_the_zoom_it_is_seen_at()
    {
        // The reader's typing at 145% came out soft and grey: the layer was
        // drawn at 100% and the ScrollView stretched it.
        string page = Page();
        string sharpen = Body(page, "private void DrawInPlaceLayerAtTheZoom()");
        Assert.Contains("InPlaceLayer.RasterizationScale = wanted;", sharpen, StringComparison.Ordinal);
        Assert.Contains("PageScroller.ZoomFactor", sharpen, StringComparison.Ordinal);

        Assert.Contains("DrawInPlaceLayerAtTheZoom();",
            Body(page, "private void RenderInPlaceEdit()"), StringComparison.Ordinal);
        Assert.Contains("DrawInPlaceLayerAtTheZoom();",
            Body(page, "private void PageScroller_ViewChanged("), StringComparison.Ordinal);
    }

    [Fact]
    public void the_overlay_starts_where_the_page_does_inside_the_card_border()
    {
        // One DIP up and left of the page put the typed text above the page's
        // baseline and left the bottoms of the old descenders under the cover.
        string xaml = Xaml();

        var card = Regex.Match(xaml,
            @"<Border Width=""\{x:Bind SlotWidth\}""[^>]*?BorderThickness=""(?<b>[\d.]+)""",
            RegexOptions.Singleline);
        Assert.True(card.Success, "the page card's border is gone; this test needs rewriting to match");

        var layer = Regex.Match(xaml,
            @"<Canvas x:Name=""InPlaceLayer""[^>]*?Margin=""(?<x>[\d.]+),(?<y>[\d.]+),0,0""");
        Assert.True(layer.Success, "the edit overlay has no margin for the card's border");

        Assert.Equal(card.Groups["b"].Value, layer.Groups["x"].Value);
        Assert.Equal(card.Groups["b"].Value, layer.Groups["y"].Value);
    }

    [Fact]
    public void the_cover_reaches_past_the_line_s_own_box()
    {
        // A cover exactly the box's height left a hairline of each old
        // ascender and descender showing, because the page smooths every edge
        // a fraction of a pixel past the glyph's outline.
        string tail = Body(Page(), "private void DrawInPlaceTail(");
        Assert.Contains("Height = height + (2 * bleed),", tail, StringComparison.Ordinal);
        Assert.Contains("Canvas.SetTop(cover, top - bleed);", tail, StringComparison.Ordinal);
    }
}
