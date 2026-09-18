using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>A Tesseract language Recognize text can download.</summary>
/// <param name="Code">Tesseract's name for it, and its file's: "ben" is ben.traineddata.</param>
/// <param name="Native">Its name in itself, or empty for a historic form.</param>
/// <param name="Script">Its script, which decides the font its words are written in.</param>
/// <param name="Tags">The BCP 47 language tags it answers to, "|"-separated; empty for a historic form.</param>
/// <param name="Size">The file's exact size in bytes.</param>
/// <param name="GitSha1">The file's git blob id, which the download is checked against.</param>
public sealed record OcrLanguage(string Code, string Name, string Native, string Script, string Tags, long Size, string GitSha1);

/// <summary>
/// Which font a script's recognised words are written in. Each pairing is
/// proved in render_core's every_script_reads_back_through_the_font_chosen_for_it:
/// words written in that font come back as the characters that went in, which
/// is what makes them searchable.
/// </summary>
public static class OcrScripts
{
    private static readonly Dictionary<string, string[]> FontsByScript = new()
    {
        ["Myanmar"] = ["mmrtext.ttf", "Pyidaungsu.ttf"],
        ["Devanagari"] = ["Nirmala.ttf", "mangal.ttf"],
        ["Bengali"] = ["Nirmala.ttf"],
        ["Gurmukhi"] = ["Nirmala.ttf"],
        ["Gujarati"] = ["Nirmala.ttf"],
        ["Oriya"] = ["Nirmala.ttf"],
        ["Tamil"] = ["Nirmala.ttf"],
        ["Telugu"] = ["Nirmala.ttf"],
        ["Kannada"] = ["Nirmala.ttf"],
        ["Malayalam"] = ["Nirmala.ttf"],
        ["Sinhala"] = ["Nirmala.ttf"],
        ["Thai"] = ["LeelawUI.ttf"],
        ["Lao"] = ["LeelawUI.ttf"],
        ["Khmer"] = ["LeelawUI.ttf"],
        ["Tibetan"] = ["himalaya.ttf"],
        ["Georgian"] = ["sylfaen.ttf"],
        ["Armenian"] = ["sylfaen.ttf"],
        ["Ethiopic"] = ["ebrima.ttf"],
        ["Cherokee"] = ["gadugi.ttf"],
        ["CanadianSyllabics"] = ["gadugi.ttf"],
        ["Thaana"] = ["mvboli.ttf"],
        ["Syriac"] = ["seguihis.ttf"],
        ["Arabic"] = ["arial.ttf"],
        ["Hebrew"] = ["arial.ttf"],
        ["Cyrillic"] = ["arial.ttf"],
        ["Greek"] = ["arial.ttf"],
        ["Latin"] = ["arial.ttf"],
    };

    /// <summary>The fonts that can carry a script, best first.</summary>
    public static IReadOnlyList<string> FontsFor(string script) =>
        FontsByScript.TryGetValue(script, out var fonts) ? fonts : ["arial.ttf"];

    /// <summary>
    /// The script a page's recognised text needs a font for, or null when
    /// every character is within Latin-1, which Helvetica carries without
    /// embedding anything. The first letter outside Latin decides: a page's
    /// words are one script with English mixed in, and each of these fonts
    /// has the Latin letters too.
    /// </summary>
    public static string? ScriptOf(string text)
    {
        bool beyondLatin1 = false;
        foreach (char c in text)
        {
            if (Classify(c) is { } script)
            {
                return script;
            }
            beyondLatin1 |= c > '\u00FF';
        }
        return beyondLatin1 ? "Latin" : null;
    }

    private static string? Classify(char c) => c switch
    {
        >= '\u1000' and <= '\u109F' or >= '\uAA60' and <= '\uAA7F' or >= '\uA9E0' and <= '\uA9FF' => "Myanmar",
        >= '\u0900' and <= '\u097F' or >= '\uA8E0' and <= '\uA8FF' => "Devanagari",
        >= '\u0980' and <= '\u09FF' => "Bengali",
        >= '\u0A00' and <= '\u0A7F' => "Gurmukhi",
        >= '\u0A80' and <= '\u0AFF' => "Gujarati",
        >= '\u0B00' and <= '\u0B7F' => "Oriya",
        >= '\u0B80' and <= '\u0BFF' => "Tamil",
        >= '\u0C00' and <= '\u0C7F' => "Telugu",
        >= '\u0C80' and <= '\u0CFF' => "Kannada",
        >= '\u0D00' and <= '\u0D7F' => "Malayalam",
        >= '\u0D80' and <= '\u0DFF' => "Sinhala",
        >= '\u0E00' and <= '\u0E7F' => "Thai",
        >= '\u0E80' and <= '\u0EFF' => "Lao",
        >= '\u1780' and <= '\u17FF' => "Khmer",
        >= '\u0F00' and <= '\u0FFF' => "Tibetan",
        >= '\u10A0' and <= '\u10FF' or >= '\u1C90' and <= '\u1CBF' or >= '\u2D00' and <= '\u2D2F' => "Georgian",
        >= '\u0530' and <= '\u058F' => "Armenian",
        >= '\u1200' and <= '\u139F' or >= '\u2D80' and <= '\u2DDF' => "Ethiopic",
        >= '\u13A0' and <= '\u13FF' => "Cherokee",
        >= '\u1400' and <= '\u167F' => "CanadianSyllabics",
        >= '\u0780' and <= '\u07BF' => "Thaana",
        >= '\u0700' and <= '\u074F' => "Syriac",
        >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u08FF' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFE' => "Arabic",
        >= '\u0590' and <= '\u05FF' or >= '\uFB1D' and <= '\uFB4F' => "Hebrew",
        _ => null,
    };
}

/// <summary>Finding, naming and suggesting the downloadable languages.</summary>
public static class OcrLanguageCatalog
{
    public static OcrLanguage? Find(string code) =>
        OcrLanguageList.All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The English name of any language Recognize text knows, shipped or downloadable.</summary>
    public static string NameOf(string code) =>
        OcrPlan.Bundled.FirstOrDefault(b => b.Code == code).Name ?? Find(code)?.Name ?? code;

    /// <summary>
    /// The languages a search finds, by English name, native name or code,
    /// ignoring case and accents: "tamil", "தமிழ்", "urd" and "espanol" all find.
    /// </summary>
    public static IEnumerable<OcrLanguage> Search(string? query)
    {
        string wanted = Fold(query ?? string.Empty);
        return wanted.Length == 0
            ? OcrLanguageList.All
            : OcrLanguageList.All.Where(l =>
                Fold(l.Name).Contains(wanted, StringComparison.Ordinal)
                || Fold(l.Native).Contains(wanted, StringComparison.Ordinal)
                || l.Code.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Languages worth offering first: the ones Windows is set up for, and the
    /// ones written in the script the open document's text is in. Historic
    /// forms and anything already installed are left out.
    /// </summary>
    public static List<OcrLanguage> Suggested(IEnumerable<string> windowsLanguageTags, int documentScript, Func<string, bool> installed)
    {
        var primaries = windowsLanguageTags
            .Select(t => t.Split('-', '_')[0].ToLowerInvariant())
            .ToHashSet();
        string? script = documentScript switch
        {
            1 => "Devanagari",
            3 => "Bengali",
            4 => "Tamil",
            5 => "Thai",
            6 => "Arabic",
            _ => null,
        };
        return OcrLanguageList.All
            .Where(l => l.Tags.Length > 0 && !installed(l.Code))
            .Where(l => l.Tags.Split('|').Any(primaries.Contains) || l.Script == script)
            .ToList();
    }

    /// <summary>How a size reads: "850 KB", "1.2 MB".</summary>
    public static string SizeText(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark || !IsLatinMark(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
    }

    // Only Latin accents are folded away. An Indic or Burmese vowel sign is a
    // letter of the word, and dropping it would find the wrong language.
    private static bool IsLatinMark(char c) => c is >= '\u0300' and <= '\u036F';
}

/// <summary>
/// Downloads one language's file and keeps it only if it is exactly the
/// file the list names: its size, and its git blob id (the SHA-1 of
/// "blob &lt;size&gt;" NUL and the bytes, which is how git names the file at
/// the pinned commit). Written to a ".part" file first, moved into place
/// only when checked, and removed on any failure, so a half download never
/// looks installed.
/// </summary>
public sealed class OcrLanguageDownloader
{
    private readonly HttpClient _http;
    private readonly string _source;
    private readonly TimeSpan _stall;

    /// <param name="stall">How long a download may go without a byte before it is given up.</param>
    public OcrLanguageDownloader(HttpClient http, string? source = null, TimeSpan? stall = null)
    {
        _http = http;
        _source = source ?? OcrLanguageList.Source;
        _stall = stall ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Null when the language is in <paramref name="folder"/>, checked, or what
    /// went wrong in words a reader can act on. Throws only when cancelled.
    /// </summary>
    public async Task<string?> DownloadAsync(OcrLanguage language, string folder, IProgress<long>? progress, CancellationToken token)
    {
        string target = Path.Combine(folder, language.Code + ".traineddata");
        string part = target + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            Directory.CreateDirectory(folder);
            stall.CancelAfter(_stall);
            using var response = await _http.GetAsync(_source + language.Code + ".traineddata", HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
            {
                return $"Couldn't download. The server answered {(int)response.StatusCode}.";
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes($"blob {language.Size}\0"));
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    stall.CancelAfter(_stall);
                    int read = await input.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    done += read;
                    if (done > language.Size)
                    {
                        return "The download wasn't the file expected, so it wasn't kept.";
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report(done);
                }
            }

            if (done != language.Size)
            {
                return "The download stopped part way. Try again.";
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), language.GitSha1, StringComparison.OrdinalIgnoreCase))
            {
                return "The download wasn't the file expected, so it wasn't kept.";
            }

            File.Move(part, target, overwrite: true);
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return "The download stopped part way. Try again.";
        }
        catch (HttpRequestException)
        {
            return "Couldn't download. Check the internet connection.";
        }
        catch (IOException ex)
        {
            return $"Couldn't save the language: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Couldn't save the language: {ex.Message}";
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
