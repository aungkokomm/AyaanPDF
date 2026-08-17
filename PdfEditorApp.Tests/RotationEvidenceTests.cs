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

    /// <summary>
    /// The reference renderer's geometry, which now lives in OverlayShapeBuilder
    /// so the parity harness can measure the SAME construction instead of its
    /// own copy of it. MainPage keeps the wrappers that resolve a page's numbers
    /// from the view model.
    ///
    /// The wrappers are expression-bodied and have no braces, so BodyOf cannot
    /// be pointed at them: it would find the next brace in the file and read a
    /// different method entirely. They are checked against the whole source.
    /// </summary>
    private static string BuilderSource() =>
        ReadSource("PdfEditorApp", "Rendering", "OverlayShapeBuilder.cs");

    [Fact]
    public void the_ink_renderer_projects_through_the_pages_transform()
    {
        // The correction. Every point goes through the same PageTransform the
        // page card, the highlights and the selection chrome all turn by.
        //
        // BOTH halves are asserted, because either can be right on its own
        // while the mark still lands in the wrong place: MainPage has to hand
        // over the page's transform, and the builder has to use it.
        Assert.Contains("ViewTransformOf(stroke.PageIndex)",
                        ReadSource("PdfEditorApp", "MainPage.xaml.cs"), StringComparison.Ordinal);

        string body = BodyOf(BuilderSource(), "public static Polyline Stroke(");

        Assert.Contains("view.ToCard(x * scale, y * scale)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(x * scale, y * scale + pageTop)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_arrow_head_projects_through_it_too()
    {
        // The head is a separate filled polygon. Left behind, an arrow's tip
        // detaches from its own shaft the moment the page turns.
        Assert.Contains("ViewTransformOf(pageIndex)",
                        ReadSource("PdfEditorApp", "MainPage.xaml.cs"), StringComparison.Ordinal);

        string body = BodyOf(BuilderSource(), "public static Polygon FilledHead(");

        Assert.Contains("view.ToCard(x * scale, y * scale)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(x * scale, (y * scale) + pageTop)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_stroke_thickness_is_scaled_by_the_page_transform()
    {
        // A quarter turn changes PageTransform.Scale, so a mark that ignores it
        // is not only in the wrong place, it is the wrong weight.
        string body = BodyOf(BuilderSource(), "public static Polyline Stroke(");

        Assert.Contains("stroke.StrokeWidth * scale * view.Scale", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_wrappers_still_hand_the_builder_the_pages_own_numbers()
    {
        // The half that cannot live in the builder: which page's scale, top and
        // transform a mark is drawn with. Passing another page's would put a
        // stroke on the wrong page and at the wrong weight, and the builder
        // could not tell.
        string page = ReadSource("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains("OverlayShapeBuilder.Stroke(", page, StringComparison.Ordinal);
        Assert.Contains("OverlayShapeBuilder.FilledHead(", page, StringComparison.Ordinal);

        // NAMED, and pinned that way. scale and pageTop are both doubles, so
        // swapping them compiles cleanly, puts every mark in the wrong place at
        // the wrong weight, and cannot be caught by this assembly, which is
        // unable to call a WinUI builder at all.
        Assert.Contains("pageTop: ViewModel.SlotTopOf(stroke.PageIndex)", page, StringComparison.Ordinal);
        Assert.Contains("pageTop: ViewModel.SlotTopOf(pageIndex)", page, StringComparison.Ordinal);
        Assert.Contains("scale: ViewModel.OverlayScale", page, StringComparison.Ordinal);
    }

    [Fact]
    public void the_live_preview_uses_the_same_projection_as_the_committed_mark()
    {
        // The preview and the finished mark must come out of the same
        // arithmetic, or a shape jumps the instant the pointer lifts. It also
        // has to resolve the page ONCE: taking the thickness from one page's
        // transform and the points from another is a way to be subtly wrong
        // only while drawing on a neighbouring page.
        // The preview builder was split out of OnInkStrokeChanged when the Skia
        // layer became a second consumer of the same signal, exactly as
        // RebuildInkCanvas already was. The arithmetic did not move; only its
        // name did, so this reads the new one.
        string body = BodyOf(
            ReadSource("PdfEditorApp", "MainPage.xaml.cs"),
            "private void UpdateInkPreview()");

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
