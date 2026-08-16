using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Adding, renaming and removing one bookmark.
///
/// The two bulk features build an outline from scratch and replace whatever the
/// document had. This is the half a reader reaches for first: mark the page in
/// front of them, fix a title that came out wrong, drop one that did not
/// belong.
/// </summary>
public class OutlineEditsTests
{
    private static Bookmark Mark(int depth, int page, string title) => new(depth, page, title);

    [Fact]
    public void a_new_bookmark_lands_in_page_order()
    {
        // Appended, it would sit at the bottom of a list reaching page 400,
        // which is a bookmark nobody will find again.
        var existing = new[]
        {
            Mark(0, 0, "Front"),
            Mark(0, 10, "Middle"),
            Mark(0, 90, "Back"),
        };

        var after = OutlineEdits.Add(existing, "New", 40);

        Assert.Equal(["Front", "Middle", "New", "Back"], after.Select(h => h.Title));
        Assert.Equal([0, 10, 40, 90], after.Select(h => h.PageIndex));
    }

    [Fact]
    public void a_bookmark_for_a_page_that_already_has_one_goes_after_it()
    {
        // Two landmarks on one page is ordinary, and the newer one is the one
        // just made, so it reads second.
        var existing = new[] { Mark(0, 5, "First on five") };

        var after = OutlineEdits.Add(existing, "Second on five", 5);

        Assert.Equal(["First on five", "Second on five"], after.Select(h => h.Title));
    }

    [Fact]
    public void the_first_bookmark_in_an_empty_document_works()
    {
        var after = OutlineEdits.Add([], "Only one", 3);

        var only = Assert.Single(after);
        Assert.Equal("Only one", only.Title);
        Assert.Equal(3, only.PageIndex);
        Assert.Equal(1, only.Level);
    }

    [Fact]
    public void nesting_survives_an_edit_elsewhere()
    {
        // The panel's depth counts from zero and the writer's level from one.
        // Getting that off by one would flatten every outline it touched.
        var existing = new[]
        {
            Mark(0, 0, "Chapter"),
            Mark(1, 1, "Section"),
            Mark(2, 2, "Sub"),
        };

        var after = OutlineEdits.Add(existing, "Later", 50);

        Assert.Equal([1, 2, 3, 1], after.Select(h => h.Level));
    }

    [Fact]
    public void deleting_a_parent_lifts_its_children_rather_than_taking_them()
    {
        // A great deal of destruction from one keystroke, otherwise. The reader
        // who wanted the children gone can delete them too.
        var existing = new[]
        {
            Mark(0, 0, "Chapter"),
            Mark(1, 1, "Section"),
            Mark(2, 2, "Sub"),
        };

        var after = OutlineEdits.Remove(existing, 0);

        Assert.Equal(["Section", "Sub"], after.Select(h => h.Title));

        // And the orphan is pulled up to a level a PDF can express.
        Assert.Equal([1, 2], after.Select(h => h.Level));
    }

    [Fact]
    public void renaming_changes_one_title_and_nothing_else()
    {
        var existing = new[] { Mark(0, 0, "Old"), Mark(1, 4, "Child") };

        var after = OutlineEdits.Rename(existing, 0, "  New   name  ");

        Assert.Equal(["New name", "Child"], after.Select(h => h.Title));
        Assert.Equal([0, 4], after.Select(h => h.PageIndex));
        Assert.Equal([1, 2], after.Select(h => h.Level));
    }

    [Fact]
    public void a_blank_rename_is_refused()
    {
        // It would show as an empty row, which reads as a rendering fault.
        var existing = new[] { Mark(0, 0, "Keep me") };

        Assert.Equal("Keep me", OutlineEdits.Rename(existing, 0, "   ")[0].Title);
        Assert.Equal("Keep me", OutlineEdits.Rename(existing, 0, "")[0].Title);
    }

    [Fact]
    public void an_index_that_is_not_there_changes_nothing()
    {
        // The panel and the outline can disagree for an instant after a write.
        var existing = new[] { Mark(0, 0, "One") };

        Assert.Single(OutlineEdits.Remove(existing, 5));
        Assert.Single(OutlineEdits.Remove(existing, -1));
        Assert.Equal("One", OutlineEdits.Rename(existing, 9, "Nope")[0].Title);
    }

    [Fact]
    public void the_selected_text_becomes_the_title()
    {
        // Tidied, because extracted PDF text is full of stray spaces where the
        // layout left gaps between glyphs.
        Assert.Equal("Chapter One", OutlineEdits.TitleFrom("  Chapter    One\n", 0));
    }

    [Fact]
    public void selecting_nothing_still_names_the_page_truthfully()
    {
        // "Page 412" is a true statement and a usable landmark. "Untitled" is
        // neither, and it is what every reader that does this gets wrong.
        Assert.Equal("Page 412", OutlineEdits.TitleFrom(null, 411));
        Assert.Equal("Page 1", OutlineEdits.TitleFrom("   ", 0));
    }

    [Fact]
    public void a_selected_paragraph_becomes_a_title_and_not_a_paragraph()
    {
        // A reader who selects a paragraph and presses the shortcut means the
        // start of it. The whole thing would push every other entry off the
        // side of the panel.
        string paragraph = string.Join(" ", Enumerable.Repeat("word", 200));

        string title = OutlineEdits.TitleFrom(paragraph, 0);

        Assert.True(title.Length <= OutlineEdits.LongestTitle + 1, $"title was {title.Length} long");
        Assert.EndsWith("…", title);

        // Cut at a word boundary, so it does not end mid-word.
        Assert.DoesNotContain("wor…", title);
    }

    [Fact]
    public void a_long_title_with_no_spaces_is_still_cut()
    {
        // A script that does not space its words, or a run of glyphs PDFium
        // reported without gaps. There is no word boundary to find, and the cap
        // still has to hold.
        string unbroken = new('क', 400);

        Assert.True(OutlineEdits.TitleFrom(unbroken, 0).Length <= OutlineEdits.LongestTitle + 1);
    }
}
