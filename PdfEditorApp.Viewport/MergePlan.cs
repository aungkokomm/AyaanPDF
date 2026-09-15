using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One file's part in a merge: the title its bookmark gets, the pages of it
/// that go in (zero-based, in the order they go in), and its own outline.
/// </summary>
public sealed record MergePart(string Title, IReadOnlyList<int> Pages, IReadOnlyList<Bookmark> Bookmarks);

/// <summary>
/// The outline a merged file is written with. Pure, so the arithmetic of
/// where each bookmark lands can be tested without a document.
/// </summary>
public static class MergePlan
{
    /// <summary>
    /// With <paramref name="bookmarkEachFile"/>, each file starts with a
    /// top-level bookmark named after it. With
    /// <paramref name="keepFileBookmarks"/>, each file's own bookmarks follow,
    /// nested under that one, pointing at where their pages landed.
    /// </summary>
    /// <remarks>
    /// A file's bookmark whose page was not chosen is left out, and so is one
    /// with no target: the merged file has nowhere for either to point.
    /// </remarks>
    public static IReadOnlyList<DetectedHeading> Outline(
        IReadOnlyList<MergePart> parts, bool bookmarkEachFile, bool keepFileBookmarks)
    {
        var outline = new List<DetectedHeading>();
        int nest = bookmarkEachFile ? 1 : 0;
        int start = 0;

        foreach (var part in parts)
        {
            if (part.Pages.Count == 0)
            {
                continue;
            }

            if (bookmarkEachFile)
            {
                outline.Add(new DetectedHeading(part.Title, 1, start));
            }

            if (keepFileBookmarks)
            {
                foreach (var mark in part.Bookmarks)
                {
                    int at = mark.PageIndex < 0 ? -1 : IndexOfPage(part.Pages, mark.PageIndex);
                    if (at >= 0)
                    {
                        outline.Add(new DetectedHeading(mark.Title, mark.Depth + 1 + nest, start + at));
                    }
                }
            }

            start += part.Pages.Count;
        }

        return OutlineEdits.Repair(outline);
    }

    private static int IndexOfPage(IReadOnlyList<int> pages, int page)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            if (pages[i] == page)
            {
                return i;
            }
        }

        return -1;
    }
}
