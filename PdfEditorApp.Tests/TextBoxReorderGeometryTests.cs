using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A z-order change must move an object in the stack and nowhere else.
///
/// Every reorder is a delete-and-re-add, because appending is the only
/// ordering primitive PDFium offers, so each raise rebuilds the annotation.
/// A rebuild that takes its geometry from the wrong rectangle turns "send to
/// back" into "send to back and also resize", and because the error compounds
/// it is invisible on the first click and obvious on the fourth.
///
/// The shape and ink branches each solved this and left a comment saying how.
/// The text branch did not, and this holds it to the answer. The measured
/// behaviour is pinned in render_core, which can rebuild a real annotation and
/// read its rectangle back; this guards the CALL, which is the half that was
/// wrong.
/// </summary>
public class TextBoxReorderGeometryTests
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

    private static string RaiseToTopBody()
    {
        string source = ReadSource("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = source.IndexOf("private bool RaiseToTop(", StringComparison.Ordinal);
        Assert.True(at >= 0, "RaiseToTop has moved");

        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; }
            else if (source[i] == '}' && --depth == 0) { return source[at..(i + 1)]; }
        }

        Assert.Fail("unbalanced braces in RaiseToTop");
        return string.Empty;
    }

    [Fact]
    public void a_raised_text_box_is_rebuilt_from_its_own_upright_rect()
    {
        // The fix. The tag records the rect the box actually occupies; the
        // annotation reports, for a turned box, the enlarged box that contains
        // it.
        string body = RaiseToTopBody();

        Assert.Contains("textTag.HasBoxRect ? textTag.BoxLeft", body, StringComparison.Ordinal);
        Assert.Contains("textTag.HasBoxRect ? textTag.BoxTop", body, StringComparison.Ordinal);
        Assert.Contains("textTag.HasBoxRect ? textTag.BoxRight", body, StringComparison.Ordinal);
        Assert.Contains("textTag.HasBoxRect ? textTag.BoxBottom", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_raised_text_box_does_not_use_the_reported_rectangle()
    {
        // The bug, stated so it cannot come back by someone "simplifying" the
        // four lines above into the obvious one-liner.
        string body = RaiseToTopBody();

        Assert.DoesNotContain("float tl = (float)(item.Left * CaptureWidth)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureWidth, tl, tt, tr, tb", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_shape_branch_still_passes_no_bounds_at_all()
    {
        // The working path, guarded because it is the reference the text branch
        // was compared against. restyle with no overrides rebuilds a shape from
        // its tag and undoes the stroke pad itself.
        string body = RaiseToTopBody();

        Assert.Contains("restyle_shape_annotation", body, StringComparison.Ordinal);
        Assert.Contains("colorRgba: 0, widthPx: -1f", body, StringComparison.Ordinal);
    }

    [Fact]
    public void raising_still_only_reorders_and_never_moves_or_resizes()
    {
        // The property the whole command has to keep: nothing in this method
        // may set a position, a size or an angle. set_annotation_bounds and the
        // move and rotate entry points are all absent on purpose, and an
        // "improvement" that reached for one of them would be exactly the
        // regression this file exists for.
        string body = RaiseToTopBody();

        foreach (string forbidden in new[]
        {
            "set_annotation_bounds",
            "move_shape_annotation",
            "rotate_shape_annotation",
            "rotate_text_box_annotation",
            "resize_shape_annotation",
        })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void the_core_pins_both_the_turned_and_the_unturned_case()
    {
        // The behavioural half lives in render_core, which can rebuild a real
        // annotation and read its rectangle back. If those tests are renamed or
        // removed, the guards above are all that is left and they only check
        // that the call LOOKS right.
        string core = ReadSource("render_core", "src", "lib.rs");

        Assert.Contains("fn raising_a_text_box_repeatedly_does_not_move_it",
                        core, StringComparison.Ordinal);
        Assert.Contains("fn raising_a_ROTATED_text_box_repeatedly_does_not_move_it",
                        core, StringComparison.Ordinal);
    }
}
