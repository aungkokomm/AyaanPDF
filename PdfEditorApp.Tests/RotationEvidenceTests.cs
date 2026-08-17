using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Holds the real ink renderer to the arithmetic the rotation evidence
/// reproduces.
///
/// RotationEvidenceCapture draws a mark the way BuildStrokePolyline draws one,
/// because a test assembly cannot load a WinUI page and call it. That makes the
/// capture's conclusion only as good as the claim that the two agree, so the
/// claim is asserted here against the actual file.
///
/// These are RECORDS OF THE CURRENT STATE, not statements of what is right.
/// Commit 1 documents a defect; the assertions below say "no rotation term
/// today" and are expected to be inverted by the commit that fixes it.
/// </summary>
public class RotationEvidenceTests
{
    private static string ReadSource(params string[] relative)
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

    /// <summary>The body of one method, from its signature to the next one.</summary>
    private static string BodyOf(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");

        // Far enough to cover the method and not the rest of the file. These
        // are all short builders.
        int length = Math.Min(1400, source.Length - at);
        return source.Substring(at, length);
    }

    [Fact]
    public void the_ink_renderer_projects_without_the_pages_transform_today()
    {
        // The defect, asserted rather than described. BuildStrokePolyline maps
        // a normalized point straight into slot space and never consults the
        // PageTransform that the page card, the highlights and the selection
        // chrome all turn by.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polyline BuildStrokePolyline(");

        Assert.Contains("new Point(x * scale, y * scale + pageTop)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToCard", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PageTransform", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewTransformOf", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_arrow_head_projects_without_it_either()
    {
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polygon BuildFilledHead(");

        Assert.Contains("new Point(x * scale, (y * scale) + pageTop)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToCard", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_stroke_thickness_is_not_scaled_by_the_page_transform_today()
    {
        // A quarter turn changes PageTransform.Scale, so a mark that ignores it
        // is not only in the wrong place, it is the wrong weight.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polyline BuildStrokePolyline(");

        Assert.Contains("StrokeThickness = stroke.StrokeWidth * scale", body, StringComparison.Ordinal);
        Assert.DoesNotContain("view.Scale", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_per_page_overlays_do_turn_with_the_page()
    {
        // The control the evidence compares against. These collections are
        // bound INSIDE the grid that carries the rotation transform, which is
        // why the same shape drawn as a highlight follows the page while the
        // same shape drawn as ink does not.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml");

        int rotated = page.IndexOf("<CompositeTransform ScaleX=\"{x:Bind ViewScale}\"", StringComparison.Ordinal);
        Assert.True(rotated > 0, "the page card's rotation transform has moved");

        foreach (string collection in new[]
        {
            "HighlightRects", "SearchMatchRects", "SelectionOutline", "SelectionGrips",
        })
        {
            int found = page.IndexOf($"ItemsSource=\"{{x:Bind {collection}", StringComparison.Ordinal);
            Assert.True(found > rotated, $"{collection} is no longer inside the rotated grid");
        }
    }

    [Fact]
    public void the_ink_layer_is_outside_every_page_card()
    {
        // And therefore outside the only thing that rotates. InkCanvas is a
        // sibling of the page stack, and nothing anywhere applies a transform
        // to it.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml");

        int inkCanvas = page.IndexOf("<Canvas x:Name=\"InkCanvas\"", StringComparison.Ordinal);
        int template = page.IndexOf("</DataTemplate>", StringComparison.Ordinal);

        Assert.True(inkCanvas > 0, "InkCanvas has moved");
        Assert.True(inkCanvas > template, "InkCanvas is now inside the page card template");

        Assert.DoesNotMatch(new Regex(@"InkCanvas\.RenderTransform"), page);
        Assert.DoesNotMatch(
            new Regex(@"InkCanvas\.RenderTransform"),
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"));
    }
}
