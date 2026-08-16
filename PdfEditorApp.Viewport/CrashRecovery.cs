using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What was on screen when the app stopped, so it can be offered back.
///
/// A snapshot beside a manifest rather than one file: the snapshot is a real
/// PDF written by PDFium, and the marks that are not yet IN the document have
/// to travel next to it.
/// </summary>
/// <param name="OriginalPath">
/// The document this came from, so a restore can save back to the right file.
/// Empty for a document that has never been saved anywhere.
/// </param>
/// <param name="SnapshotPath">The PDF holding the state at the moment it was taken.</param>
/// <param name="SavedAtTicks">UTC ticks, for telling the reader how much they are getting back.</param>
public sealed record RecoveryRecord(
    string OriginalPath,
    string SnapshotPath,
    long SavedAtTicks,
    int PageCount)
{
    /// <summary>Format of the manifest, so a future change can refuse an old one.</summary>
    public int Version { get; init; } = CrashRecovery.Version;

    /// <summary>
    /// Marks that exist only in the app's memory and not in the document.
    ///
    /// Highlights and notes are still added to overlay lists and only written
    /// into the PDF when the user saves, so a snapshot of the document alone
    /// would come back without them, which is exactly the work most likely to
    /// be lost.
    /// </summary>
    public List<RecoveredHighlight> Highlights { get; init; } = new();

    public List<RecoveredNote> Notes { get; init; } = new();
}

/// <summary>One highlight, flattened to what it takes to put it back.</summary>
public sealed record RecoveredHighlight(int PageIndex, string ColorHex, List<RecoveredRect> Rects);

public sealed record RecoveredRect(double Left, double Top, double Right, double Bottom);

/// <summary>A sticky note. No colour: a note does not carry one.</summary>
public sealed record RecoveredNote(int PageIndex, double X, double Y, string Text);

/// <summary>
/// When to take a snapshot, and how to name it.
///
/// Deliberately NOT an auto-save in the usual sense: nothing here ever writes
/// the user's file. A save that happens without being asked for is a save the
/// user cannot decline, and the one thing worse than losing an afternoon's
/// marks is silently committing them to a document someone was only reading.
/// So the work goes to a copy the app owns, and the user is offered it back.
///
/// Cost is measured, not guessed: PDFium writes about 0.4ms per page, so a
/// 300-page document snapshots in 120ms and the 3,352-page book in about 1.3
/// seconds. That is cheap enough to do on a timer and far too expensive to do
/// on the UI thread or on every keystroke, which is what the policy below is
/// shaped around.
/// </summary>
public static class CrashRecovery
{
    public const int Version = 1;

    /// <summary>
    /// How long the document must have been left alone before a snapshot.
    ///
    /// Snapshotting mid-gesture would take a copy of a half-drawn shape and
    /// spend a second of PDFium's lock doing it, in the middle of the one
    /// operation where the reader is watching the screen closely.
    /// </summary>
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Floor on how often a snapshot is taken, however much is happening.
    ///
    /// Two minutes is the most work anyone loses, against a second of lock on a
    /// very large document. Shorter would spend more time copying the document
    /// than reading it.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether now is the moment to take one.
    ///
    /// Pure, so the rule can be tested without a clock, a timer or a document.
    /// </summary>
    public static bool ShouldSnapshot(
        bool hasUnsavedWork,
        TimeSpan sinceLastEdit,
        TimeSpan sinceLastSnapshot)
    {
        // Nothing to lose. This is the common case by a wide margin: most of
        // the time the app is being read from, not written to.
        if (!hasUnsavedWork)
        {
            return false;
        }

        // Mid-gesture. Wait for a pause rather than freezing one.
        if (sinceLastEdit < IdleGrace)
        {
            return false;
        }

        return sinceLastSnapshot >= MinInterval;
    }

    /// <summary>
    /// A stable file name for a document.
    ///
    /// Hashed rather than derived from the name, because the name is the user's:
    /// it can contain anything Windows allows in a path and nothing that is
    /// legal there is guaranteed to be legal as a single file name. Case is
    /// folded first so the same document reached by a differently-cased path
    /// keeps one snapshot instead of accumulating one per spelling.
    /// </summary>
    public static string KeyFor(string documentPath)
    {
        string normalized = (documentPath ?? string.Empty).Trim().ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        var text = new StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// How the reader is told what they are being offered.
    ///
    /// In how-long-ago terms rather than a timestamp: the question being
    /// answered is "is this worth taking back", and "12 minutes ago" answers it
    /// where "14:23" makes them work it out.
    /// </summary>
    public static string DescribeAge(long savedAtTicks, DateTime utcNow)
    {
        var age = utcNow - new DateTime(savedAtTicks, DateTimeKind.Utc);

        if (age < TimeSpan.Zero)
        {
            // A clock change, or a file copied from another machine. Saying
            // "in 3 hours" would be worse than saying nothing.
            return "moments ago";
        }

        if (age < TimeSpan.FromMinutes(1)) { return "moments ago"; }
        if (age < TimeSpan.FromMinutes(2)) { return "a minute ago"; }
        if (age < TimeSpan.FromHours(1)) { return $"{(int)age.TotalMinutes} minutes ago"; }
        if (age < TimeSpan.FromHours(2)) { return "an hour ago"; }
        if (age < TimeSpan.FromDays(1)) { return $"{(int)age.TotalHours} hours ago"; }
        if (age < TimeSpan.FromDays(2)) { return "yesterday"; }

        return $"{(int)age.TotalDays} days ago";
    }

    /// <summary>
    /// What the reader is told the recovered document IS.
    ///
    /// The original's name, because that is what they will look for. A snapshot
    /// of a document never saved anywhere has only the app's word for it.
    /// </summary>
    public static string DescribeDocument(string originalPath) =>
        string.IsNullOrWhiteSpace(originalPath)
            ? "an unsaved document"
            : System.IO.Path.GetFileName(originalPath);
}
