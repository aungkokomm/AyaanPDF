using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Where the recent list is kept: recent.json next to the exe, the same place
/// diag.log lives, because this app is portable and has nothing in the
/// registry or a roaming profile.
///
/// Every path here is best-effort. A recent list is a convenience, so a
/// read-only install folder or a half-written file must degrade to "no recents"
/// rather than stop the app opening.
/// </summary>
internal static class RecentFilesStore
{
    private static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "recent.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new List<string>();
            }

            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path))
                       ?? new List<string>();

            // Pruned on the way out, so a file deleted since it was last opened
            // never reaches the menu.
            return RecentFiles.Prune(list, File.Exists);
        }
        catch (Exception ex)
        {
            Diag.Log($"recent: could not read the list: {ex.Message}");
            return new List<string>();
        }
    }

    public static void Add(string path)
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(RecentFiles.Add(Load(), path)));
        }
        catch (Exception ex)
        {
            Diag.Log($"recent: could not write the list: {ex.Message}");
        }
    }
}
