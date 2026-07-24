using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A page's intrinsic size in PDF points.</summary>
public readonly record struct PageSizePoints(double Width, double Height);

/// <summary>Where one page sits in slot space, and how big its slot is.</summary>
public readonly record struct PageSlotBox(int PageIndex, double Top, double Width, double Height);

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
    /// Rebuilds the stack for the given page sizes at the given width. Pages
    /// keep their aspect ratio; a page that reports a non-positive size gets a
    /// square slot so it still occupies a predictable, non-zero space rather
    /// than collapsing and shifting everything below it.
    /// </summary>
    public void Rebuild(IReadOnlyList<PageSizePoints> pageSizes, double layoutWidth)
    {
        _slots.Clear();
        LayoutWidth = Math.Max(1.0, layoutWidth);

        double top = 0;
        for (int i = 0; i < pageSizes.Count; i++)
        {
            var size = pageSizes[i];
            double aspect = size.Width > 0 && size.Height > 0 ? size.Height / size.Width : 1.0;
            double height = LayoutWidth * aspect;

            _slots.Add(new PageSlotBox(i, top, LayoutWidth, height));
            top += height + PageGap;
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

        return best;
    }

    /// <summary>Slot-space top of a page, for scroll-to-page.</summary>
    public double TopOf(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _slots.Count ? _slots[pageIndex].Top : 0;

    /// <summary>
    /// The zoom factor that makes a page exactly fill the viewport width.
    /// Layout width is fixed, so fit-width is purely a zoom decision, which is
    /// why the layout never has to be rebuilt when the window resizes.
    /// </summary>
    public double FitWidthZoom(double viewportWidth) =>
        LayoutWidth > 0 ? viewportWidth / LayoutWidth : 1.0;
}
