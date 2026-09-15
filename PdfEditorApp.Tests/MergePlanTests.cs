using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Merge files: the order files sort in, and the outline the merged file is
/// written with.
/// </summary>
public class MergePlanTests
{
    private static readonly MergePart[] Parts =
    {
        new("Book A", new[] { 0, 1, 2 }, new Bookmark[] { new(0, 0, "Intro"), new(1, 2, "Deep") }),
        new("Book B", new[] { 5 }, new Bookmark[] { new(0, 5, "Five"), new(0, 1, "Not chosen"), new(0, -1, "Nowhere") }),
    };

    private static (string, int, int)[] Shape(System.Collections.Generic.IReadOnlyList<DetectedHeading> outline) =>
        outline.Select(h => (h.Title, h.Level, h.PageIndex)).ToArray();

    [Fact]
    public void each_file_gets_a_bookmark_with_its_own_bookmarks_nested_under_it()
    {
        Assert.Equal(
            new[] { ("Book A", 1, 0), ("Intro", 2, 0), ("Deep", 3, 2), ("Book B", 1, 3), ("Five", 2, 3) },
            Shape(MergePlan.Outline(Parts, bookmarkEachFile: true, keepFileBookmarks: true)));
    }

    [Fact]
    public void without_file_bookmarks_the_files_own_outlines_are_joined_at_the_top_level()
    {
        Assert.Equal(
            new[] { ("Intro", 1, 0), ("Deep", 2, 2), ("Five", 1, 3) },
            Shape(MergePlan.Outline(Parts, bookmarkEachFile: false, keepFileBookmarks: true)));
    }

    [Fact]
    public void file_bookmarks_alone_mark_where_each_file_starts()
    {
        Assert.Equal(
            new[] { ("Book A", 1, 0), ("Book B", 1, 3) },
            Shape(MergePlan.Outline(Parts, bookmarkEachFile: true, keepFileBookmarks: false)));
        Assert.Empty(MergePlan.Outline(Parts, bookmarkEachFile: false, keepFileBookmarks: false));
    }

    [Fact]
    public void a_section_whose_chapter_was_not_chosen_moves_up_to_a_level_a_pdf_can_hold()
    {
        var parts = new[] { new MergePart("C", new[] { 3 }, new Bookmark[] { new(0, 0, "Chapter"), new(1, 3, "Section") }) };

        Assert.Equal(new[] { ("Section", 1, 0) }, Shape(MergePlan.Outline(parts, bookmarkEachFile: false, keepFileBookmarks: true)));
    }

    [Fact]
    public void file_names_sort_the_way_people_number_them()
    {
        string[] names = { "Chapter 10.pdf", "Chapter 2.pdf", "chapter 1.pdf", "Appendix.pdf", "Chapter 02b.pdf" };

        Array.Sort(names, NaturalOrder.Instance);

        Assert.Equal(new[] { "Appendix.pdf", "chapter 1.pdf", "Chapter 2.pdf", "Chapter 02b.pdf", "Chapter 10.pdf" }, names);
    }
}
