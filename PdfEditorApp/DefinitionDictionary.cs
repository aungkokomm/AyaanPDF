using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// The offline English dictionary behind Define, read once, on first use.
/// </summary>
/// <remarks>
/// Off the UI thread, because the file holds well over a hundred thousand
/// words and reading it must not land on the click that asked for a
/// definition. Lazy, so a reader who never uses Define never pays for it.
/// Null when the file is missing or unreadable, which Define says rather than
/// failing.
/// </remarks>
internal static class DefinitionDictionary
{
    public static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary", "wordnet-en.tsv.gz");

    private static readonly Lazy<Task<WordDefinitions?>> Loaded = new(() => Task.Run(Load));

    public static Task<WordDefinitions?> LoadAsync() => Loaded.Value;

    private static WordDefinitions? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Diag.Log($"define: no dictionary at {FilePath}");
                return null;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var file = File.OpenRead(FilePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var dictionary = WordDefinitions.Load(reader);
            Diag.Log($"define: {dictionary.WordCount} words loaded in {clock.ElapsedMilliseconds} ms");
            return dictionary;
        }
        catch (Exception ex)
        {
            Diag.Log($"define: the dictionary could not be read: {ex.Message}");
            return null;
        }
    }
}
