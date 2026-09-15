using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;
using Windows.Foundation;

namespace PdfEditorApp.Controls;

/// <summary>
/// Lays the page cards out exactly where the view model has them, realizing
/// only the ones near the viewport.
///
/// ⚠️ NOT StackLayout. StackLayout places cards it has not measured by the
/// average size of the ones it has, and in a book whose opening pages are a
/// different shape from the rest that estimate drifts by thousands of pages.
/// Measured on a 39881-page book: a link to page 39175 scrolled to the right
/// offset, the view model rendered pages 39173 to 39177, and the screen showed
/// blank cards for pages near 36000 that nothing had rendered. See
/// <see cref="PageStackGeometry"/>.
///
/// Cards not asked for in a measure pass are recycled by the ItemsRepeater
/// itself, because nothing here asks it to suppress that.
/// </summary>
public sealed class PageStackLayout : VirtualizingLayout
{
    /// <summary>
    /// How far a window may reach when the repeater offers an unbounded one,
    /// as it can before it has a viewport. Without a bound that first pass
    /// would build a card for every page in the book.
    /// </summary>
    private const double UnboundedWindow = 4000;

    private PageStackGeometry? _geometry;
    private int _first = -1;
    private int _last = -1;

    /// <summary>Gap between cards. Must equal the view model's page gap.</summary>
    public double Spacing { get; set; } = 16;

    /// <summary>
    /// The slots the repeater shows, read directly when building the geometry.
    /// </summary>
    /// <remarks>
    /// ⚠️ ASKING THE CONTEXT IS A TRIP PER PAGE. <c>context.GetItemAt</c> goes
    /// through the repeater's WinRT view of the list, and building the geometry
    /// that way for a 39,881-page book took 250 to 300 ms of the UI thread on
    /// every open. The same objects, read from the list itself, cost nothing.
    /// Used only while its count matches the context's; the context is the
    /// fallback, and the answer either way.
    /// </remarks>
    public IReadOnlyList<PageSlot>? Slots { get; set; }

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        // Rebuilt once, at the next measure, however the stack was refilled.
        _geometry = null;
        base.OnItemsChangedCore(context, source, args);
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        using var uiStall = UiStall.Section("PageStack.Measure");
        var geometry = GeometryFor(context);
        var window = context.RealizationRect;
        double top = window.Y;
        double bottom = double.IsInfinity(window.Height) ? top + UnboundedWindow : top + window.Height;

        (_first, _last) = geometry.Range(top, bottom);
        for (int i = _first; i >= 0 && i <= _last; i++)
        {
            context.GetOrCreateElementAt(i).Measure(new Size(geometry.WidthOf(i), geometry.HeightOf(i)));
        }

        return new Size(geometry.Width, geometry.Height);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        using var uiStall = UiStall.Section("PageStack.Arrange");
        var geometry = GeometryFor(context);
        for (int i = _first; i >= 0 && i <= _last && i < geometry.Count; i++)
        {
            double width = geometry.WidthOf(i);
            context.GetOrCreateElementAt(i).Arrange(
                new Rect((finalSize.Width - width) / 2, geometry.TopOf(i), width, geometry.HeightOf(i)));
        }

        return finalSize;
    }

    private PageStackGeometry GeometryFor(VirtualizingLayoutContext context)
    {
        if (_geometry is { } built && built.Count == context.ItemCount)
        {
            return built;
        }

        using var uiStall = UiStall.Section("PageStack.Geometry");
        var geometry = new PageStackGeometry(Spacing);
        if (Slots is { } slots && slots.Count == context.ItemCount)
        {
            geometry.Rebuild(slots.Count, i => (slots[i].SlotWidth, slots[i].SlotHeight));
        }
        else
        {
            geometry.Rebuild(context.ItemCount, i =>
                context.GetItemAt(i) is PageSlot slot ? (slot.SlotWidth, slot.SlotHeight) : (0, 0));
        }
        _geometry = geometry;
        return geometry;
    }
}
