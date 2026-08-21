using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the selection frame is worked out from the shape's CURRENT geometry.
///
/// The regression this pins: the frame kept a remembered SIZE, captured when
/// the shape was selected, and nothing recomputed it afterwards. Resizing a
/// shape therefore left the frame and every handle at the old size while the
/// shape itself followed the drag. Measured in the app, resizing a rectangle
/// from 0.3543 wide to 0.7628: the shape's own geometry read 0.7628 and the
/// frame still read 0.3543. It reproduced with the shadow switched off, so it
/// was never anything to do with the shadow.
///
/// Source-reading, the technique AppWiringTests uses and for the same reason:
/// this logic lives in a WinUI class no test assembly can load, which is
/// exactly why it went unnoticed. What the source says is the only thing a
/// test can check here, so it checks the two things that actually went wrong.
/// </summary>
public class SelectionFrameWiringTests
{
    private static string ViewModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string Body(string signature)
    {
        string source = ViewModel();
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " has been renamed; this test needs updating");

        // A blank line ends the member. Matched by regex rather than by
        // searching for a bare newline pair, which never matches a CRLF file
        // and quietly returned the rest of it instead.
        var m = new Regex(@"\r?\n[ \t]*\r?\n").Match(source, at);
        return m.Success ? source[at..m.Index] : source[at..];
    }

    [Fact]
    public void the_frame_is_worked_out_from_the_shapes_own_bounds()
    {
        // Not from anything remembered: UprightBounds takes the live rectangle
        // and the tag, and gives back where the shape actually is.
        string body = Body(
            "private (double Left, double Top, double Right, double Bottom) SelectionFrameOf(");

        Assert.Contains("UprightBounds(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void no_remembered_size_is_kept_for_the_frame()
    {
        // The stale value itself. A field holding the shape's measured width
        // and height is the shape of this bug: it can only be right until the
        // shape changes size.
        Assert.DoesNotContain("_selectedShapeBoxNorm", ViewModel(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_frame_is_the_whole_rectangle_and_not_a_size_centred_on_another()
    {
        // The second half, which the same measurement caught: a shadow grows
        // /Rect on ONE side, so its centre is not the shape's centre. A frame
        // built as "this size, centred there" sat half the shadow's offset away
        // from the shape even when the size was right.
        string body = Body(
            "private (double Left, double Top, double Right, double Bottom) SelectionFrameOf(");

        Assert.DoesNotContain("/ 2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionFrame.Upright", ViewModel(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_cached_tag_is_re_read_after_an_edit_that_rewrites_it()
    {
        // A resize records a new upright size on the tag, and the frame of a
        // TURNED shape is worked out from exactly that. Cached from before the
        // edit, it would be one gesture out of date.
        string source = ViewModel();
        int commit = source.IndexOf("private void CommitLoadedMoveCore()", StringComparison.Ordinal);
        Assert.True(commit > 0, "CommitLoadedMoveCore has been renamed; this test needs updating");

        int end = source.IndexOf("What edge of the selection", commit, StringComparison.Ordinal);
        string body = end > commit ? source[commit..end] : source[commit..];

        Assert.Matches(
            new Regex(@"_selectedShapeTag\s*=\s*ReadAnnotationContents\("),
            body);
    }

    [Fact]
    public void the_stroke_pad_is_only_applied_when_there_is_no_tag_to_do_better()
    {
        // UprightBounds already takes the pad off. Applying it again on top
        // would pull the frame inside the shape by another pad.
        Assert.Contains(
            "_selectedIsShape && _selectedShapeTag is null",
            ViewModel(),
            StringComparison.Ordinal);
    }
}
