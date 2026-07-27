using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The page-index arithmetic behind organising pages, kept pure and here so it
/// can be tested without a document.
///
/// Every page operation is expressed as an ORDER: the list of source page
/// indices the document should be rebuilt from. A permutation reorders,
/// a repeated index duplicates, an omitted index deletes. The same list is
/// handed to render_core's <c>rebuild_page_order</c> and used to remap the
/// annotation overlay, so the two cannot disagree about where a page went.
/// </summary>
public static class PageReorder
{
    /// <summary>The identity order for a document of <paramref name="count"/> pages: 0,1,2,…</summary>
    public static List<int> Identity(int count)
    {
        var order = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            order.Add(i);
        }

        return order;
    }

    /// <summary>
    /// The order after moving the page at <paramref name="from"/> to sit at
    /// <paramref name="to"/>, both zero-based, in a document of
    /// <paramref name="count"/> pages.
    /// </summary>
    public static List<int> Move(int count, int from, int to)
    {
        var order = Identity(count);
        if (from < 0 || from >= count || to < 0 || to >= count || from == to)
        {
            return order;
        }

        int page = order[from];
        order.RemoveAt(from);
        order.Insert(to, page);
        return order;
    }

    /// <summary>The order that inserts a copy of page <paramref name="index"/> right after it.</summary>
    public static List<int> Duplicate(int count, int index)
    {
        var order = Identity(count);
        if (index >= 0 && index < count)
        {
            order.Insert(index + 1, index);
        }

        return order;
    }

    /// <summary>The order with page <paramref name="index"/> removed. Never empties the document.</summary>
    public static List<int> Delete(int count, int index)
    {
        var order = Identity(count);
        if (count > 1 && index >= 0 && index < count)
        {
            order.RemoveAt(index);
        }

        return order;
    }

    /// <summary>
    /// Where the page that WAS at <paramref name="oldPage"/> ends up under the
    /// given order, or -1 if it was dropped. First occurrence wins, so a
    /// duplicated page's marks follow its first copy.
    ///
    /// This is what an overlay annotation's page index is remapped through, so a
    /// mark stays on its page as the page moves, and is dropped when its page is.
    /// </summary>
    public static int NewIndexOf(IReadOnlyList<int> order, int oldPage)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == oldPage)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True if the order is a genuine reordering of 0..count-1, no adds or drops.</summary>
    public static bool IsPermutation(IReadOnlyList<int> order, int count)
    {
        if (order.Count != count)
        {
            return false;
        }

        var seen = new bool[count];
        foreach (int i in order)
        {
            if (i < 0 || i >= count || seen[i])
            {
                return false;
            }

            seen[i] = true;
        }

        return true;
    }
}
