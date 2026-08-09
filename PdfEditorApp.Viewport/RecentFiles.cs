using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The most-recently-opened list, as a value.
///
/// Kept pure and separate from where it is stored, because the rules are the
/// part that goes wrong: reopening a file must MOVE it to the top rather than
/// add a second entry, the same file spelled two ways is still one file, and
/// the list must not grow without bound.
/// </summary>
public static class RecentFiles
{
    /// <summary>Entries kept. Longer than this and the menu stops being a shortcut.</summary>
    public const int Max = 10;

    /// <summary>
    /// <paramref name="path"/> at the head, everything else after it in order,
    /// with any earlier mention of the same file removed.
    /// </summary>
    public static List<string> Add(IEnumerable<string> existing, string path, int max = Max)
    {
        var list = new List<string> { path };
        foreach (string other in existing)
        {
            // Windows paths are case-insensitive, so "C:\A.pdf" and "c:\a.pdf"
            // are the same file and must not both be listed.
            if (!Same(other, path))
            {
                list.Add(other);
            }
        }

        return list.Count > max ? list.GetRange(0, max) : list;
    }

    /// <summary>
    /// The list with entries removed, for files that have since been deleted or
    /// moved. A recent list that offers files that are not there is worse than
    /// a short one.
    /// </summary>
    public static List<string> Prune(IEnumerable<string> existing, Func<string, bool> exists) =>
        existing.Where(exists).ToList();

    private static bool Same(string a, string b)
    {
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(a),
                System.IO.Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
