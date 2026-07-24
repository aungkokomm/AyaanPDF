using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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

    private PageTextLayer? _textLayer;
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

    /// <summary>Highlight rects for the current text selection, in the same render-pixel space as PageBitmap.</summary>
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

    /// <summary>Fires on every point added to the in-progress ink stroke, so MainPage can redraw its live preview.</summary>
    public event Action? InkStrokeChanged;

    /// <summary>
    /// Layout size of the page in DIPs. The Image is pinned to this, so a
    /// sharper re-render fills the SAME box with more pixels instead of
    /// changing the element's size.
    ///
    /// This decoupling is the whole point: layout size and render resolution
    /// used to be the same number (Stretch="None" on a raw bitmap), so every
    /// re-render resized the content, which moved the fit reference, which
    /// moved the zoom readout, and forced a counter-scale to stop the page
    /// visibly jumping. Fit Width could never win against that. Now
    /// ZoomFactor 1.0 always means "page spans the viewport width" and
    /// nothing the renderer does can disturb it.
    /// </summary>
    [ObservableProperty]
    public partial double PageLayoutWidth { get; set; }

    [ObservableProperty]
    public partial double PageLayoutHeight { get; set; }

    /// <summary>Raised when a new page's layout size is established, so MainPage can reset the zoom to fit.</summary>
    public event Action? PageLayoutEstablished;

    /// <summary>
    /// DIPs per bitmap pixel. Text-layer boxes, selection rects and ink
    /// strokes are all in bitmap-pixel space, but the page is now laid out in
    /// DIPs, so overlays scale by this and pointer coordinates divide by it.
    /// Changes whenever a sharper render lands.
    /// </summary>
    public double ContentToLayoutScale =>
        _currentRenderedWidth > 0 && PageLayoutWidth > 0 ? PageLayoutWidth / _currentRenderedWidth : 1.0;

    /// <summary>Raised when <see cref="ContentToLayoutScale"/> changes, so MainPage can restretch the overlays.</summary>
    public event Action? ContentScaleChanged;

    // ---------------- Document lifecycle ----------------

    public ViewportViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        OpenDocument(Path.Combine(AppContext.BaseDirectory, "sample.pdf"));
    }

    /// <summary>Re-opens the bundled sample document (the "Re-render" button).</summary>
    [RelayCommand]
    private void OpenAndRenderSampleDocument() => OpenDocument(Path.Combine(AppContext.BaseDirectory, "sample.pdf"));

    /// <summary>Opens any PDF by path - the File&gt;Open entry point.</summary>
    public void OpenDocument(string path)
    {
        CloseCurrentDocument();

        _documentHandle = RenderCoreNative.open_document(path);

        Thumbnails.Clear();
        // Annotations are keyed by page index only, so carrying them across a
        // document switch would misattach them to whatever page shares that
        // index in the new file.
        _allHighlights.Clear();
        _allNotes.Clear();
        _allInkStrokes.Clear();
        Highlights.Clear();
        Notes.Clear();
        InkStrokes.Clear();

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
        RenderCurrentPage();
    }

    /// <summary>Navigates to a page (e.g. a thumbnail click).</summary>
    public void GoToPage(int pageIndex)
    {
        if (_documentHandle == 0 || pageIndex < 0 || pageIndex >= PageCount || pageIndex == CurrentPageIndex)
        {
            return;
        }

        CurrentPageIndex = pageIndex;
        RenderCurrentPage();
    }

    // ---------------- Page operations ----------------

    public void RotateCurrentPage(int degrees)
    {
        if (_documentHandle == 0 ||
            RenderCoreNative.rotate_page(_documentHandle, CurrentPageIndex, degrees) != RenderStatus.OkPdfium)
        {
            return;
        }

        var thumb = PageRenderer.RenderLowRes(_documentHandle, CurrentPageIndex, ThumbnailWidth);
        Thumbnails[CurrentPageIndex].Bitmap = thumb.Bitmap;
        RenderCurrentPage();
    }

    /// <summary>Deletes the current page. Refuses to delete the last remaining page.</summary>
    public void DeleteCurrentPage()
    {
        if (_documentHandle == 0 || PageCount <= 1 ||
            RenderCoreNative.delete_page(_documentHandle, CurrentPageIndex) != RenderStatus.OkPdfium)
        {
            return;
        }

        PageCount--;

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

    public bool SaveDocumentAs(string path) =>
        _documentHandle != 0 && RenderCoreNative.save_document(_documentHandle, path) == RenderStatus.OkPdfium;

    // ---------------- AcroForm ----------------

    public int FormFieldCount =>
        _documentHandle != 0 ? Math.Max(0, RenderCoreNative.get_form_field_count(_documentHandle)) : 0;

    public bool FillFormField(string fieldName, string value) =>
        _documentHandle != 0 && RenderCoreNative.fill_text_field(_documentHandle, fieldName, value) == RenderStatus.OkPdfium;

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

        thumbnail.Bitmap = PageRenderer.ToBitmap(raw).Bitmap;
        thumbnail.EndRender();
    }

    // ---------------- Text selection ----------------

    public void BeginTextSelection(double x, double y)
    {
        ClearSelection();
        if (_textLayer is null)
        {
            return;
        }

        int index = _textLayer.HitTestNearest(x, y);
        if (index < 0)
        {
            return;
        }

        _selectionAnchorCharIndex = index;
        SetSelectionRange(index, index);
    }

    public void UpdateTextSelection(double x, double y)
    {
        if (_textLayer is null || _selectionAnchorCharIndex is not int anchor)
        {
            return;
        }

        int current = _textLayer.HitTestNearest(x, y);
        if (current < 0)
        {
            return;
        }

        SetSelectionRange(Math.Min(anchor, current), Math.Max(anchor, current));
    }

    public void EndTextSelection()
    {
        _selectionAnchorCharIndex = null;

        if (ActiveTool != ToolMode.Highlight || _textLayer is null || _selectionLength <= 0)
        {
            return;
        }

        var rects = _textLayer.GetRangeRects(_selectionStart, _selectionLength);
        if (rects.Count == 0)
        {
            return;
        }

        var highlight = new HighlightAnnotation(CurrentPageIndex, rects, "#FFFF00");
        _allHighlights.Add(highlight);
        Highlights.Add(highlight);
        ClearSelection();
    }

    public string? GetSelectedText() =>
        _textLayer is not null && _selectionLength > 0
            ? _textLayer.Text.Substring(_selectionStart, _selectionLength)
            : null;

    private void SetSelectionRange(int startIndex, int endIndexInclusive)
    {
        _selectionStart = startIndex;
        _selectionLength = endIndexInclusive - startIndex + 1;

        SelectionRects.Clear();
        if (_textLayer is null)
        {
            return;
        }

        foreach (var rect in _textLayer.GetRangeRects(_selectionStart, _selectionLength))
        {
            SelectionRects.Add(rect);
        }
    }

    private void ClearSelection()
    {
        _selectionAnchorCharIndex = null;
        _selectionStart = 0;
        _selectionLength = 0;
        SelectionRects.Clear();
    }

    // ---------------- Ink / notes ----------------

    public void BeginInkStroke(double x, double y)
    {
        _currentStroke = new List<(double, double)> { (x, y) };
        InkStrokeChanged?.Invoke();
    }

    public void ExtendInkStroke(double x, double y)
    {
        if (_currentStroke is null)
        {
            return;
        }

        _currentStroke.Add((x, y));
        InkStrokeChanged?.Invoke();
    }

    public void EndInkStroke()
    {
        if (_currentStroke is { Count: > 1 })
        {
            var stroke = new InkStrokeAnnotation(CurrentPageIndex, _currentStroke, "#FFE00000", 2.0);
            _allInkStrokes.Add(stroke);
            InkStrokes.Add(stroke);
        }

        _currentStroke = null;
        InkStrokeChanged?.Invoke();
    }

    public void AddNoteAt(double x, double y)
    {
        var note = new NoteAnnotation(CurrentPageIndex, x, y, string.Empty);
        _allNotes.Add(note);
        Notes.Add(note);
    }

    private void RefreshAnnotationsForCurrentPage()
    {
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

    private void RecomputeSearchMatches()
    {
        SearchMatchRects.Clear();
        if (_textLayer is null || string.IsNullOrEmpty(SearchQuery))
        {
            return;
        }

        foreach (var (start, length) in _textLayer.FindMatches(SearchQuery))
        {
            foreach (var rect in _textLayer.GetRangeRects(start, length))
            {
                SearchMatchRects.Add(rect);
            }
        }
    }

    private void RefreshTextLayer(int renderedWidth)
    {
        _textLayer = TextLayerLoader.Load(_documentHandle, CurrentPageIndex, renderedWidth);
        ClearSelection();
        RecomputeSearchMatches();
    }

    // ---------------- Rendering ----------------

    private void RenderCurrentPage()
    {
        _hasEstablishedInitialView = false;
        _currentRenderedWidth = 0;
        _textLayer = null;
        ClearSelection();
        SearchMatchRects.Clear();
        RefreshAnnotationsForCurrentPage();

        var low = PageRenderer.RenderLowRes(_documentHandle, CurrentPageIndex, LowResWidth);
        PageBitmap = low.Bitmap;
        Status = Describe("low-res", low.Outcome, low.Bitmap);
        Log("low-res", low.Outcome, low.Bitmap);

        if (_viewportSizeKnown)
        {
            RequestInitialFitRender();
        }
    }

    /// <summary>
    /// Fixes the page's on-screen box, in DIPs, to span the viewport width at
    /// ZoomFactor 1.0. Only called when the page itself changes — never on a
    /// resolution change — so the zoom reference stays put.
    /// </summary>
    private void EstablishLayoutSize(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        double aspect = (double)pixelHeight / pixelWidth;
        double width = _viewportWidth > 0 ? _viewportWidth : pixelWidth;

        PageLayoutWidth = width;
        PageLayoutHeight = width * aspect;
        PageLayoutEstablished?.Invoke();
    }

    /// <summary>Viewport size in DIPs, pushed in by MainPage from the ScrollView.</summary>
    public void SetViewportSize(double width, double height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        bool nowKnown = width > 0 && height > 0;

        if (!_viewportSizeKnown && nowKnown && _documentHandle != 0 && !_hasEstablishedInitialView)
        {
            _viewportSizeKnown = true;
            RequestInitialFitRender();
            return;
        }

        _viewportSizeKnown = nowKnown;
    }

    /// <summary>
    /// Called on every ScrollView view change. Debounced, so a zoom gesture
    /// schedules exactly one high-res render once it settles.
    /// </summary>
    public void OnViewportZoomChanged(double zoomFactor)
    {
        _currentZoomFactor = zoomFactor;
        ScheduleRenderSettle();
    }

    // ---------------- Debounced full-res re-render ----------------

    private void ScheduleRenderSettle()
    {
        _renderSettleTimer ??= _dispatcherQueue.CreateTimer();
        _renderSettleTimer.Interval = RenderSettleDelay;
        _renderSettleTimer.IsRepeating = false;
        _renderSettleTimer.Tick -= OnRenderSettleTick;
        _renderSettleTimer.Tick += OnRenderSettleTick;
        _renderSettleTimer.Stop();
        _renderSettleTimer.Start();
    }

    private void OnRenderSettleTick(DispatcherQueueTimer sender, object args) => RequestHighResAtCurrentZoom();

    /// <summary>
    /// The very first high-res request for a freshly opened document (or a
    /// freshly navigated-to page): sized to the viewport (a "fit width"
    /// default) rather than to whatever tiny footprint the low-res bitmap
    /// currently occupies on screen.
    /// </summary>
    private void RequestInitialFitRender()
    {
        if (_documentHandle == 0)
        {
            return;
        }

        // force: this establishes the baseline for a new page, so the
        // "would this actually be sharper" gate must not suppress it.
        IssueHighResRequest(ToDeviceWidth(_viewportWidth), force: true);
    }

    /// <summary>Subsequent re-renders once interaction settles: sized to whatever the user has currently zoomed to.</summary>
    private void RequestHighResAtCurrentZoom()
    {
        if (_documentHandle == 0)
        {
            return;
        }

        // On-screen width in DIPs = the fixed layout box scaled by zoom. It no
        // longer depends on the current bitmap's pixel size, so re-rendering
        // cannot feed back into the geometry that decides the next render.
        double onScreenWidth = PageLayoutWidth * _currentZoomFactor;
        if (onScreenWidth <= 0)
        {
            return;
        }

        IssueHighResRequest(ToDeviceWidth(onScreenWidth));
    }

    /// <summary>
    /// Display scale (1.0 at 100%, 1.5 at 150%), supplied by MainPage from
    /// XamlRoot.RasterizationScale.
    /// </summary>
    public double RasterizationScale { get; set; } = 1.0;

    /// <summary>
    /// Converts a width in DIPs to physical pixels. Rendering at DIP width on
    /// a scaled display hands the compositor an undersized bitmap, which it
    /// then upscales — soft text that no amount of re-rendering fixes,
    /// because the render was never asked for the missing pixels.
    /// </summary>
    private int ToDeviceWidth(double dipWidth) =>
        (int)Math.Clamp(Math.Round(dipWidth * Math.Max(1.0, RasterizationScale)), MinRenderWidth, MaxRenderWidth);

    /// <summary>
    /// Width of the bitmap currently on screen, so a re-render can be skipped
    /// when it would not actually be sharper.
    /// </summary>
    private int _currentRenderedWidth;

    /// <summary>
    /// A re-render below this ratio of what is already displayed is not
    /// visibly sharper, so it is pure cost. Panning at a fixed zoom, or
    /// nudging the zoom a few percent, used to fire a full-page rasterization
    /// every time interaction settled.
    /// </summary>
    private const double ResharpenThreshold = 1.25;

    private void IssueHighResRequest(int targetWidth, bool force = false)
    {
        if (!force && _currentRenderedWidth > 0)
        {
            double ratio = (double)targetWidth / _currentRenderedWidth;
            // Skip only marginal *increases*; always honour a meaningful
            // shrink so zooming out releases the oversized bitmap.
            if (ratio < ResharpenThreshold && ratio > 1.0 / ResharpenThreshold)
            {
                return;
            }
        }

        // Give up on whatever the previous request was rather than leaving
        // it to complete unpolled — without this, render_core would keep
        // its result (a full bitmap) alive forever every time the viewport
        // moves on before a request finishes.
        if (_pendingHighResRequestId != 0)
        {
            RenderCoreNative.discard_request(_pendingHighResRequestId);
        }

        _pendingHighResRequestId = RenderCoreNative.request_high_res(_documentHandle, CurrentPageIndex, targetWidth);
        StartPolling();
    }

    private void StartPolling()
    {
        _pollTimer ??= _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick -= OnPollTick;
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void OnPollTick(DispatcherQueueTimer sender, object args)
    {
        var polled = PageRenderer.PollHighRes(_pendingHighResRequestId);
        if (polled.Outcome == PageRenderOutcome.Pending)
        {
            return;
        }

        sender.Stop();
        _pendingHighResRequestId = 0;

        if (polled.Outcome is PageRenderOutcome.RealPage or PageRenderOutcome.Placeholder && polled.Bitmap is not null)
        {
            bool isNewPage = !_hasEstablishedInitialView;

            PageBitmap = polled.Bitmap;
            _hasEstablishedInitialView = true;

            // Text layer coordinates only line up with the bitmap they were
            // extracted at, so refresh whenever the displayed bitmap changes
            // (a new page, or a resolution change from zooming).
            _currentRenderedWidth = polled.Bitmap.PixelWidth;
            RefreshTextLayer(polled.Bitmap.PixelWidth);

            if (isNewPage)
            {
                EstablishLayoutSize(polled.Bitmap.PixelWidth, polled.Bitmap.PixelHeight);
            }

            // The bitmap's pixel size changed, so overlay scaling must follow.
            ContentScaleChanged?.Invoke();
        }

        Status = Describe("high-res", polled.Outcome, polled.Bitmap);
        Log("high-res", polled.Outcome, polled.Bitmap);
    }

    private static string Describe(string tier, PageRenderOutcome outcome, WriteableBitmap? bitmap) => outcome switch
    {
        PageRenderOutcome.RealPage => $"{tier}: {bitmap!.PixelWidth}x{bitmap.PixelHeight} via PDFium",
        PageRenderOutcome.Placeholder => $"{tier}: placeholder (PDFium unavailable or page failed to load)",
        PageRenderOutcome.Cancelled => $"{tier}: superseded by a newer request",
        _ => $"{tier}: render failed",
    };

    private static void Log(string tier, PageRenderOutcome outcome, WriteableBitmap? bitmap) =>
        Debug.WriteLine($"[ViewportViewModel] tier={tier} outcome={outcome} size={bitmap?.PixelWidth}x{bitmap?.PixelHeight}");

    private void CloseCurrentDocument()
    {
        _pollTimer?.Stop();
        _renderSettleTimer?.Stop();

        if (_pendingHighResRequestId != 0)
        {
            RenderCoreNative.discard_request(_pendingHighResRequestId);
            _pendingHighResRequestId = 0;
        }

        if (_documentHandle != 0)
        {
            RenderCoreNative.close_document(_documentHandle);
            _documentHandle = 0;
        }
    }

    public void Dispose() => CloseCurrentDocument();
}
