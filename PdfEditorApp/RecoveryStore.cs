using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Keeps the snapshots on disk, and hands back whatever a previous run left
/// behind.
///
/// The leftovers ARE the signal. A run that ends properly clears its own
/// snapshots, so anything still here when the app starts belongs to a run that
/// did not: a crash, a power cut, or a task-manager kill. That is why there is
/// no heartbeat file and no "was I running" flag, both of which have to be
/// right in exactly the circumstances where nothing gets a chance to be right.
///
/// Nothing here ever touches the document the user opened. The snapshots live
/// in the app's own folder, beside the settings, and the only paths written are
/// ones this class made up.
/// </summary>
internal static class RecoveryStore
{
    /// <summary>A Recovery folder beside the exe, matching how stamps and
    /// settings turn portable.</summary>
    private static string PortableFolder => Path.Combine(AppContext.BaseDirectory, "Recovery");

    private static string UserFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppInfo.Name,
        "Recovery");

    private static string Folder =>
        Directory.Exists(PortableFolder) ? PortableFolder : UserFolder;

    private static string ManifestPath(string key) => Path.Combine(Folder, key + ".json");

    private static string SnapshotPath(string key) => Path.Combine(Folder, key + ".pdf");

    /// <summary>
    /// Where a snapshot of this document should be written.
    ///
    /// Handed out before the write so the caller can give PDFium a path,
    /// because save_document writes a file rather than handing back bytes.
    /// </summary>
    public static string PathForSnapshot(string documentPath)
    {
        Directory.CreateDirectory(Folder);
        return SnapshotPath(CrashRecovery.KeyFor(documentPath));
    }

    /// <summary>
    /// Records that the snapshot at <see cref="PathForSnapshot"/> is now good.
    ///
    /// Written AFTER the PDF, and separately, so a manifest never describes a
    /// snapshot that does not exist. The reverse leftover, a PDF with no
    /// manifest, is ignored on read and cleaned up, which is the harmless way
    /// round.
    /// </summary>
    public static void Commit(RecoveryRecord record)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string key = CrashRecovery.KeyFor(record.OriginalPath);

            // Written aside and renamed, so a crash DURING the write cannot
            // leave a half-written manifest that the next run would try to
            // restore from. The rename is what publishes it.
            string staging = ManifestPath(key) + ".writing";
            File.WriteAllText(staging, JsonSerializer.Serialize(record));
            File.Move(staging, ManifestPath(key), overwrite: true);
        }
        catch (Exception ex)
        {
            // Losing a snapshot is not worth interrupting what the user is
            // doing; the next one is two minutes away.
            Diag.Log($"recovery: could not write the manifest: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything a previous run left behind, newest first.
    ///
    /// A manifest whose snapshot has gone, or which cannot be read at all, is
    /// dropped rather than reported: the file is on the user's disk and may be
    /// half-written, hand-edited, or from a version that did not exist yet.
    /// </summary>
    public static List<RecoveryRecord> Pending()
    {
        var found = new List<RecoveryRecord>();

        try
        {
            if (!Directory.Exists(Folder))
            {
                return found;
            }

            foreach (string path in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path));

                    if (record is null
                        || record.Version != CrashRecovery.Version
                        || !File.Exists(record.SnapshotPath))
                    {
                        File.Delete(path);
                        continue;
                    }

                    found.Add(record);
                }
                catch (Exception ex)
                {
                    Diag.Log($"recovery: discarding an unreadable manifest: {ex.Message}");
                    try { File.Delete(path); } catch { /* it will be retried next run */ }
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"recovery: could not read the folder: {ex.Message}");
        }

        found.Sort((a, b) => b.SavedAtTicks.CompareTo(a.SavedAtTicks));
        return found;
    }

    /// <summary>
    /// Copies a snapshot somewhere the app will never write again, and returns
    /// where to open it from.
    ///
    /// ⚠️ Opening the snapshot IN PLACE is a data-loss bug, not a tidiness
    /// point. The restored document answers to the ORIGINAL's path, so the next
    /// snapshot two minutes later would target this very file, and PDFium
    /// streams page content lazily from the file it loaded: writing back over
    /// it returns OK, keeps the page count, and blanks every page. That would
    /// destroy the recovered work and the snapshot of it in one call. See
    /// SaveDocumentAs, which takes the same precaution for the same reason.
    ///
    /// A copy in the system temp folder, not the recovery folder, so the two
    /// can never collide however the naming changes.
    /// </summary>
    public static string? TakeForRestore(RecoveryRecord record)
    {
        try
        {
            string working = Path.Combine(
                Path.GetTempPath(),
                $"{AppInfo.Name} recovered {CrashRecovery.KeyFor(record.OriginalPath)}.pdf");

            File.Copy(record.SnapshotPath, working, overwrite: true);
            return working;
        }
        catch (Exception ex)
        {
            Diag.Log($"recovery: could not stage the snapshot: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Drops a document's snapshot.
    ///
    /// Called when the document is saved, when it is closed, and when the
    /// reader declines the offer. The manifest goes FIRST: without it the
    /// snapshot is invisible to the next run, so a failure part-way through
    /// leaves a stray PDF rather than an offer to restore something the user
    /// already dealt with.
    /// </summary>
    public static void Discard(string documentPath) => DiscardKey(CrashRecovery.KeyFor(documentPath));

    private static void DiscardKey(string key)
    {
        try
        {
            string manifest = ManifestPath(key);
            if (File.Exists(manifest)) { File.Delete(manifest); }

            string snapshot = SnapshotPath(key);
            if (File.Exists(snapshot)) { File.Delete(snapshot); }
        }
        catch (Exception ex)
        {
            Diag.Log($"recovery: could not discard a snapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a record the reader has finished with, by what it describes
    /// rather than by where its files are.
    /// </summary>
    public static void Discard(RecoveryRecord record) => Discard(record.OriginalPath);

    /// <summary>
    /// Clears snapshot PDFs with no manifest.
    ///
    /// The harmless leftover: a crash between writing the PDF and publishing
    /// the manifest. Nothing will ever offer them, so without this they would
    /// accumulate a copy of a large document at a time.
    /// </summary>
    public static void SweepOrphans()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (string pdf in Directory.GetFiles(Folder, "*.pdf"))
            {
                string manifest = Path.ChangeExtension(pdf, ".json");
                if (!File.Exists(manifest))
                {
                    File.Delete(pdf);
                }
            }

            foreach (string writing in Directory.GetFiles(Folder, "*.writing"))
            {
                File.Delete(writing);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"recovery: could not sweep: {ex.Message}");
        }
    }
}
