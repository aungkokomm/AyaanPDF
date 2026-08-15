using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The recent documents on the welcome screen.
///
/// This is the first thing anyone sees, so the failure modes matter more than
/// usual: a list of files that have been moved or deleted is worse than no
/// list, and a row that says "Page 1 of 300" on every document is noise
/// dressed up as information.
/// </summary>
public class WelcomeListTests
{
    private static readonly Func<string, bool> AllExist = _ => true;

    private static Dictionary<string, ReadingPosition> Positions(
        params (string Path, int Page, int Count)[] entries)
    {
        var map = new Dictionary<string, ReadingPosition>(StringComparer.Ordinal);
        foreach (var (path, page, count) in entries)
        {
            map[ReadingPositions.Key(path)] =
                new ReadingPosition(page, 0.2, 1.0, DateTime.UtcNow.Ticks, count);
        }
        return map;
    }

    // ---------------- What gets in ----------------

    [Fact]
    public void the_list_is_capped()
    {
        var recent = Enumerable.Range(0, 20).Select(i => $@"C:\docs\book{i}.pdf");

        Assert.Equal(WelcomeList.Max, WelcomeList.Build(recent, null, AllExist).Count);
    }

    [Fact]
    public void order_is_preserved_so_the_newest_is_first()
    {
        var rows = WelcomeList.Build(
            [@"C:\a.pdf", @"C:\b.pdf", @"C:\c.pdf"], null, AllExist);

        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Name));
    }

    [Fact]
    public void a_file_that_is_no_longer_there_is_skipped()
    {
        // Recent lists rot. Offering a row that fails when clicked is a worse
        // first impression than a shorter list.
        var rows = WelcomeList.Build(
            [@"C:\gone.pdf", @"C:\here.pdf"],
            null,
            p => p.EndsWith("here.pdf", StringComparison.Ordinal));

        Assert.Single(rows);
        Assert.Equal("here", rows[0].Name);
    }

    [Fact]
    public void a_missing_file_does_not_use_up_a_slot()
    {
        // Otherwise deleting one document would leave a four-row list with a
        // gap, rather than promoting the next one.
        var recent = new[] { @"C:\gone.pdf" }
            .Concat(Enumerable.Range(0, 10).Select(i => $@"C:\ok{i}.pdf"));

        var rows = WelcomeList.Build(recent, null, p => !p.Contains("gone", StringComparison.Ordinal));

        Assert.Equal(WelcomeList.Max, rows.Count);
    }

    [Fact]
    public void the_same_file_is_not_listed_twice()
    {
        var rows = WelcomeList.Build(
            [@"C:\docs\a.pdf", @"C:\DOCS\A.PDF"], null, AllExist);

        Assert.Single(rows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void blank_entries_are_ignored(string path) =>
        Assert.Empty(WelcomeList.Build([path], null, AllExist));

    [Fact]
    public void no_recent_files_is_an_empty_list_not_a_crash()
    {
        Assert.Empty(WelcomeList.Build(null, null, AllExist));
        Assert.Empty(WelcomeList.Build([], null, AllExist));
    }

    // ---------------- How a row reads ----------------

    [Fact]
    public void the_extension_is_dropped_from_the_name()
    {
        // Every row is a PDF. Saying so five times is noise.
        Assert.Equal("Annual Report", WelcomeList.NameOf(@"C:\docs\Annual Report.pdf"));
    }

    [Fact]
    public void the_folder_is_the_containing_folder_not_the_whole_path()
    {
        // The question on this screen is "which of my documents is this", not
        // "where exactly does it live". A full path pushes the name off the row.
        Assert.Equal("Work", WelcomeList.FolderOf(@"C:\Users\me\Documents\Work\report.pdf"));
    }

    [Fact]
    public void a_file_at_a_drive_root_still_gets_a_sensible_folder() =>
        Assert.Equal(@"C:\", WelcomeList.FolderOf(@"C:\report.pdf"));

    [Fact]
    public void a_path_with_no_folder_gets_no_folder() =>
        Assert.Equal("", WelcomeList.FolderOf("report.pdf"));

    // ---------------- Where you stopped ----------------

    [Fact]
    public void a_row_says_where_reading_stopped()
    {
        // The whole reason this list is better than a plain recent-files menu.
        var rows = WelcomeList.Build(
            [@"C:\book.pdf"], Positions((@"C:\book.pdf", 179, 3352)), AllExist);

        Assert.Equal("Page 180 of 3352", rows[0].Resume);
    }

    [Fact]
    public void page_numbers_are_the_ones_a_reader_sees()
    {
        // Stored zero-based, shown one-based. Off by one here is the kind of
        // thing nobody notices until they are looking at page 179 wondering.
        Assert.Equal("Page 1 of 10",
            WelcomeList.ResumeTextFor(new ReadingPosition(0, 0.5, 1, 0, 10)));
    }

    [Fact]
    public void a_document_left_at_the_very_start_says_nothing()
    {
        // "Page 1 of 300" on every document you opened once and closed is noise
        // pretending to be progress.
        Assert.Equal("", WelcomeList.ResumeTextFor(new ReadingPosition(0, 0, 1, 0, 300)));
        Assert.Equal("", WelcomeList.ResumeTextFor(null));
    }

    [Fact]
    public void a_position_from_before_page_counts_were_stored_still_reads()
    {
        // PageCount defaults to 0 on a record written by an earlier build. The
        // row must degrade to "Page 180" rather than "Page 180 of 0".
        Assert.Equal("Page 180",
            WelcomeList.ResumeTextFor(new ReadingPosition(179, 0.5, 1, 0)));
    }

    [Fact]
    public void a_document_never_opened_has_no_resume_text()
    {
        var rows = WelcomeList.Build([@"C:\fresh.pdf"], Positions(), AllExist);

        Assert.Equal("", rows[0].Resume);
    }

    [Fact]
    public void the_full_path_is_kept_for_opening_and_for_the_tooltip()
    {
        var rows = WelcomeList.Build([@"C:\docs\a.pdf"], null, AllExist);

        Assert.Equal(@"C:\docs\a.pdf", rows[0].Path);
    }
}
