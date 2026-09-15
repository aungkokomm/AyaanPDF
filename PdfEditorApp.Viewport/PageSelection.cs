using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which pages of a file are chosen, written the way a person writes them:
/// "1-4, 7, 9". The page picker's text box and its thumbnail grid both go
/// through here, so what is typed and what is ticked can never disagree.
/// </summary>
/// <remarks>
/// Page NUMBERS in text are 1-based, as printed; page INDICES everywhere else
/// are zero-based. Chosen pages always come back in page order, each once:
/// picking is choosing a set, and "9, 1-3" means the same set as "1-3, 9".
/// </remarks>
public static class PageSelection
{
    /// <summary>
    /// Reads "1-4, 7, 9" into zero-based indices in page order. "All", in any
    /// case, is every page; blank is none. A reversed range ("5-3") is read
    /// forwards. On a mistake, <paramref name="error"/> says what is wrong in
    /// words the text box can show.
    /// </summary>
    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> indices, out string? error)
    {
        indices = Array.Empty<int>();
        error = null;

        // An en dash is what a range looks like when it was copied from a
        // document rather than typed.
        string trimmed = (text ?? string.Empty).Replace('–', '-').Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
        {
            indices = Enumerable.Range(0, Math.Max(0, pageCount)).ToArray();
            return true;
        }

        var chosen = new SortedSet<int>();
        foreach (string part in trimmed.Split(','))
        {
            string item = part.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            int dash = item.IndexOf('-');
            int from;
            int to;
            if (dash < 0)
            {
                if (!TryNumber(item, out from))
                {
                    error = $"\"{item}\" isn't a page number.";
                    return false;
                }

                to = from;
            }
            else if (!TryNumber(item[..dash], out from) || !TryNumber(item[(dash + 1)..], out to))
            {
                error = $"\"{item}\" isn't a page range.";
                return false;
            }

            if (from > to)
            {
                (from, to) = (to, from);
            }

            if (from < 1)
            {
                error = "Pages start at 1.";
                return false;
            }

            if (to > pageCount)
            {
                error = $"Page {to} is past the end. This file has {pageCount} {(pageCount == 1 ? "page" : "pages")}.";
                return false;
            }

            for (int page = from; page <= to; page++)
            {
                chosen.Add(page - 1);
            }
        }

        indices = chosen.ToArray();
        return true;
    }

    /// <summary>
    /// Writes zero-based indices the way <see cref="TryParse"/> reads them:
    /// neighbours as a range, every page as "All", none as an empty string.
    /// </summary>
    public static string Format(IEnumerable<int> indices, int pageCount)
    {
        var sorted = indices.Where(i => i >= 0 && i < pageCount).Distinct().Order().ToArray();
        if (sorted.Length == 0)
        {
            return string.Empty;
        }

        if (sorted.Length == pageCount)
        {
            return "All";
        }

        return string.Join(", ", Runs(sorted).Select(run =>
            run.Count == 1
                ? (run.First + 1).ToString(CultureInfo.InvariantCulture)
                : $"{run.First + 1}-{run.First + run.Count}"));
    }

    /// <summary>
    /// Sorted indices as runs of neighbours, so a grid can select "1-5000" in
    /// one call instead of five thousand.
    /// </summary>
    public static IEnumerable<(int First, int Count)> Runs(IReadOnlyList<int> sortedIndices)
    {
        int i = 0;
        while (i < sortedIndices.Count)
        {
            int first = sortedIndices[i];
            int count = 1;
            while (i + count < sortedIndices.Count && sortedIndices[i + count] == first + count)
            {
                count++;
            }

            yield return (first, count);
            i += count;
        }
    }

    /// <summary>Pages 1, 3, 5 and on, as zero-based indices.</summary>
    public static IReadOnlyList<int> OddPages(int pageCount) =>
        Enumerable.Range(0, Math.Max(0, pageCount)).Where(i => i % 2 == 0).ToArray();

    /// <summary>Pages 2, 4, 6 and on, as zero-based indices.</summary>
    public static IReadOnlyList<int> EvenPages(int pageCount) =>
        Enumerable.Range(0, Math.Max(0, pageCount)).Where(i => i % 2 == 1).ToArray();

    private static bool TryNumber(string text, out int number) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
