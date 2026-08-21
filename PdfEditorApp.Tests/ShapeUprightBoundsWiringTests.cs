using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the rebuild path asks the core for a shape's real extent, and that
/// every caller can answer the one question the core needs to tell it.
///
/// An annotation's /Rect is not its geometry: it carries the stroke pad, and
/// for a turned shape it is the axis-aligned box CONTAINING the rotation. A
/// shape rebuilt straight from that rectangle comes back larger every time,
/// compounding, and at 90 degrees comes back lying the wrong way round. The
/// arithmetic that undoes it lives in render_core, where the resize path
/// already needed it, and is proven there by
/// a_duplicated_shape_is_the_same_shape_at_every_angle.
///
/// What that Rust test CANNOT see is whether the app actually calls it. These
/// read the source, the same technique ShapeRewriteWiringTests uses and for the
/// same reason: ViewportViewModel is a WinUI class the test assembly cannot
/// load. Drop the call and every Rust test still passes while the bug returns.
/// </summary>
public class ShapeUprightBoundsWiringTests
{
    private static string SourceOf(params string[] relative)
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

    private static string ViewModelSource() =>
        SourceOf("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");

    private static string BodyOf(string signature)
    {
        string source = ViewModelSource();
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, signature + " has been renamed; this test needs updating");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of " + signature);

        return source[start..end];
    }

    // ---------------- the correction happens ----------------

    [Fact]
    public void the_rebuild_corrects_the_rectangle_before_using_it()
    {
        Assert.Contains("UprightBounds(", BodyOf("private static bool ShapeSpecFromTag("),
            StringComparison.Ordinal);
    }

    [Fact]
    public void the_rebuild_does_not_size_the_shape_from_the_raw_selection()
    {
        // The defect itself, spelled out. sel.Left..sel.Bottom is the reported
        // /Rect; handing those four to the reader is what grew the shape.
        string body = BodyOf("private static bool ShapeSpecFromTag(");

        Assert.DoesNotContain("contents, sel.Left, sel.Top, sel.Right, sel.Bottom",
            body, StringComparison.Ordinal);
        Assert.Contains("contents, left, top, right, bottom", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_correction_is_the_cores_and_not_a_second_copy_of_the_arithmetic()
    {
        // The whole point of putting it in render_core is that resize and
        // rebuild share ONE inversion. A C# reimplementation would drift from
        // it exactly the way the tag parser did.
        string body = BodyOf(
            "private static (double Left, double Top, double Right, double Bottom) UprightBounds(");

        Assert.Contains("RenderCoreNative.shape_upright_bounds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Cos", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Sin", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_tag_the_core_cannot_place_falls_back_rather_than_failing()
    {
        // An unknown tag, a zero-width page or a rotated shape written before
        // the tag recorded its upright size. None is a reason to refuse the
        // rebuild: the rectangle as given is what the app used to use.
        string body = BodyOf(
            "private static (double Left, double Top, double Right, double Bottom) UprightBounds(");

        Assert.Contains("(sel.Left, sel.Top, sel.Right, sel.Bottom)", body, StringComparison.Ordinal);
    }

    // ---------------- every caller can supply the page width ----------------

    [Theory]
    [InlineData("ShapeSpecFromTag(e.Contents, pasted, CaptureWidth, pastePageWidthPts,")]
    [InlineData("ShapeSpecFromTag(contents, sel, CaptureWidth,")]
    [InlineData("ShapeSpecFromTag(tag, target, Cap, PagePointsFor(page).W,")]
    public void every_rebuild_site_passes_a_page_width(string call)
    {
        // Points cannot become pixels without one, so a site that could not
        // supply it would silently fall back and keep the old behaviour. Paste,
        // ctrl+drag duplicate and undoing a delete are the three.
        Assert.Contains(call, ViewModelSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_page_width_is_read_once_per_paste_and_not_once_per_shape()
    {
        // Every pasted shape lands on the same page, and PagePointsFor asks the
        // core for the whole size table each time it is called.
        Assert.Contains("double pastePageWidthPts = PagePointsFor(page).W;",
            ViewModelSource(), StringComparison.Ordinal);
    }
}
