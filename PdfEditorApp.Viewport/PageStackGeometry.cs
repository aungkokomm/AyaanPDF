using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Where every card in the page stack sits, for the layout that realizes them.
///
/// ⚠️ EXACT, NEVER ESTIMATED. WinUI's StackLayout guesses where a card it has
/// not measured sits from the average of the ones it has. A 39881-page book
/// whose opening pages are taller than the rest put a jump to page 39175 among
/// cards for pages near 36000, which nothing had rendered, so the reader saw
/// blank sheets while the right page was drawn into a card off screen. Every
/// card's size is known the moment the document opens, so nothing here has to
/// be guessed.
///
/// Same stacking rule as <see cref="ContinuousLayout"/>: each card starts one
/// gap below the previous one, with no gap after the last.
/// </summary>
public sealed class PageStackGeometry
{
    private double[] _tops = [];
    private double[] _widths = [];
    private double[] _heights = [];

    public PageStackGeometry(double spacing)
    {
        Spacing = spacing;
    }

    /// <summary>Gap between cards, in slot-space DIPs.</summary>
    public double Spacing { get; }

    public int Count => _tops.Length;

    /// <summary>The widest card, which is how wide the stack is.</summary>
    public double Width { get; private set; }

    /// <summary>Top of the first card to bottom of the last.</summary>
    public double Height { get; private set; }

    public double TopOf(int index) => _tops[index];

    public double WidthOf(int index) => _widths[index];

    public double HeightOf(int index) => _heights[index];

    /// <summary>Stacks <paramref name="count"/> cards whose sizes <paramref name="cardAt"/> gives.</summary>
    public void Rebuild(int count, Func<int, (double Width, double Height)> cardAt)
    {
        _tops = new double[count];
        _widths = new double[count];
        _heights = new double[count];
        Width = 0;

        double top = 0;
        for (int i = 0; i < count; i++)
        {
            var (width, height) = cardAt(i);
            _tops[i] = top;
            _widths[i] = width;
            _heights[i] = height;
            Width = Math.Max(Width, width);
            top += height + Spacing;
        }

        Height = count > 0 ? top - Spacing : 0;
    }

    /// <summary>
    /// The inclusive range of cards intersecting a vertical band, or (-1, -1)
    /// when none does. The same test as <see cref="ContinuousLayout.VisibleRange"/>,
    /// found by binary search because a layout pass asks on every scroll and a
    /// book can have tens of thousands of cards.
    /// </summary>
    public (int First, int Last) Range(double top, double bottom)
    {
        int count = Count;
        if (count == 0 || bottom < top)
        {
            return (-1, -1);
        }

        // The first card whose bottom reaches the band.
        int first = count;
        int lo = 0;
        int hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_tops[mid] + _heights[mid] >= top)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (first == count || _tops[first] > bottom)
        {
            return (-1, -1);
        }

        // The last card whose top is still inside it.
        int last = first;
        lo = first;
        hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_tops[mid] <= bottom)
            {
                last = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (first, last);
    }
}
