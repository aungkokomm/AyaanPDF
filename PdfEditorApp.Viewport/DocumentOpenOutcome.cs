namespace PdfEditorApp.Viewport;

/// <summary>
/// How an attempt to open a document ended.
///
/// Three answers rather than a bool, because the middle one is not a failure:
/// an encrypted document is a perfectly good PDF that is waiting for something
/// the user can supply. Treating it as broken, which is what a bare zero handle
/// forced, is why every protected PDF in the world used to report "Failed to
/// open" and leave the reader with nowhere to go.
/// </summary>
public enum DocumentOpenOutcome
{
    Opened,

    /// <summary>
    /// Encrypted, and the password given (if any) did not open it. Reported the
    /// same way for a first attempt with no password and for a wrong one:
    /// PDFium does not distinguish them, and the prompt asks again either way.
    /// </summary>
    NeedsPassword,

    /// <summary>Missing, not a PDF, or damaged beyond reading.</summary>
    Failed,
}
