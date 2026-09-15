using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Finds lines of text a layout pass skipped.
/// </summary>
/// <remarks>
/// Measured on a Myanmar page set in Myanmar Text at 14 pt: Tesseract's line
/// finding silently skipped two whole sentences, while every line it did find
/// was read perfectly. A skipped line still leaves ink in its rows, so bands of
/// inked rows that no found line covers are lines to read as well.
/// </remarks>
public static class OcrLineGaps
{
    /// <summary>A band counts as found when a found line covers at least this
    /// share of its rows. Found lines are padded, so their edges often clip the
    /// marks of the line next door; a mere touch must not count.</summary>
    public const double CoveredShare = 0.5;

    /// <param name="rowHasInk">One entry per pixel row of the page image.</param>
    /// <param name="found">Rows already covered by found lines, top inclusive, bottom exclusive.</param>
    /// <param name="minHeight">Shorter bands are specks or rules, not text.</param>
    /// <param name="maxHeight">Taller bands are pictures or merged columns, not one line.</param>
    public static IReadOnlyList<(int Top, int Bottom)> Uncovered(
        IReadOnlyList<bool> rowHasInk,
        IReadOnlyList<(int Top, int Bottom)> found,
        int minHeight,
        int maxHeight)
    {
        var covered = new bool[rowHasInk.Count];
        foreach (var (top, bottom) in found)
        {
            for (int y = Math.Max(0, top); y < Math.Min(covered.Length, bottom); y++) { covered[y] = true; }
        }

        var missed = new List<(int Top, int Bottom)>();
        int start = -1;
        for (int y = 0; y <= rowHasInk.Count; y++)
        {
            bool ink = y < rowHasInk.Count && rowHasInk[y];
            if (ink && start < 0)
            {
                start = y;
            }
            else if (!ink && start >= 0)
            {
                int height = y - start;
                int coveredRows = 0;
                for (int k = start; k < y; k++) { if (covered[k]) { coveredRows++; } }

                if (height >= minHeight && height <= maxHeight && coveredRows < height * CoveredShare)
                {
                    missed.Add((start, y));
                }
                start = -1;
            }
        }

        return missed;
    }
}
