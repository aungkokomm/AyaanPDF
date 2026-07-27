using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>Which pages a rotate applies to.</summary>
public enum RotateRange
{
    All,
    CurrentPage,
    PageRange,
}

/// <summary>Whether a rotate touches all pages or only odd / only even ones (by page NUMBER, 1-based).</summary>
public enum RotateParity
{
    All,
    OddOnly,
    EvenOnly,
}

/// <summary>An orientation filter, so a rotate can target only landscape or only portrait pages.</summary>
public enum RotateOrientation
{
    Any,
    PortraitOnly,
    LandscapeOnly,
}

/// <summary>
/// Turns a rotate request (range, parity, orientation) into the list of page
/// indices to rotate. Pure and here so the dialog's arithmetic is tested rather
/// than guessed: an off-by-one in odd/even or an inverted range is exactly the
/// kind of thing that silently rotates the wrong pages.
/// </summary>
public static class RotationPlan
{
    /// <summary>
    /// The zero-based page indices to rotate.
    /// </summary>
    /// <param name="fromOneBased">Range start, 1-based inclusive (ignored unless range is PageRange).</param>
    /// <param name="toOneBased">Range end, 1-based inclusive.</param>
    /// <param name="isLandscape">
    /// Per-page landscape flag (as displayed, so rotation is already accounted
    /// for). May be shorter than <paramref name="count"/>; missing entries are
    /// treated as portrait.
    /// </param>
    public static List<int> SelectPages(
        int count,
        RotateRange range,
        int currentPage,
        int fromOneBased,
        int toOneBased,
        RotateParity parity,
        RotateOrientation orientation,
        IReadOnlyList<bool> isLandscape)
    {
        var result = new List<int>();
        if (count <= 0)
        {
            return result;
        }

        // The candidate span, before the parity and orientation filters.
        int first, last;
        switch (range)
        {
            case RotateRange.CurrentPage:
                first = last = Clamp(currentPage, 0, count - 1);
                break;

            case RotateRange.PageRange:
                // 1-based and inclusive; a reversed pair is read the sensible way
                // round rather than yielding nothing.
                int lo = Min(fromOneBased, toOneBased);
                int hi = Max(fromOneBased, toOneBased);
                first = Clamp(lo - 1, 0, count - 1);
                last = Clamp(hi - 1, 0, count - 1);
                break;

            default:
                first = 0;
                last = count - 1;
                break;
        }

        for (int i = first; i <= last; i++)
        {
            // Parity is by page NUMBER: page 1 (index 0) is odd.
            int pageNumber = i + 1;
            bool isOdd = (pageNumber & 1) == 1;
            if ((parity == RotateParity.OddOnly && !isOdd) ||
                (parity == RotateParity.EvenOnly && isOdd))
            {
                continue;
            }

            if (orientation != RotateOrientation.Any)
            {
                bool landscape = i < isLandscape.Count && isLandscape[i];
                if ((orientation == RotateOrientation.LandscapeOnly && !landscape) ||
                    (orientation == RotateOrientation.PortraitOnly && landscape))
                {
                    continue;
                }
            }

            result.Add(i);
        }

        return result;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static int Min(int a, int b) => a < b ? a : b;

    private static int Max(int a, int b) => a > b ? a : b;
}
