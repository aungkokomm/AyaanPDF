using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Bookmarks follow their pages when pages are reordered, duplicated or
/// deleted, because the rebuilt document has no outline of its own.
/// </summary>
public class OutlineRemapTests
{
    private static readonly Bookmark[] Book =
    {
        new(0, 0, "One"),
        new(1, 2, "Two"),
        new(0, 4, "Four"),
        new(0, -1, "Nowhere"),
    };

    private static DetectedHeading[] Remap(int[] order) =>
        OutlineEdits.Remap(Book, p => PageReorder.NewIndexOf(order, p), 5).ToArray();

    [Fact]
    public void each_bookmark_follows_its_page_and_keeps_its_title_and_nesting()
    {
        var moved = Remap(new[] { 4, 3, 2, 1, 0 });

        Assert.Equal(new[] { 4, 2, 0, -1 }, moved.Select(h => h.PageIndex));
        Assert.Equal(new[] { 1, 2, 1, 1 }, moved.Select(h => h.Level));
        Assert.Equal(new[] { "One", "Two", "Four", "Nowhere" }, moved.Select(h => h.Title));
    }

    [Fact]
    public void a_bookmark_on_a_deleted_page_lands_on_the_next_page_that_survived()
    {
        // Page 2 deleted: its bookmark goes to old page 3, now index 2.
        Assert.Equal(2, Remap(new[] { 0, 1, 3, 4 })[1].PageIndex);
    }

    [Fact]
    public void a_bookmark_on_a_deleted_last_page_lands_on_the_page_before_it()
    {
        Assert.Equal(3, Remap(new[] { 0, 1, 2, 3 })[2].PageIndex);
    }

    [Fact]
    public void a_duplicated_page_keeps_its_bookmark_on_the_first_copy()
    {
        Assert.Equal(new[] { 0, 3, 5, -1 }, Remap(new[] { 0, 0, 1, 2, 3, 4 }).Select(h => h.PageIndex));
    }
}
