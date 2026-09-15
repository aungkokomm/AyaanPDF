using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// The offline dictionaries behind Define, each read once, on first use: the
/// English definitions and the Myanmar meanings.
/// </summary>
/// <remarks>
/// Off the UI thread, because each file holds tens of thousands of words and
/// reading one must not land on the click that asked for a definition. Lazy,
/// so a reader who never uses Define never pays for either. Null when a file
/// is missing or unreadable, which Define says or quietly does without,
/// rather than failing.
/// </remarks>
internal static class DefinitionDictionary
{
    public static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary", "wordnet-en.tsv.gz");

    public static readonly string MyanmarFilePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary", "akk-en-my.tsv.gz");

    public static readonly string HindiFilePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionary", "hindi-en-hi.tsv.gz");

    private static readonly Lazy<Task<HindiGlosses?>> LoadedHindi =
        new(() => Task.Run(() => Read(HindiFilePath, HindiGlosses.Load, g => $"{g.Count} Hindi word/part-of-speech entries")));

    public static Task<HindiGlosses?> LoadHindiAsync() => LoadedHindi.Value;

    private static readonly Lazy<Task<WordDefinitions?>> Loaded =
        new(() => Task.Run(() => Read(FilePath, WordDefinitions.Load, d => $"{d.WordCount} words")));

    private static readonly Lazy<Task<MyanmarGlosses?>> LoadedMyanmar =
        new(() => Task.Run(() => Read(MyanmarFilePath, MyanmarGlosses.Load, g => $"{g.Count} Myanmar word/part-of-speech entries")));

    public static Task<WordDefinitions?> LoadAsync() => Loaded.Value;

    public static Task<MyanmarGlosses?> LoadMyanmarAsync() => LoadedMyanmar.Value;

    private static T? Read<T>(string path, Func<TextReader, T> load, Func<T, string> describe)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                Diag.Log($"define: no dictionary at {path}");
                return null;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            T dictionary = load(reader);
            Diag.Log($"define: {describe(dictionary)} loaded in {clock.ElapsedMilliseconds} ms");
            return dictionary;
        }
        catch (Exception ex)
        {
            Diag.Log($"define: {Path.GetFileName(path)} could not be read: {ex.Message}");
            return null;
        }
    }
}
