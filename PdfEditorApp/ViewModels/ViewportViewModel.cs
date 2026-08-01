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
// ToolMode moved to PdfEditorApp.Viewport.ToolCatalog, so the tool rail and
// its shortcuts can be defined in one testable place.

public partial class ViewportViewModel : ObservableObject, IDisposable
{
    private const int LowResWidth = 200;
    private const int MinRenderWidth = 100;

    /// <summary>
    /// The thumbnail's on-screen width, in DIPs. Adjustable (Ctrl+scroll, or
    /// dragging the panel edge) between these bounds; the pane and the images
    /// size from it.
    /// </summary>
    [ObservableProperty]
    public partial double ThumbnailDisplayWidth { get; set; } = 126.0;

    public const double MinThumbnailWidth = 90.0;
    public const double MaxThumbnailWidth = 320.0;

    /// <summary>Display height, at US-Letter aspect so the card reads as a page.</summary>
    public double ThumbnailDisplayHeight => ThumbnailDisplayWidth * (11.0 / 8.5);

    /// <summary>The panel's width: the thumbnail plus room for its margins and the scrollbar.</summary>
    public double ThumbnailPaneWidth => ThumbnailDisplayWidth + 52;

    /// <summary>
    /// The pixel width thumbnails are RASTERIZED at: the display size times the
    /// monitor's scale, so a page is drawn at the resolution it is shown at
    /// rather than upscaled from a fixed small bitmap. This is what makes them
    /// crisp. Clamped so an extreme DPI or size cannot ask for a huge render.
    /// </summary>
    private int ThumbnailPixelWidth =>
        Math.Clamp((int)Math.Round(ThumbnailDisplayWidth * Math.Max(1.0, RasterizationScale)), 90, 900);

    /// <summary>
    /// Nudges the thumbnail size by a delta in DIPs, clamped. Ctrl+scroll and
    /// the panel's resize grip both go through here.
    /// </summary>
    public void AdjustThumbnailSize(double deltaDip) =>
        ThumbnailDisplayWidth = Math.Clamp(ThumbnailDisplayWidth + deltaDip, MinThumbnailWidth, MaxThumbnailWidth);

    private DispatcherQueueTimer? _thumbResharpenTimer;

    partial void OnThumbnailDisplayWidthChanged(double value)
    {
        OnPropertyChanged(nameof(ThumbnailDisplayHeight));
        OnPropertyChanged(nameof(ThumbnailPaneWidth));

        // Resize the cards right now so the panel tracks the drag/scroll
        // smoothly — the existing bitmap just scales to fit, which is cheap.
        foreach (var t in Thumbnails)
        {
            t.CardWidth = value;
        }

        // Re-rasterizing at the new resolution is the expensive part, so debounce
        // it: a drag or a burst of Ctrl+scroll ticks only triggers one sharpening
        // pass ~180ms after it settles, instead of one per pixel.
        _thumbResharpenTimer ??= CreateResharpenTimer();
        _thumbResharpenTimer.Stop();
        _thumbResharpenTimer.Start();
    }

    private DispatcherQueueTimer CreateResharpenTimer()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ResharpenVisibleThumbnails();
        return timer;
    }

    /// <summary>
    /// Drops the bitmaps of the thumbnails currently showing and re-renders them
    /// at the current <see cref="ThumbnailPixelWidth"/>, so a resize sharpens
    /// them rather than leaving the old lower-resolution bitmap scaled up. Ones
    /// not yet realized render fresh when scrolled to.
    /// </summary>
    private void ResharpenVisibleThumbnails()
    {
        var visible = new List<int>();
        foreach (var t in Thumbnails)
        {
            if (t.Bitmap is not null)
            {
                visible.Add(t.PageIndex);
                t.Bitmap = null;
            }
        }

        foreach (int i in visible)
        {
            EnsureThumbnailRendered(i);
        }
    }
    private readonly DispatcherQueue _dispatcherQueue;

    private ulong _documentHandle;
    private double _currentZoomFactor = 1.0;

    private readonly List<HighlightAnnotation> _allHighlights = new();
    private readonly List<NoteAnnotation> _allNotes = new();
    private readonly List<InkStrokeAnnotation> _allInkStrokes = new();
    private readonly List<ShapeAnnotation> _allShapes = new();
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

    public ObservableCollection<ShapeAnnotation> Shapes { get; } = new();

    /// <summary>The stroke currently being drawn (Draw tool, drag in progress), or null between strokes.</summary>
    public IReadOnlyList<(double X, double Y)>? CurrentStrokeInProgress => _currentStroke;

    /// <summary>
    /// Every ink stroke on every page. The ink canvas draws from THIS, not the
    /// current-page collection: the continuous viewport shows several pages at
    /// once, so rebuilding from only the current page made ink on a visible
    /// neighbouring page vanish the moment scrolling changed the current page.
    /// </summary>
    public IReadOnlyList<InkStrokeAnnotation> AllInkStrokes => _allInkStrokes;

    public IReadOnlyList<ShapeAnnotation> AllShapes => _allShapes;

    // ---------------- AcroForm filling ----------------
    //
    // The document's form-field widgets, read once on open. The app cannot make
    // PDFium render a field value written to /V (that path never regenerates the
    // widget appearance, proven in render_core), so "filling" a form here means
    // placing the app's OWN text-box annotations at the field rects the way the
    // user would place any text — which renders, saves and flattens. This first
    // slice fills TEXT fields; checkbox/radio/choice/signature are enumerated
    // (so the pane can report them) but not yet clickable.

    private readonly List<FormField> _formFields = new();

    /// <summary>Every form-field widget the open document carries.</summary>
    public IReadOnlyList<FormField> FormFields => _formFields;

    /// <summary>True when the document has any fillable (text) field.</summary>
    [ObservableProperty]
    public partial bool HasFillableForm { get; set; }

    /// <summary>The Fill Form toggle only exists when there is something to fill.</summary>
    public Visibility FillFormButtonVisibility =>
        HasFillableForm ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasFillableFormChanged(bool value) =>
        OnPropertyChanged(nameof(FillFormButtonVisibility));

    /// <summary>When on, fillable fields are outlined and a click opens the text editor on one.</summary>
    [ObservableProperty]
    public partial bool FormFillMode { get; set; }

    /// <summary>Reads the document's form fields. Cheap; called on open and after page-structure changes.</summary>
    private void LoadFormFields()
    {
        _formFields.Clear();
        HasFillableForm = false;
        if (_documentHandle == 0)
        {
            return;
        }

        var buf = RenderCoreNative.get_form_fields(_documentHandle);
        try
        {
            if (buf.Status == (int)RenderStatus.OkPdfium && buf.Data != IntPtr.Zero && buf.Len > 0)
            {
                byte[] bytes = new byte[(int)buf.Len];
                Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
                _formFields.AddRange(FormFieldReader.Parse(bytes));
            }
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buf);
        }

        HasFillableForm = _formFields.Any(f => f.IsFillable);
        if (!HasFillableForm)
        {
            FormFillMode = false;
        }
    }

    partial void OnFormFillModeChanged(bool value)
    {
        DistributeFormOutlines();
        if (value)
        {
            int n = _formFields.Count(f => f.IsFillable);
            Status = n == 1
                ? "Form fill: click the highlighted field to type."
                : $"Form fill: click any of the {n} highlighted fields to type.";
        }
    }

    /// <summary>Puts each fillable field's outline on its page slot, or clears them when fill mode is off.</summary>
    private void DistributeFormOutlines()
    {
        foreach (var slot in PageSlots)
        {
            slot.FormFieldOutlines.Clear();
        }

        if (!FormFillMode)
        {
            return;
        }

        foreach (var f in _formFields)
        {
            if (!f.IsFillable || f.PageIndex < 0 || f.PageIndex >= PageSlots.Count)
            {
                continue;
            }

            var slot = PageSlots[f.PageIndex];
            var rect = new TextRect(f.Left, f.Top, f.Right, f.Bottom);
            var scaled = ScaledRect.From(rect, slot.OverlayScale);
            if (scaled.IsVisible)
            {
                slot.FormFieldOutlines.Add(scaled);
            }
        }
    }

    /// <summary>
    /// The fillable text field under a normalized page-local point, or null. The
    /// click in fill mode opens the text editor on whatever this returns.
    /// </summary>
    public FormField? FillableFieldAt(int page, double normX, double normY)
    {
        foreach (var f in _formFields)
        {
            if (f.PageIndex == page && f.IsFillable
                && normX >= f.Left && normX <= f.Right
                && normY >= f.Top && normY <= f.Bottom)
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>
    /// Fills a form field with typed text: removes the field's widget (so its box
    /// stops drawing over the text — see PDFium rule 10) and places the text as a
    /// normal text-box annotation, all in one undo step. The field then drops out
    /// of the fillable set, so its outline disappears. Returns false on failure.
    /// </summary>
    public bool FillFormField(string fieldName, int page, double normLeft, double normTop,
                              double normRight, double normBottom,
                              string text, string colorHex, double fontSizeNorm)
    {
        if (_documentHandle == 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // One document snapshot covers both the widget removal and the text add,
        // so a single undo restores the empty interactive field.
        PushHistory(HistoryScope.Document, "Fill field");

        RenderCoreNative.delete_form_field_widget(_documentHandle, fieldName);

        bool placed = AddTextBoxNormalized(page, normLeft, normTop, normRight, normBottom,
                                           text, colorHex, fontSizeNorm);

        // The field is consumed: re-read so it no longer counts as fillable and
        // its outline is dropped.
        LoadFormFields();
        DistributeFormOutlines();
        return placed;
    }

    /// <summary>
    /// The shape being dragged out, or null. Redrawn on every pointer move, so
    /// the preview is the same polyline the finished shape will be and the two
    /// cannot disagree.
    /// </summary>
    public ShapeDraft? ShapeInProgress => _shapeDraft;

    /// <summary>
    /// The page the shape being dragged belongs to. The preview has to be
    /// anchored to it rather than to the current page: in continuous view a
    /// shape can be started on a visible page that is not the current one, and
    /// the preview must land where the shape will.
    /// </summary>
    public int ActiveShapePage => _shapePageIndex;

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
            _allShapes.Clear();
        }
        Highlights.Clear();
        Notes.Clear();
        InkStrokes.Clear();
        Shapes.Clear();

        // Layers are keyed by page index, so carrying them across a document
        // switch would hand the new document the old one's text.
        _textLayers.Clear();
        ClearLoadedAnnotations();

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
            Thumbnails.Add(new PageThumbnail(i) { CardWidth = ThumbnailDisplayWidth });
        }

        CurrentPageIndex = 0;
        RebuildContinuousLayout();
        RenderCurrentPage();
        ReportExistingAnnotations();
        LoadFormFields();
    }

    /// <summary>
    /// Forgets which annotations each page carried. Indices belong to a
    /// specific document, so carrying them across an open would address
    /// whatever now happens to sit at that position.
    /// </summary>
    private void ClearLoadedAnnotations()
    {
        _loadedByPage.Clear();
        _selectedLoaded = null;
        _loadedDrag = null;
    }

    /// <summary>
    /// Number of annotation objects the opened document already carries.
    /// </summary>
    public int ExistingAnnotationCount { get; private set; }

    /// <summary>
    /// Notes what markup the opened file already has.
    ///
    /// These already DRAW, because PDFium renders annotations as part of the
    /// page, so a file annotated in Acrobat or saved by this app now opens
    /// looking right. What is not yet possible is selecting and editing them:
    /// that needs each kind's own geometry, quad points for a highlight and
    /// the point list for a stroke, which reading their bounding boxes cannot
    /// supply. Counting them at least means the app knows they are there
    /// instead of silently treating the page as unmarked.
    /// </summary>
    private void ReportExistingAnnotations()
    {
        if (_documentHandle == 0 || PageCount == 0)
        {
            ExistingAnnotationCount = 0;
            return;
        }

        // Bounded: a 300-page document should not pay for a full sweep just to
        // put a number in the status bar.
        int scanned = Math.Min(PageCount, 25);
        ExistingAnnotationCount = Interop.AnnotationLoader.CountAll(_documentHandle, scanned);

        if (ExistingAnnotationCount > 0)
        {
            Diag.Log($"open: {ExistingAnnotationCount} existing annotations in the first {scanned} pages");
        }
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

        var thumb = PageRenderer.RenderLowRes(_documentHandle, CurrentPageIndex, ThumbnailPixelWidth);
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
            Thumbnails.Add(new PageThumbnail(i) { CardWidth = ThumbnailDisplayWidth });
        }

        if (CurrentPageIndex >= PageCount)
        {
            CurrentPageIndex = PageCount - 1;
        }

        RenderCurrentPage();
    }

    // ---------------- Page organising ----------------

    /// <summary>
    /// Rebuilds the document to a new sequence of its own pages: reorder,
    /// duplicate or delete, from a list of source page indices.
    ///
    /// The document is rebuilt in the core (pages keep their saved annotations),
    /// and the in-progress overlay marks, which are keyed by page index, are
    /// remapped so each stays on its page and a mark on a removed page is
    /// dropped. One undo step covers the whole thing.
    /// </summary>
    public bool RebuildPages(IReadOnlyList<int> order)
    {
        if (_documentHandle == 0 || order.Count == 0)
        {
            return false;
        }

        PushHistory(HistoryScope.Document, "Reorganize pages");

        int[] arr = order.ToArray();
        if (RenderCoreNative.rebuild_page_order(_documentHandle, arr, (nuint)arr.Length) != RenderStatus.OkPdfium)
        {
            Status = "Could not reorganize the pages.";
            return false;
        }

        MapOverlayPages(p => PageReorder.NewIndexOf(order, p));
        ReloadAfterPageStructureChange();
        IsDirty = true;
        return true;
    }

    /// <summary>Moves the page at <paramref name="from"/> to sit at <paramref name="to"/>.</summary>
    public bool MovePage(int from, int to) =>
        from != to && RebuildPages(PageReorder.Move(PageCount, from, to));

    /// <summary>
    /// Inserts every page of another PDF file at <paramref name="atIndex"/>. The
    /// overlay marks on pages at or after the insertion point shift down by the
    /// number of pages inserted.
    /// </summary>
    public bool InsertPagesFromFile(string path, int atIndex)
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(path);
        }
        catch
        {
            Status = "Could not read that file.";
            return false;
        }

        PushHistory(HistoryScope.Document, "Insert pages");
        int inserted = RenderCoreNative.insert_pages_from_bytes(
            _documentHandle, bytes, (nuint)bytes.Length, atIndex);
        if (inserted <= 0)
        {
            Status = "Could not insert those pages.";
            return false;
        }

        MapOverlayPages(p => p < atIndex ? p : p + inserted);
        ReloadAfterPageStructureChange();
        IsDirty = true;
        GoToPage(Math.Clamp(atIndex, 0, PageCount - 1));
        return true;
    }

    /// <summary>Inserts a blank page, sized to match the current page, at <paramref name="atIndex"/>.</summary>
    public bool InsertBlankPage(int atIndex)
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        var (w, h) = CurrentPageSizePoints();

        PushHistory(HistoryScope.Document, "Insert blank page");
        if (RenderCoreNative.insert_blank_page(_documentHandle, atIndex, (float)w, (float)h) != RenderStatus.OkPdfium)
        {
            Status = "Could not insert a blank page.";
            return false;
        }

        MapOverlayPages(p => p < atIndex ? p : p + 1);
        ReloadAfterPageStructureChange();
        IsDirty = true;
        GoToPage(Math.Clamp(atIndex, 0, PageCount - 1));
        return true;
    }

    /// <summary>Writes the given pages, in order, to a new PDF file. The document is unchanged.</summary>
    public bool ExtractPagesToFile(IReadOnlyList<int> indices, string path)
    {
        if (_documentHandle == 0 || indices.Count == 0)
        {
            return false;
        }

        return RenderCoreNative.extract_pages_to_file(
            _documentHandle, indices.ToArray(), (nuint)indices.Count, path) == RenderStatus.OkPdfium;
    }

    /// <summary>Deletes a set of pages at once, never emptying the document.</summary>
    public bool DeletePages(IReadOnlyList<int> indices)
    {
        var drop = new HashSet<int>(indices);
        var order = new List<int>();
        for (int i = 0; i < PageCount; i++)
        {
            if (!drop.Contains(i))
            {
                order.Add(i);
            }
        }

        return order.Count > 0 && order.Count < PageCount && RebuildPages(order);
    }

    /// <summary>The current page's size in points, or US Letter if it cannot be read.</summary>
    private (double W, double H) CurrentPageSizePoints()
    {
        var array = RenderCoreNative.get_page_sizes(_documentHandle);
        try
        {
            if (array.Status == RenderStatus.OkPdfium && array.Sizes != IntPtr.Zero
                && CurrentPageIndex >= 0 && CurrentPageIndex < (int)array.Len)
            {
                int stride = Marshal.SizeOf<NativePageSize>();
                var native = Marshal.PtrToStructure<NativePageSize>(array.Sizes + (CurrentPageIndex * stride));
                if (native.Width > 0 && native.Height > 0)
                {
                    return (native.Width, native.Height);
                }
            }
        }
        finally
        {
            RenderCoreNative.free_page_size_array(array);
        }

        return (612, 792); // US Letter
    }

    /// <summary>Inserts a copy of a page right after it.</summary>
    public bool DuplicatePage(int index) => RebuildPages(PageReorder.Duplicate(PageCount, index));

    /// <summary>Deletes a page by index. Refuses to remove the last remaining page.</summary>
    public bool DeletePage(int index) =>
        PageCount > 1 && RebuildPages(PageReorder.Delete(PageCount, index));

    /// <summary>Rotates one page by degrees, updating its thumbnail and the view if it is current.</summary>
    public void RotatePage(int index, int degrees)
    {
        if (_documentHandle == 0 || index < 0 || index >= PageCount)
        {
            return;
        }

        PushHistory(HistoryScope.Document, "Rotate page");

        if (RenderCoreNative.rotate_page(_documentHandle, index, degrees) != RenderStatus.OkPdfium)
        {
            return;
        }

        IsDirty = true;

        // A rotate changes the page's aspect, so the slot stack has to be laid
        // out again, not just re-rendered in place.
        RebuildContinuousLayout();

        var thumb = PageRenderer.RenderLowRes(_documentHandle, index, ThumbnailPixelWidth);
        if (index < Thumbnails.Count)
        {
            Thumbnails[index].Bitmap = thumb.Bitmap;
        }

        RenderCurrentPage();
    }

    /// <summary>
    /// Rotates a set of pages by the same amount, in one undo step, then
    /// re-lays out and re-renders once. The workhorse behind the Rotate Pages
    /// dialog; the single-page <see cref="RotatePage"/> is the quick path.
    /// </summary>
    public bool RotatePages(IReadOnlyList<int> indices, int degrees)
    {
        if (_documentHandle == 0 || indices.Count == 0)
        {
            return false;
        }

        PushHistory(HistoryScope.Document, "Rotate pages");

        foreach (int i in indices)
        {
            if (i >= 0 && i < PageCount)
            {
                RenderCoreNative.rotate_page(_documentHandle, i, degrees);
            }
        }

        IsDirty = true;

        // Rotating swaps a page's aspect, so the whole slot stack is laid out
        // again; then only the thumbnails that had already rendered are redrawn,
        // and the rest render fresh when the sidebar realizes them.
        RebuildContinuousLayout();
        foreach (int i in indices)
        {
            if (i >= 0 && i < Thumbnails.Count && Thumbnails[i].Bitmap is not null)
            {
                Thumbnails[i].Bitmap = PageRenderer.RenderLowRes(_documentHandle, i, ThumbnailPixelWidth).Bitmap;
            }
        }

        RenderCurrentPage();
        return true;
    }

    /// <summary>
    /// Each page's displayed orientation: true where it is wider than it is
    /// tall. Read from the laid-out slots, so a page already rotated reads by
    /// how it currently looks, which is what the orientation filter means.
    /// </summary>
    public IReadOnlyList<bool> PageIsLandscape()
    {
        var flags = new List<bool>(PageSlots.Count);
        foreach (var slot in PageSlots)
        {
            flags.Add(slot.SlotWidth > slot.SlotHeight);
        }

        return flags;
    }

    /// <summary>
    /// Remaps every overlay collection's page index through <paramref name="mapPage"/>,
    /// in place: it returns where a page moved to, or -1 to drop its marks. A
    /// reorder maps through the new order; an insert shifts pages after the
    /// insertion point.
    /// </summary>
    private void MapOverlayPages(Func<int, int> mapPage)
    {
        MapRecords(_allHighlights, mapPage, (h, p) => h with { PageIndex = p });
        MapRecords(_allInkStrokes, mapPage, (s, p) => s with { PageIndex = p });
        MapRecords(_allShapes, mapPage, (sh, p) => sh with { PageIndex = p });

        // Notes are a class, not a record, so they are rebuilt rather than
        // `with`-copied; Scale is carried across.
        var notes = new List<NoteAnnotation>(_allNotes.Count);
        foreach (var n in _allNotes)
        {
            int np = mapPage(n.PageIndex);
            if (np >= 0)
            {
                notes.Add(new NoteAnnotation(np, n.X, n.Y, n.Text) { Scale = n.Scale });
            }
        }

        _allNotes.Clear();
        _allNotes.AddRange(notes);
    }

    private static void MapRecords<T>(List<T> list, Func<int, int> mapPage, Func<T, int, T> withPage)
        where T : IAnnotation
    {
        var rebuilt = new List<T>(list.Count);
        foreach (var a in list)
        {
            int np = mapPage(a.PageIndex);
            if (np >= 0)
            {
                rebuilt.Add(withPage(a, np));
            }
        }

        list.Clear();
        list.AddRange(rebuilt);
    }

    /// <summary>
    /// The full reload after the page count or order changed: fresh thumbnails,
    /// a rebuilt slot stack, redistributed overlay, and a re-render.
    /// </summary>
    /// <summary>
    /// True while the thumbnail list is being rebuilt by a page operation, so
    /// the view's drag-reorder handler can tell the app's own Clear/Add of the
    /// Thumbnails collection apart from a user drag and not loop.
    /// </summary>
    public bool IsRebuildingPages { get; private set; }

    private void ReloadAfterPageStructureChange()
    {
        _textLayers.Clear();
        ClearLoadedAnnotations();
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));

        PageCount = Math.Max(0, RenderCoreNative.get_page_count(_documentHandle));

        IsRebuildingPages = true;
        try
        {
            Thumbnails.Clear();
            for (int i = 0; i < PageCount; i++)
            {
                Thumbnails.Add(new PageThumbnail(i) { CardWidth = ThumbnailDisplayWidth });
            }
        }
        finally
        {
            IsRebuildingPages = false;
        }

        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1));

        RebuildContinuousLayout();
        RefreshAnnotationsForCurrentPage();
        RenderCurrentPage();
        RefreshSelectionOutline();
        InkStrokeChanged?.Invoke();

        // Field rects are keyed by page index; a reorder/insert/delete moves
        // them, so re-read from the (rebuilt) document rather than remap.
        LoadFormFields();
        DistributeFormOutlines();
    }

    /// <summary>
    /// Path the current document was opened from, so it can be reloaded after
    /// a save that burned annotations into it.
    /// </summary>
    private string? _currentDocumentPath;

    /// <summary>
    /// Writes annotations into the document as real annotation OBJECTS, saves
    /// it to <paramref name="path"/>, then reloads from what was written.
    ///
    /// Objects rather than flattened pixels, which is the whole point: reopen
    /// the saved file and the marks are still marks. They can be moved,
    /// recoloured and deleted, other viewers see them as annotations, and a
    /// file annotated elsewhere opens here with its markup intact. Flattening
    /// is still available, but as a deliberate command rather than as the
    /// silent consequence of pressing Save.
    ///
    /// The reload is what stops a second save from writing the same marks
    /// again on top of the first set. Writing mutates the in-memory document
    /// while the overlay lists stay populated so the canvas keeps working, so
    /// without discarding that copy every subsequent save would stack another
    /// duplicate set.
    /// </summary>
    public bool SaveDocumentAs(string path) => SaveDocumentAs(path, flatten: false);

    /// <summary>
    /// As above, but <paramref name="flatten"/> burns the marks into page
    /// content instead, which is permanent and cannot be undone by reopening.
    /// </summary>
    public bool SaveDocumentAs(string path, bool flatten)
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        bool burned = flatten ? BurnAllAnnotations() : WriteAnnotationObjects();

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
    /// Writes every stored annotation into the document as a real annotation
    /// object. Returns false when there was nothing to write, so the caller
    /// can skip the reload.
    ///
    /// Coordinates are stored normalized; render_core wants them in the pixel
    /// space of some capture width, so both sides agree on a reference width
    /// and it cancels out.
    /// </summary>
    private bool WriteAnnotationObjects()
    {
        bool addedNotes = AddNoteAnnotations();

        if (_allHighlights.Count == 0 && _allInkStrokes.Count == 0 && _allShapes.Count == 0)
        {
            return addedNotes;
        }

        const int CaptureWidth = 1000;

        // One SPEC per highlight, however many lines it spans, so a multi-line
        // highlight stays a single object to click, recolour and delete.
        var specs = new List<HighlightSpec>();
        var quads = new List<HighlightQuad>();
        foreach (var h in _allHighlights)
        {
            if (h.Rects.Count == 0)
            {
                continue;
            }

            var (r, g, b, a) = ParseHex(h.ColorHex, defaultAlpha: 0x88);
            specs.Add(new HighlightSpec
            {
                PageIndex = h.PageIndex,
                QuadOffset = (uint)quads.Count,
                QuadCount = (uint)h.Rects.Count,
                R = r, G = g, B = b, A = a,
            });

            foreach (var rect in h.Rects)
            {
                quads.Add(new HighlightQuad
                {
                    Left = (float)(rect.Left * CaptureWidth),
                    Top = (float)(rect.Top * CaptureWidth),
                    Right = (float)(rect.Right * CaptureWidth),
                    Bottom = (float)(rect.Bottom * CaptureWidth),
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

        // Shapes cross as the drag's start and end, in capture-space pixels.
        // The core builds the outline and any arrowhead from those, so what
        // lands in the file comes from the same description the user drew
        // rather than from a flattened list of points.
        var shapes = new List<NativeShapeSpec>();
        foreach (var sh in _allShapes)
        {
            var (r, g, b, a) = ParseHex(sh.ColorHex, defaultAlpha: 0xFF);
            shapes.Add(new NativeShapeSpec
            {
                PageIndex = sh.PageIndex,
                Kind = (int)sh.Draft.Kind,
                X1 = (float)(sh.Draft.X1 * CaptureWidth),
                Y1 = (float)(sh.Draft.Y1 * CaptureWidth),
                X2 = (float)(sh.Draft.X2 * CaptureWidth),
                Y2 = (float)(sh.Draft.Y2 * CaptureWidth),
                R = r, G = g, B = b, A = a,
                WidthPx = (float)(sh.StrokeWidth * CaptureWidth),
            });
        }

        if (specs.Count == 0 && strokes.Count == 0 && shapes.Count == 0)
        {
            return addedNotes;
        }

        bool ok = true;

        if (specs.Count > 0)
        {
            int status = RenderCoreNative.add_highlight_annotations(
                _documentHandle, CaptureWidth,
                specs.ToArray(), (nuint)specs.Count,
                quads.ToArray(), (nuint)quads.Count);
            ok &= status == RenderStatus.OkPdfium;
            Diag.Log($"save: {specs.Count} highlight annotations ({quads.Count} quads) -> {status}");
        }

        if (shapes.Count > 0)
        {
            int status = RenderCoreNative.add_shape_annotations(
                _documentHandle, CaptureWidth, shapes.ToArray(), (nuint)shapes.Count);
            ok &= status == RenderStatus.OkPdfium;
            Diag.Log($"save: {shapes.Count} shape annotations -> {status}");
        }

        if (strokes.Count > 0)
        {
            int status = RenderCoreNative.add_ink_annotations(
                _documentHandle, CaptureWidth,
                strokes.ToArray(), (nuint)strokes.Count,
                points.ToArray(), (nuint)points.Count);
            ok &= status == RenderStatus.OkPdfium;
            Diag.Log($"save: {strokes.Count} ink annotations -> {status}");
        }

        return ok;
    }

    /// <summary>
    /// Burns every stored annotation into page content. Returns false when
    /// there was nothing to burn, so the caller can skip the reload.
    ///
    /// This is now only reached through the explicit Flatten command. Saving
    /// writes annotation OBJECTS instead, see <see cref="WriteAnnotationObjects"/>.
    /// </summary>
    private bool BurnAllAnnotations()
    {
        // Notes go first and by a different mechanism: they become real PDF
        // text annotations rather than flattened content, so their text
        // survives. See AddNoteAnnotations.
        bool addedNotes = AddNoteAnnotations();

        if (_allHighlights.Count == 0 && _allInkStrokes.Count == 0 && _allShapes.Count == 0)
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

        // Shapes burn as their own outlines, through the same stroke path as
        // ink. Without this, flattening a document silently DROPPED every
        // rectangle, ellipse, line and arrow on it, and the loss would only
        // show up in the saved copy.
        foreach (var sh in _allShapes)
        {
            var outline = sh.Outline;
            if (outline.Count < 2)
            {
                continue;
            }

            var (r, g, b, a) = ParseHex(sh.ColorHex, defaultAlpha: 0xFF);
            strokes.Add(new BurnStroke
            {
                PageIndex = sh.PageIndex,
                PointOffset = (uint)points.Count,
                PointCount = (uint)outline.Count,
                WidthPx = (float)(sh.StrokeWidth * CaptureWidth),
                R = r, G = g, B = b, A = a,
            });
            foreach (var (x, y) in outline)
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

    /// <summary>
    /// Packs an "#AARRGGBB" colour into 0xRRGGBBAA for the styled text-box FFI.
    /// An empty string is 0, which the core reads as "no fill / no outline".
    /// </summary>
    private static uint PackRgba(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return 0;
        }

        var (r, g, b, a) = ParseHex(hex, defaultAlpha: 0xFF);
        return ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
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
    private double _lastViewLeft;
    private double _lastViewRight;

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

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PagePositionLabel));
        OnPropertyChanged(nameof(TextAvailabilityLabel));
    }

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PagePositionLabel));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(TextAvailabilityLabel));
    }

    /// <summary>
    /// Drives rendering and release from the current scroll position.
    ///
    /// Offsets arrive in ZOOMED pixels and are divided back into slot space,
    /// which is the single mapping every pass here shares. Called on every
    /// view change, so it must stay cheap: the work is bounded by the number
    /// of visible pages, not the document length.
    /// </summary>
    public void UpdateVisibleWindow(
        double verticalOffset, double viewportHeight, double zoomFactor,
        double horizontalOffset = 0, double viewportWidth = 0)
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
        _lastViewLeft = horizontalOffset / zoom;
        _lastViewRight = (horizontalOffset + viewportWidth) / zoom;

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

        // try/finally, not a call to EndRender on each exit: an exception
        // between TryBeginRender and EndRender would leave the slot marked as
        // rendering forever, so it would never draw again. And async void
        // means an escaping exception is raised where nothing observes it and
        // takes the process down with no message, so it is caught and logged.
        try
        {
            ulong handle = _documentHandle;
            int pageIndex = slot.PageIndex;
            int width = _budget.BaseWidth;

            var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, width));

            // The document can be closed or replaced while a render is in flight.
            if (handle != _documentHandle)
            {
                return;
            }

            if (raw.Bgra is not null)
            {
                slot.SetBaseRender(PageRenderer.ToBitmap(raw).Bitmap, raw.Width);
                Diag.Log($"base {pageIndex}: {raw.Width}x{raw.Height} {raw.Outcome}");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"RenderBaseTier p{slot.PageIndex} failed: {ex}");
        }
        finally
        {
            slot.EndRender();
        }
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

            // Past the point where a whole-page render has to be capped, the
            // full-page tier can no longer keep up with the screen, so the
            // visible area is drawn from the tile pyramid instead. The sharp
            // render is dropped rather than kept underneath: it would be both
            // expensive and blurrier than the tiles covering it, and the cheap
            // base bitmap is a better stand-in for the moment before a tile
            // lands.
            if (_budget.NeedsTiles(slot.SlotWidth, _currentZoomFactor, RasterizationScale, aspect))
            {
                slot.DropSharpRender();
                RenderVisibleTiles(slot);
                continue;
            }

            slot.ClearTiles();

            int desired = _budget.SharpWidthFor(slot.SlotWidth, _currentZoomFactor, RasterizationScale, aspect);
            if (_budget.ShouldResharpen(slot.RenderedWidth, desired))
            {
                SharpenSlot(slot, desired);
            }
        }
    }

    /// <summary>
    /// Brings the page's visible tiles up to date.
    ///
    /// This is the pass that makes deep zoom feel weightless. Tiles sit on a
    /// fixed grid, so a pan keeps every tile still on screen and pays only for
    /// the strip that scrolled in; render_core serves the rest from its cache
    /// in about two milliseconds for a whole viewport.
    ///
    /// Work is bounded on both sides: the grid returns only the tiles the
    /// viewport touches plus one ring of prefetch, and each tile is a fixed
    /// 256px however far the user has zoomed.
    /// </summary>
    private void RenderVisibleTiles(PageSlot slot)
    {
        double top = _layout.TopOf(slot.PageIndex);

        // Viewport intersected with this page, in page-local slot DIPs.
        double left = Math.Max(0, _lastViewLeft);
        double right = Math.Min(slot.SlotWidth, _lastViewRight);
        double pageTop = Math.Max(0, _lastViewTop - top);
        double pageBottom = Math.Min(slot.SlotHeight, _lastViewBottom - top);

        if (right - left < 1 || pageBottom - pageTop < 1)
        {
            slot.ClearTiles();
            return;
        }

        // Level from what the screen actually needs across the whole page.
        int level = TileGrid.LevelForWidth(
            slot.SlotWidth * _currentZoomFactor * Math.Max(1.0, RasterizationScale));

        var wanted = TileGrid.VisibleTiles(
            slot.SlotWidth, slot.SlotHeight, level, left, pageTop, right, pageBottom);

        var added = slot.SyncTiles(wanted, level);

        // Reuse is the whole point of tiling, so it is the number worth
        // watching: after a pan, most of the wanted tiles should already be on
        // the card and only the strip that scrolled in should be new.
        Diag.Log($"tiles p{slot.PageIndex}: level={level} want={wanted.Count} " +
                 $"new={added.Count} reused={wanted.Count - added.Count}");

        foreach (var tile in added)
        {
            RenderTile(slot, tile);
        }
    }

    private async void RenderTile(PageSlot slot, PageTile tile)
    {
        // async void, so an exception escaping here has nowhere to go: it is
        // raised on the thread pool, nothing observes it, and the process
        // dies with no message. Catching and logging turns a silent death
        // into a line in the trace. See RenderBaseTier for the same reason.
        try
        {
            ulong handle = _documentHandle;
            int pageIndex = slot.PageIndex;
            var addr = tile.Address;

            var raw = await Task.Run(() =>
                PageRenderer.RenderTileRaw(handle, pageIndex, addr.Level, addr.Col, addr.Row));

            // The document can close, or the tile can be scrolled away and
            // discarded, while its render is in flight.
            if (handle != _documentHandle || slot.TileLevel != addr.Level)
            {
                return;
            }

            if (raw.Bgra is not null)
            {
                tile.Bitmap = PageRenderer.ToBitmap(raw).Bitmap;
                tile.IsExact = true;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"RenderTile p{slot.PageIndex} failed: {ex}");
        }
    }

    private async void SharpenSlot(PageSlot slot, int targetWidth)
    {
        if (!slot.TryBeginSharpen())
        {
            return;
        }

        // See RenderBaseTier: try/finally so an exception cannot leave the
        // slot permanently marked as sharpening, and a catch so an async void
        // failure is logged instead of taking the process down silently.
        try
        {
            ulong handle = _documentHandle;
            int pageIndex = slot.PageIndex;

            var raw = await Task.Run(() => PageRenderer.RenderUncachedRaw(handle, pageIndex, targetWidth));

            if (handle != _documentHandle)
            {
                return;
            }

            // Drop a result the zoom has already moved past. A sharp render of
            // a large page takes long enough that a zoom gesture can finish
            // while it is in flight, and applying it would show a bitmap at
            // the wrong resolution until the next pass replaced it.
            double aspect = slot.SlotWidth > 0 ? slot.SlotHeight / slot.SlotWidth : 1.0;
            int wantedNow = _budget.SharpWidthFor(slot.SlotWidth, _currentZoomFactor, RasterizationScale, aspect);
            if (_budget.ShouldResharpen(targetWidth, wantedNow))
            {
                return;
            }

            if (raw.Bgra is not null)
            {
                slot.SetSharpRender(PageRenderer.ToBitmap(raw).Bitmap, raw.Width);
                Diag.Log($"sharpened {pageIndex} to {raw.Width}x{raw.Height}");
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"SharpenSlot p{slot.PageIndex} failed: {ex}");
        }
        finally
        {
            slot.EndSharpen();
        }
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
            slot.Shapes.Clear();
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

        foreach (var slot in PageSlots)
        {
            slot.RebuildHighlightRects();
        }

        foreach (var sh in _allShapes)
        {
            if (sh.PageIndex >= 0 && sh.PageIndex < PageSlots.Count)
            {
                PageSlots[sh.PageIndex].Shapes.Add(sh);
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

        DistributeFormOutlines();
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

    public bool HasSelectedAnnotation => _selectedAnnotationId is not null || _selectedLoaded is not null;

    /// <summary>True when the current loaded selection is one of our text boxes.
    /// The toolbar uses this to show the text style controls (font, fill, outline,
    /// thickness) whenever a text box is selected, no matter which tool is armed,
    /// so a Select-tool click on a text box still exposes its properties.</summary>
    public bool HasSelectedTextBox => _selectedLoaded is not null && _selectedIsTextBox;

    /// <summary>True when the current loaded selection is one of our SHAPES
    /// (rectangle, ellipse, line, arrow). The toolbar uses this to show the
    /// colour and width sections whenever a shape is selected under any tool,
    /// so a Select-tool pick on a shape exposes its style.</summary>
    public bool HasSelectedShape => _selectedLoaded is not null && _selectedIsShape;

    /// <summary>How many loaded annotations are in the current multi-selection
    /// (anchor plus extras). Zero when nothing is selected. The align/distribute
    /// controls key their visibility off this: alignment needs at least two.</summary>
    public int SelectionCount => (_selectedLoaded is null ? 0 : 1) + _extraSelected.Count;

    /// <summary>True when at least two annotations are selected together, so
    /// alignment and grouping become meaningful.</summary>
    public bool HasMultiSelection => SelectionCount >= 2;

    /// <summary>Whether the current selection is a shape (rectangle, ellipse,
    /// line, arrow). Set once on selection so the drag path pays no per-sample
    /// FFI cost. Cleared alongside the other selection flags.</summary>
    private bool _selectedIsShape;

    /// <summary>Every annotation, in draw order, as the layer stack.</summary>
    private List<IAnnotation> AllAnnotations()
    {
        var all = new List<IAnnotation>(_allHighlights.Count + _allInkStrokes.Count + _allShapes.Count);
        all.AddRange(_allHighlights);
        all.AddRange(_allInkStrokes);
        all.AddRange(_allShapes);
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

        if (hit is not null)
        {
            _selectedLoaded = null;
            _loadedDrag = null;
            _loadedGrip = LoadedAnnotationPicker.Grip.None;
            RefreshSelectionOutline();
            OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
            return true;
        }

        // A handle of what is ALREADY selected wins, before anything else is
        // considered, and it is grabbable slightly OUTSIDE the annotation.
        //
        // Half of every handle is drawn outside the shape it belongs to, and
        // the pick below requires the point to be INSIDE, so grabbing a
        // handle's outer edge used to deselect instead of resizing. That is
        // why only the top-left corner appeared to work: it is the one people
        // naturally click slightly inward on.
        if (_selectedLoaded is LoadedSelection current && current.PageIndex == pageIndex)
        {
            // Turns the pointer into the box's own frame and also finds the rotate
            // handle above it, so grabbing a handle of an already-selected (possibly
            // rotated) box works.
            var grip = GripForPoint(current, normX, normY);

            if (grip != LoadedAnnotationPicker.Grip.None && CanResize(current))
            {
                _loadedDrag = (normX, normY, current);
                _loadedGrip = grip;
                // Handle grip - resize/rotate belongs to the anchor only, so the
                // extras drag-origin can stay empty (move-all does not apply).
                _extraDragOrigin.Clear();
                return true;
            }
        }

        // Nothing of ours here, so try what the file already had. Marks made
        // this session sit in the overlay ABOVE the page, so they win a tie.
        // A plain click clears any previous selection so SelectLoadedAt can set
        // a fresh anchor; a SHIFT click leaves the current selection alone so
        // SelectLoadedAt sees the anchor and can push it to the extras list.
        // Clearing unconditionally here was the reason shift-adding never
        // worked - prev was already null by the time the shift branch ran.
        _loadedDrag = null;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        if (!IsShiftDown())
        {
            _selectedLoaded = null;
        }
        if (SelectLoadedAt(pageIndex, normX, normY))
        {
            return true;
        }

        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return false;
    }

    public void ClearAnnotationSelection()
    {
        if (_selectedAnnotationId is null && _selectedLoaded is null)
        {
            return;
        }

        _selectedAnnotationId = null;
        _selectedLoaded = null;
        _selectedIsTextBox = false;
        _selectedIsShape = false;
        _extraSelected.Clear();
        _extraDragOrigin.Clear();
        _loadedDrag = null;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        _moveOrigin = null;
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
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
        if (_selectedLoaded is not null)
        {
            DragLoadedTo(normX, normY);
            return;
        }

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
        // A loaded annotation is written through here, at the END of the drag,
        // rather than on every pointer sample.
        CommitLoadedMove();

        _isMovingAnnotation = false;
        _moveOrigin = null;
    }

    public void DeleteSelectedAnnotation()
    {
        if (DeleteSelectedLoaded())
        {
            return;
        }

        if (_selectedAnnotationId is not Guid id)
        {
            return;
        }

        int removed = _allHighlights.RemoveAll(h => h.Id == id)
            + _allInkStrokes.RemoveAll(s => s.Id == id)
            + _allShapes.RemoveAll(sh => sh.Id == id);
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
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
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

        for (int i = 0; i < _allShapes.Count; i++)
        {
            if (_allShapes[i].Id == id)
            {
                _allShapes[i] = (ShapeAnnotation)edit(_allShapes[i]);
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
            slot.SelectionGrips.Clear();
            slot.ExtraSelectionOutlines.Clear();
            slot.SelectionRotation = 0; // nothing turned unless a rotated box says so below
        }

        if (_selectedLoaded is LoadedSelection sel)
        {
            var slot = SlotFor(sel.PageIndex);
            slot?.SelectionOutline.Add(new ScaledRect(
                sel.Left * SlotLayoutWidth,
                sel.Top * SlotLayoutWidth,
                (sel.Right - sel.Left) * SlotLayoutWidth,
                (sel.Bottom - sel.Top) * SlotLayoutWidth,
                string.Empty));

            // The frame and handles are laid out UPRIGHT (from the tight box) and
            // then turned as one about the box centre, so a rotated text box is
            // framed at its real angle.
            if (slot is not null)
            {
                slot.SelectionRotation = (_selectedIsTextBox || _selectedIsShape) ? _selectedRotationDeg : 0;
                slot.SelectionCenterX = (sel.Left + sel.Right) / 2 * SlotLayoutWidth;
                slot.SelectionCenterY = (sel.Top + sel.Bottom) / 2 * SlotLayoutWidth;
            }

            // Grips only for what can actually be resized. Offering them on a
            // drawing, which PDFium refuses to scale, would be an invitation
            // to an error message.
            if (slot is not null && CanResize(sel))
            {
                // Edge (one-axis) handles only for a free resize; an aspect-locked
                // picture keeps just its four corners. A text box also gets the
                // rotate handle above its top edge.
                AddGrips(slot, sel, edges: AspectToPreserve(sel) == 0,
                         rotate: _selectedIsTextBox || _selectedIsShape);
            }

            // Draw a marquee (no handles) for each extra-selected object, on its
            // own page's slot. Operations live on the anchor; the extras just
            // participate in Delete for now.
            foreach (var extra in _extraSelected)
            {
                var extraSlot = SlotFor(extra.PageIndex);
                extraSlot?.ExtraSelectionOutlines.Add(new ScaledRect(
                    extra.Left * SlotLayoutWidth,
                    extra.Top * SlotLayoutWidth,
                    (extra.Right - extra.Left) * SlotLayoutWidth,
                    (extra.Bottom - extra.Top) * SlotLayoutWidth,
                    string.Empty));
            }

            InkStrokeChanged?.Invoke();
            return;
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

        SlotFor(selected.PageIndex)?.SelectionOutline.Add(
            ScaledRect.From(selected.Bounds, SlotLayoutWidth));
        InkStrokeChanged?.Invoke();
    }

    // ---------------- Annotations already in the file ----------------
    //
    // Marks made this session live in _allHighlights / _allInkStrokes and are
    // drawn by the overlay. Marks that were ALREADY in the opened file are a
    // different thing: PDFium draws them as part of the page, and the only
    // handle we have on one is its index and bounding box.
    //
    // Rebuilding them into the overlay model would need each kind's own
    // geometry, quad points for a highlight and a point list for a stroke, and
    // would then have to keep two representations of the same mark in step.
    // Editing them where they live is simpler and works for EVERY subtype,
    // including ones this app cannot draw, so a file marked up in Acrobat is
    // editable here too.

    /// <summary>A selected annotation that came from the file, in normalized units.</summary>
    private readonly record struct LoadedSelection(
        int PageIndex, int Index, double Left, double Top, double Right, double Bottom);

    private LoadedSelection? _selectedLoaded;

    /// <summary>Where a drag of a loaded annotation began, and its rect then.</summary>
    private (double X, double Y, LoadedSelection Start)? _loadedDrag;

    /// <summary>Which corner the drag has hold of; None means it is a move.</summary>
    private LoadedAnnotationPicker.Grip _loadedGrip = LoadedAnnotationPicker.Grip.None;

    /// <summary>Objects that are ALSO selected besides <see cref="_selectedLoaded"/>
    /// (the anchor / most-recently-clicked). Shift-click grows this set; a plain
    /// click clears it. The anchor keeps the handles and drives style edits; the
    /// extras get a marquee only so Delete removes them all at once. Follow-ups
    /// will extend rotate, restyle and align to the whole set.</summary>
    private readonly List<LoadedSelection> _extraSelected = new();

    /// <summary>Snapshot of <see cref="_extraSelected"/> taken at drag start, so
    /// every pointer sample applies the anchor's total delta to the ORIGINALS,
    /// never accumulates. Without this the extras would drift by delta every
    /// sample and race off the page.</summary>
    private readonly List<LoadedSelection> _extraDragOrigin = new();

    /// <summary>
    /// Whether the current selection is one of our text boxes. Read ONCE when the
    /// selection changes (an FFI + parse), then used by the per-sample drag path,
    /// which cannot afford to read the tag on every pointer move. A text box
    /// resizes freely (eight handles) and re-wraps its text; an image stamp keeps
    /// its aspect and scales.
    /// </summary>
    private bool _selectedIsTextBox;

    /// <summary>The selected text box's clockwise rotation in degrees; 0 otherwise.
    /// Read once on selection, updated live while the rotate handle is dragged. The
    /// value the overlay draws with lives on the page slot (per page).</summary>
    private double _selectedRotationDeg;

    /// <summary>Per-page cache of what the file already carries.</summary>
    private readonly Dictionary<int, List<Interop.ExistingAnnotation>> _loadedByPage = new();


    private List<Interop.ExistingAnnotation> LoadedFor(int pageIndex)
    {
        if (_loadedByPage.TryGetValue(pageIndex, out var cached))
        {
            return cached;
        }

        var list = _documentHandle == 0
            ? new List<Interop.ExistingAnnotation>()
            : Interop.AnnotationLoader.Load(_documentHandle, pageIndex);
        _loadedByPage[pageIndex] = list;
        return list;
    }

    /// <summary>
    /// Picks the topmost annotation already in the file under a point.
    /// Later entries are drawn on top, so the search runs backwards.
    /// </summary>
    private bool SelectLoadedAt(int pageIndex, double normX, double normY)
    {
        var boxes = LoadedFor(pageIndex)
            .Select(a => new AnnotationBox(a.Index, a.Left, a.Top, a.Right, a.Bottom))
            .ToList();

        if (LoadedAnnotationPicker.PickTopmost(boxes, normX, normY) is not AnnotationBox hit)
        {
            return false;
        }

        // Shift-click grows the selection. If the clicked mark was already in
        // the multi-selection it is removed (deselected individually); otherwise
        // the previous anchor moves to the extras list and the clicked mark
        // becomes the new anchor. A plain click clears the extras.
        bool shift = IsShiftDown();
        if (shift)
        {
            // Clicked mark is already in the extras: remove it and keep the anchor.
            int existingExtra = _extraSelected.FindIndex(x =>
                x.PageIndex == pageIndex && x.Index == hit.Index);
            if (existingExtra >= 0)
            {
                _extraSelected.RemoveAt(existingExtra);
                RefreshSelectionOutline();
                return true;
            }
            // Clicked mark is already the anchor: demote the FIRST extra to be
            // the new anchor (or clear the anchor if there are no extras).
            if (_selectedLoaded is LoadedSelection cur
                && cur.PageIndex == pageIndex && cur.Index == hit.Index)
            {
                if (_extraSelected.Count > 0)
                {
                    _selectedLoaded = _extraSelected[0];
                    _extraSelected.RemoveAt(0);
                    ApplyTextBoxSelectionInfo(_selectedLoaded.Value.PageIndex, _selectedLoaded.Value.Index);
                }
                else
                {
                    ClearAnnotationSelection();
                    return true;
                }
                RefreshSelectionOutline();
                return true;
            }
            // New mark to add: push the current anchor to extras, promote clicked.
            if (_selectedLoaded is LoadedSelection prev)
            {
                _extraSelected.Add(prev);
            }
        }
        else
        {
            _extraSelected.Clear();
        }

        _selectedLoaded = new LoadedSelection(
            pageIndex, hit.Index, hit.Left, hit.Top, hit.Right, hit.Bottom);
        ApplyTextBoxSelectionInfo(pageIndex, hit.Index);
        var sel = _selectedLoaded.Value;
        _loadedDrag = shift ? null : (normX, normY, sel);
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        if (!shift)
        {
            _loadedGrip = GripForPoint(sel, normX, normY);
        }
        // Snapshot the extras so the drag can apply the anchor's delta to their
        // ORIGINAL positions each sample, not to whatever they were on the last
        // sample. Only meaningful for a body drag (no grip); a resize/rotate is
        // an anchor-only operation, and the shift-add path returns above without
        // starting a drag at all.
        _extraDragOrigin.Clear();
        if (!shift && _loadedGrip == LoadedAnnotationPicker.Grip.None)
        {
            _extraDragOrigin.AddRange(_extraSelected);
        }
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    /// <summary>
    /// Reads the just-selected annotation's tag ONCE (kept off the per-sample drag
    /// path): whether it is one of our text boxes, its rotation, and its own TIGHT
    /// upright rect, which replaces the enlarged bounds a rotated box reports so the
    /// frame is drawn around the real box.
    /// </summary>
    private void ApplyTextBoxSelectionInfo(int pageIndex, int index)
    {
        _selectedRotationDeg = 0;
        // Whether the selection is a shape is decided by whether its /Contents
        // parses as our shape tag; the shape check comes first because it is a
        // cheap prefix test and rules out most other marks.
        string? contents = ReadAnnotationContents(pageIndex, index);
        _selectedIsShape = contents is not null && contents.StartsWith("AyaanShape:", StringComparison.Ordinal);

        if (_selectedIsShape)
        {
            // Pull the shape's rotation out of its tag. The rest of the shape
            // style stays on the tool and applies through ApplyStyleToSelectedShape.
            _selectedRotationDeg = ParseShapeRotation(contents!);

            // Also mirror the shape's OWN colour into the tool's InkColorHex, so
            // the opacity slider, colour swatch and the "current colour" indicator
            // all reflect what THIS shape actually is - not the last colour the
            // user picked. A subsequent slider drag then re-styles the shape at
            // the right starting point.
            string? shapeColor = ParseShapeColor(contents!);
            if (shapeColor is not null)
            {
                InkColorHex = shapeColor;
            }
        }

        if (!TextBoxTagReader.TryParse(contents, out var tag))
        {
            _selectedIsTextBox = false;
            return;
        }
        _selectedIsShape = false; // a text box, not a shape

        _selectedIsTextBox = true;
        _selectedRotationDeg = tag.RotationDeg;
        if (tag.HasBoxRect && _selectedLoaded is LoadedSelection s)
        {
            _selectedLoaded = s with
            {
                Left = tag.BoxLeft, Top = tag.BoxTop, Right = tag.BoxRight, Bottom = tag.BoxBottom,
            };
        }

        // Populate the tool's style from the box's own tag, so the toolbar shows
        // this box's colours/font/size/decorations and any change picks up on top
        // of them, rather than overwriting with whatever leftover state the tool
        // happened to have. Without this, picking a fill colour after selecting
        // an existing box did nothing visible because the box was rebuilt with
        // stale style values from the last new box.
        TextFontSize = tag.FontSizeNorm;
        InkColorHex = tag.ColorHex;
        TextAlign = tag.Align;
        TextFillHex = tag.FillHex;
        TextOutlineHex = tag.OutlineHex;
        if (tag.OutlineWidthNorm > 0)
        {
            TextOutlineWidthNorm = tag.OutlineWidthNorm;
        }
        TextUnderline = tag.Underline;
        TextStrikethrough = tag.Strikethrough;
        RestoreTextFont(tag.FontPath);
    }

    /// <summary>
    /// Re-writes the selected SHAPE (rectangle, ellipse, line, arrow) with the
    /// current ink colour and/or the current stroke width, keeping its bounds
    /// and kind. Called by the picker click handlers so a colour or width
    /// change applies to the selected shape, not just to the next one drawn.
    /// A width of null keeps the shape's own width.
    /// </summary>
    public void ApplyStyleToSelectedShape(bool changeColor = true, bool changeWidth = false)
    {
        if (_documentHandle == 0
            || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsShape)
        {
            return;
        }

        const int CaptureWidth = 1000;
        // 0 alpha in the packed rgba tells the core to keep the tag's current
        // colour, so we only send the ink colour when the caller is actually
        // changing it. Same idea for width: negative means keep.
        uint colorRgba = changeColor ? PackRgba(InkColorHex) : 0u;
        float widthPx = changeWidth ? (float)InkWidth : -1f;

        PushHistory(HistoryScope.Document, "Restyle shape");
        int status = RenderCoreNative.restyle_shape_annotation(
            _documentHandle, sel.PageIndex, sel.Index, CaptureWidth,
            colorRgba, widthPx, out int newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not apply that style.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);

        // The re-added shape is at the end of the list; follow it and re-read
        // its bounds so the marquee stays on the mark.
        var actual = LoadedFor(sel.PageIndex)
            .Where(x => x.Index == newIndex)
            .Select(x => (Interop.ExistingAnnotation?)x)
            .FirstOrDefault();
        _selectedLoaded = actual is Interop.ExistingAnnotation a
            ? new LoadedSelection(sel.PageIndex, newIndex, a.Left, a.Top, a.Right, a.Bottom)
            : sel with { Index = newIndex };
        RefreshSelectionOutline();
    }

    /// <summary>
    /// Re-writes the selected text box with the tool's current style (fill,
    /// outline, thickness, colour, alignment, decorations), keeping the box's
    /// words, font, bounds, and rotation. Used by the picker click handlers so
    /// a preset colour applies to the box you have selected, not just to the
    /// next box you type.
    /// </summary>
    public void ApplyStyleToSelectedTextBox()
    {
        if (_documentHandle == 0
            || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsTextBox)
        {
            return;
        }

        const int CaptureWidth = 1000;
        var (tr, tg, tb, ta) = ParseHex(InkColorHex, defaultAlpha: 0xFF);
        uint textRgba = ((uint)tr << 24) | ((uint)tg << 16) | ((uint)tb << 8) | ta;

        PushHistory(HistoryScope.Document, "Restyle text");

        int status = RenderCoreNative.restyle_text_box_annotation(
            _documentHandle, sel.PageIndex, sel.Index, CaptureWidth,
            textRgba,
            (int)TextAlign,
            PackRgba(TextFillHex),
            PackRgba(TextOutlineHex),
            (float)(TextOutlineWidthNorm * CaptureWidth),
            TextUnderline ? 1 : 0,
            TextStrikethrough ? 1 : 0,
            out int newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not apply that style.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);

        // Follow the re-laid-out box's own tight rect and angle, the same way
        // CommitLoadedMove and CommitRotation do, so the marquee stays on it.
        if (TextBoxTagReader.TryParse(ReadAnnotationContents(sel.PageIndex, newIndex), out var tag)
            && tag.HasBoxRect)
        {
            _selectedRotationDeg = tag.RotationDeg;
            _selectedLoaded = new LoadedSelection(
                sel.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom);
        }
        else
        {
            _selectedLoaded = sel with { Index = newIndex };
        }
        RefreshSelectionOutline();
    }

    /// <summary>The handle under a point, accounting for the box's rotation: the
    /// pointer is turned back into the box's own upright frame first, then the
    /// rotate handle (above the top edge) and the resize handles are tested.</summary>
    private LoadedAnnotationPicker.Grip GripForPoint(LoadedSelection sel, double nx, double ny)
    {
        var box = new AnnotationBox(sel.Index, sel.Left, sel.Top, sel.Right, sel.Bottom);
        var (lx, ly) = InverseRotate(nx, ny, box, _selectedRotationDeg);
        if ((_selectedIsTextBox || _selectedIsShape)
            && LoadedAnnotationPicker.IsRotateHandle(box, lx, ly))
        {
            return LoadedAnnotationPicker.Grip.Rotate;
        }
        // Shapes are free-resize (edge handles) too; text boxes always are.
        return LoadedAnnotationPicker.GripAt(box, lx, ly,
            edges: _selectedIsTextBox || _selectedIsShape);
    }

    /// <summary>Turns a screen-space point back into a box's own upright frame,
    /// the inverse of the WinUI RotateTransform (clockwise-positive) the overlay
    /// applies to the frame.</summary>
    private static (double X, double Y) InverseRotate(double x, double y, AnnotationBox box, double deg)
    {
        if (deg == 0)
        {
            return (x, y);
        }
        double cx = (box.Left + box.Right) / 2, cy = (box.Top + box.Bottom) / 2;
        double rad = deg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = x - cx, dy = y - cy;
        return (cx + dx * cos + dy * sin, cy - dx * sin + dy * cos);
    }

    /// <summary>Whether Shift is held right now, for snapping the rotate drag.</summary>
    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>
    /// Drags the marquee only. The document is not touched until the gesture
    /// ends: committing on every pointer sample would mean a PDFium write and
    /// a full page re-render per sample, which no amount of caching makes
    /// smooth.
    /// </summary>
    private void DragLoadedTo(double normX, double normY)
    {
        if (_loadedDrag is not (double ox, double oy, LoadedSelection start))
        {
            return;
        }

        var box = new AnnotationBox(start.Index, start.Left, start.Top, start.Right, start.Bottom);

        // The rotate handle turns the box: the angle is the direction from the
        // centre to the pointer, measured from straight up (the handle's home), so
        // dragging it round spins the frame. Bounds do not change.
        if (_loadedGrip == LoadedAnnotationPicker.Grip.Rotate)
        {
            double cx = (start.Left + start.Right) / 2, cy = (start.Top + start.Bottom) / 2;
            double ang = Math.Atan2(normY - cy, normX - cx) * 180.0 / Math.PI + 90.0;

            // Hold Shift to snap to 15-degree steps.
            if (IsShiftDown())
            {
                ang = Math.Round(ang / 15.0) * 15.0;
            }
            if (ang < 0) { ang += 360; }
            if (ang >= 360) { ang -= 360; }

            _selectedRotationDeg = ang;
            _selectedLoaded = start;
            RefreshSelectionOutline();
            return;
        }

        // A grip resizes, anything else moves. Both are computed from where the
        // drag STARTED, so neither can creep across a long gesture. A resize on a
        // rotated box works in the box's own frame, so the pointer is turned back
        // into it first; a move is a plain screen-space translation either way.
        AnnotationBox moved;
        if (_loadedGrip == LoadedAnnotationPicker.Grip.None)
        {
            moved = LoadedAnnotationPicker.Dragged(box, ox, oy, normX, normY);
        }
        else
        {
            var (lnx, lny) = InverseRotate(normX, normY, box, _selectedRotationDeg);
            moved = LoadedAnnotationPicker.Resized(box, _loadedGrip, lnx, lny, AspectToPreserve(start));

            // On a rotated box, the corner (or edge) OPPOSITE the grip must stay
            // put in SCREEN space, the way Word behaves. The local-frame resize
            // above shifted the box's centre from C0 to C1, and turning about that
            // new centre displaces every point of the frame in screen space by
            // (I - R) * (C1 - C0). Nudging the new bounds by the negative of that
            // brings anything with unchanged local coordinates (the opposite
            // corner or edge midpoint) back to its old screen position, and it
            // also lines the dragged corner up under the pointer.
            if (_selectedRotationDeg != 0)
            {
                double dx = (moved.Left + moved.Right) / 2 - (box.Left + box.Right) / 2;
                double dy = (moved.Top + moved.Bottom) / 2 - (box.Top + box.Bottom) / 2;
                double rad = _selectedRotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);
                double shiftX = dx * (cos - 1) - dy * sin;
                double shiftY = dx * sin + dy * (cos - 1);
                moved = moved.MovedBy(shiftX, shiftY);
            }
        }

        _selectedLoaded = start with
        {
            Left = moved.Left, Top = moved.Top, Right = moved.Right, Bottom = moved.Bottom,
        };

        // Move-all: on a body drag with extras selected, apply the anchor's total
        // delta to each extra's ORIGINAL position captured at drag start. Resize
        // and rotate stay anchor-only for now (multi-resize/rotate is a bigger
        // interaction question).
        if (_loadedGrip == LoadedAnnotationPicker.Grip.None && _extraDragOrigin.Count > 0)
        {
            double dx = moved.Left - box.Left;
            double dy = moved.Top - box.Top;
            for (int i = 0; i < _extraDragOrigin.Count && i < _extraSelected.Count; i++)
            {
                var o = _extraDragOrigin[i];
                _extraSelected[i] = o with
                {
                    Left = o.Left + dx,
                    Top = o.Top + dy,
                    Right = o.Right + dx,
                    Bottom = o.Bottom + dy,
                };
            }
        }

        RefreshSelectionOutline();
    }

    /// <summary>Writes a finished drag through to the document.</summary>
    private void CommitLoadedMove()
    {
        if (_loadedDrag is not (_, _, LoadedSelection start) || _selectedLoaded is not LoadedSelection now)
        {
            return;
        }

        _loadedDrag = null;

        // The grip belongs to the gesture that just ended, so it is read once
        // and cleared here rather than on each of the returns below. Leaving
        // it set would make the next plain drag resize from a corner nobody is
        // holding, and clearing it per exit path is how one gets missed.
        bool rotating = _loadedGrip == LoadedAnnotationPicker.Grip.Rotate;
        bool resizing = _loadedGrip != LoadedAnnotationPicker.Grip.None && !rotating;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;

        // A rotate changes only the angle, not the bounds, so it takes its own
        // path BEFORE the "did the rectangle move" check that would otherwise call
        // it a no-op and drop it.
        if (rotating)
        {
            CommitRotation(start);
            return;
        }

        // Nothing actually changed, so do not dirty the document or reflow.
        if (!LoadedAnnotationPicker.IsRealMove(
                new AnnotationBox(start.Index, start.Left, start.Top, start.Right, start.Bottom),
                new AnnotationBox(now.Index, now.Left, now.Top, now.Right, now.Bottom)))
        {
            return;
        }

        // The inverse of a move or resize is four numbers: put the rectangle
        // back. Recorded BEFORE the write, and at annotation granularity
        // rather than as a document snapshot, because dragging a stamp around
        // a page is the most repeated edit there is and each snapshot is the
        // whole PDF.
        PushHistory(HistoryScope.AnnotationBounds,
                    resizing ? "Resize annotation" : "Move annotation",
                    new AnnotationBoundsState(start.PageIndex, start.Index,
                                              start.Left, start.Top, start.Right, start.Bottom));

        // resize_annotation, not set_annotation_bounds: it does the same thing
        // for a move or a quad-point resize, and rebuilds the annotation when
        // PDFium will not scale it, which is the only way a stamp can grow.
        // Rebuilding moves it to the end of the page's list, so the index it
        // reports back is the one to keep.
        const int CaptureWidth = 1000;

        float l = (float)(now.Left * CaptureWidth);
        float t = (float)(now.Top * CaptureWidth);
        float r = (float)(now.Right * CaptureWidth);
        float b = (float)(now.Bottom * CaptureWidth);

        // Shapes go through their own path first, and ONLY when actually being
        // resized. A shape records its kind, colour and width, so it can be
        // redrawn at any size; that is the one thing an arbitrary mark cannot
        // do, and it is why a rectangle drawn last week can still be dragged
        // bigger today. Anything else reports Unsupported here and falls
        // through to the general path below, which handles moves for every
        // kind of annotation and rebuilds a stamp from its own image.
        int status = RenderStatus.Unsupported;
        int newIndex = now.Index;

        // A text box ALWAYS goes through its own re-layout: on a resize it re-wraps
        // to the new width, and on a MOVE it keeps its angle (the generic path
        // resizes the annotation's rect without turning the content, which clipped
        // or dropped a rotated box).
        if (_selectedIsTextBox)
        {
            status = RenderCoreNative.resize_text_box_annotation(
                _documentHandle, now.PageIndex, now.Index, CaptureWidth, l, t, r, b, out newIndex);
        }

        if (status != RenderStatus.OkPdfium && resizing)
        {
            // A shape redraws from its tag. Only meaningful for a resize.
            status = RenderCoreNative.resize_shape_annotation(
                _documentHandle, now.PageIndex, now.Index, CaptureWidth, l, t, r, b, out newIndex);
        }

        if (status != RenderStatus.OkPdfium)
        {
            status = RenderCoreNative.resize_annotation(
                _documentHandle, now.PageIndex, now.Index, CaptureWidth, l, t, r, b, out newIndex);
        }

        Diag.Log($"{(resizing ? "resize" : "move")} loaded annotation " +
                 $"p{now.PageIndex}#{now.Index} -> {status}, index now {newIndex}");

        if (status != RenderStatus.OkPdfium)
        {
            // Put the marquee back where the mark still is, rather than
            // leaving it somewhere the document does not agree with.
            _selectedLoaded = start;
            RefreshSelectionOutline();
            Status = status == RenderStatus.Unsupported
                ? (resizing ? "A drawing cannot be resized yet." : "That annotation cannot be moved.")
                : $"Could not {(resizing ? "resize" : "move")} that annotation.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(now.PageIndex);

        // A re-wrapped text box can be a different HEIGHT than was dragged, and
        // its ANNOTATION rect is the enlarged bounding box when rotated (that is
        // what stops PDFium clipping the turned corners), not the tight upright
        // box the frame draws. Reading LoadedFor's bounds instead of the tag was
        // the "each resize makes the frame BIGGER" bug: the enlarged rect became
        // the starting rect for the next drag, and the next commit inflated it
        // again. Take the box's OWN tight rect and angle from the tag it just
        // wrote. Everything else can trust the dragged rectangle.
        if (_selectedIsTextBox
            && TextBoxTagReader.TryParse(ReadAnnotationContents(now.PageIndex, newIndex), out var tag)
            && tag.HasBoxRect)
        {
            _selectedRotationDeg = tag.RotationDeg;
            _selectedLoaded = new LoadedSelection(
                now.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom);
        }
        else
        {
            _selectedLoaded = now with { Index = newIndex };
        }

        // Move-all commit: write each extra's NEW position to the document. The
        // anchor's re-add above shifts indices on its page (rebuilt annotation
        // moves to the end), so read the CURRENT extras (already at their new
        // in-memory positions) and match them back by their old index against
        // the fresh cache. Only body drags (grip None) trigger this; a resize
        // or rotate is anchor-only.
        if (!resizing && !rotating && _extraDragOrigin.Count > 0)
        {
            var pagesTouched = new HashSet<int> { now.PageIndex };
            for (int i = 0; i < _extraSelected.Count && i < _extraDragOrigin.Count; i++)
            {
                var target = _extraSelected[i];
                float exl = (float)(target.Left * CaptureWidth);
                float ext = (float)(target.Top * CaptureWidth);
                float exr = (float)(target.Right * CaptureWidth);
                float exb = (float)(target.Bottom * CaptureWidth);

                // Text box: full re-layout preserves rotation/font/wrapping.
                // Shape: same via its shape restyle path (no bounds change tool,
                // so use generic resize which just moves for a matching size).
                // Anything else: generic resize_annotation (moves the /Rect).
                int extStatus = RenderStatus.Unsupported;
                int extNewIndex = target.Index;

                string? extContents = ReadAnnotationContents(target.PageIndex, target.Index);
                bool extIsText = TextBoxTagReader.TryParse(extContents, out _);
                bool extIsShape = extContents is not null
                    && extContents.StartsWith("AyaanShape:", StringComparison.Ordinal);

                if (extIsText)
                {
                    extStatus = RenderCoreNative.resize_text_box_annotation(
                        _documentHandle, target.PageIndex, target.Index, CaptureWidth,
                        exl, ext, exr, exb, out extNewIndex);
                }
                if (extStatus != RenderStatus.OkPdfium && extIsShape)
                {
                    extStatus = RenderCoreNative.resize_shape_annotation(
                        _documentHandle, target.PageIndex, target.Index, CaptureWidth,
                        exl, ext, exr, exb, out extNewIndex);
                }
                if (extStatus != RenderStatus.OkPdfium)
                {
                    extStatus = RenderCoreNative.resize_annotation(
                        _documentHandle, target.PageIndex, target.Index, CaptureWidth,
                        exl, ext, exr, exb, out extNewIndex);
                }

                if (extStatus == RenderStatus.OkPdfium)
                {
                    _extraSelected[i] = target with { Index = extNewIndex };
                    pagesTouched.Add(target.PageIndex);
                }
                Diag.Log($"move extra p{target.PageIndex}#{target.Index} -> {extStatus}, now #{extNewIndex}");
            }
            _extraDragOrigin.Clear();
            foreach (int page in pagesTouched)
            {
                InvalidateLoadedPage(page);
            }
        }

        RefreshSelectionOutline();
    }

    /// <summary>What edge of the selection an alignment snaps to.</summary>
    public enum AlignMode { Left, CenterH, Right, Top, MiddleV, Bottom }

    /// <summary>Aligns every object in the multi-selection to a shared edge or
    /// centre. Uses the SELECTION's overall bounding box as the reference (the
    /// "align to selection" convention every editor uses). Each object moves
    /// only; sizes stay. Written back to the document per object through the
    /// same routing the drag-move uses (text-box relayout, shape restyle-move,
    /// or generic bounds set).</summary>
    public void AlignSelected(AlignMode mode)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection anchor)
        {
            return;
        }
        // Alignment on a single object is a no-op; save the caller a UI check.
        if (_extraSelected.Count == 0) { return; }

        // Selection bounding box.
        double minL = anchor.Left, minT = anchor.Top;
        double maxR = anchor.Right, maxB = anchor.Bottom;
        foreach (var s in _extraSelected)
        {
            if (s.Left < minL) { minL = s.Left; }
            if (s.Top < minT) { minT = s.Top; }
            if (s.Right > maxR) { maxR = s.Right; }
            if (s.Bottom > maxB) { maxB = s.Bottom; }
        }

        PushHistory(HistoryScope.Document, "Align");

        // Compute each object's NEW bounds by shifting to hit the target.
        // Anchor first, then extras. The write helper takes care of routing per type.
        LoadedSelection newAnchor = ShiftToAlign(anchor, mode, minL, minT, maxR, maxB);
        var newExtras = new List<LoadedSelection>(_extraSelected.Count);
        foreach (var s in _extraSelected)
        {
            newExtras.Add(ShiftToAlign(s, mode, minL, minT, maxR, maxB));
        }

        CommitAlignedOrDistributed(anchor, newAnchor, newExtras);
    }

    /// <summary>Distributes the multi-selection evenly along one axis: each
    /// object's centre lands on an even step between the two outermost centres.
    /// Needs at least three objects (with two, "distribute" is undefined - they
    /// are just the endpoints). Positions only; sizes stay.</summary>
    public void DistributeSelected(bool horizontal)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection anchor)
        {
            return;
        }
        int total = 1 + _extraSelected.Count;
        if (total < 3) { return; }

        // Sort all selections by their axis centre. The two outermost keep their
        // positions; the middle ones get moved to even steps between.
        var all = new List<LoadedSelection> { anchor };
        all.AddRange(_extraSelected);
        double Centre(LoadedSelection s) => horizontal
            ? (s.Left + s.Right) / 2
            : (s.Top + s.Bottom) / 2;
        var sorted = all.OrderBy(Centre).ToList();

        double firstC = Centre(sorted[0]);
        double lastC = Centre(sorted[^1]);
        double step = (lastC - firstC) / (total - 1);

        // Build the target centre for each sorted item.
        var moved = new Dictionary<int, LoadedSelection>(); // keyed by identity via list index
        for (int i = 0; i < sorted.Count; i++)
        {
            double targetC = firstC + step * i;
            var s = sorted[i];
            double delta = targetC - Centre(s);
            var shifted = horizontal
                ? s with { Left = s.Left + delta, Right = s.Right + delta }
                : s with { Top = s.Top + delta, Bottom = s.Bottom + delta };
            moved[i] = shifted;
        }

        // Map sorted results back onto anchor/extras by identity.
        LoadedSelection FindMoved(LoadedSelection original)
        {
            int idx = sorted.FindIndex(x => x.PageIndex == original.PageIndex && x.Index == original.Index);
            return idx >= 0 ? moved[idx] : original;
        }

        PushHistory(HistoryScope.Document, horizontal ? "Distribute horizontally" : "Distribute vertically");
        var newAnchor = FindMoved(anchor);
        var newExtras = _extraSelected.Select(FindMoved).ToList();
        CommitAlignedOrDistributed(anchor, newAnchor, newExtras);
    }

    /// <summary>Shifts an object so a chosen edge/centre lands on a target value
    /// taken from the selection bounding box. Size unchanged.</summary>
    private static LoadedSelection ShiftToAlign(LoadedSelection s, AlignMode mode,
        double minL, double minT, double maxR, double maxB)
    {
        double w = s.Right - s.Left;
        double h = s.Bottom - s.Top;
        double centreX = (minL + maxR) / 2;
        double centreY = (minT + maxB) / 2;
        return mode switch
        {
            AlignMode.Left => s with { Left = minL, Right = minL + w },
            AlignMode.Right => s with { Left = maxR - w, Right = maxR },
            AlignMode.CenterH => s with { Left = centreX - w / 2, Right = centreX + w / 2 },
            AlignMode.Top => s with { Top = minT, Bottom = minT + h },
            AlignMode.Bottom => s with { Top = maxB - h, Bottom = maxB },
            AlignMode.MiddleV => s with { Top = centreY - h / 2, Bottom = centreY + h / 2 },
            _ => s,
        };
    }

    /// <summary>Writes new bounds for the anchor and each extra to the document
    /// through the same routing the drag-move uses (text-box relayout, shape
    /// restyle-move, or generic bounds set), then refreshes the overlay.</summary>
    private void CommitAlignedOrDistributed(LoadedSelection oldAnchor,
        LoadedSelection newAnchor, List<LoadedSelection> newExtras)
    {
        const int CaptureWidth = 1000;
        var pagesTouched = new HashSet<int>();

        // Anchor.
        int anchorNewIndex = WriteMovedAnnotation(oldAnchor, newAnchor, CaptureWidth);
        if (anchorNewIndex >= 0)
        {
            pagesTouched.Add(newAnchor.PageIndex);
            _selectedLoaded = newAnchor with { Index = anchorNewIndex };
        }

        // Extras.
        for (int i = 0; i < _extraSelected.Count && i < newExtras.Count; i++)
        {
            var oldExtra = _extraSelected[i];
            var target = newExtras[i];
            int newIdx = WriteMovedAnnotation(oldExtra, target, CaptureWidth);
            if (newIdx >= 0)
            {
                _extraSelected[i] = target with { Index = newIdx };
                pagesTouched.Add(target.PageIndex);
            }
        }

        IsDirty = true;
        foreach (int page in pagesTouched) { InvalidateLoadedPage(page); }
        RefreshSelectionOutline();
    }

    /// <summary>Writes a single annotation's new bounds through the type-aware
    /// path (text-box, shape, generic). Returns the new index or -1 on failure.</summary>
    private int WriteMovedAnnotation(LoadedSelection oldSel, LoadedSelection target, int captureWidth)
    {
        float l = (float)(target.Left * captureWidth);
        float t = (float)(target.Top * captureWidth);
        float r = (float)(target.Right * captureWidth);
        float b = (float)(target.Bottom * captureWidth);

        string? contents = ReadAnnotationContents(oldSel.PageIndex, oldSel.Index);
        bool isText = TextBoxTagReader.TryParse(contents, out _);
        bool isShape = contents is not null
            && contents.StartsWith("AyaanShape:", StringComparison.Ordinal);

        int status = RenderStatus.Unsupported;
        int newIndex = oldSel.Index;

        if (isText)
        {
            status = RenderCoreNative.resize_text_box_annotation(
                _documentHandle, oldSel.PageIndex, oldSel.Index, captureWidth,
                l, t, r, b, out newIndex);
        }
        if (status != RenderStatus.OkPdfium && isShape)
        {
            status = RenderCoreNative.resize_shape_annotation(
                _documentHandle, oldSel.PageIndex, oldSel.Index, captureWidth,
                l, t, r, b, out newIndex);
        }
        if (status != RenderStatus.OkPdfium)
        {
            status = RenderCoreNative.resize_annotation(
                _documentHandle, oldSel.PageIndex, oldSel.Index, captureWidth,
                l, t, r, b, out newIndex);
        }
        return status == RenderStatus.OkPdfium ? newIndex : -1;
    }

    /// <summary>Clones the current anchor selection IN PLACE and selects the clone,
    /// so the drag that follows moves the clone and leaves the original where it
    /// was. This is the Ctrl-drag = copy convention every editor uses. Only text
    /// boxes and shapes are supported for now (the two kinds that live in the PDF
    /// with round-trippable tags). Returns true if a clone was made.</summary>
    public bool DuplicateSelectedForDrag()
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel)
        {
            return false;
        }

        string? contents = ReadAnnotationContents(sel.PageIndex, sel.Index);
        if (contents is null)
        {
            return false;
        }

        PushHistory(HistoryScope.Document, "Duplicate");
        const int CaptureWidth = 1000;
        int status;
        int newIndex;

        if (_selectedIsShape)
        {
            // Rebuild the ShapeSpec from the tag and add a second copy at the SAME
            // bounds. The subsequent drag will slide it off the original.
            if (!ShapeSpecFromTag(contents, sel, CaptureWidth, out var spec))
            {
                return false;
            }
            status = RenderCoreNative.add_shape_annotations(
                _documentHandle, CaptureWidth, new[] { spec }, 1);
            if (status != RenderStatus.OkPdfium) { return false; }
            newIndex = LoadedFor(sel.PageIndex).Count; // will resolve after invalidate
        }
        else if (_selectedIsTextBox
                 && TextBoxTagReader.TryParse(contents, out var tag))
        {
            // Re-add the box via the styled writer using the tag's own settings.
            double left = tag.HasBoxRect ? tag.BoxLeft : sel.Left;
            double top = tag.HasBoxRect ? tag.BoxTop : sel.Top;
            double right = tag.HasBoxRect ? tag.BoxRight : sel.Right;
            double bottom = tag.HasBoxRect ? tag.BoxBottom : sel.Bottom;
            var (r, g, b, a) = ParseHex(tag.ColorHex, defaultAlpha: 0xFF);
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(tag.Text);
            byte[]? fontUtf8 = string.IsNullOrEmpty(tag.FontPath)
                ? null
                : System.Text.Encoding.UTF8.GetBytes(tag.FontPath);

            status = RenderCoreNative.add_text_box_annotation_styled(
                _documentHandle, sel.PageIndex, CaptureWidth,
                (float)(left * CaptureWidth), (float)(top * CaptureWidth),
                (float)(right * CaptureWidth), (float)(bottom * CaptureWidth),
                utf8, (nuint)utf8.Length,
                (float)(tag.FontSizeNorm * CaptureWidth), r, g, b, a,
                (int)tag.Align,
                PackRgba(tag.FillHex), PackRgba(tag.OutlineHex),
                (float)(tag.OutlineWidthNorm * CaptureWidth),
                fontUtf8, (nuint)(fontUtf8?.Length ?? 0),
                tag.Underline ? 1 : 0, tag.Strikethrough ? 1 : 0);
            if (status != RenderStatus.OkPdfium) { return false; }
            // Rotation lives in the tag but the styled add doesn't take it; if the
            // original was rotated, follow up with a rotate to the same angle.
            if (tag.RotationDeg != 0)
            {
                int rotStatus = RenderCoreNative.rotate_text_box_annotation(
                    _documentHandle, sel.PageIndex,
                    (int)(LoadedFor(sel.PageIndex).Count), // last one - reload after invalidate below
                    CaptureWidth,
                    (float)(left * CaptureWidth), (float)(top * CaptureWidth),
                    (float)(right * CaptureWidth), (float)(bottom * CaptureWidth),
                    (float)tag.RotationDeg, out int _);
                _ = rotStatus; // best-effort; if it fails the clone comes out upright
            }
            newIndex = LoadedFor(sel.PageIndex).Count;
        }
        else
        {
            return false;
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);

        // Pick the last annotation on the page - the one we just added - and make
        // it the new anchor so the drag now moves the clone, not the original.
        var all = LoadedFor(sel.PageIndex);
        if (all.Count == 0) { return false; }
        var newest = all[^1];
        _selectedLoaded = new LoadedSelection(
            sel.PageIndex, newest.Index, newest.Left, newest.Top, newest.Right, newest.Bottom);
        _extraSelected.Clear();
        ApplyTextBoxSelectionInfo(sel.PageIndex, newest.Index);
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    /// <summary>Reconstructs a NativeShapeSpec from a selected shape's tag, using
    /// its own bounds. Returns false if the tag is malformed.</summary>
    private static bool ShapeSpecFromTag(string contents, LoadedSelection sel,
        int captureWidth, out Interop.NativeShapeSpec spec)
    {
        spec = default;
        string? rest = contents.StartsWith("AyaanShape:", StringComparison.Ordinal)
            ? contents.Substring("AyaanShape:".Length) : null;
        if (rest is null) { return false; }
        string[] parts = rest.Split(':');
        if (parts.Length < 5) { return false; }
        if (!int.TryParse(parts[0], out int kind)) { return false; }
        string rgba = parts[1];
        if (rgba.Length != 8) { return false; }
        byte r = byte.Parse(rgba.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(rgba.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(rgba.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        byte a = byte.Parse(rgba.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double widthPts))
        {
            return false;
        }
        bool fx = parts[3] == "1";
        bool fy = parts[4] == "1";
        double rot = parts.Length >= 6
            && double.TryParse(parts[5], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double rv) ? rv : 0;

        // The drag-direction flags let the arrow head keep its side.
        float x1 = (float)((fx ? sel.Left : sel.Right) * captureWidth);
        float x2 = (float)((fx ? sel.Right : sel.Left) * captureWidth);
        float y1 = (float)((fy ? sel.Top : sel.Bottom) * captureWidth);
        float y2 = (float)((fy ? sel.Bottom : sel.Top) * captureWidth);

        // Tag stores width in POINTS; ShapeSpec wants capture-space pixels. Without
        // the page width here we approximate: capture_width matches the writer's
        // capture, so widthPts * (capture/page_w) - but page_w isn't exposed. Use
        // widthPts directly; the resize path uses the same conversion the writer
        // did, so a duplicate will be roughly the same visual width.
        float widthPx = (float)widthPts;

        spec = new Interop.NativeShapeSpec
        {
            PageIndex = sel.PageIndex, Kind = kind,
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            R = r, G = g, B = b, A = a,
            WidthPx = widthPx, RotationDeg = (float)rot,
        };
        return true;
    }

    /// <summary>The shape's colour from its tag, as "#AARRGGBB" including the
    /// alpha, or null if the tag is malformed. Used to mirror the shape's actual
    /// colour and opacity into the tool state on selection so the pickers and
    /// the opacity slider reflect this shape, not the tool's leftover state.</summary>
    private static string? ParseShapeColor(string contents)
    {
        string? rest = contents.StartsWith("AyaanShape:", StringComparison.Ordinal)
            ? contents.Substring("AyaanShape:".Length)
            : null;
        if (rest is null) { return null; }
        string[] parts = rest.Split(':');
        if (parts.Length < 2) { return null; }
        string rgba = parts[1]; // RRGGBBAA
        if (rgba.Length != 8) { return null; }
        foreach (char ch in rgba)
        {
            if (!Uri.IsHexDigit(ch)) { return null; }
        }
        // Tag stores RRGGBBAA; the app uses "#AARRGGBB".
        return $"#{rgba.Substring(6, 2)}{rgba.Substring(0, 6)}";
    }

    /// <summary>The shape tag has fields separated by ':'; the rotation, if
    /// present, is the ninth (after kind, RGBA, width, fx, fy). An older tag
    /// without it comes back as 0.</summary>
    private static double ParseShapeRotation(string contents)
    {
        string? rest = contents.StartsWith("AyaanShape:", StringComparison.Ordinal)
            ? contents.Substring("AyaanShape:".Length)
            : null;
        if (rest is null)
        {
            return 0;
        }
        string[] parts = rest.Split(':');
        // Fields: 0=kind, 1=rgba, 2=width, 3=fx, 4=fy, 5=rotation
        if (parts.Length < 6)
        {
            return 0;
        }
        return double.TryParse(parts[5], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
               && double.IsFinite(v)
            ? v : 0;
    }

    /// <summary>Writes a finished ROTATE through to the document: the object is
    /// re-laid-out at its own upright bounds, turned to the new angle. Bounds
    /// are unchanged, so the marquee keeps them and its angle. Branches on the
    /// selected type so shapes go through their own FFI.</summary>
    private void CommitRotation(LoadedSelection start)
    {
        const int CaptureWidth = 1000;

        if (_selectedIsShape)
        {
            PushHistory(HistoryScope.Document, "Rotate shape");
            int shapeStatus = RenderCoreNative.rotate_shape_annotation(
                _documentHandle, start.PageIndex, start.Index, CaptureWidth,
                (float)_selectedRotationDeg, out int shapeNewIndex);
            if (shapeStatus != RenderStatus.OkPdfium)
            {
                _selectedLoaded = start;
                RefreshSelectionOutline();
                Status = "Could not rotate that shape.";
                return;
            }
            IsDirty = true;
            InvalidateLoadedPage(start.PageIndex);
            _selectedLoaded = start with { Index = shapeNewIndex };
            RefreshSelectionOutline();
            return;
        }

        float l = (float)(start.Left * CaptureWidth);
        float t = (float)(start.Top * CaptureWidth);
        float r = (float)(start.Right * CaptureWidth);
        float b = (float)(start.Bottom * CaptureWidth);

        PushHistory(HistoryScope.Document, "Rotate text");

        int status = RenderCoreNative.rotate_text_box_annotation(
            _documentHandle, start.PageIndex, start.Index, CaptureWidth, l, t, r, b,
            (float)_selectedRotationDeg, out int newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            _selectedLoaded = start;
            RefreshSelectionOutline();
            Status = "Could not rotate that text.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(start.PageIndex);

        // Take the box's OWN rect and angle from the tag the core just wrote, so
        // the frame is drawn around EXACTLY what was rendered rather than the
        // pre-commit guess. This is what keeps the frame and the text aligned.
        if (TextBoxTagReader.TryParse(ReadAnnotationContents(start.PageIndex, newIndex), out var tag)
            && tag.HasBoxRect)
        {
            _selectedRotationDeg = tag.RotationDeg;
            _selectedLoaded = new LoadedSelection(
                start.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom);
        }
        else
        {
            _selectedLoaded = start with { Index = newIndex };
        }

        RefreshSelectionOutline();
    }

    /// <summary>
    /// Selects the annotation most recently added to a page.
    ///
    /// New annotations go to the END of the page's list, so the last one is
    /// the one just placed. Used to hand a freshly stamped image straight to
    /// the Select tool, already picked up and ready to nudge.
    /// </summary>
    public void SelectNewestAnnotation(int pageIndex)
    {
        var all = LoadedFor(pageIndex);
        if (all.Count == 0)
        {
            return;
        }

        var newest = all[^1];
        _selectedLoaded = new LoadedSelection(
            pageIndex, newest.Index, newest.Left, newest.Top, newest.Right, newest.Bottom);
        ApplyTextBoxSelectionInfo(pageIndex, newest.Index);
        _loadedDrag = null;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        _selectedAnnotationId = null;

        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>Deletes the selected annotation from the file itself.</summary>
    private bool DeleteSelectedLoaded()
    {
        if (_selectedLoaded is not LoadedSelection sel)
        {
            return false;
        }

        // A document snapshot, unlike move and resize. Undoing a delete means
        // bringing the annotation's whole content back, and nothing smaller
        // than the document holds it: the entry would have to carry the image,
        // the quad points, the colours and the appearance stream. This is the
        // one annotation edit worth the cost.
        PushHistory(HistoryScope.Document, "Delete annotation");

        int status = RenderCoreNative.delete_annotation(_documentHandle, sel.PageIndex, sel.Index);
        Diag.Log($"delete loaded annotation p{sel.PageIndex}#{sel.Index} -> {status}");

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not delete that annotation.";
            return true;
        }

        // The multi-selection extras go next. Indices SHIFT after a delete
        // (annotations are compacted), so delete in DESCENDING order per page:
        // erasing #7 first leaves #5 and #3 at their original indices, whereas
        // ascending order would leave stale numbers pointing at the wrong marks.
        var pagesTouched = new HashSet<int> { sel.PageIndex };
        foreach (var extra in _extraSelected
            .OrderByDescending(x => x.PageIndex)
            .ThenByDescending(x => x.Index))
        {
            int extraStatus = RenderCoreNative.delete_annotation(
                _documentHandle, extra.PageIndex, extra.Index);
            Diag.Log($"delete extra p{extra.PageIndex}#{extra.Index} -> {extraStatus}");
            if (extraStatus == RenderStatus.OkPdfium)
            {
                pagesTouched.Add(extra.PageIndex);
            }
        }
        _extraSelected.Clear();

        _selectedLoaded = null;
        _loadedDrag = null;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        IsDirty = true;
        foreach (int page in pagesTouched)
        {
            InvalidateLoadedPage(page);
        }
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    /// <summary>
    /// Drops the cached annotation list for a page and redraws it.
    ///
    /// Both halves matter: the indices shift after a delete, so a stale list
    /// would address the wrong annotation next time, and the marks are part of
    /// the page bitmap, so without a re-render the change stays invisible.
    /// </summary>
    private void InvalidateLoadedPage(int pageIndex)
    {
        _loadedByPage.Remove(pageIndex);

        var slot = SlotFor(pageIndex);
        if (slot is not null)
        {
            slot.ClearTiles();
            slot.ReleaseBitmap();
            RenderBaseTier(slot);
            ScheduleSharpenPass();
        }
    }

    /// <summary>Half a grip's on-screen size, in slot DIPs.</summary>
    private const double GripHalf = 4.5;

    /// <summary>
    /// Which corner grip of the current selection lies under a point, for
    /// cursor feedback.
    ///
    /// Without this the handles are decoration: the pointer keeps the tool's
    /// own cursor over them, so nothing says they can be dragged and nobody
    /// tries. A resize handle that does not announce itself is not a handle.
    /// </summary>
    public LoadedAnnotationPicker.Grip GripUnder(int pageIndex, double normX, double normY)
    {
        if (_selectedLoaded is not LoadedSelection sel || sel.PageIndex != pageIndex)
        {
            return LoadedAnnotationPicker.Grip.None;
        }

        if (!CanResize(sel))
        {
            return LoadedAnnotationPicker.Grip.None;
        }

        return GripForPoint(sel, normX, normY);
    }

    /// <summary>Whether a point is inside the current selection, so it can be dragged.</summary>
    public bool IsOverSelection(int pageIndex, double normX, double normY)
    {
        if (_selectedLoaded is not LoadedSelection sel || sel.PageIndex != pageIndex)
        {
            return false;
        }

        return normX >= sel.Left && normX <= sel.Right
            && normY >= sel.Top && normY <= sel.Bottom;
    }

    /// <summary>
    /// The aspect a resize must preserve, or 0 for a free resize.
    ///
    /// A stamp is a picture: dragging a corner freely stretches a signature
    /// into something that no longer looks like the person's handwriting.
    /// Text markup has no such shape to protect.
    /// </summary>
    private double AspectToPreserve(LoadedSelection sel)
    {
        // A text box is a stamp underneath, but it is NOT a picture: it resizes
        // freely and re-wraps its text, so it keeps no aspect and gets the full
        // eight handles. Only a real image stamp (a signature, a pasted picture)
        // holds its aspect.
        if (_selectedIsTextBox)
        {
            return 0;
        }

        foreach (var a in LoadedFor(sel.PageIndex))
        {
            if (a.Index == sel.Index && a.Subtype == Interop.AnnotSubtype.Stamp)
            {
                double w = sel.Right - sel.Left;
                return w > 0 ? (sel.Bottom - sel.Top) / w : 0;
            }
        }
        return 0;
    }

    /// <summary>
    /// Whether this annotation can be resized at all.
    ///
    /// Ink cannot: its shape is a path, and PDFium will neither scale it nor
    /// let it be rebuilt from the outside. Everything else either scales in
    /// place or can be rebuilt from its own image.
    /// </summary>
    private bool CanResize(LoadedSelection sel)
    {
        // Our shapes are stored as ink annotations underneath (create_ink_annotation
        // in the core) but they CAN be resized and rotated through their tag, so
        // the plain "ink is unresizable" rule does not apply to them. This was
        // why a selected shape got no grips at all - not the eight resize handles
        // AND not the rotate handle - even though the rotation stack is wired.
        if (_selectedIsShape) { return true; }

        foreach (var a in LoadedFor(sel.PageIndex))
        {
            if (a.Index == sel.Index)
            {
                return a.Subtype != Interop.AnnotSubtype.Ink;
            }
        }
        return true;
    }

    private static void AddGrips(PageSlot slot, LoadedSelection sel, bool edges, bool rotate)
    {
        double l = sel.Left * SlotLayoutWidth;
        double t = sel.Top * SlotLayoutWidth;
        double r = sel.Right * SlotLayoutWidth;
        double b = sel.Bottom * SlotLayoutWidth;
        double mx = (l + r) / 2;
        double my = (t + b) / 2;

        // Four corners always; the four edge midpoints when the object resizes
        // freely, for a Word-style eight-handle frame.
        var points = new List<(double, double)> { (l, t), (r, t), (l, b), (r, b) };
        if (edges)
        {
            points.Add((mx, t));
            points.Add((mx, b));
            points.Add((l, my));
            points.Add((r, my));
        }

        // The rotate handle floats above the top edge on a thin stem; the whole
        // frame is turned about the centre, so laid out upright here it follows
        // the box round. The stem is added first so the handle draws on top of it.
        if (rotate)
        {
            double gapPx = LoadedAnnotationPicker.RotateHandleGap * SlotLayoutWidth;
            slot.SelectionGrips.Add(new ScaledRect(mx - 1, t - gapPx, 2, gapPx, string.Empty));
            points.Add((mx, t - gapPx));
        }

        foreach (var (cx, cy) in points)
        {
            slot.SelectionGrips.Add(new ScaledRect(
                cx - GripHalf, cy - GripHalf, GripHalf * 2, GripHalf * 2, string.Empty));
        }
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
        var raw = await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, ThumbnailPixelWidth));

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

    /// <summary>
    /// True when a page carries real, selectable text.
    ///
    /// A scanned document is images all the way down: PDFium reports zero
    /// characters, which is a perfectly valid page, not a failure. Selection
    /// and text highlighting are impossible there, and silently doing nothing
    /// is the worst possible response because it is indistinguishable from a
    /// broken tool. Callers use this to switch to a geometric marquee and to
    /// say so in the UI.
    /// </summary>
    public bool PageHasText(int pageIndex) => (TextLayerFor(pageIndex)?.CharCount ?? 0) > 0;

    /// <summary>Shown in the status pill so "why can't I select?" answers itself.</summary>
    public string TextAvailabilityLabel =>
        PageCount == 0 ? string.Empty
        : PageHasText(CurrentPageIndex) ? string.Empty
        : "Scanned page – no text";

    // ---------------- Marquee (geometric) selection ----------------

    private (double X, double Y)? _marqueeAnchor;
    private int _marqueePage;

    /// <summary>
    /// Starts a rectangular selection, for pages with no text layer.
    ///
    /// On a scan there is nothing to select by character, but marking a REGION
    /// is still meaningful and is what a reader actually wants. Coordinates
    /// are normalized on the way in, exactly like every other annotation, so a
    /// marquee highlight is indistinguishable from a text one once committed.
    /// </summary>
    public void BeginMarquee(int pageIndex, double x, double y)
    {
        ClearSelection();
        _marqueePage = pageIndex;
        _marqueeAnchor = (Norm(x), Norm(y));
    }

    public void UpdateMarquee(double x, double y)
    {
        if (_marqueeAnchor is not (double ax, double ay))
        {
            return;
        }

        var rect = MarqueeRect(ax, ay, Norm(x), Norm(y));

        var slot = SlotFor(_marqueePage);
        if (slot is null)
        {
            return;
        }

        slot.SelectionRects.Clear();
        slot.SelectionRects.Add(ScaledRect.From(rect, SlotLayoutWidth));
    }

    /// <summary>Commits the marquee as a highlight, if it is big enough to be deliberate.</summary>
    public void EndMarquee()
    {
        if (_marqueeAnchor is not (double ax, double ay))
        {
            return;
        }

        var slot = SlotFor(_marqueePage);
        // The preview is stored pre-scaled for display; the annotation itself
        // must be normalized like every other, so convert back.
        var scaled = slot?.SelectionRects.Count > 0 ? slot.SelectionRects[0] : default;
        var rect = new TextRect(
            scaled.Left / SlotLayoutWidth,
            scaled.Top / SlotLayoutWidth,
            (scaled.Left + scaled.Width) / SlotLayoutWidth,
            (scaled.Top + scaled.Height) / SlotLayoutWidth);
        _marqueeAnchor = null;
        slot?.SelectionRects.Clear();

        // A click, or a stray one-pixel drag, is not an attempt to highlight.
        const double MinSize = 0.01;
        if (ActiveTool != ToolMode.Highlight || rect.Width < MinSize || rect.Height < MinSize)
        {
            return;
        }

        PushHistory(HistoryScope.Annotations, "Highlight");
        var highlight = new HighlightAnnotation(_marqueePage, new[] { rect }, HighlightColorHex);
        _allHighlights.Add(highlight);
        var mSlot = SlotFor(_marqueePage);
        mSlot?.Highlights.Add(highlight);
        mSlot?.RebuildHighlightRects();
        if (_marqueePage == CurrentPageIndex)
        {
            Highlights.Add(highlight);
        }

        IsDirty = true;
    }

    private static TextRect MarqueeRect(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));

    /// <summary>
    /// Starts a selection-marquee: drag a rectangle over annotations and every
    /// one it touches gets added to the multi-selection on release. Does NOT
    /// clear the current selection - the marquee EXTENDS what is selected the
    /// same way Shift-click does. The preview uses the same SelectionRects
    /// overlay slot the highlight-tool marquee uses (nothing on screen shows
    /// both at once, and this saves a second binding).
    /// </summary>
    public void BeginSelectionMarquee(int pageIndex, double x, double y)
    {
        _marqueePage = pageIndex;
        _marqueeAnchor = (Norm(x), Norm(y));
    }

    /// <summary>Ends a selection marquee: adds every loaded annotation on the
    /// marquee's page whose bounds INTERSECT the rectangle to the multi-selection.
    /// A tiny marquee (a click without drag) is ignored. Duplicates are skipped so
    /// re-marqueeing over the same set is idempotent.</summary>
    public void EndSelectionMarquee()
    {
        if (_marqueeAnchor is not (double _, double _))
        {
            return;
        }

        var slot = SlotFor(_marqueePage);
        var scaled = slot?.SelectionRects.Count > 0 ? slot.SelectionRects[0] : default;
        var rect = new TextRect(
            scaled.Left / SlotLayoutWidth,
            scaled.Top / SlotLayoutWidth,
            (scaled.Left + scaled.Width) / SlotLayoutWidth,
            (scaled.Top + scaled.Height) / SlotLayoutWidth);
        _marqueeAnchor = null;
        slot?.SelectionRects.Clear();

        const double MinSize = 0.005;
        if (rect.Width < MinSize || rect.Height < MinSize)
        {
            return;
        }

        // Hit-test every loaded annotation on this page against the rect.
        // "Intersects" not "contains", so a shape that is only partly under the
        // marquee gets picked up - the way Illustrator and PowerPoint work.
        var loaded = LoadedFor(_marqueePage);
        var hits = new List<LoadedSelection>();
        foreach (var a in loaded)
        {
            if (a.Right < rect.Left || a.Left > rect.Right) { continue; }
            if (a.Bottom < rect.Top || a.Top > rect.Bottom) { continue; }
            hits.Add(new LoadedSelection(_marqueePage, a.Index, a.Left, a.Top, a.Right, a.Bottom));
        }
        if (hits.Count == 0)
        {
            RefreshSelectionOutline();
            return;
        }

        // If nothing is currently selected, the first hit becomes the anchor and
        // the rest become extras. If an anchor already exists, everything the
        // marquee touched is added to the extras (skipping duplicates).
        if (_selectedLoaded is not LoadedSelection currentAnchor)
        {
            var first = hits[0];
            _selectedLoaded = first;
            ApplyTextBoxSelectionInfo(first.PageIndex, first.Index);
            for (int i = 1; i < hits.Count; i++)
            {
                if (!_extraSelected.Any(e => e.PageIndex == hits[i].PageIndex && e.Index == hits[i].Index))
                {
                    _extraSelected.Add(hits[i]);
                }
            }
        }
        else
        {
            foreach (var s in hits)
            {
                if (s.PageIndex == currentAnchor.PageIndex && s.Index == currentAnchor.Index) { continue; }
                if (_extraSelected.Any(e => e.PageIndex == s.PageIndex && e.Index == s.Index)) { continue; }
                _extraSelected.Add(s);
            }
        }

        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
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
            var hSlot = SlotFor(highlight.PageIndex);
            hSlot?.Highlights.Add(highlight);
            hSlot?.RebuildHighlightRects();
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
                // Pre-multiplied into slot DIPs: a normalized rect inside a
                // scaled layer lays out sub-pixel and never draws.
                var sr = ScaledRect.From(normalized, SlotLayoutWidth);
                if (sr.IsVisible)
                {
                    slot?.SelectionRects.Add(sr);
                }
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

    // ---- Shapes ----

    private ShapeDraft? _shapeDraft;
    private int _shapePageIndex;

    /// <summary>Which shape the tool draws. Chosen in the property bar.</summary>
    [ObservableProperty]
    public partial ShapeKind ActiveShapeKind { get; set; } = ShapeKind.Rectangle;

    public void BeginShape(int pageIndex, double x, double y)
    {
        // The page is fixed for the whole gesture, like ink: a shape dragged
        // past a page edge belongs to the page it started on rather than
        // jumping to whichever page the pointer ended over.
        _shapePageIndex = pageIndex;
        _shapeDraft = new ShapeDraft(ActiveShapeKind, Norm(x), Norm(y), Norm(x), Norm(y));
        InkStrokeChanged?.Invoke();
    }

    public void ExtendShape(double x, double y)
    {
        if (_shapeDraft is not { } d)
        {
            return;
        }

        _shapeDraft = d with { X2 = Norm(x), Y2 = Norm(y) };
        InkStrokeChanged?.Invoke();
    }

    public void EndShape()
    {
        if (_shapeDraft is { } d && ShapeGeometry.IsWorthDrawing(d) && _documentHandle != 0)
        {
            // Write the shape STRAIGHT into the document as a real annotation, the
            // way text boxes are. Before this, shapes lived in an overlay list and
            // only became real annotations on save, which meant the selection code
            // that draws rotate/resize handles (keyed off _selectedLoaded and the
            // shape's tag) never saw them, so a freshly-drawn shape had a marquee
            // and no handles. Immediate write unifies the two lives of a shape.
            const int CaptureWidth = 1000;
            var (r, g, b, a) = ParseHex(InkColorHex, defaultAlpha: 0xFF);
            var spec = new Interop.NativeShapeSpec
            {
                PageIndex = _shapePageIndex,
                Kind = (int)d.Kind,
                X1 = (float)(d.X1 * CaptureWidth),
                Y1 = (float)(d.Y1 * CaptureWidth),
                X2 = (float)(d.X2 * CaptureWidth),
                Y2 = (float)(d.Y2 * CaptureWidth),
                R = r, G = g, B = b, A = a,
                WidthPx = (float)(InkWidth * CaptureWidth),
                RotationDeg = 0f,
            };

            PushHistory(HistoryScope.Document, "Draw shape");
            int status = RenderCoreNative.add_shape_annotations(
                _documentHandle, CaptureWidth, new[] { spec }, 1);

            if (status == RenderStatus.OkPdfium)
            {
                IsDirty = true;
                InvalidateLoadedPage(_shapePageIndex);
                SelectNewestAnnotation(_shapePageIndex);
            }
            else
            {
                Diag.Log($"EndShape add_shape_annotations -> {status}");
                Status = "Could not add that shape.";
            }
        }

        _shapeDraft = null;
        InkStrokeChanged?.Invoke();
    }

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

    /// <summary>
    /// Places a stamp image on a page, centred on the point clicked.
    ///
    /// Written STRAIGHT into the document as a real annotation, rather than
    /// held in an overlay list until save like highlights and ink are. That is
    /// deliberate: the moment it is an annotation object, everything built for
    /// annotations already in a file applies to it, so a stamp can be clicked,
    /// dragged and deleted with no code of its own. The overlay model exists
    /// because text markup has to follow selected text; a stamp has no such
    /// need.
    ///
    /// <paramref name="widthFraction"/> is the stamp's width as a fraction of
    /// the page width. Height follows the image's own aspect, so a stamp is
    /// never squashed.
    /// </summary>
    public bool PlaceStamp(int pageIndex, double x, double y, StampPixels pixels,
                           double widthFraction = 0.25)
    {
        if (_documentHandle == 0 || pixels.Width <= 0 || pixels.Height <= 0)
        {
            return false;
        }

        // Normalized, top-left origin, both axes over the page WIDTH, which is
        // the convention render_core reads and writes in. The geometry itself
        // lives in StampPlacement so it can be tested; keeping it here is how
        // the last two geometry bugs reached the user.
        var (left, top, right, bottom) = StampPlacement.Compute(
            Norm(x), Norm(y), pixels.Width, pixels.Height, widthFraction);

        // BEFORE the edit. PushHistory captures the state to restore TO, so
        // pushing afterwards would record the document WITH the stamp and undo
        // would do nothing. Document scope, because a stamp goes into the file
        // itself rather than into an overlay list.
        PushHistory(HistoryScope.Document, "Place stamp");

        const int CaptureWidth = 1000;
        int status = RenderCoreNative.add_stamp_annotation(
            _documentHandle, pageIndex, CaptureWidth,
            (float)(left * CaptureWidth), (float)(top * CaptureWidth),
            (float)(right * CaptureWidth), (float)(bottom * CaptureWidth),
            pixels.Bgra, (nuint)pixels.Bgra.Length, pixels.Width, pixels.Height);

        Diag.Log($"place stamp p{pageIndex} at ({left:F3},{top:F3})-({right:F3},{bottom:F3}) " +
                 $"from {pixels.Width}x{pixels.Height}px -> {status}");

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not place that stamp.";
            return false;
        }

        IsDirty = true;
        InvalidateLoadedPage(pageIndex);
        return true;
    }

    /// <summary>Font size for new text boxes, as a fraction of page width.</summary>
    [ObservableProperty]
    public partial double TextFontSize { get; set; } = TextBoxPresets.DefaultSize.Value;

    /// <summary>How new text boxes align their lines.</summary>
    [ObservableProperty]
    public partial TextAlign TextAlign { get; set; } = TextAlign.Left;

    /// <summary>Background fill for new text boxes, "#AARRGGBB", or "" for none.</summary>
    [ObservableProperty]
    public partial string TextFillHex { get; set; } = "";

    /// <summary>Border colour for new text boxes, "#AARRGGBB", or "" for none.</summary>
    [ObservableProperty]
    public partial string TextOutlineHex { get; set; } = "";

    /// <summary>Border thickness for new text boxes, as a fraction of page width.</summary>
    [ObservableProperty]
    public partial double TextOutlineWidthNorm { get; set; } = 2.0 / 1000.0;

    /// <summary>
    /// OS path to the TrueType/OpenType font new text boxes are drawn in, or ""
    /// for the built-in Helvetica. Set by the font picker; the core embeds the
    /// file as a Unicode font so non-Latin scripts render. See
    /// project-ayaanpdf-text-fonts-foundation.
    /// </summary>
    [ObservableProperty]
    public partial string TextFontPath { get; set; } = "";

    /// <summary>The chosen font family's display name, for the editor preview
    /// (WinUI resolves an installed font by name); "" means the default.</summary>
    [ObservableProperty]
    public partial string TextFontFamily { get; set; } = "";

    [ObservableProperty]
    public partial bool TextBold { get; set; }

    [ObservableProperty]
    public partial bool TextItalic { get; set; }

    [ObservableProperty]
    public partial bool TextUnderline { get; set; }

    [ObservableProperty]
    public partial bool TextStrikethrough { get; set; }

    /// <summary>Installed font families for the picker, loaded once, lazily.</summary>
    public ObservableCollection<FontFamily> FontFamilies { get; } = new();

    private FontFamily? _selectedFontFamily;
    private bool _fontsLoading;

    /// <summary>
    /// Populates <see cref="FontFamilies"/> off the UI thread the first time the
    /// picker is shown (scanning the font files is too slow to do inline).
    /// </summary>
    public void EnsureFontsLoaded()
    {
        if (FontFamilies.Count > 0 || _fontsLoading)
        {
            return;
        }
        _fontsLoading = true;

        Task.Run(() =>
        {
            IReadOnlyList<FontFamily> families;
            try
            {
                families = FontCatalog.Enumerate();
            }
            catch
            {
                families = System.Array.Empty<FontFamily>();
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                foreach (var f in families)
                {
                    FontFamilies.Add(f);
                }
                _fontsLoading = false;
            });
        });
    }

    /// <summary>Picks a font family, resolving the file for the current bold/italic state.</summary>
    public void SelectFontFamily(FontFamily? family)
    {
        _selectedFontFamily = family;
        TextFontFamily = family?.Name ?? "";
        ResolveFontFile();
        if (family is not null)
        {
            _recentFontNames.RemoveAll(n => string.Equals(n, family.Name, StringComparison.OrdinalIgnoreCase));
            _recentFontNames.Insert(0, family.Name);
            if (_recentFontNames.Count > RecentFontLimit)
            {
                _recentFontNames.RemoveRange(RecentFontLimit, _recentFontNames.Count - RecentFontLimit);
            }
        }
    }

    /// <summary>How many recently-used fonts float above the rest of the picker.</summary>
    private const int RecentFontLimit = 6;

    /// <summary>The names of the recently used fonts, most-recent-first. Kept for
    /// a future reordering pass; the picker's own ItemsSource is NOT edited from
    /// inside its SelectionChanged handler, which crashed the app: modifying a
    /// ComboBox's items while it is dispatching a selection tears its state.</summary>
    private readonly List<string> _recentFontNames = new();

    partial void OnTextBoldChanged(bool value) => ResolveFontFile();

    partial void OnTextItalicChanged(bool value) => ResolveFontFile();

    /// <summary>
    /// Restores the font a re-opened box was drawn in, from its stored file path,
    /// so committing the edit re-embeds the SAME font. Without this a Burmese or
    /// Hindi box would silently fall back to the default on re-commit and its
    /// shaping would break. Also reflects the family name and bold/italic into
    /// the toolbar so it shows what is being edited. An empty path means the box
    /// was in the default font, which is restored as "no chosen font".
    /// </summary>
    public void RestoreTextFont(string fontPath)
    {
        if (string.IsNullOrEmpty(fontPath))
        {
            _selectedFontFamily = null;
            TextFontFamily = "";
            TextBold = false;
            TextItalic = false;
            TextFontPath = "";
            return;
        }

        // Read the one file for its family name and bold/italic cut. Prefer the
        // fully-enumerated family when the picker has already loaded it, so
        // toggling bold/italic mid-edit still finds the other cuts; otherwise the
        // one-file family is enough to re-embed exactly this file.
        FontFamily? single = FontCatalog.ReadFamilyFromFile(fontPath, out bool bold, out bool italic);
        FontFamily? full = single is null
            ? null
            : FontFamilies.FirstOrDefault(
                f => string.Equals(f.Name, single.Name, StringComparison.OrdinalIgnoreCase));

        _selectedFontFamily = full ?? single;
        TextFontFamily = _selectedFontFamily?.Name ?? "";
        TextBold = bold;
        TextItalic = italic;
        // Land on the EXACT file the box used, regardless of what ResolvePath
        // picked when TextBold/TextItalic changed above.
        TextFontPath = fontPath;
    }

    /// <summary>Recomputes <see cref="TextFontPath"/> from the family and B/I flags.</summary>
    private void ResolveFontFile()
    {
        TextFontPath = _selectedFontFamily?.ResolvePath(TextBold, TextItalic) ?? "";
    }

    /// <summary>
    /// Writes a text box to the document as real vector text, then hands it back
    /// as an ordinary loaded annotation.
    ///
    /// Committed straight into the PDF, the same way a stamp is, rather than
    /// held in a parallel overlay list: a text box is a stamp annotation
    /// underneath, so writing it immediately means it is selected, moved,
    /// resized and deleted by the machinery every loaded annotation already
    /// uses, instead of a second copy of all of it.
    /// </summary>
    /// <returns>True if it was written; the caller then selects the newest.</returns>
    public bool PlaceTextBox(int pageIndex, double normLeft, double normTop,
                             double normRight, double normBottom,
                             string text, string colorHex, double fontSizeNorm)
    {
        if (_documentHandle == 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }

        PushHistory(HistoryScope.Document, "Add text");
        return AddTextBoxNormalized(pageIndex, normLeft, normTop, normRight, normBottom,
                                    text, colorHex, fontSizeNorm);
    }

    /// <summary>
    /// The details needed to re-open the editor on an existing text box. The
    /// rectangle is the box's bounds, NORMALIZED, so re-editing keeps its width
    /// and the text re-wraps to it.
    /// </summary>
    public readonly record struct TextBoxEditTarget(
        int PageIndex, int Index, double Left, double Top, double Right, double Bottom,
        string Text, string ColorHex, double FontSizeNorm,
        TextAlign Align, string FillHex, string OutlineHex, double OutlineWidthNorm,
        string FontPath, bool Underline, bool Strikethrough);

    /// <summary>
    /// If a loaded text box is under the point, returns what is needed to edit
    /// it; otherwise null.
    ///
    /// This is how a placed text box becomes modifiable: the words are read
    /// back out of the annotation's tag, which is the only place they survive
    /// once the box is committed. Anything that is not one of our text boxes,
    /// a shape, a highlight, someone else's annotation, returns null and is
    /// left to the ordinary select-and-move path.
    /// </summary>
    public TextBoxEditTarget? HitLoadedTextBox(int pageIndex, double normX, double normY)
    {
        if (_documentHandle == 0)
        {
            return null;
        }

        var boxes = LoadedFor(pageIndex)
            .Select(a => new AnnotationBox(a.Index, a.Left, a.Top, a.Right, a.Bottom))
            .ToList();

        if (LoadedAnnotationPicker.PickTopmost(boxes, normX, normY) is not AnnotationBox hit)
        {
            return null;
        }

        string? contents = ReadAnnotationContents(pageIndex, hit.Index);
        if (!TextBoxTagReader.TryParse(contents, out var tag))
        {
            return null;
        }

        return new TextBoxEditTarget(
            pageIndex, hit.Index, hit.Left, hit.Top, hit.Right, hit.Bottom,
            tag.Text, tag.ColorHex, tag.FontSizeNorm,
            tag.Align, tag.FillHex, tag.OutlineHex, tag.OutlineWidthNorm,
            tag.FontPath, tag.Underline, tag.Strikethrough);
    }

    /// <summary>
    /// Removes a text box from the document so it can be re-edited on a clean
    /// page, and re-renders so it disappears from behind the editor.
    ///
    /// This is what stops the box showing as TWO layers while editing: the
    /// rendered box used to stay baked into the page under the live editor. Now
    /// the page shows nothing there and the editor is the only thing on it, so
    /// what is typed is what will be, immediately. The box is added back on
    /// commit; the single snapshot here makes the whole edit one undo step.
    /// </summary>
    public bool BeginLoadedTextBoxEdit(int pageIndex, int index)
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        PushHistory(HistoryScope.Document, "Edit text");

        if (RenderCoreNative.delete_annotation(_documentHandle, pageIndex, index) != RenderStatus.OkPdfium)
        {
            Status = "Could not edit that text.";
            return false;
        }

        _selectedLoaded = null;
        _loadedDrag = null;
        _loadedGrip = LoadedAnnotationPicker.Grip.None;
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        RefreshSelectionOutline();

        IsDirty = true;
        InvalidateLoadedPage(pageIndex);   // re-render: the box vanishes from the page
        return true;
    }

    /// <summary>
    /// Writes the edited text back as a box. The old one was already removed by
    /// <see cref="BeginLoadedTextBoxEdit"/>, so this only ADDS, and shares that
    /// edit's single undo snapshot. Empty text leaves the box gone, which is
    /// how a box is cleared by emptying it.
    /// </summary>
    public bool CommitEditedTextBox(int pageIndex,
                                    double normLeft, double normTop, double normRight, double normBottom,
                                    string text, string colorHex, double fontSizeNorm)
    {
        if (_documentHandle == 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return AddTextBoxNormalized(pageIndex, normLeft, normTop, normRight, normBottom,
                                    text, colorHex, fontSizeNorm);
    }

    /// <summary>Reads an annotation's /Contents, or null on any failure.</summary>
    private string? ReadAnnotationContents(int pageIndex, int index)
    {
        var buffer = RenderCoreNative.get_annotation_contents(_documentHandle, pageIndex, index);
        try
        {
            if (buffer.Status != RenderStatus.OkPdfium || buffer.Data == IntPtr.Zero || buffer.Len == 0)
            {
                return buffer.Status == RenderStatus.OkPdfium ? string.Empty : null;
            }

            byte[] bytes = new byte[(int)buffer.Len];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buffer);
        }
    }

    /// <summary>
    /// The shared add path. Coordinates are NORMALIZED and the caller owns the
    /// history push, so both the plain place and the replace go through exactly
    /// the same write.
    /// </summary>
    private bool AddTextBoxNormalized(int pageIndex, double left, double top, double right, double bottom,
                                      string text, string colorHex, double fontSizeNorm)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // The box the user dragged is passed straight through: the core wraps
        // the text to this WIDTH and grows the height to fit, so the box owns
        // its own layout and the exact bottom here is only a minimum.
        const int CaptureWidth = 1000;
        var (r, g, b, a) = ParseHex(colorHex, defaultAlpha: 0xFF);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);

        byte[]? fontUtf8 = string.IsNullOrEmpty(TextFontPath)
            ? null
            : System.Text.Encoding.UTF8.GetBytes(TextFontPath);

        int status = RenderCoreNative.add_text_box_annotation_styled(
            _documentHandle, pageIndex, CaptureWidth,
            (float)(left * CaptureWidth), (float)(top * CaptureWidth),
            (float)(right * CaptureWidth), (float)(bottom * CaptureWidth),
            utf8, (nuint)utf8.Length,
            (float)(fontSizeNorm * CaptureWidth), r, g, b, a,
            (int)TextAlign, PackRgba(TextFillHex), PackRgba(TextOutlineHex),
            (float)(TextOutlineWidthNorm * CaptureWidth),
            fontUtf8, (nuint)(fontUtf8?.Length ?? 0),
            TextUnderline ? 1 : 0, TextStrikethrough ? 1 : 0);

        Diag.Log($"text box p{pageIndex} \"{text.Replace("\n", "\\n")}\" align={TextAlign} -> {status}");

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not add that text.";
            return false;
        }

        IsDirty = true;
        InvalidateLoadedPage(pageIndex);
        return true;
    }

    /// <summary>Normalized page coordinate to slot-space DIPs, for positioning overlays.</summary>
    public double ToSlot(double normalized) => normalized * SlotLayoutWidth;

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
    private HistoryEntry Capture(HistoryEntry target) => Capture(target.Scope, target.Label, target.Bounds);

    private HistoryEntry Capture(HistoryScope scope, string label,
                                 AnnotationBoundsState? boundsTarget = null)
    {
        // For a per-annotation step the inverse is that SAME annotation's
        // rectangle as it stands right now, so redo puts it back where undo
        // took it from. Read live rather than remembered, since a rebuild on
        // resize can have changed its index.
        AnnotationBoundsState? bounds = null;
        if (scope == HistoryScope.AnnotationBounds && boundsTarget is not null)
        {
            foreach (var a in LoadedFor(boundsTarget.PageIndex))
            {
                if (a.Index == boundsTarget.Index)
                {
                    bounds = new AnnotationBoundsState(
                        boundsTarget.PageIndex, a.Index, a.Left, a.Top, a.Right, a.Bottom);
                    break;
                }
            }

            // The annotation is gone, so there is nothing to restore to. Fall
            // back to the remembered rectangle rather than dropping the step.
            bounds ??= boundsTarget;
        }

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
            Bounds = bounds,
            // Highlights and ink are immutable records, so copying the list is
            // a real snapshot. Notes are mutable (their text is edited after
            // creation), so their VALUES are captured instead.
            Highlights = _allHighlights.ToList(),
            InkStrokes = _allInkStrokes.ToList(),
            Shapes = _allShapes.ToList(),
            Notes = _allNotes.Select(n => new NoteState(n.PageIndex, n.X, n.Y, n.Text)).ToList(),
        };
    }

    /// <summary>Records the pre-edit state. Call immediately BEFORE mutating.</summary>
    private void PushHistory(HistoryScope scope, string label,
                             AnnotationBoundsState? bounds = null)
    {
        // A bounds step records the rectangle as it is RIGHT NOW, which is
        // where undo has to put it back to. Passed through Capture rather than
        // patched on afterwards, since HistoryEntry is deliberately immutable.
        _history.Push(Capture(scope, label, bounds));
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
        // A per-annotation step restores one rectangle and leaves everything
        // else alone. It must NOT fall through to the overlay restore below:
        // this annotation lives in the document, not in those lists, and
        // rewriting them would wipe marks made since.
        if (entry.Scope == HistoryScope.AnnotationBounds && entry.Bounds is { } b)
        {
            const int CaptureWidth = 1000;
            int status = RenderCoreNative.resize_annotation(
                _documentHandle, b.PageIndex, b.Index, CaptureWidth,
                (float)(b.Left * CaptureWidth), (float)(b.Top * CaptureWidth),
                (float)(b.Right * CaptureWidth), (float)(b.Bottom * CaptureWidth),
                out int newIndex);

            Diag.Log($"undo bounds p{b.PageIndex}#{b.Index} -> {status}, index now {newIndex}");

            if (status == RenderStatus.OkPdfium)
            {
                IsDirty = entry.WasDirty;
                _selectedLoaded = new LoadedSelection(
                    b.PageIndex, newIndex, b.Left, b.Top, b.Right, b.Bottom);
                _loadedGrip = LoadedAnnotationPicker.Grip.None;
                _loadedDrag = null;
                InvalidateLoadedPage(b.PageIndex);
                RefreshSelectionOutline();
                OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
            }

            NotifyHistoryChanged();
            return;
        }

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
                    Thumbnails.Add(new PageThumbnail(i) { CardWidth = ThumbnailDisplayWidth });
                }
            }
        }

        _allHighlights.Clear();
        _allHighlights.AddRange(entry.Highlights);
        _allInkStrokes.Clear();
        _allInkStrokes.AddRange(entry.InkStrokes);
        _allShapes.Clear();
        _allShapes.AddRange(entry.Shapes);
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

        Shapes.Clear();
        foreach (var sh in _allShapes.Where(sh => sh.PageIndex == CurrentPageIndex))
        {
            Shapes.Add(sh);
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
                    var mr = ScaledRect.From(normalized, SlotLayoutWidth);
                    if (mr.IsVisible)
                    {
                        slot?.SearchMatchRects.Add(mr);
                    }
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
