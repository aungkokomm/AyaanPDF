using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Where the snapshot is taken, and the two things it must never do.
///
/// The policy is covered by CrashRecoveryTests. This covers the wiring, whose
/// failure modes both destroy work rather than merely failing to save it.
/// </summary>
public class CrashRecoveryWiringTests
{
    private static string Read(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    private static string ViewModel() => Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
    private static string Store() => Read("PdfEditorApp", "RecoveryStore.cs");
    private static string PageCode() => Read("PdfEditorApp", "MainPage.xaml.cs");
    private static string WindowCode() => Read("PdfEditorApp", "MainWindow.xaml.cs");

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is gone; this test needs rewriting to match");

        int next = source.IndexOf("\n    private ", at + signature.Length, StringComparison.Ordinal);
        int alt = source.IndexOf("\n    public ", at + signature.Length, StringComparison.Ordinal);
        if (alt >= 0 && (next < 0 || alt < next))
        {
            next = alt;
        }

        return next > at ? source[at..next] : source[at..];
    }

    [Fact]
    public void a_snapshot_never_writes_the_users_file()
    {
        // THE promise. This is not an auto-save: a save nobody asked for is a
        // save nobody can decline, and committing marks to a document somebody
        // was only reading is worse than losing them. The only path PDFium is
        // given comes from RecoveryStore, which makes its own up.
        string body = MethodBody(ViewModel(), "private async System.Threading.Tasks.Task<bool> SnapshotNowAsync");

        Assert.Contains("RecoveryStore.PathForSnapshot(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentDocumentPath)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_snapshot_does_not_mutate_the_open_document()
    {
        // ⚠️ Saving calls these FIRST, and they do not clear what they wrote,
        // which is why the save path reopens the file afterwards. On a
        // two-minute timer they would duplicate every mark in the document the
        // user is still working in.
        string body = MethodBody(ViewModel(), "private async System.Threading.Tasks.Task<bool> SnapshotNowAsync");

        Assert.DoesNotContain("WriteAnnotationObjects", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistGroups", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncSearchableText", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BurnAllAnnotations", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_snapshot_never_writes_over_the_file_the_document_is_read_from()
    {
        // ⚠️ Data loss, not untidiness. PDFium streams page content lazily out
        // of the file it loaded, so writing back over it returns OK, keeps the
        // page count, and blanks every page. A recovered document answers to
        // the ORIGINAL's path while being read from a copy, which is exactly
        // how the snapshot target and the open file can line up.
        string body = MethodBody(ViewModel(), "private async System.Threading.Tasks.Task<bool> SnapshotNowAsync");

        Assert.Contains("_backingPath", body, StringComparison.Ordinal);
        Assert.Contains("refusing to snapshot over the open file", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_restore_opens_a_copy_rather_than_the_snapshot_itself()
    {
        // The other half of the same hazard, and the half that makes the guard
        // above unreachable in normal use.
        string body = MethodBody(ViewModel(), "public bool RestoreFrom");

        Assert.Contains("RecoveryStore.TakeForRestore(record)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenDocument(record.SnapshotPath)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_backing_file_is_recorded_wherever_a_document_is_opened()
    {
        // The guard is only as good as this being set.
        Assert.Contains("_backingPath = path;",
                        MethodBody(ViewModel(), "public DocumentOpenOutcome OpenDocument"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void a_restored_document_answers_to_the_original_and_stays_unsaved()
    {
        // Saving must go where the reader expects, not into the app's own
        // recovery folder, and the original on disk does not contain any of
        // this yet.
        string body = MethodBody(ViewModel(), "public bool RestoreFrom");

        Assert.Contains("record.OriginalPath", body, StringComparison.Ordinal);
        Assert.Contains("IsDirty = true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_marks_that_are_not_in_the_document_travel_with_it()
    {
        // Highlights and notes live in the app's memory until a save writes
        // them, so a snapshot of the document alone comes back without them,
        // and they are the work most likely to be lost.
        string body = MethodBody(ViewModel(), "private async System.Threading.Tasks.Task<bool> SnapshotNowAsync");

        Assert.Contains("Highlights = PendingHighlights()", body, StringComparison.Ordinal);
        Assert.Contains("Notes = PendingNotes()", body, StringComparison.Ordinal);

        string restore = MethodBody(ViewModel(), "public bool RestoreFrom");
        Assert.Contains("_allHighlights.Add(", restore, StringComparison.Ordinal);
        Assert.Contains("_allNotes.Add(", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void unsaved_work_counts_marks_the_document_does_not_know_about()
    {
        // IsDirty tracks the PDFium document. A page full of highlights with no
        // other edit leaves it false, and that is precisely a session worth
        // recovering.
        string body = MethodBody(ViewModel(), "public bool HasUnsavedWork");

        Assert.Contains("_allHighlights.Count", body, StringComparison.Ordinal);
        Assert.Contains("_allNotes.Count", body, StringComparison.Ordinal);
    }

    [Fact]
    public void the_snapshot_runs_off_the_ui_thread()
    {
        // Measured at about 0.4ms per page, so the 3,352-page book takes over a
        // second, and that second must not be one where the window stops
        // answering.
        Assert.Contains("await Task.Run(",
                        MethodBody(ViewModel(), "private async System.Threading.Tasks.Task<bool> SnapshotNowAsync"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void saving_the_document_clears_what_there_was_to_recover()
    {
        // The work is in the user's file now. Leaving the snapshot would offer
        // it back on the next launch as though something had gone wrong.
        Assert.Contains("RecoveryStore.Discard(",
                        MethodBody(ViewModel(), "private bool FinishSave(SavePlan plan)"),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void a_clean_shutdown_clears_the_snapshot_and_that_is_the_whole_signal()
    {
        // There is no heartbeat and no "still running" flag, because both have
        // to be correct in exactly the circumstances where nothing gets a
        // chance to be. What is left behind IS the evidence.
        string body = MethodBody(ViewModel(), "public void ShutDownCleanly");

        Assert.Contains("RecoveryStore.Discard(", body, StringComparison.Ordinal);
        Assert.Contains("_snapshotTimer?.Stop()", body, StringComparison.Ordinal);

        // And the window shuts every tab down, not just the one in front.
        Assert.Contains("page.ViewModel.ShutDownCleanly()",
                        WindowCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_offer_is_made_once_per_run_not_once_per_tab()
    {
        // Every tab is a MainPage running the same Loaded handler.
        string code = PageCode();

        Assert.Contains("private static bool _recoveryOffered;", code, StringComparison.Ordinal);
        Assert.Contains("if (_recoveryOffered)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void declining_keeps_the_work()
    {
        // "Not now" must not be a delete. It may be the only remaining copy of
        // an afternoon's marks, so throwing it away takes the explicit button.
        string body = MethodBody(PageCode(), "private async System.Threading.Tasks.Task<bool> OfferRecoveryAsync");

        var discards = Regex.Matches(body, @"RecoveryStore\.Discard\(");
        Assert.Single(discards);
        Assert.Contains("answer == ContentDialogResult.Secondary", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_manifest_is_published_by_a_rename_so_it_never_half_exists()
    {
        // A crash during the write would otherwise leave a truncated manifest
        // that the next run would try to restore from.
        string body = MethodBody(Store(), "public static void Commit");

        Assert.Contains(".writing", body, StringComparison.Ordinal);
        Assert.Contains("File.Move(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void a_manifest_without_its_snapshot_is_dropped_rather_than_offered()
    {
        string body = MethodBody(Store(), "public static List<RecoveryRecord> Pending");

        Assert.Contains("File.Exists(record.SnapshotPath)", body, StringComparison.Ordinal);
        Assert.Contains("record.Version != CrashRecovery.Version", body, StringComparison.Ordinal);
    }

    [Fact]
    public void nothing_in_the_recovery_folder_is_a_path_the_user_chose()
    {
        // Every path written here is one the app made up from a hash. If a
        // document's own path ever reached a write, this would be an auto-save
        // over the user's file wearing a different name.
        string store = Store();

        foreach (Match m in Regex.Matches(store, @"File\.(WriteAllText|Move|Copy|Delete)\("))
        {
            int line = store.Take(m.Index).Count(c => c == '\n') + 1;
            string statement = store[m.Index..store.IndexOf(';', m.Index)];

            Assert.False(
                statement.Contains("OriginalPath", StringComparison.Ordinal),
                $"line {line} writes to the document's own path: {statement}");
        }
    }
}
