using Microsoft.UI.Xaml;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One row of the bookmarks panel.
///
/// A display wrapper around <see cref="Bookmark"/>, which stays a plain value
/// in the testable project. The nesting arrives as a depth on a flat list, so
/// the indent is turned into a margin here rather than by building a tree of
/// nested item controls: a flat list virtualizes, and a document with a
/// thousand outline entries scrolls like the thumbnails do.
/// </summary>
public sealed class BookmarkItem
{
    public BookmarkItem(Bookmark mark)
    {
        Mark = mark;
        Title = mark.DisplayTitle;
        // 16 DIPs per level, from the left only. Deep outlines are clamped by
        // BookmarkReader, so this cannot run off the side of the panel.
        Indent = new Thickness(mark.Depth * 16, 0, 0, 0);
    }

    public Bookmark Mark { get; }

    public string Title { get; }

    public Thickness Indent { get; }

    public int PageIndex => Mark.PageIndex;

    /// <summary>Blank for an entry that goes nowhere, rather than a misleading "0".</summary>
    public string PageLabel => Mark.HasTarget ? (Mark.PageIndex + 1).ToString() : string.Empty;

    /// <summary>
    /// An entry with no resolvable destination is shown but not clickable. It
    /// is part of the author's outline, so hiding it would misrepresent the
    /// document; letting it look clickable would just do nothing.
    /// </summary>
    public bool IsEnabled => Mark.HasTarget;
}
