using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Whether a URI out of a PDF may be handed to the operating system, and what
/// the user should be typing into the URL box.
///
/// ⚠️ A LINK IN A DOCUMENT IS UNTRUSTED INPUT. The file came from somewhere,
/// and its `/URI` can say anything: `javascript:`, `file:///C:/Windows/...`, or
/// a scheme some other installed program has registered a handler for. Handing
/// that straight to ShellExecute is how a PDF gets to run something. Only the
/// schemes a reader actually needs are allowed through, and everything else is
/// shown to the user and refused.
///
/// Pure, and in the viewport library rather than beside the launching code, so
/// the rule is checked by tests instead of by clicking links in strange files.
/// </summary>
public static class LinkTarget
{
    /// <summary>
    /// Whether this URI may be opened. Anything that is not plainly web or mail
    /// is refused, including a URI with no scheme at all.
    /// </summary>
    public static bool CanOpen(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) { return false; }

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        return parsed.Scheme is "http" or "https" or "mailto";
    }

    /// <summary>
    /// Why a URI was refused, for the dialog. Empty when it can be opened.
    /// </summary>
    public static string RefusalReason(string? uri)
    {
        if (CanOpen(uri)) { return string.Empty; }

        if (string.IsNullOrWhiteSpace(uri))
        {
            return "This link has no address.";
        }

        return Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri? parsed)
            ? $"Ayaan will not open a \"{parsed.Scheme}\" link. "
              + "Only web and mail addresses can be opened."
            : "This link's address is not one Ayaan can read.";
    }

    /// <summary>
    /// What to store for the URL a user typed, or null if it cannot be stored.
    ///
    /// A bare host gets <c>https://</c> in front of it, which is what every
    /// address box does and what a reader typing "example.com" means. Nothing
    /// else is guessed: a typed scheme is respected and then held to the same
    /// rule as a link out of a document, so the app cannot write a link it will
    /// afterwards refuse to open.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) { return null; }

        string text = typed.Trim();

        // No scheme, and not something that only looks like one because of a
        // colon further along ("example.com:8080/x" is a bare host).
        if (!text.Contains("://", StringComparison.Ordinal)
            && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        return CanOpen(text) ? text : null;
    }
}
