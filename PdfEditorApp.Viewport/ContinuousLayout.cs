using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A page's intrinsic size in PDF points.</summary>
public readonly record struct PageSizePoints(double Width, double Height);

/// <summary>
/// Where one page sits in slot space, how big its slot is, and how its content
/// is turned inside it.
///
/// Width and Height are the CARD, which is what the stack is made of. The
/// transform carries the content box, which is what everything drawn on the
/// page is measured against, and is the same whether or not the view is
/// rotated.
/// </summary>
public readonly record struct PageSlotBox(
    int PageIndex, double Top, double Width, double Height, PageTransform Transform);

/// <summary>
/// Lays pages out as a vertical stack of slots and maps scroll offsets to
/// pages.
///
/// Everything here is in SLOT SPACE: unzoomed DIPs. Zoom is applied by the
/// ScrollView's own zoom factor, so a scroll offset is divided by that factor
/// to land back in slot space. Keeping one zoom-independent coordinate system
/// is what lets the virtualizer, the scroll-to-page logic and the sharpening
/// pass all agree; mixing zoomed and unzoomed units is the classic way this
/// kind of viewport ends up off by exactly the zoom factor.
///
/// Slot heights depend only on page aspect ratio and the layout width, never
/// on what has actually been rendered. That is deliberate: a slot must know
/// its height before its bitmap exists, or pages would resize as they stream
/// in and the scrollbar would jump under the user.
/// </summary>
public sealed class ContinuousLayout
{
    private readonly List<PageSlotBox> _slots = [];

    /// <summary>Gap between page cards, in slot-space DIPs.</summary>
    public double PageGap { get; }

    /// <summary>The width every page is laid out at, in slot-space DIPs.</summary>
    public double LayoutWidth { get; private set; }

    /// <summary>Total height of the stack, including the gaps between pages.</summary>
    public double TotalHeight { get; private set; }

    public int PageCount => _slots.Count;

    public ContinuousLayout(double pageGap = 12.0)
    {
        PageGap = pageGap;
    }

    public IReadOnlyList<PageSlotBox> Slots => _slots;

    /// <summary>
    /// The quarter turn the whole view is shown at. Not a property of the
    /// document: it is never saved, and reopening a file shows it upright.
    /// </summary>
    public int ViewRotation { get; private set; }

    /// <summary>
    /// Rebuilds the stack for the given page sizes at the given width. Pages
    /// keep their aspect ratio; a page that reports a non-positive size gets a
    /// square slot so it still occupies a predictable, non-zero space rather
    /// than collapsing and shifting everything below it.
    ///
    /// A rotated view changes the SHAPE of every card, so it comes through here
    /// rather than being applied afterwards: the stack's heights, and with them
    /// the scrollbar and which page a scroll offset lands on, all follow from
    /// the cards.
    /// </summary>
    public void Rebuild(
        IReadOnlyList<PageSizePoints> pageSizes,
        double layoutWidth,
        int viewRotation = 0,
        int onlyPage = -1)
    {
        _slots.Clear();
        LayoutWidth = Math.Max(1.0, layoutWidth);
        ViewRotation = PageTransform.Normalize(viewRotation);

        double top = 0;
        for (int i = 0; i < pageSizes.Count; i++)
        {
            // Single-page view lays out ONE page. Not every page with the rest
            // hidden: the stack's height is the scroll range, so leaving them in
            // would let the reader scroll through a document's worth of nothing.
            if (onlyPage >= 0 && i != onlyPage)
            {
                continue;
            }

            var size = pageSizes[i];
            double aspect = size.Width > 0 && size.Height > 0 ? size.Height / size.Width : 1.0;

            // The content box is what it has always been, the layout width by
            // the page's aspect. Only the card it sits in changes.
            var transform = PageTransform.For(LayoutWidth, LayoutWidth * aspect, ViewRotation, LayoutWidth);

            _slots.Add(new PageSlotBox(i, top, transform.CardWidth, transform.CardHeight, transform));
            top += transform.CardHeight + PageGap;
        }

        // No trailing gap after the last page.
        TotalHeight = _slots.Count > 0 ? top - PageGap : 0;
    }

    /// <summary>
    /// The inclusive range of pages intersecting a viewport, expressed in
    /// SLOT space. Callers convert from scroll offsets by dividing by the zoom
    /// factor. Returns (-1, -1) when nothing intersects.
    /// </summary>
    public (int First, int Last) VisibleRange(double viewTop, double viewBottom)
    {
        int first = -1;
        int last = -1;

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            double bottom = slot.Top + slot.Height;
            if (bottom >= viewTop && slot.Top <= viewBottom)
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
            }
            else if (first >= 0)
            {
                // Slots are ordered, so once past the viewport we are done.
                break;
            }
        }

        return (first, last);
    }

    /// <summary>
    /// The page occupying most of the viewport, which is what a page-number
    /// readout should show. Falls back to the first intersecting page.
    /// </summary>
    public int DominantPage(double viewTop, double viewBottom)
    {
        var (first, last) = VisibleRange(viewTop, viewBottom);
        if (first < 0)
        {
            return 0;
        }

        int best = first;
        double bestOverlap = -1;
        for (int i = first; i <= last; i++)
        {
            var slot = _slots[i];
            double overlap = Math.Min(viewBottom, slot.Top + slot.Height) - Math.Max(viewTop, slot.Top);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = i;
            }
        }

        // The PAGE, not the slot's position in the stack. Single-page view lays
        // out one slot, and it is page 40 rather than page 0.
        return _slots[best].PageIndex;
    }

    /// <summary>
    /// Slot-space top of a PAGE, for scroll-to-page. Zero for a page the
    /// current view mode is not showing.
    ///
    /// By page number rather than by position in the stack, because those stop
    /// being the same thing the moment a mode lays out fewer pages than the
    /// document has. They were already being conflated: one caller passed a
    /// page and another passed a stack position, and in continuous view both
    /// happened to be right.
    /// </summary>
    public double TopOf(int pageIndex) => SlotForPage(pageIndex)?.Top ?? 0;

    /// <summary>Slot-space height of a PAGE, or 0 if it is not laid out.</summary>
    public double HeightOf(int pageIndex) => SlotForPage(pageIndex)?.Height ?? 0;

    /// <summary>The box for a page, or null when the mode is not showing it.</summary>
    public PageSlotBox? SlotForPage(int pageIndex)
    {
        if (pageIndex < 0)
        {
            return null;
        }

        // Continuous view is the overwhelmingly common case and its stack is
        // page-ordered from zero, so try the direct hit before walking.
        if (pageIndex < _slots.Count && _slots[pageIndex].PageIndex == pageIndex)
        {
            return _slots[pageIndex];
        }

        foreach (var slot in _slots)
        {
            if (slot.PageIndex == pageIndex)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// The zoom factor that makes a page exactly fill the viewport width.
    /// Layout width is fixed, so fit-width is purely a zoom decision, which is
    /// why the layout never has to be rebuilt when the window resizes.
    /// </summary>
    public double FitWidthZoom(double viewportWidth) =>
        LayoutWidth > 0 ? viewportWidth / LayoutWidth : 1.0;
}
