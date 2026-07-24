using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One page card in the continuous viewport.
///
/// <see cref="SlotWidth"/> and <see cref="SlotHeight"/> come from the page's
/// intrinsic size and are set the moment the document opens, BEFORE any
/// rendering. That is what keeps the stack stable: a slot always occupies its
/// final space, so bitmaps can stream in or be released without anything below
/// shifting and without the scrollbar jumping.
/// </summary>
public partial class PageSlot : ObservableObject
{
    public int PageIndex { get; }

    /// <summary>1-based, for display.</summary>
    public int DisplayNumber => PageIndex + 1;

    /// <summary>Slot-space size in unzoomed DIPs. Fixed for the document's lifetime.</summary>
    public double SlotWidth { get; }

    public double SlotHeight { get; }

    /// <summary>
    /// Null while this page is outside the render window. The card keeps its
    /// size and shows a blank page instead, so releasing memory is invisible
    /// in layout terms.
    /// </summary>
    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }

    /// <summary>Annotations belonging to this page, in normalized coordinates.</summary>
    public ObservableCollection<HighlightAnnotation> Highlights { get; } = new();

    public ObservableCollection<InkStrokeAnnotation> InkStrokes { get; } = new();

    public ObservableCollection<NoteAnnotation> Notes { get; } = new();

    public ObservableCollection<TextRect> SelectionRects { get; } = new();

    public ObservableCollection<TextRect> SearchMatchRects { get; } = new();

    /// <summary>
    /// Multiplier turning a normalized overlay coordinate into a slot-space
    /// offset. Both axes use the slot WIDTH so the scale stays uniform, which
    /// matches how the coordinates were normalized when they were captured.
    /// </summary>
    public double OverlayScale => SlotWidth;

    private bool _isRendering;

    /// <summary>The width, in pixels, the current <see cref="Bitmap"/> was rendered at.</summary>
    public int RenderedWidth { get; set; }

    public PageSlot(int pageIndex, double slotWidth, double slotHeight)
    {
        PageIndex = pageIndex;
        SlotWidth = slotWidth;
        SlotHeight = slotHeight;
    }

    /// <summary>
    /// Claims the right to render, returning false if one is already in
    /// flight. Scrolling re-enters the visible range repeatedly and every
    /// render serializes behind render_core's global PDFium lock, so without
    /// this a fast scroll would queue many redundant renders of the same page.
    /// </summary>
    public bool TryBeginRender()
    {
        if (_isRendering)
        {
            return false;
        }

        _isRendering = true;
        return true;
    }

    public void EndRender() => _isRendering = false;

    /// <summary>Drops the bitmap but keeps the slot's size.</summary>
    public void ReleaseBitmap()
    {
        Bitmap = null;
        RenderedWidth = 0;
    }
}
