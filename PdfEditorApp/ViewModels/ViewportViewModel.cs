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
using PdfEditorApp.Rendering.Skia;
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

    /// <summary>
    /// Renders pages dark for night reading.
    ///
    /// Applied to the rendered PIXELS, never to the document, so nothing is
    /// written back and turning it off simply re-renders. Deliberately not
    /// applied to print output, which is the one thing that must come out the
    /// way the document actually looks.
    /// </summary>
    [ObservableProperty]
    public partial bool IsNightMode { get; set; }

    partial void OnIsNightModeChanged(bool value)
    {
        // Every cached bitmap has the old treatment baked in, and the transform
        // is not its own inverse, so none of them can be fixed up: they have to
        // be dropped and re-rendered from source.
        foreach (var slot in PageSlots)
        {
            slot.ClearTiles();
            slot.ReleaseBitmap();
        }

        // Re-render through the render WINDOW, not by walking every slot.
        //
        // Walking them was the first version and it was badly wrong: on a
        // 3,352-page book one toggle queued hundreds of base renders, the page
        // actually on screen sat behind all of them showing white, and the
        // sharpen pass was starved so what did appear stayed at low resolution.
        // Both reported symptoms, one cause. The window knows which handful of
        // pages are worth rendering; this asks it.
        RepaintVisibleWindow();
        ScheduleSharpenPass();
        RefreshThumbnailsForNightMode();
        Diag.Log($"night mode {(value ? "on" : "off")}, visible window re-rendered");
    }

    /// <summary>
    /// Applies the night treatment to a fresh render, if it is on.
    ///
    /// Called in the RAW phase, which runs off the UI thread, so the per-pixel
    /// pass never lands in the middle of a scroll.
    /// </summary>
    private RawPageRender ForReading(RawPageRender raw)
    {
        if (IsNightMode && raw.Bgra is not null)
        {
            NightMode.Apply(raw.Bgra);
        }

        return raw;
    }

    /// <summary>
    /// One thumbnail, treated the same way the page is.
    ///
    /// Thumbnails follow night mode rather than staying bright. A strip of
    /// white panels down the side of a dark page is the thing the mode exists
    /// to avoid looking at.
    /// </summary>
    private PageRenderResult RenderThumbnail(int pageIndex) =>
        PageRenderer.ToBitmap(ForReading(
            PageRenderer.RenderLowResRaw(_documentHandle, pageIndex, ThumbnailPixelWidth)));

    /// <summary>
    /// Re-renders the thumbnails already built, after the mode changes.
    ///
    /// Only the ones that actually have a bitmap: the panel is virtualized, so
    /// that is the realized handful rather than one per page. The count is
    /// logged because this runs on the UI thread, and if it ever stops being a
    /// handful the log will say so before anyone has to guess.
    /// </summary>
    private void RefreshThumbnailsForNightMode()
    {
        int redrawn = 0;

        for (int i = 0; i < Thumbnails.Count; i++)
        {
            if (Thumbnails[i].Bitmap is not null)
            {
                Thumbnails[i].Bitmap = RenderThumbnail(i).Bitmap;
                redrawn++;
            }
        }

        Diag.Log($"night mode: {redrawn} thumbnails re-rendered");
    }

    /// <summary>
    /// Re-runs the render window over the view that is already on screen.
    ///
    /// The pump takes ZOOMED offsets and the stored view bounds are unzoomed,
    /// so they are multiplied back. Used when the pixels have to be rebuilt
    /// without the view having moved.
    /// </summary>
    private void RepaintVisibleWindow()
    {
        double zoom = Math.Max(0.01, _currentZoomFactor);

        UpdateVisibleWindow(
            _lastViewTop * zoom,
            (_lastViewBottom - _lastViewTop) * zoom,
            zoom,
            _lastViewLeft * zoom,
            (_lastViewRight - _lastViewLeft) * zoom);
    }

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

    /// <summary>Whether search distinguishes upper from lower case.</summary>
    [ObservableProperty]
    public partial bool SearchMatchCase { get; set; }

    /// <summary>Whether search requires the query to stand alone as a word.</summary>
    [ObservableProperty]
    public partial bool SearchWholeWord { get; set; }

    /// <summary>Selection rects for the current page, normalized. The per-slot copies are what the cards draw.</summary>
    public ObservableCollection<TextRect> SelectionRects { get; } = new();

    [ObservableProperty]
    public partial ToolMode ActiveTool { get; set; } = ToolMode.Select;

    /// <summary>Committed annotations for the current page only — repopulated whenever the page changes.</summary>
    public ObservableCollection<HighlightAnnotation> Highlights { get; } = new();
    public ObservableCollection<NoteAnnotation> Notes { get; } = new();
    public ObservableCollection<InkStrokeAnnotation> InkStrokes { get; } = new();

    public ObservableCollection<ShapeAnnotation> Shapes { get; } = new();

    /// <summary>The stroke currently being drawn (Draw tool, drag in progress), or null between strokes.</summary>
    /// <summary>
    /// The stroke being drawn, smoothed, as the preview should show it.
    ///
    /// Smoothed HERE rather than only when the pen lifts. Both this and
    /// EndInkStroke run the same pure function over the same raw samples, so
    /// they cannot disagree and the stroke cannot change shape at the moment
    /// of release. Smoothing only on commit is the split-path failure that
    /// made a rounded rectangle snap square when the pointer came up.
    /// </summary>
    public IReadOnlyList<(double X, double Y)>? CurrentStrokeInProgress =>
        _currentStroke is null ? null : StrokeSmoothing.Smooth(_currentStroke);

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

    // ---------------- Bookmarks (the document's own outline) ----------------

    /// <summary>The document's outline, flat and in reading order, indented by depth.</summary>
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();

    /// <summary>
    /// Whether this document has an outline at all. Most PDFs do not, and the
    /// panel needs to say "this file has no bookmarks" rather than show an
    /// empty box that looks like a failure to load.
    /// </summary>
    [ObservableProperty]
    public partial bool HasBookmarks { get; set; }

    /// <summary>The "no bookmarks" explanation, shown only when there are none.</summary>
    public Visibility NoBookmarksVisibility =>
        HasBookmarks ? Visibility.Collapsed : Visibility.Visible;

    partial void OnHasBookmarksChanged(bool value) =>
        OnPropertyChanged(nameof(NoBookmarksVisibility));

    /// <summary>
    /// Reads the document's outline.
    ///
    /// Cheap even on a very large book: titles and destinations come from the
    /// catalog and the page tree, so nothing here LOADS a page. That is worth
    /// stating because the same assumption was wrong for form fields and cost
    /// 34 seconds on a 3352-page document.
    /// </summary>
    private void LoadBookmarks()
    {
        Bookmarks.Clear();
        HasBookmarks = false;

        // Whatever was pending belonged to the outline that was on screen a
        // moment ago. This is the file's own outline being read, so the two
        // cannot both be true.
        _pendingOutline = null;
        OnPropertyChanged(nameof(HasUnsavedOutline));

        if (_documentHandle == 0)
        {
            return;
        }

        var buf = RenderCoreNative.get_bookmarks(_documentHandle);
        try
        {
            if (buf.Status == (int)RenderStatus.OkPdfium && buf.Data != IntPtr.Zero && buf.Len > 0)
            {
                byte[] bytes = new byte[(int)buf.Len];
                Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
                foreach (var mark in BookmarkReader.Parse(bytes))
                {
                    Bookmarks.Add(new BookmarkItem(mark));
                }
            }
        }
        finally
        {
            RenderCoreNative.free_byte_buffer(buf);
        }

        HasBookmarks = Bookmarks.Count > 0;
    }

    /// <summary>
    /// Jumps to a bookmark's page. Ignores an entry with no target rather than
    /// jumping somewhere arbitrary.
    /// </summary>
    public void GoToBookmark(BookmarkItem item)
    {
        if (item.Mark.HasTarget)
        {
            GoToPage(item.PageIndex);
        }
    }

    // ---------------- Auto bookmarks ----------------

    /// <summary>
    /// Scans the document's text for headings.
    ///
    /// Runs off the UI thread and reports progress, because reading a page's
    /// text LOADS and parses that page: on a 3352-page book this is thousands
    /// of parses and takes real time however it is written. The text layers are
    /// deliberately NOT cached here the way <see cref="TextLayerFor"/> caches
    /// them, since holding every glyph of every page would cost far more memory
    /// than the scan is worth.
    /// </summary>
    public Task<IReadOnlyList<DetectedHeading>> ScanForHeadingsAsync(
        string pattern, IProgress<double>? progress, CancellationToken token)
    {
        ulong handle = _documentHandle;
        int pages = PageCount;
        int width = (int)SlotLayoutWidth;

        if (handle == 0 || pages <= 0)
        {
            return Task.FromResult<IReadOnlyList<DetectedHeading>>(Array.Empty<DetectedHeading>());
        }

        return Task.Run<IReadOnlyList<DetectedHeading>>(() =>
        {
            var texts = new List<string>(pages);
            for (int i = 0; i < pages; i++)
            {
                token.ThrowIfCancellationRequested();

                // Repaired for the same reason the styled runs are: a document
                // that records its text in painting order gives Devanagari with
                // every short-i sign in front of its consonant, and a heading
                // pattern would be matching nonsense.
                texts.Add(DevanagariText.Repair(TextLayerLoader.Load(handle, i, width).Text));

                // Every 16 pages rather than every page: on a long book the
                // progress callback marshals to the UI thread, and doing that
                // 3352 times costs more than the scan it is reporting on.
                if ((i & 0xF) == 0)
                {
                    progress?.Report((double)i / pages);
                }
            }

            progress?.Report(1.0);
            return HeadingDetector.Detect(texts, pattern);
        }, token);
    }

    // ---------------- Editing the outline ----------------
    //
    // ⚠️ Writing an outline means rewriting the FILE. PDFium cannot create a
    // bookmark, so the document is closed, the tree is written by lopdf, and it
    // is reopened: around two seconds on a 54 MB book, measured.
    //
    // Doing that per keystroke made Ctrl+B a two-second freeze on exactly the
    // documents worth bookmarking, and forced a save nobody asked for. So an
    // edit changes the outline IN MEMORY, shows immediately, and marks the
    // document dirty; the file is rewritten when the document is saved, along
    // with everything else. That is what every editor does, and it means the
    // existing unsaved-changes prompt already covers bookmarks.

    /// <summary>
    /// The outline as edited, waiting to be written, or null when the file's
    /// own outline is what is on screen.
    /// </summary>
    private List<DetectedHeading>? _pendingOutline;

    /// <summary>Whether a save has bookmarks to write as well.</summary>
    public bool HasUnsavedOutline => _pendingOutline is not null;

    /// <summary>Adds one bookmark, in memory. Written on the next save.</summary>
    /// <returns>Null on success, or a message saying what stopped it.</returns>
    public string? AddBookmark(string title, int pageIndex) =>
        EditOutline(marks => OutlineEdits.Add(marks, title, pageIndex));

    public string? RenameBookmark(int index, string title) =>
        EditOutline(marks => OutlineEdits.Rename(marks, index, title));

    public string? DeleteBookmark(int index) =>
        EditOutline(marks => OutlineEdits.Remove(marks, index));

    private string? EditOutline(Func<IReadOnlyList<Bookmark>, IReadOnlyList<DetectedHeading>> edit)
    {
        if (_documentHandle == 0)
        {
            return "No document is open.";
        }

        // Checked HERE rather than at write time, so a reader is told before
        // they have made a dozen edits that cannot go anywhere.
        if (_currentDocumentPath is null)
        {
            return "Save the document first: bookmarks are written into the file.";
        }

        var edited = edit([.. Bookmarks.Select(b => b.Mark)]);

        _pendingOutline = [.. edited];
        ShowOutline(edited);

        // The document now differs from the file, which is what the dirty dot,
        // the unsaved-changes prompt and Ctrl+S all key off. Nothing else has
        // to learn that bookmarks exist.
        IsDirty = true;
        OnPropertyChanged(nameof(HasUnsavedOutline));
        return null;
    }

    /// <summary>
    /// Puts an edited outline on screen without touching the file.
    ///
    /// Depth counts from zero and level from one, which is the only difference
    /// between what the panel shows and what the writer takes.
    /// </summary>
    private void ShowOutline(IReadOnlyList<DetectedHeading> headings)
    {
        Bookmarks.Clear();
        foreach (var heading in headings)
        {
            Bookmarks.Add(new BookmarkItem(new Bookmark(heading.Level - 1, heading.PageIndex, heading.Title)));
        }

        HasBookmarks = Bookmarks.Count > 0;
    }

    /// <summary>
    /// Writes the pending outline into the file just saved.
    ///
    /// Called at the END of a save, once the document's own bytes are on disk:
    /// the outline writer works on a CLOSED file, so it has to be the last
    /// thing that happens.
    /// </summary>
    private void FlushPendingOutline()
    {
        if (_pendingOutline is not { } outline)
        {
            return;
        }

        // Cleared first. A write that fails must not leave the edits pending
        // for the next save to try again forever, and ApplyOutline reports its
        // own failure.
        _pendingOutline = null;
        OnPropertyChanged(nameof(HasUnsavedOutline));

        if (ApplyOutline(outline) is { } problem)
        {
            Status = problem;
            Diag.Log($"outline flush failed: {problem}");
        }
    }

    /// <summary>
    /// Reads every styled run in the document, for bookmarking by style.
    ///
    /// ONE scan, kept by the caller, and both halves of the dialog work off it:
    /// the list of styles the document uses, and the matching that turns ticked
    /// styles into headings. Scanning again for each would double the only slow
    /// part, and the dialog would have to explain why ticking a box took
    /// thirty seconds.
    ///
    /// Off the UI thread with progress and cancellation for the same reason the
    /// pattern scan is: reading a page's text LOADS and parses that page, so a
    /// long book is thousands of parses however this is written.
    /// </summary>
    public Task<IReadOnlyList<StyledRunPage>> ScanStyledRunsAsync(
        IProgress<double>? progress, CancellationToken token)
    {
        ulong handle = _documentHandle;
        int pages = PageCount;

        if (handle == 0 || pages <= 0)
        {
            return Task.FromResult<IReadOnlyList<StyledRunPage>>(Array.Empty<StyledRunPage>());
        }

        return Task.Run<IReadOnlyList<StyledRunPage>>(() =>
        {
            var scanned = new List<StyledRunPage>(pages);
            for (int i = 0; i < pages; i++)
            {
                token.ThrowIfCancellationRequested();

                // Per PAGE rather than one flat list of runs, because a page
                // that gave nothing is itself the finding: it is how the dialog
                // can say "this document is a scan" instead of "no styles".
                scanned.Add(StyledRunLoader.Load(handle, i));

                // Every 16 pages, as the pattern scan does: marshalling a
                // progress report to the UI thread three thousand times costs
                // more than the scan it is reporting on.
                if ((i & 0xF) == 0)
                {
                    progress?.Report((double)i / pages);
                }
            }

            progress?.Report(1.0);
            return scanned;
        }, token);
    }

    /// <summary>
    /// Where the current text selection starts, or null when nothing is
    /// selected.
    ///
    /// Exposed as a position rather than as a style, because the runs live in
    /// the dialog that scanned for them and asking the document a second time
    /// would answer from a different reading of the same page.
    /// </summary>
    public (int Page, int CharIndex)? SelectionStart =>
        _selection is DocumentSelection selection
            ? (selection.Start.Page, selection.Start.CharIndex)
            : null;

    /// <summary>
    /// Writes an outline into the document, replacing whatever it had.
    ///
    /// The file has to be on disk and closed for this, because the outline is
    /// written by a PDF object-graph library rather than by PDFium, which has
    /// no API to create bookmarks at all. So: save any pending edits, close the
    /// document, rewrite the file, and reopen it. The same
    /// temp-then-replace-then-reopen dance the in-place save already does, and
    /// for the same reason.
    /// </summary>
    /// <returns>Null on success, or a message saying what stopped it.</returns>
    public string? ApplyOutline(IReadOnlyList<DetectedHeading> headings)
    {
        // This REPLACES the outline, so any hand edits waiting to be written
        // have just been overtaken. Leaving them pending would let the next
        // save quietly put the old outline back over this one.
        _pendingOutline = null;
        OnPropertyChanged(nameof(HasUnsavedOutline));

        if (_documentHandle == 0)
        {
            return "No document is open.";
        }
        if (_currentDocumentPath is not { } path)
        {
            return "Save the document first: bookmarks are written into the file.";
        }

        // Pending marks would be lost by the close/reopen below, so they go to
        // the file first. This also means the outline is written into a file
        // that already matches what is on screen.
        if (IsDirty && !SaveDocument())
        {
            return "Could not save the document, so the bookmarks were not written.";
        }

        byte[] buffer = BookmarkWriter.Serialise(headings);
        string temp = path + ".ayaan-outline";

        // Closed BEFORE the write, not after: PDFium streams page content from
        // the open file, and the writer is about to replace it.
        int restoreTo = CurrentPageIndex;
        CloseCurrentDocument();

        try
        {
            var status = RenderCoreNative.write_outline(path, temp, buffer, (nuint)buffer.Length);
            if (status != RenderStatus.OkPdfium)
            {
                Diag.Log($"write_outline failed: {status}");
                return status == RenderStatus.Unsupported
                    ? "This PDF could not be rewritten. Encrypted files cannot take bookmarks."
                    : "Something went wrong writing the bookmarks.";
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // The original is untouched either way. Say where the rewritten
            // copy is rather than deleting it: a failed replace must never be
            // able to lose both.
            Diag.Log($"write_outline: could not replace the original: {ex.Message}");
            return File.Exists(temp)
                ? $"Could not replace the original. The bookmarked copy is at {temp}"
                : "Could not write the bookmarks.";
        }
        finally
        {
            OpenDocument(path, preserveAnnotations: false);
            GoToPage(Math.Min(restoreTo, Math.Max(0, PageCount - 1)), animate: false);
        }

        Status = headings.Count == 0
            ? "Bookmarks removed."
            : $"Added {headings.Count} bookmarks.";
        return null;
    }

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
            // Null once single-page view can be on: the field is on a page that
            // is not currently laid out, so there is no card to outline it on.
            if (!f.IsFillable || SlotFor(f.PageIndex) is not { } slot)
            {
                continue;
            }

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

    /// <summary>Fires whenever the selection's on-screen geometry changes, so
    /// the view can move chrome anchored to it. Separate from the property
    /// notifications because the geometry can change while the COUNT does not,
    /// which is exactly what a drag does.</summary>
    public event Action? SelectionVisualsChanged;

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
    ///
    /// <paramref name="password"/> is null on the first try. A caller that gets
    /// back <see cref="DocumentOpenOutcome.NeedsPassword"/> asks the user and
    /// calls again with what they typed.
    /// </summary>
    public DocumentOpenOutcome OpenDocument(
        string path, bool preserveAnnotations = false, string? password = null)
    {
        // The document being left behind is no longer recoverable through this
        // tab, and by here the reader has already answered the unsaved-changes
        // guard. Leaving its snapshot would offer it back on the next launch as
        // though the app had crashed.
        if (_currentDocumentPath is { } leaving && !SamePath(leaving, path))
        {
            RecoveryStore.Discard(leaving);
        }

        CloseCurrentDocument();

        var opened = RenderCoreNative.open_document_protected(path, password);
        _documentHandle = opened.Handle;
        _currentDocumentPath = path;

        // The file PDFium is actually streaming this document out of, which is
        // NOT always the path the document answers to: a recovered document is
        // opened from a copy but keeps the original's name. Anything that
        // writes a PDF has to know the difference, because writing over this
        // one blanks every page.
        _backingPath = path;
        NotifyDocumentTitleChanged();

        // Recorded here rather than at the picker, so a file reaches the recent
        // list however it was opened: the picker, the recent menu itself, or a
        // reload after saving.
        //
        // Anything inside the install folder is the app's own furniture, the
        // blank template and the bundled sample, never something the user
        // chose. Without this the recent list fills with blank.pdf, since every
        // new document loads it.
        if (_documentHandle != 0 && !IsAppOwnFile(path))
        {
            RecentFilesStore.Add(path);
        }

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
        // switch would hand the new document the old one's text. Any search in
        // flight is reading those same page indices, so it goes too.
        _textLayers.Clear();
        RestartSearch();
        ClearLoadedAnnotations();

        // A view rotation belongs to the reading session, not to the file, so
        // it does not follow the reader into the next document. Set directly
        // rather than through SetViewRotation: the layout is about to be built
        // from scratch anyway, and there is no page to return to yet.
        if (ViewRotation != 0)
        {
            ViewRotation = 0;
            OnPropertyChanged(nameof(ViewRotation));
            OnPropertyChanged(nameof(IsViewRotated));
        }

        // A fresh document has no history and no unsaved edits. A reload after
        // a burn/save (preserveAnnotations) is also a clean slate: those marks
        // are now baked into the page content, so there is nothing to undo.
        _history.Clear();
        // Any half-open action belongs to the document being closed.
        _openBatch = null;
        _openBatchFallback = null;
        IsDirty = false;
        NotifyHistoryChanged();

        if (_documentHandle == 0)
        {
            PageCount = 0;

            // The path the file is ON is never logged with the attempt, and the
            // password never is at all. A trace the user might send on is not
            // the place for either.
            if (opened.Status == RenderStatus.NeedsPassword)
            {
                Status = $"{Path.GetFileName(path)} is password protected";
                Diag.Log("open refused: document is encrypted");
                return DocumentOpenOutcome.NeedsPassword;
            }

            Status = $"Failed to open {Path.GetFileName(path)}";
            return DocumentOpenOutcome.Failed;
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
        LoadBookmarks();
        LoadGuidesFromSidecar();
        return DocumentOpenOutcome.Opened;
    }

    // ---------------- Guide persistence (sidecar JSON) ----------------
    //
    // Guides live in a small file next to the PDF: <pdf>.ayaanguides.json.
    // Sidecar over in-PDF because (a) other viewers would render any real
    // annotation regardless of tag, and (b) the guides are the editor's
    // state, not the document's - Word doesn't store its ruler settings in
    // a .docx either.
    //
    // Written on Save/SaveAs (and any explicit save-guides call from the
    // menu). Read on Open. A missing sidecar is not an error.

    private sealed record GuideRecord(int Page, bool Horizontal, double Pos);
    private sealed record GuideSidecar(int Version, List<GuideRecord> Guides);

    private string? SidecarPath =>
        _currentDocumentPath is { } p ? p + ".ayaanguides.json" : null;

    /// <summary>Writes every current guide out to the sidecar for the loaded
    /// PDF. No-op if no document is open. Called from Save/SaveAs so the
    /// sidecar always lands next to the freshly-saved file.</summary>
    public void SaveGuidesToSidecar()
    {
        if (SidecarPath is not { } path) { return; }
        try
        {
            var records = new List<GuideRecord>();
            foreach (var slot in PageSlots)
            {
                foreach (var g in slot.Guides)
                {
                    records.Add(new GuideRecord(slot.PageIndex, g.Horizontal, g.NormalizedPos));
                }
            }

            // Skip writing when there are no guides AND no existing sidecar -
            // no reason to make a file just to say "empty".
            if (records.Count == 0 && !File.Exists(path)) { return; }

            var payload = new GuideSidecar(1, records);
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Diag.Log($"guides sidecar write failed: {ex.Message}");
        }
    }

    /// <summary>Reads the sidecar next to the loaded PDF, if any, and drops
    /// guides onto their pages. A malformed or missing file is silent - the
    /// user just sees no guides.</summary>
    public void LoadGuidesFromSidecar()
    {
        if (SidecarPath is not { } path) { return; }
        if (!File.Exists(path)) { return; }

        try
        {
            var json = File.ReadAllText(path);
            var payload = System.Text.Json.JsonSerializer.Deserialize<GuideSidecar>(json);
            if (payload?.Guides is null) { return; }
            foreach (var r in payload.Guides)
            {
                var slot = PageSlots.FirstOrDefault(s => s.PageIndex == r.Page);
                if (slot is null) { continue; }
                slot.Guides.Add(new GuideMark(r.Horizontal, r.Pos, slot.SlotWidth, slot.SlotHeight));
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"guides sidecar read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Forgets which annotations each page carried. Indices belong to a
    /// specific document, so carrying them across an open would address
    /// whatever now happens to sit at that position.
    /// </summary>
    private void ClearLoadedAnnotations()
    {
        _loadedByPage.Clear();
        _pageModelByPage.Clear();   // same lifetime as the cache it projects
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
    /// Asks the view to bring a page into view. Carries the page index,
    /// whether the move should be animated, and optionally a band WITHIN the
    /// page that has to end up on screen.
    ///
    /// The view model cannot scroll: the ScrollView owns the scroll position
    /// and runs the animation on the compositor. So navigation is a REQUEST
    /// the view fulfils, which also keeps the slot-space to scroll-offset
    /// conversion in the one place that already does it.
    ///
    /// The band is null for every kind of navigation but search. A bookmark or
    /// a page jump means the page, and puts its top on screen; a search match
    /// means a line somewhere inside it.
    /// </summary>
    public event Action<int, bool, PageBand?>? ScrollToPageRequested;

    public void GoToPage(int pageIndex, bool animate = true, PageBand? reveal = null)
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
        ScrollToPageRequested?.Invoke(pageIndex, animate, reveal);
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

        var thumb = RenderThumbnail(CurrentPageIndex);
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
        // index now describe the wrong pages, and so do a running search's
        // results.
        _textLayers.Clear();
        RestartSearch();
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
    /// <summary>Width/height of the currently displayed page in PDF POINTS
    /// (1/72 inch), for anything outside the view model that needs to place
    /// coordinates in physical units - notably the on-screen rulers.</summary>
    public (double WidthPoints, double HeightPoints) CurrentPagePoints()
    {
        var (w, h) = CurrentPageSizePoints();
        return (w, h);
    }

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

    /// <summary>
    /// The quarter turn the whole view is shown at.
    ///
    /// Deliberately NOT the same thing as <see cref="RotatePage"/>, which edits
    /// the document. This turns what is on screen: nothing is written, no undo
    /// step is recorded, the file is not made dirty, and closing forgets it. It
    /// is what a sideways scan needs from a reader, and it works on a document
    /// that cannot be written to at all.
    ///
    /// It is also the only one of the two that can be applied to a whole book
    /// at once. Rotating three thousand pages for real means parsing three
    /// thousand pages, which takes over a minute; this is a rebuild of the
    /// layout and nothing else.
    /// </summary>
    public int ViewRotation { get; private set; }

    /// <summary>Whether the view is turned, for a menu tick and the status bar.</summary>
    public bool IsViewRotated => ViewRotation != 0;

    /// <summary>
    /// How much the current page's content is scaled down to fit its card, on
    /// top of the zoom.
    ///
    /// One number for both axes: the scale is uniform and the rotations are
    /// quarter turns, so the on-screen size of a page point is the same
    /// horizontally and vertically whichever way the view is turned. The rulers
    /// need it, or they measure the page at its unturned size and every
    /// distance they report is out by this factor.
    /// </summary>
    public double CurrentViewScale =>
        SlotFor(CurrentPageIndex)?.ViewScale ?? 1.0;

    /// <summary>
    /// How many pages the viewport shows at once.
    ///
    /// Changing it rebuilds the stack, because the stack IS the difference:
    /// continuous view lays out every page, single-page view lays out one. It
    /// does not touch the document, so like the view rotation there is nothing
    /// to save and nothing to undo.
    /// </summary>
    public PageViewMode PageViewMode { get; private set; } = PageViewMode.Continuous;

    public bool IsSinglePageView => PageViewMode == PageViewMode.SinglePage;

    public void SetPageViewMode(PageViewMode mode)
    {
        if (mode == PageViewMode)
        {
            return;
        }

        int wasOn = CurrentPageIndex;
        PageViewMode = mode;
        OnPropertyChanged(nameof(PageViewMode));
        OnPropertyChanged(nameof(IsSinglePageView));

        RebuildContinuousLayout(pagesUnchanged: true);
        Diag.Log($"page view mode {mode}, on page {wasOn}");

        // Reuses the rotation signal: both rearrange the stack under a reader
        // who is part-way through a document and both have to put them back
        // where they were. A second event carrying the same payload to the same
        // handler would be two ways to say one thing.
        ViewRotated?.Invoke(wasOn);
    }

    /// <summary>
    /// Brings the layout onto a new current page when only one page is shown.
    ///
    /// In continuous view every page is already laid out and moving between
    /// them is a scroll. In single-page view the stack IS the current page, so
    /// turning a page is a rebuild.
    /// </summary>
    private void RelayoutForPageTurn(int page)
    {
        // _rebuildingLayout: opening a document sets the page to 0 and then
        // lays out, and other paths lay out and then correct the page. Without
        // the guard each of those would build the stack twice, and on a page
        // turn the second one would be re-entrant.
        if (PageViewMode != PageViewMode.SinglePage || _documentHandle == 0 || _rebuildingLayout)
        {
            return;
        }

        RebuildContinuousLayout(pagesUnchanged: true);
        ViewRotated?.Invoke(page);
    }

    private bool _rebuildingLayout;

    /// <summary>Turns the view a quarter clockwise.</summary>
    public void RotateViewClockwise() => SetViewRotation(ViewRotation + 90);

    /// <summary>Turns the view a quarter anticlockwise.</summary>
    public void RotateViewCounterClockwise() => SetViewRotation(ViewRotation - 90);

    /// <summary>Puts the view back upright.</summary>
    public void ResetViewRotation() => SetViewRotation(0);

    /// <summary>
    /// Applies a view rotation and puts the reader back on the page they were
    /// reading.
    ///
    /// The page has to be restored explicitly. Every card changes shape, so
    /// every offset below the current one moves, and keeping the raw scroll
    /// offset would land somewhere unrelated in a long document.
    /// </summary>
    private void SetViewRotation(int degrees)
    {
        int next = PageTransform.Normalize(degrees);
        if (next == ViewRotation)
        {
            return;
        }

        int wasOn = CurrentPageIndex;
        ViewRotation = next;
        OnPropertyChanged(nameof(ViewRotation));
        OnPropertyChanged(nameof(IsViewRotated));

        RebuildContinuousLayout(pagesUnchanged: true);

        // The panel follows the page. No re-render: the bitmaps are unchanged,
        // it is only how they are shown that turns, so this is a transform on
        // a few realized cards rather than work per page.
        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.ViewRotation = next;
        }

        Diag.Log($"view rotation {next}, back to page {wasOn}");

        ViewRotated?.Invoke(wasOn);
    }

    /// <summary>
    /// Raised after a view rotation has rebuilt the layout, with the page that
    /// was being read. The page host owns scrolling, so it restores the
    /// position; the view model does not reach into the scroller.
    /// </summary>
    public event Action<int>? ViewRotated;

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

        var thumb = RenderThumbnail(index);
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
                Thumbnails[i].Bitmap = RenderThumbnail(i).Bitmap;
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
        RestartSearch();
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

        // Outline destinations point at page OBJECTS, so PDFium resolves them
        // to the new indices after a reorder. Re-reading gets that for free;
        // remapping the numbers ourselves would get it wrong.
        LoadBookmarks();
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
    /// <summary>
    /// Turns every gradient-filled shape in a just-written file into a real PDF
    /// shading, in place.
    ///
    /// Nothing here reads the screen. The writer works from each shape's own
    /// stored gradient and the appearance PDFium wrote for it, so what lands in
    /// the file is a consequence of the document rather than of what happened
    /// to be rendered.
    ///
    /// A document with no gradient is not rewritten at all: the writer says so
    /// and does not create the destination, which is what keeps an ordinary
    /// save from paying for a feature it is not using.
    /// </summary>
    private static void WriteGradientFills(string path)
    {
        string temp = path + ".ayaan-gradients";

        int status = RenderCoreNative.write_gradients(path, temp, out int written);
        if (status != RenderStatus.OkPdfium)
        {
            Diag.Log($"write_gradients {path} -> {status}");
            return;
        }

        if (written == 0)
        {
            return;
        }

        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // The saved file is intact and its gradients are simply not painted
            // into it yet, so the shapes come out unfilled rather than wrong.
            // The rewritten copy is left where it is rather than deleted: a
            // failed replace must never be able to lose both.
            Diag.Log($"write_gradients: could not replace {path}: {ex.Message}");
        }
    }

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

        // Groups live in memory while the app runs; this is where they become
        // part of the file. Before it, every group the user made was lost the
        // moment the document closed.
        PersistGroups();

        // And this is where a text box's words become findable. Must run AFTER
        // the annotations are written, because the layer is built from their
        // tags.
        SyncSearchableText();

        // Writing over the file we have OPEN destroys it, silently.
        //
        // open_document uses load_pdf_from_file and PDFium streams page content
        // lazily from that file, so saving back over it pulls the source out
        // from under the writer: the structure is rewritten, the content
        // streams are not, and every page comes out blank. save_document still
        // returns OK and the page count is still right, so nothing looks wrong
        // until the file is reopened. Proved in
        // saving_over_the_open_file_is_what_plain_save_has_to_do.
        //
        // So an in-place save goes to a temp file first, and the original is
        // only replaced once the document has been closed and PDFium has let go
        // of it. This covers Save, and equally a Save As where the user picks
        // the file that is already open.
        bool inPlace = _currentDocumentPath is { } current && SamePath(current, path);
        string writePath = inPlace ? path + ".ayaan-saving" : path;

        bool saved = RenderCoreNative.save_document(_documentHandle, writePath) == RenderStatus.OkPdfium;

        // A gradient becomes real PDF paint HERE, on the file PDFium has just
        // written, because PDFium cannot create a shading and so cannot put one
        // in the appearance stream it generates. It runs on the file rather
        // than on the open document for the same reason the outline writer
        // does: lopdf works file to file, and the document PDFium is streaming
        // from must never be the file being rewritten.
        //
        // AFTER the save and BEFORE the swap, so an in-place save rewrites the
        // temporary copy and the original is only replaced once.
        if (saved)
        {
            WriteGradientFills(writePath);
        }

        if (inPlace && saved)
        {
            CloseCurrentDocument();
            try
            {
                File.Move(writePath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // The original is untouched and the saved copy is intact under
                // the temp name. Say where it is rather than deleting it: a
                // failed replace must never be able to lose both.
                Diag.Log($"save: could not replace the original: {ex.Message}");
                Status = $"Could not replace the original. Your saved copy is at {writePath}";
                OpenDocument(path, preserveAnnotations: false);
                return false;
            }
        }

        // An in-place save MUST reload as well, because the document was closed
        // above to release the file.
        if (saved && (burned || inPlace))
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

            // The working document becomes the file just written, which is what
            // every editor does after a Save As and what makes a following Save
            // go to the right place. Only the reload paths above set this via
            // OpenDocument, so a save with nothing to burn used to leave the
            // app still pointing at the file it was opened from.
            string wasAt = _currentDocumentPath ?? string.Empty;
            _currentDocumentPath = path;
            NotifyDocumentTitleChanged();

            // The work is in the user's own file now, so there is nothing left
            // to recover. Cleared under the path the snapshot was FILED under,
            // which after a Save As is where the document used to be rather
            // than where it now is.
            RecoveryStore.Discard(wasAt);
            if (!SamePath(wasAt, path))
            {
                RecoveryStore.Discard(path);
            }
            _lastSnapshotUtc = DateTime.UtcNow;

            // Last, because the outline writer works on a CLOSED file and
            // closes and reopens the document to do it. Everything above has to
            // have finished with the handle first.
            //
            // Safe against recursion: ApplyOutline saves first only when the
            // document is dirty, and IsDirty was cleared above.
            FlushPendingOutline();
        }

        return saved;
    }

    // ---------------- Crash recovery ----------------
    //
    // A snapshot of the open document, written to a copy the app owns, so an
    // afternoon's marks survive a crash. It is NOT an auto-save: nothing here
    // writes the user's file, because a save that happens without being asked
    // for is a save that cannot be declined, and committing marks to a document
    // somebody was only reading is worse than losing them.

    private DateTime _lastEditUtc = DateTime.MinValue;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;

    /// <summary>
    /// The file the open document is being streamed out of.
    ///
    /// Usually the same as the document's path, and deliberately NOT the same
    /// after a recovery, where the document is opened from a copy but answers
    /// to the original's name.
    /// </summary>
    private string? _backingPath;

    /// <summary>
    /// Whether there is work a crash would lose.
    ///
    /// Highlights and notes count even when the document itself is clean: they
    /// live in the app's memory until a save writes them, so they are exactly
    /// the marks a crash takes.
    /// </summary>
    public bool HasUnsavedWork => IsDirty || _allHighlights.Count > 0 || _allNotes.Count > 0;

    /// <summary>
    /// Takes a snapshot if it is time to, and says whether it did.
    ///
    /// Called on a timer. The decision is <see cref="CrashRecovery"/>'s, which
    /// is pure and tested; this half is the part that touches PDFium and the
    /// disk.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> SnapshotIfDueAsync()
    {
        var now = DateTime.UtcNow;

        if (_documentHandle == 0 || _snapshotInFlight)
        {
            return false;
        }

        if (!CrashRecovery.ShouldSnapshot(HasUnsavedWork, now - _lastEditUtc, now - _lastSnapshotUtc))
        {
            return false;
        }

        return await SnapshotNowAsync();
    }

    private bool _snapshotInFlight;

    private DispatcherQueueTimer? _snapshotTimer;

    /// <summary>
    /// How often the policy is CONSULTED, which is not how often a snapshot is
    /// taken: the policy says no on almost every tick, and saying no costs a
    /// subtraction. Frequent enough that the first snapshot after a burst of
    /// editing lands promptly rather than up to two minutes late.
    /// </summary>
    private static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Starts the crash-recovery timer. Idempotent.
    /// </summary>
    public void StartRecoverySnapshots()
    {
        if (_snapshotTimer is not null)
        {
            return;
        }

        _snapshotTimer = _dispatcherQueue.CreateTimer();
        _snapshotTimer.Interval = SnapshotPollInterval;
        _snapshotTimer.IsRepeating = true;
        _snapshotTimer.Tick += async (_, _) => await SnapshotIfDueAsync();
        _snapshotTimer.Start();
    }

    /// <summary>
    /// Writes the snapshot, whatever the timer thinks.
    ///
    /// ⚠️ Does NOT call WriteAnnotationObjects, PersistGroups or
    /// SyncSearchableText, which is what saving does first. Those MUTATE the
    /// open document and do not clear what they wrote, which is why the save
    /// path reopens the file afterwards to avoid writing everything twice.
    /// Calling them on a timer would duplicate every mark in the document the
    /// user is still working in, on a two-minute cycle. So the snapshot is a
    /// pure READ of the document as it stands, and the marks that are not in it
    /// yet travel beside it in the manifest.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> SnapshotNowAsync()
    {
        _snapshotInFlight = true;
        try
        {
            ulong handle = _documentHandle;
            string original = _currentDocumentPath ?? string.Empty;
            string snapshot = RecoveryStore.PathForSnapshot(original);

            // ⚠️ NEVER write over the file this document is being streamed
            // from. PDFium reads page content lazily out of it, so an in-place
            // write returns OK, keeps the page count, and blanks every page.
            //
            // RestoreFrom already opens a copy so this cannot line up, but the
            // consequence is destroying the user's recovered work in the act of
            // trying to protect it, and that deserves a second lock on the
            // door rather than a comment saying it cannot happen.
            if (SamePath(snapshot, _backingPath ?? string.Empty))
            {
                Diag.Log("recovery: refusing to snapshot over the open file");
                return false;
            }

            // Off the UI thread. Measured at about 0.4ms per page, so the
            // 3,352-page book takes over a second, and that second must not be
            // one where the window stops answering.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool ok = await Task.Run(() =>
                RenderCoreNative.save_document(handle, snapshot) == RenderStatus.OkPdfium);

            if (!ok || handle != _documentHandle)
            {
                Diag.Log($"recovery: snapshot not taken (ok={ok})");
                return false;
            }

            RecoveryStore.Commit(new RecoveryRecord(original, snapshot, DateTime.UtcNow.Ticks, PageCount)
            {
                Highlights = PendingHighlights(),
                Notes = PendingNotes(),
            });

            _lastSnapshotUtc = DateTime.UtcNow;
            Diag.Log($"recovery: snapshot of {PageCount} pages in {clock.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"recovery: snapshot failed: {ex.Message}");
            return false;
        }
        finally
        {
            _snapshotInFlight = false;
        }
    }

    private List<RecoveredHighlight> PendingHighlights()
    {
        var list = new List<RecoveredHighlight>();
        foreach (var h in _allHighlights)
        {
            var rects = new List<RecoveredRect>();
            foreach (var r in h.Rects)
            {
                rects.Add(new RecoveredRect(r.Left, r.Top, r.Right, r.Bottom));
            }
            list.Add(new RecoveredHighlight(h.PageIndex, h.ColorHex, rects));
        }
        return list;
    }

    private List<RecoveredNote> PendingNotes()
    {
        var list = new List<RecoveredNote>();
        foreach (var n in _allNotes)
        {
            list.Add(new RecoveredNote(n.PageIndex, n.X, n.Y, n.Text ?? string.Empty));
        }
        return list;
    }

    /// <summary>
    /// Opens a snapshot as the document it was taken from.
    ///
    /// The document is loaded from the SNAPSHOT but answers to the ORIGINAL's
    /// path, so Ctrl+S goes where the reader expects. It stays dirty, because
    /// the original on disk does not contain any of this yet, and the snapshot
    /// is discarded only once a real save has happened.
    /// </summary>
    public bool RestoreFrom(RecoveryRecord record)
    {
        // Opened from a COPY, never from the snapshot itself. The restored
        // document answers to the original's path, so the next snapshot would
        // target the very file PDFium is streaming this document out of, and
        // writing back over that blanks every page while reporting success.
        if (RecoveryStore.TakeForRestore(record) is not { } working)
        {
            return false;
        }

        if (OpenDocument(working) != DocumentOpenOutcome.Opened)
        {
            Diag.Log("recovery: the snapshot itself would not open");
            return false;
        }

        foreach (var h in record.Highlights)
        {
            var rects = new List<TextRect>();
            foreach (var r in h.Rects)
            {
                rects.Add(new TextRect(r.Left, r.Top, r.Right, r.Bottom));
            }
            _allHighlights.Add(new HighlightAnnotation(h.PageIndex, rects, h.ColorHex));
        }

        foreach (var n in record.Notes)
        {
            _allNotes.Add(new NoteAnnotation(n.PageIndex, n.X, n.Y, n.Text));
        }

        // Answers to the original, not to the snapshot. Without this a save
        // would write into the app's own recovery folder and the reader would
        // never find their document again.
        _currentDocumentPath = string.IsNullOrWhiteSpace(record.OriginalPath) ? null : record.OriginalPath;

        // Nothing here is on disk in the user's file yet.
        IsDirty = true;

        DistributeAnnotationsToSlots();
        NotifyDocumentTitleChanged();
        RenderCurrentPage();

        Status = $"Recovered {CrashRecovery.DescribeDocument(record.OriginalPath)}";
        Diag.Log($"recovery: restored {record.PageCount} pages");
        return true;
    }

    /// <summary>
    /// Starts an empty document.
    ///
    /// The blank page is loaded from the app's own template, but the document
    /// must NOT remember that path: it lives in the install folder, so a Save
    /// would write over the template that every new document is made from.
    /// Clearing the path is what makes Save fall through to Save As, which is
    /// the correct behaviour for a document that has never been saved.
    /// </summary>
    public void OpenBlankDocument()
    {
        OpenDocument(Path.Combine(AppContext.BaseDirectory, "blank.pdf"));
        _currentDocumentPath = null;
        IsDirty = false;
        NotifyDocumentTitleChanged();
    }

    /// <summary>
    /// Saves back over the file this document came from. False when there is
    /// nowhere to save to yet, which is the caller's cue to run Save As.
    /// </summary>
    public bool SaveDocument()
    {
        if (_documentHandle == 0 || _currentDocumentPath is not { } path)
        {
            return false;
        }
        return SaveDocumentAs(path, flatten: false);
    }

    /// <summary>
    /// Makes the text boxes on the pages we have touched findable.
    ///
    /// Only the pages in <c>_loadedByPage</c>, which is the set the user has
    /// actually visited. A page never visited cannot hold a box created this
    /// session, and sweeping the whole document to find out would mean loading
    /// and parsing every page of it. The core no-ops a page with no text boxes,
    /// so passing a few extra costs nothing.
    /// </summary>
    private void SyncSearchableText()
    {
        if (_documentHandle == 0 || _loadedByPage.Count == 0)
        {
            return;
        }

        int[] pages = _loadedByPage.Keys.ToArray();
        int status = RenderCoreNative.sync_text_layer(
            _documentHandle, pages, (nuint)pages.Length);

        // Never fail a save over this. The document and its marks are intact
        // either way; the only loss is that the words are not searchable, which
        // is exactly where things stood before this existed.
        if (status != RenderStatus.OkPdfium)
        {
            Diag.Log($"sync_text_layer returned {status} for {pages.Length} pages");
        }
    }

    /// <summary>True for files shipped with the app rather than opened by the user.</summary>
    private static bool IsAppOwnFile(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two paths name the same file, so an in-place save is recognised
    /// however the picker spelled it.
    /// </summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An unparseable path is not worth throwing over: treat it as a
            // different file, which takes the safe direct-write branch.
            return false;
        }
    }

    /// <summary>The open file's name, or a placeholder before it has one.</summary>
    public string DocumentTitle =>
        _currentDocumentPath is { } p ? Path.GetFileName(p) : "Untitled";

    /// <summary>True once there is a path to Save to without asking.</summary>
    public bool HasDocumentPath => _currentDocumentPath is not null;

    /// <summary>
    /// The open file's path, or null for a document that has never been saved.
    ///
    /// Exposed so the reading position can be keyed against it. Read-only: the
    /// path is set by opening and saving, and nothing else may move it.
    /// </summary>
    public string? DocumentPath => _currentDocumentPath;

    /// <summary>
    /// What the title bar shows. The bullet is the unsaved marker, the same
    /// convention as every editor, so the window itself says whether there is
    /// work that would be lost.
    /// </summary>
    public string WindowTitle =>
        $"{(IsDirty ? "• " : string.Empty)}{DocumentTitle} - Ayaan PDF";

    /// <summary>
    /// What a TAB says: the file name, and a dot if it has unsaved work.
    ///
    /// Not WindowTitle, which the tabs were using. That appends " - Ayaan PDF",
    /// which is right above the taskbar and absurd on a tab inside the app that
    /// already says so: every tab read "something - Ayaan PDF" in a window
    /// titled Ayaan PDF.
    /// </summary>
    public string TabTitle =>
        // "Welcome" for a tab holding no document at all, which is the first
        // thing a new user reads. "Untitled" described it accurately and said
        // nothing: it is not an untitled document, it is not a document.
        // File > New still produces a real blank document and keeps "Untitled".
        PageCount == 0 && !HasDocumentPath
            ? "Welcome"
            : $"{(IsDirty ? "• " : string.Empty)}{DocumentTitle}";

    private void NotifyDocumentTitleChanged()
    {
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(HasDocumentPath));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));
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
        var shapeEffects = new List<string?>();
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
                // From the DRAFT, the single definition the live preview also
                // draws with. Zero for every other kind, which ignores it.
                CornerRadiusPx = (float)(sh.Draft.CornerRadius * CaptureWidth),
            });

            // The effects cross as TEXT, in the same capture-pixel space as the
            // rest of the shape, so the core needs no second scale and no
            // knowledge of which effects exist. An empty string is none.
            shapeEffects.Add(ShapeEffectsTag.TextOf(sh.Effects, CaptureWidth));
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
            int status = NativeShapes.Add(
                _documentHandle, CaptureWidth, shapes.ToArray(), shapeEffects.ToArray());
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
    /// <summary>
    /// Every page's intrinsic size, read once per document.
    ///
    /// Cached because single-page view rebuilds the layout on every page turn,
    /// and asking the document again each time would be an FFI call and a
    /// marshalled array of a few thousand structs to move forward one page.
    /// Cleared when the page set changes, which is the only thing that can
    /// invalidate it.
    /// </summary>
    private List<PageSizePoints>? _pageSizes;

    private List<PageSizePoints> PageSizes()
    {
        if (_pageSizes is not null)
        {
            return _pageSizes;
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

        _pageSizes = sizes;
        return sizes;
    }

    /// <summary>
    /// Rebuilds the stack of page cards.
    ///
    /// <paramref name="pagesUnchanged"/> keeps the cached page sizes, and is
    /// for the rebuilds that change only how pages are ARRANGED: turning the
    /// view, switching view mode, and moving to the next page in single-page
    /// view. Everything else re-reads them.
    ///
    /// Defaulting to re-reading is deliberate. A stale cache lays the document
    /// out at the shapes it used to have, and the ways to change the pages are
    /// many (insert, delete, duplicate, reorder, rotate, undo any of those,
    /// reload after save) while the ways to merely rearrange them are three.
    /// Getting the safe answer by saying nothing is the right way round.
    /// </summary>
    private void RebuildContinuousLayout(bool pagesUnchanged = false)
    {
        if (!pagesUnchanged)
        {
            _pageSizes = null;
        }

        bool outermost = !_rebuildingLayout;
        _rebuildingLayout = true;
        try
        {
            RebuildContinuousLayoutCore();
        }
        finally
        {
            if (outermost)
            {
                _rebuildingLayout = false;
            }
        }
    }

    private void RebuildContinuousLayoutCore()
    {
        PageSlots.Clear();
        _layout.Rebuild([], SlotLayoutWidth, ViewRotation);

        if (_documentHandle == 0)
        {
            RefreshSearchHighlights();

        OnPropertyChanged(nameof(ContentWidth));
            OnPropertyChanged(nameof(ContentHeight));
            return;
        }

        var sizes = PageSizes();

        // Single-page view lays out the page being read and nothing else, so
        // the scroll range is that page. The slots are therefore NOT one per
        // page any more, which is why every lookup here goes through a page
        // number rather than a position in the list.
        int only = PageViewMode == PageViewMode.SinglePage ? CurrentPageIndex : -1;

        _layout.Rebuild(sizes, SlotLayoutWidth, ViewRotation, only);
        foreach (var slot in _layout.Slots)
        {
            PageSlots.Add(new PageSlot(slot.PageIndex, slot.Transform));
        }

        Diag.Log($"layout: {PageSlots.Count} slots, content {ContentWidth:F0}x{ContentHeight:F0}" +
                 (ViewRotation != 0 ? $", view rotated {ViewRotation}" : string.Empty) +
                 (only >= 0 ? $", single page {only}" : string.Empty));

        DistributeAnnotationsToSlots();

        // The cards are new objects, so anything drawn ONTO a card rather than
        // derived from the document has to be put back. Search highlights are
        // the case that matters: single-page view lays out the page being read
        // and nothing else, so stepping to a match on another page computed its
        // rectangles against a card that did not exist yet, and the card that
        // replaced it came up empty. In continuous view every page always had a
        // card, which is why the order never mattered before.
        //
        // Cheap: it derives a handful of rectangles for ONE page, and returns
        // immediately when nothing is being searched for.
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

    /// <summary>
    /// Slot-space height of a page, or 0 if there is no such page.
    ///
    /// Pages are not all the same height, so remembering a reading position as
    /// "part-way down page N" needs the height of that particular page rather
    /// than an average.
    /// </summary>
    public double SlotHeightOf(int pageIndex) => _layout.HeightOf(pageIndex);

    /// <summary>
    /// How a page's content is turned inside its card, for anything drawn OVER
    /// that page rather than inside it.
    ///
    /// The page card, the highlights, the search hits and the selection chrome
    /// all sit inside the grid this transform is bound to, so they follow the
    /// view rotation for free. The ink layer spans the whole stack and is
    /// outside every card, so it has to apply the same turn itself.
    ///
    /// Identity for an unturned page, so a caller that always routes through it
    /// changes nothing at 0 degrees.
    /// </summary>
    public PageTransform ViewTransformOf(int pageIndex) =>
        _layout.SlotForPage(pageIndex)?.Transform
        ?? PageTransform.For(OverlayScale, OverlayScale, 0, OverlayScale);

    /// <summary>Which page contains the given Y in slot-space (ViewportHost's
    /// content minus Padding.Top), or -1 if the Y is above the first page or
    /// past the last one. Linear walk over slots is fine - typical documents
    /// have a few hundred pages at most and this only fires on a ruler drop.</summary>
    public int PageAt(double slotY)
    {
        // Walks the CARDS and asks each one which page it is, rather than
        // treating its position in the list as its page number. Those stopped
        // being the same thing when single-page view began laying out one card.
        foreach (var slot in PageSlots)
        {
            double top = _layout.TopOf(slot.PageIndex);
            double bottom = top + slot.SlotHeight;
            if (slotY >= top && slotY < bottom) { return slot.PageIndex; }
        }
        return -1;
    }

    /// <summary>Adds a guide (dragged out from a ruler) to the given page.
    /// Session-only; the guide isn't written to the PDF yet.</summary>
    public void AddGuide(int pageIndex, bool horizontal, double normalizedPos)
    {
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        if (slot is null) { return; }
        slot.Guides.Add(new GuideMark(horizontal, normalizedPos, slot.SlotWidth, slot.SlotHeight));
    }

    /// <summary>Adds four MARGIN guides (top, bottom, left, right) at the
    /// specified inset in POINTS. Called by the "Add margin guides..."
    /// dialog. If <paramref name="allPages"/>, applies to every page in the
    /// document; otherwise only the given page.</summary>
    public void AddMarginGuides(int pageIndex, double topPts, double bottomPts,
                                double leftPts, double rightPts, bool allPages)
    {
        IEnumerable<int> pages = allPages
            ? Enumerable.Range(0, PageCount)
            : new[] { pageIndex };
        foreach (int p in pages)
        {
            var slot = PageSlots.FirstOrDefault(s => s.PageIndex == p);
            if (slot is null) { continue; }
            // Query the page's own dimensions in points so a mixed-size
            // document still lands its right/bottom margin on each page's
            // actual edge, not a shared assumption.
            var (pageWpt, pageHpt) = PagePointsFor(p);
            if (pageWpt <= 0 || pageHpt <= 0) { continue; }
            slot.Guides.Add(new GuideMark(true,  topPts    / pageHpt,               slot.SlotWidth, slot.SlotHeight));
            slot.Guides.Add(new GuideMark(true,  (pageHpt - bottomPts) / pageHpt,   slot.SlotWidth, slot.SlotHeight));
            slot.Guides.Add(new GuideMark(false, leftPts   / pageWpt,               slot.SlotWidth, slot.SlotHeight));
            slot.Guides.Add(new GuideMark(false, (pageWpt - rightPts) / pageWpt,    slot.SlotWidth, slot.SlotHeight));
        }
    }

    /// <summary>Adds N COLUMN guides evenly across a content band bounded by
    /// left/right insets (in points), with a gutter between columns. Each
    /// column contributes TWO vertical guides (its left edge and right edge),
    /// so N columns adds 2N guides. First column's left edge sits at
    /// leftInsetPts; last column's right edge at (pageW - rightInsetPts).</summary>
    public void AddColumnGuides(int pageIndex, int columns, double gutterPts,
                                double leftInsetPts, double rightInsetPts, bool allPages)
    {
        if (columns < 1) { return; }
        IEnumerable<int> pages = allPages
            ? Enumerable.Range(0, PageCount)
            : new[] { pageIndex };
        foreach (int p in pages)
        {
            var slot = PageSlots.FirstOrDefault(s => s.PageIndex == p);
            if (slot is null) { continue; }
            var (pageWpt, _) = PagePointsFor(p);
            if (pageWpt <= 0) { continue; }
            double contentW = pageWpt - leftInsetPts - rightInsetPts;
            if (contentW <= 0) { continue; }
            // Column width from the standard grid formula: N columns and
            // (N-1) gutters must fit into the content band.
            double colW = (contentW - (columns - 1) * gutterPts) / columns;
            if (colW <= 0) { continue; }
            double cursor = leftInsetPts;
            for (int c = 0; c < columns; c++)
            {
                slot.Guides.Add(new GuideMark(false, cursor / pageWpt,           slot.SlotWidth, slot.SlotHeight));
                slot.Guides.Add(new GuideMark(false, (cursor + colW) / pageWpt,  slot.SlotWidth, slot.SlotHeight));
                cursor += colW + gutterPts;
            }
        }
    }

    /// <summary>Point-space dimensions of the given page. Same source
    /// <see cref="CurrentPagePoints"/> uses, exposed per-index so the margin
    /// / column presets can handle mixed-size documents correctly (a legal
    /// page + a letter page in the same PDF would want different margins).</summary>
    private (double W, double H) PagePointsFor(int pageIndex)
    {
        var array = RenderCoreNative.get_page_sizes(_documentHandle);
        try
        {
            if (array.Status == RenderStatus.OkPdfium && array.Sizes != IntPtr.Zero
                && pageIndex >= 0 && pageIndex < (int)array.Len)
            {
                int stride = Marshal.SizeOf<NativePageSize>();
                var native = Marshal.PtrToStructure<NativePageSize>(array.Sizes + (pageIndex * stride));
                if (native.Width > 0 && native.Height > 0) { return (native.Width, native.Height); }
            }
        }
        finally
        {
            RenderCoreNative.free_page_size_array(array);
        }
        return (0, 0);
    }

    /// <summary>The guide the user has picked (single-select). Delete removes
    /// it, and its appearance flips to accent red. Held as a page/guide pair
    /// because the guide lives on a specific page.</summary>
    private (int Page, GuideMark Guide)? _selectedGuide;

    /// <summary>Picks the guide under a page-local NORMALIZED pointer (nx, ny)
    /// on the given page, if any is within the tolerance. Horizontal guides
    /// match on Y, vertical on X. Tolerance is 0.005 normalized (about 4 DIPs
    /// on the 800-wide slot) which is Illustrator's rough click zone for a
    /// hairline object. Returns null when nothing's close enough.
    ///
    /// The incoming (nx, ny) are BOTH normalized by page WIDTH (the annotation
    /// convention: OverlayScale is SlotLayoutWidth). A HORIZONTAL guide's
    /// NormalizedPos is 0-1 across page HEIGHT though, so we rescale ny into
    /// height units before comparing - without this, horizontal-guide clicks
    /// and drags landed at wrong positions or missed the guide entirely.</summary>
    public GuideMark? PickGuideAt(int pageIndex, double nx, double ny)
    {
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        if (slot is null) { return null; }
        const double Tol = 0.005;
        // ny is (slotLocalY / SlotLayoutWidth); convert to (slotLocalY /
        // SlotHeight) so it lines up with horizontal-guide NormalizedPos.
        double nyInHeight = slot.SlotHeight > 0
            ? ny * SlotLayoutWidth / slot.SlotHeight
            : ny;
        foreach (var g in slot.Guides)
        {
            double d = g.Horizontal
                ? Math.Abs(nyInHeight - g.NormalizedPos)
                : Math.Abs(nx - g.NormalizedPos);
            if (d <= Tol) { return g; }
        }
        return null;
    }

    /// <summary>Selects the given guide (page + guide), highlighting it and
    /// clearing any prior selection - including annotation selection, so the
    /// property bar stops showing shape/text controls that don't apply.</summary>
    public void SelectGuide(int pageIndex, GuideMark guide)
    {
        // Clear the annotation selection so the property bar reacts (nothing
        // shape-like or text-like is picked while a guide is active).
        ClearAnnotationSelection();

        if (_selectedGuide is (_, GuideMark prev)) { prev.IsSelected = false; }
        _selectedGuide = (pageIndex, guide);
        guide.IsSelected = true;
    }

    public void ClearGuideSelection()
    {
        if (_selectedGuide is (_, GuideMark g)) { g.IsSelected = false; }
        _selectedGuide = null;
    }

    public bool HasSelectedGuide => _selectedGuide is not null;

    /// <summary>When true, guides can't be dragged to move, dragged off to
    /// delete, or removed via the Delete key. Click-select still works so
    /// the user can see which guide they're pointing at, and snap still uses
    /// them. Standard Illustrator/PageMaker "Lock Guides" affordance for
    /// preventing accidental changes during heavy edit work.</summary>
    [ObservableProperty]
    public partial bool AreGuidesLocked { get; set; }

    /// <summary>True while a click-and-hold on a selected guide is being
    /// dragged. Set by <see cref="BeginGuideDrag"/>; cleared by the release
    /// path. The MainPage pointer-move handler routes to <see cref="DragGuideTo"/>
    /// while this is set instead of falling through to the tool path.</summary>
    public bool IsDraggingGuide { get; private set; }

    public void BeginGuideDrag()
    {
        // Locked guides can be selected but not moved.
        if (AreGuidesLocked) { return; }
        if (_selectedGuide is not null) { IsDraggingGuide = true; }
    }

    /// <summary>Moves the guide currently under drag to the pointer's page-local
    /// normalized position. Horizontal guide takes ny (rescaled from width-
    /// normalized to height-normalized so the units match its NormalizedPos);
    /// vertical takes nx unchanged. Clamps to [0, 1] so a guide can't slip
    /// past a page edge.</summary>
    public void DragGuideTo(int pageIndex, double nx, double ny)
    {
        if (!IsDraggingGuide || _selectedGuide is not (int selPage, GuideMark g)) { return; }
        // A guide that follows the pointer onto a DIFFERENT page moves to the
        // NEW page. Rare in practice but keeps the interaction consistent with
        // shape-drag which also crosses pages.
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        if (slot is null) { return; }
        // Same width->height rescale as PickGuideAt: ny arrives width-normalized,
        // but a horizontal guide's NormalizedPos is height-normalized.
        double pos;
        if (g.Horizontal)
        {
            double nyInHeight = slot.SlotHeight > 0
                ? ny * SlotLayoutWidth / slot.SlotHeight
                : ny;
            pos = Math.Clamp(nyInHeight, 0, 1);
        }
        else
        {
            pos = Math.Clamp(nx, 0, 1);
        }
        g.MoveTo(pos, slot.SlotWidth, slot.SlotHeight);
        if (pageIndex != selPage)
        {
            var oldSlot = PageSlots.FirstOrDefault(s => s.PageIndex == selPage);
            oldSlot?.Guides.Remove(g);
            slot.Guides.Add(g);
            _selectedGuide = (pageIndex, g);
        }
    }

    /// <summary>Releases the drag. If <paramref name="offPage"/>, the guide is
    /// deleted (drag-off-to-delete convention). Otherwise it stays wherever
    /// the last DragGuideTo call put it.</summary>
    public void EndGuideDrag(bool offPage)
    {
        if (!IsDraggingGuide) { return; }
        IsDraggingGuide = false;
        if (offPage) { DeleteSelectedGuide(); }
    }

    /// <summary>Deletes the currently-selected guide, if any. Bound to the
    /// Delete/Backspace key path from MainPage, same as annotation delete.</summary>
    public bool DeleteSelectedGuide()
    {
        if (_selectedGuide is not (int page, GuideMark g)) { return false; }
        // Locked guides can't be deleted. Return true anyway so the Delete
        // key handler swallows the event - deleting an annotation instead
        // when the user's intent was clearly the highlighted guide would be
        // worse.
        if (AreGuidesLocked) { return true; }
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == page);
        slot?.Guides.Remove(g);
        _selectedGuide = null;
        return true;
    }

    public void ClearGuidesOnPage(int pageIndex)
    {
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        slot?.Guides.Clear();
    }

    public void ClearAllGuides()
    {
        foreach (var s in PageSlots) { s.Guides.Clear(); }
    }

    /// <summary>"7 / 20", or empty when no document is open.</summary>
    public string PagePositionLabel =>
        PageCount > 0 ? $"{CurrentPageIndex + 1} / {PageCount}" : string.Empty;

    /// <summary>
    /// The "/ 20" half of the readout. The current page is no longer text: it
    /// is an editable box the user can type a page number into, so only the
    /// total is still a label.
    /// </summary>
    public string PageCountSuffix => PageCount > 0 ? $"/ {PageCount}" : string.Empty;

    /// <summary>Shown only when nothing is open, so the canvas is never a blank void.</summary>
    public Visibility EmptyStateVisibility =>
        PageCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Whether there is a page before this one, and after it.
    ///
    /// The bar's page arrows were always live: on page 1 the back arrow looked
    /// exactly as it does on page 2 and pressing it did nothing at all. A
    /// control that offers something it cannot do is worse than one that is
    /// missing, because the reader is left wondering what they did wrong.
    /// </summary>
    public bool CanGoToPreviousPage => PageCount > 0 && CurrentPageIndex > 0;

    public bool CanGoToNextPage => PageCount > 0 && CurrentPageIndex < PageCount - 1;

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PagePositionLabel));
        OnPropertyChanged(nameof(TextAvailabilityLabel));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));

        // In single-page view the stack IS this page, so turning a page has to
        // build a new one. In continuous view the page changes constantly as
        // the reader scrolls and there is nothing to do.
        RelayoutForPageTurn(value);
    }

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PagePositionLabel));
        OnPropertyChanged(nameof(PageCountSuffix));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(TextAvailabilityLabel));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
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
    /// <summary>
    /// Renders one page for printing, at a width the caller chooses.
    ///
    /// Separate from the viewport's tiers because print resolution has nothing
    /// to do with what fits on screen: the page is rendered once, at print
    /// density, and never cached into a slot. Synchronous because the print
    /// framework asks for a page and expects an element back, with nowhere to
    /// await.
    ///
    /// Kept here rather than handing the document handle out, so the handle
    /// stays private and every render goes through the same place.
    /// </summary>
    public WriteableBitmap? RenderPageForPrint(int pageIndex, int widthPx)
    {
        if (_documentHandle == 0 || pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        var raw = PageRenderer.RenderLowResRaw(_documentHandle, pageIndex, widthPx);
        return raw.Bgra is null ? null : PageRenderer.ToBitmap(raw).Bitmap;
    }

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

            var raw = ForReading(await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, width)));

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

            // The CONTENT box, not the card, because that is what is rendered.
            // Turned a quarter, the card has the other shape entirely, and
            // sizing a render from it asks for a bitmap of the wrong aspect.
            double aspect = slot.ContentWidth > 0 ? slot.ContentHeight / slot.ContentWidth : 1.0;

            // A page turned on its side is scaled down to fit the card, so it
            // is drawn smaller than its own coordinates say. Folding that into
            // the zoom is what keeps the render matched to the pixels actually
            // on screen instead of over- or under-shooting by the scale.
            double zoom = slot.View.EffectiveZoom(_currentZoomFactor);

            // Past the point where a whole-page render has to be capped, the
            // full-page tier can no longer keep up with the screen, so the
            // visible area is drawn from the tile pyramid instead. The sharp
            // render is dropped rather than kept underneath: it would be both
            // expensive and blurrier than the tiles covering it, and the cheap
            // base bitmap is a better stand-in for the moment before a tile
            // lands.
            if (_budget.NeedsTiles(slot.ContentWidth, zoom, RasterizationScale, aspect))
            {
                slot.DropSharpRender();
                RenderVisibleTiles(slot);
                continue;
            }

            slot.ClearTiles();

            int desired = _budget.SharpWidthFor(slot.ContentWidth, zoom, RasterizationScale, aspect);
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

        // Viewport intersected with this page's CARD, in card-local slot DIPs.
        double left = Math.Max(0, _lastViewLeft);
        double right = Math.Min(slot.SlotWidth, _lastViewRight);
        double cardTop = Math.Max(0, _lastViewTop - top);
        double cardBottom = Math.Min(slot.SlotHeight, _lastViewBottom - top);

        if (right - left < 1 || cardBottom - cardTop < 1)
        {
            slot.ClearTiles();
            return;
        }

        // Tiles are addressed on the page, so the visible part of the card has
        // to be asked for in page coordinates. Turned a quarter, the strip
        // along the top of the screen is one SIDE of the page, and asking for
        // the top of the page instead would fetch tiles nobody can see while
        // leaving the visible ones blank. Quarter turns, so this is exact.
        var (pageLeft, pageTop, pageRight, pageBottom) =
            slot.View.ContentBounds(left, cardTop, right, cardBottom);

        // Level from what the screen actually needs across the whole page, at
        // the size the page is really drawn.
        int level = TileGrid.LevelForWidth(
            slot.ContentWidth * slot.View.EffectiveZoom(_currentZoomFactor) * Math.Max(1.0, RasterizationScale));

        var wanted = TileGrid.VisibleTiles(
            slot.ContentWidth, slot.ContentHeight, level, pageLeft, pageTop, pageRight, pageBottom);

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

            var raw = ForReading(await Task.Run(() =>
                PageRenderer.RenderTileRaw(handle, pageIndex, addr.Level, addr.Col, addr.Row)));

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

            var raw = ForReading(await Task.Run(() => PageRenderer.RenderUncachedRaw(handle, pageIndex, targetWidth)));

            if (handle != _documentHandle)
            {
                return;
            }

            // Drop a result the zoom has already moved past. A sharp render of
            // a large page takes long enough that a zoom gesture can finish
            // while it is in flight, and applying it would show a bitmap at
            // the wrong resolution until the next pass replaced it.
            double aspect = slot.SlotWidth > 0 ? slot.SlotHeight / slot.SlotWidth : 1.0;
            int wantedNow = _budget.SharpWidthFor(slot.ContentWidth, _currentZoomFactor, RasterizationScale, aspect);
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

            // NOT SearchMatchRects. Search owns its own highlight now and
            // refreshes it only when the selected match moves. This ran on
            // every page change while scrolling, and used to be harmless
            // because the search was recomputed in the same breath; without
            // that, clearing here wipes the highlight and nothing puts it back.
        }

        foreach (var h in _allHighlights)
        {
            if (h.PageIndex >= 0 && h.PageIndex < PageSlots.Count)
            {
                SlotFor(h.PageIndex)?.Highlights.Add(h);
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
                SlotFor(sh.PageIndex)?.Shapes.Add(sh);
            }
        }

        foreach (var s in _allInkStrokes)
        {
            if (s.PageIndex >= 0 && s.PageIndex < PageSlots.Count)
            {
                SlotFor(s.PageIndex)?.InkStrokes.Add(s);
            }
        }

        foreach (var n in _allNotes)
        {
            if (n.PageIndex >= 0 && n.PageIndex < PageSlots.Count)
            {
                SlotFor(n.PageIndex)?.Notes.Add(n);
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

        // Card coordinates so far. Everything above this call works in CONTENT
        // coordinates, which are the same whichever way the view is turned, so
        // the mapping happens here and nowhere else. Upright it is the
        // identity.
        (localX, localY) = slots[best].Transform.ToContent(slotX, slotY - slots[best].Top);
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

    /// <summary>True when the selection is a REAL PDFium annotation (text box,
    /// shape, stamp, or any loaded mark) - i.e. anything the arrow-key nudge,
    /// align/distribute, and multi-select machinery can act on. Overlay-only
    /// entities like fresh ink or highlights (selected via _selectedAnnotationId)
    /// are excluded because they don't yet participate in that path.</summary>
    public bool HasSelectedAnnotationLoaded => _selectedLoaded is not null;

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

    /// <summary>True when Group would do something: two or more objects picked.</summary>
    public bool CanGroupSelection => SelectionCount >= 2;

    /// <summary>True when the anchor belongs to a group, so Ungroup has a target.</summary>
    public bool CanUngroupSelection =>
        _selectedLoaded is LoadedSelection a && GroupContaining(a.Id) is not null;

    /// <summary>
    /// The rectangle floating chrome must keep clear of: the selection on the
    /// ANCHOR's page, in that page's slot-space DIPs, GROWN upward by the rotate
    /// handle's reach when one is showing. The handle floats above the top edge
    /// on a stem, so a box that stopped at the annotation bounds would let the
    /// toolbar land across it.
    ///
    /// Extras on OTHER pages are left out rather than unioned in: a box spanning
    /// two pages would centre the toolbar in the gutter between them, pointing
    /// at nothing.
    /// </summary>
    public bool TryGetSelectionBox(
        out int pageIndex, out double left, out double top, out double right, out double bottom)
    {
        pageIndex = -1;
        left = top = right = bottom = 0;
        if (_selectedLoaded is not LoadedSelection anchor) { return false; }

        pageIndex = anchor.PageIndex;
        left = anchor.Left;
        top = anchor.Top;
        right = anchor.Right;
        bottom = anchor.Bottom;
        foreach (var ex in _extraSelected)
        {
            if (ex.PageIndex != pageIndex) { continue; }
            left = Math.Min(left, ex.Left);
            top = Math.Min(top, ex.Top);
            right = Math.Max(right, ex.Right);
            bottom = Math.Max(bottom, ex.Bottom);
        }

        // Mirrors the condition RefreshSelectionOutline draws the handle under.
        bool hasRotateHandle =
            (_selectedIsTextBox || _selectedIsShape || _selectedIsStamp || _selectedIsInk) && CanResize(anchor);
        if (hasRotateHandle)
        {
            top -= LoadedAnnotationPicker.RotateHandleGap + LoadedAnnotationPicker.GripReach;
        }

        left *= SlotLayoutWidth;
        top *= SlotLayoutWidth;
        right *= SlotLayoutWidth;
        bottom *= SlotLayoutWidth;
        return true;
    }

    /// <summary>Whether the current selection is a shape (rectangle, ellipse,
    /// line, arrow). Set once on selection so the drag path pays no per-sample
    /// FFI cost. Cleared alongside the other selection flags.</summary>
    private bool _selectedIsShape;

    /// <summary>True when the selection is a placed IMAGE stamp, as opposed to
    /// a shape or a text box, which are also /Stamp annotations underneath.
    /// Distinguished by the tag: an image stamp carries AyaanStamp: (or, for
    /// one placed before stamps were tagged, no tag at all).</summary>
    private bool _selectedIsStamp;

    /// <summary>True when the selection is one of OUR freehand strokes, i.e. one
    /// carrying an InkTag and therefore rebuildable at a new size. A stroke from
    /// another editor has no such description and is not this.</summary>
    private bool _selectedIsInk;

    /// <summary>
    /// Whether the anchor can be redrawn at a new size, decided once when the
    /// selection changes rather than on every pointer move.
    ///
    /// The rule itself lives in <see cref="AnnotationResize"/> where it is
    /// tested; this only caches its answer. It depends solely on the object's
    /// subtype and tag, neither of which a move, resize or rebuild changes, so
    /// the cached value stays valid for as long as the same object is selected.
    /// </summary>
    private bool _selectedCanResize;

    /// <summary>The anchor's angle when a rotate drag began. The DELTA between
    /// this and the live angle is what the rest of a multi-selection turns by;
    /// the live angle alone is the anchor's absolute heading and means nothing
    /// to the others.</summary>
    private double _rotateStartDeg;

    /// <summary>Every member's rectangle when a rotate drag began, anchor
    /// first. Orbiting has to be measured from these, not from wherever the
    /// members are mid-gesture, or the group creeps outward as it turns.</summary>
    private readonly List<LoadedSelection> _rotateOrigin = new();

    /// <summary>Slot-space (DIP) inset from _selectedLoaded's /Rect back to
    /// the shape's outer stroke edge. The writer adds width/2 + 1 on every
    /// side to keep PDFium from clipping the stroke; the frame and grips
    /// draw INSIDE the /Rect by this amount so they hug the visible shape.
    /// Zero for non-shape selections.</summary>
    private double _selectedShapePadDips;

    /// <summary>The selected SHAPE's own upright box, width and height in
    /// normalized page units, or null when it cannot be recovered (anything
    /// that is not a shape, or a shape whose tag predates the recorded size).
    ///
    /// Not the same thing as _selectedLoaded's rectangle once a shape is
    /// turned: that is the axis-aligned box CONTAINING the rotation. The frame,
    /// the grips and their hit zones are laid out UPRIGHT and then turned as
    /// one, so laying them out in the /Rect turned them twice.
    ///
    /// Normalized rather than DIPs so it survives a zoom, and cached at
    /// selection time so the overlay never pays an FFI to redraw.</summary>
    /// <summary>
    /// The selected shape's own tag, kept so the frame can be worked out from
    /// the shape's CURRENT rectangle every time it is drawn.
    ///
    /// A remembered SIZE stood here and went stale the moment the shape was
    /// resized: the commit updates the rectangle and redraws the outline, but
    /// nothing recomputed the size, so the frame and its handles stayed at
    /// whatever the shape measured when it was selected. The tag is the right
    /// thing to keep because it does not change during a gesture; the size
    /// does.
    /// </summary>
    private string? _selectedShapeTag;

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
        ClearDragTimeVisuals();
        _isMovingAnnotation = false;
        _moveOrigin = null;
    }

    /// <summary>Drops every drag-time visual on every page - guide snap-flash
    /// (yellow) and smart alignment guides (orange) - unconditionally. Every
    /// drag-end path calls this so the overlays disappear no matter which
    /// path runs (PointerReleased, PointerCaptureLost, tool switch, etc.).
    /// Sets to null unconditionally (skips the "already null" fast-return)
    /// so the change-notify fires even if internal state got out of sync.</summary>
    public void ClearDragTimeVisuals()
    {
        foreach (var s in PageSlots)
        {
            foreach (var g in s.Guides) { if (g.IsSnapActive) { g.IsSnapActive = false; } }
            if (s.SmartGuideLines.Count > 0) { s.SmartGuideLines.Clear(); }
        }
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
    /// <summary>Rebuilds the selection chrome, then tells the view its geometry
    /// moved. Wrapping rather than signalling inside is deliberate: the body has
    /// several early returns, and a signal missed on one of them would strand
    /// the floating toolbar at a stale position.</summary>
    private void RefreshSelectionOutline()
    {
        RefreshSelectionOutlineCore();
        SelectionVisualsChanged?.Invoke();
    }

    private void RefreshSelectionOutlineCore()
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
            // Inset the DRAW rect by the shape's stroke pad so the frame
            // hugs the shape's outer stroke edge rather than the padded /Rect.
            // Zero pad for non-shapes; unchanged behaviour there.
            // The shape's own upright box when it is known. That box already
            // has the stroke pad off it, so the inset applies only to the
            // fallback, where the rectangle is still the raw /Rect.
            var frame = SelectionFrameOf(sel);
            double p = _selectedIsShape && _selectedShapeTag is null
                ? _selectedShapePadDips
                : 0;
            double fl = frame.Left  * SlotLayoutWidth + p;
            double ft = frame.Top   * SlotLayoutWidth + p;
            double fr = frame.Right * SlotLayoutWidth - p;
            double fb = frame.Bottom* SlotLayoutWidth - p;
            slot?.SelectionOutline.Add(new ScaledRect(
                fl, ft, fr - fl, fb - ft, string.Empty));

            // The frame and handles are laid out UPRIGHT (from the tight box) and
            // then turned as one about the box centre, so a rotated text box is
            // framed at its real angle.
            if (slot is not null)
            {
                slot.SelectionRotation = (_selectedIsTextBox || _selectedIsShape || _selectedIsStamp || _selectedIsInk) ? _selectedRotationDeg : 0;
                slot.SelectionCenterX = (frame.Left + frame.Right) / 2 * SlotLayoutWidth;
                slot.SelectionCenterY = (frame.Top + frame.Bottom) / 2 * SlotLayoutWidth;
            }

            // Grips only for what can actually be resized. Offering them on a
            // drawing, which PDFium refuses to scale, would be an invitation
            // to an error message.
            if (slot is not null && CanResize(sel))
            {
                // Edge (one-axis) handles only for a free resize; an aspect-locked
                // picture keeps just its four corners. A text box also gets the
                // rotate handle above its top edge. Grips inset by the same
                // shape-pad amount so they sit ON the frame, not outside it.
                AddGrips(slot, frame, edges: AspectToPreserve(sel) == 0,
                         rotate: _selectedIsTextBox || _selectedIsShape || _selectedIsStamp || _selectedIsInk,
                         insetDips: p);
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

    /// <summary>A selected annotation that came from the file, in normalized units.
    /// <see cref="Id"/> is the stable identity that survives the delete+re-add
    /// churn of every edit; groups reference it, so it must be carried through
    /// every resize/move so the new PDFium index gets stamped with the same Id.</summary>
    private readonly record struct LoadedSelection(
        int PageIndex, int Index, double Left, double Top, double Right, double Bottom, Guid Id = default);

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

    /// <summary>Strips any extras that duplicate the anchor's (page, index)
    /// or duplicate another extra. Called before every multi-write so the
    /// extras loop can't double-write the same annotation.
    ///
    /// The shift-click / marquee / group-expansion paths already dedup on
    /// entry, but they trust invariants that occasionally break (e.g. the
    /// plain-click "swap old anchor into extras" logic can add an entry that
    /// was already in extras). Rather than proving every entry path clean,
    /// we normalize at the multi-write boundary. Cheap; a linear scan of a
    /// selection that in practice holds a handful of items.</summary>
    private void NormalizeExtras(Guid anchorId, string callerTag)
    {
        if (_extraSelected.Count == 0) { return; }

        // Keyed on the stable Id, NEVER on (page, index).
        //
        // v2.1.2 deduped on the index and silently dropped real shapes: two
        // different group members can carry the SAME stale index between a
        // write and a cache refresh, so an index-keyed dedup sees a duplicate
        // that isn't one. The symptom was a group of three where two moved,
        // the third stayed put, and its frame moved anyway (the frame comes
        // from _extraSelected, which the drag had already updated).
        //
        // Entries whose Id is empty cannot be compared this way, so they are
        // kept: dropping a shape is far worse than writing one twice.
        var seen = new HashSet<Guid>();
        if (anchorId != Guid.Empty) { seen.Add(anchorId); }
        int removed = _extraSelected.RemoveAll(e => e.Id != Guid.Empty && !seen.Add(e.Id));
        if (removed > 0)
        {
            Diag.Log($"{callerTag}: dropped {removed} extras that were the same annotation as another; extras now {_extraSelected.Count}");
            if (_extraDragOrigin.Count > _extraSelected.Count)
            {
                _extraDragOrigin.Clear();
                _extraDragOrigin.AddRange(_extraSelected);
            }
        }
    }

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
        AbsorbGroupsFrom(list);
        return list;
    }

    /// <summary>
    /// Rebuilds session groups from what the document says, as pages load.
    ///
    /// Groups used to live only in memory, so every group the user made
    /// evaporated when the file closed. They are written to the annotations on
    /// save and read back here.
    ///
    /// Per page rather than in one pass at open, because pages load lazily and
    /// scanning a 300-page document to find groupings the user may never look
    /// at would undo that. A group spanning pages assembles as its pages load,
    /// which is enough: nothing can act on a member before its page is loaded.
    /// </summary>
    private void AbsorbGroupsFrom(IReadOnlyList<Interop.ExistingAnnotation> loaded)
    {
        foreach (var byGroup in loaded
            .Where(a => a.GroupId != Guid.Empty && a.Id != Guid.Empty)
            .GroupBy(a => a.GroupId))
        {
            var members = byGroup.Select(a => a.Id).ToList();

            // Merge into whichever existing group already holds any of these,
            // so the halves of a group split across pages become one group
            // rather than two.
            var existing = _groups.FirstOrDefault(g => g.Any(members.Contains));
            if (existing is null)
            {
                if (members.Count >= 1) { _groups.Add(members); }
                continue;
            }
            foreach (var id in members)
            {
                if (!existing.Contains(id)) { existing.Add(id); }
            }
        }
    }

    /// <summary>
    /// Writes every session group onto its members, so they survive the file
    /// being closed. Called just before a save.
    ///
    /// The group's id is its smallest member Guid: deterministic, needs no
    /// extra state, and a change of membership is a different group anyway.
    /// Marks in no group have any stale id cleared, so an ungroup sticks.
    /// </summary>
    private void PersistGroups()
    {
        if (_documentHandle == 0) { return; }

        var groupOf = new Dictionary<Guid, Guid>();
        foreach (var g in _groups)
        {
            if (g.Count < 2) { continue; }
            Guid key = g.Min();
            foreach (var id in g) { groupOf[id] = key; }
        }

        foreach (int page in _loadedByPage.Keys.ToList())
        {
            foreach (var a in LoadedFor(page))
            {
                Guid wanted = groupOf.TryGetValue(a.Id, out var k) ? k : Guid.Empty;
                if (wanted != a.GroupId)
                {
                    Interop.AnnotationLoader.WriteGroup(
                        _documentHandle, page, a.Index, wanted == Guid.Empty ? null : wanted);
                }
            }
        }
        Diag.Log($"PersistGroups: {_groups.Count(g => g.Count >= 2)} groups written");
    }

    /// <summary>
    /// The topmost OBJECT under a point, asked of the document model.
    ///
    /// This used to test each annotation's /Rect, and /Rect is not the object:
    /// it is the drag's extent grown by the stroke pad, and for anything turned
    /// it is the axis-aligned box of the rotated content. A diagonal arrow could
    /// therefore be selected anywhere inside a box it barely touches, which is
    /// the bug this replaces.
    ///
    /// Handed back as an <see cref="AnnotationBox"/> so everything downstream is
    /// unchanged. The three fields callers use all mean exactly what they meant
    /// before: <c>Index</c> is the annotation's position on the page (the model's
    /// ZOrder IS that index), the rectangle is the annotation's own /Rect, which
    /// the move, resize and frame-drawing paths are all threaded on, and the Id
    /// is the same stable identity. Only WHICH object comes back has changed.
    ///
    /// PageModelFor reads through LoadedFor, so the group absorption that used
    /// to happen here still happens, and the model shares the annotation cache's
    /// lifetime so it can never be staler than the data it replaces.
    /// </summary>
    private AnnotationBox? PickLoadedAt(int pageIndex, double normX, double normY)
    {
        var picked = ObjectHitTest.PickTopmost(
            PageModelFor(pageIndex), normX, normY, AnnotationHitTester.DefaultTolerance);

        if (picked is null)
        {
            return null;
        }

        return new AnnotationBox(
            picked.ZOrder,
            picked.Bounds.Left, picked.Bounds.Top, picked.Bounds.Right, picked.Bounds.Bottom,
            picked.Id);
    }

    /// <summary>
    /// Picks the topmost annotation already in the file under a point.
    /// Later entries are drawn on top, so the search runs backwards.
    /// </summary>
    private bool SelectLoadedAt(int pageIndex, double normX, double normY)
    {
        if (PickLoadedAt(pageIndex, normX, normY) is not AnnotationBox hit)
        {
            return false;
        }

        // Grouping: a plain click on any group member selects the WHOLE group.
        // The clicked mark becomes the anchor and the other members become
        // extras. Shift-click bypasses group expansion (so a shift-click on a
        // group member removes just that one from the multi-selection, which
        // matches Illustrator's ungroup-on-shift behaviour). Membership is
        // read once here on click and pinned to the selection until the next
        // click; index shifts inside the drag can't confuse the anchor because
        // it's cached above.
        // Make the clicked annotation's Id real before anything relies on it.
        // A mark that has never been stamped gets a fresh ephemeral Id from
        // every cache reload, so any Id captured into a selection or a group
        // would be stale the next time the page reloaded. Writing it on the
        // click is the earliest point where we know the user cares about this
        // particular mark.
        if (hit.Id != Guid.Empty)
        {
            Interop.AnnotationLoader.WriteId(_documentHandle, pageIndex, hit.Index, hit.Id);
        }

        bool shift = IsShiftDown();
        Diag.Log($"SelectLoadedAt hit p{pageIndex}#{hit.Index} id={hit.Id:N} shift={shift} groupsCount={_groups.Count} inGroup={(GroupContaining(hit.Id) is not null)}");
        if (!shift && GroupContaining(hit.Id) is { } group && group.Count > 1)
        {
            Diag.Log($"  expanding group of {group.Count} members");
            _extraSelected.Clear();
            _selectedLoaded = new LoadedSelection(
                pageIndex, hit.Index, hit.Left, hit.Top, hit.Right, hit.Bottom, hit.Id);
            // Extras = every other group member. Resolve each Guid back to
            // its current (page, index) via the loaded cache; a member on an
            // unvisited page or one whose page hasn't been loaded yet is
            // silently dropped from the pick.
            foreach (var memberId in group)
            {
                if (memberId == hit.Id) { continue; }
                if (FindLoadedById(memberId) is not (int p, int idx)) { continue; }
                var page = LoadedFor(p);
                int mi = page.FindIndex(a => a.Index == idx);
                if (mi < 0) { continue; }
                var found = page[mi];
                _extraSelected.Add(new LoadedSelection(p, idx, found.Left, found.Top, found.Right, found.Bottom, found.Id));
            }
            ApplyTextBoxSelectionInfo(pageIndex, hit.Index);
            var gsel = _selectedLoaded.Value;
            _loadedDrag = (normX, normY, gsel);
            _loadedGrip = GripForPoint(gsel, normX, normY);
            _extraDragOrigin.Clear();
            if (_loadedGrip == LoadedAnnotationPicker.Grip.None)
            {
                _extraDragOrigin.AddRange(_extraSelected);
            }
            RefreshSelectionOutline();
            OnPropertyChanged(nameof(HasSelectedAnnotation));
            OnPropertyChanged(nameof(HasSelectedAnnotationLoaded));
            OnPropertyChanged(nameof(HasSelectedTextBox));
            OnPropertyChanged(nameof(HasSelectedShape));
            OnPropertyChanged(nameof(HasMultiSelection));
            return true;
        }

        // Shift-click grows the selection. If the clicked mark was already in
        // the multi-selection it is removed (deselected individually); otherwise
        // the previous anchor moves to the extras list and the clicked mark
        // becomes the new anchor. A plain click clears the extras.
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
            // Plain click on any member of the current multi-selection (anchor
            // OR extras) PRESERVES the group and starts dragging the whole
            // thing - the convention every editor follows. Only when the click
            // lands on something OUTSIDE the current selection does the group
            // collapse to just the clicked mark. Without this, marquee-select
            // eight lines then click-drag one and only that one moved.
            bool clickedInSelection =
                (_selectedLoaded is LoadedSelection cur
                    && cur.PageIndex == pageIndex && cur.Index == hit.Index)
                || _extraSelected.Any(x => x.PageIndex == pageIndex && x.Index == hit.Index);

            if (clickedInSelection)
            {
                // Keep the extras exactly as they are, and if the clicked mark
                // was an extra, promote it to anchor so subsequent per-anchor
                // ops (rotate handle, resize grip) act on the mark under the
                // pointer. Its previous position in extras is dropped and the
                // old anchor moves in.
                int wasExtra = _extraSelected.FindIndex(x =>
                    x.PageIndex == pageIndex && x.Index == hit.Index);
                if (wasExtra >= 0 && _selectedLoaded is LoadedSelection oldAnchor)
                {
                    _extraSelected.RemoveAt(wasExtra);
                    _extraSelected.Add(oldAnchor);
                }
            }
            else
            {
                _extraSelected.Clear();
            }
        }

        _selectedLoaded = new LoadedSelection(
            pageIndex, hit.Index, hit.Left, hit.Top, hit.Right, hit.Bottom, hit.Id);
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

        // A rotate gesture needs its own starting state: the anchor's angle, so
        // a delta can be derived, and every member's rectangle, so the orbit is
        // measured from where the group actually was.
        if (!shift && _loadedGrip == LoadedAnnotationPicker.Grip.Rotate)
        {
            _rotateStartDeg = _selectedRotationDeg;
            _rotateOrigin.Clear();
            _rotateOrigin.Add(sel);
            _rotateOrigin.AddRange(_extraSelected);
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
        _selectedShapePadDips = 0;
        _selectedShapeTag = null;
        _selectedIsRoundedRect = false;
        // Whether the selection is a shape is decided by whether its /Contents
        // parses as our shape tag; the shape check comes first because it is a
        // cheap prefix test and rules out most other marks.
        string? contents = ReadAnnotationContents(pageIndex, index);
        _selectedIsShape = contents is not null && contents.StartsWith("AyaanShape:", StringComparison.Ordinal);

        // An image stamp is what is left once shapes and text boxes are
        // ruled out: all three are /Stamp annotations underneath, so the
        // TAG is what separates them, not the subtype. A stamp placed
        // before stamps were tagged has no tag, hence the empty case.
        _selectedIsStamp = !_selectedIsShape
            && !TextBoxTagReader.TryParse(contents, out _)
            && (string.IsNullOrEmpty(contents)
                || contents!.StartsWith("AyaanStamp:", StringComparison.Ordinal));
        if (_selectedIsStamp)
        {
            _selectedRotationDeg = ParseStampRotation(contents);
        }

        // One of our strokes, and therefore whether it can be resized at all.
        // Decided HERE, where the tag has already been read, so the hover and
        // drag paths never pay an FFI for it. Both are set before the text-box
        // parse below, which returns early for everything that is not one.
        _selectedIsInk = InkTag.TryParse(contents, out _, out _, out _, out double inkAngle);
        _selectedCanResize = AnnotationResize.CanResize(SubtypeOf(pageIndex, index), contents);
        if (_selectedIsInk)
        {
            _selectedRotationDeg = inkAngle;
        }

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

            // Mirror the shape's fill too, so the Fill picker shows this shape's
            // ACTUAL fill (which the tool state does not know about — a user can
            // pick a shape drawn a week ago). Null clears the picker to No Fill.
            ShapeFillHex = ParseShapeFill(contents!);

            // And its corners, for the same reason: the slider must show where
            // THIS shape is, not where the tool was left. The tag holds the
            // radius in POINTS, so it goes back through the page width to become
            // the fraction of maximum the slider works in.
            _selectedIsRoundedRect = ParseShapeKind(contents!) == (int)ShapeKind.RoundedRectangle;
            if (_selectedIsRoundedRect && _selectedLoaded is LoadedSelection box)
            {
                var (wpt, _) = PagePointsFor(pageIndex);
                double radiusNorm = wpt > 0 ? ParseShapeCornerRadiusPts(contents!) / wpt : 0;
                ShapeCornerPercent = ShapeGeometry.CornerFractionFromRadius(
                    radiusNorm, box.Right - box.Left, box.Bottom - box.Top) * 100;
            }

            // Compute the pad the writer added around the stroke (width/2 + 1
            // on every side, so PDFium doesn't clip). Stored in slot DIPs so
            // RefreshSelectionOutline / AddGrips can INSET the drawn frame and
            // grips by it - keeping _selectedLoaded on the /Rect (which the
            // drag + commit pipeline is threaded on) while the VISUAL frame
            // hugs the shape's outer stroke edge. This is what makes the
            // right/bottom gap disappear without breaking drag (v1.95.1's
            // mistake was shrinking the storage bounds).
            double widthPts = ParseShapeStrokeWidthPts(contents!);
            var (pageWpt, _) = PagePointsFor(pageIndex);
            _selectedShapePadDips = (widthPts > 0 && pageWpt > 0)
                ? (widthPts / 2.0 + 1.0) * SlotLayoutWidth / pageWpt
                : 0;

            // The tag, so the frame can be worked out from the shape's own
            // rectangle whenever it is drawn. See SelectionFrameOf.
            _selectedShapeTag = contents;
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
            ? new LoadedSelection(sel.PageIndex, newIndex, a.Left, a.Top, a.Right, a.Bottom, sel.Id)
            : sel with { Index = newIndex };
        RefreshSelectionOutline();
    }

    /// <summary>Applies the current <see cref="ShapeFillHex"/> to the selected
    /// shape. Null clears the fill (stroke-only). This is a separate path from
    /// ApplyStyleToSelectedShape because fill has its own picker and it would
    /// be surprising if picking a fill also re-wrote the stroke colour with
    /// whatever InkColorHex happens to be.</summary>
    /// <summary>
    /// How round the corners of a rounded rectangle are, 0 to 100, where 100 is
    /// the roundest the box can be (half its shorter side). Doubles as the tool
    /// state for the NEXT shape drawn and as the editor for the selected one,
    /// which is how colour and stroke width already behave here.
    ///
    /// A percentage of the maximum rather than an absolute measurement, so the
    /// control means the same thing on a badge and on a full-page box, and so
    /// the number stays meaningful after a resize.
    /// </summary>
    [ObservableProperty]
    public partial double ShapeCornerPercent { get; set; }
        = ShapeGeometry.DefaultCornerFraction * 100;

    /// <summary>True when the selection is a rounded rectangle, so the corner
    /// slider can show itself only where it does something.</summary>
    public bool HasSelectedRoundedRect => _selectedIsRoundedRect;

    private bool _selectedIsRoundedRect;

    /// <summary>
    /// Re-writes the selected rounded rectangle at the corner radius the slider
    /// is on. Bounds, colour, stroke width and fill are untouched: the core
    /// rebuilds everything else from the shape's own tag.
    /// </summary>
    public void ApplyCornerRadiusToSelectedShape()
    {
        if (_documentHandle == 0
            || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsShape
            || !_selectedIsRoundedRect)
        {
            return;
        }

        const int CaptureWidth = 1000;
        double radiusNorm = ShapeGeometry.CornerRadiusFromFraction(
            ShapeCornerPercent / 100.0,
            sel.Right - sel.Left,
            sel.Bottom - sel.Top);

        PushHistory(HistoryScope.Document, "Corner radius");
        int status = RenderCoreNative.restyle_shape_radius_annotation(
            _documentHandle, sel.PageIndex, sel.Index, CaptureWidth,
            (float)(radiusNorm * CaptureWidth), out int newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not change that corner radius.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);

        // Same re-selection dance as the fill restyle: the shape was deleted and
        // re-added, so the marquee has to follow it to its new index.
        var actual = LoadedFor(sel.PageIndex)
            .Where(x => x.Index == newIndex)
            .Select(x => (Interop.ExistingAnnotation?)x)
            .FirstOrDefault();
        _selectedLoaded = actual is Interop.ExistingAnnotation a
            ? new LoadedSelection(sel.PageIndex, newIndex, a.Left, a.Top, a.Right, a.Bottom, sel.Id)
            : sel with { Index = newIndex };
        RefreshSelectionOutline();
    }

    /// <summary>
    /// ALL of the selected shape's effects, read from its tag, or null when
    /// there is no shape selected or it has none.
    ///
    /// THE PRIMITIVE both named views and every edit go through. Read from the
    /// annotation rather than held as tool state, because a row has to show what
    /// THIS shape is, not what the controls were last left at, and because an
    /// edit has to start from the shape's CURRENT list or it would send one
    /// without whatever it did not know about.
    /// </summary>
    public ShapeEffects? SelectedShapeEffects
    {
        get
        {
            if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel
                || !_selectedIsShape)
            {
                return null;
            }

            string? contents = ReadAnnotationContents(sel.PageIndex, sel.Index);
            if (contents is null || !ShapeTagReader.TryParse(contents, out var tag))
            {
                return null;
            }

            return ShapeEffectsTag.From(tag, PagePointsFor(sel.PageIndex).W);
        }
    }

    /// <summary>
    /// The selected shape's OWN drop shadow, or null when it casts none.
    /// Narrowed out of <see cref="SelectedShapeEffects"/>, never stored beside
    /// it.
    /// </summary>
    public DropShadow? SelectedShapeShadow => SelectedShapeEffects?.Shadow;

    /// <summary>The selected shape's OWN glow, on the same terms.</summary>
    public Glow? SelectedShapeGlow => SelectedShapeEffects?.Glow;

    /// <summary>
    /// What the selected shape is filled with: nothing, one colour, or a
    /// gradient.
    ///
    /// Read out of the tag on demand exactly as its effects are, and for the
    /// same reason: the document is the authority, and a copy kept beside it is
    /// a copy that drifts. Not to be confused with
    /// <see cref="ShapeFillHex"/>, which is the TOOL's colour for shapes about
    /// to be drawn.
    /// </summary>
    public ShapeFill SelectedShapeFill
    {
        get
        {
            if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel
                || !_selectedIsShape)
            {
                return ShapeFill.None;
            }

            string? contents = ReadAnnotationContents(sel.PageIndex, sel.Index);

            return contents is not null && ShapeTagReader.TryParse(contents, out var tag)
                ? ShapeFillTag.From(tag)
                : ShapeFill.None;
        }
    }

    /// <summary>
    /// The selected shape's OWN gradient, or null when it has none. Narrowed
    /// out of <see cref="SelectedShapeFill"/> the way the shadow and the glow
    /// are narrowed out of the effects.
    /// </summary>
    public GradientFill? SelectedShapeGradient => SelectedShapeFill.Gradient;

    /// <summary>
    /// What Skia paints for the selected shape on top of PDFium's rendering of
    /// it, which is nothing at all unless that shape has a gradient.
    ///
    /// THE ONE COMMITTED SHAPE THE LIVE SURFACE DRAWS, and only while it is
    /// selected and only while it needs it. PDFium cannot make a shading, so a
    /// gradient reaches the appearance stream only when the file is saved; this
    /// is what stands in between choosing one and saving. Every other shape on
    /// every page is PDFium's, as it has been since v1.72.
    ///
    /// Read from the tag on demand, like the fill and the effects, and for the
    /// same reason: the document is the authority.
    /// </summary>
    public IReadOnlyList<ShapeRenderItem> SelectedShapeOverlayItems
    {
        get
        {
            if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel
                || !_selectedIsShape)
            {
                return Array.Empty<ShapeRenderItem>();
            }

            string? contents = ReadAnnotationContents(sel.PageIndex, sel.Index);
            if (contents is null || !ShapeTagReader.TryParse(contents, out var tag))
            {
                return Array.Empty<ShapeRenderItem>();
            }

            const int CaptureWidth = 1000;
            double pageWidthPts = PagePointsFor(sel.PageIndex).W;
            var (l, t, r, b) = UprightBounds(contents, sel, CaptureWidth, pageWidthPts);

            return SelectedShapeOverlay.ItemsFor(tag, sel.PageIndex, l, t, r, b, pageWidthPts);
        }
    }

    /// <summary>The selected shape's page width in points, which is what turns
    /// the row's points into the model's normalized lengths. Zero when there is
    /// no selection.</summary>
    public double SelectedShapePageWidthPts =>
        _selectedLoaded is LoadedSelection sel ? PagePointsFor(sel.PageIndex).W : 0;

    /// <summary>
    /// Gives the selected shape a drop shadow, changes the one it has, or takes
    /// it away when handed null.
    ///
    /// Goes through the core's shadow override, which rebuilds the shape from
    /// its tag with only the shadow replaced, so the rotation, colour, width,
    /// fill and corners all survive. A null shadow writes a zero colour, and
    /// the core reads that as "no shadow" and drops the field from the tag
    /// entirely rather than leaving an invisible one behind.
    /// </summary>
    public void ApplyShadowToSelectedShape(DropShadow? shadow) =>
        ApplyEffectsToSelectedShape(
            (SelectedShapeEffects ?? new ShapeEffects()).With(shadow),
            shadow is null ? "Remove shadow" : "Drop shadow",
            "Could not apply that shadow.");

    /// <summary>
    /// Gives the selected shape a glow, changes the one it has, or takes it
    /// away when handed null. The shadow's twin, on the same terms.
    /// </summary>
    public void ApplyGlowToSelectedShape(Glow? glow) =>
        ApplyEffectsToSelectedShape(
            (SelectedShapeEffects ?? new ShapeEffects()).With(glow),
            glow is null ? "Remove glow" : "Glow",
            "Could not apply that glow.");

    /// <summary>
    /// Writes the selected shape's WHOLE effects list, replacing whatever it
    /// had.
    ///
    /// THE ONLY WAY EFFECTS ARE WRITTEN, and the reason the two rows above are
    /// two lines each. The core replaces the list wholesale rather than merging
    /// field by field, so an edit that sends only its own effect deletes every
    /// other one: moving the shadow's slider would take the glow off, and
    /// touching either would take off an effect written by a later build. Both
    /// start from the shape's current list and change one thing in it.
    /// </summary>
    private void ApplyEffectsToSelectedShape(
        ShapeEffects? effects, string historyLabel, string failure)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsShape)
        {
            return;
        }

        // THE WHOLE TAIL, fill included. The core replaces it wholesale, so
        // sending only the effects would take the shape's gradient off every
        // time somebody nudged the shadow's slider. The fill is read back from
        // the shape rather than passed in, because this edit is not about it,
        // and its positional half is left alone for the same reason.
        WriteShapePaint(sel, SelectedShapeFill, effects, solid: null, historyLabel, failure);
    }

    /// <summary>
    /// Gives the selected shape a gradient fill, changes the one it has, or
    /// takes it away when handed null.
    ///
    /// TWO WRITES, ONE UNDO. A gradient lives on the tag's tail and a solid
    /// lives in the tag's positional fill field, and the two must never both be
    /// set: PDFium paints the positional fill as part of the appearance it
    /// generates, so a leftover solid would be painted straight over the
    /// shading the save-time writer puts underneath it. The core exposes one
    /// entry point for each, so this makes both calls inside a single history
    /// push and the pair undoes together.
    ///
    /// THE WHOLE TAIL GOES, effects included, for the same reason the effect
    /// rows send the whole effect list: the core replaces it wholesale, so
    /// sending only the gradient would take the shape's shadow and glow off.
    ///
    /// Turning a gradient off leaves the shape in its START colour rather than
    /// blank. The solid it had before the gradient was cleared when the
    /// gradient was applied and is not remembered anywhere, and going blank
    /// would make the shape vanish under a switch that only said "solid".
    /// </summary>
    public void ApplyGradientToSelectedShape(GradientFill? gradient)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsShape)
        {
            return;
        }

        const int CaptureWidth = 1000;

        var effects = SelectedShapeEffects;
        var fill = gradient is { } g ? ShapeFill.Of(g) : ShapeFill.None;

        // What the shape is left filled with once the gradient is gone. Read
        // BEFORE the writes, because the first of them replaces the annotation.
        uint solid = gradient is null
            ? SolidAfterGradient(SelectedShapeFill)
            : 0;

        WriteShapePaint(
            sel, fill, effects, solid,
            gradient is null ? "Remove gradient" : "Gradient fill",
            "Could not apply that gradient.");
    }

    /// <summary>
    /// Writes a shape's WHOLE paint: the tail that carries any gradient, then
    /// the positional field that carries any solid.
    ///
    /// THE ONLY WAY BOTH ARE SET, so they cannot end up disagreeing. One
    /// history push covers the pair, because a person switching a fill did one
    /// thing and expects one undo.
    /// </summary>
    /// <param name="solid">
    /// The positional fill to leave the shape with, or NULL to leave whatever
    /// it already has.
    ///
    /// Null is the ordinary case and costs a write: an edit to an effect has no
    /// opinion about the fill, and rewriting it would rebuild the annotation a
    /// second time on every drag of a slider.
    /// </param>
    private void WriteShapePaint(
        LoadedSelection sel,
        ShapeFill fill,
        ShapeEffects? effects,
        uint? solid,
        string label,
        string failure)
    {
        const int CaptureWidth = 1000;

        PushHistory(HistoryScope.Document, label);

        string tail = ShapeFillTag.TailOf(fill, effects, CaptureWidth);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(tail);

        int status = RenderCoreNative.restyle_shape_effects_annotation(
            _documentHandle, sel.PageIndex, sel.Index, CaptureWidth,
            utf8, (nuint)utf8.Length, out int newIndex);

        if (status == RenderStatus.OkPdfium && solid is { } rgba)
        {
            status = RenderCoreNative.restyle_shape_fill_annotation(
                _documentHandle, sel.PageIndex, newIndex, CaptureWidth, rgba, out newIndex);
        }

        if (status != RenderStatus.OkPdfium)
        {
            Status = failure;
            return;
        }

        if (solid is { } written)
        {
            ShapeFillHex = written == 0 ? null : $"#{written:X8}";
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);
        FollowRestyledShape(sel, newIndex);
    }

    /// <summary>
    /// The solid a shape keeps when its gradient is taken away: the gradient's
    /// own first stop, opaque, or nothing at all when there was no gradient.
    /// </summary>
    private static uint SolidAfterGradient(ShapeFill fill)
    {
        if (fill.Gradient is not { } gradient)
        {
            return 0;
        }

        var c = gradient.From;

        return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    /// <summary>
    /// Moves the marquee onto the annotation a restyle just re-added, which
    /// every shape restyle has to do because the core deletes and re-adds
    /// rather than editing in place.
    /// </summary>
    private void FollowRestyledShape(LoadedSelection sel, int newIndex)
    {
        var actual = LoadedFor(sel.PageIndex)
            .Where(x => x.Index == newIndex)
            .Select(x => (Interop.ExistingAnnotation?)x)
            .FirstOrDefault();

        _selectedLoaded = actual is Interop.ExistingAnnotation a
            ? new LoadedSelection(sel.PageIndex, newIndex, a.Left, a.Top, a.Right, a.Bottom, sel.Id)
            : sel with { Index = newIndex };

        RefreshSelectionOutline();
    }

    public void ApplyFillToSelectedShape()
    {
        if (_documentHandle == 0
            || _selectedLoaded is not LoadedSelection sel
            || !_selectedIsShape)
        {
            return;
        }

        const int CaptureWidth = 1000;
        uint fillRgba = PackShapeFillRgba(ShapeFillHex);

        // A SOLID REPLACES A GRADIENT, it does not sit under one. The gradient
        // is the more specific paint and wins wherever both are recorded, so
        // picking a colour on a gradient-filled shape would otherwise appear to
        // do nothing at all. Taking the gradient off needs the tail rewritten
        // as well, which is a second write and belongs with the first.
        if (SelectedShapeFill.Gradient is not null)
        {
            WriteShapePaint(
                sel, ShapeFill.None, SelectedShapeEffects, fillRgba,
                "Shape fill", "Could not apply that fill.");
            return;
        }

        PushHistory(HistoryScope.Document, "Shape fill");
        int status = RenderCoreNative.restyle_shape_fill_annotation(
            _documentHandle, sel.PageIndex, sel.Index, CaptureWidth,
            fillRgba, out int newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            Status = "Could not apply that fill.";
            return;
        }

        IsDirty = true;
        InvalidateLoadedPage(sel.PageIndex);

        // Rebuild the marquee on the re-added shape (same as ApplyStyleToSelectedShape).
        var actual = LoadedFor(sel.PageIndex)
            .Where(x => x.Index == newIndex)
            .Select(x => (Interop.ExistingAnnotation?)x)
            .FirstOrDefault();
        _selectedLoaded = actual is Interop.ExistingAnnotation a
            ? new LoadedSelection(sel.PageIndex, newIndex, a.Left, a.Top, a.Right, a.Bottom, sel.Id)
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
                sel.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom, sel.Id);
        }
        else
        {
            _selectedLoaded = sel with { Index = newIndex };
        }
        RefreshSelectionOutline();
    }

    /// <summary>
    /// Turns a whole multi-selection about its own centre.
    ///
    /// Two writes per member, because the core keeps position and angle apart:
    /// the orbit is a bounds write and the spin is an angle write, and
    /// rotate_shape_annotation takes no bounds so they cannot be combined.
    /// Members are located by stable Id between the two, since the first write
    /// deletes and re-adds and so moves the index.
    /// </summary>
    private void CommitGroupRotation()
    {
        const int CaptureWidth = 1000;
        double delta = _selectedRotationDeg - _rotateStartDeg;
        if (Math.Abs(delta) < 0.01)
        {
            RefreshSelectionOutline();
            return;
        }

        var rects = _rotateOrigin
            .Select(m => (m.Left, m.Top, m.Right, m.Bottom))
            .ToList();
        if (GroupRotation.BoundingBox(rects) is not { } box)
        {
            RefreshSelectionOutline();
            return;
        }
        double pivotX = (box.Left + box.Right) / 2.0;
        double pivotY = (box.Top + box.Bottom) / 2.0;

        BeginEdit("Rotate group");

        var placed = new List<LoadedSelection>();
        foreach (var member in _rotateOrigin)
        {
            if (FindLoadedById(member.Id, member.PageIndex) is not (int page, int index))
            {
                continue;
            }

            // Its own heading before this gesture, so the spin ADDS to whatever
            // it already had rather than resetting it.
            string? tag = ReadAnnotationContents(page, index);
            double ownAngle = tag is null ? 0 : AngleFromTag(tag);

            var orbited = GroupRotation.OrbitRect(
                (member.Left, member.Top, member.Right, member.Bottom), pivotX, pivotY, delta);

            RecordEdit(new BoundsRecord(
                member.Id, page,
                new EditRect(member.Left, member.Top, member.Right, member.Bottom),
                new EditRect(orbited.Left, orbited.Top, orbited.Right, orbited.Bottom)));

            // 1. Orbit.
            var target = new LoadedSelection(
                page, index, orbited.Left, orbited.Top, orbited.Right, orbited.Bottom, member.Id);
            int moved = WriteMovedAnnotation(target, target, CaptureWidth);
            if (moved < 0) { continue; }
            InvalidateLoadedPage(page);
            Interop.AnnotationLoader.WriteId(_documentHandle, page, moved, member.Id);
            InvalidateLoadedPage(page);

            // 2. Spin, at the new home.
            if (FindLoadedById(member.Id, page) is not (int p2, int i2)) { continue; }
            float newAngle = (float)(ownAngle + delta);
            int spun = i2;
            bool isText = TextBoxTagReader.TryParse(ReadAnnotationContents(p2, i2), out _);
            if (isText)
            {
                RenderCoreNative.rotate_text_box_annotation(
                    _documentHandle, p2, i2, CaptureWidth,
                    (float)(orbited.Left * CaptureWidth), (float)(orbited.Top * CaptureWidth),
                    (float)(orbited.Right * CaptureWidth), (float)(orbited.Bottom * CaptureWidth),
                    newAngle, out spun);
            }
            else if (ReadAnnotationContents(p2, i2) is string t2
                     && t2.StartsWith("AyaanStamp:", StringComparison.Ordinal))
            {
                RenderCoreNative.rotate_stamp_annotation(
                    _documentHandle, p2, i2, CaptureWidth, newAngle, out spun);
            }
            else
            {
                RenderCoreNative.rotate_shape_annotation(
                    _documentHandle, p2, i2, CaptureWidth, newAngle, out spun);
            }

            InvalidateLoadedPage(p2);
            if (spun >= 0)
            {
                Interop.AnnotationLoader.WriteId(_documentHandle, p2, spun, member.Id);
                InvalidateLoadedPage(p2);
            }

            placed.Add(new LoadedSelection(
                p2, spun, orbited.Left, orbited.Top, orbited.Right, orbited.Bottom, member.Id));
        }

        CommitEdit();
        IsDirty = true;

        // Rebuild the selection from what actually landed, anchor first.
        if (placed.Count > 0)
        {
            _selectedLoaded = placed[0];
            _extraSelected.Clear();
            for (int i = 1; i < placed.Count; i++) { _extraSelected.Add(placed[i]); }
        }
        _rotateOrigin.Clear();
        RefreshSelectionOutline();
    }

    /// <summary>An annotation's own rotation, whatever kind it is, or 0.</summary>
    private static double AngleFromTag(string tag)
    {
        if (tag.StartsWith("AyaanShape:", StringComparison.Ordinal)) { return ParseShapeRotation(tag); }
        if (tag.StartsWith("AyaanStamp:", StringComparison.Ordinal)) { return ParseStampRotation(tag); }
        if (InkTag.TryParse(tag, out _, out _, out _, out double inkDeg)) { return inkDeg; }
        return TextBoxTagReader.TryParse(tag, out var box) ? box.RotationDeg : 0;
    }

    /// <summary>
    /// The upright rectangle the selection frame, its grips and their hit zones
    /// are laid out in, before the overlay turns the whole assembly about its
    /// centre.
    ///
    /// One method for all three. A frame drawn in one rectangle and grabbed in
    /// another is worse than either mistake on its own, and before this they
    /// agreed only because both were wrong the same way.
    ///
    /// WORKED OUT EVERY TIME, from the shape's CURRENT rectangle. A remembered
    /// size stood here and went stale the moment the shape was resized: the
    /// commit updated the rectangle and redrew the outline, but nothing
    /// recomputed the size, so the frame and its handles kept whatever the
    /// shape measured when it was selected. Measured in the app, resizing a
    /// rectangle from 0.3543 wide to 0.7628: the shape followed the drag
    /// exactly and the frame stayed at 0.3543.
    ///
    /// It also has to be the whole rectangle rather than a size centred on the
    /// annotation's own. A shadow grows /Rect on ONE side, which moves its
    /// centre by half the offset, so a box centred there sat that far off the
    /// shape. UprightBounds takes the stroke pad, the shadow's offset and the
    /// blur's reach off all the right sides.
    /// </summary>
    private (double Left, double Top, double Right, double Bottom) SelectionFrameOf(
        LoadedSelection sel) =>
        _selectedIsShape && _selectedShapeTag is string tag
            ? UprightBounds(tag, sel, 1000, PagePointsFor(sel.PageIndex).W)
            : (sel.Left, sel.Top, sel.Right, sel.Bottom);

    /// <summary>The handle under a point, accounting for the box's rotation: the
    /// pointer is turned back into the box's own upright frame first, then the
    /// rotate handle (above the top edge) and the resize handles are tested.</summary>
    private LoadedAnnotationPicker.Grip GripForPoint(LoadedSelection sel, double nx, double ny)
    {
        // The same rectangle the handles are DRAWN in, or a handle would be
        // grabbable somewhere other than where it appears.
        var f = SelectionFrameOf(sel);
        var box = new AnnotationBox(sel.Index, f.Left, f.Top, f.Right, f.Bottom);
        var (lx, ly) = InverseRotate(nx, ny, box, _selectedRotationDeg);
        if ((_selectedIsTextBox || _selectedIsShape || _selectedIsStamp || _selectedIsInk)
            && LoadedAnnotationPicker.IsRotateHandle(box, lx, ly))
        {
            return LoadedAnnotationPicker.Grip.Rotate;
        }
        // Shapes are free-resize (edge handles) too; text boxes always are, and
        // so is a stroke, which has no aspect to protect. Ink is listed here
        // because AddGrips DRAWS its eight handles (AspectToPreserve returns 0
        // for it), and a handle that is drawn but not grabbable is worse than
        // one that was never offered.
        return LoadedAnnotationPicker.GripAt(box, lx, ly,
            edges: _selectedIsTextBox || _selectedIsShape || _selectedIsInk);
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

    /// <summary>Alt disables object snapping during a move, so the user can
    /// place freely when snap keeps grabbing something they don't want.</summary>
    private static bool IsAltDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
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
        // Diagnostic: prove counts + first-extra state at each drag frame.
        // Only log on the first move of a gesture to keep the log tractable.
        if (_extraSelected.Count > 0 && _extraDragOrigin.Count == 0)
        {
            var e0 = _extraSelected[0];
            Diag.Log($"DragLoadedTo start: extras.Count={_extraSelected.Count} origins.Count=0 extra[0]=p{e0.PageIndex}#{e0.Index} at ({e0.Left:F3},{e0.Top:F3}) anchor=p{start.PageIndex}#{start.Index} at ({start.Left:F3},{start.Top:F3})");
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

            // Object snap: nudge the moved box so its edges/centres line up with
            // other annotations' edges/centres on the same page when close. Alt
            // held disables snapping so a user can place freely when the snap
            // wants to grab something unwanted. Snap runs BEFORE the extras
            // delta below so the whole group inherits the snap - one anchor snap
            // moves every extra by the same amount, so relative positions hold.
            if (!IsAltDown())
            {
                moved = SnapMovedToNearbyAnnotations(start.PageIndex, moved);
            }
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

        // Move-all: on a body drag with extras selected, apply the anchor's
        // total delta to each extra's ORIGINAL position. Auto-snapshot the
        // origins if the cached list doesn't match the current extras count
        // - covers group expansion, shift-click, or any other path that
        // might set up extras without capturing their origins.
        if (_loadedGrip == LoadedAnnotationPicker.Grip.None && _extraSelected.Count > 0)
        {
            if (_extraDragOrigin.Count != _extraSelected.Count)
            {
                _extraDragOrigin.Clear();
                _extraDragOrigin.AddRange(_extraSelected);
                Diag.Log($"DragLoadedTo: auto-snapshotted {_extraDragOrigin.Count} extras");
            }
        }
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

    /// <summary>How close (in normalized page-width units) an edge or centre
    /// has to come to another annotation's edge/centre before snapping. 0.005
    /// is 5 units on the 1000-wide capture, about 3-4 pixels on screen at fit-
    /// width - close enough to feel intentional, far enough to not fight normal
    /// dragging.</summary>
    private const double SnapThresholdNorm = 0.005;

    /// <summary>Guides get a WIDER snap threshold than object edges: the user
    /// placed them intentionally as alignment targets, so they should grab
    /// more eagerly. Matches Illustrator's convention of a bigger guide zone.</summary>
    private const double GuideSnapThresholdNorm = 0.012;

    /// <summary>Returns the moved box shifted so its nearest edge or centre
    /// lines up with a NEARBY OTHER annotation on the same page. Independent
    /// axes: X can snap to one annotation while Y snaps to another. Snapping
    /// preserves the box's size (a snap on Left shifts Right by the same
    /// amount). Same-anchor and extras-in-motion are excluded because a shape
    /// should never snap to itself or to its own moving companions.</summary>
    private AnnotationBox SnapMovedToNearbyAnnotations(int pageIndex, AnnotationBox moved)
    {
        var candidates = LoadedFor(pageIndex);
        if (candidates.Count == 0) { return moved; }

        // Set of indices to ignore: the anchor being dragged and every extra
        // being moved with it. Snapping to those is nonsense - they follow the
        // anchor by the same delta so their positions are correlated.
        var ignore = new HashSet<int>();
        if (_selectedLoaded is LoadedSelection a && a.PageIndex == pageIndex)
        {
            ignore.Add(a.Index);
        }
        foreach (var e in _extraSelected)
        {
            if (e.PageIndex == pageIndex) { ignore.Add(e.Index); }
        }

        // Two lists of snap targets per axis, one for object edges (tight
        // threshold) and one for guides (wider threshold, because a placed
        // guide is an explicit "line up on me" declaration). Each entry
        // carries an optional GuideMark so a guide-triggered snap can flash
        // the guide it engaged.
        var xTargets = new List<(double Pos, GuideMark? Guide)>(candidates.Count * 3);
        var yTargets = new List<(double Pos, GuideMark? Guide)>(candidates.Count * 3);
        foreach (var c in candidates)
        {
            if (ignore.Contains(c.Index)) { continue; }
            xTargets.Add((c.Left, null));
            xTargets.Add((c.Right, null));
            xTargets.Add(((c.Left + c.Right) / 2, null));
            yTargets.Add((c.Top, null));
            yTargets.Add((c.Bottom, null));
            yTargets.Add(((c.Top + c.Bottom) / 2, null));
        }
        // Guides: a vertical guide's NormalizedPos is width-normalized already;
        // a horizontal guide's is HEIGHT-normalized and needs rescaling to
        // width-units so it's comparable to annotation Y (which uses the same
        // width normalization as X).
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        if (slot is not null)
        {
            foreach (var g in slot.Guides)
            {
                if (g.Horizontal)
                {
                    double gyWidthNorm = slot.SlotWidth > 0
                        ? g.NormalizedPos * slot.SlotHeight / slot.SlotWidth
                        : g.NormalizedPos;
                    yTargets.Add((gyWidthNorm, g));
                }
                else
                {
                    xTargets.Add((g.NormalizedPos, g));
                }
            }
        }
        if (xTargets.Count == 0 && yTargets.Count == 0) { return moved; }

        double movedCx = (moved.Left + moved.Right) / 2;
        double movedCy = (moved.Top + moved.Bottom) / 2;

        // For each axis, pick the smallest signed offset whose absolute
        // distance is under the applicable threshold (object vs. guide).
        // GuideSnapped_ record the guide the snap hit (or null if the snap
        // came from an object edge) so we can flash the right guide below.
        (double Offset, GuideMark? Guide) BestSnap(List<(double Pos, GuideMark? Guide)> targets,
                                                    double own1, double own2, double ownC)
        {
            double bestOff = 0;
            double bestDist = double.MaxValue;
            GuideMark? bestGuide = null;
            foreach (var (pos, guide) in targets)
            {
                double tol = guide is null ? SnapThresholdNorm : GuideSnapThresholdNorm;
                double[] ownVals = { own1, own2, ownC };
                foreach (double v in ownVals)
                {
                    double d = pos - v;
                    if (Math.Abs(d) < tol && Math.Abs(d) < bestDist)
                    {
                        bestDist = Math.Abs(d);
                        bestOff = d;
                        bestGuide = guide;
                    }
                }
            }
            return (bestOff, bestGuide);
        }

        var (bestDx, snapGuideX) = BestSnap(xTargets, moved.Left, moved.Right, movedCx);
        var (bestDy, snapGuideY) = BestSnap(yTargets, moved.Top, moved.Bottom, movedCy);

        // Flash whichever guide got snapped to (up to one per axis). Every
        // OTHER guide on this page is turned off, so the flash follows the
        // pointer as the snap switches guides through the drag.
        if (slot is not null)
        {
            foreach (var g in slot.Guides)
            {
                g.IsSnapActive = (g == snapGuideX) || (g == snapGuideY);
            }

            // Smart guides: temporary alignment lines that appear when the
            // moved shape's edge/centre lines up with ANOTHER OBJECT's edge/
            // centre (not with a placed guide, which already flashes yellow
            // above). Rebuild the collection each frame so removal is
            // automatic when snap disengages - ObservableCollection.Clear
            // pattern is what proved reliable for the user-placed guides.
            slot.SmartGuideLines.Clear();
            bool xActive = Math.Abs(bestDx) > 0 && snapGuideX is null;
            bool yActive = Math.Abs(bestDy) > 0 && snapGuideY is null;
            if (xActive)
            {
                double sx = FindMatchingSnapPos(xTargets, moved.Left, moved.Right, movedCx, bestDx);
                // Vertical line, 0.5 DIP wide, spans page height.
                slot.SmartGuideLines.Add(new SmartGuideLine(
                    Horizontal: false,
                    PixelLeft: sx * SlotLayoutWidth, PixelTop: 0,
                    PixelWidth: 0.5, PixelHeight: slot.SlotHeight));
            }
            if (yActive)
            {
                double sy = FindMatchingSnapPos(yTargets, moved.Top, moved.Bottom, movedCy, bestDy);
                slot.SmartGuideLines.Add(new SmartGuideLine(
                    Horizontal: true,
                    PixelLeft: 0, PixelTop: sy * SlotLayoutWidth,
                    PixelWidth: slot.SlotWidth, PixelHeight: 0.5));
            }
        }

        return moved.MovedBy(bestDx, bestDy);
    }

    /// <summary>Given the offset that snap chose and the moved edges, find
    /// which target position it engaged (there's exactly one within threshold
    /// per axis - the winning target). Used to draw the smart-guide line at
    /// the ACTUAL alignment position rather than an edge of the moved box.</summary>
    private static double FindMatchingSnapPos(List<(double Pos, GuideMark? Guide)> targets,
                                              double edge1, double edge2, double centre, double bestOff)
    {
        const double Eps = 1e-6;
        foreach (var (pos, _) in targets)
        {
            if (Math.Abs((pos - edge1) - bestOff) < Eps) { return pos; }
            if (Math.Abs((pos - edge2) - bestOff) < Eps) { return pos; }
            if (Math.Abs((pos - centre) - bestOff) < Eps) { return pos; }
        }
        return edge1 + bestOff;  // fallback: the moved edge after snap
    }

    /// <summary>
    /// The unpadded upright rectangle to redraw a TURNED shape into after a
    /// resize, or null to leave it on the existing move path.
    ///
    /// The shape's own size comes off its tag, in points, which is the only
    /// exact record of it once the shape is turned; the drag supplies the RATIO
    /// to scale it by. Null whenever anything needed is missing, so a shape
    /// whose tag predates the upright-size field keeps behaving exactly as it
    /// does today rather than being given a size nobody chose.
    /// </summary>
    private TextRect? UprightResizeTarget(LoadedSelection start, LoadedSelection now)
    {
        if (!ShapeTagReader.TryParse(ReadAnnotationContents(now.PageIndex, now.Index), out var tag))
        {
            return null;
        }

        var (pageWidthPts, _) = PagePointsFor(now.PageIndex);
        if (pageWidthPts <= 0)
        {
            return null;
        }

        return ShapeResize.UprightTargetFor(
            new TextRect(start.Left, start.Top, start.Right, start.Bottom),
            new TextRect(now.Left, now.Top, now.Right, now.Bottom),
            tag.BoxWidthPts / pageWidthPts,
            tag.BoxHeightPts / pageWidthPts);
    }

    /// <summary>
    /// The annotation's own /Rect as the document reports it NOW, or null when
    /// it cannot be found. Read after a write that rebuilt the annotation, for
    /// the cases where the rectangle asked for and the rectangle produced are
    /// not the same thing.
    /// </summary>
    private TextRect? FreshBoundsOf(int pageIndex, int index)
    {
        var page = LoadedFor(pageIndex);
        int at = page.FindIndex(a => a.Index == index);
        if (at < 0)
        {
            return null;
        }

        var a2 = page[at];
        return new TextRect(a2.Left, a2.Top, a2.Right, a2.Bottom);
    }

    /// <summary>Writes a finished drag through to the document.</summary>
    private void CommitLoadedMove() => RunCommand(CommitLoadedMoveCore);

    private void CommitLoadedMoveCore()
    {
        if (_loadedDrag is not (_, _, LoadedSelection start) || _selectedLoaded is not LoadedSelection now)
        {
            return;
        }

        _loadedDrag = null;

        ClearDragTimeVisuals();

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

        NormalizeExtras(now.Id, "CommitLoadedMove");

        // The inverse of a move or resize is four numbers: put the rectangle
        // back. Recorded BEFORE the write, and at annotation granularity
        // rather than as a document snapshot, because dragging a stamp around
        // a page is the most repeated edit there is and each snapshot is the
        // whole PDF.
        // Every object the gesture touches, not just the anchor. Recording the
        // anchor alone meant undoing a group move returned one mark and left
        // the rest where they had been dragged. The extras' pre-drag rectangles
        // come from _extraDragOrigin, which is the snapshot taken when the drag
        // began; _extraSelected already holds their moved positions by now.
        var undoTargets = new List<AnnotationBoundsState>
        {
            new(start.PageIndex, start.Index, start.Left, start.Top, start.Right, start.Bottom, start.Id),
        };
        var origins = _extraDragOrigin.Count == _extraSelected.Count ? _extraDragOrigin : _extraSelected;
        foreach (var e in origins)
        {
            undoTargets.Add(new AnnotationBoundsState(
                e.PageIndex, e.Index, e.Left, e.Top, e.Right, e.Bottom, e.Id));
        }

        // ONE entry for the whole gesture, however many objects it moved and
        // however many pointer events produced it. The matching records are
        // emitted at the end of this method, once the writes have landed and
        // the real "after" rectangles can be read back.
        BeginEdit(resizing ? "Resize annotation" : "Move annotation");

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

        // Whether the shape branch below redrew a TURNED shape at a new upright
        // size. Its resulting /Rect is the axis-aligned box of the newly sized
        // rotated content, which is NOT the rectangle that was dragged unless
        // both axes happened to scale by the same factor, so the selection has
        // to be re-read afterwards rather than assumed.
        bool shapeUprightResized = false;

        // A text box ALWAYS goes through its own re-layout: on a resize it re-wraps
        // to the new width, and on a MOVE it keeps its angle (the generic path
        // resizes the annotation's rect without turning the content, which clipped
        // or dropped a rotated box).
        if (_selectedIsTextBox)
        {
            status = RenderCoreNative.resize_text_box_annotation(
                _documentHandle, now.PageIndex, now.Index, CaptureWidth, l, t, r, b, out newIndex);
        }

        if (status != RenderStatus.OkPdfium && _selectedIsShape)
        {
            // A shape is redrawn from its tag for a MOVE as well as a resize,
            // which is what the extras loop below has always done. The anchor
            // used to reach for this only when resizing and otherwise fell
            // through to resize_annotation, which REFUSES a shape outright
            // (proved by moving_a_shape_moves_the_drawing_not_just_the_rectangle:
            // it answers UNSUPPORTED, while resize_shape_annotation moves the
            // drawing correctly). Whether the fallthrough appeared to work
            // depended on whether PDFium had built an appearance stream for
            // the shape yet, which is why moving a group behaved differently
            // from one attempt to the next. Anchor and extras now take the
            // same path for the same operation.
            // A RESIZE of a TURNED shape cannot take the move path.
            //
            // move_shape_annotation says "these bounds are the padded /Rect",
            // and for a turned shape that tells the core to rebuild from the
            // upright size recorded on the tag, because a turned /Rect is the
            // axis-aligned box of the rotated content and cannot be de-padded
            // or inverted. That is exactly right for a move and throws away the
            // whole gesture for a resize: the shape was re-centred at its
            // original size, so the corner handles appeared to do nothing.
            //
            // Resizing instead sends the UNPADDED upright rectangle the drag
            // works out to, which resize_shape_annotation takes at face value.
            // An unrotated shape is deliberately left on the move path, where
            // the pad de-pads exactly and resizing has always worked.
            TextRect? upright = resizing && _selectedRotationDeg != 0
                ? UprightResizeTarget(start, now)
                : null;
            shapeUprightResized = upright is not null;

            status = upright is TextRect box
                ? RenderCoreNative.resize_shape_annotation(
                    _documentHandle, now.PageIndex, now.Index, CaptureWidth,
                    (float)(box.Left * CaptureWidth), (float)(box.Top * CaptureWidth),
                    (float)(box.Right * CaptureWidth), (float)(box.Bottom * CaptureWidth),
                    out newIndex)
                : RenderCoreNative.move_shape_annotation(
                    _documentHandle, now.PageIndex, now.Index, CaptureWidth, l, t, r, b, out newIndex);
        }

        // A stroke takes the same route as a shape, and for the same reason:
        // its tag describes it completely, so it can be re-drawn into any
        // rectangle. Without this it fell through to resize_annotation, which
        // answers Unsupported for ink, and a drawing could not be resized at
        // all. Move works here too, so a drawing stops being the one mark that
        // has to be deleted and drawn again to be adjusted.
        if (status != RenderStatus.OkPdfium
            && InkTag.TryParse(ReadAnnotationContents(now.PageIndex, now.Index),
                               out string inkColor, out double inkWidth, out var inkControl,
                               out double inkAngle))
        {
            // A TURNED stroke resizes the way a turned shape does. Its /Rect is
            // the box containing the turned ink, so the dragged rectangle is not
            // the stroke's own size; scaling the upright points straight onto it
            // would stretch the drawing rather than resize it. Take the RATIO
            // the frame changed by and apply it to the upright box instead.
            //
            // An upright stroke keeps the existing path exactly: its /Rect and
            // its own box are the same thing, and resizing has worked that way
            // since it was switched on.
            var inkBox = UprightBoxOf(inkControl);
            TextRect target = new(now.Left, now.Top, now.Right, now.Bottom);

            if (resizing && inkAngle != 0
                && ShapeResize.UprightTargetFor(
                       new TextRect(start.Left, start.Top, start.Right, start.Bottom),
                       target,
                       inkBox.Right - inkBox.Left,
                       inkBox.Bottom - inkBox.Top) is TextRect scaledBox)
            {
                target = scaledBox;
            }
            else if (inkAngle != 0)
            {
                // A MOVE of a turned stroke: same size, new place. Re-centre the
                // upright box on where the drag put the rectangle rather than
                // adopting the enlarged rectangle as the stroke's own size.
                double cx = (target.Left + target.Right) / 2;
                double cy = (target.Top + target.Bottom) / 2;
                double halfW = (inkBox.Right - inkBox.Left) / 2;
                double halfH = (inkBox.Bottom - inkBox.Top) / 2;
                target = new TextRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
            }

            status = RebuildInkAt(
                now.PageIndex, now.Index, CaptureWidth, inkColor, inkWidth, inkControl,
                target.Left, target.Top, target.Right, target.Bottom, now.Id, out newIndex,
                inkAngle);

            // Same reason a turned shape re-reads: the rectangle that lands is
            // the box containing the turned ink, not the one that was dragged.
            shapeUprightResized = inkAngle != 0;
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
            AbandonEdit();
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
                now.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom, start.Id);
        }
        else if (shapeUprightResized && FreshBoundsOf(now.PageIndex, newIndex) is TextRect grown)
        {
            // A turned shape that was just redrawn at a new size: its /Rect is
            // the axis-aligned box of the rotated content, so stretching one
            // axis produces a rectangle nothing like the drag. Keeping the
            // dragged one would draw the frame in the wrong place and, worse,
            // hand the next resize a wrong starting size to take its ratio
            // against, which compounds. The document knows; ask it.
            _selectedLoaded = new LoadedSelection(
                now.PageIndex, newIndex, grown.Left, grown.Top, grown.Right, grown.Bottom, start.Id);
        }
        else
        {
            _selectedLoaded = now with { Index = newIndex };
        }

        // Stamp the anchor's Id back on BEFORE the extras loop, because that
        // loop finds each of its targets by scanning for Ids. The write above
        // rebuilt /Contents from the tag and dropped the prefix, so without
        // this the anchor is anonymous and an extra carrying a stale Id could
        // match it instead of its own mark.
        if (now.Id != Guid.Empty)
        {
            Interop.AnnotationLoader.WriteId(_documentHandle, now.PageIndex, newIndex, now.Id);
            InvalidateLoadedPage(now.PageIndex);
        }

        // Move-all commit: write each extra's NEW position.
        //
        // Every earlier attempt here tried to PREDICT what the indices would
        // be after each delete+re-add: adjust for the anchor's shift, sort
        // descending so deletes don't orphan later writes, then recover the
        // final indices from the last-N run in write order. Each correction
        // was right about the case it was written for and wrong about another,
        // because the prediction depends on which path the anchor took (the
        // in-place move and the rebuild shift indices differently).
        //
        // Nothing is predicted now. Each extra is located by its stable Id
        // immediately before it is written, and the whole selection is
        // re-resolved by Id afterwards. Gate is on _extraSelected, not on
        // _extraDragOrigin, which is legitimately empty for a group-expanded
        // selection; _extraSelected already carries the target bounds from
        // DragLoadedTo's move-all update.
        if (!resizing && !rotating && _extraSelected.Count > 0)
        {
            var order = new List<(int Slot, LoadedSelection Adjusted)>(_extraSelected.Count);
            for (int i = 0; i < _extraSelected.Count; i++)
            {
                order.Add((i, _extraSelected[i]));
            }

            // Track write order per page (anchor's write already happened above;
            // count it as slot -1 so its stored index also gets corrected). The
            var pagesTouched = new HashSet<int> { now.PageIndex };

            foreach (var (slot, targetCached) in order)
            {
                // Resolve the annotation's LIVE index from its stable Id right
                // before writing it, instead of trusting the index cached at
                // selection time.
                //
                // Each write in this loop is a delete + re-add, so every write
                // reshuffles the page and invalidates the indices of the ones
                // still queued. The old code compensated with an
                // adjust-then-sort-descending scheme that assumed the anchor
                // had also been rebuilt; when the anchor took the in-place
                // path instead, the compensation aimed the extras' writes at
                // the wrong annotations. Asking the document where the
                // annotation is NOW cannot drift, whatever happened before it.
                var target = targetCached;
                if (target.Id != Guid.Empty)
                {
                    InvalidateLoadedPage(target.PageIndex);
                    if (FindLoadedById(target.Id, target.PageIndex) is (int livePage, int liveIndex))
                    {
                        target = target with { PageIndex = livePage, Index = liveIndex };
                    }
                    else
                    {
                        Diag.Log($"move extra id={target.Id:N}: not found on reload, skipping");
                        continue;
                    }
                }

                float exl = (float)(target.Left * CaptureWidth);
                float ext = (float)(target.Top * CaptureWidth);
                float exr = (float)(target.Right * CaptureWidth);
                float exb = (float)(target.Bottom * CaptureWidth);

                // Text box: full re-layout preserves rotation/font/wrapping.
                // Shape: same via its shape restyle path (no bounds change tool,
                // so use generic resize which just moves for a matching size).
                // Ink: re-drawn from its control points, since the generic call
                // refuses it.
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
                    extStatus = RenderCoreNative.move_shape_annotation(
                        _documentHandle, target.PageIndex, target.Index, CaptureWidth,
                        exl, ext, exr, exb, out extNewIndex);
                }

                // A stroke, re-drawn from its tag exactly as the anchor is.
                // The anchor got this branch first and the extras did not,
                // which made a drawing move on its own but sit still inside a
                // group: the generic call below answers Unsupported for ink, so
                // the member was silently skipped while everything else moved.
                //
                // Re-adding shifts every later index on the page, but the loop
                // already re-resolves each target by Id at the top of the next
                // iteration, which is what that invalidate is for.
                if (extStatus != RenderStatus.OkPdfium
                    && InkTag.TryParse(extContents, out string exInkColor, out double exInkWidth,
                                       out var exInkControl, out double exInkAngle))
                {
                    // The ANGLE goes through too. Without it a turned stroke was
                    // rewritten upright the moment its group was moved: the
                    // anchor's branch carried the rotation and this one silently
                    // defaulted it to zero. CommitLoadedMove dispatches TWICE,
                    // once here and once for the anchor, with separate branch
                    // lists, and teaching only one of them is the standing trap
                    // of this method.
                    //
                    // The extras only ever MOVE (the loop is gated on !resizing),
                    // so the stroke keeps its own size: its upright box is
                    // re-centred on where the drag put it rather than being
                    // stretched onto the enlarged rectangle a turned stroke
                    // reports.
                    var exBox = UprightBoxOf(exInkControl);
                    double exHalfW = (exBox.Right - exBox.Left) / 2;
                    double exHalfH = (exBox.Bottom - exBox.Top) / 2;
                    double exCx = (target.Left + target.Right) / 2;
                    double exCy = (target.Top + target.Bottom) / 2;

                    extStatus = RebuildInkAt(
                        target.PageIndex, target.Index, CaptureWidth, exInkColor, exInkWidth, exInkControl,
                        exCx - exHalfW, exCy - exHalfH, exCx + exHalfW, exCy + exHalfH,
                        target.Id, out extNewIndex, exInkAngle);
                }

                if (extStatus != RenderStatus.OkPdfium)
                {
                    extStatus = RenderCoreNative.resize_annotation(
                        _documentHandle, target.PageIndex, target.Index, CaptureWidth,
                        exl, ext, exr, exb, out extNewIndex);
                }

                if (extStatus == RenderStatus.OkPdfium)
                {
                    _extraSelected[slot] = target with { Index = extNewIndex };
                    pagesTouched.Add(target.PageIndex);

                    // Re-stamp the Id NOW, not after the loop. The resize FFIs
                    // rebuild /Contents from the shape or text tag and do not
                    // carry the ID prefix through, so the annotation is
                    // anonymous the moment this write returns. The very next
                    // iteration resolves ITS target by scanning for Ids, and
                    // an anonymous annotation would both be unfindable and let
                    // a stale Id match the wrong mark.
                    if (target.Id != Guid.Empty)
                    {
                        Interop.AnnotationLoader.WriteId(
                            _documentHandle, target.PageIndex, extNewIndex, target.Id);
                    }
                }
                Diag.Log($"move extra p{target.PageIndex}#{target.Index} id={target.Id:N} -> {extStatus}, now #{extNewIndex}");
            }
            _extraDragOrigin.Clear();

            foreach (int page in pagesTouched)
            {
                InvalidateLoadedPage(page);
            }

            // Re-resolve the whole selection from the document by Id. No
            // arithmetic, no assumptions about write order: whatever the page
            // looks like now is what the selection points at.
            if (_selectedLoaded is LoadedSelection anchorSel && anchorSel.Id != Guid.Empty
                && FindLoadedById(anchorSel.Id) is (int ap, int ai))
            {
                _selectedLoaded = anchorSel with { PageIndex = ap, Index = ai };
            }
            for (int i = 0; i < _extraSelected.Count; i++)
            {
                var ex = _extraSelected[i];
                if (ex.Id != Guid.Empty && FindLoadedById(ex.Id) is (int ep, int ei))
                {
                    _extraSelected[i] = ex with { PageIndex = ep, Index = ei };
                }
            }
        }

        // The anchor's own write also stripped its ID prefix, and a
        // single-selection move never enters the block above at all.
        StampSelectedIds();
        RecordBoundsBatch(undoTargets);
        CommitEdit();

        // The write above rebuilt the shape from its tag and recorded a new
        // upright size on it, so the cached tag is one gesture out of date.
        // Everything the frame is worked out from has to come from after the
        // edit, not before it.
        if (_selectedIsShape && _selectedLoaded is LoadedSelection after)
        {
            _selectedShapeTag = ReadAnnotationContents(after.PageIndex, after.Index);
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

    /// <summary>Moves every selected object (anchor + extras) by the given delta
    /// in NORMALIZED page-width units. The arrow-key nudge and the shift-arrow
    /// bigger nudge both come through here; only the caller decides the step
    /// size. Reuses <see cref="CommitAlignedOrDistributed"/> for the actual
    /// write so the last-N index tracking that keeps repeated align/move
    /// consistent covers nudge automatically.</summary>
    public void NudgeSelected(double dx, double dy) => NudgeSelected(dx, dy, recordHistory: true);

    /// <summary>
    /// As above, but a caller that has ALREADY pushed a history step for the
    /// action it is part of can suppress a second one.
    ///
    /// Ctrl+D is the case: it duplicates and then offsets the copy, and a
    /// history step captures the state as it is when pushed, so the single step
    /// taken before the duplicate already returns to before both. Pushing again
    /// would make one keystroke cost two undos.
    /// </summary>
    private void NudgeSelected(double dx, double dy, bool recordHistory)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection anchor)
        {
            return;
        }
        if (dx == 0 && dy == 0) { return; }

        if (recordHistory)
        {
            PushHistory(HistoryScope.Document, "Nudge");
        }
        LoadedSelection Shift(LoadedSelection s) => s with
        {
            Left = s.Left + dx, Right = s.Right + dx,
            Top = s.Top + dy, Bottom = s.Bottom + dy,
        };
        var newAnchor = Shift(anchor);
        var newExtras = _extraSelected.Select(Shift).ToList();
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
    /// restyle-move, or generic bounds set), then refreshes the overlay.
    ///
    /// The writes go in DESCENDING index order per page. Each write does a
    /// delete + re-add which moves that annotation to the END of the page's
    /// list, and every annotation with a higher original index shifts down by
    /// one. Writing higher indices first means every not-yet-processed index is
    /// unaffected. Ascending order was the reason a second Align (or any second
    /// multi-write) landed on the wrong annotations - what looked like an
    /// intermittent bug ("aligns now and next it don't") was actually indices
    /// pointing at slid-down neighbours.</summary>
    private void CommitAlignedOrDistributed(LoadedSelection oldAnchor,
        LoadedSelection newAnchor, List<LoadedSelection> newExtras)
        => RunCommand(() => CommitAlignedOrDistributedCore(oldAnchor, newAnchor, newExtras));

    private void CommitAlignedOrDistributedCore(LoadedSelection oldAnchor,
        LoadedSelection newAnchor, List<LoadedSelection> newExtras)
    {
        const int CaptureWidth = 1000;
        var pagesTouched = new HashSet<int>();

        // Build a joint list of (oldSel, target, slot). The slot is where the
        // NEW index gets written back after the FFI call: 0 for the anchor and
        // i+1 for extra[i]. Sorting by descending page index THEN descending
        // annotation index means the deletes never affect a later write.
        int n = 1 + _extraSelected.Count;
        var jobs = new List<(LoadedSelection Old, LoadedSelection Target, int Slot)>(n);
        jobs.Add((oldAnchor, newAnchor, 0));
        for (int i = 0; i < _extraSelected.Count && i < newExtras.Count; i++)
        {
            jobs.Add((_extraSelected[i], newExtras[i], i + 1));
        }
        jobs.Sort((a, b) =>
        {
            int p = b.Old.PageIndex.CompareTo(a.Old.PageIndex);
            return p != 0 ? p : b.Old.Index.CompareTo(a.Old.Index);
        });

        // Track write order PER PAGE. The newIndex each write returns is L-1 at
        // THAT moment - but every subsequent delete on a LOWER index shifts
        // already-added items down. After all writes on a page, the successful
        // items sit at positions [count - N, count - N + 1, ..., count - 1] in
        // WRITE ORDER (not sort order, though we're processing sorted). So
        // recording the returned index per write is wrong (that value gets
        // stale after the very next delete on the same page). Recompute below
        // once every write on this page is done. This is what made "align top
        // then align bottom" behave inconsistently: the second align's cached
        // indices pointed at slid-down neighbours.
        var writeOrderPerPage = new Dictionary<int, List<int>>();
        foreach (var (oldSel, target, slot) in jobs)
        {
            int newIdx = WriteMovedAnnotation(oldSel, target, CaptureWidth);
            if (newIdx >= 0)
            {
                pagesTouched.Add(target.PageIndex);
                if (!writeOrderPerPage.TryGetValue(target.PageIndex, out var list))
                {
                    list = new List<int>();
                    writeOrderPerPage[target.PageIndex] = list;
                }
                list.Add(slot);
            }
        }

        // Invalidate the loaded-annotation cache to get fresh counts, then
        // assign every successfully-written slot its TRUE final index. On each
        // page the writes ended up in the last-N run in write order.
        foreach (int page in pagesTouched)
        {
            InvalidateLoadedPage(page);
        }

        var newIndicesBySlot = new Dictionary<int, int>(n);
        foreach (var kv in writeOrderPerPage)
        {
            int count = LoadedFor(kv.Key).Count;
            var slots = kv.Value;
            for (int i = 0; i < slots.Count; i++)
            {
                newIndicesBySlot[slots[i]] = count - slots.Count + i;
            }
        }

        // Write results back into the anchor and extras in ORIGINAL slot order.
        if (newIndicesBySlot.TryGetValue(0, out int anchorIdx))
        {
            _selectedLoaded = newAnchor with { Index = anchorIdx };
        }
        for (int i = 0; i < _extraSelected.Count && i < newExtras.Count; i++)
        {
            if (newIndicesBySlot.TryGetValue(i + 1, out int idx))
            {
                _extraSelected[i] = newExtras[i] with { Index = idx };
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
            status = RenderCoreNative.move_shape_annotation(
                _documentHandle, oldSel.PageIndex, oldSel.Index, captureWidth,
                l, t, r, b, out newIndex);
        }

        // A stroke, re-drawn from its own control points.
        //
        // Without this branch ink fell through to resize_annotation, which
        // answers Unsupported for it, so this returned -1 and the caller did
        // nothing at all. That is what made UNDO of a drawing's move or resize
        // silently do nothing: the history record was written and replayed
        // correctly and the write at the end of it was refused. The rotation
        // path never showed it because it takes a document snapshot instead.
        //
        // A turned stroke keeps its own SIZE here: the rectangle it is being
        // put back to is the box containing the turned ink, so its upright box
        // is re-centred on that rather than stretched onto it.
        if (status != RenderStatus.OkPdfium
            && InkTag.TryParse(contents, out string inkColor, out double inkWidth,
                               out var inkControl, out double inkAngle))
        {
            var box = UprightBoxOf(inkControl);
            double halfW = (box.Right - box.Left) / 2;
            double halfH = (box.Bottom - box.Top) / 2;
            double cx = (target.Left + target.Right) / 2;
            double cy = (target.Top + target.Bottom) / 2;

            var into = inkAngle != 0
                ? new TextRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH)
                : new TextRect(target.Left, target.Top, target.Right, target.Bottom);

            status = RebuildInkAt(
                oldSel.PageIndex, oldSel.Index, captureWidth, inkColor, inkWidth, inkControl,
                into.Left, into.Top, into.Right, into.Bottom, oldSel.Id, out newIndex, inkAngle);
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
    /// <summary>A stashed annotation ready to be pasted. Captures the Contents
    /// tag (which fully describes a shape or text box - kind/style/font/text)
    /// and its normalized bounds on the source page. Paste re-emits the
    /// annotation on the target page at an offset from the original bounds.</summary>
    private sealed record ClipboardEntry(
        string Contents,
        double Left, double Top, double Right, double Bottom);
    private readonly List<ClipboardEntry> _clipboard = new();

    /// <summary>True when Ctrl+V has something to paste.</summary>
    public bool HasClipboardContent => _clipboard.Count > 0;

    /// <summary>Copies every selected loaded annotation into the in-memory
    /// clipboard. Returns true if anything was captured. Ink and highlight
    /// overlays are skipped (they don't yet participate in the loaded-
    /// annotation model). Same session only for now.</summary>
    public bool CopySelectedAnnotations()
    {
        var toCopy = new List<LoadedSelection>();
        if (_selectedLoaded is LoadedSelection a) { toCopy.Add(a); }
        toCopy.AddRange(_extraSelected);
        if (toCopy.Count == 0) { return false; }

        _clipboard.Clear();
        foreach (var sel in toCopy)
        {
            string? contents = ReadAnnotationContents(sel.PageIndex, sel.Index);
            if (string.IsNullOrEmpty(contents)) { continue; }
            _clipboard.Add(new ClipboardEntry(contents, sel.Left, sel.Top, sel.Right, sel.Bottom));
        }
        return _clipboard.Count > 0;
    }

    /// <summary>Copy plus delete, the standard Ctrl+X semantic. Delete uses
    /// the existing multi-select delete path so an anchor + extras go together.</summary>
    public bool CutSelectedAnnotations()
    {
        if (!CopySelectedAnnotations()) { return false; }
        DeleteSelectedAnnotation();
        return true;
    }

    /// <summary>Pastes everything in the clipboard onto the current page,
    /// offset by ~12pt right and down from the source bounds so pastes stack
    /// visibly rather than landing on top of the original. Newly-pasted
    /// annotations become the new selection so a follow-up move affects
    /// exactly what was just pasted, the way every editor works.</summary>
    public bool PasteAnnotations()
    {
        if (_documentHandle == 0 || _clipboard.Count == 0) { return false; }
        int page = CurrentPageIndex;
        var slot = PageSlots.FirstOrDefault(s => s.PageIndex == page);
        if (slot is null) { return false; }

        // Offset in NORMALIZED (width-based) units. ~12pt on Letter -> 0.02.
        const double Offset = 0.02;
        const int CaptureWidth = 1000;
        PushHistory(HistoryScope.Document, _clipboard.Count == 1 ? "Paste" : "Paste " + _clipboard.Count);

        int firstNewIndex = LoadedFor(page).Count;
        int emitted = 0;
        // Read once: every pasted shape lands on the same page.
        double pastePageWidthPts = PagePointsFor(page).W;
        foreach (var e in _clipboard)
        {
            // Fresh normalized bounds on the current page. Clamp so a paste
            // near a page edge lands INSIDE the page even if the offset would
            // push it off.
            double w = e.Right - e.Left;
            double h = e.Bottom - e.Top;
            double left = Math.Clamp(e.Left + Offset, 0, 1 - w);
            double top  = Math.Clamp(e.Top  + Offset, 0, Math.Max(0, 1.5 - h));  // vertical isn't 0-1 in width-norm units
            var pasted = new LoadedSelection(page, -1, left, top, left + w, top + h);

            if (e.Contents.StartsWith("AyaanShape:", StringComparison.Ordinal))
            {
                if (!ShapeSpecFromTag(e.Contents, pasted, CaptureWidth, pastePageWidthPts,
                                      out var spec, out string effects)) { continue; }
                if (Interop.NativeShapes.Add(_documentHandle, CaptureWidth, spec, effects)
                    == RenderStatus.OkPdfium) { emitted++; }
            }
            else if (InkTag.TryParse(e.Contents, out string inkColor, out double inkWidth,
                                     out var inkControl, out double inkAngle))
            {
                // A drawing pastes like anything else now: its control points
                // are its description, so it can be re-drawn at the offset the
                // paste chose. Copy already put the tag on the clipboard, and
                // this branch was simply missing, so a copied drawing was
                // silently dropped and the paste reported nothing to show for it.
                var placed = InkTag.ScaleTo(
                    inkControl, pasted.Left, pasted.Top, pasted.Right, pasted.Bottom);

                if (AddInkFromControl(page, CaptureWidth, inkColor, inkWidth, placed,
                                      out int inkIndex, inkAngle))
                {
                    WriteInkTag(page, inkIndex, inkColor, inkWidth, placed, inkAngle);
                    emitted++;
                }
            }
            else if (TextBoxTagReader.TryParse(e.Contents, out var tag))
            {
                double l2 = pasted.Left, t2 = pasted.Top, r2 = pasted.Right, b2 = pasted.Bottom;
                var (rr, gg, bb, aa) = ParseHex(tag.ColorHex, defaultAlpha: 0xFF);
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(tag.Text);
                byte[]? fontUtf8 = string.IsNullOrEmpty(tag.FontPath)
                    ? null
                    : System.Text.Encoding.UTF8.GetBytes(tag.FontPath);
                int status = RenderCoreNative.add_text_box_annotation_styled(
                    _documentHandle, page, CaptureWidth,
                    (float)(l2 * CaptureWidth), (float)(t2 * CaptureWidth),
                    (float)(r2 * CaptureWidth), (float)(b2 * CaptureWidth),
                    utf8, (nuint)utf8.Length,
                    (float)(tag.FontSizeNorm * CaptureWidth), rr, gg, bb, aa,
                    (int)tag.Align,
                    PackRgba(tag.FillHex), PackRgba(tag.OutlineHex),
                    (float)(tag.OutlineWidthNorm * CaptureWidth),
                    fontUtf8, (nuint)(fontUtf8?.Length ?? 0),
                    tag.Underline ? 1 : 0, tag.Strikethrough ? 1 : 0);
                if (status != RenderStatus.OkPdfium) { continue; }
                if (tag.RotationDeg != 0)
                {
                    RenderCoreNative.rotate_text_box_annotation(
                        _documentHandle, page,
                        (int)(LoadedFor(page).Count),
                        CaptureWidth,
                        (float)(l2 * CaptureWidth), (float)(t2 * CaptureWidth),
                        (float)(r2 * CaptureWidth), (float)(b2 * CaptureWidth),
                        (float)tag.RotationDeg, out _);
                }
                emitted++;
            }
        }
        if (emitted == 0) { return false; }

        IsDirty = true;
        InvalidateLoadedPage(page);

        // Select what was just pasted: anchor = last one, extras = the rest.
        var all = LoadedFor(page);
        _extraSelected.Clear();
        _selectedLoaded = null;
        for (int i = firstNewIndex; i < all.Count && i < firstNewIndex + emitted; i++)
        {
            var a = all[i];
            var newSel = new LoadedSelection(page, a.Index, a.Left, a.Top, a.Right, a.Bottom, a.Id);
            if (_selectedLoaded is null) { _selectedLoaded = newSel; }
            else { _extraSelected.Add(newSel); }
        }
        if (_selectedLoaded is LoadedSelection anchor)
        {
            ApplyTextBoxSelectionInfo(anchor.PageIndex, anchor.Index);
        }
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedAnnotationLoaded));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    // ---------------- Grouping ----------------
    //
    // A group is a set of annotation references (page + index) that always
    // select together: click any member and the whole set becomes the
    // multi-selection, so a follow-up move / delete / restyle / paste treats
    // them as one. Session-only for now; not persisted to the PDF.
    //
    // Index-shift caveat: multi-writes (align, move-all, bring-to-front) do
    // delete + re-add on each member and the indices shift. The recompute
    // hooks below rewrite group members with their new indices after those
    // operations. Groups still won't survive OPERATIONS ON OTHER annotations
    // (an align of some unrelated shape won't touch our group's indices, but
    // adding a new annotation ABOVE a group member would leave the group's
    // stored index pointing at the wrong mark). Acceptable MVP limitation.

    // Groups are stored by stable annotation Id, not by (page, index).
    // Indices reshuffle on every delete+re-add churn; Ids don't. This makes
    // groups survive any multi-write (move, align, distribute, z-order)
    // without a remap pass. See project-ayaanpdf-guid-migration-plan for the
    // rationale.
    private readonly List<List<Guid>> _groups = new();

    public bool HasGrouping => _groups.Count > 0;

    /// <summary>Finds the current (page, index) of a loaded annotation by
    /// its stable Id. Scans every page that's currently in the loaded cache;
    /// returns null if the Id isn't among them (e.g. its page hasn't been
    /// visited yet this session, or the annotation has been deleted).</summary>
    private (int Page, int Index)? FindLoadedById(Guid id, int preferPage = -1)
    {
        if (id == Guid.Empty) { return null; }

        // LoadedFor, never a bare _loadedByPage scan. InvalidateLoadedPage
        // REMOVES the page from that dictionary, so a scan straight after an
        // invalidate looks at a page that isn't there and reports the
        // annotation missing. That is what made every extra in a group move
        // get skipped: each one invalidated its page, found nothing, and
        // dropped out of the write loop, leaving only the dragged mark moving.
        if (preferPage >= 0)
        {
            foreach (var a in LoadedFor(preferPage))
            {
                if (a.Id == id) { return (preferPage, a.Index); }
            }
        }

        for (int p = 0; p < PageCount; p++)
        {
            if (p == preferPage) { continue; }
            foreach (var a in LoadedFor(p))
            {
                if (a.Id == id) { return (p, a.Index); }
            }
        }
        return null;
    }

    /// <summary>Stamps the current selection's Ids onto their /Contents so
    /// they survive the next delete+re-add churn. Called at the end of every
    /// multi-write that re-emits annotations (CommitLoadedMove, BringToFront).
    /// The resize FFIs rebuild /Contents from the tag and don't carry the ID
    /// prefix through; if we didn't re-stamp here, the next click on a group
    /// member would look up an Id that no longer exists in the file.</summary>
    private void StampSelectedIds()
    {
        if (_documentHandle == 0) { return; }
        if (_selectedLoaded is LoadedSelection anchor)
        {
            StampOne(anchor);
        }
        foreach (var ex in _extraSelected)
        {
            StampOne(ex);
        }

        // Locate the annotation by Id and write to THAT index, never to the
        // index cached on the selection.
        //
        // This used to write to sel.Index directly, and it was destroying
        // identity. By the time this runs, every write in the move loop has
        // deleted and re-added its annotation, so the whole page has shifted
        // and the cached indices name different marks. Stamping through them
        // put one member's Id onto another member's annotation: a five-shape
        // group lost a member outright (its slot was overwritten by the
        // anchor's Id) and gained a duplicate elsewhere, after which the group
        // could only ever expand to four, and NormalizeExtras dropped the
        // duplicate as a repeat. That is the "select five, only one moves"
        // report.
        //
        // Skipping when the Id cannot be found is the point: an annotation
        // that does not answer to its Id must NOT be repaired by guessing at a
        // slot, because guessing is what broke it.
        void StampOne(LoadedSelection sel)
        {
            if (sel.Id == Guid.Empty) { return; }
            if (FindLoadedById(sel.Id, sel.PageIndex) is not (int page, int index))
            {
                Diag.Log($"StampSelectedIds: id={sel.Id:N} not found, skipping rather than stamping a stale slot");
                return;
            }
            Interop.AnnotationLoader.WriteId(_documentHandle, page, index, sel.Id);
        }
    }

    /// <summary>Creates a group from the current multi-selection (anchor +
    /// extras). Needs 2+ marks. Returns false if there's nothing to group.
    /// Each member's Id is stamped to the annotation's /Contents so the
    /// group survives writes.</summary>
    public bool GroupSelected()
    {
        Diag.Log($"GroupSelected: anchor={(_selectedLoaded.HasValue ? $"p{_selectedLoaded.Value.PageIndex}#{_selectedLoaded.Value.Index} id={_selectedLoaded.Value.Id:N}" : "null")} extras={_extraSelected.Count}");
        foreach (var e in _extraSelected)
        {
            Diag.Log($"  extra p{e.PageIndex}#{e.Index} id={e.Id:N}");
        }
        if (_selectedLoaded is not LoadedSelection anchor)
        {
            Status = "Nothing selected to group.";
            return false;
        }
        // Collect Ids and persist each to its annotation's /Contents. A member
        // whose Id is Guid.Empty (legacy annotation whose ephemeral Id was
        // never stamped) is skipped; that's rare because SelectLoadedAt fills
        // Id from the loader, which generates one if the annotation had none.
        var ids = new List<Guid>();
        if (anchor.Id != Guid.Empty)
        {
            ids.Add(anchor.Id);
            Interop.AnnotationLoader.WriteId(_documentHandle, anchor.PageIndex, anchor.Index, anchor.Id);
        }
        foreach (var e in _extraSelected)
        {
            if (e.Id != Guid.Empty && !ids.Contains(e.Id))
            {
                ids.Add(e.Id);
                Interop.AnnotationLoader.WriteId(_documentHandle, e.PageIndex, e.Index, e.Id);
            }
        }
        if (ids.Count < 2)
        {
            Status = "Select two or more marks (shift-click) before grouping.";
            return false;
        }

        // A mark can only be in ONE group at a time (flat, non-nested); drop
        // any group that overlaps with the new one, then add.
        var groupsBefore = SnapshotGroups();
        _groups.RemoveAll(g => g.Any(id => ids.Contains(id)));
        _groups.Add(ids);
        BeginEdit("Group");
        RecordEdit(new GroupsRecord(groupsBefore, SnapshotGroups()));
        CommitEdit();
        Diag.Log($"GroupSelected done: groups={_groups.Count}, members=[{string.Join(",", ids)}]");
        Status = $"Grouped {ids.Count} marks.";
        return true;
    }

    /// <summary>Dissolves the group that the current anchor is in. Returns
    /// false if the anchor isn't in a group.</summary>
    public bool UngroupSelected()
    {
        if (_selectedLoaded is not LoadedSelection anchor || anchor.Id == Guid.Empty)
        {
            Status = "Nothing selected to ungroup.";
            return false;
        }
        var ungroupBefore = SnapshotGroups();
        int removed = _groups.RemoveAll(g => g.Contains(anchor.Id));
        if (removed > 0)
        {
            BeginEdit("Ungroup");
            RecordEdit(new GroupsRecord(ungroupBefore, SnapshotGroups()));
            CommitEdit();
        }
        Status = removed > 0 ? "Ungrouped." : "That mark isn't in a group.";
        return removed > 0;
    }

    /// <summary>Returns the group containing the given annotation Id, or
    /// null if it isn't grouped.</summary>
    private IReadOnlyList<Guid>? GroupContaining(Guid id)
    {
        if (id == Guid.Empty) { return null; }
        foreach (var g in _groups)
        {
            if (g.Contains(id)) { return g; }
        }
        return null;
    }

    /// <summary>Moves the currently selected annotation(s) to the top of the
    /// page's z-order. Works via delete + re-add - a fresh annotation always
    /// lands at the end of the page's list (which renders LAST, i.e. on top).
    /// Multi-selection processes each in current order so the anchor ends up
    /// on top of the extras. Non-Ayaan annotations on the page keep their
    /// existing positions relative to each other, but of course our resused
    /// ones now sit above them.</summary>
    public bool BringSelectedToFront() =>
        Reorder(AnnotationOrder.BringToFront, "Bring to front");

    /// <summary>Puts the selection underneath everything else on its page.</summary>
    public bool SendSelectedToBack() =>
        Reorder(AnnotationOrder.SendToBack, "Send to back");

    /// <summary>Raises the selection by exactly one place.</summary>
    public bool BringSelectedForward() =>
        Reorder(AnnotationOrder.BringForward, "Bring forward");

    /// <summary>Lowers the selection by exactly one place.</summary>
    public bool SendSelectedBackward() =>
        Reorder(AnnotationOrder.SendBackward, "Send backward");

    /// <summary>
    /// The one z-order path. Works out the target paint order as data, checks
    /// what it would cost, and only then rewrites the page.
    ///
    /// The rewrite is a run of removals and re-adds, because appending is the
    /// only ordering primitive PDFium has. That has two consequences worth
    /// knowing before reading the loop:
    ///
    /// 1. Only the tail that actually changed is rewritten. An annotation
    ///    sitting in the untouched prefix is never removed, which is what lets
    ///    a page carrying an Acrobat comment survive some reorders intact.
    /// 2. Every write reshuffles the indices of everything after it, so each
    ///    step re-resolves its target by Guid immediately before writing. Aiming
    ///    a write at an index cached before the previous write is what destroyed
    ///    annotation identities in v2.7.5.
    ///
    /// Single page only: the anchor's. A selection spanning pages has no single
    /// stack to reorder within.
    /// </summary>
    private bool Reorder(
        Func<IReadOnlyList<Guid>, ISet<Guid>, List<Guid>> plan, string label)
        => RunCommand(() => ReorderCore(plan, label));

    private bool ReorderCore(
        Func<IReadOnlyList<Guid>, ISet<Guid>, List<Guid>> plan, string label)
    {
        if (_documentHandle == 0 || _selectedLoaded is not LoadedSelection anchor)
        {
            // Logged, because from the outside "nothing selected" and "refused"
            // and "already there" all look identical: the command runs, the
            // page repaints, and the object does not move.
            Diag.Log($"reorder '{label}': NOTHING SELECTED");
            return false;
        }

        NormalizeExtras(anchor.Id, label);
        int page = anchor.PageIndex;

        InvalidateLoadedPage(page);

        // The page as OBJECTS, from the model, rather than as annotations to be
        // re-parsed. The model already knows each object's kind and whether it
        // can be rebuilt, which is exactly what this operation has to decide;
        // asking it costs one pass instead of re-reading every annotation's tag
        // twice, once for the guard and again for the write.
        var stack = PageModelFor(page).Objects;
        var current = stack.Select(o => o.Id).ToList();

        var moving = new HashSet<Guid>();
        if (anchor.Id != Guid.Empty) { moving.Add(anchor.Id); }
        foreach (var ex in _extraSelected)
        {
            if (ex.PageIndex == page && ex.Id != Guid.Empty) { moving.Add(ex.Id); }
        }
        if (moving.Count == 0)
        {
            Diag.Log($"reorder '{label}': selection has no id on page {page}");
            return false;
        }

        var target = plan(current, moving);
        int from = AnnotationOrder.RewriteFrom(current, target);

        Diag.Log($"reorder '{label}': page {page} stack={current.Count} moving={moving.Count} rewriteFrom={from}");

        if (from >= target.Count)
        {
            Status = label switch
            {
                "Bring to front" or "Bring forward" => "Already at the front.",
                _ => "Already at the back.",
            };
            return false;
        }

        // Refuse BEFORE touching anything. A rewrite that discovers halfway
        // through that it cannot rebuild an annotation has already deleted the
        // ones before it, and there is nothing honest to do at that point.
        for (int i = from; i < target.Count; i++)
        {
            var obj = stack.FirstOrDefault(o => o.Id == target[i]);
            if (obj is null || !obj.IsRebuildable)
            {
                Diag.Log($"reorder '{label}': REFUSED at {i}, " +
                         (obj is null ? "object not in the stack" : $"{obj.Kind} is not rebuildable"));
                Status = "Cannot reorder here: the page has a mark this app did not create, "
                       + "and moving it would lose it.";
                return false;
            }

            // And it must still be FINDABLE by that id after a reload, because
            // that is how every write in the loop below addresses its target.
            // An annotation whose id was invented rather than persisted gets a
            // different one on the next load, so the plan is built against an
            // identity that no longer exists. Checking here means the command
            // refuses whole rather than rewriting half the page and stopping.
            InvalidateLoadedPage(page);
            if (FindLoadedById(obj.Id, page) is null)
            {
                Diag.Log($"{label}: id={obj.Id:N} does not survive a reload, refusing");
                Status = "Cannot reorder here: one of these marks has no stable identity.";
                return false;
            }
        }

        // A LIST OF GUIDS, not a copy of the PDF. This used to push a whole
        // document snapshot, so every click of one of the four z-order buttons
        // copied the entire file into the undo stack. A page of fifty marks
        // costs about a kilobyte here.
        BeginEdit(label);
        RecordEdit(new OrderRecord(page, current, target));
        ApplyOrder(page, target, label);
        CommitEdit();

        ResolveSelectionById(page);
        StampSelectedIds();
        RefreshSelectionOutline();
        RenderCurrentPage();
        return true;
    }

    /// <summary>
    /// Rewrites a page into the given paint order.
    ///
    /// Shared by the commands and by undo/redo, so reversing a reorder runs the
    /// identical code in the other direction and the two cannot drift apart.
    ///
    /// Only the tail after the first disagreement is rewritten, because
    /// appending is the only ordering primitive there is: re-adding those in the
    /// wanted order lands the page exactly on it, and anything in the untouched
    /// prefix is never removed.
    /// </summary>
    private void ApplyOrder(int page, IReadOnlyList<Guid> target, string label)
    {
        InvalidateLoadedPage(page);
        var current = PageModelFor(page).Objects.Select(o => o.Id).ToList();
        int from = AnnotationOrder.RewriteFrom(current, target);

        for (int i = from; i < target.Count; i++)
        {
            if (!RaiseToTop(page, target[i]))
            {
                Diag.Log($"{label}: raise failed for id={target[i]:N}, page left partially reordered");
                break;
            }
        }

        InvalidateLoadedPage(page);
        IsDirty = true;
    }


    /// <summary>Moves one annotation, named by Id, to the top of its page's
    /// paint order. Resolves the live index at the moment of the write, since
    /// the previous iteration's write already shifted the page.</summary>
    private bool RaiseToTop(int page, Guid id)
    {
        const int CaptureWidth = 1000;

        InvalidateLoadedPage(page);
        if (FindLoadedById(id, page) is not (int livePage, int liveIndex)) { return false; }

        var live = LoadedFor(livePage).ToList();
        int at = live.FindIndex(a => a.Index == liveIndex);
        if (at < 0) { return false; }
        var item = live[at];

        string? contents = ReadAnnotationContents(livePage, liveIndex);
        bool isText = TextBoxTagReader.TryParse(contents, out var textTag);
        bool isShape = ShapeTagReader.IsShapeTag(contents);

        int status;
        int newIndex;
        if (isShape)
        {
            // NO BOUNDS. A raise must not touch geometry, and passing the
            // annotation's own rectangle back in is not geometry-neutral: the
            // writer stores /Rect INFLATED by the stroke pad, and resize treats
            // what it is given as the un-inflated extent, so it pads again.
            // Send to back then to front and the shape came back visibly fatter,
            // by roughly a stroke width each time.
            //
            // restyle with no overrides is the geometry-preserving rebuild: it
            // undoes the pad itself and redraws from the tag, so repeated raises
            // are a fixed point. Proved by
            // raising_a_shape_repeatedly_does_not_grow_it in render_core.
            status = RenderCoreNative.restyle_shape_annotation(
                _documentHandle, livePage, liveIndex, CaptureWidth,
                colorRgba: 0, widthPx: -1f, out newIndex);
        }
        else if (isText)
        {
            // The box's OWN UPRIGHT rect, out of its tag, NOT the annotation's
            // reported rectangle. Same trap the shape branch above avoids, by a
            // different route: a TURNED box reports the axis-aligned bounding
            // box of its rotated content, which is larger than the rect the box
            // actually occupies, so feeding it back in re-lays the text out into
            // something bigger and the next raise enlarges that again. Measured
            // at 3.2x the original size after four raises, which is two clicks
            // of Send to Back and Bring to Front.
            //
            // Unturned, the two rectangles are the same and this changes
            // nothing. Both cases are pinned in render_core by
            // raising_a_text_box_repeatedly_does_not_move_it and
            // raising_a_ROTATED_text_box_repeatedly_does_not_move_it.
            //
            // A box written before the tag carried its own rect has no upright
            // rect to use, and falls back to what this always did. That is
            // correct for the unturned case and no worse than today for the
            // turned one.
            double bl = textTag.HasBoxRect ? textTag.BoxLeft : item.Left;
            double bt = textTag.HasBoxRect ? textTag.BoxTop : item.Top;
            double br = textTag.HasBoxRect ? textTag.BoxRight : item.Right;
            double bb = textTag.HasBoxRect ? textTag.BoxBottom : item.Bottom;

            status = RenderCoreNative.resize_text_box_annotation(
                _documentHandle, livePage, liveIndex, CaptureWidth,
                (float)(bl * CaptureWidth), (float)(bt * CaptureWidth),
                (float)(br * CaptureWidth), (float)(bb * CaptureWidth), out newIndex);
        }
        else if (InkTag.TryParse(contents, out string inkColor, out double inkWidth,
                                 out var inkControl, out double inkAngle))
        {
            // A stroke raises by being re-drawn from its own control points,
            // which appends it and so puts it on top. Ink used to fall into the
            // stamp branch below, which is the wrong call for an /Ink
            // annotation, so a drawing could not be reordered at all.
            //
            // Geometry-neutral for the same reason the shape branch avoids
            // passing /Rect back: the stroke is rebuilt into its OWN upright
            // box, taken from the points themselves, which makes ScaleTo an
            // identity and repeated raises a fixed point. Feeding the
            // annotation's rectangle in would re-fit the stroke to the padded
            // box and grow it a little on every raise.
            var box = UprightBoxOf(inkControl);
            status = RebuildInkAt(
                livePage, liveIndex, CaptureWidth, inkColor, inkWidth, inkControl,
                box.Left, box.Top, box.Right, box.Bottom, id, out newIndex, inkAngle);
        }
        else
        {
            float l = (float)(item.Left * CaptureWidth);
            float t = (float)(item.Top * CaptureWidth);
            float r = (float)(item.Right * CaptureWidth);
            float b = (float)(item.Bottom * CaptureWidth);
            // NOT resize_annotation: it writes bounds in place for a same-size
            // call and would report success without moving anything.
            status = RenderCoreNative.raise_stamp_annotation(
                _documentHandle, livePage, liveIndex, CaptureWidth, l, t, r, b, out newIndex);
        }

        if (status != RenderStatus.OkPdfium) { return false; }

        // The text and shape rebuilds regenerate /Contents from the tag and drop
        // the id with it. (The stamp path carries its own across.)
        Interop.AnnotationLoader.WriteId(_documentHandle, livePage, newIndex, id);
        return true;
    }

    /// <summary>Re-points the selection at wherever the document now holds each
    /// member, by Id. After a reorder every cached index is meaningless.</summary>
    private void ResolveSelectionById(int page)
    {
        if (_selectedLoaded is LoadedSelection a && a.Id != Guid.Empty
            && FindLoadedById(a.Id, page) is (int ap, int ai))
        {
            _selectedLoaded = a with { PageIndex = ap, Index = ai };
        }
        for (int i = 0; i < _extraSelected.Count; i++)
        {
            var ex = _extraSelected[i];
            if (ex.Id != Guid.Empty && FindLoadedById(ex.Id, page) is (int ep, int ei))
            {
                _extraSelected[i] = ex with { PageIndex = ep, Index = ei };
            }
        }
    }

    /// <summary>
    /// How far a duplicate is offset from its original, in normalized page
    /// width.
    ///
    /// About 10 DIP at the layout width. Enough that the copy is visibly its
    /// own object rather than appearing to have done nothing, small enough that
    /// it is obviously related to what it came from. Every drawing tool offsets
    /// a duplicate for the same reason.
    /// </summary>
    private const double DuplicateOffset = 10.0 / 800.0;

    /// <summary>
    /// Duplicates the selection in place and offsets the copy, leaving it
    /// selected. The Ctrl+D path.
    /// </summary>
    /// <remarks>
    /// The drag version puts the copy at IDENTICAL bounds, because the drag
    /// that follows is what separates them. From the keyboard there is no drag,
    /// so an unoffset copy would sit exactly on top of the original and read as
    /// nothing having happened.
    ///
    /// One history step, not two: DuplicateSelectedForDrag pushes before it
    /// changes anything, and a step captures the state at the moment it is
    /// pushed, so it already returns to before both halves.
    /// </remarks>
    public bool DuplicateSelected()
    {
        if (!DuplicateSelectedForDrag())
        {
            return false;
        }

        NudgeSelected(DuplicateOffset, DuplicateOffset, recordHistory: false);
        return true;
    }

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
            if (!ShapeSpecFromTag(contents, sel, CaptureWidth,
                                  PagePointsFor(sel.PageIndex).W, out var spec,
                                  out string effects))
            {
                return false;
            }
            status = Interop.NativeShapes.Add(
                _documentHandle, CaptureWidth, spec, effects);
            if (status != RenderStatus.OkPdfium) { return false; }
            newIndex = LoadedFor(sel.PageIndex).Count; // will resolve after invalidate
        }
        else if (_selectedIsInk
                 && InkTag.TryParse(contents, out string inkColor, out double inkWidth,
                                    out var inkControl, out double inkAngle))
        {
            // A second stroke at the SAME place, angle included; the drag that
            // follows slides the clone off the original. Ink used to reach the
            // end of this method and return false, so Ctrl+drag on a drawing
            // quietly moved the original instead of copying it, which is the
            // one outcome a copy gesture must never produce.
            if (!AddInkFromControl(sel.PageIndex, CaptureWidth, inkColor, inkWidth, inkControl,
                                   out int inkIndex, inkAngle))
            {
                return false;
            }
            WriteInkTag(sel.PageIndex, inkIndex, inkColor, inkWidth, inkControl, inkAngle);
            status = RenderStatus.OkPdfium;
            newIndex = inkIndex;
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
            sel.PageIndex, newest.Index, newest.Left, newest.Top, newest.Right, newest.Bottom, newest.Id);
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

    /// <summary>
    /// Reconstructs a NativeShapeSpec from a selected shape's tag, at its own
    /// bounds. Returns false if the tag is not a shape we can rebuild.
    ///
    /// The READING is done by ShapeWriter.TryForExistingShape, in the viewport
    /// library, where a test can reach it. This method used to parse the tag
    /// itself, and that copy read the first eight fields and dropped the rest:
    /// three bugs of one kind came out of it, a duplicate losing its fill, a
    /// duplicated rounded rectangle coming back square, and a duplicate losing
    /// its drop shadow. None was caught by a test, because this class cannot be
    /// loaded by the test assembly.
    ///
    /// What is left here is the copy into the interop struct and nothing else.
    /// </summary>
    private static bool ShapeSpecFromTag(string contents, LoadedSelection sel,
        int captureWidth, double pageWidthPts, out Interop.NativeShapeSpec spec,
        out string effects)
    {
        spec = default;
        effects = string.Empty;

        // The selection's rectangle is the annotation's /Rect, which is not the
        // shape's extent. Recovering the extent is the core's job because the
        // resize path already had to do it; see UprightBounds.
        var (left, top, right, bottom) = UprightBounds(
            contents, sel, captureWidth, pageWidthPts);

        if (!ShapeWriter.TryForExistingShape(
                contents, left, top, right, bottom, captureWidth,
                out var rebuilt))
        {
            return false;
        }

        var geometry = rebuilt.Geometry;
        var style = rebuilt.Style;

        spec = new Interop.NativeShapeSpec
        {
            // The page is the SELECTION's, not the tag's: a paste puts the copy
            // on the page being pasted into.
            PageIndex = sel.PageIndex,
            Kind = (int)geometry.Kind,
            X1 = geometry.X1, Y1 = geometry.Y1, X2 = geometry.X2, Y2 = geometry.Y2,
            R = style.R, G = style.G, B = style.B, A = style.A,
            WidthPx = geometry.StrokeWidthPx,
            RotationDeg = geometry.RotationDeg,
            FillRgba = style.FillRgba,
            CornerRadiusPx = geometry.CornerRadiusPx,
        };

        // Verbatim off the tag, so a duplicate keeps every effect the original
        // had, including one this build cannot name.
        effects = style.Effects;

        return true;
    }

    /// <summary>
    /// A shape's own extent, recovered from the /Rect it is reported at.
    ///
    /// A rebuilt shape used to be sized from that rectangle directly, which
    /// carries the stroke pad and, for a turned shape, is the axis-aligned box
    /// CONTAINING the rotation rather than the shape itself. Every paste,
    /// ctrl+drag duplicate and undone delete therefore came back larger, and
    /// the error compounded because each copy's rectangle fed the next. At 90
    /// degrees the copy came back lying the wrong way round.
    ///
    /// Falls back to the rectangle as given whenever the core cannot help: a
    /// tag it does not recognise, a page with no width, or a rotated shape
    /// written before the tag recorded its upright size. That is exactly the
    /// behaviour this replaced, so falling back is no worse than not asking.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom) UprightBounds(
        string contents, LoadedSelection sel, int captureWidth, double pageWidthPts)
    {
        if (pageWidthPts <= 0 || captureWidth <= 0)
        {
            return (sel.Left, sel.Top, sel.Right, sel.Bottom);
        }

        byte[] tagUtf8 = System.Text.Encoding.UTF8.GetBytes(contents);

        int status = RenderCoreNative.shape_upright_bounds(
            captureWidth, (float)pageWidthPts, tagUtf8, (nuint)tagUtf8.Length,
            (float)(sel.Left * captureWidth), (float)(sel.Top * captureWidth),
            (float)(sel.Right * captureWidth), (float)(sel.Bottom * captureWidth),
            out float l, out float t, out float r, out float b);

        // Back into normalized units, the space the rest of the rebuild works
        // in. The core takes capture pixels because that is what the resize
        // path speaks, and one space for both keeps the inversion shared.
        return status == RenderStatus.OkPdfium
            ? (l / (double)captureWidth, t / (double)captureWidth,
               r / (double)captureWidth, b / (double)captureWidth)
            : (sel.Left, sel.Top, sel.Right, sel.Bottom);
    }

    /// <summary>
    /// The one place a <see cref="ShapeWriteSpec"/> becomes the interop struct.
    /// Every field is copied here and nowhere else, so a field added to the
    /// native struct has exactly one site to be threaded through.
    /// </summary>
    private static Interop.NativeShapeSpec ToNative(
        ShapeWriteSpec s, int pageIndex, byte r, byte g, byte b, byte a, uint fillRgba) => new()
        {
            PageIndex = pageIndex,
            Kind = (int)s.Kind,
            X1 = s.X1,
            Y1 = s.Y1,
            X2 = s.X2,
            Y2 = s.Y2,
            R = r, G = g, B = b, A = a,
            WidthPx = s.StrokeWidthPx,
            RotationDeg = s.RotationDeg,
            FillRgba = fillRgba,
            CornerRadiusPx = s.CornerRadiusPx,
        };

    // ---------------- Read-only document model ----------------
    //
    // A SNAPSHOT of the objects on a page, built from the annotations already
    // read back from the document. Deliberately NOT authoritative: nothing in
    // this class reads it, no rendering, selection, grouping, history or save
    // path consults it, and it goes stale the moment the document is edited.
    //
    // It exists so that a later stage has something to reason about other than
    // a tag string re-parsed on demand. Making it the source of truth before it
    // is proven accurate would put a SECOND source of truth into a codebase
    // whose worst bugs have come from having two.
    //
    // Neither method is called from a render or edit path. Building a page's
    // model costs one tag read per annotation, which is fine on demand and
    // would not be fine on every invalidation, and this app invalidates a page
    // after every single edit.

    /// <summary>
    /// The objects on one page, in paint order, as the model sees them.
    /// Reads the annotations already loaded for that page; does not touch the
    /// document.
    /// </summary>
    private readonly Dictionary<int, PageModel> _pageModelByPage = new();

    /// <summary>
    /// The page's objects, built once per page load and dropped with the
    /// annotation cache.
    ///
    /// Cached because building costs one tag read per annotation, and this app
    /// invalidates a page after every edit; rebuilding per lookup would turn a
    /// single read into one per object on every click and every drag commit.
    /// Callers that run once per user command can afford it, which is why
    /// z-order reads from here and selection does not.
    /// </summary>
    private PageModel PageModelFor(int pageIndex)
    {
        if (_pageModelByPage.TryGetValue(pageIndex, out var cached)) { return cached; }
        var model = BuildPageModel(pageIndex);
        _pageModelByPage[pageIndex] = model;
        return model;
    }

    public PageModel BuildPageModel(int pageIndex)
    {
        var snapshots = new List<AnnotationSnapshot>();
        foreach (var a in LoadedFor(pageIndex))
        {
            snapshots.Add(new AnnotationSnapshot(
                a.Index, a.Subtype, a.Left, a.Top, a.Right, a.Bottom,
                a.Opacity, a.Id, ReadAnnotationContents(pageIndex, a.Index), a.GroupId));
        }
        // The page width, so the model can convert the tags' points-valued
        // fields (stroke width, corner radius, a turned shape's upright size)
        // into the normalized units everything else is in. One FFI per model
        // build, which is once per page load, not once per object.
        var (pageWidthPts, _) = PagePointsFor(pageIndex);
        return DocumentModelBuilder.BuildPage(pageIndex, snapshots, pageWidthPts);
    }

    /// <summary>
    /// A snapshot of every page currently loaded. Pages are loaded lazily, so
    /// this covers what the user has actually visited rather than forcing a
    /// 300-page document to be read end to end.
    /// </summary>
    public DocumentModel BuildDocumentModel()
    {
        var pages = new List<PageModel>();
        for (int p = 0; p < PageCount; p++)
        {
            if (!_loadedByPage.ContainsKey(p)) { continue; }
            pages.Add(PageModelFor(p));
        }
        return DocumentModelBuilder.Build(pages);
    }

    // ---------------- Shape tag reads ----------------
    //
    // All six of these used to pick their own field out of the same string,
    // each re-splitting it and each with its own idea of what a malformed value
    // meant. They now share ShapeTagReader, which is tested and is also what
    // the document model is built from, so the model and the selection code can
    // no longer disagree about what a shape is.
    //
    // Behaviour is unchanged: the defaults below are the ones these helpers
    // already returned for a tag that does not parse.

    /// <summary>The kind number from a shape tag, or -1 if it is malformed.</summary>
    private static int ParseShapeKind(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? (int)t.Kind : -1;

    /// <summary>The corner radius from a shape tag, in PDF points. Zero for a
    /// kind that has no corners, and for any tag written before rounded
    /// rectangles existed.</summary>
    private static double ParseShapeCornerRadiusPts(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? t.CornerRadiusPts : 0;

    /// <summary>The shape's colour from its tag, as "#AARRGGBB" including the
    /// alpha, or null if the tag is malformed. Used to mirror the shape's actual
    /// colour and opacity into the tool state on selection so the pickers and
    /// the opacity slider reflect this shape, not the tool's leftover state.</summary>
    private static string? ParseShapeColor(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? t.StrokeHex : null;

    /// <summary>The shape tag's stroke width field, in points. Returns 0 on a
    /// malformed tag.</summary>
    private static double ParseShapeStrokeWidthPts(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? t.StrokeWidthPts : 0;

    /// <summary>The shape tag's optional fill, as "#AARRGGBB". Null when the
    /// shape is stroke-only.</summary>
    private static string? ParseShapeFill(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? t.FillHex : null;

    /// <summary>The shape's clockwise rotation in degrees. Zero on a tag
    /// written before rotation existed.</summary>
    private static double ParseShapeRotation(string contents) =>
        ShapeTagReader.TryParse(contents, out var t) ? t.RotationDeg : 0;

    /// <summary>Packs a "#AARRGGBB" fill hex into the 0xAARRGGBB uint the FFI
    /// wants. Null (no fill) becomes 0.</summary>
    private static uint PackShapeFillRgba(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) { return 0; }
        string h = hex.StartsWith('#') ? hex.Substring(1) : hex;
        if (h.Length != 8) { return 0; }
        return uint.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out uint v)
            ? v : 0;
    }


    /// <summary>Writes a finished ROTATE through to the document: the object is
    /// re-laid-out at its own upright bounds, turned to the new angle. Bounds
    /// are unchanged, so the marquee keeps them and its angle. Branches on the
    /// selected type so shapes go through their own FFI.</summary>
    /// <summary>The angle recorded on an image stamp's tag, or 0 for one
    /// placed before stamps were tagged (which is upright by definition).
    /// Tag shape: AyaanStamp:&lt;deg&gt;:&lt;l&gt;:&lt;t&gt;:&lt;r&gt;:&lt;b&gt;.</summary>
    private static double ParseStampRotation(string? contents)
    {
        if (string.IsNullOrEmpty(contents)) { return 0; }
        const string Prefix = "AyaanStamp:";
        if (!contents.StartsWith(Prefix, StringComparison.Ordinal)) { return 0; }
        string[] parts = contents.Substring(Prefix.Length).Split(':');
        return parts.Length > 0 && double.TryParse(parts[0],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double deg) ? deg : 0;
    }

    private void CommitRotation(LoadedSelection start)
    {
        const int CaptureWidth = 1000;

        // A multi-selection turns as one rigid body: every member ORBITS the
        // selection's centre and SPINS by the same delta. Handled before the
        // single-object paths below, which only know how to turn one mark on
        // the spot.
        if (_extraSelected.Count > 0 && _rotateOrigin.Count == _extraSelected.Count + 1)
        {
            CommitGroupRotation();
            return;
        }

        if (_selectedIsStamp)
        {
            // An image stamp is turned by re-placing its picture with a
            // rotated matrix; it has no tag geometry to redraw from the way a
            // shape does, and its bounds are recomputed core-side to the box
            // that CONTAINS the turned image, so nothing is passed here but
            // the angle.
            PushHistory(HistoryScope.Document, "Rotate stamp");
            int stampStatus = RenderCoreNative.rotate_stamp_annotation(
                _documentHandle, start.PageIndex, start.Index, CaptureWidth,
                (float)_selectedRotationDeg, out int stampNewIndex);
            if (stampStatus != RenderStatus.OkPdfium)
            {
                _selectedLoaded = start;
                RefreshSelectionOutline();
                Status = "Could not rotate that stamp.";
                return;
            }
            IsDirty = true;
            InvalidateLoadedPage(start.PageIndex);

            // Re-read the bounds: a turned stamp's rectangle is the ENLARGED
            // box that contains it, so the dragged rect is not what landed.
            var placed = LoadedFor(start.PageIndex).Find(a => a.Index == stampNewIndex);
            _selectedLoaded = placed.Index == stampNewIndex
                ? new LoadedSelection(start.PageIndex, stampNewIndex,
                    placed.Left, placed.Top, placed.Right, placed.Bottom, start.Id)
                : start with { Index = stampNewIndex };
            StampSelectedIds();
            RefreshSelectionOutline();
            return;
        }

        if (_selectedIsInk
            && InkTag.TryParse(ReadAnnotationContents(start.PageIndex, start.Index),
                               out string inkColor, out double inkWidth, out var inkControl))
        {
            // A stroke is turned by rewriting it from its UPRIGHT points at the
            // new angle, not by nudging where it currently sits. Turning a point
            // cloud moves its bounding box, so an incremental rotation would
            // drift a little on every drag; from upright there is nothing to
            // accumulate. The tag keeps the upright points either way, so a
            // later resize still scales the stroke instead of shearing it.
            PushHistory(HistoryScope.Document, "Rotate drawing");

            // The stroke's OWN upright box, taken from the points themselves.
            // NOT `start`, which is the rectangle it occupies right now: for a
            // stroke that is already turned that is the enlarged box containing
            // the turned ink, and scaling the upright points onto it would
            // stretch the drawing a little further on every rotation.
            var box = UprightBoxOf(inkControl);
            int inkStatus = RebuildInkAt(
                start.PageIndex, start.Index, CaptureWidth, inkColor, inkWidth, inkControl,
                box.Left, box.Top, box.Right, box.Bottom, start.Id, out int inkNewIndex,
                _selectedRotationDeg);

            if (inkStatus != RenderStatus.OkPdfium)
            {
                _selectedLoaded = start;
                RefreshSelectionOutline();
                Status = "Could not rotate that drawing.";
                return;
            }

            IsDirty = true;
            InvalidateLoadedPage(start.PageIndex);

            // Re-read: a turned stroke's rectangle is the box that CONTAINS the
            // turned ink, which is not the one it had upright.
            _selectedLoaded = FreshBoundsOf(start.PageIndex, inkNewIndex) is TextRect turned
                ? new LoadedSelection(start.PageIndex, inkNewIndex,
                                      turned.Left, turned.Top, turned.Right, turned.Bottom, start.Id)
                : start with { Index = inkNewIndex };
            StampSelectedIds();
            RefreshSelectionOutline();
            return;
        }

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
                start.PageIndex, newIndex, tag.BoxLeft, tag.BoxTop, tag.BoxRight, tag.BoxBottom, start.Id);
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
    /// <summary>
    /// Selects every mark on the current page.
    ///
    /// Ctrl+A means this everywhere else and had no equivalent here at all, so
    /// the only way to act on a whole page of marks was to marquee them, which
    /// fails as soon as one sits outside the rectangle you can drag.
    /// </summary>
    public bool SelectAllOnPage()
    {
        if (_documentHandle == 0)
        {
            return false;
        }

        int page = CurrentPageIndex;
        var all = LoadedFor(page);
        if (all.Count == 0)
        {
            return false;
        }

        // First becomes the anchor and the rest extras, which is the shape the
        // rest of the selection code expects: every operation reads the anchor
        // and treats _extraSelected as the ones that follow it.
        _extraSelected.Clear();
        var first = all[0];
        _selectedLoaded = new LoadedSelection(
            page, first.Index, first.Left, first.Top, first.Right, first.Bottom, first.Id);
        ApplyTextBoxSelectionInfo(page, first.Index);

        for (int i = 1; i < all.Count; i++)
        {
            var a = all[i];
            _extraSelected.Add(new LoadedSelection(page, a.Index, a.Left, a.Top, a.Right, a.Bottom, a.Id));
        }

        Diag.Log($"SelectAllOnPage p{page}: {all.Count} marks");
        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasSelectedTextBox));
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasMultiSelection));
        return true;
    }

    public void SelectNewestAnnotation(int pageIndex)
    {
        var all = LoadedFor(pageIndex);
        if (all.Count == 0)
        {
            return;
        }

        var newest = all[^1];

        // Persist the Id before selecting on it. Every creation path (shape,
        // stamp, text box) comes through here, and a freshly added annotation
        // carries NO Id in the file, so AnnotationLoader hands out a fresh
        // ephemeral one on each load. Selecting on an ephemeral Id means the
        // next cache reload issues a different value and the selection points
        // at nothing; worse, two annotations can be handed colliding values,
        // which is how a group ended up with two members claiming the same
        // identity and one shape being dropped from the move. Writing it here
        // makes the Id real. Already-persisted annotations rewrite the same
        // value, so this is a no-op for them.
        Interop.AnnotationLoader.WriteId(_documentHandle, pageIndex, newest.Index, newest.Id);

        _selectedLoaded = new LoadedSelection(
            pageIndex, newest.Index, newest.Left, newest.Top, newest.Right, newest.Bottom, newest.Id);
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

        Diag.Log($"DeleteSelectedLoaded: anchor=p{sel.PageIndex}#{sel.Index}, extras={_extraSelected.Count}");

        // Drop the deleted marks from any groups they were in. A group whose
        // members are all deleted goes away entirely; a partially-deleted
        // group shrinks. Session-only groups so this doesn't touch the PDF.
        var deletedIds = new HashSet<Guid>();
        if (sel.Id != Guid.Empty) { deletedIds.Add(sel.Id); }
        foreach (var e in _extraSelected)
        {
            if (e.Id != Guid.Empty) { deletedIds.Add(e.Id); }
        }
        if (deletedIds.Count > 0)
        {
            for (int gi = _groups.Count - 1; gi >= 0; gi--)
            {
                _groups[gi].RemoveAll(id => deletedIds.Contains(id));
                if (_groups[gi].Count < 2) { _groups.RemoveAt(gi); }
            }
        }

        // Deleting records each mark's tag so it can be rebuilt, and falls
        // back to a document snapshot only for kinds a tag cannot describe -
        // an image stamp, whose pixels live in the file. Shapes and text boxes,
        // which is most of what gets deleted, cost bytes instead of megabytes.
        BeginEdit("Delete annotation");
        foreach (var victim in new[] { sel }.Concat(_extraSelected))
        {
            if (ReadAnnotationState(victim.PageIndex, victim.Index) is not (string vtag, EditRect vrect))
            {
                RecordUnreversible();
                continue;
            }
            bool recoverable = vtag.StartsWith("AyaanShape:", StringComparison.Ordinal)
                               || TextBoxTagReader.TryParse(vtag, out _);
            if (!recoverable) { RecordUnreversible(); }
            RecordEdit(new ExistenceRecord(
                victim.Id, victim.PageIndex, vtag, vrect, ExistsAfter: false, Recoverable: recoverable));
        }

        int status = RenderCoreNative.delete_annotation(_documentHandle, sel.PageIndex, sel.Index);
        Diag.Log($"delete loaded annotation p{sel.PageIndex}#{sel.Index} -> {status}");

        if (status != RenderStatus.OkPdfium)
        {
            // Nothing was removed, so there is nothing to record. Abandoning
            // matters more than it looks: BeginEdit is re-entrant-guarded, so a
            // batch left open here would swallow every later operation's
            // records and silently stop the history.
            AbandonEdit();
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
        CommitEdit();
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
        // The cache always goes NOW: the very next line of the caller may look
        // an annotation up by Id and must not see pre-write indices. Only the
        // repaint is deferrable, and only inside a command.
        InvalidateAnnotationCache(pageIndex);
        if (_repaints.Mark(pageIndex))
        {
            RedrawPage(pageIndex);
        }
    }

    private readonly RepaintQueue _repaints = new();

    /// <summary>
    /// Runs one user command, repainting each touched page once at the end
    /// instead of once per write.
    ///
    /// Wrapping a command is the whole fix: the call sites inside it are left
    /// alone and still call InvalidateLoadedPage, but their repaints collect in
    /// the queue rather than each throwing away the page bitmap. A five-member
    /// group move went from five repaints to one without a single line inside
    /// the move changing.
    ///
    /// try/finally because these methods return early on every failure path,
    /// and a command that never flushed would leave the page showing the state
    /// before the edit.
    /// </summary>
    private void RunCommand(Action body) => RunCommand<object?>(() => { body(); return null; });

    private T RunCommand<T>(Func<T> body)
    {
        _repaints.Begin();
        try
        {
            return body();
        }
        finally
        {
            foreach (int page in _repaints.End())
            {
                RedrawPage(page);
            }
        }
    }

    /// <summary>
    /// Drops the cached annotation list for a page, and the model built from
    /// it. Cheap: two dictionary removals, no rendering.
    ///
    /// This is the half an operation needs when it is only re-resolving
    /// indices. Every write is a delete and re-add, so the indices shift under
    /// anything still queued, and the fix is to look the annotation up by its
    /// stable Id against fresh data. That needs the cache gone; it does not
    /// need the page repainted.
    /// </summary>
    private void InvalidateAnnotationCache(int pageIndex)
    {
        _loadedByPage.Remove(pageIndex);
        // The model is a projection of the annotation cache, so it is dropped
        // with it and can never be staler than the data everything else already
        // trusts. Giving it a lifetime of its own is how a second source of
        // truth starts.
        _pageModelByPage.Remove(pageIndex);
    }

    /// <summary>
    /// Throws away the page's bitmap and tiles and renders it again. Expensive,
    /// and the reason a five-member group move used to cost five repaints.
    ///
    /// Marks are part of the page bitmap, so a change stays invisible until
    /// this runs; it just does not have to run once per write.
    /// </summary>
    // ---------------- the committed drop shadow ----------------
    //
    // PDF has no blur, so a SOFT shadow cannot be a path in the file. Skia
    // draws it and it goes into the shape's own annotation as a picture,
    // underneath the shape. A HARD shadow is untouched: render_core draws it as
    // paths, crisp at any zoom.
    //
    // ONE PASS, HERE, once per command per page. Every edit that changes a
    // shape rebuilds its annotation, and a rebuild drops the picture on
    // purpose: a bitmap stretched to a new size stretches the BLUR with it, and
    // one turned bodily turns the LIGHT with it. So the rule is that the
    // picture is regenerated rather than transformed, and the one place that
    // reliably runs after every edit is the repaint.
    //
    // No cache. Rasterising costs 2 to 10ms and happens once per command, not
    // per frame, and a cache would need to know exactly what this pass exists
    // to avoid getting wrong.

    /// <summary>True while the sync is writing, so its own writes cannot start
    /// another one.</summary>
    private bool _syncingShadowImages;

    private void SyncShadowImages(int pageIndex)
    {
        if (_documentHandle == 0 || _syncingShadowImages)
        {
            return;
        }

        double pageWidthPts = PagePointsFor(pageIndex).W;
        if (pageWidthPts <= 0)
        {
            return;
        }

        _syncingShadowImages = true;
        try
        {
            // By INDEX, descending, because attaching rebuilds the annotation
            // and moves it to the end of the page's list. Walking down means an
            // index this loop has not reached yet cannot have been disturbed.
            var todo = new List<int>();
            foreach (var a in LoadedFor(pageIndex))
            {
                todo.Add(a.Index);
            }

            todo.Sort();
            todo.Reverse();

            foreach (int index in todo)
            {
                AttachShadowImage(pageIndex, index, pageWidthPts);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"SyncShadowImages p{pageIndex} failed: {ex.Message}");
        }
        finally
        {
            _syncingShadowImages = false;
        }
    }

    /// <summary>
    /// Draws one shape's soft shadow and puts it in its annotation, or does
    /// nothing at all when the shape has no soft shadow to draw.
    /// </summary>
    private void AttachShadowImage(int pageIndex, int index, double pageWidthPts)
    {
        // The same 1000 every other write across this boundary uses.
        const int CaptureWidth = 1000;

        string? contents = ReadAnnotationContents(pageIndex, index);
        if (contents is null || !ShapeTagReader.TryParse(contents, out var tag))
        {
            return;
        }

        var effects = ShapeEffectsTag.From(tag, pageWidthPts);
        if (effects is null || effects.IsEmpty)
        {
            return;
        }

        // ANY effect with a blur needs a picture; the rasteriser decides which
        // ones actually go in it. A shape whose only effect is a hard shadow, or
        // one this build cannot draw, needs nothing here.
        bool anySoft = false;
        foreach (var spec in effects.Specs)
        {
            if (spec.Blur > 0 && spec.Color.A != 0)
            {
                anySoft = true;
                break;
            }
        }

        if (!anySoft)
        {
            return;
        }

        // ALREADY DONE? Attaching rebuilds the annotation, which changes its
        // index, so doing it on every repaint churned the file and moved the
        // selection out from under itself. Every edit that changes the shape or
        // its shadow rebuilds the annotation and drops the picture, so having
        // one means having a current one.
        if (RenderCoreNative.shape_has_shadow_image(_documentHandle, pageIndex, index) == 1)
        {
            return;
        }

        var found = LoadedFor(pageIndex).FirstOrDefault(a => a.Index == index);
        if (found.Id == Guid.Empty && found.Index != index)
        {
            return;
        }

        var sel = new LoadedSelection(
            pageIndex, index, found.Left, found.Top, found.Right, found.Bottom, found.Id);
        var (l, t, r, b) = UprightBounds(contents, sel, CaptureWidth, pageWidthPts);

        var items = ShadowRasterizer.CasterItemsFor(tag, l, t, r, b, pageWidthPts);
        if (items.Count == 0)
        {
            return;
        }

        var raster = ShadowRasterizer.Rasterize(items, effects.Specs, pageWidthPts);
        if (raster is null)
        {
            return;
        }

        int newIndex = -1;
        int status = RenderCoreNative.set_shape_shadow_image(
            _documentHandle, pageIndex, index, CaptureWidth,
            (float)(raster.Value.Left * CaptureWidth),
            (float)(raster.Value.Top * CaptureWidth),
            (float)(raster.Value.Right * CaptureWidth),
            (float)(raster.Value.Bottom * CaptureWidth),
            raster.Value.Bgra, (nuint)raster.Value.Bgra.Length,
            raster.Value.PixelWidth, raster.Value.PixelHeight,
            out newIndex);

        if (status != RenderStatus.OkPdfium)
        {
            Diag.Log($"set_shape_shadow_image p{pageIndex} #{index} -> {status}");
            return;
        }

        // The annotation was rebuilt, so anything holding the old index is
        // stale. The cache is dropped rather than patched: the very next lookup
        // by id has to see where the shape actually is now.
        InvalidateAnnotationCache(pageIndex);
        RepointSelection(pageIndex, index, newIndex);
    }

    /// <summary>
    /// Follows the selection to where a rebuilt annotation actually went.
    ///
    /// A selection is held by INDEX, and attaching a shadow deletes the
    /// annotation and re-adds it at the end of the page. Left alone, the frame
    /// and its handles would then be drawn from whatever annotation had taken
    /// over the old index, which is what put a small frame in the middle of a
    /// large shape.
    /// </summary>
    private void RepointSelection(int pageIndex, int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex)
        {
            return;
        }

        static LoadedSelection Moved(LoadedSelection s, ExistingAnnotation now) =>
            s with
            {
                Index = now.Index,
                Left = now.Left, Top = now.Top, Right = now.Right, Bottom = now.Bottom,
            };

        var loaded = LoadedFor(pageIndex);

        if (_selectedLoaded is { } sel && sel.PageIndex == pageIndex && sel.Index == oldIndex)
        {
            foreach (var a in loaded)
            {
                if (a.Index == newIndex)
                {
                    _selectedLoaded = Moved(sel, a);
                    break;
                }
            }
        }

        for (int i = 0; i < _extraSelected.Count; i++)
        {
            var ex = _extraSelected[i];
            if (ex.PageIndex != pageIndex || ex.Index != oldIndex)
            {
                continue;
            }

            foreach (var a in loaded)
            {
                if (a.Index == newIndex)
                {
                    _extraSelected[i] = Moved(ex, a);
                    break;
                }
            }
        }
    }

    private void RedrawPage(int pageIndex)
    {
        var slot = SlotFor(pageIndex);
        if (slot is null)
        {
            return;
        }

        // The soft shadows first: this is the one place that reliably runs
        // after every edit, and a rebuilt annotation has had its picture
        // dropped. Before the tiles go, so the page is rendered with them.
        SyncShadowImages(pageIndex);

        slot.ClearTiles();
        slot.ReleaseBitmap();
        RenderBaseTier(slot);
        ScheduleSharpenPass();
        // One line per repaint. This is the cost the whole command batching
        // exists to control, so it stays visible: a group move that logs more
        // than one of these per page has escaped its command.
        Diag.Log($"repaint p{pageIndex} (#{++_repaintCount})");
    }

    /// <summary>Repaints since the document opened, for the log line above.</summary>
    private int _repaintCount;

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

    /// <summary>
    /// Whether a point is on the current selection, so it can be dragged.
    ///
    /// Asked of the object, not of its rectangle, because this drives the move
    /// cursor and the cursor has to agree with what a click will actually do.
    /// A SizeAll shown over the empty corner of a diagonal arrow's bounding box
    /// promises a drag that would in fact deselect.
    ///
    /// The ANCHOR only, which is what a drag moves from. Right-click wants
    /// <see cref="IsOverSelectedObject"/>.
    /// </summary>
    public bool IsOverSelection(int pageIndex, double normX, double normY) =>
        _selectedLoaded is LoadedSelection sel && Covers(sel, pageIndex, normX, normY);

    /// <summary>
    /// Whether a point is on ANY member of the selection, extras included.
    ///
    /// <see cref="IsOverSelection"/> asks only about the anchor, which is right
    /// for the move cursor: a drag is anchored. A right-click is not. Asking the
    /// anchor there would mean right-clicking the second of three selected
    /// objects reported "not on the selection", re-picked that one alone, and
    /// offered a greyed-out Group - at the exact moment the user was reaching
    /// for it.
    /// </summary>
    public bool IsOverSelectedObject(int pageIndex, double normX, double normY)
    {
        if (IsOverSelection(pageIndex, normX, normY)) { return true; }

        foreach (var ex in _extraSelected)
        {
            if (Covers(ex, pageIndex, normX, normY)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Whether one selected mark covers a point, asked of the OBJECT where the
    /// model can resolve it and of its rectangle where it cannot. The fallback
    /// is the behaviour this had before the model existed: a mark with no stable
    /// identity yet, or one whose page has been invalidated out from under the
    /// selection, stays draggable rather than turning suddenly inert.
    /// </summary>
    private bool Covers(LoadedSelection sel, int pageIndex, double normX, double normY)
    {
        if (sel.PageIndex != pageIndex)
        {
            return false;
        }

        if (sel.Id != Guid.Empty && PageModelFor(pageIndex).ById(sel.Id) is { } obj)
        {
            return ObjectHitTest.Hit(obj, normX, normY, AnnotationHitTester.DefaultTolerance);
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
    /// Whether the current selection can be resized at all.
    ///
    /// The rule lives in <see cref="AnnotationResize"/>, where it is tested;
    /// this hands back the answer worked out when the selection was made. It
    /// used to be decided here, and refused every stroke on the grounds that
    /// PDFium will not scale a path. That stopped being the whole story once
    /// strokes carried an InkTag: one of ours records its control points and is
    /// rebuilt into the dragged rectangle exactly as a shape is. A stroke from
    /// another editor still has nothing to rebuild from and is still refused.
    /// </summary>
    /// <param name="sel">
    /// The selection being asked about. Ignored: every caller passes the
    /// ANCHOR, and the answer was cached from that same anchor's subtype and
    /// tag. Kept in the signature so the call sites still read as a question
    /// about a specific object rather than about hidden state.
    /// </param>
    private bool CanResize(LoadedSelection sel) => _selectedCanResize;

    /// <summary>
    /// The box a set of stroke points occupies, which for the UPRIGHT points on
    /// a stroke's tag is the stroke's own upright box. Rebuilding into it is a
    /// no-op scale, which is what a rotation wants: turn the drawing, do not
    /// resize it.
    /// </summary>
    private static TextRect UprightBoxOf(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0) { return new TextRect(0, 0, 0, 0); }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            if (x < minX) { minX = x; }
            if (y < minY) { minY = y; }
            if (x > maxX) { maxX = x; }
            if (y > maxY) { maxY = y; }
        }

        return new TextRect(minX, minY, maxX, maxY);
    }

    /// <summary>The annotation's PDFium subtype, from the page's loaded cache.
    /// Zero-cost next to an FFI read, and the cache is always warm here because
    /// the caller has just read the same annotation's tag.</summary>
    private int SubtypeOf(int pageIndex, int index)
    {
        foreach (var a in LoadedFor(pageIndex))
        {
            if (a.Index == index) { return a.Subtype; }
        }
        return Interop.AnnotSubtype.Other;
    }

    private static void AddGrips(
        PageSlot slot, (double Left, double Top, double Right, double Bottom) frame,
        bool edges, bool rotate, double insetDips = 0)
    {
        // The SAME rectangle the frame is drawn in, inset by the caller's
        // amount, so the grips sit on the visible outer stroke edge rather than
        // floating outside the shape.
        double l = frame.Left  * SlotLayoutWidth + insetDips;
        double t = frame.Top   * SlotLayoutWidth + insetDips;
        double r = frame.Right * SlotLayoutWidth - insetDips;
        double b = frame.Bottom* SlotLayoutWidth - insetDips;
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

    /// <summary>
    /// The card showing a page, or null if the current view mode is not
    /// showing it.
    ///
    /// By PAGE NUMBER, not by position in the list. Those were the same thing
    /// while every page had a card; single-page view lays out one card, and it
    /// is page 40 rather than page 0.
    /// </summary>
    private PageSlot? SlotFor(int pageIndex)
    {
        if (pageIndex < 0)
        {
            return null;
        }

        // Continuous view is the common case and its list is page-ordered from
        // zero, so try the direct hit before walking.
        if (pageIndex < PageSlots.Count && PageSlots[pageIndex].PageIndex == pageIndex)
        {
            return PageSlots[pageIndex];
        }

        foreach (var slot in PageSlots)
        {
            if (slot.PageIndex == pageIndex)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Scrolling changed which page is current.
    ///
    /// This deliberately does NOT clear the selection. A selection can span
    /// pages, and extending one to a page below necessarily scrolls, so
    /// discarding it here would make a cross-page drag impossible.
    ///
    /// Search used to be recomputed here as well, because its results only
    /// covered the forty pages nearest the viewport and had to be re-centred
    /// every time the viewport moved. That made scrolling with a query in the
    /// box re-extract and re-scan up to forty text layers, on the UI thread,
    /// at every page boundary crossed. The index now covers the whole document
    /// and there is nothing to re-centre.
    /// </summary>
    private void OnCurrentPageChangedByScroll()
    {
        RefreshAnnotationsForCurrentPage();
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
        var raw = ForReading(await Task.Run(() => PageRenderer.RenderLowResRaw(handle, pageIndex, ThumbnailPixelWidth)));

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
            hits.Add(new LoadedSelection(_marqueePage, a.Index, a.Left, a.Top, a.Right, a.Bottom, a.Id));
        }
        Diag.Log($"EndSelectionMarquee: rect {rect.Left:F3},{rect.Top:F3}..{rect.Right:F3},{rect.Bottom:F3} hit {hits.Count} annotations");
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

        Diag.Log($"EndSelectionMarquee done: SelectionCount={SelectionCount} (anchor + {_extraSelected.Count} extras)");
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
        // The corner setting is captured on the DRAFT at the start of the drag,
        // so the preview and the written annotation round the shape by the same
        // amount even if the slider is touched mid-gesture.
        _shapeDraft = new ShapeDraft(ActiveShapeKind, Norm(x), Norm(y), Norm(x), Norm(y))
        {
            CornerFraction = ShapeCornerPercent / 100.0,
        };
        InkStrokeChanged?.Invoke();
    }

    public void ExtendShape(double x, double y, bool constrain = false)
    {
        if (_shapeDraft is not { } d)
        {
            return;
        }

        double nx = Norm(x);
        double ny = Norm(y);

        // Shift-constrain while drawing (Illustrator/Word convention). A rect
        // or ellipse becomes a square/circle (matching the LARGER of the two
        // deltas, so the shape follows the pointer as far as it went in the
        // stronger axis). A line or arrow snaps to the nearest 45° from start,
        // keeping its length - so a shift-line comes out perfectly horizontal,
        // vertical, or diagonal.
        if (constrain)
        {
            double dx = nx - d.X1;
            double dy = ny - d.Y1;
            switch (d.Kind)
            {
                case ShapeKind.Rectangle:
                case ShapeKind.RoundedRectangle:
                case ShapeKind.Ellipse:
                {
                    double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    nx = d.X1 + Math.Sign(dx == 0 ? 1 : dx) * side;
                    ny = d.Y1 + Math.Sign(dy == 0 ? 1 : dy) * side;
                    break;
                }
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                {
                    // Snap the angle to the nearest 45° and keep the length.
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    if (len > 0)
                    {
                        double angle = Math.Atan2(dy, dx);
                        double snap = Math.PI / 4; // 45°
                        double snapped = Math.Round(angle / snap) * snap;
                        nx = d.X1 + Math.Cos(snapped) * len;
                        ny = d.Y1 + Math.Sin(snapped) * len;
                    }
                    break;
                }
            }
        }

        _shapeDraft = d with { X2 = nx, Y2 = ny };
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
            // Built by ShapeWriter, not by hand. Building it here by hand is
            // what made a rounded rectangle snap square the moment the pointer
            // lifted: the preview rounded it, this write said radius 0, and the
            // core honoured the write. One builder, one place to forget a field,
            // and a test that watches that place.
            var spec = ToNative(
                ShapeWriter.ForNewShape(d, InkWidth, CaptureWidth),
                _shapePageIndex, r, g, b, a, PackShapeFillRgba(ShapeFillHex));

            BeginEdit("Draw shape");
            int status = RenderCoreNative.add_shape_annotations(
                _documentHandle, CaptureWidth, new[] { spec }, 1);

            if (status == RenderStatus.OkPdfium)
            {
                IsDirty = true;
                InvalidateLoadedPage(_shapePageIndex);
                SelectNewestAnnotation(_shapePageIndex);

                // A shape is fully described by its tag, so undo deletes it and
                // redo rebuilds it from the same description. No document copy
                // for the commonest edit there is.
                if (_selectedLoaded is LoadedSelection made
                    && ReadAnnotationState(made.PageIndex, made.Index) is (string tag, EditRect rect))
                {
                    RecordEdit(new ExistenceRecord(
                        made.Id, made.PageIndex, tag, rect, ExistsAfter: true, Recoverable: true));
                }
                CommitEdit();
            }
            else
            {
                AbandonEdit();
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

    /// <summary>Fill for rectangle and ellipse shapes as "#AARRGGBB", or null
    /// for stroke-only (the historic default). Doubles as the tool state for
    /// newly-drawn shapes AND the reflection of the selected shape's fill; the
    /// picker writes to it, the selection code reads out of it.</summary>
    [ObservableProperty]
    public partial string? ShapeFillHex { get; set; }

    /// <summary>
    /// Commits the stroke as a real /Ink annotation, the way EndShape commits a
    /// shape.
    ///
    /// Ink was the last mark still living two lives: it sat in an overlay list
    /// and only became an annotation on save. That is exactly the state shapes
    /// were rescued from, and it has the same consequence, that the selection
    /// code never sees the object, so a drawing could not be picked, moved,
    /// grouped, reordered or given handles. Writing at pen-up gives it one
    /// life, and identity, selection and undo all follow from being a real
    /// annotation rather than being built separately for ink.
    /// </summary>
    public void EndInkStroke()
    {
        var raw = _currentStroke;
        _currentStroke = null;

        // The control points, not the fitted curve. The curve goes into the
        // PDF's /InkList to be drawn; these go into the tag, so the stroke can
        // be rebuilt by undo and re-drawn at a new size by a resize.
        var control = raw is null ? new List<(double X, double Y)>() : StrokeSmoothing.Thin(raw);
        if (control.Count < 2 || _documentHandle == 0)
        {
            InkStrokeChanged?.Invoke();
            return;
        }

        const int CaptureWidth = 1000;

        BeginEdit("Draw");
        if (!AddInkFromControl(_inkPageIndex, CaptureWidth, InkColorHex, InkWidth, control, out int index))
        {
            AbandonEdit();
            InkStrokeChanged?.Invoke();
            return;
        }

        // The tag is written after the add, because add_ink_annotations writes
        // the geometry and nothing else.
        WriteInkTag(_inkPageIndex, index, InkColorHex, InkWidth, control);

        IsDirty = true;
        InvalidateLoadedPage(_inkPageIndex);
        SelectNewestAnnotation(_inkPageIndex);

        if (_selectedLoaded is LoadedSelection made
            && ReadAnnotationState(made.PageIndex, made.Index) is (string tag, EditRect rect))
        {
            // Recoverable, because the tag now describes the whole stroke.
            // Undo deletes it and redo re-draws it, with no document copy for
            // what is one of the commonest edits there is.
            RecordEdit(new ExistenceRecord(
                made.Id, made.PageIndex, tag, rect, ExistsAfter: true, Recoverable: true));
        }

        CommitEdit();
        InkStrokeChanged?.Invoke();
    }

    /// <summary>
    /// Fits the control points and writes the curve as an /Ink annotation,
    /// reporting the index it landed at.
    ///
    /// The one place ink geometry crosses the FFI, so a caller cannot forget to
    /// fit first and write the bare control points, which would put a visibly
    /// angular stroke in the document next to a smooth preview.
    /// </summary>
    /// <param name="rotationDeg">
    /// The stroke's own angle. The points handed in are UPRIGHT, as drawn, and
    /// are turned here on the way to the page: the tag keeps the upright ones so
    /// a later resize scales the stroke rather than shearing it.
    /// </param>
    private bool AddInkFromControl(
        int page, int captureWidth, string colorHex, double width,
        IReadOnlyList<(double X, double Y)> control, out int index, double rotationDeg = 0)
    {
        index = -1;

        // Turned BEFORE the fit rather than after. A rotation is rigid, so the
        // two agree, and there are far fewer control points than fitted ones.
        var placed = rotationDeg == 0
            ? control
            : Geometry2D.RotateAboutCentre(control, rotationDeg);
        var curve = StrokeSmoothing.Fit(placed);
        if (curve.Count < 2 || _documentHandle == 0)
        {
            return false;
        }

        var (r, g, b, a) = ParseHex(colorHex, defaultAlpha: 0xFF);
        var spec = new BurnStroke
        {
            PageIndex = page,
            PointOffset = 0,
            PointCount = (uint)curve.Count,
            WidthPx = (float)(width * captureWidth),
            R = r, G = g, B = b, A = a,
        };
        var points = new BurnPoint[curve.Count];
        for (int i = 0; i < curve.Count; i++)
        {
            points[i] = new BurnPoint
            {
                X = (float)(curve[i].X * captureWidth),
                Y = (float)(curve[i].Y * captureWidth),
            };
        }

        int status = RenderCoreNative.add_ink_annotations(
            _documentHandle, captureWidth, new[] { spec }, 1, points, (nuint)points.Length);
        if (status != RenderStatus.OkPdfium)
        {
            Diag.Log($"AddInkFromControl p{page} -> {status}");
            return false;
        }

        // Cache only, not a repaint: the caller is mid-command and will redraw
        // once at the end. Appending is the only ordering primitive, so the new
        // annotation is the last one.
        InvalidateAnnotationCache(page);
        var all = LoadedFor(page);
        index = all.Count > 0 ? all[^1].Index : -1;
        return index >= 0;
    }

    /// <summary>
    /// Re-draws a stroke into a new rectangle, keeping its identity.
    ///
    /// Delete then re-add, because PDFium cannot edit an annotation's geometry
    /// in place; that is the same churn every other edit in this app pays, and
    /// the reason the Id is stamped back afterwards.
    /// </summary>
    /// <param name="rotationDeg">The stroke's angle, carried through the rebuild
    /// so a turned drawing stays turned. The bounds are its UPRIGHT box; the
    /// scaled points are turned by this on the way to the page.</param>
    private int RebuildInkAt(
        int page, int index, int captureWidth, string colorHex, double width,
        IReadOnlyList<(double X, double Y)> control,
        double left, double top, double right, double bottom, Guid id, out int newIndex,
        double rotationDeg = 0)
    {
        newIndex = index;
        var scaled = InkTag.ScaleTo(control, left, top, right, bottom);

        if (RenderCoreNative.delete_annotation(_documentHandle, page, index) != RenderStatus.OkPdfium)
        {
            return RenderStatus.Unsupported;
        }
        InvalidateAnnotationCache(page);

        if (!AddInkFromControl(page, captureWidth, colorHex, width, scaled, out int added, rotationDeg))
        {
            return RenderStatus.Unsupported;
        }

        // Description first, then identity: WriteId reads the tag body and puts
        // the ID in front of it, so stamping the Id before the body would have
        // the body overwrite it.
        WriteInkTag(page, added, colorHex, width, scaled, rotationDeg);
        if (id != Guid.Empty)
        {
            Interop.AnnotationLoader.WriteId(_documentHandle, page, added, id);
        }
        InvalidateAnnotationCache(page);

        newIndex = added;
        return RenderStatus.OkPdfium;
    }

    /// <summary>
    /// Turns the selected drawing into a reusable signature.
    ///
    /// Capture is "select what you drew", not a signing dialog of its own: you
    /// sign on a real page with the real pen, at whatever zoom suits, with undo
    /// and a second attempt available. Anything selected that is not ink is
    /// ignored rather than refused, so a stray shape in the marquee does not
    /// lose the signature.
    /// </summary>
    public SignatureShape? CaptureSelectionAsSignature(string name)
    {
        var strokes = new List<(string ColorHex, double WidthNorm, IReadOnlyList<(double X, double Y)> Points)>();

        foreach (var sel in SelectedLoadedAnnotations())
        {
            if (InkTag.TryParse(ReadAnnotationContents(sel.PageIndex, sel.Index),
                                out string color, out double width, out var control))
            {
                strokes.Add((color, width, control));
            }
        }

        if (strokes.Count == 0)
        {
            Status = "Select a drawing first, then save it as a signature.";
            return null;
        }

        var shape = SignatureShape.FromDrawn(name, strokes);
        Diag.Log($"signature '{name}' captured: {strokes.Count} strokes, aspect {shape.AspectRatio:F2}");
        return shape;
    }

    /// <summary>The anchor and every extra, in one list.</summary>
    private IEnumerable<LoadedSelection> SelectedLoadedAnnotations()
    {
        if (_selectedLoaded is LoadedSelection anchor) { yield return anchor; }
        foreach (var extra in _extraSelected) { yield return extra; }
    }

    /// <summary>
    /// Drops a signature onto a page, centred on a point.
    ///
    /// Written as ordinary ink and then grouped, so it behaves as one object
    /// afterwards: drag it, resize it, delete it, undo it. That only works
    /// because drawings became real annotations first; before that a placed
    /// signature would have been invisible to selection.
    /// </summary>
    public bool PlaceSignature(SignatureShape signature, int pageIndex, double normX, double normY, double width)
    {
        if (_documentHandle == 0 || signature.Strokes.Count == 0)
        {
            return false;
        }

        const int CaptureWidth = 1000;
        var (l, t, r, b) = signature.BoxAt(normX, normY, width);
        var placed = signature.PlaceInto(l, t, r, b);

        BeginEdit("Place signature");
        var madeIds = new List<Guid>();

        foreach (var stroke in placed)
        {
            if (!AddInkFromControl(pageIndex, CaptureWidth, stroke.ColorHex, stroke.WidthNorm, stroke.Points, out int index))
            {
                continue;
            }

            WriteInkTag(pageIndex, index, stroke.ColorHex, stroke.WidthNorm, stroke.Points);

            // Read the id back rather than inventing one: AnnotationLoader
            // stamps ids on load, and an id invented here and not written would
            // be a different value the moment the page reloaded.
            var loaded = LoadedFor(pageIndex);
            var made = loaded.FirstOrDefault(a => a.Index == index);
            if (made.Id != Guid.Empty)
            {
                madeIds.Add(made.Id);
                if (ReadAnnotationState(pageIndex, index) is (string tag, EditRect rect))
                {
                    RecordEdit(new ExistenceRecord(made.Id, pageIndex, tag, rect, ExistsAfter: true, Recoverable: true));
                }
            }
        }

        if (madeIds.Count == 0)
        {
            AbandonEdit();
            Status = "Could not place the signature.";
            return false;
        }

        IsDirty = true;
        InvalidateLoadedPage(pageIndex);
        SelectByIds(pageIndex, madeIds);

        // One object, not a scattering of strokes.
        if (madeIds.Count > 1)
        {
            GroupSelected();
        }

        CommitEdit();
        Status = $"Placed {signature.Name}.";
        return true;
    }

    /// <summary>Selects exactly the given marks on a page, anchor first.</summary>
    private void SelectByIds(int pageIndex, IReadOnlyList<Guid> ids)
    {
        _extraSelected.Clear();
        _selectedLoaded = null;

        var loaded = LoadedFor(pageIndex);
        foreach (var id in ids)
        {
            int i = loaded.FindIndex(a => a.Id == id);
            if (i < 0) { continue; }

            var a = loaded[i];
            var sel = new LoadedSelection(pageIndex, a.Index, a.Left, a.Top, a.Right, a.Bottom, a.Id);
            if (_selectedLoaded is null) { _selectedLoaded = sel; }
            else { _extraSelected.Add(sel); }
        }

        RefreshSelectionOutline();
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(HasMultiSelection));
    }

    /// <summary>Stamps a stroke's description onto the annotation at an index.</summary>
    private void WriteInkTag(
        int page, int index, string colorHex, double width,
        IReadOnlyList<(double X, double Y)> control, double rotationDeg = 0)
    {
        // AARRGGBB, alpha FIRST, because that is the order ParseHex reads an
        // eight-character colour back in. Writing RRGGBBAA here round-trips a
        // stroke with its red and alpha swapped.
        var (r, g, b, a) = ParseHex(colorHex, defaultAlpha: 0xFF);
        string body = InkTag.Write($"{a:X2}{r:X2}{g:X2}{b:X2}", width, control, rotationDeg);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        int status = RenderCoreNative.set_annotation_body(
            _documentHandle, page, index, bytes, (nuint)bytes.Length);
        if (status != RenderStatus.OkPdfium)
        {
            Diag.Log($"WriteInkTag p{page}#{index} FAILED status={status}");
        }
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

        // Same pick as a single click, so a double-click opens the editor on the
        // box a click would have selected. Two pickers here would mean a box you
        // could select but not edit, or the reverse.
        if (PickLoadedAt(pageIndex, normX, normY) is not AnnotationBox hit)
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

    /// <summary>
    /// Removes the "ID:&lt;32 hex&gt;|" identity prefix, returning the bare tag
    /// body ("AyaanShape:...", "AyaanTextB:...").
    ///
    /// Phase A put that prefix on the FRONT of every annotation's /Contents,
    /// which silently broke every C# check of the form
    /// contents.StartsWith("AyaanShape:"). A shape stopped being recognised
    /// as a shape, so a group move sent its extras down the generic
    /// resize_annotation path, which relocates the annotation rectangle and
    /// leaves the drawn path where it was: the frames travelled and the
    /// shapes stayed behind. Stripping here means every caller of
    /// ReadAnnotationContents sees exactly what it saw before the prefix
    /// existed. The Rust parsers already strip it on their side.
    /// </summary>
    private static string StripIdPrefix(string contents)
    {
        const string Prefix = "ID:";
        const int HexLen = 32;
        if (!contents.StartsWith(Prefix, StringComparison.Ordinal)) { return contents; }
        int sep = contents.IndexOf('|', Prefix.Length);
        if (sep != Prefix.Length + HexLen) { return contents; }
        return contents[(sep + 1)..];
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
            return StripIdPrefix(System.Text.Encoding.UTF8.GetString(bytes));
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

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(DirtyIndicatorVisibility));
        // The title carries the unsaved marker too, so it follows the same flag.
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));

        // When the last edit happened, for the crash-recovery timer. Hung off
        // this flag rather than off the thirty-odd places that set it: a new
        // kind of edit gets it for free, and one that forgot would be invisible
        // until somebody lost work to it.
        if (value)
        {
            _lastEditUtc = DateTime.UtcNow;
        }
    }

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
    // ---------------- Record-based history ----------------

    /// <summary>
    /// Records being accumulated for the user action in progress, or null when
    /// no action is open. One gesture opens a batch, makes any number of
    /// changes, and closes it, so a drag firing hundreds of pointer events
    /// still lands as ONE undo step.
    /// </summary>
    private List<EditRecord>? _openBatch;

    private string _openBatchLabel = string.Empty;

    private bool _openBatchWasDirty;

    /// <summary>Snapshot taken at batch open, used only if a record in the
    /// batch turns out to be unreversible (see ExistenceRecord).</summary>
    private byte[]? _openBatchFallback;

    /// <summary>
    /// Opens a user action. Everything recorded until CommitEdit becomes one
    /// undo step. Re-entrant calls are ignored, so a high-level operation that
    /// internally calls a lower-level one still produces a single step.
    /// </summary>
    private void BeginEdit(string label)
    {
        // A batch left open by an early return would otherwise swallow every
        // later operation's records, because this guard would keep ignoring
        // the new BeginEdit and nothing would ever push. Rather than trust
        // every exit path, an already-open batch is CLOSED here: whatever it
        // collected is a complete user action in its own right, and pushing it
        // is strictly better than discarding it or merging it into the next
        // one. Genuine nesting does not occur - no operation here calls
        // another that also records.
        if (_openBatch is not null)
        {
            Diag.Log($"BeginEdit('{label}') found an open batch '{_openBatchLabel}' with {_openBatch.Count} records; closing it first");
            CommitEdit();
        }
        _openBatch = new List<EditRecord>();
        _openBatchLabel = label;
        _openBatchWasDirty = IsDirty;
        _openBatchFallback = null;
    }

    private void RecordEdit(EditRecord record) => _openBatch?.Add(record);

    /// <summary>
    /// Takes a document snapshot for the open batch, for an action containing a
    /// change no record can reverse. Only the first call in a batch is kept,
    /// since that is the state the batch started from.
    /// </summary>
    private void RecordUnreversible()
    {
        if (_openBatch is null || _openBatchFallback is not null || _documentHandle == 0) { return; }
        _openBatchFallback = SnapshotDocumentBytes();
    }

    /// <summary>Closes the open action and pushes it. An action that recorded
    /// nothing is dropped rather than pushed as an empty step the user would
    /// have to undo through.</summary>
    private void CommitEdit()
    {
        var batch = _openBatch;
        _openBatch = null;
        if (batch is null || batch.Count == 0) { return; }

        _history.Push(new HistoryEntry
        {
            Scope = HistoryScope.Records,
            Label = _openBatchLabel,
            WasDirty = _openBatchWasDirty,
            PageIndex = CurrentPageIndex,
            Records = batch,
            FallbackBytes = _openBatchFallback,
        });
        NotifyHistoryChanged();
    }

    /// <summary>Abandons the open action, for a gesture that changed nothing.</summary>
    private void AbandonEdit() => _openBatch = null;

    private byte[]? SnapshotDocumentBytes()
    {
        if (_documentHandle == 0) { return null; }
        var buffer = RenderCoreNative.snapshot_document(_documentHandle);
        byte[]? bytes = null;
        if (buffer.Status == RenderStatus.OkPdfium && buffer.Data != IntPtr.Zero && buffer.Len > 0)
        {
            bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
        }
        RenderCoreNative.free_byte_buffer(buffer);
        return bytes;
    }

    /// <summary>
    /// Walks a record entry. backwards is undo: records replay in reverse, each
    /// restoring its Before. Forwards is redo, applying each After in order.
    /// One code path, so the two directions cannot drift apart.
    /// </summary>
    private void ApplyRecords(HistoryEntry entry, bool backwards)
    {
        // An unreversible member (a deleted stamp) means the batch carries the
        // whole document as it was. Put that back first, then let the records
        // run so anything they describe still lands exactly.
        if (backwards && entry.FallbackBytes is { Length: > 0 })
        {
            RestoreDocumentBytes(entry.FallbackBytes);
        }

        var indices = Enumerable.Range(0, entry.Records.Count).ToList();
        if (backwards) { indices.Reverse(); }

        var touched = new HashSet<int>();
        foreach (int i in indices)
        {
            switch (entry.Records[i])
            {
                case BoundsRecord b:
                    ApplyBoundsRecord(b, backwards ? b.Before : b.After);
                    touched.Add(b.PageIndex);
                    break;

                case TagRecord t:
                    ApplyTagRecord(t, backwards ? t.BeforeTag : t.AfterTag);
                    touched.Add(t.PageIndex);
                    break;

                case ExistenceRecord e:
                    // Undo inverts what the edit did: a creation is removed, a
                    // deletion is put back.
                    ApplyExistenceRecord(e, backwards ? !e.ExistsAfter : e.ExistsAfter);
                    touched.Add(e.PageIndex);
                    break;

                case OrderRecord o:
                    // The same rewrite the command ran, aimed the other way.
                    ApplyOrder(o.Page, backwards ? o.Before : o.After, "Undo order");
                    touched.Add(o.Page);
                    break;

                case GroupsRecord g:
                    _groups.Clear();
                    foreach (var members in backwards ? g.Before : g.After)
                    {
                        _groups.Add(new List<Guid>(members));
                    }
                    break;
            }
        }

        foreach (int p in touched) { InvalidateLoadedPage(p); }

        IsDirty = backwards ? entry.WasDirty : true;
        ClearAnnotationSelection();
        RefreshAnnotationsForCurrentPage();
        RenderCurrentPage();
        NotifyHistoryChanged();
    }

    /// <summary>Puts one annotation back to a rectangle, by Id, through the
    /// same per-kind dispatch the move path uses.</summary>
    private void ApplyBoundsRecord(BoundsRecord record, EditRect target)
    {
        if (FindLoadedById(record.Id, record.PageIndex) is not (int page, int index))
        {
            Diag.Log($"history bounds id={record.Id:N}: not found");
            return;
        }
        var sel = new LoadedSelection(page, index, target.Left, target.Top, target.Right, target.Bottom, record.Id);
        int newIndex = WriteMovedAnnotation(sel, sel, 1000);
        if (newIndex >= 0)
        {
            InvalidateLoadedPage(page);
            Interop.AnnotationLoader.WriteId(_documentHandle, page, newIndex, record.Id);
            InvalidateLoadedPage(page);
        }
    }

    /// <summary>Rewrites one annotation from a stored tag: remove it, then
    /// re-create it from the tag's own description.</summary>
    private void ApplyTagRecord(TagRecord record, string tag)
    {
        if (FindLoadedById(record.Id, record.PageIndex) is not (int page, int index))
        {
            Diag.Log($"history tag id={record.Id:N}: not found");
            return;
        }
        if (RenderCoreNative.delete_annotation(_documentHandle, page, index) != RenderStatus.OkPdfium)
        {
            return;
        }
        InvalidateLoadedPage(page);
        RecreateFromTag(page, tag, record.Rect, record.Id);
    }

    private void ApplyExistenceRecord(ExistenceRecord record, bool shouldExist)
    {
        var found = FindLoadedById(record.Id, record.PageIndex);
        if ((found is not null) == shouldExist) { return; }

        if (!shouldExist)
        {
            if (found is (int page, int index))
            {
                RenderCoreNative.delete_annotation(_documentHandle, page, index);
                InvalidateLoadedPage(page);
            }
            return;
        }

        // Bringing it back. Anything not recoverable from its tag relied on the
        // batch's document snapshot, which has already been restored.
        if (record.Recoverable)
        {
            RecreateFromTag(record.PageIndex, record.Tag, record.Rect, record.Id);
        }
    }

    /// <summary>
    /// Re-creates an annotation from its tag at a rectangle, and stamps it with
    /// the Id it had before, so anything referring to it (a group, a later
    /// history record) still resolves.
    /// </summary>
    private void RecreateFromTag(int page, string tag, EditRect rect, Guid id)
    {
        const int Cap = 1000;
        bool made = false;

        if (tag.StartsWith("AyaanShape:", StringComparison.Ordinal))
        {
            var target = new LoadedSelection(page, -1, rect.Left, rect.Top, rect.Right, rect.Bottom, id);
            if (ShapeSpecFromTag(tag, target, Cap, PagePointsFor(page).W, out var spec,
                                 out string effects))
            {
                made = Interop.NativeShapes.Add(_documentHandle, Cap, spec, effects)
                       == RenderStatus.OkPdfium;
            }
        }
        else if (InkTag.TryParse(tag, out string inkColor, out double inkWidth, out var control))
        {
            // Re-drawn at the RECT the record carries, not where it was first
            // drawn, so undoing a move-then-delete puts the stroke back where
            // it last was. Scaling to the rect is the same operation a resize
            // performs, which is why both go through InkTag.ScaleTo.
            made = AddInkFromControl(
                page, Cap, inkColor, inkWidth,
                InkTag.ScaleTo(control, rect.Left, rect.Top, rect.Right, rect.Bottom),
                out int inkIndex);

            // The tag has to be re-stamped: add_ink_annotations writes geometry
            // only, so without this the rebuilt stroke would come back with no
            // description and could never be undone or resized again.
            if (made)
            {
                WriteInkTag(page, inkIndex, inkColor, inkWidth,
                    InkTag.ScaleTo(control, rect.Left, rect.Top, rect.Right, rect.Bottom));
            }
        }
        else if (TextBoxTagReader.TryParse(tag, out var box))
        {
            var (rr, gg, bb, aa) = ParseHex(box.ColorHex, defaultAlpha: 0xFF);
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(box.Text);
            byte[]? fontUtf8 = string.IsNullOrEmpty(box.FontPath)
                ? null
                : System.Text.Encoding.UTF8.GetBytes(box.FontPath);
            made = RenderCoreNative.add_text_box_annotation_styled(
                _documentHandle, page, Cap,
                (float)(rect.Left * Cap), (float)(rect.Top * Cap),
                (float)(rect.Right * Cap), (float)(rect.Bottom * Cap),
                utf8, (nuint)utf8.Length,
                (float)(box.FontSizeNorm * Cap), rr, gg, bb, aa,
                (int)box.Align,
                PackRgba(box.FillHex), PackRgba(box.OutlineHex),
                (float)(box.OutlineWidthNorm * Cap),
                fontUtf8, (nuint)(fontUtf8?.Length ?? 0),
                box.Underline ? 1 : 0, box.Strikethrough ? 1 : 0) == RenderStatus.OkPdfium;
        }

        if (!made) { return; }

        InvalidateLoadedPage(page);
        var all = LoadedFor(page);
        if (all.Count > 0)
        {
            Interop.AnnotationLoader.WriteId(_documentHandle, page, all[^1].Index, id);
            InvalidateLoadedPage(page);
        }
    }

    private void RestoreDocumentBytes(byte[] bytes)
    {
        ulong restored = RenderCoreNative.open_document_from_bytes(bytes, (nuint)bytes.Length);
        if (restored == 0) { return; }
        CloseCurrentDocument();
        _documentHandle = restored;
        _textLayers.Clear();
        ClearSelection();
        ClearLoadedAnnotations();
        PageCount = Math.Max(0, RenderCoreNative.get_page_count(_documentHandle));
        Thumbnails.Clear();
        for (int i = 0; i < PageCount; i++)
        {
            Thumbnails.Add(new PageThumbnail(i) { CardWidth = ThumbnailDisplayWidth });
        }
    }

    /// <summary>The tag and rectangle of one annotation right now, for
    /// recording the "before" side of an edit. Null if it cannot be found.</summary>
    private (string Tag, EditRect Rect)? ReadAnnotationState(int page, int index)
    {
        string? tag = ReadAnnotationContents(page, index);
        if (tag is null) { return null; }
        foreach (var a in LoadedFor(page))
        {
            if (a.Index == index)
            {
                return (tag, new EditRect(a.Left, a.Top, a.Right, a.Bottom));
            }
        }
        return null;
    }

    /// <summary>
    /// Emits one BoundsRecord per object of a finished move or resize, pairing
    /// each object's pre-gesture rectangle with where it actually ended up.
    ///
    /// The "after" has to be read now rather than assumed from the drag: a text
    /// box re-wraps to a different height, and a rotated mark's rectangle is
    /// the enlarged box that contains it, so what was dragged is not always
    /// what landed.
    /// </summary>
    private void RecordBoundsBatch(IReadOnlyList<AnnotationBoundsState> befores)
    {
        foreach (var b in befores)
        {
            if (b.Id == Guid.Empty) { continue; }
            if (FindLoadedById(b.Id, b.PageIndex) is not (int page, int index)) { continue; }

            foreach (var a in LoadedFor(page))
            {
                if (a.Index != index) { continue; }
                RecordEdit(new BoundsRecord(
                    b.Id, page,
                    new EditRect(b.Left, b.Top, b.Right, b.Bottom),
                    new EditRect(a.Left, a.Top, a.Right, a.Bottom)));
                break;
            }
        }
    }

    /// <summary>Snapshot of the current grouping, for a GroupsRecord.</summary>
    private IReadOnlyList<IReadOnlyList<Guid>> SnapshotGroups() =>
        _groups.Select(g => (IReadOnlyList<Guid>)new List<Guid>(g)).ToList();

    /// <summary>
    /// The entry to put on the opposite stack when one is applied.
    ///
    /// A Records entry is returned UNCHANGED, because it already holds both
    /// sides of every change it describes: undoing it is walking it backwards
    /// and redoing it is walking it forwards, so the same object serves both
    /// stacks. Only the older snapshot scopes need the current state captured
    /// to build their inverse.
    /// </summary>
    private HistoryEntry Capture(HistoryEntry target) =>
        target.Scope == HistoryScope.Records
            ? target
            : Capture(target.Scope, target.Label, target.Bounds);

    private HistoryEntry Capture(HistoryScope scope, string label,
                                 IReadOnlyList<AnnotationBoundsState>? boundsTargets = null)
    {
        // For a per-annotation step the inverse is those SAME annotations'
        // rectangles as they stand right now, so redo puts them back where
        // undo took them from. Read live rather than remembered, since every
        // write deletes and re-adds and so changes indices.
        var bounds = new List<AnnotationBoundsState>();
        if (scope == HistoryScope.AnnotationBounds && boundsTargets is not null)
        {
            foreach (var target in boundsTargets)
            {
                AnnotationBoundsState? live = null;

                // By Id first. The index in the target is a hint from when the
                // drag started and may already be stale.
                if (target.Id != Guid.Empty
                    && FindLoadedById(target.Id, target.PageIndex) is (int lp, int li))
                {
                    foreach (var a in LoadedFor(lp))
                    {
                        if (a.Index == li)
                        {
                            live = new AnnotationBoundsState(lp, li, a.Left, a.Top, a.Right, a.Bottom, target.Id);
                            break;
                        }
                    }
                }

                // The annotation is gone, or was never stamped, so there is
                // nothing live to read. Keep the remembered rectangle rather
                // than dropping the step.
                bounds.Add(live ?? target);
            }
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
                             IReadOnlyList<AnnotationBoundsState>? bounds = null)
    {
        // A bounds step records the rectangle as it is RIGHT NOW, which is
        // where undo has to put it back to. Passed through Capture rather than
        // patched on afterwards, since HistoryEntry is deliberately immutable.
        _history.Push(Capture(scope, label, bounds));
        NotifyHistoryChanged();
    }

    // Both log whether an entry came back. Without it a press that found
    // nothing to undo and a press that never reached here look identical from
    // the outside, and both read to the user as "undo is laggy".
    public void Undo()
    {
        var target = _history.Undo(Capture);
        Diag.Log($"undo: {(target is null ? "NOTHING to undo" : "applying")} canUndo={_history.CanUndo} canRedo={_history.CanRedo}");
        if (target is not null)
        {
            ApplyHistoryEntry(target, backwards: true);
        }
    }

    public void Redo()
    {
        var target = _history.Redo(Capture);
        Diag.Log($"redo: {(target is null ? "NOTHING to redo" : "applying")} canUndo={_history.CanUndo} canRedo={_history.CanRedo}");
        if (target is not null)
        {
            ApplyHistoryEntry(target, backwards: false);
        }
    }

    /// <summary>
    /// The single apply path shared by undo and redo. Restores whatever the
    /// entry holds; an asymmetry between the two directions is not expressible
    /// because neither has its own restore code.
    /// </summary>
    // One repaint for a whole undo or redo. This is the outermost of the
    // history calls: ApplyRecords, ApplyBoundsRecord, ApplyOrder, ApplyTagRecord,
    // ApplyExistenceRecord and RecreateFromTag all sit underneath it, and the
    // queue's nesting means none of them flushes on its own.
    private void ApplyHistoryEntry(HistoryEntry entry, bool backwards)
        => RunCommand(() => ApplyHistoryEntryCore(entry, backwards));

    private void ApplyHistoryEntryCore(HistoryEntry entry, bool backwards)
    {
        // Record entries describe their own reversal, so they take the shared
        // walker rather than any of the snapshot restores below.
        if (entry.Scope == HistoryScope.Records)
        {
            ApplyRecords(entry, backwards);
            return;
        }

        // A per-annotation step restores one rectangle and leaves everything
        // else alone. It must NOT fall through to the overlay restore below:
        // this annotation lives in the document, not in those lists, and
        // rewriting them would wipe marks made since.
        if (entry.Scope == HistoryScope.AnnotationBounds && entry.Bounds.Count > 0)
        {
            const int CaptureWidth = 1000;
            _extraSelected.Clear();
            LoadedSelection? restoredAnchor = null;

            foreach (var b in entry.Bounds)
            {
                // Resolve by Id. The recorded index is a hint: every write in
                // between deleted and re-added annotations, so it may now name
                // a different mark entirely.
                int page = b.PageIndex;
                int index = b.Index;
                if (b.Id != Guid.Empty && FindLoadedById(b.Id, b.PageIndex) is (int lp, int li))
                {
                    page = lp;
                    index = li;
                }

                // WriteMovedAnnotation, NOT resize_annotation. The latter
                // REFUSES a shape outright, so undoing a shape move used to
                // fail silently: the history step was consumed and the drawing
                // never moved. Proved by
                // undoing_a_shape_move_puts_the_drawing_back. This helper does
                // the same per-kind dispatch the move path uses.
                var from = new LoadedSelection(page, index, b.Left, b.Top, b.Right, b.Bottom, b.Id);
                int newIndex = WriteMovedAnnotation(from, from, CaptureWidth);
                Diag.Log($"undo bounds p{page}#{index} id={b.Id:N} -> newIndex {newIndex}");
                if (newIndex < 0) { continue; }

                InvalidateLoadedPage(page);
                if (b.Id != Guid.Empty)
                {
                    Interop.AnnotationLoader.WriteId(_documentHandle, page, newIndex, b.Id);
                    InvalidateLoadedPage(page);
                }

                var restored = new LoadedSelection(page, newIndex, b.Left, b.Top, b.Right, b.Bottom, b.Id);
                if (restoredAnchor is null) { restoredAnchor = restored; }
                else { _extraSelected.Add(restored); }
            }

            if (restoredAnchor is not null)
            {
                IsDirty = entry.WasDirty;
                _selectedLoaded = restoredAnchor;
                _loadedGrip = LoadedAnnotationPicker.Grip.None;
                _loadedDrag = null;
                _extraDragOrigin.Clear();
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

                // The loaded-annotation cache describes the document we just
                // replaced. Leaving it in place means hit-testing, the
                // selection frame and every subsequent write address
                // annotations that no longer exist, on a handle that no longer
                // exists either. Nothing else here drops it: ClearSelection
                // forgets what is selected, not what was read off the page.
                ClearLoadedAnnotations();

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
    //
    // Search used to cover the forty pages nearest the viewport, on the UI
    // thread, and report the answer as "3 of 12 pages" as though it had covered
    // the document. On a 3352-page book that is about one per cent of it,
    // presented in a form that reads like a complete result. It now reads every
    // page, off the UI thread, and counts matches rather than pages.

    /// <summary>Pages read between one batch of results and the next. Small
    /// enough that the count starts climbing almost at once, large enough that a
    /// long book does not marshal thousands of times to the UI thread.</summary>
    private const int SearchBatchPages = 32;

    /// <summary>Quiet time after a keystroke before the sweep starts, so typing
    /// "cat" starts one search and not three.</summary>
    private const int SearchDebounceMs = 250;

    /// <summary>
    /// The results of the current search, or null when there is no search.
    ///
    /// Holds match POSITIONS (page, offset, length), never rectangles. A book
    /// can produce thousands of matches and this is rebuilt whenever the query
    /// changes; rectangles are derived from one page's text layer at a time,
    /// only for what is actually drawn.
    /// </summary>
    private SearchIndex? _searchIndex;

    private CancellationTokenSource? _searchCts;
    private DispatcherQueueTimer? _searchDebounce;

    /// <summary>
    /// Which search a background batch belongs to.
    ///
    /// The token stops the sweep, but a batch already in flight to the UI thread
    /// cannot be recalled, and it carries page indices that a page delete or a
    /// document switch may already have invalidated. Comparing generations on
    /// arrival is what makes a late batch harmless rather than a set of results
    /// pointing into a document that no longer exists.
    /// </summary>
    private int _searchGeneration;

    /// <summary>"12 of 431", "No matches", or empty.</summary>
    public string SearchStatus => _searchIndex?.Status ?? string.Empty;

    public bool HasSearchMatches => _searchIndex is { Total: > 0 };

    /// <summary>True while the sweep is still reading pages, so the view can
    /// show that the total is still climbing without putting a marker in the
    /// count itself.</summary>
    public bool IsSearching => _searchIndex is { Complete: false };

    private void NotifySearchChanged()
    {
        OnPropertyChanged(nameof(SearchStatus));
        OnPropertyChanged(nameof(HasSearchMatches));
        OnPropertyChanged(nameof(IsSearching));
    }

    /// <summary>
    /// The page whose slot currently holds search rectangles, or -1.
    ///
    /// Remembered so that moving the selection clears exactly one slot instead
    /// of walking all of them. On a 3352-page book that is the difference
    /// between touching one collection per keystroke and touching 3352.
    /// </summary>
    private int _highlightedSearchPage = -1;

    /// <summary>
    /// Draws the matches on the page holding the selected match, and clears
    /// every other page.
    ///
    /// ONE page's worth of rectangles exists at a time. A search over a book can
    /// find thousands of matches and each rectangle is a laid-out element; the
    /// reader can only look at one page, so that is the only page drawn. The
    /// index keeps positions, not rectangles, precisely so that this can be
    /// derived on demand and thrown away again.
    /// </summary>
    private void RefreshSearchHighlights()
    {
        int page = _searchIndex?.Current?.PageIndex ?? -1;

        if (_highlightedSearchPage >= 0 && _highlightedSearchPage != page)
        {
            SlotFor(_highlightedSearchPage)?.SearchMatchRects.Clear();
            _highlightedSearchPage = -1;
        }

        if (page < 0 || _searchIndex is not { } index || SlotFor(page) is not { } slot)
        {
            return;
        }

        // Rebuilt rather than patched even when the page has not changed: the
        // selection moving within a page changes which rectangle is the strong
        // colour, and a page's matches are a handful of rects.
        slot.SearchMatchRects.Clear();
        _highlightedSearchPage = page;

        foreach (var (rect, hex) in
                 SearchHighlight.RectsFor(TextLayerFor(page), index.OnPage(page), index.Current))
        {
            var scaled = ScaledRect.From(NormRect(rect), SlotLayoutWidth, hex);
            if (scaled.IsVisible)
            {
                slot.SearchMatchRects.Add(scaled);
            }
        }
    }

    partial void OnSearchQueryChanged(string value) => RestartSearch();

    partial void OnSearchMatchCaseChanged(bool value) => RestartSearch();

    partial void OnSearchWholeWordChanged(bool value) => RestartSearch();

    /// <summary>
    /// Abandons the running search and schedules a fresh one.
    ///
    /// Scheduled rather than started, for two reasons. It debounces typing, and
    /// it means the document lifecycle can call this from the middle of opening
    /// a file: by the time the timer ticks, PageCount and CurrentPageIndex
    /// describe the document that is actually open.
    /// </summary>
    private void RestartSearch()
    {
        CancelSearch();

        _searchDebounce ??= CreateSearchDebounceTimer();
        _searchDebounce.Stop();

        if (string.IsNullOrEmpty(SearchQuery))
        {
            return;
        }

        _searchDebounce.Start();
    }

    private DispatcherQueueTimer CreateSearchDebounceTimer()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SearchDebounceMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => StartSearchSweep();
        return timer;
    }

    /// <summary>
    /// Stops the running search and drops its results.
    ///
    /// The generation bump matters as much as the token: it is what a batch
    /// already on its way to the UI thread is checked against.
    /// </summary>
    private void CancelSearch()
    {
        _searchDebounce?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searchGeneration++;

        if (_searchIndex is not null)
        {
            _searchIndex = null;
            RefreshSearchHighlights();
            NotifySearchChanged();
        }
    }

    /// <summary>
    /// Reads every page of the document for the query, off the UI thread,
    /// publishing what it finds as it goes.
    ///
    /// Reading a page's text LOADS and parses that page, so a long book takes
    /// real time however this is written; the answer is to stream rather than to
    /// wait. Pages are read from the one being viewed, forward, then round to
    /// the start, so the first results to appear are the ones nearest the reader
    /// and in the direction Next travels.
    ///
    /// Everything the background task touches is captured by value here. It
    /// never reads a property and never touches <see cref="_textLayers"/>, which
    /// is a plain Dictionary belonging to the UI thread.
    /// </summary>
    private void StartSearchSweep()
    {
        CancelSearch();

        string query = SearchQuery;
        ulong handle = _documentHandle;
        int pages = PageCount;
        int startPage = CurrentPageIndex;
        int width = (int)SlotLayoutWidth;
        var options = new SearchOptions(SearchMatchCase, SearchWholeWord);

        if (handle == 0 || pages <= 0 || string.IsNullOrEmpty(query))
        {
            return;
        }

        int generation = ++_searchGeneration;
        _searchIndex = new SearchIndex(startPage);
        NotifySearchChanged();

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        var sw = Stopwatch.StartNew();

        _ = Task.Run(() =>
        {
            var batch = new List<SearchMatch>();
            int readSincePublish = 0;

            foreach (int page in SearchSweepOrder.PagesFrom(startPage, pages))
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Not TextLayerFor: that caches into a Dictionary owned by the
                // UI thread. A sweep's layers are read once and dropped.
                var layer = TextLayerLoader.Load(handle, page, width);
                if (layer is not null)
                {
                    foreach (var (start, length) in layer.FindMatches(query, options))
                    {
                        batch.Add(new SearchMatch(page, start, length));
                    }
                }

                if (++readSincePublish >= SearchBatchPages)
                {
                    PublishSearchBatch(batch, generation, complete: false);
                    batch = new List<SearchMatch>();
                    readSincePublish = 0;
                }
            }

            Diag.Log($"search '{query}' swept {pages} pages in {sw.ElapsedMilliseconds}ms");
            PublishSearchBatch(batch, generation, complete: true);
        }, token);
    }

    /// <summary>
    /// Hands a batch of matches back to the UI thread, dropping it if the search
    /// it belongs to has been superseded.
    /// </summary>
    private void PublishSearchBatch(List<SearchMatch> batch, int generation, bool complete)
    {
        if (batch.Count == 0 && !complete)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _searchGeneration || _searchIndex is not { } index)
            {
                return;
            }

            // Whether this batch is the one that gives the search its first
            // result. The reader is taken there, which is what makes the first
            // Enter go to the SECOND match rather than skipping the first.
            bool hadSelection = index.Current is not null;

            if (batch.Count > 0)
            {
                index.Add(batch);
            }
            if (complete)
            {
                index.MarkComplete();
            }

            if (!hadSelection && index.Current is { } first)
            {
                // The batch that gives the search its first result is also the
                // only batch that can change what is drawn: a page is read
                // entirely within one batch, so no later one can add matches to
                // the page already being shown.
                RefreshSearchHighlights();
                GoToPage(first.PageIndex, animate: true, BandFor(first));
            }

            NotifySearchChanged();
        });
    }

    /// <summary>
    /// Moves to the next or previous MATCH, wrapping at the ends.
    ///
    /// Match by match, not page by page. Stepping by page meant a page holding
    /// six hits counted once and five of them could not be reached at all.
    /// </summary>
    public void StepSearchMatch(int direction)
    {
        if (_searchIndex is not { Total: > 0 } index)
        {
            return;
        }

        var match = direction >= 0 ? index.Next() : index.Previous();
        RefreshSearchHighlights();
        NotifySearchChanged();

        if (match is { } m)
        {
            GoToPage(m.PageIndex, animate: true, BandFor(m));
        }
    }

    /// <summary>
    /// The vertical band a match occupies on its page, in slot-space DIPs, or
    /// null when the page has no text to measure against.
    ///
    /// The layer is extracted at the slot width, so its rectangles are already
    /// in slot space and need no conversion. A match wrapping across a line
    /// break produces several rectangles; the band spans all of them, so the
    /// whole hit is what gets shown rather than its first line.
    /// </summary>
    private PageBand? BandFor(SearchMatch match)
    {
        if (TextLayerFor(match.PageIndex) is not { } layer)
        {
            return null;
        }

        var rects = layer.GetRangeRects(match.Start, match.Length);
        if (rects.Count == 0)
        {
            return null;
        }

        return new PageBand(rects.Min(r => r.Top), rects.Max(r => r.Bottom));
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
        RefreshAnnotationsForCurrentPage();
    }

    /// <summary>
    /// Display scale (1.0 at 100%, 1.5 at 150%), supplied by MainPage from
    /// XamlRoot.RasterizationScale.
    /// </summary>
    public double RasterizationScale { get; set; } = 1.0;

    private void CloseCurrentDocument()
    {
        // A sweep in flight is reading pages out of the document about to be
        // closed. The handle it holds stops resolving the moment this returns,
        // which render_core answers safely, but there is no reason to let it
        // keep asking.
        CancelSearch();

        _sharpenTimer?.Stop();

        if (_documentHandle != 0)
        {
            RenderCoreNative.close_document(_documentHandle);
            _documentHandle = 0;
        }
    }

    /// <summary>
    /// Gives up on a document that was never opened, leaving an empty tab.
    ///
    /// A refused password is the only caller. OpenDocument records the path
    /// before it knows whether the file will open, so without this the tab
    /// would sit there named after a document it does not have, offering to
    /// save it.
    /// </summary>
    public void AbandonOpen()
    {
        CloseCurrentDocument();

        _currentDocumentPath = null;
        PageCount = 0;
        Thumbnails.Clear();
        PageSlots.Clear();
        IsDirty = false;
        NotifyDocumentTitleChanged();
        RebuildContinuousLayout();
    }

    /// <summary>
    /// Closes down cleanly, which is what tells the next run there was no
    /// crash.
    ///
    /// The absence of a snapshot IS the signal. Anything left in the recovery
    /// folder belongs to a run that did not get here, which is why there is no
    /// heartbeat and no "still running" flag: both have to be correct in
    /// exactly the circumstances where nothing gets a chance to be.
    /// </summary>
    public void ShutDownCleanly()
    {
        _snapshotTimer?.Stop();

        // Only the snapshot for THIS document. Another tab may still be open
        // and holding work of its own.
        RecoveryStore.Discard(_currentDocumentPath ?? string.Empty);

        CloseCurrentDocument();
    }

    public void Dispose() => ShutDownCleanly();
}
