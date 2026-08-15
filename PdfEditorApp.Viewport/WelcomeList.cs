using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>One row on the welcome screen.</summary>
/// <param name="Path">Full path, for opening it and for the tooltip.</param>
/// <param name="Name">File name without its extension. The extension is the
/// same on every row and says nothing.</param>
/// <param name="Folder">Where it lives, shortened for reading rather than for
/// correctness. The tooltip carries the real path.</param>
/// <param name="Resume">
/// "Page 180 of 3352", or empty for a document that was never read past its
/// first page. This is the row's reason to exist: a recent list says what you
/// opened, and this says where you stopped.
/// </param>
public readonly record struct WelcomeEntry(
    string Path,
    string Name,
    string Folder,
    string Resume);

/// <summary>
/// The recent documents shown on the welcome screen.
///
/// Pure, and given a file-exists test rather than calling the disk itself, so
/// the rules can be asserted: a list full of files that have been moved or
/// deleted is worse than no list, and it is the first thing anyone sees.
/// </summary>
public static class WelcomeList
{
    /// <summary>
    /// Rows shown. Five is what fits beside the buttons without the welcome
    /// screen turning into a file manager.
    /// </summary>
    public const int Max = 5;

    /// <summary>
    /// The rows to show, newest first, skipping anything no longer on disk.
    /// </summary>
    /// <param name="exists">
    /// Whether a path is still openable. Injected so this is testable without
    /// touching a real file system, and so a slow or disconnected network drive
    /// is the caller's problem rather than a hang in here.
    /// </param>
    public static IReadOnlyList<WelcomeEntry> Build(
        IEnumerable<string>? recent,
        IReadOnlyDictionary<string, ReadingPosition>? positions,
        Func<string, bool> exists,
        int max = Max)
    {
        ArgumentNullException.ThrowIfNull(exists);

        if (recent is null)
        {
            return Array.Empty<WelcomeEntry>();
        }

        var rows = new List<WelcomeEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in recent)
        {
            if (rows.Count >= Math.Max(0, max))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path) || !exists(path))
            {
                continue;
            }

            rows.Add(new WelcomeEntry(
                path,
                NameOf(path),
                FolderOf(path),
                ResumeTextFor(ReadingPositions.For(positions, path))));
        }

        return rows;
    }

    /// <summary>
    /// The file name without its extension. Every row is a PDF, so ".pdf" five
    /// times over is noise.
    /// </summary>
    public static string NameOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path ?? "");
        return name.Length > 0 ? name : (path ?? "");
    }

    /// <summary>
    /// A readable home for the file: the containing folder's name, or the drive
    /// for something sitting at a drive root.
    ///
    /// Deliberately not the full path. On the welcome screen the question is
    /// "which of my documents is this", not "where exactly does it live", and a
    /// full path pushes the name off the row. The tooltip has the real thing.
    /// </summary>
    public static string FolderOf(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path ?? "");
            if (string.IsNullOrEmpty(dir))
            {
                return "";
            }

            string leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            return leaf.Length > 0 ? leaf : dir;
        }
        catch (ArgumentException)
        {
            // A path with characters Path cannot parse. The row is still worth
            // showing; it just does not get a folder.
            return "";
        }
    }

    /// <summary>
    /// What the row says about where reading stopped.
    ///
    /// Empty for a document left on its first page, because "Page 1 of 300" is
    /// not progress, it is noise on every row you opened once and closed.
    /// </summary>
    public static string ResumeTextFor(ReadingPosition? position)
    {
        if (position is not { } p || !ReadingPositions.IsWorthRestoring(p))
        {
            return "";
        }

        // One-based, because page numbers shown to a reader always are.
        int page = p.PageIndex + 1;

        return p.PageCount > 0
            ? $"Page {page} of {p.PageCount}"
            : $"Page {page}";
    }
}
