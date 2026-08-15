using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Where someone had got to in a document.
/// </summary>
/// <param name="PageIndex">The page that was in view, zero-based.</param>
/// <param name="PageFraction">
/// How far down that page, 0 to 1. Stored as a fraction of the PAGE rather than
/// as a scroll offset in pixels, because the offset only means anything at the
/// zoom and window size it was taken at. A fraction survives both.
/// </param>
/// <param name="Zoom">
/// The zoom it was read at. A reader that returns you to page 180 at a
/// different magnification has only done half the job.
/// </param>
/// <param name="SavedAtTicks">
/// When it was stored, so the oldest can be dropped once the list is full.
/// Ticks rather than DateTime so the JSON is a plain number and cannot fail to
/// parse on a machine with different date settings.
/// </param>
/// <param name="PageCount">
/// How many pages the document had. Stored so the welcome list can say "page
/// 180 of 3352" without opening the file, which on a book of that size is
/// several seconds of work to answer a question the reader only glances at.
/// Defaulted, so a position written by an earlier build still loads.
/// </param>
public readonly record struct ReadingPosition(
    int PageIndex,
    double PageFraction,
    double Zoom,
    long SavedAtTicks,
    int PageCount = 0);

/// <summary>
/// The remembered place in every document that has been read.
///
/// Pure, and separate from where it is stored, for the reason RecentFiles
/// already records: the rules are the part that goes wrong. The same file
/// spelled two ways is one file, reopening must replace rather than accumulate,
/// and the list cannot grow without bound on a machine that opens thousands of
/// documents.
/// </summary>
public static class ReadingPositions
{
    /// <summary>
    /// How many documents are remembered.
    ///
    /// Far more than the ten in the recent list, because forgetting where you
    /// were is exactly the failure this exists to prevent and a reader may
    /// have several books on the go. Still bounded: this lives in the settings
    /// file, which is read on every launch.
    /// </summary>
    public const int Max = 100;

    /// <summary>
    /// A fraction that is out of range means a corrupt or hand-edited file, and
    /// clamping is kinder than refusing to restore anything.
    /// </summary>
    public static ReadingPosition Sanitised(ReadingPosition p) => p with
    {
        PageIndex = Math.Max(0, p.PageIndex),
        PageCount = Math.Max(0, p.PageCount),
        PageFraction = double.IsFinite(p.PageFraction) ? Math.Clamp(p.PageFraction, 0, 1) : 0,
        Zoom = double.IsFinite(p.Zoom) && p.Zoom > 0 ? p.Zoom : 0,
    };

    /// <summary>
    /// The key a path is stored under.
    ///
    /// Windows paths are case-insensitive, so "C:\Book.pdf" and "c:\book.pdf"
    /// are the same document and must not get two entries with two different
    /// positions. Upper-cased with the invariant culture, not the current one:
    /// a Turkish machine lower-cases I to a dotless ı, which would key the same
    /// file differently depending on who was running the app.
    /// </summary>
    public static string Key(string path) =>
        (path ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// The map with this document's position recorded, replacing any earlier
    /// one for the same file.
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read, so eviction is testable without waiting.
    /// </param>
    public static Dictionary<string, ReadingPosition> Remember(
        IReadOnlyDictionary<string, ReadingPosition>? existing,
        string path,
        ReadingPosition position,
        DateTime now,
        int max = Max)
    {
        var map = existing is null
            ? new Dictionary<string, ReadingPosition>(StringComparer.Ordinal)
            : new Dictionary<string, ReadingPosition>(existing, StringComparer.Ordinal);

        string key = Key(path);
        if (key.Length == 0)
        {
            // An unsaved document has nowhere to be remembered against.
            return map;
        }

        map[key] = Sanitised(position with { SavedAtTicks = now.Ticks });

        // Oldest out first. Without this the settings file grows for the life
        // of the install, and it is read synchronously at every launch.
        while (map.Count > Math.Max(1, max))
        {
            string oldest = map.OrderBy(e => e.Value.SavedAtTicks).First().Key;
            map.Remove(oldest);
        }

        return map;
    }

    /// <summary>Where this document was left, or null if it has not been read.</summary>
    public static ReadingPosition? For(
        IReadOnlyDictionary<string, ReadingPosition>? map, string path)
    {
        if (map is null)
        {
            return null;
        }

        string key = Key(path);
        return key.Length > 0 && map.TryGetValue(key, out var found) ? Sanitised(found) : null;
    }

    /// <summary>
    /// Drops a document, for when it has been deleted or the user asks to
    /// forget it.
    /// </summary>
    public static Dictionary<string, ReadingPosition> Forget(
        IReadOnlyDictionary<string, ReadingPosition>? existing, string path)
    {
        var map = existing is null
            ? new Dictionary<string, ReadingPosition>(StringComparer.Ordinal)
            : new Dictionary<string, ReadingPosition>(existing, StringComparer.Ordinal);

        map.Remove(Key(path));
        return map;
    }

    /// <summary>
    /// Whether a remembered position is worth restoring.
    ///
    /// Page 0 at the very top is where the document opens anyway, so restoring
    /// it is indistinguishable from not restoring it, and it would make the
    /// "resumed at page N" message a lie. The zoom is still worth applying.
    /// </summary>
    public static bool IsWorthRestoring(ReadingPosition p) =>
        p.PageIndex > 0 || p.PageFraction > 0.01;
}
