using System;

namespace PdfEditorApp.Viewport;

/// <summary>A caret position: a character index on a specific page.</summary>
public readonly record struct TextPosition(int Page, int CharIndex) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other)
    {
        int byPage = Page.CompareTo(other.Page);
        return byPage != 0 ? byPage : CharIndex.CompareTo(other.CharIndex);
    }

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// A text selection that can span pages, as an anchor and a focus.
///
/// The anchor is where the drag started and the focus is where the pointer is
/// now, so dragging BACKWARDS (up the document) is just a focus that sorts
/// before the anchor. Everything downstream works off the ordered
/// <see cref="Start"/>/<see cref="End"/> pair, so no caller has to care which
/// direction the user dragged.
///
/// The selection is stored as positions, not as rectangles. Rectangles depend
/// on the text layer, which is loaded lazily per page and can be evicted; the
/// positions stay valid regardless, and each page's rectangles are recomputed
/// from them on demand.
/// </summary>
public readonly record struct DocumentSelection(TextPosition Anchor, TextPosition Focus)
{
    /// <summary>The earlier end, whichever direction the drag went.</summary>
    public TextPosition Start => Anchor <= Focus ? Anchor : Focus;

    /// <summary>The later end.</summary>
    public TextPosition End => Anchor <= Focus ? Focus : Anchor;

    public bool IsEmpty => Start.Page == End.Page && Start.CharIndex > End.CharIndex;

    public bool SpansMultiplePages => Start.Page != End.Page;

    /// <summary>Inclusive range of pages this selection touches.</summary>
    public (int First, int Last) PageRange => (Start.Page, End.Page);

    /// <summary>
    /// The character run selected on one page, as (start, length), or null if
    /// this page is untouched or contributes nothing.
    ///
    /// A page in the MIDDLE of a multi-page selection is fully selected, the
    /// first page is selected from the anchor to its end, and the last page
    /// from its start to the focus. Getting these three cases right is the
    /// whole point of the type: doing it inline at each call site is how
    /// cross-page selection ends up subtly wrong at the seams.
    /// </summary>
    public (int Start, int Length)? RangeForPage(int page, int pageCharCount)
    {
        if (pageCharCount <= 0 || page < Start.Page || page > End.Page)
        {
            return null;
        }

        int from = page == Start.Page ? Start.CharIndex : 0;
        int toInclusive = page == End.Page ? End.CharIndex : pageCharCount - 1;

        from = Math.Clamp(from, 0, pageCharCount - 1);
        toInclusive = Math.Clamp(toInclusive, 0, pageCharCount - 1);

        if (toInclusive < from)
        {
            return null;
        }

        return (from, toInclusive - from + 1);
    }

    /// <summary>A selection collapsed at one position, i.e. nothing selected yet.</summary>
    public static DocumentSelection At(int page, int charIndex)
    {
        var p = new TextPosition(page, charIndex);
        return new DocumentSelection(p, p);
    }

    /// <summary>Moves the focus, keeping the anchor. This is a drag in progress.</summary>
    public DocumentSelection ExtendTo(int page, int charIndex) =>
        this with { Focus = new TextPosition(page, charIndex) };
}
