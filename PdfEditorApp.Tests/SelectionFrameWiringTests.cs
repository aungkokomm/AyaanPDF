using System;
using System.IO;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// That the overlay actually lays its frame, grips and hit zones out in the
/// shape's own box.
///
/// SelectionFrameTests proves the arithmetic. It cannot prove the view model
/// calls it, and ViewportViewModel is a WinUI class the test assembly cannot
/// load, so these read the source the way ShapeRewriteWiringTests does.
///
/// The one that matters most is that all THREE use the same rectangle. A frame
/// drawn in the shape's box while the handles are grabbed on the /Rect would
/// look fixed and stop working, which is a worse bug than the one being fixed.
/// </summary>
public class SelectionFrameWiringTests
{
    private static string ViewModelSource()
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

    // ---------------- all three sites, one rectangle ----------------

    [Theory]
    [InlineData("var frame = SelectionFrameOf(sel);", "the frame draw")]
    [InlineData("AddGrips(slot, frame, edges:", "the grips")]
    [InlineData("var f = SelectionFrameOf(sel);", "the grip hit zones")]
    public void every_part_of_the_selection_is_laid_out_in_the_same_box(
        string call, string site)
    {
        Assert.True(ViewModelSource().Contains(call, StringComparison.Ordinal),
            site + " no longer goes through SelectionFrameOf");
    }

    [Theory]
    [InlineData("double l = sel.Left  * SlotLayoutWidth + insetDips;")]
    [InlineData("new AnnotationBox(sel.Index, sel.Left, sel.Top, sel.Right, sel.Bottom)")]
    [InlineData("double fl = sel.Left  * SlotLayoutWidth + p;")]
    public void nothing_lays_the_selection_out_from_the_reported_rectangle(string oldForm)
    {
        // Each of these is one of the three places that used the /Rect
        // directly. Any of them coming back reinstates the bug at that site
        // alone, which is exactly how it would go unnoticed.
        Assert.DoesNotContain(oldForm, ViewModelSource(), StringComparison.Ordinal);
    }

    // ---------------- the box is cached, and cleared ----------------

    [Fact]
    public void the_shapes_own_box_is_recovered_through_the_core()
    {
        // Not re-derived in C#. The resize path, the rebuild path and now the
        // overlay all ask the same function, which is the whole point of
        // having exposed it.
        Assert.Contains("var up = UprightBounds(contents!, shapeSel, Cap, pageWpt);",
            ViewModelSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_box_is_cleared_when_the_selection_changes()
    {
        // Without this a shape's box would outlive it and frame whatever was
        // selected next, which for a text box or a stamp would be a rectangle
        // belonging to something else entirely.
        Assert.Contains("_selectedShapeBoxNorm = null;", ViewModelSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_stroke_pad_is_not_taken_off_twice()
    {
        // The recovered box already has the pad off it. Insetting it again
        // would shrink every unrotated shape's frame by a stroke width, which
        // is the kind of small wrong that survives for months.
        Assert.Contains("_selectedIsShape && _selectedShapeBoxNorm is null",
            ViewModelSource(), StringComparison.Ordinal);
    }
}
