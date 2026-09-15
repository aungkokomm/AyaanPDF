using System;
using System.IO;
using System.Text.Json;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The one-way trip an old settings.json takes, and everything it must not
/// disturb on the way.
///
/// WHY THIS EXISTS. UseSkiaShapeLayer was a candidate flag, defaulting to
/// false, and settings are serialised in FULL, so every machine that ran the
/// app in that period has an explicit false on disk. When the default flipped,
/// those machines kept the stored answer and stayed on the XAML overlay for
/// good. Nobody had chosen it: the flag has no UI and never had one. It was
/// invisible until the gradient live preview, which draws through the Skia
/// layer, could not appear on such a machine at all, and a full seven-boundary
/// trace was needed to find that the first line of the renderer was returning.
///
/// So these tests are about a stored value's MEANING changing, not about a
/// property being added. The two halves matter equally: an old file has to
/// move, and a current one has to be left alone.
/// </summary>
public class SettingsMigrationTests
{
    /// <summary>
    /// A real settings.json from before versioning, keys and all. Copied from
    /// the shape the app actually writes rather than reduced to the one
    /// property under test, so the migration is exercised against a file it
    /// will really meet.
    /// </summary>
    private const string BeforeVersioning = @"{
  ""Theme"": 1,
  ""DefaultView"": 1,
  ""ColorIntensity"": 0,
  ""ShowRulers"": true,
  ""RulerUnit"": ""Inches"",
  ""RecentLimit"": 10,
  ""StatusBarDock"": 0,
  ""SearchMatchCase"": false,
  ""SearchWholeWord"": false,
  ""RememberReadingPosition"": true,
  ""NightMode"": false,
  ""UseSkiaShapeLayer"": false,
  ""PageViewMode"": 0,
  ""ReadingPositions"": {
    ""C:\\DOCS\\A.PDF"": {
      ""PageIndex"": 2,
      ""PageFraction"": 0.25,
      ""Zoom"": 1.5,
      ""SavedAtTicks"": 639225694790970988,
      ""PageCount"": 3352
    }
  }
}";

    private static AppSettings Read(string json) =>
        (JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings()).Sanitised();

    // ---------------- an old file moves ----------------

    [Fact]
    public void a_file_written_before_versioning_reads_as_version_zero()
    {
        // The whole mechanism rests on this: no key means no version, and no
        // version means old. If the property ever gains a non-zero default,
        // every old file starts claiming to be current and nothing below
        // detects anything.
        Assert.Equal(0, Read(BeforeVersioning).SettingsVersion);
    }

    [Fact]
    public void an_old_stored_false_is_migrated_onto_the_skia_renderer()
    {
        var stored = Read(BeforeVersioning);
        Assert.False(stored.UseSkiaShapeLayer);

        var migrated = stored.Migrated();

        Assert.True(migrated.UseSkiaShapeLayer);
        Assert.Equal(AppSettings.CurrentSettingsVersion, migrated.SettingsVersion);
    }

    [Fact]
    public void the_migration_changes_nothing_else_about_an_old_file()
    {
        var stored = Read(BeforeVersioning);
        var migrated = stored.Migrated();

        // Everything the person actually chose survives, including the map that
        // is the bulk of the file.
        Assert.Equal(stored.Theme, migrated.Theme);
        Assert.Equal(stored.DefaultView, migrated.DefaultView);
        Assert.Equal(stored.ColorIntensity, migrated.ColorIntensity);
        Assert.Equal(stored.ShowRulers, migrated.ShowRulers);
        Assert.Equal(stored.RulerUnit, migrated.RulerUnit);
        Assert.Equal(stored.RecentLimit, migrated.RecentLimit);
        Assert.Equal(stored.SearchMatchCase, migrated.SearchMatchCase);
        Assert.Equal(stored.SearchWholeWord, migrated.SearchWholeWord);
        Assert.Equal(stored.RememberReadingPosition, migrated.RememberReadingPosition);
        Assert.Equal(stored.NightMode, migrated.NightMode);
        Assert.Equal(stored.PageViewMode, migrated.PageViewMode);

        Assert.Same(stored.ReadingPositions, migrated.ReadingPositions);
        Assert.Equal(2, migrated.ReadingPositions[@"C:\DOCS\A.PDF"].PageIndex);
    }

    [Fact]
    public void a_fresh_install_is_stamped_with_the_current_version()
    {
        // A default AppSettings is version zero too, so it takes the same trip.
        // Harmless, because the migration only ever moves it to a value it
        // already has, and it means one code path writes the stamp.
        var fresh = new AppSettings();
        Assert.Equal(0, fresh.SettingsVersion);

        var migrated = fresh.Migrated();

        Assert.True(migrated.UseSkiaShapeLayer);
        Assert.Equal(AppSettings.CurrentSettingsVersion, migrated.SettingsVersion);
    }

    // ---------------- a current file does not ----------------

    [Fact]
    public void a_stamped_file_is_returned_untouched()
    {
        // THE OTHER HALF, and the one a careless fix breaks: a false written
        // AFTER the migration is a real choice and has to survive. Without the
        // version check this test fails and the flag becomes unsettable.
        string json = Stamped(AppSettings.CurrentSettingsVersion, false);

        var stored = Read(json);
        Assert.Equal(AppSettings.CurrentSettingsVersion, stored.SettingsVersion);

        var migrated = stored.Migrated();

        Assert.False(migrated.UseSkiaShapeLayer);
        Assert.Same(stored.ReadingPositions, migrated.ReadingPositions);
        Assert.Equal(stored, migrated);
    }

    [Fact]
    public void a_stamped_true_is_also_left_alone()
    {
        var migrated = Read(Stamped(AppSettings.CurrentSettingsVersion, true)).Migrated();

        Assert.True(migrated.UseSkiaShapeLayer);
        Assert.Equal(AppSettings.CurrentSettingsVersion, migrated.SettingsVersion);
    }

    [Fact]
    public void a_file_from_a_newer_build_is_not_dragged_backwards()
    {
        var migrated = Read(Stamped(99, false)).Migrated();

        Assert.False(migrated.UseSkiaShapeLayer);
        Assert.Equal(99, migrated.SettingsVersion);
    }

    [Fact]
    public void migrating_twice_is_the_same_as_migrating_once()
    {
        var once = Read(BeforeVersioning).Migrated();
        var twice = once.Migrated();

        Assert.Equal(once, twice);
    }

    /// <summary>The same file, with a version stamp and a chosen flag.</summary>
    private static string Stamped(int version, bool skia) => BeforeVersioning.Replace(
        @"""UseSkiaShapeLayer"": false",
        $@"""SettingsVersion"": {version}, ""UseSkiaShapeLayer"": {(skia ? "true" : "false")}");

    // ---------------- the round trip ----------------

    [Fact]
    public void the_stamp_survives_being_written_and_read_again()
    {
        // The migration is only once-per-install if the stamp actually reaches
        // the file. Settings are serialised in full, so this is really asking
        // whether the new property is serialised at all.
        var migrated = Read(BeforeVersioning).Migrated();

        string written = JsonSerializer.Serialize(migrated);
        Assert.Contains(@"""SettingsVersion"":", written, StringComparison.Ordinal);

        var reread = Read(written);

        Assert.Equal(AppSettings.CurrentSettingsVersion, reread.SettingsVersion);
        Assert.True(reread.UseSkiaShapeLayer);
    }

    [Fact]
    public void a_deliberate_false_set_after_the_migration_survives_the_next_start()
    {
        // The escape hatch, end to end: migrate, write, someone edits the flag
        // back to false by hand, start again. The second start must not undo it.
        string afterMigration = JsonSerializer.Serialize(Read(BeforeVersioning).Migrated());
        string handEdited = afterMigration.Replace(
            @"""UseSkiaShapeLayer"":true", @"""UseSkiaShapeLayer"":false");

        Assert.NotEqual(afterMigration, handEdited);

        Assert.False(Read(handEdited).Migrated().UseSkiaShapeLayer);
    }

    [Fact]
    public void sanitising_does_not_lose_the_stamp()
    {
        // Load applies both, and an order or a copy that dropped the version
        // would make the migration run forever and rewrite the file on every
        // launch.
        var migrated = Read(BeforeVersioning).Migrated();

        Assert.Equal(AppSettings.CurrentSettingsVersion, migrated.Sanitised().SettingsVersion);
        Assert.True(migrated.Sanitised().UseSkiaShapeLayer);
    }

    // ---------------- the wiring ----------------
    //
    // SettingsStore lives in the WinUI project, which a net10.0 test assembly
    // cannot reference, so the only way to hold the call sites is to read them.

    private static string SettingsStoreSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "SettingsStore.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    [Fact]
    public void the_store_reads_through_the_migration()
    {
        string source = SettingsStoreSource();

        Assert.Contains("_cached ??= LoadAndMigrate()", source, StringComparison.Ordinal);
        Assert.Contains("stored.Migrated()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void the_store_writes_the_stamp_back_only_when_it_changed()
    {
        // Both halves of the guard. Writing unconditionally would rewrite
        // settings.json on every launch; not writing at all would migrate on
        // every launch and let any later save put an un-stamped file back.
        string source = SettingsStoreSource();

        Assert.Contains(
            "if (migrated.SettingsVersion == stored.SettingsVersion)", source, StringComparison.Ordinal);
        Assert.Contains("Save(migrated);", source, StringComparison.Ordinal);
    }
}
