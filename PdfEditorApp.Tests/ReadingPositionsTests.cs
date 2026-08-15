using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Remembering where someone had got to.
///
/// The complaint this answers: open a 400-page book, read to page 180, close
/// it, open it again, and you are back at page 1. Every reader anyone has used
/// remembers, and the project's own spec says its differentiator is matching
/// the muscle memory people already have.
///
/// Pure, like RecentFiles and for the same reason: the rules are the part that
/// goes wrong, not the storage.
/// </summary>
public class ReadingPositionsTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ReadingPosition At(int page, double fraction = 0.5, double zoom = 1.0) =>
        new(page, fraction, zoom, 0);

    // ---------------- Identity ----------------

    [Fact]
    public void the_same_file_spelled_two_ways_is_one_document()
    {
        // Windows paths are case-insensitive. Two entries would mean the
        // position you get back depends on how the file happened to be opened.
        var map = ReadingPositions.Remember(null, @"C:\Books\Atlas.pdf", At(180), Now);
        map = ReadingPositions.Remember(map, @"c:\books\atlas.PDF", At(240), Now);

        Assert.Single(map);
        Assert.Equal(240, ReadingPositions.For(map, @"C:\BOOKS\ATLAS.pdf")!.Value.PageIndex);
    }

    [Fact]
    public void keys_do_not_depend_on_the_machines_language()
    {
        // Turkish lower-cases I to a dotless i. Using the current culture would
        // key the same file differently depending on who ran the app, so this
        // pins the invariant casing rather than merely the result.
        Assert.Equal(
            ReadingPositions.Key(@"C:\INDEX.pdf"),
            ReadingPositions.Key(@"c:\index.pdf"));
    }

    [Fact]
    public void reopening_replaces_rather_than_accumulates()
    {
        var map = ReadingPositions.Remember(null, @"C:\a.pdf", At(10), Now);
        map = ReadingPositions.Remember(map, @"C:\a.pdf", At(20), Now);
        map = ReadingPositions.Remember(map, @"C:\a.pdf", At(30), Now);

        Assert.Single(map);
        Assert.Equal(30, ReadingPositions.For(map, @"C:\a.pdf")!.Value.PageIndex);
    }

    [Fact]
    public void a_document_that_has_never_been_read_has_no_position()
    {
        Assert.Null(ReadingPositions.For(null, @"C:\new.pdf"));
        Assert.Null(ReadingPositions.For(
            ReadingPositions.Remember(null, @"C:\a.pdf", At(5), Now), @"C:\b.pdf"));
    }

    [Fact]
    public void a_document_with_no_path_is_not_remembered()
    {
        // File > New produces a document that has never been saved. There is
        // nothing to key it against, and inventing a key would make two blank
        // documents share a position.
        Assert.Empty(ReadingPositions.Remember(null, "", At(5), Now));
        Assert.Empty(ReadingPositions.Remember(null, "   ", At(5), Now));
    }

    // ---------------- Bounds ----------------

    [Fact]
    public void the_list_cannot_grow_without_bound()
    {
        // It lives in the settings file, which is read synchronously at launch.
        var map = new Dictionary<string, ReadingPosition>();
        for (int i = 0; i < ReadingPositions.Max + 25; i++)
        {
            map = ReadingPositions.Remember(map, $@"C:\book{i}.pdf", At(i), Now.AddMinutes(i));
        }

        Assert.Equal(ReadingPositions.Max, map.Count);
    }

    [Fact]
    public void the_oldest_document_is_the_one_dropped()
    {
        var map = ReadingPositions.Remember(null, @"C:\oldest.pdf", At(1), Now);
        for (int i = 0; i < ReadingPositions.Max; i++)
        {
            map = ReadingPositions.Remember(map, $@"C:\later{i}.pdf", At(i), Now.AddMinutes(i + 1));
        }

        Assert.Null(ReadingPositions.For(map, @"C:\oldest.pdf"));
        Assert.NotNull(ReadingPositions.For(map, @"C:\later5.pdf"));
    }

    [Fact]
    public void reading_a_document_again_keeps_it_from_being_evicted()
    {
        // The book you are actually reading must not be dropped just because it
        // was first opened a long time ago.
        var map = ReadingPositions.Remember(null, @"C:\current.pdf", At(1), Now);
        for (int i = 0; i < ReadingPositions.Max - 1; i++)
        {
            map = ReadingPositions.Remember(map, $@"C:\other{i}.pdf", At(i), Now.AddMinutes(i + 1));
        }

        // Touched again, most recently of all.
        map = ReadingPositions.Remember(map, @"C:\current.pdf", At(300), Now.AddHours(1));
        map = ReadingPositions.Remember(map, @"C:\overflow.pdf", At(1), Now.AddHours(2));

        Assert.Equal(300, ReadingPositions.For(map, @"C:\current.pdf")!.Value.PageIndex);
    }

    // ---------------- Bad data ----------------

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(180, 180)]
    public void a_negative_page_is_clamped(int stored, int expected) =>
        Assert.Equal(expected, ReadingPositions.Sanitised(At(stored)).PageIndex);

    [Theory]
    [InlineData(1.5, 1)]
    [InlineData(-0.2, 0)]
    [InlineData(0.4, 0.4)]
    public void a_fraction_outside_the_page_is_clamped(double stored, double expected) =>
        Assert.Equal(expected, ReadingPositions.Sanitised(At(1, stored)).PageFraction);

    [Fact]
    public void nonsense_numbers_from_a_hand_edited_file_do_not_escape()
    {
        // The settings file is the user's and may be edited, truncated, or
        // written by a future version. NaN reaching the scroller would move the
        // view to nowhere and leave a blank canvas.
        var bad = ReadingPositions.Sanitised(new ReadingPosition(3, double.NaN, double.NaN, 0));

        Assert.Equal(0, bad.PageFraction);
        Assert.Equal(0, bad.Zoom);
    }

    [Fact]
    public void a_zero_or_negative_zoom_is_treated_as_no_zoom()
    {
        // Rather than restoring a zoom of zero, which renders nothing at all.
        Assert.Equal(0, ReadingPositions.Sanitised(At(1, 0.5, 0)).Zoom);
        Assert.Equal(0, ReadingPositions.Sanitised(At(1, 0.5, -2)).Zoom);
    }

    // ---------------- Restoring ----------------

    [Fact]
    public void the_top_of_the_first_page_is_not_worth_restoring()
    {
        // It is where the document opens anyway, so "restoring" it is
        // indistinguishable from doing nothing, and announcing it would be a
        // lie.
        Assert.False(ReadingPositions.IsWorthRestoring(At(0, 0)));
        Assert.False(ReadingPositions.IsWorthRestoring(At(0, 0.005)));
    }

    [Fact]
    public void anywhere_else_is_worth_restoring()
    {
        Assert.True(ReadingPositions.IsWorthRestoring(At(180, 0)));
        Assert.True(ReadingPositions.IsWorthRestoring(At(0, 0.4)));
    }

    // ---------------- Forgetting ----------------

    [Fact]
    public void a_document_can_be_forgotten()
    {
        var map = ReadingPositions.Remember(null, @"C:\a.pdf", At(9), Now);
        map = ReadingPositions.Remember(map, @"C:\b.pdf", At(9), Now);

        map = ReadingPositions.Forget(map, @"c:\A.PDF");

        Assert.Null(ReadingPositions.For(map, @"C:\a.pdf"));
        Assert.NotNull(ReadingPositions.For(map, @"C:\b.pdf"));
    }

    [Fact]
    public void forgetting_something_unknown_is_harmless() =>
        Assert.Empty(ReadingPositions.Forget(null, @"C:\never-seen.pdf"));

    // ---------------- Storage ----------------

    [Fact]
    public void positions_survive_a_round_trip_through_the_settings_file()
    {
        var settings = new AppSettings
        {
            ReadingPositions = ReadingPositions.Remember(null, @"C:\Books\Atlas.pdf", At(180, 0.25, 1.5), Now),
        };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var position = ReadingPositions.For(reloaded.ReadingPositions, @"C:\Books\Atlas.pdf");

        Assert.NotNull(position);
        Assert.Equal(180, position!.Value.PageIndex);
        Assert.Equal(0.25, position.Value.PageFraction, 4);
        Assert.Equal(1.5, position.Value.Zoom, 4);
    }

    [Fact]
    public void a_settings_file_from_an_older_build_still_loads()
    {
        // The whole reason this is a separate property rather than a change to
        // the recent-files list: an older file simply has no such key, and must
        // load with an empty map rather than throwing.
        const string old = """{"Theme":2,"DefaultView":1,"ColorIntensity":0,"RecentLimit":10}""";

        var settings = JsonSerializer.Deserialize<AppSettings>(old)!;

        Assert.NotNull(settings.ReadingPositions);
        Assert.Empty(settings.ReadingPositions);
        Assert.Null(ReadingPositions.For(settings.ReadingPositions, @"C:\anything.pdf"));
    }
}
