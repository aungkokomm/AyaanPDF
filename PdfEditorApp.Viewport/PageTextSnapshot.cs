namespace PdfEditorApp.Viewport;

/// <summary>
/// One TEXT OBJECT out of a page's content stream, in the terms the document
/// model builder needs.
///
/// NOT one of ours. Our text boxes live in an annotation's appearance stream
/// and reach the model through <see cref="AnnotationSnapshot"/>; this is text
/// the document already had when it was opened, authored by whatever made it.
///
/// <paramref name="ObjectIndex"/> is the object's real position in the page's
/// content, which is what <c>FPDFPage_GetObject</c> takes. It is emphatically
/// NOT an annotation index: nothing that edits an annotation may be handed one
/// of these.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </summary>
/// <param name="FontSizePts">
/// The size the object is set at before its own matrix, in points. What a
/// reader would call the font size, and the number an edit has to preserve.
/// </param>
/// <param name="IsFontEmbedded">
/// Whether the font travels with the document. The one bit that decides whether
/// the characters available are the document's own or a substitute the reader
/// chose, which is the difference between an edit that looks right everywhere
/// and one that does not.
/// </param>
public readonly record struct PageTextSnapshot(
    int ObjectIndex,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double FontSizePts,
    uint ColorRgb,
    bool IsFontEmbedded,
    string FontName,
    string Text);
