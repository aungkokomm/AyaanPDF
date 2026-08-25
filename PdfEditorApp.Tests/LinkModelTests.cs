using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A link's place in the document model.
///
/// ⚠️ WHY THIS IS NOT COSMETIC. Before links were named, a real document's
/// hyperlinks arrived as <see cref="DocumentObjectKind.Unknown"/>: anonymous
/// rectangles that the pick returned, that could be dragged and restyled like
/// one of our own marks, and that made the page refuse to reorder. Naming the
/// kind is what lets each of those be answered properly.
/// </summary>
public class LinkModelTests
{
    private static AnnotationSnapshot Link(int index, double top = 0.20) =>
        new(index, PdfAnnotationSubtype.Link, 0.10, top, 0.50, top + 0.04,
            1.0, Guid.NewGuid(), null);

    private static AnnotationSnapshot Stamp(int index) =>
        new(index, PdfAnnotationSubtype.Stamp, 0.10, 0.50, 0.60, 0.70,
            1.0, Guid.NewGuid(), "AyaanStamp:0");

    [Fact]
    public void a_link_is_a_link_and_not_something_unrecognised()
    {
        var page = DocumentModelBuilder.BuildPage(0, [Link(0)]);

        Assert.Equal(DocumentObjectKind.Link, Assert.Single(page.Objects).Kind);
    }

    [Fact]
    public void the_pick_does_not_hand_back_a_link_as_a_draggable_box()
    {
        // The caller turns what comes back into a selection that can be moved,
        // restyled and deleted. A link belongs to the DOCUMENT, and a reader who
        // clicks one is asking to follow it, not to drag it somewhere.
        var page = DocumentModelBuilder.BuildPage(0, [Link(0)]);

        Assert.Null(ObjectHitTest.PickTopmost(page, 0.30, 0.22));
    }

    [Fact]
    public void a_mark_of_ours_under_a_link_is_still_pickable()
    {
        // Skipping links must not blind the pick to what is beneath them: a
        // document's link laid over one of our stamps would otherwise make the
        // stamp unselectable.
        var over = Link(1, top: 0.55);
        var page = DocumentModelBuilder.BuildPage(0, [Stamp(0), over]);

        var hit = ObjectHitTest.PickTopmost(page, 0.30, 0.57);

        Assert.NotNull(hit);
        Assert.Equal(DocumentObjectKind.Stamp, hit!.Kind);
    }

    [Fact]
    public void a_link_still_occupies_its_place_in_the_paint_order()
    {
        // It is skipped by the PICK, not dropped from the model. A page whose
        // foreign annotations were missing would report the wrong stacking for
        // everything above them, which is what z-order decisions are read from.
        var page = DocumentModelBuilder.BuildPage(0, [Stamp(0), Link(1), Stamp(2)]);

        Assert.Equal(3, page.Objects.Count);
        Assert.Equal(DocumentObjectKind.Link, page.Objects[1].Kind);
        Assert.Equal(1, page.Objects[1].ZOrder);
    }

    [Fact]
    public void the_link_subtype_matches_the_number_the_core_sends()
    {
        // Two copies of this table exist, one in each language, because the
        // viewport library cannot reference the app. A silent disagreement here
        // would classify every link as something else.
        Assert.Equal(10, PdfAnnotationSubtype.Link);
    }

    [Fact]
    public void the_link_tool_has_a_shortcut_of_its_own()
    {
        var link = ToolCatalog.For(ToolMode.Link);

        Assert.Equal("Link", link.Name);
        Assert.Equal(ToolMode.Link, ToolCatalog.ForShortcut('l')?.Mode);
        Assert.Equal(ToolMode.Link, ToolCatalog.ForShortcut('L')?.Mode);

        // No options: a link has no appearance to style, so a colour or width
        // control would be a setting that changes nothing.
        Assert.Equal(ToolOptions.None, link.Options);
    }

    [Fact]
    public void every_tool_still_has_a_shortcut_no_other_tool_claims()
    {
        var seen = new System.Collections.Generic.HashSet<char>();

        foreach (var tool in ToolCatalog.All)
        {
            Assert.True(seen.Add(tool.Shortcut), $"{tool.Name} reuses '{tool.Shortcut}'");
        }
    }

    [Fact]
    public void the_link_tool_shows_a_crosshair_like_every_other_tool_that_drags_out_a_box()
    {
        Assert.Equal(
            ViewportCursor.Cross,
            CursorPolicy.Resolve(ToolMode.Link, spaceHandHeld: false, dragging: false));
    }
}
