using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>A release newer than the running build.</summary>
internal sealed record UpdateInfo(string Version, string ReleaseUrl);

/// <summary>
/// Asks GitHub once whether a newer Ayaan PDF has been released.
/// </summary>
/// <remarks>
/// ⚠️ IT ONLY ASKS, AND IT SENDS NOTHING ABOUT THE READER. One request for the
/// public "latest release" of the repository, the same page anyone can open in
/// a browser. Nothing is downloaded or installed: the bubble it leads to opens
/// the release page, and the reader decides.
///
/// ⚠️ AND EVERY FAILURE IS SILENCE. No network, a proxy, GitHub's rate limit or
/// a reply it cannot read all mean "nothing to say", never an error in front
/// of someone who opened the app to read a PDF.
/// </remarks>
internal static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/aungkokomm/AyaanPDF/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// The version this build compares itself as. PDFEDITOR_PRETEND_VERSION
    /// overrides it, the way PDFEDITOR_AUTOOPEN drives a load, so the bubble
    /// can be seen against the real latest release from a build that is not
    /// older than it.
    /// </summary>
    private static string Current =>
        Environment.GetEnvironmentVariable("PDFEDITOR_PRETEND_VERSION") is { Length: > 0 } pretend
            ? pretend
            : AppInfo.Version;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub refuses a request with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AyaanPDF", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// The newer release, or null when this build is the latest, when that
    /// release is the one the reader chose to skip, or when GitHub could not
    /// be asked.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string? skippedVersion)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Log($"update: GitHub answered {(int)response.StatusCode}");
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            string? tag = json.RootElement.GetProperty("tag_name").GetString();
            string? page = json.RootElement.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(page))
            {
                return null;
            }

            string latest = UpdateVersions.Normalize(tag);
            if (!UpdateVersions.IsNewer(latest, Current))
            {
                Diag.Log($"update: {Current} is up to date (latest {latest})");
                return null;
            }
            if (string.Equals(latest, skippedVersion, StringComparison.OrdinalIgnoreCase))
            {
                Diag.Log($"update: {latest} is out, and the reader chose to skip it");
                return null;
            }
            Diag.Log($"update: {latest} is out, this is {Current}");
            return new UpdateInfo(latest, page);
        }
        catch (Exception ex)
        {
            Diag.Log($"update: could not check ({ex.GetType().Name})");
            return null;
        }
    }
}
