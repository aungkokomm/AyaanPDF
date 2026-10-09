namespace PdfEditorApp.Viewport;

/// <summary>
/// The project's own pages that the Help menu opens, in the reader's browser.
/// </summary>
public static class HelpLinks
{
    public const string Repository = "https://github.com/aungkokomm/AyaanPDF";

    /// <summary>Every release, newest first, each with its notes.</summary>
    public const string WhatsNew = Repository + "/releases";

    /// <summary>A new issue, for a problem or a wish.</summary>
    public const string ReportProblem = Repository + "/issues/new";
}
