using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Ocr;

/// <summary>
/// The Recognize text languages downloaded on this computer, and the queue
/// that downloads more.
/// </summary>
/// <remarks>
/// App-wide, and outliving the window that asks: a download carries on when
/// the languages window closes, and Recognize text sees a new language the
/// moment it lands. Everything here runs on the UI thread; only the download
/// itself awaits off it. One download at a time, the rest waiting, so a slow
/// connection is not split between them.
/// </remarks>
internal static class OcrLanguageStore
{
    /// <summary>In the user's own folder, so updating or reinstalling Ayaan PDF keeps them.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "OCR", "tessdata");

    public enum State
    {
        Available,
        Waiting,
        Downloading,
        Installed,
    }

    /// <summary>A language was queued, started, finished, failed, cancelled or removed.</summary>
    public static event Action? Changed;

    /// <summary>Bytes received so far for the language downloading.</summary>
    public static event Action<string, long>? Progress;

    private static readonly List<OcrLanguage> Waiting = new();
    private static readonly Dictionary<string, string> Problems = new();
    private static OcrLanguage? _downloading;
    private static long _done;
    private static CancellationTokenSource? _cancel;

    // The whole request may take this long to answer; the bytes after that
    // are watched by the downloader's own stall timer.
    private static readonly OcrLanguageDownloader Downloader =
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

    public static string PathOf(string code) => Path.Combine(Folder, code + ".traineddata");

    /// <summary>Downloaded and whole: there, and exactly the size the list names.</summary>
    public static bool IsInstalled(string code) =>
        OcrLanguageCatalog.Find(code) is { } language
        && new FileInfo(PathOf(code)) is { Exists: true } file
        && file.Length == language.Size;

    public static List<OcrLanguage> Installed => OcrLanguageList.All.Where(l => IsInstalled(l.Code)).ToList();

    public static State StateOf(string code) =>
        _downloading?.Code == code ? State.Downloading
        : Waiting.Any(w => w.Code == code) ? State.Waiting
        : IsInstalled(code) ? State.Installed
        : State.Available;

    public static long DoneOf(string code) => _downloading?.Code == code ? _done : 0;

    /// <summary>Why the last attempt at this language failed, until it is tried again.</summary>
    public static string? ProblemOf(string code) => Problems.GetValueOrDefault(code);

    public static void Download(OcrLanguage language)
    {
        if (StateOf(language.Code) != State.Available)
        {
            return;
        }
        Problems.Remove(language.Code);
        Waiting.Add(language);
        Changed?.Invoke();
        if (_downloading is null)
        {
            _ = RunAsync();
        }
    }

    public static void Cancel(string code)
    {
        if (_downloading?.Code == code)
        {
            _cancel?.Cancel();
        }
        else if (Waiting.RemoveAll(w => w.Code == code) > 0)
        {
            Changed?.Invoke();
        }
    }

    public static void Remove(string code)
    {
        if (StateOf(code) != State.Installed)
        {
            return;
        }
        try
        {
            File.Delete(PathOf(code));
            Problems.Remove(code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Most likely in use by a run reading it right now.
            Problems[code] = "Couldn't remove it while Recognize text is using it. Try again when it finishes.";
        }
        Changed?.Invoke();
    }

    private static async Task RunAsync()
    {
        while (Waiting.Count > 0)
        {
            var language = Waiting[0];
            Waiting.RemoveAt(0);
            _downloading = language;
            _done = 0;
            _cancel = new CancellationTokenSource();
            Changed?.Invoke();

            var progress = new Progress<long>(done =>
            {
                if (_downloading?.Code == language.Code)
                {
                    _done = done;
                    Progress?.Invoke(language.Code, done);
                }
            });

            try
            {
                string? problem = await Downloader.DownloadAsync(language, Folder, progress, _cancel.Token);
                if (problem is not null)
                {
                    Problems[language.Code] = problem;
                }
                Diag.Log($"ocr language {language.Code}: {problem ?? "installed"}");
            }
            catch (OperationCanceledException)
            {
                Diag.Log($"ocr language {language.Code}: cancelled");
            }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                _downloading = null;
                Changed?.Invoke();
            }
        }
    }
}
