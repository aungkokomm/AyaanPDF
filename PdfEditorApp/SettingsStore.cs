using System;
using System.IO;
using System.Text.Json;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Where the app's own preferences live: settings.json, in the same per-user
/// folder as stamps and signatures.
///
/// Per-user rather than beside the exe, for the reason StampLibrary records
/// the hard way: anything kept in the install folder is lost on reinstall and
/// looks exactly like the app threw it away. A Settings file beside the exe
/// still wins if one exists, so a copy on a stick stays self-contained.
///
/// Held in memory after the first read. Settings are consulted on every
/// document open and every dialog, and re-reading a file for that is work for
/// nothing.
/// </summary>
internal static class SettingsStore
{
    private static AppSettings? _cached;

    private static string PortablePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppInfo.Name,
        "settings.json");

    private static string FilePath => File.Exists(PortablePath) ? PortablePath : UserPath;

    public static AppSettings Current => _cached ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var read = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                // Sanitised on the way IN, so a hand-edited or newer file can
                // never put the app in a state its menus cannot show.
                return (read ?? new AppSettings()).Sanitised();
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"settings: could not read, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>
    /// Stores a change and writes it out. Failure is logged, not thrown: losing
    /// a preference is not worth interrupting what the user was doing.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        _cached = settings.Sanitised();
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written beside and moved into place, so an interrupted write
            // leaves the previous settings rather than a truncated file that
            // reads as "no settings at all".
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cached, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Log($"settings: could not write: {ex.Message}");
        }
    }

    /// <summary>Applies one change to the stored settings.</summary>
    public static void Update(Func<AppSettings, AppSettings> change) => Save(change(Current));
}
