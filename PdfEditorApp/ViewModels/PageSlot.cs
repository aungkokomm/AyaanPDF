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

    // Every collection below is in SLOT-SPACE DIPs, already multiplied by the
    // slot width, because a normalized rect inside a scaled layer lays out
    // sub-pixel and never draws. See ScaledRect.

    public ObservableCollection<ScaledRect> SelectionRects { get; } = new();

    public ObservableCollection<ScaledRect> SearchMatchRects { get; } = new();

    /// <summary>Every highlight's rectangles, flattened and pre-scaled.</summary>
    public ObservableCollection<ScaledRect> HighlightRects { get; } = new();

    /// <summary>
    /// Marquee around the selected annotation, at most one entry. A plain rect
    /// collection rather than per-annotation selection state, so the
    /// annotation templates stay unaware of selection entirely.
    /// </summary>
    public ObservableCollection<ScaledRect> SelectionOutline { get; } = new();

    /// <summary>Rebuilds the flattened highlight rectangles from the annotations.</summary>
    public void RebuildHighlightRects()
    {
        HighlightRects.Clear();
        foreach (var h in Highlights)
        {
            foreach (var r in h.Rects)
            {
                var sr = ScaledRect.From(r, SlotWidth, h.ColorHex);
                if (sr.IsVisible)
                {
                    HighlightRects.Add(sr);
                }
            }
        }
    }

    /// <summary>
    /// Multiplier turning a normalized overlay coordinate into a slot-space
    /// offset. Both axes use the slot WIDTH so the scale stays uniform, which
    /// matches how the coordinates were normalized when they were captured.
    /// </summary>
    public double OverlayScale => SlotWidth;

    private bool _isRendering;
    private bool _isSharpening;

    /// <summary>The width, in pixels, the displayed <see cref="Bitmap"/> was rendered at.</summary>
    public int RenderedWidth { get; set; }

    /// <summary>
    /// The cached base render, kept even while a sharp one is displayed, so a
    /// page that stops being near the viewport can drop its expensive bitmap
    /// and fall straight back to this without a blank flash or a re-render.
    /// </summary>
    public WriteableBitmap? BaseBitmap { get; private set; }

    public int BaseWidth { get; private set; }

    /// <summary>True while a sharper-than-base render is on display.</summary>
    public bool IsSharp { get; private set; }

    /// <summary>Records a base-tier render and shows it unless a sharp one is already up.</summary>
    public void SetBaseRender(WriteableBitmap? bitmap, int width)
    {
        BaseBitmap = bitmap;
        BaseWidth = width;

        if (!IsSharp)
        {
            Bitmap = bitmap;
            RenderedWidth = width;
        }
    }

    public void SetSharpRender(WriteableBitmap? bitmap, int width)
    {
        if (bitmap is null)
        {
            return;
        }

        Bitmap = bitmap;
        RenderedWidth = width;
        IsSharp = true;
    }

    /// <summary>
    /// Drops the sharp bitmap and shows the base render again. Called when a
    /// page leaves the sharpening window, which is what stops hi-res bitmaps
    /// accumulating for every page the user has scrolled past.
    /// </summary>
    public void DropSharpRender()
    {
        if (!IsSharp)
        {
            return;
        }

        IsSharp = false;
        Bitmap = BaseBitmap;
        RenderedWidth = BaseWidth;
    }

    public bool TryBeginSharpen()
    {
        if (_isSharpening)
        {
            return false;
        }

        _isSharpening = true;
        return true;
    }

    public void EndSharpen() => _isSharpening = false;

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

    /// <summary>Drops every bitmap but keeps the slot's size.</summary>
    public void ReleaseBitmap()
    {
        Bitmap = null;
        BaseBitmap = null;
        RenderedWidth = 0;
        BaseWidth = 0;
        IsSharp = false;
    }
}
