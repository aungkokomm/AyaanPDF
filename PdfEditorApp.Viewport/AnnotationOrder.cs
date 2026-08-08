namespace PdfEditorApp.Viewport;

/// <summary>
/// Paint order for the annotations on one page: first in the list is painted
/// first, so the LAST entry is the one on top.
///
/// PDFium has no move or swap for annotations, and the /Annots array is not
/// reachable through its public API. Appending is the only ordering primitive
/// there is, so every reorder is carried out by removing annotations and
/// re-adding them in the order wanted. That is expensive and lossy for anything
/// the app cannot rebuild, which is why the target order is worked out here as
/// plain data first: the caller can count the rewrites and check what they would
/// touch BEFORE it destroys anything.
/// </summary>
public static class AnnotationOrder
{
    /// <summary>Selected items to the very bottom, keeping their order relative
    /// to each other, with everything else stacked above them unchanged.</summary>
    public static List<Guid> SendToBack(IReadOnlyList<Guid> current, ISet<Guid> moving)
    {
        var result = new List<Guid>(current.Count);
        result.AddRange(current.Where(moving.Contains));
        result.AddRange(current.Where(id => !moving.Contains(id)));
        return result;
    }

    /// <summary>Selected items to the very top, keeping their relative order.</summary>
    public static List<Guid> BringToFront(IReadOnlyList<Guid> current, ISet<Guid> moving)
    {
        var result = new List<Guid>(current.Count);
        result.AddRange(current.Where(id => !moving.Contains(id)));
        result.AddRange(current.Where(moving.Contains));
        return result;
    }

    /// <summary>
    /// Every selected item rises by exactly one place, which is the single-step
    /// convention in Illustrator and PowerPoint: an item already at the top
    /// stays, and a run of selected items moves as a block rather than
    /// scrambling its own internal order.
    ///
    /// Walking DOWNWARD matters. Going the other way, an item that had just been
    /// raised would be met again by the next comparison and raised twice.
    /// </summary>
    public static List<Guid> BringForward(IReadOnlyList<Guid> current, ISet<Guid> moving)
    {
        var result = new List<Guid>(current);
        for (int i = result.Count - 2; i >= 0; i--)
        {
            if (moving.Contains(result[i]) && !moving.Contains(result[i + 1]))
            {
                (result[i], result[i + 1]) = (result[i + 1], result[i]);
            }
        }
        return result;
    }

    /// <summary>Every selected item drops by one place. Mirror of
    /// <see cref="BringForward"/>, walking upward for the same reason.</summary>
    public static List<Guid> SendBackward(IReadOnlyList<Guid> current, ISet<Guid> moving)
    {
        var result = new List<Guid>(current);
        for (int i = 1; i < result.Count; i++)
        {
            if (moving.Contains(result[i]) && !moving.Contains(result[i - 1]))
            {
                (result[i], result[i - 1]) = (result[i - 1], result[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// The first position where the two orders disagree, which is where the
    /// rewrite has to start. Everything before it is already correct and must be
    /// left alone: those annotations are then never removed, so a foreign
    /// annotation sitting in the untouched prefix survives a reorder that would
    /// otherwise have to destroy it.
    ///
    /// Re-adding <c>target[from..]</c> in order lands the page in exactly
    /// <c>target</c>, because each re-add appends to the end.
    /// </summary>
    public static int RewriteFrom(IReadOnlyList<Guid> current, IReadOnlyList<Guid> target)
    {
        int n = Math.Min(current.Count, target.Count);
        for (int i = 0; i < n; i++)
        {
            if (current[i] != target[i]) { return i; }
        }
        return n;
    }
}
