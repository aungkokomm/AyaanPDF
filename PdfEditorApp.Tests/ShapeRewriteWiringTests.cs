using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the view model's rebuild path really uses the shared reader, and
/// really copies every field it returns.
///
/// These read the SOURCE, which is the technique AppWiringTests and
/// SearchWiringTests already use for the same reason: ViewportViewModel is a
/// WinUI class and the test assembly cannot load it, so the alternative is no
/// coverage at all. That absence is exactly why a duplicate lost its fill,
/// then its corner radius, then its drop shadow, each found by a person.
///
/// The parsing is now shared and tested properly next door. What is left in
/// the view model is a copy into the interop struct, and a copy is precisely
/// the kind of thing that silently forgets a field. So the check is: every
/// field the shared reader returns must appear on the right-hand side of that
/// copy. Add a field to the tag and forget to thread it, and this fails.
/// </summary>
public class ShapeRewriteWiringTests
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

    /// <summary>The body of ShapeSpecFromTag, which is the copy under test.</summary>
    private static string RebuildBody()
    {
        string source = ViewModelSource();
        int start = source.IndexOf(
            "private static bool ShapeSpecFromTag(", StringComparison.Ordinal);
        Assert.True(start > 0, "ShapeSpecFromTag has been renamed; this test needs updating");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of ShapeSpecFromTag");

        return source[start..end];
    }

    [Fact]
    public void the_rebuild_path_reads_through_the_shared_reader()
    {
        Assert.Contains("ShapeWriter.TryForExistingShape", RebuildBody(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_rebuild_path_does_not_parse_the_tag_itself_any_more()
    {
        // The copy it replaced split the string by hand. If that ever comes
        // back, the fields drift apart again and nothing else would notice.
        string body = RebuildBody();

        Assert.DoesNotContain("Split(':')", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AyaanShape:\"", body, StringComparison.Ordinal);
    }

    [Theory]
    // Every member of ShapeWriteSpec...
    [InlineData("Kind")]
    [InlineData("X1")]
    [InlineData("Y1")]
    [InlineData("X2")]
    [InlineData("Y2")]
    [InlineData("StrokeWidthPx")]
    [InlineData("RotationDeg")]
    [InlineData("CornerRadiusPx")]
    // ...and every member of ShapeStyleSpec.
    [InlineData("A")]
    [InlineData("R")]
    [InlineData("G")]
    [InlineData("B")]
    [InlineData("FillRgba")]
    [InlineData("ShadowDxPx")]
    [InlineData("ShadowDyPx")]
    [InlineData("ShadowRgba")]
    public void every_field_the_reader_returns_is_copied_into_the_interop_struct(string field)
    {
        // Read from the parsed values, not merely mentioned: the field has to
        // appear as `geometry.X` or `style.X` on the right-hand side, so
        // assigning a literal zero to it does not satisfy this.
        string body = RebuildBody();

        Assert.True(
            body.Contains("geometry." + field, StringComparison.Ordinal)
            || body.Contains("style." + field, StringComparison.Ordinal),
            $"ShapeSpecFromTag never reads {field} from the parsed tag, so a rebuilt "
            + "shape loses it. That is how the fill, the corner radius and the drop "
            + "shadow were each lost in turn.");
    }

    [Fact]
    public void the_field_list_above_is_the_whole_of_both_records()
    {
        // Keeps the theory honest. If a member is added to either record and
        // not added to the InlineData above, the copy could drop it and every
        // case would still pass.
        string source = SourceOf("PdfEditorApp.Viewport", "ShapeWriteSpec.cs");

        int expected = new[] { "ShapeWriteSpec(", "ShapeStyleSpec(" }
            .Sum(name =>
            {
                int at = source.IndexOf("public readonly record struct " + name, StringComparison.Ordinal);
                Assert.True(at > 0, $"{name} has been renamed; this test needs updating");
                int close = source.IndexOf(");", at, StringComparison.Ordinal);
                return source[at..close].Count(c => c == ',') + 1;
            });

        Assert.Equal(16, expected);
    }
}
