using System;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// When a snapshot is taken, and how it is named and described.
///
/// The policy is the interesting part. Taking one too eagerly costs a second
/// of PDFium's lock in the middle of a gesture; taking one too rarely is the
/// afternoon somebody loses.
/// </summary>
public class CrashRecoveryTests
{
    private static readonly TimeSpan Settled = CrashRecovery.IdleGrace + TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongEnough = CrashRecovery.MinInterval + TimeSpan.FromSeconds(1);

    [Fact]
    public void a_document_with_nothing_to_lose_is_never_snapshotted()
    {
        // The common case by a wide margin: the app is being read from, not
        // written to. Copying an unchanged document on a timer would spend the
        // whole session doing nothing useful.
        Assert.False(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: false, sinceLastEdit: Settled, sinceLastSnapshot: LongEnough));

        Assert.False(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: false, sinceLastEdit: TimeSpan.FromHours(9), sinceLastSnapshot: TimeSpan.FromHours(9)));
    }

    [Fact]
    public void nothing_is_snapshotted_in_the_middle_of_a_gesture()
    {
        // A snapshot mid-drag captures a half-drawn shape and spends a second
        // of the render lock doing it, at the one moment the reader is watching
        // the screen closely.
        Assert.False(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: true,
            sinceLastEdit: TimeSpan.Zero,
            sinceLastSnapshot: TimeSpan.FromHours(1)));

        Assert.False(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: true,
            sinceLastEdit: CrashRecovery.IdleGrace - TimeSpan.FromMilliseconds(1),
            sinceLastSnapshot: TimeSpan.FromHours(1)));
    }

    [Fact]
    public void a_settled_document_with_unsaved_work_is_snapshotted()
    {
        Assert.True(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: true, sinceLastEdit: Settled, sinceLastSnapshot: LongEnough));
    }

    [Fact]
    public void snapshots_do_not_come_faster_than_the_floor()
    {
        // Continuous editing must not mean a continuous copy of the document.
        Assert.False(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: true,
            sinceLastEdit: Settled,
            sinceLastSnapshot: CrashRecovery.MinInterval - TimeSpan.FromSeconds(1)));

        Assert.True(CrashRecovery.ShouldSnapshot(
            hasUnsavedWork: true,
            sinceLastEdit: Settled,
            sinceLastSnapshot: CrashRecovery.MinInterval));
    }

    [Fact]
    public void the_floor_bounds_what_anyone_can_lose()
    {
        // The number this feature is judged on. If it grows, say why.
        Assert.Equal(TimeSpan.FromMinutes(2), CrashRecovery.MinInterval);
        Assert.True(CrashRecovery.IdleGrace < CrashRecovery.MinInterval);
    }

    // ---------------- Naming ----------------

    [Fact]
    public void a_document_always_hashes_to_the_same_name()
    {
        string a = CrashRecovery.KeyFor(@"C:\Users\Someone\Reports\Q3 review (final).pdf");
        string b = CrashRecovery.KeyFor(@"C:\Users\Someone\Reports\Q3 review (final).pdf");

        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void the_same_document_reached_by_a_differently_cased_path_is_one_snapshot()
    {
        // Windows paths are case-insensitive, so the same file opened from the
        // recent list and from a drop would otherwise accumulate a snapshot per
        // spelling, and the reader would be offered the same document twice.
        Assert.Equal(
            CrashRecovery.KeyFor(@"C:\Docs\Report.pdf"),
            CrashRecovery.KeyFor(@"c:\docs\REPORT.PDF"));
    }

    [Fact]
    public void two_documents_do_not_share_a_snapshot()
    {
        Assert.NotEqual(
            CrashRecovery.KeyFor(@"C:\Docs\a.pdf"),
            CrashRecovery.KeyFor(@"C:\Docs\b.pdf"));
    }

    [Fact]
    public void a_name_is_safe_to_use_as_a_file_name()
    {
        // The document's path is the user's and can hold anything Windows
        // allows, none of which is guaranteed to be legal as a single file
        // name. Hex only, so nothing has to be escaped.
        foreach (string path in new[]
        {
            @"C:\Docs\a b & c (1).pdf",
            @"\\server\share\Ünïcødé çhârs.pdf",
            "",
            "   ",
        })
        {
            string key = CrashRecovery.KeyFor(path);

            Assert.Matches("^[0-9a-f]{32}$", key);
        }
    }

    // ---------------- What the reader is told ----------------

    [Theory]
    [InlineData(0, "moments ago")]
    [InlineData(30, "moments ago")]
    [InlineData(90, "a minute ago")]
    [InlineData(60 * 12, "12 minutes ago")]
    [InlineData(60 * 90, "an hour ago")]
    [InlineData(60 * 60 * 5, "5 hours ago")]
    [InlineData(60 * 60 * 30, "yesterday")]
    [InlineData(60 * 60 * 24 * 4, "4 days ago")]
    public void the_age_is_described_in_terms_of_how_much_is_being_offered_back(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        long ticks = now.AddSeconds(-secondsAgo).Ticks;

        Assert.Equal(expected, CrashRecovery.DescribeAge(ticks, now));
    }

    [Fact]
    public void a_snapshot_from_the_future_does_not_say_so()
    {
        // A clock change or a profile copied between machines. "Saved in 3
        // hours" would read as a bug and make the reader distrust the offer.
        var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("moments ago", CrashRecovery.DescribeAge(now.AddHours(3).Ticks, now));
    }

    [Fact]
    public void a_document_is_described_by_the_name_the_reader_would_look_for()
    {
        Assert.Equal("Q3 review.pdf", CrashRecovery.DescribeDocument(@"C:\Reports\Q3 review.pdf"));
        Assert.Equal("an unsaved document", CrashRecovery.DescribeDocument(""));
        Assert.Equal("an unsaved document", CrashRecovery.DescribeDocument("   "));
    }

    [Fact]
    public void a_record_carries_the_marks_that_are_not_in_the_document()
    {
        // Highlights and notes live in the app's memory until a save, so a
        // snapshot of the document alone comes back without them, and they are
        // exactly the work most likely to be lost.
        var record = new RecoveryRecord("C:/a.pdf", "C:/snap.pdf", DateTime.UtcNow.Ticks, 12)
        {
            Highlights = { new RecoveredHighlight(3, "#88FFFF00", [new RecoveredRect(0.1, 0.2, 0.3, 0.4)]) },
            Notes = { new RecoveredNote(4, 0.5, 0.6, "check this") },
        };

        Assert.Equal(CrashRecovery.Version, record.Version);
        Assert.Equal(3, record.Highlights[0].PageIndex);
        Assert.Equal("check this", record.Notes[0].Text);
    }
}
