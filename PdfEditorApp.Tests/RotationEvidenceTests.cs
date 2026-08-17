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
/// These began as records of a DEFECT: commit 1 asserted "no rotation term
/// today". Commit 3 corrected the renderer and inverted them, so they now hold
/// the fix in place. If someone strips the projection back out, the harness
/// stops measuring what it claims to and these say so.
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

    /// <summary>
    /// One method's body, found by matching braces.
    ///
    /// A fixed-length window was tried first and is not good enough in either
    /// direction: too short and it misses the end of a long method, so a
    /// present string reads as absent; too long and it runs into the NEXT
    /// method, so a neighbour's line reads as this one's. Both happened.
    /// </summary>
    private static string BodyOf(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");

        int open = source.IndexOf('{', at);
        Assert.True(open > 0, $"no body for {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[at..(i + 1)];
            }
        }

        Assert.Fail($"unbalanced braces after {signature}");
        return string.Empty;
    }

    [Fact]
    public void the_ink_renderer_projects_through_the_pages_transform()
    {
        // The correction. BuildStrokePolyline now routes every point through
        // the same PageTransform the page card, the highlights and the
        // selection chrome all turn by.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polyline BuildStrokePolyline(");

        Assert.Contains("ViewTransformOf(stroke.PageIndex)", body, StringComparison.Ordinal);
        Assert.Contains("view.ToCard(x * scale, y * scale)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(x * scale, y * scale + pageTop)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_arrow_head_projects_through_it_too()
    {
        // The head is a separate filled polygon. Left behind, an arrow's tip
        // detaches from its own shaft the moment the page turns.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polygon BuildFilledHead(");

        Assert.Contains("ViewTransformOf(pageIndex)", body, StringComparison.Ordinal);
        Assert.Contains("view.ToCard(x * scale, y * scale)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(x * scale, (y * scale) + pageTop)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_stroke_thickness_is_scaled_by_the_page_transform()
    {
        // A quarter turn changes PageTransform.Scale, so a mark that ignores it
        // is not only in the wrong place, it is the wrong weight.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private Polyline BuildStrokePolyline(");

        Assert.Contains("stroke.StrokeWidth * scale * view.Scale", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_live_preview_uses_the_same_projection_as_the_committed_mark()
    {
        // The preview and the finished mark must come out of the same
        // arithmetic, or a shape jumps the instant the pointer lifts. It also
        // has to resolve the page ONCE: taking the thickness from one page's
        // transform and the points from another is a way to be subtly wrong
        // only while drawing on a neighbouring page.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private void OnInkStrokeChanged()");

        Assert.Contains("ViewTransformOf(previewPage)", body, StringComparison.Ordinal);
        Assert.Contains("previewView.ToCard(x * scale, y * scale)", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.InkWidth * ViewModel.OverlayScale * previewView.Scale",
                        body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(x * scale, y * scale + pageTop)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_text_box_preview_takes_its_bounds_after_the_turn_not_before()
    {
        // Spatial projection only; nothing else about text is touched. The
        // ordering is the whole point: minimum-then-project keeps the corner
        // that WAS top-left, which after a quarter turn is a different corner.
        string source = ReadSource("PdfEditorApp", "MainPage.xaml.cs");
        string body = BodyOf(source, "private (double Left, double Top, double Width, double Height) CardRect(");

        Assert.Contains("view.ToCard(nx1 * scale, ny1 * scale)", body, StringComparison.Ordinal);
        Assert.Contains("view.ToCard(nx2 * scale, ny2 * scale)", body, StringComparison.Ordinal);

        string update = BodyOf(source, "private void UpdateTextBoxPreview(");
        Assert.Contains("CardRect(_textDragPage", update, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Min(_textDragStartX, nx)", update, StringComparison.Ordinal);
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
