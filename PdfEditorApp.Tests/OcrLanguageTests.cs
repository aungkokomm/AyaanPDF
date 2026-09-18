using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Recognition languages: the list of languages that can be downloaded, how
/// they are found and suggested, and the download itself, which keeps a file
/// only when it is exactly the one the list names.
/// </summary>
/// <remarks>
/// The downloads here are served by a fake server; nothing is fetched from
/// the internet. That the list's ids are git's own names for the files is
/// shown by the "hello" blob, whose id git has always given it.
/// </remarks>
public class OcrLanguageTests
{
    // ---------------- The list ----------------

    [Fact]
    public void the_list_offers_each_language_once_and_none_that_ship_or_cannot_be_written()
    {
        var codes = OcrLanguageList.All.Select(l => l.Code).ToList();
        Assert.Equal(112, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());

        // The shipped three are offered by Recognize text itself.
        Assert.DoesNotContain("eng", codes);
        Assert.DoesNotContain("hin", codes);
        Assert.DoesNotContain("mya", codes);

        // Chinese, Japanese and Korean have no single-file Windows font the
        // text layer could embed, so their words would not be searchable.
        Assert.DoesNotContain(codes, c => c.StartsWith("chi_") || c.StartsWith("jpn") || c.StartsWith("kor"));
    }

    [Fact]
    public void every_language_names_a_file_the_download_can_be_checked_against()
    {
        Assert.StartsWith("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/" + OcrLanguageList.Commit + "/", OcrLanguageList.Source);
        foreach (var language in OcrLanguageList.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(language.Name), language.Code);
            Assert.True(language.Size > 0, language.Code);
            Assert.Equal(40, language.GitSha1.Length);
            Assert.True(language.GitSha1.All("0123456789abcdef".Contains), language.Code);
        }
    }

    [Fact]
    public void the_list_is_in_order_of_english_name()
    {
        var names = OcrLanguageList.All.Select(l => l.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void every_language_is_written_in_a_script_the_text_layer_has_a_font_for()
    {
        string[] arialCarries = ["Latin", "Cyrillic", "Greek", "Arabic", "Hebrew"];
        foreach (var language in OcrLanguageList.All.Where(l => !arialCarries.Contains(l.Script)))
        {
            // A misspelt script would fall back to Arial, which has none of it.
            Assert.NotEqual("arial.ttf", OcrScripts.FontsFor(language.Script)[0]);
        }
    }

    [Fact]
    public void each_native_name_is_in_the_script_the_list_gives_its_language()
    {
        string[] arialCarries = ["Latin", "Cyrillic", "Greek"];
        foreach (var language in OcrLanguageList.All.Where(l => l.Native.Length > 0))
        {
            string? found = OcrScripts.ScriptOf(language.Native);
            if (arialCarries.Contains(language.Script))
            {
                Assert.True(found is null or "Latin", $"{language.Code}: {found}");
            }
            else
            {
                Assert.Equal(language.Script, found);
            }
        }
    }

    // ---------------- Finding and suggesting ----------------

    [Fact]
    public void a_search_finds_a_language_by_english_name_native_name_or_code()
    {
        Assert.Contains(OcrLanguageCatalog.Search("tamil"), l => l.Code == "tam");
        Assert.Equal("tam", OcrLanguageCatalog.Search("தமிழ்").Single().Code);
        Assert.Contains(OcrLanguageCatalog.Search("urd"), l => l.Code == "urd");
        Assert.Contains(OcrLanguageCatalog.Search("espanol"), l => l.Code == "spa");
        Assert.Contains(OcrLanguageCatalog.Search(" ESPAÑOL "), l => l.Code == "spa");

        Assert.Equal(OcrLanguageList.All.Length, OcrLanguageCatalog.Search("").Count());
        Assert.Equal(OcrLanguageList.All.Length, OcrLanguageCatalog.Search(null).Count());
        Assert.Empty(OcrLanguageCatalog.Search("klingon"));
    }

    [Fact]
    public void a_search_keeps_the_vowel_signs_of_indic_names()
    {
        // संस्कृतम् without its signs is a different word, not Sanskrit.
        Assert.Contains(OcrLanguageCatalog.Search("संस्कृतम्"), l => l.Code == "san");
        Assert.Empty(OcrLanguageCatalog.Search("ससकतम"));
    }

    [Fact]
    public void suggestions_come_from_the_windows_languages_and_the_documents_script()
    {
        var fromWindows = OcrLanguageCatalog.Suggested(["ta-IN", "en-US"], documentScript: 0, installed: _ => false);
        Assert.Equal(["tam"], fromWindows.Select(l => l.Code));

        // 3 is a Bengali document: both languages written in it.
        var fromDocument = OcrLanguageCatalog.Suggested(["en-US"], documentScript: 3, installed: _ => false);
        Assert.Equal(["asm", "ben"], fromDocument.Select(l => l.Code));

        var notInstalled = OcrLanguageCatalog.Suggested(["en-US"], documentScript: 3, installed: c => c == "ben");
        Assert.Equal(["asm"], notInstalled.Select(l => l.Code));

        // Historic forms are never suggested: Georgian brings Georgian, not Old Georgian.
        var georgian = OcrLanguageCatalog.Suggested(["ka-GE"], documentScript: 0, installed: _ => false);
        Assert.Equal(["kat"], georgian.Select(l => l.Code));
    }

    [Fact]
    public void sizes_read_in_kilobytes_or_megabytes()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("1 KB", OcrLanguageCatalog.SizeText(100));
            Assert.Equal("850 KB", OcrLanguageCatalog.SizeText(850 * 1024));
            Assert.Equal("1.2 MB", OcrLanguageCatalog.SizeText(1_258_291));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void the_first_letter_beyond_latin_decides_the_script()
    {
        Assert.Null(OcrScripts.ScriptOf("Hello, world"));
        Assert.Null(OcrScripts.ScriptOf("café naïve"));
        Assert.Equal("Tamil", OcrScripts.ScriptOf("page 3 தமிழ் text"));
        Assert.Equal("Georgian", OcrScripts.ScriptOf("Tbilisi თბილისი"));
        Assert.Equal("Hebrew", OcrScripts.ScriptOf("שלום"));
        Assert.Equal("Arabic", OcrScripts.ScriptOf("اردو"));

        // Beyond Latin-1 but carried by Arial: curly quotes, Cyrillic.
        Assert.Equal("Latin", OcrScripts.ScriptOf("“quoted”"));
        Assert.Equal("Latin", OcrScripts.ScriptOf("Москва"));
    }

    // ---------------- Downloading ----------------

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Yield();
            Asked = request.RequestUri;
            return answer(request);
        }
    }

    /// <summary>Sends its bytes, then nothing more until cancelled: a connection gone quiet.</summary>
    private sealed class QuietStream(byte[] first) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Callback(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static HttpResponseMessage Serve(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Serve(Stream stream) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    /// <summary>What git names a file's content: the SHA-1 of "blob", its size, a NUL, and the bytes.</summary>
    private static string GitBlobId(byte[] bytes) => Convert.ToHexString(SHA1.HashData(
        Encoding.ASCII.GetBytes("blob " + bytes.Length).Append((byte)0).Concat(bytes).ToArray())).ToLowerInvariant();

    private static byte[] SomeBytes(int count)
    {
        var bytes = new byte[count];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    private static OcrLanguage LanguageFor(byte[] bytes) =>
        new("tst", "Test", "Test", "Latin", "xx", bytes.Length, GitBlobId(bytes));

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ayaan-ocr-languages-" + Guid.NewGuid().ToString("N"));

        public string[] Files => Directory.Exists(Path)
            ? Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)!).ToArray()
            : [];

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [Fact]
    public async Task a_download_is_checked_against_the_id_git_gives_the_file()
    {
        // git hash-object on a file holding "hello" and a newline.
        byte[] hello = Encoding.ASCII.GetBytes("hello").Append((byte)10).ToArray();
        var language = new OcrLanguage("tst", "Test", "Test", "Latin", "xx", hello.Length, "ce013625030ba8dba906f756967f9e9ca394464a");
        Assert.Equal(language.GitSha1, GitBlobId(hello));

        using var folder = new Folder();
        var server = new FakeServer(_ => Serve(hello));
        var downloader = new OcrLanguageDownloader(new HttpClient(server), "https://example.test/tessdata/");

        Assert.Null(await downloader.DownloadAsync(language, folder.Path, null, CancellationToken.None));
        Assert.Equal(new Uri("https://example.test/tessdata/tst.traineddata"), server.Asked);
        Assert.Equal(hello, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "tst.traineddata")));
    }

    [Fact]
    public async Task a_whole_download_is_kept_with_its_progress_reported_to_the_last_byte()
    {
        byte[] bytes = SomeBytes(300_000);
        using var folder = new Folder();
        var downloader = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => Serve(bytes))), "https://example.test/");

        long last = 0;
        string? problem = await downloader.DownloadAsync(LanguageFor(bytes), folder.Path, new Callback(done => last = done), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(bytes.Length, last);
        Assert.Equal(["tst.traineddata"], folder.Files);
        Assert.Equal(bytes, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "tst.traineddata")));
    }

    [Fact]
    public async Task a_download_that_is_not_the_file_named_is_not_kept()
    {
        byte[] bytes = SomeBytes(100_000);
        byte[] changed = bytes.ToArray();
        changed[50_000] ^= 1;

        using var folder = new Folder();
        var downloader = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => Serve(changed))), "https://example.test/");

        string? problem = await downloader.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None);

        Assert.Contains("wasn't the file expected", problem);
        Assert.Empty(folder.Files);
    }

    [Fact]
    public async Task a_download_too_short_or_too_long_is_not_kept()
    {
        byte[] bytes = SomeBytes(100_000);
        using var folder = new Folder();

        var shorter = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => Serve(bytes[..90_000]))), "https://example.test/");
        Assert.Contains("stopped part way", await shorter.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None));
        Assert.Empty(folder.Files);

        var longer = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => Serve(bytes.Concat(bytes[..10]).ToArray()))), "https://example.test/");
        Assert.Contains("wasn't the file expected", await longer.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None));
        Assert.Empty(folder.Files);
    }

    [Fact]
    public async Task a_server_or_network_failure_is_said_in_words_and_leaves_nothing()
    {
        byte[] bytes = SomeBytes(1_000);
        using var folder = new Folder();

        var missing = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), "https://example.test/");
        Assert.Equal("Couldn't download. The server answered 404.", await missing.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None));

        var offline = new OcrLanguageDownloader(new HttpClient(new FakeServer(_ => throw new HttpRequestException("no route"))), "https://example.test/");
        Assert.Equal("Couldn't download. Check the internet connection.", await offline.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None));

        Assert.Empty(folder.Files);
    }

    [Fact]
    public async Task a_connection_gone_quiet_is_given_up_and_leaves_nothing()
    {
        byte[] bytes = SomeBytes(100_000);
        using var folder = new Folder();
        var downloader = new OcrLanguageDownloader(
            new HttpClient(new FakeServer(_ => Serve(new QuietStream(bytes[..1_000])))), "https://example.test/", TimeSpan.FromMilliseconds(200));

        string? problem = await downloader.DownloadAsync(LanguageFor(bytes), folder.Path, null, CancellationToken.None);

        Assert.Equal("The download stopped part way. Try again.", problem);
        Assert.Empty(folder.Files);
    }

    [Fact]
    public async Task cancelling_a_download_stops_it_and_leaves_nothing()
    {
        byte[] bytes = SomeBytes(100_000);
        using var folder = new Folder();
        using var cancel = new CancellationTokenSource();
        var downloader = new OcrLanguageDownloader(
            new HttpClient(new FakeServer(_ => Serve(new QuietStream(bytes[..1_000])))), "https://example.test/", TimeSpan.FromMinutes(5));

        // Cancelled once the first bytes are written, as Cancel would be mid-download.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(LanguageFor(bytes), folder.Path, new Callback(_ => cancel.Cancel()), cancel.Token));

        Assert.Empty(folder.Files);
    }
}
