using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEditorApp.Interop;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// Owns viewport state independent of XAML: which document/page is open, the
/// dual-tier bitmap pipeline from phase 0, the zoom/pan state from phase 1,
/// and now (phase 2) which page is current plus the thumbnail sidebar's
/// lazily-rendered entries. The actual zoom/pan/rubber-band math lives in
/// the plain <see cref="PanZoomState"/>/<see cref="MomentumDecay"/> classes
/// (PdfEditorApp.Viewport project) so it's unit-testable without a UI
/// thread; this class is the thin, WinUI-aware glue.
/// </summary>
/// <summary>What plain left-drag/click currently does in the viewport.</summary>
public enum ToolMode
{
    /// <summary>Drag selects text (leaves a transient selection for copy).</summary>
    Select,
    /// <summary>Drag selects text and immediately commits it as a permanent highlight.</summary>
    Highlight,
    /// <summary>Click places a sticky note.</summary>
    Note,
    /// <summary>Drag draws a freehand ink stroke.</summary>
    Draw,
    /// <summary>Plain left-drag pans, same as holding Space with any other tool active.</summary>
    Hand,
}

public partial class ViewportViewModel : ObservableObject, IDisposable
{
    private const int LowResWidth = 200;
    private const int ThumbnailWidth = 120;
    private const int MinRenderWidth = 100;
    private const int MaxRenderWidth = 6000;

    private static readonly TimeSpan RenderSettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _dispatcherQueue;

    private DispatcherQueueTimer? _pollTimer;
    private DispatcherQueueTimer? _renderSettleTimer;

    private ulong _documentHandle;
    private ulong _pendingHighResRequestId;
    private bool _viewportSizeKnown;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _currentZoomFactor = 1.0;
    private bool _hasEstablishedInitialView;

    private int? _selectionAnchorCharIndex;
    private int _selectionStart;
    private int _selectionLength;

    private readonly List<HighlightAnnotation> _allHighlights = new();
    private readonly List<NoteAnnotation> _allNotes = new();
    private readonly List<InkStrokeAnnotation> _allInkStrokes = new();
    private List<(double X, double Y)>? _currentStroke;

    [ObservableProperty]
    public partial WriteableBitmap? PageBitmap { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Opening document...";

    [ObservableProperty]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    /// <summary>Sidebar entries — one per page, rendered lazily as MainPage's virtualized ListView realizes each item's container.</summary>
    public ObservableCollection<PageThumbnail> Thumbnails { get; } = new();

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>Selection rects for the current page, normalized. The per-slot copies are what the cards draw.</summary>
    public ObservableCollection<TextRect> SelectionRects { get; } = new();

    /// <summary>Highlight rects for every match of <see cref="SearchQuery"/> on the current page.</summary>
    public ObservableCollection<TextRect> SearchMatchRects { get; } = new();

    [ObservableProperty]
    public partial ToolMode ActiveTool { get; set; } = ToolMode.Select;

    /// <summary>Committed annotations for the current page only — repopulated whenever the page changes.</summary>
    public ObservableCollection<HighlightAnnotation> Highlights { get; } = new();
    public ObservableCollection<NoteAnnotation> Notes { get; } = new();
    public ObservableCollection<InkStrokeAnnotation> InkStrokes { get; } = new();

    /// <summary>The stroke currently being drawn (Draw tool, drag in progress), or null between strokes.</summary>
    public IReadOnlyList<(double X, double Y)>? CurrentStrokeInProgress => _currentStroke;

    /// <summary>
    /// Every ink stroke on every page. The ink canvas draws from THIS, not the
    /// current-page collection: the continuous viewport shows several pages at
    /// once, so rebuilding from only the current page made ink on a visible
    /// neighbouring page vanish the moment scrolling changed the current page.
    /// </summary>
    public IReadOnlyList<InkStrokeAnnotation> AllInkStrokes => _allInkStrokes;

    /// <summary>The page the in-progress stroke belongs to, for the live preview.</summary>
    public int ActiveInkPage => _inkPageIndex;

    /// <summary>Fires on every point added to the in-progress ink stroke, so MainPage can redraw its live preview.</summary>
    public event Action? InkStrokeChanged;

    /// <summary>
    /// DIPs per NORMALIZED unit, i.e. the multiplier that turns a stored
    /// overlay coordinate into a position inside the page's layout box.
    ///
    /// Every overlay coordinate is stored normalized against the page WIDTH
    /// (both axes share that one divisor, so the scale stays uniform and the
    /// aspect ratio is preserved). Storing raw bitmap pixels instead would
    /// tie each annotation to whatever resolution happened to be on screen
    /// when it was drawn: zooming in swaps a 1165px bitmap for an 1820px one
    /// and every earlier mark would silently shift. Normalized coordinates
    /// are resolution-independent, so a re-render can never move them.
    /// </summary>
    public double OverlayScale => SlotLayoutWidth;

    /// <summary>
    /// Slot-space DIPs to normalized units.
    ///
    /// The divisor is the FIXED slot width, never the current bitmap width.
    /// Normalizing against the bitmap would tie a mark to whatever resolution
    /// happened to be on screen when it was drawn, so zooming in and
    /// re-rendering would silently move every earlier mark.
    /// </summary>
    private static double Norm(double slotDips) => slotDips / SlotLayoutWidth;

    private TextRect NormRect(TextRect r) =>
        new(Norm(r.Left), Norm(r.Top), Norm(r.Right), Norm(r.Bottom));

    /// <summary>Raised when the overlay scale changes, so MainPage can restretch the overlays.</summary>
    public event Action? ContentScaleChanged;

    // ---------------- Document lifecycle ----------------

    public ViewportViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Deliberately does NOT open a document. It used to load the bundled
        // sample here, which meant every launch opened, laid out, fitted and
        // rasterized a document the user never asked for, and then did the
        // whole thing again the moment they opened their own file. The app
        // starts on an empty canvas instead.
    }

    /// <summary>Re-opens the bundled sample document (the "Re-render" button).</summary>
    [RelayCommand]
    private void OpenAndRenderSampleDocument() => OpenDocument(Path.Combine(AppContext.BaseDirectory, "sample.pdf"));

    /// <summary>Opens any PDF by path - the File&gt;Open entry point.</summary>
    /// <summary>
    /// Opens a PDF. <paramref name="preserveAnnotations"/> is only set by the
    /// save path, which reloads the SAME file to discard a burned copy and
    /// must keep the overlays that are still pending.
    /// </summary>
    public void OpenDocument(string path, bool preserveAnnotations = false)
    {
        CloseCurrentDocument();

        _documentHandle = RenderCoreNative.open_document(path);
        _currentDocumentPath = path;

        Thumbnails.Clear();
        if (!preserveAnnotations)
        {
            // Annotations are keyed by page index only, so carrying them across
            // a document switch would misattach them to whatever page shares
            // that index in the new file.
            _allHighlights.Clear();
            _allNotes.Clear();
            _allInkStrokes.Clear();
        }
        Highlights.Clear();
        Notes.Clear();
        InkStrokes.Clear();

        // Layers are keyed by page index, so carrying them across a document
        // switch would hand the new document the old one's text.
        _textLayers.Clear();

        // A fresh document has no history and no unsaved edits. A reload after
        // a burn/save (preserveAnnotations) is also a clean slate: those marks
        // are now baked into the page content, so there is nothing to undo.
        _history.Clear();
        IsDirty = false;
        NotifyHistoryChanged();

        if (_documentHandle == 0)
        {
            PageCount = 0;
            Status = $"Failed to open {Path.GetFileName(path)}";
            return;
        }

        PageCount = Math.Max(0, RenderCoreNative.get_page_count(_documentHandle));
        for (int i = 0; i < PageCount; i++)
        {
            Thumbnails.Add(new PageThumbnail(i));
        }

        CurrentPageIndex = 0;
        RebuildContinuousLayout();
        RenderCurrentPage();
    }

    /// <summary>Navigates to a page (e.g. a thumbnail click).</summary>
    /// <summary>
    /// Asks the view to bring a page into view. Carries the page index and
    /// whether the move should be animated.
    ///
    /// The view model cannot scroll: the ScrollView owns the scroll position
    /// and runs the animation on the compositor. So navigation is a REQUEST
    /// the view fulfils, which also keeps the slot-space to scroll-offset
    /// conversion in the one place that already does it.
    /// </summary>
    public event Action<int, bool>? ScrollToPageRequested;

    public void GoToPage(int pageIndex, bool animate = true)
    {
        if (_documentHandle == 0 || pageIndex < 0 || pageIndex >= PageCount)
        {
            return;
        }

        // Deliberately NOT short-circuited on pageIndex == CurrentPageIndex.
        // Scrolling makes a partly visible page current long before it is
        // actually in view, so clicking its thumbnail has to still scroll to
        // it; bailing out early is why clicking a thumbnail could do nothing.
        CurrentPageIndex = pageIndex;
        RenderCurrentPage();
        ScrollToPageRequested?.Invoke(pageIndex, animate);
    }

    // ---------------- Page operations ----------------

    public void RotateCurrentPage(int degrees)
    {
        if (_documentHandle == 0)
        {
            return;
        }

        // Snapshot before the rotate, since it restructures the page tree.
        PushHistory(HistoryScope.Document, "Rotate page");

        if (RenderCoreNative.rotate_page(_documentHandle, CurrentPageIndex, degrees) != RenderStatus.OkPdfium)
        {
            return;
        }

        IsDirty = true;

        var thumb = PageRenderer.RenderLowRes(_documentHandle, CurrentPageIndex, ThumbnailWidth);
        Thumbnails[CurrentPageIndex].Bitmap = thumb.Bitmap;
        RenderCurrentPage();
    }

    /// <summary>Deletes the current page. Refuses to delete the last remaining page.</summary>
    public void DeleteCurrentPage()
    {
        if (_documentHandle == 0 || PageCount <= 1)
        {
            return;
        }

        // A delete cannot be reversed object-by-object, so snapshot first.
        PushHistory(HistoryScope.Document, "Delete page");

        if (RenderCoreNative.delete_page(_documentHandle, CurrentPageIndex) != RenderStatus.OkPdfium)
        {
            return;
        }

        IsDirty = true;
        PageCount--;

        // Every page after the deleted one shifted down, so layers cached by
        // index now describe the wrong pages.
        _textLayers.Clear();
        ClearSelection();

        // Later pages all shifted down one, so cached thumbnails (rendered at
        // the old indices) are stale; rebuild and let virtualization re-render.
        Thumbnails.Clear();
        for (int i = 0; i < PageCount; i++)
        {
            Thumbnails.Add(new PageThumbnail(i));
        }

        if (CurrentPageIndex >= PageCount)
        {
            CurrentPageIndex = PageCount - 1;
        }

        RenderCurrentPage();
    }

    /// <summary>
    /// Path the current document was opened from, so it can be reloaded after
    /// a save that burned annotations into it.
    /// </summary>
    private string? _currentDocumentPath;

    /// <summary>
    /// Flattens annotations into the document, writes it to <paramref name="path"/>,
    /// then RELOADS from the original file.
    ///
    /// The reload is what stops a second save from burning the same marks
    /// again on top of the first set. Burning mutates the in-memory document,
    /// but the annotation lists stay populated so the overlays keep working,
    /// so without discarding the burned copy every subsequent save would
    /// stack another layer of the same highlights and strokes.
    /// </summary>
    public bool SaveDocumentAs(string path)
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        bool burned = BurnAllAnnotations();

        bool saved = RenderCoreNative.save_document(_documentHandle, path) == RenderStatus.OkPdfium;

        if (burned && saved)
        {
            // Reopen the file we just wrote, with the overlay lists cleared.
            //
            // The saved file already contains the burned marks AND any page
            // rotations/deletes, so reopening it is both the clean slate that
            // stops the next save from burning the same marks twice and the
            // only reload source that keeps the page operations. Reloading the
            // ORIGINAL instead would silently drop every rotate/delete, since
            // those live only in the in-memory document, not on the original
            // on disk. After a Save As the working document becomes the new
            // file, matching how editors retitle to the saved path.
            int page = CurrentPageIndex;
            OpenDocument(path, preserveAnnotations: false);
            // Not animated: this is restoring the position the user was
            // already at after a save reloaded the document, not navigating
            // somewhere. Animating it would look like the app moved on its own.
            GoToPage(Math.Min(page, Math.Max(0, PageCount - 1)), animate: false);
        }

        if (saved)
        {
            // On disk and in memory now agree. OpenDocument already cleared
            // this on the burn path; this covers a save with no burnable marks
            // (e.g. only page rotations/deletes).
            IsDirty = false;
        }

        return saved;
    }

    /// <summary>
    /// Writes notes into the document as PDF text annotations.
    ///
    /// Notes were previously dropped entirely on save: highlights and ink were
    /// flattened and notes were simply not passed to the burn call, so a user
    /// who annotated a document with notes and saved it lost every one of them
    /// without being told. They are not flattened either, because a note IS
    /// its text and a flattened marker would keep the mark and lose the words.
    ///
    /// Returns true if anything was added, so the caller knows the document
    /// changed and must be reloaded.
    /// </summary>
    private bool AddNoteAnnotations()
    {
        if (_documentHandle == 0 || _allNotes.Count == 0)
        {
            return false;
        }

        const int CaptureWidth = 1000;

        var notes = new BurnNote[_allNotes.Count];
        // The native side reads these pointers during the call, so the
        // unmanaged copies have to outlive it and then be freed by hand.
        var allocated = new IntPtr[_allNotes.Count];

        try
        {
            for (int i = 0; i < _allNotes.Count; i++)
            {
                var note = _allNotes[i];
                allocated[i] = Marshal.StringToCoTaskMemUTF8(note.Text ?? string.Empty);
                notes[i] = new BurnNote
                {
                    PageIndex = note.PageIndex,
                    X = (float)(note.X * CaptureWidth),
                    Y = (float)(note.Y * CaptureWidth),
                    Text = allocated[i],
                };
            }

            return RenderCoreNative.add_note_annotations(
                _documentHandle, CaptureWidth, notes, (nuint)notes.Length) == RenderStatus.OkPdfium;
        }
        finally
        {
            foreach (var ptr in allocated)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
    }

    /// <summary>
    /// Burns every stored annotation into page content. Returns false when
    /// there was nothing to burn, so the caller can skip the reload.
    /// </summary>
    private bool BurnAllAnnotations()
    {
        // Notes go first and by a different mechanism: they become real PDF
        // text annotations rather than flattened content, so their text
        // survives. See AddNoteAnnotations.
        bool addedNotes = AddNoteAnnotations();

        if (_allHighlights.Count == 0 && _allInkStrokes.Count == 0)
        {
            return addedNotes;
        }

        // Coordinates are stored normalized; render_core wants them in the
        // pixel space of some capture width, so scale by a reference width.
        // Any value works as long as both sides agree, since it cancels out.
        const int CaptureWidth = 1000;

        var rects = new List<BurnRect>();
        foreach (var h in _allHighlights)
        {
            var (r, g, b, a) = ParseHex(h.ColorHex, defaultAlpha: 0x88);
            foreach (var rect in h.Rects)
            {
                rects.Add(new BurnRect
                {
                    PageIndex = h.PageIndex,
                    Left = (float)(rect.Left * CaptureWidth),
                    Top = (float)(rect.Top * CaptureWidth),
                    Right = (float)(rect.Right * CaptureWidth),
                    Bottom = (float)(rect.Bottom * CaptureWidth),
                    R = r, G = g, B = b, A = a,
                });
            }
        }

        var strokes = new List<BurnStroke>();
        var points = new List<BurnPoint>();
        foreach (var s in _allInkStrokes)
        {
            if (s.Points.Count < 2)
            {
                continue;
            }

            var (r, g, b, a) = ParseHex(s.ColorHex, defaultAlpha: 0xFF);
            strokes.Add(new BurnStroke
            {
                PageIndex = s.PageIndex,
                PointOffset = (uint)points.Count,
                PointCount = (uint)s.Points.Count,
                WidthPx = (float)(s.StrokeWidth * CaptureWidth),
                R = r, G = g, B = b, A = a,
            });
            foreach (var (x, y) in s.Points)
            {
                points.Add(new BurnPoint { X = (float)(x * CaptureWidth), Y = (float)(y * CaptureWidth) });
            }
        }

        if (rects.Count == 0 && strokes.Count == 0)
        {
            return false;
        }

        int status = RenderCoreNative.burn_annotations(
            _documentHandle,
            CaptureWidth,
            rects.ToArray(), (nuint)rects.Count,
            strokes.ToArray(), (nuint)strokes.Count,
            points.ToArray(), (nuint)points.Count);

        Debug.WriteLine($"[ViewportViewModel] burn: {rects.Count} rects, {strokes.Count} strokes -> status={status}");
        return status == RenderStatus.OkPdfium;
    }

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB"; falls back to opaque yellow.</summary>
    private static (byte R, byte G, byte B, byte A) ParseHex(string hex, byte defaultAlpha)
    {
        hex = (hex ?? string.Empty).TrimStart('#');
        try
        {
            if (hex.Length == 8)
            {
                return (Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16),
                        Convert.ToByte(hex.Substring(6, 2), 16),
                        Convert.ToByte(hex.Substring(0, 2), 16));
            }
            if (hex.Length == 6)
            {
                return (Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16),
                        defaultAlpha);
            }
        }
        catch { /* fall through */ }

        return (0xFF, 0xFF, 0x00, defaultAlpha);
    }

    // ---------------- AcroForm ----------------

    public int FormFieldCount =>
        _documentHandle != 0 ? Math.Max(0, RenderCoreNative.get_form_field_count(_documentHandle)) : 0;

    public bool FillFormField(string fieldName, string value) =>
        _documentHandle != 0 && RenderCoreNative.fill_text_field(_documentHandle, fieldName, value) == RenderStatus.OkPdfium;

    // ---------------- Continuous viewport ----------------

    /// <summary>
    /// Width every page is laid out at in SLOT space (unzoomed DIPs). Fixed
    /// and independent of the window: zoom, not layout, is what makes a page
    /// fit the viewport, so resizing never rebuilds the stack or moves the
    /// scroll position.
    /// </summary>
    private const double SlotLayoutWidth = 800;

    /// <summary>Pages beyond the viewport that are kept rendered.</summary>
    private const int RenderAheadPages = 2;

    /// <summary>Pages beyond which bitmaps are released. Wider than the render
    /// window so a small scroll oscillation doesn't thrash render and release.</summary>
    private const int ReleaseBeyondPages = 6;

    private readonly ContinuousLayout _layout = new(pageGap: 16);
    private readonly RenderBudget _budget = new();

    /// <summary>Last viewport bounds in SLOT space, so the debounced sharpen
    /// pass knows what was on screen when the view settled.</summary>
    private double _lastViewTop;
    private double _lastViewBottom;

    public ObservableCollection<PageSlot> PageSlots { get; } = new();

    /// <summary>Slot-space size of the whole stack, for the scroll content.</summary>
    public double ContentWidth => _layout.LayoutWidth;

    public double ContentHeight => _layout.TotalHeight;

    /// <summary>Raised after the slot stack is rebuilt, so the view can fit-width.</summary>
    public event Action? LayoutRebuilt;

    /// <summary>
    /// Reads every page size in one native call and lays the stack out. Sizes
    /// are known before any rendering, so each card occupies its final space
    /// immediately and nothing shifts as bitmaps stream in.
    /// </summary>
    private void RebuildContinuousLayout()
    {
        PageSlots.Clear();
        _layout.Rebuild([], SlotLayoutWidth);

        if (_documentHandle == 0)
        {
            OnPropertyChanged(nameof(ContentWidth));
            OnPropertyChanged(nameof(ContentHeight));
            return;
        }

        var sizes = new List<PageSizePoints>();
        var array = RenderCoreNative.get_page_sizes(_documentHandle);
        try
        {
            if (array.Status == RenderStatus.OkPdfium && array.Sizes != IntPtr.Zero)
            {
                int count = (int)array.Len;
                int stride = Marshal.SizeOf<NativePageSize>();
                for (int i = 0; i < count; i++)
                {
                    var native = Marshal.PtrToStructure<NativePageSize>(array.Sizes + i * stride);
                    sizes.Add(new PageSizePoints(native.Width, native.Height));
                }
            }
        }
        finally
        {
            RenderCoreNative.free_page_size_array(array);
        }

        _layout.Rebuild(sizes, SlotLayoutWidth);
        foreach (var slot in _layout.Slots)
        {
            PageSlots.Add(new PageSlot(slot.PageIndex, slot.Width, slot.Height));
        }

        Diag.Log($"layout: {PageSlots.Count} slots, content {ContentWidth:F0}x{ContentHeight:F0}");

        DistributeAnnotationsToSlots();
        OnPropertyChanged(nameof(ContentWidth));
        OnPropertyChanged(nameof(ContentHeight));
        LayoutRebuilt?.Invoke();
    }

    /// <summary>
    /// The zoom that makes a page span the viewport width. Pure function of
    /// the fixed layout width, so it is always correct, even before anything
    /// has rendered.
    /// </summary>
    public double FitWidthZoom(double viewportWidth) => _layout.FitWidthZoom(viewportWidth);

    /// <summary>
    /// The zoom that shows the WHOLE first page with air around it.
    ///
    /// This is the default view, not fit-width. Fit-width makes the page span
    /// the viewport exactly, so the sheet runs edge to edge and reads as a
    /// region of the window rather than an object on a canvas. Fitting the
    /// page instead leaves the surface visible on all sides, which is what
    /// makes it feel like something sitting on a work surface.
    /// </summary>
    public double FitPageZoom(double viewportWidth, double viewportHeight)
    {
        if (PageSlots.Count == 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return 1.0;
        }

        var first = PageSlots[0];
        if (first.SlotWidth <= 0 || first.SlotHeight <= 0)
        {
            return 1.0;
        }

        // Breathing room on every side, as a fraction of the viewport.
        const double Margin = 0.94;

        double byWidth = viewportWidth * Margin / first.SlotWidth;
        double byHeight = viewportHeight * Margin / first.SlotHeight;
        return Math.Min(byWidth, byHeight);
    }

    /// <summary>Slot-space top of a page, for scroll-to-page.</summary>
    public double SlotTopOf(int pageIndex) => _layout.TopOf(pageIndex);

    /// <summary>"7 / 20", or empty when no document is open.</summary>
    public string PagePositionLabel =>
        PageCount > 0 ? $"{CurrentPageIndex + 1} / {PageCount}" : string.Empty;

    /// <summary>Shown only when nothing is open, so the canvas is never a blank void.</summary>
    public Visibility EmptyStateVisibility =>
        PageCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnCurrentPageIndexChanged(int value) =>
        OnPropertyChanged(nameof(PagePositionLabel));

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PagePositionLabel));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    /// <summary>
    /// Drives rendering and release from the current scroll position.
    ///
    /// Offsets arrive in ZOOMED pixels and are divided back into slot space,
    /// which is the single mapping every pass here shares. Called on every
    /// view change, so it must stay cheap: the work is bounded by the number
    /// of visible pages, not the document length.
    /// </summary>
    public void UpdateVisibleWindow(double verticalOffset, double viewportHeight, double zoomFactor)
    {
        if (_documentHandle == 0 || PageSlots.Count == 0)
        {
            return;
        }

        double zoom = Math.Max(0.01, zoomFactor);
        _currentZoomFactor = zoom;

        double viewTop = verticalOffset / zoom;
        double viewBottom = (verticalOffset + viewportHeight) / zoom;
        _lastViewTop = viewTop;
        _lastViewBottom = viewBottom;

        var (first, last) = _layout.VisibleRange(viewTop, viewBottom);
        if (first < 0)
        {
            return;
        }

        int page = _layout.DominantPage(viewTop, viewBottom);
        if (page != CurrentPageIndex)
        {
            CurrentPageIndex = page;
            OnCurrentPageChangedByScroll();
        }

        var (renderFrom, renderTo) = RenderBudget.Widen(first, last, RenderAheadPages, PageSlots.Count);
        var (keepFrom, keepTo) = RenderBudget.Widen(first, last, ReleaseBeyondPages, PageSlots.Count);

        for (int i = 0; i < PageSlots.Count; i++)
        {
            var slot = PageSlots[i];

            if (i < keepFrom || i > keepTo)
            {
                // Outside the keep window: drop pixels, keep the slot's size.
                if (slot.Bitmap is not null || slot.BaseBitmap is not null)
                {
                    slot.ReleaseBitmap();
                }
                continue;
            }

            // Inside the keep window but outside the sharpening window: give
            // back the expensive bitmap and show the cached base render. This
            // is what stops hi-res bitmaps accumulating for every page already
            // scrolled past.
            if (slot.IsSharp && (i < first - SharpenAheadPages || i > last + SharpenAheadPages))
            {
                slot.DropSharpRender();
            }

            if (i >= renderFrom && i <= renderTo && slot.BaseBitmap is null)
            {
                RenderBaseTier(slot);
            }
        }

        // Sharpening is deferred: during a scroll or a zoom gesture the base
        // tier is what the user sees, and re-rasterizing on every frame would
        // just burn the UI thread on bitmaps that are obsolete before they
        // finish. One pass once the view settles.
        ScheduleSharpenPass();
    }

    /// <summary>
    /// Renders the cached, modest tier. Fast, shared through render_core's
    /// tile cache, and the fallback a page returns to when it stops being
    /// near the viewport.
    /// </summary>
    private async void RenderBaseTier(PageSlot slot)
    {
        if (!slot.TryBeginRender())
        {
            return;
        }

        ulong handle = _documentHandle;
        int pageIndex = slot.PageIndex;
        int width = _budget.BaseWidth;

        var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, width));

        // The document can be closed or replaced while a render is in flight.
        if (handle != _documentHandle)
        {
            slot.EndRender();
            return;
        }

        if (raw.Bgra is not null)
        {
            slot.SetBaseRender(PageRenderer.ToBitmap(raw).Bitmap, raw.Width);
            Diag.Log($"base {pageIndex}: {raw.Width}x{raw.Height} {raw.Outcome}");
        }

        slot.EndRender();
    }

    // ---------------- Debounced sharpening pass ----------------

    private DispatcherQueueTimer? _sharpenTimer;

    /// <summary>Pages either side of the viewport that are kept sharp.</summary>
    private const int SharpenAheadPages = 1;

    private void ScheduleSharpenPass()
    {
        _sharpenTimer ??= CreateSharpenTimer();
        if (_sharpenTimer is null)
        {
            return;
        }

        // Restarting on every view change is the debounce: the pass only runs
        // once the user stops moving.
        _sharpenTimer.Stop();
        _sharpenTimer.Start();
    }

    private DispatcherQueueTimer? CreateSharpenTimer()
    {
        var queue = _dispatcherQueue;
        if (queue is null)
        {
            return null;
        }

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (s, _) =>
        {
            s.Stop();
            SharpenVisiblePages();
        };
        return timer;
    }

    /// <summary>
    /// Re-renders the pages actually on screen at a DPI- and zoom-aware
    /// resolution, uncached. Only these few pages pay for a large bitmap, and
    /// they hand it back as soon as they scroll away.
    /// </summary>
    private void SharpenVisiblePages()
    {
        if (_documentHandle == 0 || PageSlots.Count == 0)
        {
            return;
        }

        var (first, last) = _layout.VisibleRange(_lastViewTop, _lastViewBottom);
        if (first < 0)
        {
            return;
        }

        // Zoomed deep, one page fills the viewport, so sharpening its
        // neighbours spends the budget on pixels nobody can see. Narrowing to
        // the visible page alone is what pays for the much larger per-page
        // resolution the budget now allows.
        int ahead = _currentZoomFactor > RenderBudget.SoloSharpenZoom ? 0 : SharpenAheadPages;
        var (from, to) = RenderBudget.Widen(first, last, ahead, PageSlots.Count);

        for (int i = from; i <= to; i++)
        {
            var slot = PageSlots[i];
            double aspect = slot.SlotWidth > 0 ? slot.SlotHeight / slot.SlotWidth : 1.0;
            int desired = _budget.SharpWidthFor(slot.SlotWidth, _currentZoomFactor, RasterizationScale, aspect);

            if (_budget.ShouldResharpen(slot.RenderedWidth, desired))
            {
                SharpenSlot(slot, desired);
            }
        }
    }

    private async void SharpenSlot(PageSlot slot, int targetWidth)
    {
        if (!slot.TryBeginSharpen())
        {
            return;
        }

        ulong handle = _documentHandle;
        int pageIndex = slot.PageIndex;

        var raw = await Task.Run(() => PageRenderer.RenderUncachedRaw(handle, pageIndex, targetWidth));

        if (handle != _documentHandle)
        {
            slot.EndSharpen();
            return;
        }

        // Drop a result the zoom has already moved past. A sharp render of a
        // large page takes long enough that a zoom gesture can finish while it
        // is in flight, and applying it would show a bitmap at the wrong
        // resolution until the next pass replaced it.
        double aspect = slot.SlotWidth > 0 ? slot.SlotHeight / slot.SlotWidth : 1.0;
        int wantedNow = _budget.SharpWidthFor(slot.SlotWidth, _currentZoomFactor, RasterizationScale, aspect);
        if (_budget.ShouldResharpen(targetWidth, wantedNow))
        {
            slot.EndSharpen();
            return;
        }

        if (raw.Bgra is not null)
        {
            slot.SetSharpRender(PageRenderer.ToBitmap(raw).Bitmap, raw.Width);
            Diag.Log($"sharpened {pageIndex} to {raw.Width}x{raw.Height}");
        }

        slot.EndSharpen();
    }

    /// <summary>
    /// Fans the flat annotation lists out to the slot that owns each one, so a
    /// card only ever draws its own marks.
    /// </summary>
    private void DistributeAnnotationsToSlots()
    {
        foreach (var slot in PageSlots)
        {
            slot.Highlights.Clear();
            slot.InkStrokes.Clear();
            slot.Notes.Clear();
            slot.SelectionRects.Clear();
            slot.SearchMatchRects.Clear();
        }

        foreach (var h in _allHighlights)
        {
            if (h.PageIndex >= 0 && h.PageIndex < PageSlots.Count)
            {
                PageSlots[h.PageIndex].Highlights.Add(h);
            }
        }

        foreach (var s in _allInkStrokes)
        {
            if (s.PageIndex >= 0 && s.PageIndex < PageSlots.Count)
            {
                PageSlots[s.PageIndex].InkStrokes.Add(s);
            }
        }

        foreach (var n in _allNotes)
        {
            if (n.PageIndex >= 0 && n.PageIndex < PageSlots.Count)
            {
                PageSlots[n.PageIndex].Notes.Add(n);
            }
        }
    }

    /// <summary>
    /// Scrolling changed which page is current. The text layer belongs to the
    /// current page, so it is dropped and reloaded lazily rather than kept for
    /// a page the user has left.
    /// </summary>
    /// <summary>
    /// Resolves a point in SLOT space (unzoomed DIPs from the top-left of the
    /// page stack) to the page under it plus a point local to that page's
    /// card. A point in a gap between pages snaps to the nearer page, so a
    /// drag that crosses a page boundary does not lose its target.
    ///
    /// Returns false only when there are no pages at all.
    /// </summary>
    public bool HitTestSlotSpace(double slotX, double slotY, out int pageIndex, out double localX, out double localY)
    {
        pageIndex = 0;
        localX = 0;
        localY = 0;

        if (_layout.PageCount == 0)
        {
            return false;
        }

        var slots = _layout.Slots;
        int best = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (slotY >= s.Top && slotY <= s.Top + s.Height)
            {
                best = i;
                break;
            }

            // Past this page: remember it as the nearest so far and keep going.
            if (slotY > s.Top)
            {
                best = i;
            }
        }

        pageIndex = best;
        localX = slotX;
        localY = slotY - slots[best].Top;
        return true;
    }

    // ---------------- Annotation layer: select, move, delete ----------------

    /// <summary>
    /// The selected annotation's id, or null. Held as an id rather than a
    /// reference because annotations are immutable records: moving one
    /// produces a new instance, and a reference would go stale mid-drag.
    /// </summary>
    private Guid? _selectedAnnotationId;

    /// <summary>Where the current move gesture started, in normalized units.</summary>
    private (double X, double Y)? _moveOrigin;

    public bool HasSelectedAnnotation => _selectedAnnotationId is not null;

    /// <summary>Every annotation, in draw order, as the layer stack.</summary>
    private List<IAnnotation> AllAnnotations()
    {
        var all = new List<IAnnotation>(_allHighlights.Count + _allInkStrokes.Count);
        all.AddRange(_allHighlights);
        all.AddRange(_allInkStrokes);
        return all;
    }

    /// <summary>
    /// Selects the topmost annotation under a normalized page-local point,
    /// or clears the selection when the point is empty. Returns true if
    /// something was selected, so the caller knows a drag should move it
    /// rather than start a new mark.
    /// </summary>
    public bool SelectAnnotationAt(int pageIndex, double normX, double normY)
    {
        var hit = AnnotationHitTester.HitTest(AllAnnotations(), pageIndex, normX, normY);
        _selectedAnnotationId = hit?.Id;
        _moveOrigin = hit is not null ? (normX, normY) : null;

        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        return hit is not null;
    }

    public void ClearAnnotationSelection()
    {
        if (_selectedAnnotationId is null)
        {
            return;
        }

        _selectedAnnotationId = null;
        _moveOrigin = null;
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
    }

    /// <summary>
    /// Drags the selected annotation to a new normalized point.
    ///
    /// The whole gesture is ONE undo step: history is pushed on the first
    /// move, not on every pointer sample, or dragging a mark across a page
    /// would bury the undo stack under hundreds of entries.
    /// </summary>
    public void MoveSelectedAnnotationTo(double normX, double normY)
    {
        if (_selectedAnnotationId is not Guid id || _moveOrigin is not (double ox, double oy))
        {
            return;
        }

        double dx = normX - ox;
        double dy = normY - oy;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        if (!_isMovingAnnotation)
        {
            PushHistory(HistoryScope.Annotations, "Move annotation");
            _isMovingAnnotation = true;
        }

        ReplaceAnnotation(id, a => a.Translate(dx, dy));
        _moveOrigin = (normX, normY);
        IsDirty = true;

        DistributeAnnotationsToSlots();
        RefreshSelectionOutline();
    }

    private bool _isMovingAnnotation;

    /// <summary>Ends a move gesture, so the next one starts a fresh undo step.</summary>
    public void EndAnnotationMove()
    {
        _isMovingAnnotation = false;
        _moveOrigin = null;
    }

    public void DeleteSelectedAnnotation()
    {
        if (_selectedAnnotationId is not Guid id)
        {
            return;
        }

        int removed = _allHighlights.RemoveAll(h => h.Id == id) + _allInkStrokes.RemoveAll(s => s.Id == id);
        if (removed == 0)
        {
            return;
        }

        PushHistory(HistoryScope.Annotations, "Delete annotation");
        _selectedAnnotationId = null;
        _moveOrigin = null;
        IsDirty = true;

        DistributeAnnotationsToSlots();
        RefreshAnnotationsForCurrentPage();
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
    }

    private void ReplaceAnnotation(Guid id, Func<IAnnotation, IAnnotation> edit)
    {
        for (int i = 0; i < _allHighlights.Count; i++)
        {
            if (_allHighlights[i].Id == id)
            {
                _allHighlights[i] = (HighlightAnnotation)edit(_allHighlights[i]);
                return;
            }
        }

        for (int i = 0; i < _allInkStrokes.Count; i++)
        {
            if (_allInkStrokes[i].Id == id)
            {
                _allInkStrokes[i] = (InkStrokeAnnotation)edit(_allInkStrokes[i]);
                return;
            }
        }
    }

    /// <summary>
    /// Puts a marquee around the selected annotation, on its page's card. The
    /// outline is a plain rect collection rather than per-annotation state, so
    /// nothing in the annotation templates has to know about selection.
    /// </summary>
    private void RefreshSelectionOutline()
    {
        foreach (var slot in PageSlots)
        {
            slot.SelectionOutline.Clear();
        }

        if (_selectedAnnotationId is not Guid id)
        {
            return;
        }

        var selected = AllAnnotations().FirstOrDefault(a => a.Id == id);
        if (selected is null)
        {
            return;
        }

        SlotFor(selected.PageIndex)?.SelectionOutline.Add(selected.Bounds);
        InkStrokeChanged?.Invoke();
    }

    /// <summary>The card owning a page, or null if the index is out of range.</summary>
    private PageSlot? SlotFor(int pageIndex) =>
        pageIndex >= 0 && pageIndex < PageSlots.Count ? PageSlots[pageIndex] : null;

    /// <summary>
    /// Scrolling changed which page is current.
    ///
    /// This deliberately does NOT clear the selection. A selection can span
    /// pages, and extending one to a page below necessarily scrolls, so
    /// discarding it here would make a cross-page drag impossible. Search
    /// results are re-centred on the new page instead.
    /// </summary>
    private void OnCurrentPageChangedByScroll()
    {
        RefreshAnnotationsForCurrentPage();
        RecomputeSearchMatches();
    }

    // ---------------- Thumbnails ----------------

    /// <summary>
    /// Renders a thumbnail on first realization of its container, so a
    /// 300-page document only renders what the sidebar actually shows.
    /// </summary>
    public async void EnsureThumbnailRendered(int pageIndex)
    {
        if (_documentHandle == 0 || pageIndex < 0 || pageIndex >= Thumbnails.Count)
        {
            return;
        }

        var thumbnail = Thumbnails[pageIndex];
        if (thumbnail.Bitmap is not null || !thumbnail.TryBeginRender())
        {
            return;
        }

        ulong handle = _documentHandle;

        // PDFium rasterization is tens of ms per page and sidebar scrolling
        // realizes containers in bursts, so this must not run inline.
        var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, ThumbnailWidth));

        if (handle != _documentHandle)
        {
            thumbnail.EndRender();
            return;
        }

        Diag.Log($"thumb {pageIndex}: {raw.Width}x{raw.Height} {raw.Outcome}");
        thumbnail.Bitmap = PageRenderer.ToBitmap(raw).Bitmap;
        thumbnail.EndRender();
    }

    // ---------------- Text selection ----------------

    /// <summary>
    /// The live selection, which can span pages. Null when nothing is
    /// selected. Held as positions rather than rectangles so it survives a
    /// text layer being evicted and re-extracted.
    /// </summary>
    private DocumentSelection? _selection;

    /// <summary>True while a drag is in progress.</summary>
    private bool _isSelecting;

    /// <summary>Diagnostics only: how many glyphs a page's text layer holds.</summary>
    public int DebugTextLayerCharCount(int pageIndex) => TextLayerFor(pageIndex)?.CharCount ?? -1;

    /// <summary>Diagnostics only: the extent the text layer's glyph boxes occupy.</summary>
    public string DebugTextLayerBounds(int pageIndex)
    {
        var layer = TextLayerFor(pageIndex);
        if (layer is null || layer.CharCount == 0)
        {
            return "none";
        }

        var r = layer.GetRangeRects(0, layer.CharCount);
        if (r.Count == 0)
        {
            return "no rects";
        }

        return $"x {r.Min(v => v.Left):F0}..{r.Max(v => v.Right):F0}  y {r.Min(v => v.Top):F0}..{r.Max(v => v.Bottom):F0}";
    }

    public void BeginTextSelection(int pageIndex, double x, double y)
    {
        ClearSelection();

        var layer = TextLayerFor(pageIndex);
        if (layer is null)
        {
            return;
        }

        int index = layer.HitTestNearest(x, y);
        if (index < 0)
        {
            return;
        }

        _selection = DocumentSelection.At(pageIndex, index);
        _isSelecting = true;
        RefreshSelectionRects();
    }

    /// <summary>
    /// Extends the selection to a point on <paramref name="pageIndex"/>, which
    /// may be a DIFFERENT page from where the drag started. The anchor stays
    /// put, so dragging up the document works the same as dragging down.
    /// </summary>
    public void UpdateTextSelection(int pageIndex, double x, double y)
    {
        if (!_isSelecting || _selection is not DocumentSelection current)
        {
            return;
        }

        var layer = TextLayerFor(pageIndex);
        if (layer is null)
        {
            return;
        }

        int index = layer.HitTestNearest(x, y);
        if (index < 0)
        {
            return;
        }

        _selection = current.ExtendTo(pageIndex, index);
        RefreshSelectionRects();
    }

    public void EndTextSelection()
    {
        _isSelecting = false;

        if (ActiveTool != ToolMode.Highlight || _selection is not DocumentSelection selection)
        {
            return;
        }

        // A selection spanning pages becomes ONE highlight per page. Annotation
        // coordinates are page-local, and burning writes into a single page's
        // content stream, so a highlight that straddles a page boundary has no
        // meaningful single-page representation.
        var created = new List<HighlightAnnotation>();
        var (firstPage, lastPage) = selection.PageRange;

        for (int page = firstPage; page <= lastPage; page++)
        {
            var layer = TextLayerFor(page);
            if (layer is null)
            {
                continue;
            }

            if (selection.RangeForPage(page, layer.CharCount) is not (int start, int length))
            {
                continue;
            }

            var rects = layer.GetRangeRects(start, length);
            if (rects.Count == 0)
            {
                continue;
            }

            created.Add(new HighlightAnnotation(page, rects.Select(NormRect).ToList(), HighlightColorHex));
        }

        if (created.Count == 0)
        {
            return;
        }

        // One undo step for the whole gesture, not one per page.
        PushHistory(HistoryScope.Annotations, created.Count > 1 ? "Highlight pages" : "Highlight");
        foreach (var highlight in created)
        {
            _allHighlights.Add(highlight);
            SlotFor(highlight.PageIndex)?.Highlights.Add(highlight);
            if (highlight.PageIndex == CurrentPageIndex)
            {
                Highlights.Add(highlight);
            }
        }

        IsDirty = true;
        ClearSelection();
    }

    /// <summary>The selected text, joined across pages with a newline at each seam.</summary>
    public string? GetSelectedText()
    {
        if (_selection is not DocumentSelection selection)
        {
            return null;
        }

        var parts = new List<string>();
        var (firstPage, lastPage) = selection.PageRange;

        for (int page = firstPage; page <= lastPage; page++)
        {
            var layer = TextLayerFor(page);
            if (layer is null)
            {
                continue;
            }

            if (selection.RangeForPage(page, layer.CharCount) is (int start, int length))
            {
                parts.Add(layer.Text.Substring(start, length));
            }
        }

        return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : null;
    }

    /// <summary>
    /// Recomputes selection rectangles for every page the selection touches
    /// and hands each page's share to its own card.
    /// </summary>
    private void RefreshSelectionRects()
    {
        foreach (var slot in PageSlots)
        {
            slot.SelectionRects.Clear();
        }
        SelectionRects.Clear();

        if (_selection is not DocumentSelection selection)
        {
            return;
        }

        var (firstPage, lastPage) = selection.PageRange;
        for (int page = firstPage; page <= lastPage; page++)
        {
            var layer = TextLayerFor(page);
            if (layer is null)
            {
                continue;
            }

            if (selection.RangeForPage(page, layer.CharCount) is not (int start, int length))
            {
                continue;
            }

            var slot = SlotFor(page);
            foreach (var rect in layer.GetRangeRects(start, length))
            {
                var normalized = NormRect(rect);
                slot?.SelectionRects.Add(normalized);
                if (page == CurrentPageIndex)
                {
                    SelectionRects.Add(normalized);
                }
            }
        }
    }

    private void ClearSelection()
    {
        _selection = null;
        _isSelecting = false;
        SelectionRects.Clear();
        foreach (var slot in PageSlots)
        {
            slot.SelectionRects.Clear();
        }
    }

    // ---------------- Ink / notes ----------------

    // Ink points are normalized on the way IN, not on commit, so the live
    // preview MainPage draws from _currentStroke sits in the same coordinate
    // space as the finished strokes and the two cannot disagree.
    /// <summary>
    /// The page a stroke started on, held for the whole gesture so a stroke
    /// that wanders past a page edge still belongs to the page it began on
    /// rather than jumping to whichever page the pointer ended over.
    /// </summary>
    private int _inkPageIndex;

    public void BeginInkStroke(int pageIndex, double x, double y)
    {
        _inkPageIndex = pageIndex;
        _currentStroke = new List<(double, double)> { (Norm(x), Norm(y)) };
        InkStrokeChanged?.Invoke();
    }

    public void ExtendInkStroke(double x, double y)
    {
        if (_currentStroke is null)
        {
            return;
        }

        _currentStroke.Add((Norm(x), Norm(y)));
        InkStrokeChanged?.Invoke();
    }

    /// <summary>Pen colour for new ink strokes.</summary>
    [ObservableProperty]
    public partial string InkColorHex { get; set; } = InkPresets.DefaultColor.Hex;

    /// <summary>Pen width for new ink strokes, in normalized page units.</summary>
    [ObservableProperty]
    public partial double InkWidth { get; set; } = InkPresets.DefaultWidth.Value;

    /// <summary>Fill colour for new highlights.</summary>
    [ObservableProperty]
    public partial string HighlightColorHex { get; set; } = InkPresets.DefaultHighlightColor.Hex;

    public void EndInkStroke()
    {
        if (_currentStroke is { Count: > 1 })
        {
            PushHistory(HistoryScope.Annotations, "Draw");
            var stroke = new InkStrokeAnnotation(
                _inkPageIndex,
                new List<(double X, double Y)>(_currentStroke),
                InkColorHex,
                InkWidth);
            _allInkStrokes.Add(stroke);
            InkStrokes.Add(stroke);
            SlotFor(stroke.PageIndex)?.InkStrokes.Add(stroke);
            IsDirty = true;
        }

        _currentStroke = null;
        InkStrokeChanged?.Invoke();
    }

    /// <summary>
    /// Removes a specific note, as one undo step. Reached from the note's own
    /// flyout: unlike highlights and ink, a note marker is a button whose
    /// click opens the editor, so the Select-tool click-to-pick path can never
    /// reach it and it needs its own way to die.
    /// </summary>
    public void DeleteNote(NoteAnnotation note)
    {
        if (!_allNotes.Contains(note))
        {
            return;
        }

        PushHistory(HistoryScope.Annotations, "Delete note");
        _allNotes.Remove(note);
        Notes.Remove(note);
        SlotFor(note.PageIndex)?.Notes.Remove(note);
        IsDirty = true;
    }

    public void AddNoteAt(int pageIndex, double x, double y)
    {
        PushHistory(HistoryScope.Annotations, "Add note");
        var note = new NoteAnnotation(pageIndex, Norm(x), Norm(y), string.Empty) { Scale = SlotLayoutWidth };
        _allNotes.Add(note);
        Notes.Add(note);
        SlotFor(note.PageIndex)?.Notes.Add(note);
        IsDirty = true;
    }

    // ---------------- Undo / redo ----------------

    private readonly DocumentHistory _history = new();

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>Menu text, e.g. "Undo Delete page". Falls back to plain "Undo".</summary>
    public string UndoLabel => _history.NextUndoLabel is { } l ? $"Undo {l}" : "Undo";

    public string RedoLabel => _history.NextRedoLabel is { } l ? $"Redo {l}" : "Redo";

    /// <summary>True when there are edits not yet written to disk.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>
    /// Visibility of the "unsaved" marker. Exposed as a Visibility rather than
    /// binding IsDirty through a converter, to keep the XAML converter-free
    /// like the rest of this view.
    /// </summary>
    public Visibility DirtyIndicatorVisibility =>
        IsDirty ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsDirtyChanged(bool value) =>
        OnPropertyChanged(nameof(DirtyIndicatorVisibility));

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }

    /// <summary>
    /// Snapshots the state an undo step would restore. Annotation scope copies
    /// the three overlay lists; document scope serializes the whole PDF, which
    /// is the only way to reverse a page delete or rotation.
    /// </summary>
    private HistoryEntry Capture(HistoryScope scope, string label)
    {
        byte[]? bytes = null;
        if (scope == HistoryScope.Document && _documentHandle != 0)
        {
            var buffer = RenderCoreNative.snapshot_document(_documentHandle);
            if (buffer.Status == RenderStatus.OkPdfium && buffer.Data != IntPtr.Zero && buffer.Len > 0)
            {
                bytes = new byte[(int)buffer.Len];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            }
            RenderCoreNative.free_byte_buffer(buffer);
        }

        return new HistoryEntry
        {
            Scope = scope,
            Label = label,
            WasDirty = IsDirty,
            PageIndex = CurrentPageIndex,
            DocumentBytes = bytes,
            // Highlights and ink are immutable records, so copying the list is
            // a real snapshot. Notes are mutable (their text is edited after
            // creation), so their VALUES are captured instead.
            Highlights = _allHighlights.ToList(),
            InkStrokes = _allInkStrokes.ToList(),
            Notes = _allNotes.Select(n => new NoteState(n.PageIndex, n.X, n.Y, n.Text)).ToList(),
        };
    }

    /// <summary>Records the pre-edit state. Call immediately BEFORE mutating.</summary>
    private void PushHistory(HistoryScope scope, string label)
    {
        _history.Push(Capture(scope, label));
        NotifyHistoryChanged();
    }

    public void Undo()
    {
        if (_history.Undo(Capture) is { } target)
        {
            ApplyHistoryEntry(target);
        }
    }

    public void Redo()
    {
        if (_history.Redo(Capture) is { } target)
        {
            ApplyHistoryEntry(target);
        }
    }

    /// <summary>
    /// The single apply path shared by undo and redo. Restores whatever the
    /// entry holds; an asymmetry between the two directions is not expressible
    /// because neither has its own restore code.
    /// </summary>
    private void ApplyHistoryEntry(HistoryEntry entry)
    {
        if (entry.Scope == HistoryScope.Document && entry.DocumentBytes is { Length: > 0 })
        {
            ulong restored = RenderCoreNative.open_document_from_bytes(
                entry.DocumentBytes, (nuint)entry.DocumentBytes.Length);

            if (restored != 0)
            {
                CloseCurrentDocument();
                _documentHandle = restored;

                // A document-scope undo can restore a different page count and
                // ordering, so index-keyed layers are no longer trustworthy.
                _textLayers.Clear();
                ClearSelection();

                PageCount = Math.Max(0, RenderCoreNative.get_page_count(_documentHandle));
                Thumbnails.Clear();
                for (int i = 0; i < PageCount; i++)
                {
                    Thumbnails.Add(new PageThumbnail(i));
                }
            }
        }

        _allHighlights.Clear();
        _allHighlights.AddRange(entry.Highlights);
        _allInkStrokes.Clear();
        _allInkStrokes.AddRange(entry.InkStrokes);
        _allNotes.Clear();
        _allNotes.AddRange(entry.Notes.Select(
            n => new NoteAnnotation(n.PageIndex, n.X, n.Y, n.Text) { Scale = SlotLayoutWidth }));

        CurrentPageIndex = Math.Clamp(entry.PageIndex, 0, Math.Max(0, PageCount - 1));
        IsDirty = entry.WasDirty;

        RefreshAnnotationsForCurrentPage();
        RenderCurrentPage();
        NotifyHistoryChanged();
    }

    private void RefreshAnnotationsForCurrentPage()
    {
        // The continuous viewport draws marks per page card, so any change to
        // the flat lists has to be fanned back out to the slots.
        DistributeAnnotationsToSlots();

        Highlights.Clear();
        foreach (var h in _allHighlights.Where(h => h.PageIndex == CurrentPageIndex))
        {
            Highlights.Add(h);
        }

        Notes.Clear();
        foreach (var n in _allNotes.Where(n => n.PageIndex == CurrentPageIndex))
        {
            Notes.Add(n);
        }

        InkStrokes.Clear();
        foreach (var st in _allInkStrokes.Where(st => st.PageIndex == CurrentPageIndex))
        {
            InkStrokes.Add(st);
        }
    }

    // ---------------- Search ----------------

    partial void OnSearchQueryChanged(string value) => RecomputeSearchMatches();

    /// <summary>
    /// Highlights every match across the pages currently laid out, not just
    /// the current one.
    ///
    /// With a continuous view several pages are on screen at once, so a
    /// per-page search would leave visible matches unmarked. Search is capped
    /// at <see cref="SearchPageBudget"/> pages so a query on a 300-page
    /// document does not extract every text layer on the UI thread; pages
    /// nearest the viewport are searched first, since those are the ones whose
    /// highlights the user can actually see.
    /// </summary>
    private const int SearchPageBudget = 40;

    /// <summary>Pages holding at least one match, ascending, for step navigation.</summary>
    private readonly List<int> _matchPages = new();

    private int _currentMatch = -1;

    /// <summary>"3 of 12 pages", or empty when there is no active search.</summary>
    public string SearchStatus =>
        string.IsNullOrEmpty(SearchQuery) ? string.Empty
        : _matchPages.Count == 0 ? "No matches"
        : $"{Math.Max(0, _currentMatch) + 1} of {_matchPages.Count}";

    public bool HasSearchMatches => _matchPages.Count > 0;

    /// <summary>
    /// Moves to the next or previous page containing a match and asks the view
    /// to scroll there, wrapping at the ends.
    ///
    /// Search previously highlighted matches but could not navigate to them,
    /// so a hit on a page you were not already looking at was invisible.
    /// </summary>
    public void StepSearchMatch(int direction)
    {
        if (_matchPages.Count == 0)
        {
            return;
        }

        _currentMatch = _currentMatch < 0
            ? (direction >= 0 ? 0 : _matchPages.Count - 1)
            : (_currentMatch + direction + _matchPages.Count) % _matchPages.Count;

        OnPropertyChanged(nameof(SearchStatus));
        GoToPage(_matchPages[_currentMatch]);
    }

    private void RecomputeSearchMatches()
    {
        SearchMatchRects.Clear();
        foreach (var slot in PageSlots)
        {
            slot.SearchMatchRects.Clear();
        }

        _matchPages.Clear();
        _currentMatch = -1;

        if (string.IsNullOrEmpty(SearchQuery) || PageSlots.Count == 0)
        {
            OnPropertyChanged(nameof(SearchStatus));
            OnPropertyChanged(nameof(HasSearchMatches));
            return;
        }

        // Nearest-first ordering: walk outwards from the current page.
        var order = new List<int> { CurrentPageIndex };
        for (int d = 1; order.Count < Math.Min(SearchPageBudget, PageSlots.Count); d++)
        {
            if (CurrentPageIndex - d >= 0)
            {
                order.Add(CurrentPageIndex - d);
            }
            if (CurrentPageIndex + d < PageSlots.Count)
            {
                order.Add(CurrentPageIndex + d);
            }
            if (CurrentPageIndex - d < 0 && CurrentPageIndex + d >= PageSlots.Count)
            {
                break;
            }
        }

        foreach (int page in order)
        {
            var layer = TextLayerFor(page);
            if (layer is null)
            {
                continue;
            }

            var slot = SlotFor(page);
            bool pageHasMatch = false;

            foreach (var (start, length) in layer.FindMatches(SearchQuery))
            {
                pageHasMatch = true;
                foreach (var rect in layer.GetRangeRects(start, length))
                {
                    var normalized = NormRect(rect);
                    slot?.SearchMatchRects.Add(normalized);
                    if (page == CurrentPageIndex)
                    {
                        SearchMatchRects.Add(normalized);
                    }
                }
            }

            if (pageHasMatch)
            {
                _matchPages.Add(page);
            }
        }

        // Pages were visited nearest-first for responsiveness, but stepping
        // through matches has to run in document order or Next would jump
        // backwards and forwards unpredictably.
        _matchPages.Sort();

        OnPropertyChanged(nameof(SearchStatus));
        OnPropertyChanged(nameof(HasSearchMatches));
    }

    /// <summary>
    /// Text layers by page, loaded on demand.
    ///
    /// A selection that spans pages needs several layers live at once, so this
    /// cannot be the single current-page layer it used to be. Extraction is
    /// cheap next to rasterization and a layer is a few thousand glyph boxes,
    /// so they are simply kept for the document's lifetime rather than
    /// evicted; opening another document clears the lot.
    /// </summary>
    private readonly Dictionary<int, PageTextLayer?> _textLayers = new();

    /// <summary>
    /// The text layer for a page, extracted in SLOT space on first use.
    /// Returns null for a page with no text, and caches that too so an
    /// image-only page is not re-extracted on every pointer move.
    /// </summary>
    private PageTextLayer? TextLayerFor(int pageIndex)
    {
        if (_documentHandle == 0 || pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        if (_textLayers.TryGetValue(pageIndex, out var cached))
        {
            return cached;
        }

        var layer = TextLayerLoader.Load(_documentHandle, pageIndex, (int)SlotLayoutWidth);
        _textLayers[pageIndex] = layer;
        return layer;
    }

    /// <summary>Loads the current page's layer and refreshes its search hits.</summary>
    private void EnsureTextLayer()
    {
        if (TextLayerFor(CurrentPageIndex) is not null)
        {
            RecomputeSearchMatches();
        }
    }

    // ---------------- Rendering ----------------

    /// <summary>
    /// Resets the per-page state that follows the current page.
    ///
    /// This used to also rasterize the page synchronously on the UI thread
    /// into <c>PageBitmap</c> and kick the old single-page high-res pipeline.
    /// Nothing binds PageBitmap any more (the continuous viewport draws from
    /// each slot's own bitmap), so that was a blocking PDFium render on every
    /// page change and every document open, producing an image no one ever
    /// saw. Page pixels are entirely the slot renderer's job now.
    /// </summary>
    private void RenderCurrentPage()
    {
        ClearSelection();
        SearchMatchRects.Clear();
        RefreshAnnotationsForCurrentPage();
    }

    /// <summary>
    /// Display scale (1.0 at 100%, 1.5 at 150%), supplied by MainPage from
    /// XamlRoot.RasterizationScale.
    /// </summary>
    public double RasterizationScale { get; set; } = 1.0;

    private void CloseCurrentDocument()
    {
        // The sharpen pass is the only timer this pipeline still owns.
        _sharpenTimer?.Stop();

        if (_documentHandle != 0)
        {
            RenderCoreNative.close_document(_documentHandle);
            _documentHandle = 0;
        }
    }

    public void Dispose() => CloseCurrentDocument();
}
