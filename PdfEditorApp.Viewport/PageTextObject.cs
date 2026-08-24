namespace PdfEditorApp.Viewport;

/// <summary>
/// A piece of the document's OWN text, as an object in the model.
///
/// The first thing in the model that is not an annotation. Everything else here
/// came from the page's annotation list, which is a list this app writes; this
/// came from the page's content stream, which it did not.
///
/// WHY IT IS IN THE MODEL AT ALL, given nothing edits it yet: because the model
/// is what answers "what is under this point", and until now it could only
/// answer for marks the app had made. A reader clicking a word in their own
/// document got nothing, and no amount of work on the editing end would change
/// that while the thing being clicked was invisible to the thing doing the
/// picking.
///
/// ⚠️ <see cref="ObjectIndex"/> IS NOT AN ANNOTATION INDEX. Every other object
/// in this model carries <see cref="DocumentObject.ZOrder"/>, which the app
/// uses as a position in the page's ANNOTATION list, and hands to calls that
/// move, restyle and delete annotations. Handing one of those an object index
/// would edit an unrelated annotation, or a nonexistent one. That is the reason
/// <see cref="ObjectHitTest.PickTopmost"/> refuses to return these and there is
/// a separate pick for them.
/// </summary>
public sealed record PageTextObject : DocumentObject
{
    /// <summary>
    /// Fixed HERE rather than at the one call site, so the kind cannot be
    /// forgotten or set to something else. It is what
    /// <see cref="ObjectHitTest.PickTopmost"/> refuses on, and a page text
    /// object that claimed to be anything else would be handed to annotation
    /// calls as an index into a list it is not in.
    /// </summary>
    public PageTextObject() => Kind = DocumentObjectKind.PageText;

    /// <summary>What the object says, as PDFium reports it.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The PDF font's name, e.g. "Helvetica-Bold" or a subset tag.</summary>
    public string FontName { get; init; } = string.Empty;

    /// <summary>The size it is set at, in points, before its own matrix.</summary>
    public double FontSizePts { get; init; }

    /// <summary>Fill colour packed as 0x00RRGGBB.</summary>
    public uint ColorRgb { get; init; }

    /// <summary>
    /// Whether the font travels with the document.
    ///
    /// Recorded now because it is free now and decisive later: replacing the
    /// words is limited to the characters the object's own font can express,
    /// and a non-embedded standard font is a different bet from an embedded
    /// subset. Nothing reads it yet.
    /// </summary>
    public bool IsFontEmbedded { get; init; }

    /// <summary>
    /// The object's position in the page's CONTENT, which is what
    /// <c>FPDFPage_GetObject</c> takes. See the warning on this type: it is not
    /// interchangeable with an annotation index.
    /// </summary>
    public int ObjectIndex { get; init; }
}
