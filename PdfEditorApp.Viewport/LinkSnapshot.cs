namespace PdfEditorApp.Viewport;

/// <summary>What a link does when it is followed. Mirrors the core's LINK_* codes.</summary>
public enum LinkKind
{
    /// <summary>Opens a URI. The only kind this build creates or retargets.</summary>
    Uri = 0,

    /// <summary>Jumps somewhere inside this document. Read and preserved; PDFium
    /// has no setter for a destination, so it is never written here.</summary>
    Internal = 1,

    /// <summary>A link annotation carrying neither: a launch action, an embedded
    /// file, or nothing at all. Shown so the reader can see it is there.</summary>
    Other = 2,
}

/// <summary>
/// ONE LINK on a page.
///
/// <paramref name="AnnotationIndex"/> is a position in the page's ANNOTATION
/// list, which is what every call that edits or deletes one takes. Deliberately
/// not a position in a list of links: PDFium's own link collection indexes the
/// annotation array anyway, and it was measured to over-report and to repeat
/// links on any page that mixes them with other marks.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
///
/// A link has NO APPEARANCE of its own. Measured in every real file: no /AP, a
/// zero-width /Border, nothing drawn. The rectangle is a hit area and making it
/// visible is the app's job.
/// </summary>
/// <param name="TargetPage">
/// Where an internal link goes, or -1. For display only; this build does not
/// create or retarget internal links.
/// </param>
public sealed record LinkSnapshot(
    int AnnotationIndex,
    LinkKind Kind,
    double Left,
    double Top,
    double Right,
    double Bottom,
    int TargetPage,
    string Uri)
{
    /// <summary>Whether the core would accept a retarget of this link.</summary>
    public bool CanEditUrl => Kind == LinkKind.Uri;

    /// <summary>What to show a reader for this link, in their terms.</summary>
    public string Describe => Kind switch
    {
        LinkKind.Uri => Uri,
        LinkKind.Internal when TargetPage >= 0 => $"Goes to page {TargetPage + 1}",
        LinkKind.Internal => "Goes somewhere in this document",
        _ => "This link's action is not one Ayaan understands.",
    };

    /// <summary>Whether a normalized page-local point falls inside this link.</summary>
    public bool Contains(double x, double y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;
}
