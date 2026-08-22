using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfEditorApp.Controls;
using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace PdfEditorApp;

/// <summary>
/// The main content page. Pan and zoom belong to the <c>ScrollView</c>
/// (compositor-thread InteractionTracker), so this class no longer owns any
/// transform state — it only routes tool gestures (select / highlight / note
/// / draw) and keeps the zoom readout and re-render scheduling in step with
/// the scroller.
/// </summary>
public sealed partial class MainPage : Page
{
    public ViewportViewModel ViewModel { get; } = new();

    private bool _isSpaceHandActive;

    /// <summary>The enum the currently applied cursor was chosen from; see Apply.</summary>
    private object? _appliedCursorKey;
    private bool _isCtrlDown;
    private bool _isSelectingText;
    private bool _isDrawing;
    private bool _isDrawingShape;
    private bool _isMovingAnnotation;
    private bool _isMarqueeing;

    /// <summary>Distinguishes the SELECT-tool marquee (picks up annotations) from
    /// the HIGHLIGHT-tool marquee (creates a highlight). Both share the same
    /// preview drawing and pointer flag; only the release side branches.</summary>
    private bool _isAnnotationMarquee;
    private bool _isPanning;
    private Point _panLastPoint;
    private Point _panTarget;
    private uint _dragPointerId;
    private Polyline? _livePreviewStroke;

    public MainPage()
    {
        InitializeComponent();

        // DIAGNOSTIC (temporary): watch EVERY key that reaches RootGrid,
        // including ones an earlier handler already marked Handled. The normal
        // KeyDown handler below is skipped for those, so on its own it cannot
        // tell "the key never arrived" from "something upstream ate it". This
        // only writes to the log; it never sets Handled and never changes
        // routing.
        RootGrid.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(DiagKeyDownSpy),
            handledEventsToo: true);
        ViewModel.InkStrokeChanged += OnInkStrokeChanged;
        ViewModel.SelectionVisualsChanged += UpdateObjectToolbar;
        ViewModel.InkStrokes.CollectionChanged += OnInkStrokesCollectionChanged;
        // Shapes are a SEPARATE collection but share the ink canvas, so without
        // this a finished shape was added to the model and nothing ever redrew
        // it: the drag preview vanished on mouse-up and left an empty page.
        ViewModel.Shapes.CollectionChanged += OnInkStrokesCollectionChanged;
        ViewModel.LayoutRebuilt += OnLayoutRebuilt;
        ViewModel.ViewRotated += OnViewRotated;

        // handledEventsToo: the ScrollView marks the wheel handled before this
        // would bubble, so without the flag the handler never runs at all.
        PageScroller.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(PageScroller_PointerWheelChanged),
            handledEventsToo: true);

        // Same reason: a drawing tool marks pointer moves handled while it is
        // tracking, and the bar has to come back in full screen whether or not
        // the reader is in the middle of something. It only ever reads.
        RootGrid.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(RootGrid_PointerMoved),
            handledEventsToo: true);
        ViewModel.ScrollToPageRequested += OnScrollToPageRequested;
        // Drag-reorder in the thumbnail list moves an item in this collection;
        // that is the signal to rebuild the document in the new order.
        ViewModel.Thumbnails.CollectionChanged += Thumbnails_CollectionChanged;

        // When the loaded selection changes to (or from) one of our text boxes,
        // the toolbar's font/fill/outline sections need to show up (or hide) even
        // though the active tool has not changed. Any tool + a selected text box
        // exposes its style, the way Word does.
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.HasSelectedTextBox)
                || args.PropertyName == nameof(ViewModel.HasSelectedShape)
                || args.PropertyName == nameof(ViewModel.HasMultiSelection))
            {
                UpdateToolRail();
            }

            if (args.PropertyName == nameof(ViewModel.WindowTitle))
            {
                PushWindowTitle();
                RefreshRecentMenu();
            }

            if (args.PropertyName == nameof(ViewModel.CurrentPageIndex))
            {
                SyncPageJumpBox();
            }

            // A document arriving or leaving decides which chrome makes sense.
            if (args.PropertyName == nameof(ViewModel.PageCount))
            {
                UpdateChromeForDocument();
            }

            if (args.PropertyName is nameof(ViewModel.CanUndo)
                or nameof(ViewModel.CanRedo)
                or nameof(ViewModel.HasDocumentPath)
                or nameof(ViewModel.IsDirty))
            {
                CommandStateChanged?.Invoke(this);
            }
        };
        Loaded += (_, _) =>
        {
            RootGrid.Focus(FocusState.Programmatic);
            InitializePenPickers();
            UpdateToolRail();
            UpdateCursor();
            PushWindowTitle();
            RefreshRecentMenu();
            RefreshSignatureMenu();
            SyncPageJumpBox();
            ApplySettings();
        };

        // Lets the app be driven headlessly for diagnosis: set
        // PDFEDITOR_AUTOOPEN to a PDF path and it loads on startup, and
        // PDFEDITOR_AUTOZOOM to a factor to zoom there once it has settled.
        // Deep zoom is where the interesting render behaviour lives, and
        // without this it can only be reached by hand.
        Loaded += async (_, _) =>
        {
            // Development diagnostic, before anything else touches the window:
            // draw one rectangle through both renderers, write a PNG of each,
            // and quit. Guarded by an environment variable, so it cannot fire
            // for a user, and it runs first so no document or dialog is in the
            // way of the capture.
            if (Rendering.SkiaParityCapture.RequestedDirectory is { } captureDir
                && Content is Panel captureHost)
            {
                string report = await Rendering.SkiaParityCapture.RunAsync(captureHost, captureDir);
                Diag.Log($"skia-capture: {report}");
                Application.Current.Exit();
                return;
            }

            if (Rendering.SkiaPerfCapture.RequestedDirectory is { } perfDir
                && Content is Panel perfHost)
            {
                string report = Rendering.SkiaPerfCapture.Run(perfHost, perfDir);
                Diag.Log($"skia-perf: {report}");
                Application.Current.Exit();
                return;
            }

            if (Rendering.RotationEvidenceCapture.RequestedDirectory is { } rotationDir
                && Content is Panel rotationHost)
            {
                string report = await Rendering.RotationEvidenceCapture.RunAsync(rotationHost, rotationDir);
                Diag.Log($"rotation-evidence: {report}");
                Application.Current.Exit();
                return;
            }

            // Before anything is opened. A previous run that did not shut down
            // cleanly left its work behind, and the reader should be asked
            // about it before the app puts something else in front of them.
            bool recovered = await OfferRecoveryAsync();

            ViewModel.StartRecoverySnapshots();

            string probe = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOOPEN") ?? "";
            if (recovered)
            {
                // Recovered work is already in this tab. Opening anything else
                // over it would throw away what the reader just asked for.
            }
            else if (InitialDocumentPath is { } wanted && System.IO.File.Exists(wanted))
            {
                // A tab opened for a specific file. Set before this page is
                // shown, because a Page has no constructor the window can pass
                // arguments to.
                await LoadDocumentAsync(wanted);
            }
            else if (probe.Length > 0 && System.IO.File.Exists(probe))
            {
                await LoadDocumentAsync(probe);
            }
            else if (StartBlank)
            {
                // File > New asked for a document, so make one. It is a real
                // one-page document, so every tool, the rulers and the save
                // path behave exactly as they do for a file the user opened.
                //
                // OpenBlankDocument, not OpenDocument: this page must not
                // remember the template's path inside the install folder, or
                // Ctrl+S would write over the blank every new document is made
                // from.
                ViewModel.OpenBlankDocument();
            }

            // Otherwise nothing is opened, and the empty state shows.
            //
            // Launching used to conjure a blank one-page document nobody asked
            // for. It made the app look like it had opened something, put an
            // untitled document in front of a user who wanted to open a file,
            // and meant the empty state below could never be seen.

            // Places a stamp straight after opening, so the decode-and-place
            // path can be checked without a mouse. Done inline rather than on
            // a timer: a previous harness here scheduled one that never fired,
            // and its silence was mistaken for the app crashing.
            string stamp = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOSTAMP") ?? "";
            if (stamp.Length > 0 && System.IO.File.Exists(stamp))
            {
                var pixels = await StampLibrary.DecodeAsync(stamp);
                Diag.Log(pixels is null
                    ? $"autostamp: could not decode {stamp}"
                    : $"autostamp: decoded {pixels.Width}x{pixels.Height}, {pixels.Bgra.Length} bytes");

                if (pixels is not null)
                {
                    bool placed = ViewModel.PlaceStamp(0, ViewModel.OverlayScale / 2,
                                                       ViewModel.OverlayScale / 2, pixels);
                    Diag.Log($"autostamp: placed={placed}");
                }
            }

            // DIAGNOSTIC (temporary): draws every built-in stamp to one PNG so
            // the result can be looked at rather than inferred from a green
            // build.
            string sheet = Environment.GetEnvironmentVariable("PDFEDITOR_STAMPSHEET") ?? "";
            if (sheet.Length > 0)
            {
                await StampRenderer.DumpContactSheet(
                    sheet, StampTheme.Default, System.Globalization.CultureInfo.CurrentCulture);
            }

            // Exercises the page-organising reload path without a mouse: a
            // reorder, a duplicate and a delete, logging page counts so a crash
            // or a wrong count shows up headlessly.
            if ((Environment.GetEnvironmentVariable("PDFEDITOR_AUTOREORDER") ?? "").Length > 0
                && ViewModel.PageCount >= 3)
            {
                int start = ViewModel.PageCount;
                bool moved = ViewModel.MovePage(0, 2);
                bool duped = ViewModel.DuplicatePage(0);
                int afterDup = ViewModel.PageCount;
                bool deleted = ViewModel.DeletePage(1);
                bool blank = ViewModel.InsertBlankPage(2);
                Diag.Log($"autoreorder: start={start} moved={moved} duped={duped} afterDup={afterDup} " +
                         $"deleted={deleted} blank={blank} end={ViewModel.PageCount}");
            }

            string zooms = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOZOOM") ?? "";
            if (zooms.Length > 0)
            {
                ScheduleAutoZoom(zooms);
            }

        };
        Unloaded += (_, _) =>
        {
            ViewModel.InkStrokeChanged -= OnInkStrokeChanged;
            ViewModel.InkStrokes.CollectionChanged -= OnInkStrokesCollectionChanged;
            ViewModel.Shapes.CollectionChanged -= OnInkStrokesCollectionChanged;
            ViewModel.LayoutRebuilt -= OnLayoutRebuilt;
            ViewModel.ViewRotated -= OnViewRotated;
            ViewModel.ScrollToPageRequested -= OnScrollToPageRequested;
            ViewModel.Dispose();
        };
    }

    /// <summary>
    /// Held in a field, not a local: a DispatcherQueueTimer that nothing
    /// references is collected before it ever ticks.
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoZoomTimer;

    /// <summary>
    /// Steps through a comma-separated list of zoom factors once the document
    /// has laid out, for PDFEDITOR_AUTOZOOM.
    ///
    /// A list rather than a single value because the interesting behaviour is
    /// in the TRANSITIONS: zooming in hands the page to the tile grid, and
    /// zooming back out has to hand it back and re-sharpen. A single zoom can
    /// only ever show half of that.
    ///
    /// Delays rather than events, because the state worth reading is after the
    /// debounced render pass has run, not merely after layout.
    /// </summary>
    private void ScheduleAutoZoom(string factors)
    {
        var steps = new Queue<float>();
        foreach (string part in factors.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture, out float f)
                && f > 0)
            {
                steps.Enqueue(f);
            }
        }

        if (steps.Count == 0)
        {
            return;
        }

        _autoZoomTimer = DispatcherQueue.CreateTimer();
        _autoZoomTimer.Interval = TimeSpan.FromSeconds(3);
        _autoZoomTimer.IsRepeating = true;
        _autoZoomTimer.Tick += (t, _) =>
        {
            if (steps.Count == 0)
            {
                t.Stop();
                return;
            }

            float target = steps.Dequeue();
            float clamped = (float)Math.Clamp(
                (double)target, (double)PageScroller.MinZoomFactor, (double)PageScroller.MaxZoomFactor);
            // Disarm auto-fit first, exactly as the zoom presets do. Leaving it
            // armed means the fit pass quietly puts the zoom straight back and
            // the harness silently measures the wrong thing.
            _autoFit = false;
            PageScroller.ZoomTo(clamped, null,
                new ScrollingZoomOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            Diag.Log($"autozoom: requested {target} clamped {clamped} " +
                     $"viewport={PageScroller.ViewportWidth:F0}x{PageScroller.ViewportHeight:F0}");
            ScheduleTileGeometryDump();
        };
        _autoZoomTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tileGeomTimer;

    private void ScheduleTileGeometryDump()
    {
        _tileGeomTimer ??= DispatcherQueue.CreateTimer();
        _tileGeomTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _tileGeomTimer.IsRepeating = false;
        _tileGeomTimer.Tick -= OnTileGeometryTick;
        _tileGeomTimer.Tick += OnTileGeometryTick;
        _tileGeomTimer.Start();
    }

    private void OnTileGeometryTick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object _)
    {
        t.Stop();
        DumpTileGeometry();
    }

    /// <summary>
    /// Logs the LAID OUT geometry of the tiles on screen.
    ///
    /// Tile counts only say that rendering happened. What decides whether the
    /// page looks right is the size and position XAML actually gave each tile,
    /// which is not necessarily the size and position it was asked for: layout
    /// rounding snaps elements to whole physical pixels, and a tile is a
    /// fraction of a DIP wide at deep zoom. This logs both so they can be
    /// compared instead of assumed.
    /// </summary>
    private void DumpTileGeometry()
    {
        int logged = 0;

        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count && logged < 5; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Image img && img.DataContext is ViewModels.PageTile tile)
                {
                    var bmp = img.Source as Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap;
                    Diag.Log(
                        $"tilegeom L{tile.Address.Level} ({tile.Address.Col},{tile.Address.Row}): " +
                        $"want {tile.Size:F4} at ({tile.Left:F4},{tile.Top:F4}) | " +
                        $"got {img.ActualWidth:F4}x{img.ActualHeight:F4} at " +
                        $"({img.Margin.Left:F4},{img.Margin.Top:F4}) | " +
                        $"bitmap {bmp?.PixelWidth ?? -1}px | rounding={img.UseLayoutRounding}");
                    logged++;
                }

                Walk(child);
            }
        }

        Walk(PageScroller);
        Diag.Log($"tilegeom: zoom={PageScroller.ZoomFactor:F2} " +
                 $"rasterScale={(XamlRoot is null ? 0 : XamlRoot.RasterizationScale):F2} " +
                 $"tilesFound={logged}");
    }

    /// <summary>
    /// Positions an overlay element at (left, top) within its Grid panel.
    /// Used from x:Bind in the overlay templates: unlike Canvas.Left/Top set
    /// inside a DataTemplate, this is compiled, so a mistake is a build error
    /// rather than a silently mispositioned rectangle.
    /// </summary>
    public static Thickness Offset(double left, double top) => new(left, top, 0, 0);

    /// <summary>Cyan for an unselected guide, accent-red for the selected
    /// one, bright yellow while a shape is snapping onto it. Snap wins over
    /// selection - a selected guide getting snapped to is a rare-but-real
    /// case and the snap indicator is the more useful feedback in that
    /// instant.</summary>
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush GuideBrushDefault =
        new(Windows.UI.Color.FromArgb(0xD8, 0x00, 0xA0, 0xD8));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush GuideBrushSelected =
        new(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x1B, 0x3B));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush GuideBrushSnapActive =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC0, 0x00));
    public static Microsoft.UI.Xaml.Media.Brush GuideFill(bool selected, bool snapActive) =>
        snapActive ? GuideBrushSnapActive
            : (selected ? GuideBrushSelected : GuideBrushDefault);

    /// <summary>Smart alignment guide brush - bright orange, distinct from
    /// user guides (cyan) and the snap-flash (yellow) so it reads as a
    /// SUGGESTION, not a placed thing. Only shown mid-drag.</summary>
    public static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SmartGuideBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x60, 0x00));

    /// <summary>Visibility helpers for the smart-guide overlays: collapse when
    /// the axis's smart guide is null (no alignment engaged on that axis),
    /// visible when it has a value.</summary>
    public static Visibility SmartGuideVis(double? v) =>
        v.HasValue ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pixel-space Margin for a VERTICAL smart-guide overlay: X only.
    /// x:Bind cannot pass literal booleans as method arguments, so the two
    /// axes are two separate helpers.</summary>
    public static Thickness SmartGuideMarginX(double? v) =>
        !v.HasValue ? new Thickness(0)
                    : new Thickness(v.Value * SlotLayoutWidth, 0, 0, 0);
    public static Thickness SmartGuideMarginY(double? v) =>
        !v.HasValue ? new Thickness(0)
                    : new Thickness(0, v.Value * SlotLayoutWidth, 0, 0);
    private const double SlotLayoutWidth = 800;

    /// <summary>
    /// Positions an element from a NORMALIZED coordinate, multiplying by the
    /// slot scale. For overlays that must not sit inside the scaled layer,
    /// because they have an intrinsic size the transform would magnify.
    /// </summary>
    public static Thickness ScaledOffset(double x, double y, double scale) =>
        new(x * scale, y * scale, 0, 0);

    /// <summary>
    /// Turns a stored "#AARRGGBB" into a brush, for x:Bind in the annotation
    /// templates. Compiled like <see cref="Offset"/>, so a mistake is a build
    /// error rather than a silently invisible mark.
    /// </summary>
    public static Brush HexBrush(string hex)
    {
        var (a, r, g, b) = InkPresets.ParseHex(hex);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    /// <summary>
    /// The rail icon for a tool: a <see cref="FontIcon"/> for the glyph tools,
    /// or a scaled <see cref="PathIcon"/> for the few that carry an SVG path.
    ///
    /// Both are IconElements, so either inherits the rail's foreground and
    /// follows the theme without extra work. The Path is wrapped in a Viewbox
    /// because its 32x32 authoring box has to come down to the 16px the glyphs
    /// occupy.
    /// </summary>
    public static UIElement ToolIcon(ToolDefinition tool)
    {
        if (!string.IsNullOrEmpty(tool.PathData))
        {
            return new Viewbox { Width = 16, Height = 16, Child = BuildPath(tool.PathData) };
        }

        return new FontIcon { Glyph = tool.Glyph, FontSize = 16 };
    }

    /// <summary>
    /// A filled <see cref="Microsoft.UI.Xaml.Shapes.Path"/> from SVG path
    /// mini-language.
    ///
    /// There is no public parser in WinUI, so this loads a whole Path element
    /// through XamlReader, whose Data property carries the type converter that
    /// understands the mini-language. The parsed Path is returned AS THE
    /// ELEMENT, not just its Data: handing that Data to a separate PathIcon
    /// threw "value does not fall within the expected range", because a
    /// geometry cannot belong to two elements.
    ///
    /// A Path does not inherit foreground the way an IconElement does, so the
    /// fill is set from the theme brush. It does not track a later theme toggle,
    /// which for a 16px rail icon is not worth a live binding.
    /// </summary>
    private static Microsoft.UI.Xaml.Shapes.Path BuildPath(string data)
    {
        const string ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // Fill declared as a ThemeResource IN the markup rather than assigned
        // afterwards. An assigned brush is resolved once and keeps the colour
        // the app happened to start in, which left the hand and stamp icons
        // black on every dark theme while the font-glyph tools beside them
        // followed along. A ThemeResource reference re-evaluates when the
        // element's theme changes, which is what themes now require.
        var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Path xmlns=\"{ns}\" Data=\"{data}\" " +
            "Fill=\"{ThemeResource TextFillColorPrimaryBrush}\" Stretch=\"Uniform\" />");
        return path;
    }

    // ---------------- ScrollView-driven zoom ----------------

    /// <summary>
    /// How the viewport is currently auto-fitting. Fit-page is the default,
    /// because a page that spans the viewport exactly reads as a region of
    /// the window rather than a sheet on a canvas.
    /// </summary>
    private enum FitMode { Page, Width }

    private FitMode _fitMode = FitMode.Page;

    /// <summary>
    /// Applies the current fit mode.
    ///
    /// Pages are laid out at a FIXED slot width, independent of the window, so
    /// fitting is purely a zoom decision and nothing the renderer does can
    /// move it. A window resize therefore never rebuilds the layout or
    /// disturbs the scroll position.
    /// </summary>
    private void FitToWidth(bool animate = false)
    {
        double available = AvailableContentWidth;
        double availableHeight = PageScroller.ViewportHeight - ViewportHost.Padding.Top - ViewportHost.Padding.Bottom;
        if (available <= 0)
        {
            return;
        }

        double target = _fitMode == FitMode.Width
            ? ViewModel.FitWidthZoom(available)
            : ViewModel.FitPageZoom(available, availableHeight);

        float zoom = (float)Math.Clamp(
            target,
            PageScroller.MinZoomFactor,
            PageScroller.MaxZoomFactor);

        PageScroller.ZoomTo(
            zoom,
            null,
            new ScrollingZoomOptions(animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled,
                                     ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>
    /// True while the viewport should keep tracking fit-width, i.e. the user
    /// has not taken the zoom over.
    ///
    /// This is what fixes the startup thrash. The ScrollView reports a zero
    /// viewport width and then grows as layout settles, so a one-shot fit
    /// either runs too early (fitting an intermediate width and leaving the
    /// page too small) or runs repeatedly with a different zoom each time.
    /// Staying in fit mode means the zoom simply follows the window until the
    /// user changes it; the debounced sharpening pass and its stale-result
    /// check then collapse all those intermediate zooms into a single
    /// rasterization at the final size. It also makes resizing behave the way
    /// a document viewer should.
    /// </summary>
    private bool _autoFit = true;

    private void OnLayoutRebuilt()
    {
        _autoFit = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            // This is the only moment a position can be restored: the pages now
            // have sizes, so there is somewhere to scroll TO.
            //
            // Guarded on the PATH rather than run every time, because this also
            // fires when a page is inserted, deleted or reordered, and yanking
            // the view back to where reading was left in the middle of editing
            // would be worse than never restoring at all.
            bool firstForThisFile =
                ViewModel.DocumentPath is { Length: > 0 } path
                && !string.Equals(path, _restoredForPath, StringComparison.OrdinalIgnoreCase);

            if (firstForThisFile)
            {
                _restoredForPath = ViewModel.DocumentPath;

                // History belongs to a document. Carrying it across would offer
                // to go "back" into a file that is no longer open.
                _navHere = new NavigationPoint(ViewModel.CurrentPageIndex, 0);
                _navigation.Reset(_navHere);
                SyncBarNavState();
            }

            // A remembered place wins. Failing that, the SAVED DEFAULT VIEW
            // decides, which it did not before: this called FitToWidth outright,
            // so Fit page and Actual size were only ever applied at the moment
            // they were chosen in the settings dialog and never on opening a
            // document.
            if (!firstForThisFile || !RestoreReadingPosition())
            {
                ApplyDefaultView();
            }

            PushVisibleWindow();
        });
    }

    /// <summary>
    /// The file whose position has already been restored in this page.
    ///
    /// Reopening the same document in the same tab has to restore again, so
    /// this is cleared when a document closes rather than kept for the life of
    /// the page.
    /// </summary>
    private string? _restoredForPath;

    /// <summary>Available content width in DIPs, excluding the canvas padding.</summary>
    private double AvailableContentWidth =>
        PageScroller.ViewportWidth - ViewportHost.Padding.Left - ViewportHost.Padding.Right;

    /// <summary>
    /// Where the reader has been in this document, for Alt+Left and Alt+Right.
    ///
    /// Lives here rather than on the view model because a place is a page AND
    /// how far down it, and only the scroller knows the second half.
    /// </summary>
    private readonly NavigationHistory _navigation = new();

    /// <summary>
    /// Where the reader is, kept up to date as they scroll.
    ///
    /// Tracked rather than measured at the moment of a jump: GoToPage sets the
    /// current page BEFORE asking for the scroll, so by the time this hears
    /// about a jump the "from" page is already gone.
    /// </summary>
    private NavigationPoint _navHere;

    /// <summary>True while Alt+Left or Alt+Right is doing the moving, so the
    /// move it performs is not recorded as a new jump.</summary>
    private bool _navigatingHistory;

    private void OnScrollToPageRequested(int pageIndex, bool animate, PageBand? reveal)
    {
        // Recorded here, which is the choke point every jump passes through:
        // bookmarks, the page box, thumbnails, search matches and Home/End all
        // reach the viewport this way.
        if (!_navigatingHistory && NavigationHistory.IsWorthRecording(_navHere.PageIndex, pageIndex))
        {
            _navigation.Record(_navHere, new NavigationPoint(pageIndex, 0));
            Diag.Log($"nav: recorded jump {_navHere.PageIndex} -> {pageIndex}");
            SyncBarNavState();
        }

        DispatcherQueue.TryEnqueue(() => ScrollToPage(pageIndex, animate, reveal));
    }

    private void GoBackInHistory() => ApplyHistoryPoint(_navigation.Back(), "back");

    private void GoForwardInHistory() => ApplyHistoryPoint(_navigation.Forward(), "forward");

    /// <summary>
    /// Moves to a remembered place without recording the move.
    ///
    /// Scrolls directly rather than through GoToPage, because the page's
    /// FRACTION is the point: landing at the top of page 1500 is not where the
    /// reader was, and after a long book that is the difference between
    /// getting back and starting to look again.
    /// </summary>
    private void ApplyHistoryPoint(NavigationPoint? point, string direction)
    {
        if (point is not { } target)
        {
            Diag.Log($"nav: nothing {direction} of here");
            return;
        }

        // The cursor has already moved, so what back and forward can do next
        // has changed even though the scrolling below has not happened yet.
        SyncBarNavState();

        _navigatingHistory = true;
        try
        {
            ViewModel.GoToPage(target.PageIndex, animate: false);

            DispatcherQueue.TryEnqueue(() =>
            {
                double slotY = ViewModel.SlotTopOf(target.PageIndex)
                             + (target.PageFraction * ViewModel.SlotHeightOf(target.PageIndex));

                PageScroller.ScrollTo(
                    PageScroller.HorizontalOffset,
                    (slotY + ViewportHost.Padding.Top) * PageScroller.ZoomFactor,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled,
                                               ScrollingSnapPointsMode.Ignore));

                _navHere = target;
                _navigatingHistory = false;
            });

            Diag.Log($"nav: {direction} to page {target.PageIndex} +{target.PageFraction:F2}");
        }
        catch
        {
            _navigatingHistory = false;
            throw;
        }
    }

    /// <summary>
    /// Brings a page to the top of the viewport, animated over short distances.
    ///
    /// The page top is in SLOT space (unzoomed DIPs), and the ScrollView wants
    /// a zoomed offset, so it is multiplied by the zoom factor. The host's top
    /// padding is part of the content, so it is included; a small lead-in is
    /// subtracted so the page does not sit flush against the top edge, which
    /// looks like it has been cut off rather than scrolled to.
    ///
    /// Whether it glides or jumps is decided by DISTANCE, not by the caller
    /// alone. An animated scroll's duration grows with how far it travels and
    /// every frame is rendered, so a bookmark 1500 pages away took nearly half
    /// a minute to arrive at. See <see cref="ScrollAnimation"/>.
    ///
    /// <paramref name="reveal"/> names a band inside the page that has to end
    /// up on screen, which is how a search match is navigated to: the page top
    /// is the right answer for a bookmark and the wrong one for a hit two
    /// thirds of the way down. When the band is already comfortably in view the
    /// scroll is skipped entirely, so stepping between two matches on the same
    /// screen does not jolt the page.
    /// </summary>
    private void ScrollToPage(int pageIndex, bool animate, PageBand? reveal = null)
    {
        double slotTop = ViewModel.SlotTopOf(pageIndex);
        double zoom = PageScroller.ZoomFactor;

        const double LeadIn = 12;
        double target = (slotTop + ViewportHost.Padding.Top) * zoom - LeadIn;
        target = Math.Max(0, target);

        if (reveal is { } band)
        {
            // Into slot space, where the band is measured, and back out again.
            // Content Y is (slotY + padding) * zoom, so slotY is its inverse.
            double viewTopSlot = (PageScroller.VerticalOffset / zoom) - ViewportHost.Padding.Top;
            double viewportSlotHeight = PageScroller.ViewportHeight / zoom;

            double? revealed = MatchReveal.OffsetFor(
                slotTop + band.Top, slotTop + band.Bottom, viewTopSlot, viewportSlotHeight);

            if (revealed is null)
            {
                Diag.Log($"scrollToPage {pageIndex}: match already in view, not scrolling");
                return;
            }

            target = Math.Max(0, (revealed.Value + ViewportHost.Padding.Top) * zoom);
        }

        double from = PageScroller.VerticalOffset;
        bool glide = ScrollAnimation.ShouldAnimate(from, target, PageScroller.ViewportHeight, animate);

        Diag.Log(
            $"scrollToPage {pageIndex}: from {from:F0} to {target:F0} " +
            $"asked={animate} glide={glide} viewport={PageScroller.ViewportHeight:F0}");

        PageScroller.ScrollTo(
            PageScroller.HorizontalOffset,
            target,
            new ScrollingScrollOptions(
                glide ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled,
                ScrollingSnapPointsMode.Ignore));
    }

    private void PageScroller_ViewChanged(ScrollView sender, object args)
    {
        UpdateZoomReadout();

        // The Skia layer is outside the scroller, so nothing moves it for free:
        // scroll and zoom reach it only by being read back and repainted. A
        // no-op while its flag is off. Added as another consumer of a handler
        // that already exists; the scrolling logic above and below is untouched.
        RefreshSkiaShapeLayer();

        // Debounced, because this fires continuously through a pan and the
        // save writes the settings file.
        QueueReadingPositionSave();

        // Undebounced, because it is two field writes and no I/O, and because
        // the value has to be right at the instant a jump happens rather than
        // 400ms later. Keeps Back returning to where the reader actually got
        // to, not where they first landed.
        if (!_navigatingHistory && CurrentReadingPosition() is { } here)
        {
            _navHere = new NavigationPoint(here.PageIndex, here.PageFraction);
            _navigation.NoteCurrent(_navHere);
        }

        // Anchored to a page position, so every scroll and zoom moves it.
        UpdateObjectToolbar();

        // A zoom that no longer matches fit-width means the user took over,
        // by pinch, Ctrl+wheel or a zoom command. Detecting it from the state
        // rather than from each input path means no gesture can be forgotten.
        if (_autoFit && AvailableContentWidth > 0)
        {
            double availableHeight = PageScroller.ViewportHeight - ViewportHost.Padding.Top - ViewportHost.Padding.Bottom;
            double fit = _fitMode == FitMode.Width
                ? ViewModel.FitWidthZoom(AvailableContentWidth)
                : ViewModel.FitPageZoom(AvailableContentWidth, availableHeight);

            if (Math.Abs(PageScroller.ZoomFactor - fit) > 0.005)
            {
                _autoFit = false;
            }
        }

        PushVisibleWindow();
        RedrawRulers();
    }

    /// <summary>
    /// Hands the current scroll position to the view model, which decides
    /// which pages to render and which bitmaps to release. Offsets go across
    /// in zoomed pixels together with the zoom factor; the view model divides
    /// them back into slot space, so that mapping lives in exactly one place.
    /// </summary>
    private void PushVisibleWindow() =>
        ViewModel.UpdateVisibleWindow(
            PageScroller.VerticalOffset,
            PageScroller.ViewportHeight,
            PageScroller.ZoomFactor,
            // Horizontal too: at deep zoom the page is wider than the window,
            // so which COLUMN is on screen decides what needs rendering.
            PageScroller.HorizontalOffset,
            PageScroller.ViewportWidth);

    private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (XamlRoot is not null)
        {
            ViewModel.RasterizationScale = XamlRoot.RasterizationScale;
        }

        ResizeStampStrip();

        if (_autoFit)
        {
            FitToWidth();
        }

        UpdateZoomReadout();
        PushVisibleWindow();
        RedrawRulers();

        // The bar lives in this column, so its room is whatever the canvas has
        // left after the panels either side of it.
        ApplyBarOverflow();
    }

    // ---------------- Rulers ----------------
    //
    // Rulers show the current page's coordinates in PDF POINTS (1/72 inch)
    // along the top and left edges of the viewport, PageMaker style. Ticks
    // adapt to zoom - the tick pitch is chosen so majors fall roughly every
    // 60-100 screen DIPs no matter the zoom factor. Origin is the first page's
    // top-left in the ScrollView content, which is what a user placing a
    // shape on page 1 will expect. (Multi-page origin per current visible
    // page can come later; for now the ruler numbers keep counting through
    // page boundaries, which still gives useful monotonic X and Y.)

    /// <summary>Units the ruler can display. All are converted from PDF points
    /// (1/72 inch) via a per-unit ratio. Default is Inches - Acrobat's default
    /// and what most non-typographers reach for on a Letter-size page.</summary>
    private enum RulerUnit { Points, Picas, Millimeters, Centimeters, Inches }
    private RulerUnit _rulerUnit = RulerUnit.Inches;

    // Group/Ungroup from the menu. The keyboard route is a case in
    // RootGrid_KeyDown, NOT the MenuFlyoutItem's accelerator: that accelerator
    // is only live while the flyout is open. See the Ctrl+O/Ctrl+S block there.
    private void Group_Click(object sender, RoutedEventArgs e) => ViewModel.GroupSelected();

    private void Ungroup_Click(object sender, RoutedEventArgs e) => ViewModel.UngroupSelected();

    // ---------------- Contextual object toolbar ----------------

    /// <summary>True while a pointer drag is in flight. The toolbar hides for
    /// the duration: it is anchored to the selection, so during a drag it would
    /// chase the object around under the user's cursor.</summary>
    private bool _objectToolbarSuppressed;

    private void ObjToolGroup_Click(object sender, RoutedEventArgs e) => ViewModel.GroupSelected();

    private void ObjToolUngroup_Click(object sender, RoutedEventArgs e) => ViewModel.UngroupSelected();

    private void ObjToolFront_Click(object sender, RoutedEventArgs e) => ViewModel.BringSelectedToFront();

    private void ObjToolBack_Click(object sender, RoutedEventArgs e) => ViewModel.SendSelectedToBack();

    private void ObjToolForward_Click(object sender, RoutedEventArgs e) => ViewModel.BringSelectedForward();

    private void ObjToolBackward_Click(object sender, RoutedEventArgs e) => ViewModel.SendSelectedBackward();

    private void ObjToolDelete_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteSelectedAnnotation();

    /// <summary>The toolbar's width is only known once it has been measured, and
    /// it changes when a button's label does. Re-place it whenever that
    /// happens, or it stays centred on its previous width.</summary>
    private void ObjectToolbar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateObjectToolbar();

    /// <summary>
    /// Puts the floating toolbar over the current selection, or hides it.
    ///
    /// Coordinates travel: normalized annotation units -> page-local slot DIPs
    /// (the view model's job) -> ScrollView viewport DIPs via TransformToVisual,
    /// which is what applies the zoom and the scroll offset and the horizontal
    /// centring of a page narrower than the viewport. Doing that last step by
    /// hand is what put the rulers on page 0's origin instead of the current
    /// page's, so let the transform do it.
    /// </summary>
    private void UpdateObjectToolbar()
    {
        // Fires from a view-model event that can outlive the page during
        // teardown, and from SizeChanged before the field is assigned.
        if (ObjectToolbar is null || ViewportHost is null || PageScroller is null) { return; }

        if (_objectToolbarSuppressed
            || !ViewModel.TryGetSelectionBox(out int page, out double l, out double t, out double r, out double b))
        {
            ObjectToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        double padL = ViewportHost.Padding.Left;
        double padT = ViewportHost.Padding.Top;
        double slotTop = ViewModel.SlotTopOf(page);

        Windows.Foundation.Point tl, br;
        try
        {
            var toViewport = ViewportHost.TransformToVisual(PageScroller);
            tl = toViewport.TransformPoint(new Windows.Foundation.Point(padL + l, padT + slotTop + t));
            br = toViewport.TransformPoint(new Windows.Foundation.Point(padL + r, padT + slotTop + b));
        }
        catch
        {
            // TransformToVisual throws while the tree is being rebuilt. The next
            // ViewChanged or selection change repositions, so a skipped frame
            // here is invisible.
            return;
        }

        double barW = ObjectToolbar.ActualWidth;
        double barH = ObjectToolbar.ActualHeight;
        if (barW <= 0 || barH <= 0)
        {
            // Never shown yet, so it has no measured size and nowhere correct to
            // go. Make it visible but fully transparent; the SizeChanged that
            // follows re-enters here with real numbers and fades it in. Showing
            // it opaque now would flash the buttons at the viewport corner.
            ObjectToolbar.Opacity = 0;
            ObjectToolbar.Visibility = Visibility.Visible;
            return;
        }

        // The property bar sits at the top of this same column and draws ABOVE
        // this toolbar, so anything placed under it is unreachable. Reserve its
        // height, measured rather than assumed, since it is one or two rows.
        double topInset = PropertyBar.Visibility == Visibility.Visible
            ? PropertyBar.ActualHeight + PropertyBar.Margin.Top - PageScroller.Margin.Top
            : 0;

        var place = ObjectToolbarPlacement.Place(
            tl.X, tl.Y, br.X, br.Y,
            barW, barH,
            PageScroller.ViewportWidth, PageScroller.ViewportHeight,
            Math.Max(0, topInset));

        // Position AND elevation through the one property.
        //
        // This used to move the toolbar with a RenderTransform, which drives the
        // same composition visual as Translation and overrides it, Z included.
        // The toolbar therefore sat at ground level however much elevation it
        // was given, and was drawn behind every page it floated over: the first
        // attempt at this fix set Translation in the XAML and appeared to change
        // nothing at all, because the transform was quietly winning.
        ObjectToolbar.Translation = new System.Numerics.Vector3(
            (float)place.Left, (float)place.Top, ObjectToolbarPlacement.Elevation);

        ObjToolGroup.IsEnabled = ViewModel.CanGroupSelection;
        ObjToolUngroup.IsEnabled = ViewModel.CanUngroupSelection;

        ObjectToolbar.Opacity = 1;
        ObjectToolbar.Visibility = Visibility.Visible;
    }

    private void RulersToggle_Click(object sender, RoutedEventArgs e)
    {
        // The bar's copy of this toggle and the menu's are kept in step by
        // assignment, and assigning IsChecked raises Click. Without the guard,
        // showing the current state would toggle it.
        if (_applyingSettings)
        {
            return;
        }

        // Whichever was clicked, both agree afterwards.
        bool on = sender == BarRulersToggle ? BarRulersToggle.IsChecked : RulersToggle.IsChecked;
        _applyingSettings = true;
        try
        {
            RulersToggle.IsChecked = on;
            BarRulersToggle.IsChecked = on;
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplyRulerVisibility();
    }

    /// <summary>
    /// Rulers show when they are switched on AND there is something to measure.
    ///
    /// Two conditions, one place. The toggle is the user's preference and
    /// survives a document being closed; whether a ruler is on screen right now
    /// also depends on whether a page exists to rule.
    /// </summary>
    // ---------------- Full screen ----------------

    /// <summary>
    /// Turns night reading on or off, and remembers the choice.
    ///
    /// The view model does the re-rendering; this only records the decision, so
    /// the menu, the setting and what is on screen cannot drift apart.
    /// </summary>
    private void NightModeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
        {
            return;
        }

        // Whichever of the three said so, they all agree afterwards. The bar's
        // own button mirrors into the menu item before calling this; the
        // flyout copy is read from the sender, the way the rulers toggle does.
        bool on = sender == BarNightModeItem ? BarNightModeItem.IsChecked : NightModeToggle.IsChecked;

        ViewModel.IsNightMode = on;
        ApplyPageSheet(on);
        SettingsStore.Update(s => s with { NightMode = on });

        _applyingSettings = true;
        try
        {
            NightModeToggle.IsChecked = on;
        }
        finally
        {
            _applyingSettings = false;
        }

        SyncBarViewState();
    }

    /// <summary>
    /// Repaints the sheet every page card is drawn on.
    ///
    /// The card is not merely a backdrop for an opaque bitmap: a slot shows it
    /// bare while its render is in flight, and after a toggle every slot is in
    /// exactly that state at once. Left white it produced the reported fault,
    /// a white page under a dark theme.
    ///
    /// NightMode.Floor rather than a colour picked here, so the sheet is the
    /// same value a white page transforms to and the card cannot show as a rim
    /// of a different shade around the bitmap.
    /// </summary>
    private void ApplyPageSheet(bool night)
    {
        if (Resources["PageSheetBrush"] is SolidColorBrush sheet)
        {
            byte v = NightMode.Floor;
            sheet.Color = night ? Color.FromArgb(255, v, v, v) : Colors.White;
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow window)
        {
            window.ToggleFullScreen();
        }
    }

    private DateTime _lastPageTurnUtc = DateTime.MinValue;

    /// <summary>
    /// Turns the page when the wheel reaches the edge of one, in single-page
    /// view.
    ///
    /// Registered with handledEventsToo, because the ScrollView marks the wheel
    /// handled before this bubbles: without that flag this never runs and the
    /// reader stays stuck, which is exactly how the mode shipped.
    ///
    /// It is only ever a page turn at the edge. Anywhere else the event is left
    /// alone and the scroller scrolls, so a page taller than the window still
    /// reads normally.
    /// </summary>
    private void PageScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsSinglePageView || ViewModel.PageCount == 0)
        {
            return;
        }

        // Ctrl+wheel is zoom, which the scroller does itself. Claiming it here
        // would turn the page every time someone zoomed at the bottom of one.
        if (_isCtrlDown)
        {
            return;
        }

        double scrollable = PageScroller.ExtentHeight * PageScroller.ZoomFactor - PageScroller.ViewportHeight;
        int delta = e.GetCurrentPoint(PageScroller).Properties.MouseWheelDelta;

        var step = SinglePageScroll.Resolve(delta, PageScroller.VerticalOffset, scrollable);
        if (step == PageStep.Scroll)
        {
            return;
        }

        // A flick is several notches, and each one arrives as its own event.
        if (!SinglePageScroll.MayTurn(DateTime.UtcNow - _lastPageTurnUtc))
        {
            e.Handled = true;
            return;
        }

        int target = ViewModel.CurrentPageIndex + (step == PageStep.Next ? 1 : -1);
        if (target < 0 || target >= ViewModel.PageCount)
        {
            // The ends of the document. Left unhandled so the scroller can do
            // its usual bounce, which is the feedback that says "no more".
            return;
        }

        _lastPageTurnUtc = DateTime.UtcNow;
        _pendingLanding = step;
        ViewModel.GoToPage(target, animate: false);
        e.Handled = true;
    }

    /// <summary>
    /// Which end of the newly laid-out page to land on, or null.
    ///
    /// Held across the turn because the page's height is not known until its
    /// layout has been rebuilt, and landing at the bottom needs that height.
    /// </summary>
    private PageStep? _pendingLanding;

    /// <summary>
    /// Puts the view at the right end of a page just turned to.
    ///
    /// Queued behind the layout rebuild for the same reason the rotation
    /// restore is: the new page's height does not exist until then.
    /// </summary>
    private void ApplyPendingLanding()
    {
        if (_pendingLanding is not { } step)
        {
            return;
        }

        _pendingLanding = null;

        DispatcherQueue.TryEnqueue(() =>
        {
            double scrollable = PageScroller.ExtentHeight * PageScroller.ZoomFactor - PageScroller.ViewportHeight;
            PageScroller.ScrollTo(
                PageScroller.HorizontalOffset,
                SinglePageScroll.LandingOffset(step, scrollable),
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        });
    }

    private void RotateViewCw_Click(object sender, RoutedEventArgs e) => ViewModel.RotateViewClockwise();

    private void RotateViewCcw_Click(object sender, RoutedEventArgs e) => ViewModel.RotateViewCounterClockwise();

    private void ResetViewRotation_Click(object sender, RoutedEventArgs e) => ViewModel.ResetViewRotation();

    // ---------------- The floating bar's view controls ----------------

    private void PrevPage_Click(object sender, RoutedEventArgs e) =>
        ViewModel.GoToPage(ViewModel.CurrentPageIndex - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) =>
        ViewModel.GoToPage(ViewModel.CurrentPageIndex + 1);

    private void NavBack_Click(object sender, RoutedEventArgs e) => GoBackInHistory();

    private void NavForward_Click(object sender, RoutedEventArgs e) => GoForwardInHistory();

    /// <summary>
    /// Greys out back and forward when there is nowhere to go.
    ///
    /// Separate from <see cref="SyncBarViewState"/> because it runs on every
    /// jump rather than on a settings change, and because none of it can
    /// re-enter a Click handler: an IsEnabled assignment raises nothing.
    /// </summary>
    private void SyncBarNavState()
    {
        NavBackButton.IsEnabled = _navigation.CanGoBack;
        NavForwardButton.IsEnabled = _navigation.CanGoForward;
        BarNavBackItem.IsEnabled = _navigation.CanGoBack;
        BarNavForwardItem.IsEnabled = _navigation.CanGoForward;
    }

    private void PageModeBar_Click(object sender, RoutedEventArgs e) =>
        ApplyPageViewMode(ViewModel.IsSinglePageView
            ? PageViewMode.Continuous
            : PageViewMode.SinglePage);

    private void NightModeBar_Click(object sender, RoutedEventArgs e)
    {
        // Routed through the same handler the menu uses, by making the menu
        // item agree first. One path, so the two can never disagree about what
        // night mode currently is.
        NightModeToggle.IsChecked = NightModeBarButton.IsChecked == true;
        NightModeToggle_Click(sender, e);
    }

    private void RotateBar_Click(object sender, RoutedEventArgs e)
    {
        // A ToggleButton, but not a toggle: it turns the view a quarter each
        // press and four presses come back round. The checked state is being
        // used to SHOW that the view is turned, which is the one thing nothing
        // else tells you, since rotation is session-only and resets on reopen.
        ViewModel.RotateViewClockwise();
        SyncBarViewState();
    }

    private void FindToggle_Click(object sender, RoutedEventArgs e) => SetFindOpen(!IsFindOpen);

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        SetFindOpen(false);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private bool IsFindOpen => FindPanel.Visibility == Visibility.Visible;

    /// <summary>
    /// Shows or hides the find controls on the bar.
    ///
    /// Collapsed by default: they held about 120 DIP of a bar that floats over
    /// the page, for something done in bursts rather than continuously. The
    /// query is left alone when it closes, so reopening resumes the same search
    /// rather than starting from nothing.
    /// </summary>
    private void SetFindOpen(bool open)
    {
        FindPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FindToggleButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

        if (open)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }

        // Find is the single widest thing on the bar, so opening it is the
        // likeliest moment for the bar to stop fitting. Queued, because the new
        // widths are not known until this layout pass has run.
        DispatcherQueue.TryEnqueue(ApplyBarOverflow);
    }

    // ---------------- Fitting the bar into the canvas ----------------

    /// <summary>The bar's own StackPanel spacing, and the margin it keeps from
    /// each edge of the canvas. Both are declared in the XAML.</summary>
    private const double BarSpacing = 4;
    private const double BarSideMargins = 32;

    /// <summary>
    /// Natural widths, each recorded while its group was on screen.
    ///
    /// Never measured while hidden. A hidden group is zero wide, and deciding
    /// from that would find that the bar now fits, put the group back, find
    /// that it does not fit, and take it away again, forever.
    /// </summary>
    private double _navGroupWidth, _viewGroupWidth, _findExtraWidth, _barCoreWidth;

    /// <summary>
    /// Drops as much of the bar as it takes to fit the canvas, and puts it back
    /// when there is room again. See <see cref="StatusBarOverflow"/> for what
    /// goes first and why.
    /// </summary>
    private void ApplyBarOverflow()
    {
        if (StatusContent.ActualWidth <= 0 || PageScroller.ActualWidth <= 0)
        {
            return;
        }

        bool navShown = NavHistoryGroup.Visibility == Visibility.Visible;
        bool viewShown = ViewModesGroup.Visibility == Visibility.Visible;

        if (navShown && NavHistoryGroup.ActualWidth > 0)
        {
            _navGroupWidth = NavHistoryGroup.ActualWidth + BarSpacing;
        }

        if (viewShown && ViewModesGroup.ActualWidth > 0)
        {
            _viewGroupWidth = ViewModesGroup.ActualWidth + BarSpacing;
        }

        if (IsFindOpen && FindPanel.ActualWidth > 0)
        {
            // What find COSTS, which is its own width less the glass it
            // replaces, since the glass is already counted in the core.
            _findExtraWidth = FindPanel.ActualWidth - FindToggleButton.Width;
        }

        // Everything the bar is made of besides the two droppable groups,
        // recorded only when both are present so the subtraction is honest.
        if (navShown && viewShown && !IsFindOpen)
        {
            _barCoreWidth = StatusContent.ActualWidth - _navGroupWidth - _viewGroupWidth;
        }

        if (_barCoreWidth <= 0)
        {
            return;
        }

        double natural = _barCoreWidth + _navGroupWidth + _viewGroupWidth
                       + (IsFindOpen ? _findExtraWidth : 0);

        var fit = StatusBarOverflow.Decide(
            PageScroller.ActualWidth - BarSideMargins, natural, _viewGroupWidth, _navGroupWidth);

        ViewModesGroup.Visibility = fit.ViewModes ? Visibility.Visible : Visibility.Collapsed;
        NavHistoryGroup.Visibility = fit.NavHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Brings the bar's toggles into line with what the viewport is actually
    /// doing.
    ///
    /// Guarded like the menu, because setting IsChecked raises Click and that
    /// handler acts: without the guard, showing the current state would change
    /// it. That trap has already cost this app one bug.
    /// </summary>
    private void SyncBarViewState()
    {
        _applyingSettings = true;
        try
        {
            NightModeBarButton.IsChecked = ViewModel.IsNightMode;
            BarNightModeItem.IsChecked = ViewModel.IsNightMode;
            RotateBarButton.IsChecked = ViewModel.IsViewRotated;
            BarResetRotationItem.IsEnabled = ViewModel.IsViewRotated;
            BarSinglePageItem.IsChecked = ViewModel.IsSinglePageView;
            BarContinuousItem.IsChecked = !ViewModel.IsSinglePageView;
            BarRulersToggle.IsChecked = RulersToggle.IsChecked;

            // A stack of sheets while the document scrolls through, one sheet
            // when it turns a page at a time. The tooltip names the OTHER mode,
            // because that is what pressing it gets you.
            PageModeBarIcon.Glyph = ViewModel.IsSinglePageView ? "\uE7C3" : "\uE81E";
            ToolTipService.SetToolTip(
                PageModeBarButton,
                ViewModel.IsSinglePageView
                    ? "Single page. Click for continuous scrolling"
                    : "Continuous scrolling. Click for single page");
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ContinuousView_Click(object sender, RoutedEventArgs e) =>
        ApplyPageViewMode(PageViewMode.Continuous);

    private void SinglePageView_Click(object sender, RoutedEventArgs e) =>
        ApplyPageViewMode(PageViewMode.SinglePage);

    /// <summary>
    /// True while the menu is being brought into line with the saved settings.
    ///
    /// Ticking a menu item in code raises its Click, so restoring a setting ran
    /// the handler that SAVES it, and the last item ticked won. Restoring
    /// "single page" therefore wrote "continuous" straight back over it: the
    /// app came up in the right mode with the wrong setting on disk, and the
    /// next thing to re-apply settings flipped the mode. The existing rulers
    /// toggle only avoided this by never assigning an unchanged value.
    /// </summary>
    private bool _applyingSettings;

    private void ApplyPageViewMode(PageViewMode mode)
    {
        if (_applyingSettings)
        {
            return;
        }

        ViewModel.SetPageViewMode(mode);
        SettingsStore.Update(s => s with { PageViewMode = mode });
        SyncBarViewState();
    }

    /// <summary>
    /// Puts the reader back on the page they were reading after a view
    /// rotation, and keeps the reset item in step.
    ///
    /// Queued rather than run straight away. Rebuilding the layout has already
    /// queued its own continuation, which decides the zoom for the new page
    /// shape; scrolling before that runs would be undone by it. The queue is in
    /// order, so being second here means running second.
    /// </summary>
    private void OnViewRotated(int page)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ResetViewRotationItem.IsEnabled = ViewModel.IsViewRotated;
            SyncBarViewState();
            ScrollToPage(page, animate: false);
            PushVisibleWindow();

            // A page turned to by the wheel lands at the end the reader was
            // heading towards, which needs the new page's height and so cannot
            // happen until the layout has been rebuilt.
            ApplyPendingLanding();
        });
    }


    /// <summary>
    /// What this page was showing before it went full screen, or null when it
    /// is not presenting.
    ///
    /// Captured rather than assumed, so leaving puts back exactly what was
    /// there: restoring a fixed set would turn the rulers on for someone who
    /// had them off and reopen a panel they had closed.
    /// </summary>
    private ChromeState? _chromeBeforePresenting;

    public bool IsPresenting => _chromeBeforePresenting is not null;

    /// <summary>
    /// Strips the page down to the document, or puts the chrome back.
    ///
    /// The window hides its own title strip and tabs; this handles everything
    /// that belongs to the page. Reading is the whole point of the mode, so the
    /// tools go too, not just the panels.
    /// </summary>
    public void SetPresenting(bool presenting)
    {
        if (presenting == IsPresenting)
        {
            return;
        }

        if (presenting)
        {
            _chromeBeforePresenting = new ChromeState(
                Rulers: RulersToggle.IsChecked,
                Thumbnails: ThumbnailPanel.Visibility == Visibility.Visible,
                Bookmarks: BookmarkPanel.Visibility == Visibility.Visible);

            Apply(ChromeState.Hidden);
        }
        else
        {
            var before = _chromeBeforePresenting ?? ChromeState.Hidden;
            _chromeBeforePresenting = null;
            Apply(before);
        }

        ToolRail.Visibility = presenting ? Visibility.Collapsed : Visibility.Visible;
        PropertyBar.Visibility = presenting ? Visibility.Collapsed : Visibility.Visible;

        // Shown on arrival, then left to fade. Full screen used to collapse the
        // bar outright, which took away the only chrome there was: night mode,
        // rotation, the page arrows and the way back out were all keyboard-only
        // in the one mode with no menu to find them in. It comes back on the
        // first movement of the pointer, the way a video player's controls do.
        _barRevealed = presenting;
        UpdateStatusBarVisibility();

        if (presenting)
        {
            ViewModel.Status = "Full screen. Press Esc or F11 to leave.";
            RestartBarHideTimer();
        }
        else
        {
            _barHideTimer?.Stop();
        }

        // The canvas has to own the keyboard or F11 and Escape go nowhere.
        RootGrid.Focus(FocusState.Programmatic);

        void Apply(ChromeState state)
        {
            RulersToggle.IsChecked = state.Rulers;
            ApplyRulerVisibility();
            ThumbnailPanel.Visibility = state.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
            BookmarkPanel.Visibility = state.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyRulerVisibility() =>
        SetRulersVisible(RulersToggle.IsChecked && ViewModel.PageCount > 0);

    /// <summary>
    /// Shows or hides the chrome that only means something with a document
    /// open.
    ///
    /// With nothing open, the rulers measured nothing, the status pill reported
    /// page 1 of a document that did not exist and offered to search it, and
    /// every drawing tool was lit with no page to draw on. The empty state was
    /// telling the user there was no document while the rest of the window
    /// carried on as though there were.
    /// </summary>
    private void UpdateChromeForDocument()
    {
        bool hasDocument = ViewModel.PageCount > 0;

        // Closing the last document lands back here, and the list must be
        // right for what is on disk NOW, not for what it was at launch.
        if (!hasDocument)
        {
            RefreshWelcome();
        }

        UpdateStatusBarVisibility();

        // The rail's own menu and settings button stay live: they are how you
        // open a file from here. Only the TOOLS go dim.
        ToolRailList.IsEnabled = hasDocument;

        ApplyRulerVisibility();
    }

    // ---------------- The bar in full screen ----------------

    /// <summary>How long the bar stays up after the pointer stops moving.</summary>
    private static readonly TimeSpan BarRevealFor = TimeSpan.FromSeconds(3);

    /// <summary>Whether the bar is currently being shown over a full screen
    /// document. Meaningless outside full screen, where it is simply on.</summary>
    private bool _barRevealed;

    private bool _pointerOverBar;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _barHideTimer;

    /// <summary>
    /// The one place that decides whether the bar is on screen.
    ///
    /// Two callers used to answer this separately and disagree: leaving full
    /// screen with no document open put the bar back.
    /// </summary>
    private void UpdateStatusBarVisibility() =>
        StatusBar.Visibility = ViewModel.PageCount > 0 && (!IsPresenting || _barRevealed)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Brings the bar back during full screen and starts its clock again.</summary>
    private void RevealStatusBar()
    {
        if (!IsPresenting)
        {
            return;
        }

        if (!_barRevealed)
        {
            _barRevealed = true;
            UpdateStatusBarVisibility();
        }

        RestartBarHideTimer();
    }

    private void RestartBarHideTimer()
    {
        _barHideTimer ??= CreateBarHideTimer();
        _barHideTimer.Stop();

        // Pinned while the pointer is on it, or while one of its menus is open:
        // hiding the bar out from under an open flyout would leave the menu
        // floating over the page attached to nothing.
        if (IsPresenting && !_pointerOverBar && !AnyBarFlyoutOpen)
        {
            _barHideTimer.Start();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateBarHideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = BarRevealFor;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_pointerOverBar || AnyBarFlyoutOpen)
            {
                RestartBarHideTimer();
                return;
            }

            _barRevealed = false;
            UpdateStatusBarVisibility();
        };
        return timer;
    }

    private bool AnyBarFlyoutOpen =>
        ZoomMenuButton.Flyout is { IsOpen: true }
        || ViewOptionsButton.Flyout is { IsOpen: true }
        || SearchOptionsButton.Flyout is { IsOpen: true };

    /// <summary>Any movement of the pointer brings the bar back in full screen.
    /// Does nothing at all otherwise, which is the common case.</summary>
    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e) => RevealStatusBar();

    private void StatusBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverBar = true;
        _barHideTimer?.Stop();
    }

    private void StatusBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverBar = false;
        RestartBarHideTimer();
    }

    private void RulersHide_Click(object sender, RoutedEventArgs e)
    {
        RulersToggle.IsChecked = false;
        SetRulersVisible(false);
    }

    private void SetRulersVisible(bool on)
    {
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        TopRuler.Visibility = vis;
        LeftRuler.Visibility = vis;
        RulerCorner.Visibility = vis;
        // Reclaim the reserved 22px when off, restore it when back on. The
        // ScrollView's other margins never change, so this doesn't fight any
        // other layout hint.
        PageScroller.Margin = on ? new Thickness(22, 22, 0, 0) : new Thickness(0);
        // The floating toolbar is positioned in the scroller's viewport space,
        // so its origin has to move with the scroller's or it lands 22px out
        // whenever the rulers are toggled.
        ObjectToolbar.Margin = PageScroller.Margin;
        // Same reason, same space: the Skia layer's origin is the viewport's
        // top-left too.
        SkiaShapeCanvas.Margin = PageScroller.Margin;
        if (on) { RedrawRulers(); }
    }

    private void RulerUnit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse<RulerUnit>(tag, out var picked))
        {
            _rulerUnit = picked;
            // Keep both the View menu and right-click menu radio groups in sync
            // - they show the same choice but live in two separate DOM subtrees.
            SyncRulerUnitRadios(picked);
            RedrawRulers();
        }
    }

    private void SyncRulerUnitRadios(RulerUnit picked)
    {
        UnitInches.IsChecked = picked == RulerUnit.Inches;
        UnitCentimeters.IsChecked = picked == RulerUnit.Centimeters;
        UnitMillimeters.IsChecked = picked == RulerUnit.Millimeters;
        UnitPoints.IsChecked = picked == RulerUnit.Points;
        UnitPicas.IsChecked = picked == RulerUnit.Picas;
    }

    private void Ruler_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        // Both TopRuler and LeftRuler route here so the same context menu is
        // reachable from either. TopRuler already has it declared as its own
        // ContextFlyout for automatic show on right-tap; LeftRuler shows it
        // manually anchored to the pointer position.
        if (sender is Canvas c && c == LeftRuler)
        {
            RulerFlyout.ShowAt(LeftRuler, new FlyoutShowOptions
            {
                Position = e.GetPosition(LeftRuler),
            });
            e.Handled = true;
        }
    }

    private void Rulers_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawRulers();
    }

    /// <summary>Points-per-unit conversion factor (points *per one* selected
    /// unit). Multiply a point value by 1/factor to get the unit value.</summary>
    private static double PointsPerUnit(RulerUnit u) => u switch
    {
        RulerUnit.Points => 1.0,
        RulerUnit.Picas => 12.0,                    // 1 pica = 12 pt
        RulerUnit.Inches => 72.0,                   // 1 in   = 72 pt
        RulerUnit.Millimeters => 72.0 / 25.4,       // 1 mm   = 72/25.4 pt ≈ 2.835
        RulerUnit.Centimeters => 72.0 / 2.54,       // 1 cm   = 72/2.54 pt ≈ 28.35
        _ => 1.0,
    };

    /// <summary>Candidate major-tick spacings PER UNIT. Chosen so labels land on
    /// round numbers in the display unit. For inches, that means 1/8, 1/4, 1/2,
    /// 1, 2, 5, 10, ... rather than an arbitrary "0.375".</summary>
    private static double[] MajorStepsPerUnit(RulerUnit u) => u switch
    {
        RulerUnit.Inches => new[] { 0.125, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 25.0 },
        RulerUnit.Centimeters => new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 25.0, 50.0 },
        RulerUnit.Millimeters => new[] { 1.0, 2.5, 5.0, 10.0, 20.0, 50.0, 100.0, 250.0, 500.0 },
        RulerUnit.Picas => new[] { 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0 },
        _ => new[] { 1.0, 2.0, 5.0, 10.0, 25.0, 50.0, 100.0, 200.0, 500.0, 1000.0 },
    };

    private void RedrawRulers()
    {
        if (TopRuler is null || LeftRuler is null) { return; }
        if (TopRuler.Visibility != Visibility.Visible) { return; }
        if (ViewModel.PageCount == 0) { return; }

        var (pageWpt, pageHpt) = ViewModel.CurrentPagePoints();
        if (pageWpt <= 0 || pageHpt <= 0) { return; }

        double layoutW = ViewportHost.ActualWidth > 0 ? ViewportHost.ActualWidth : 800;
        double zoom = PageScroller.ZoomFactor;

        // A turned page is scaled down to fit its card, so a page point covers
        // fewer screen DIPs than the zoom alone says. One factor serves both
        // rulers: the scale is uniform and the turns are quarters, so a point
        // is the same size on screen along either axis.
        double dipsPerPoint = (layoutW / pageWpt) * zoom * ViewModel.CurrentViewScale;
        if (dipsPerPoint <= 0) { return; }

        // Convert to selected unit. Ticks and labels operate in unit-space so
        // the labelled values are round (e.g. 1.0, 1.25 in inches, not the
        // arbitrary point values that would produce those inches).
        double pointsPerUnit = PointsPerUnit(_rulerUnit);
        double dipsPerUnit = dipsPerPoint * pointsPerUnit;

        // Pick a MAJOR spacing (in the selected unit) landing near 80 DIPs.
        // If ALL candidate steps are smaller than the target, use the largest
        // one; if all are larger (zoom out extreme), use the smallest.
        double[] steps = MajorStepsPerUnit(_rulerUnit);
        double targetDips = 80;
        double bestStep = steps[^1];
        double bestDelta = double.MaxValue;
        foreach (double s in steps)
        {
            double d = Math.Abs(s * dipsPerUnit - targetDips);
            if (d < bestDelta) { bestDelta = d; bestStep = s; }
        }
        double majorUnits = bestStep;
        double minorUnits = majorUnits / 5;                 // 5 minors per major
        double majorDips = majorUnits * dipsPerUnit;
        double minorDips = minorUnits * dipsPerUnit;

        // CURRENT page's top-left in RulerCanvas coords. PageMaker/Acrobat
        // put ruler zero at whichever page the user is looking at, not
        // permanently at page 1 - scroll to page 5 and the ruler restarts
        // at 0 for page 5's edges. Do the same by computing from the current
        // page's slot-top plus Padding.Top and the zoom, then subtracting the
        // scroll offset. TransformToVisual gave page 0's origin instead which
        // is why later pages had the ruler running past the page's top edge.
        //
        // ScrollView content coords are unzoomed; visual/screen coords are
        // zoomed. Page top in content-space = SlotTopOf(current) + Padding.Top.
        // In screen (ruler) space that's (that value) * zoom - VerticalOffset.
        double pageOriginX, pageOriginY;
        try
        {
            double slotTop = ViewModel.SlotTopOf(ViewModel.CurrentPageIndex);
            pageOriginY = (slotTop + ViewportHost.Padding.Top) * zoom - PageScroller.VerticalOffset;

            // Horizontal keeps the visual-tree walk because pages are centered
            // in ScrollView when content is narrower than viewport, so a plain
            // formula would have to redo that layout math. TransformToVisual
            // already knows it.
            var toTop = ViewportHost.TransformToVisual(TopRuler);
            var pT = toTop.TransformPoint(new Windows.Foundation.Point(
                ViewportHost.Padding.Left, 0));
            pageOriginX = pT.X;
        }
        catch
        {
            // TransformToVisual can throw during teardown; skip a redraw rather
            // than crash - the next ViewChanged will retry.
            return;
        }

        double viewportW = PageScroller.ViewportWidth;
        double viewportH = PageScroller.ViewportHeight;

        DrawRulerTicks(TopRuler, horizontal: true,
            originScreen: pageOriginX,
            viewportSpan: TopRuler.ActualWidth > 0 ? TopRuler.ActualWidth : viewportW,
            majorDips: majorDips, minorDips: minorDips, majorUnits: majorUnits);
        DrawRulerTicks(LeftRuler, horizontal: false,
            originScreen: pageOriginY,
            viewportSpan: LeftRuler.ActualHeight > 0 ? LeftRuler.ActualHeight : viewportH,
            majorDips: majorDips, minorDips: minorDips, majorUnits: majorUnits);
    }

    private void DrawRulerTicks(Canvas canvas, bool horizontal,
        double originScreen, double viewportSpan,
        double majorDips, double minorDips, double majorUnits)
    {
        canvas.Children.Clear();
        if (viewportSpan <= 0 || majorDips <= 0) { return; }

        // ActualTheme, not the app dictionary: the theme lives on the root
        // element, and Application.Current.Resources does not know about it.
        bool dark = canvas.ActualTheme == ElementTheme.Dark;
        var stroke = Theming.RulerTickBrush(dark);
        var text = Theming.RulerTextBrush(dark);

        // Walk minor-tick indices covering the visible span with one on each
        // side for safety. Compute unit value from index rather than accumulating
        // a double to avoid drift over a long ruler.
        int firstIdx = (int)Math.Floor((0 - originScreen) / minorDips) - 1;
        int lastIdx = (int)Math.Ceiling((viewportSpan - originScreen) / minorDips) + 1;

        const double barLen = 22;
        for (int i = firstIdx; i <= lastIdx; i++)
        {
            double s = originScreen + i * minorDips;
            double unitValue = i * (majorUnits / 5);
            // A major tick is one where the minor index is a multiple of 5.
            bool isMajor = (i % 5) == 0;
            double tickLen = isMajor ? barLen - 4 : barLen - 14;

            var line = new Microsoft.UI.Xaml.Shapes.Line
            {
                Stroke = stroke,
                StrokeThickness = 1,
            };
            if (horizontal)
            {
                line.X1 = line.X2 = Math.Round(s) + 0.5;
                line.Y1 = barLen - tickLen;
                line.Y2 = barLen;
            }
            else
            {
                line.Y1 = line.Y2 = Math.Round(s) + 0.5;
                line.X1 = barLen - tickLen;
                line.X2 = barLen;
            }
            canvas.Children.Add(line);

            if (isMajor)
            {
                var tb = new TextBlock
                {
                    Text = FormatRulerLabel(unitValue),
                    FontSize = 9,
                    Foreground = text,
                };
                if (horizontal)
                {
                    Canvas.SetLeft(tb, s + 2);
                    Canvas.SetTop(tb, 1);
                }
                else
                {
                    tb.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = -90 };
                    Canvas.SetLeft(tb, 1);
                    Canvas.SetTop(tb, s + 20);
                }
                canvas.Children.Add(tb);
            }
        }
    }

    // ---------------- Guide drag-out from ruler ----------------
    //
    // Down on a ruler starts a "creating guide" gesture; capture the pointer
    // so subsequent Moves/Releases land here even after the pointer leaves
    // the ruler and crosses into the page. On release, find which page (if
    // any) is under the pointer and add a guide there.
    //
    // Convention: dragging FROM the TOP ruler creates a HORIZONTAL guide
    // (horizontal line at the Y where you released), and FROM the LEFT ruler
    // creates a VERTICAL one. That is what PageMaker and Acrobat do - the
    // ruler you pull from measures the axis perpendicular to the guide, so
    // the number you're picking on the top ruler is X, and the LINE you drop
    // is a horizontal one at whatever Y you land on.
    private enum GuideDragSource { None, TopRuler, LeftRuler }
    private GuideDragSource _guideDrag = GuideDragSource.None;

    private void TopRuler_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(TopRuler).Properties.IsRightButtonPressed) { return; }
        _guideDrag = GuideDragSource.TopRuler;
        TopRuler.CapturePointer(e.Pointer);
        ShowGuidePreview(e);
        e.Handled = true;
    }

    private void LeftRuler_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(LeftRuler).Properties.IsRightButtonPressed) { return; }
        _guideDrag = GuideDragSource.LeftRuler;
        LeftRuler.CapturePointer(e.Pointer);
        ShowGuidePreview(e);
        e.Handled = true;
    }

    private void Ruler_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_guideDrag == GuideDragSource.None) { return; }
        UpdateGuidePreview(e);
    }

    private void Ruler_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Signal that this ruler is grab-and-pull. Hand cursor is what
        // Illustrator/PageMaker both use for the same gesture, and it lands
        // in the right ballpark of "you can drag out of here" without being
        // as literal as a custom I-beam-with-arrow.
        if (sender is Controls.CursorCanvas cc)
        {
            cc.SetCursorShape(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }
    }

    private void Ruler_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Controls.CursorCanvas cc)
        {
            cc.ClearCursor();
        }
    }

    private void Ruler_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_guideDrag == GuideDragSource.None) { return; }
        var source = _guideDrag;
        _guideDrag = GuideDragSource.None;
        HideGuidePreview();
        if (sender is UIElement el) { el.ReleasePointerCapture(e.Pointer); }

        var pInHost = e.GetCurrentPoint(ViewportHost).Position;
        double slotX = pInHost.X - ViewportHost.Padding.Left;
        double slotY = pInHost.Y - ViewportHost.Padding.Top;
        if (slotX < 0 || slotY < 0) { return; }

        int pageIndex = ViewModel.PageAt(slotY);
        if (pageIndex < 0) { return; }

        var slot = ViewModel.PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex);
        if (slot is null) { return; }

        double slotLocalY = slotY - ViewModel.SlotTopOf(pageIndex);
        double normX = slot.SlotWidth  > 0 ? Math.Clamp(slotX / slot.SlotWidth,  0, 1) : 0.5;
        double normY = slot.SlotHeight > 0 ? Math.Clamp(slotLocalY / slot.SlotHeight, 0, 1) : 0.5;

        // Top ruler => horizontal guide at Y; left ruler => vertical guide at X.
        if (source == GuideDragSource.TopRuler) { ViewModel.AddGuide(pageIndex, horizontal: true,  normY); }
        else                                    { ViewModel.AddGuide(pageIndex, horizontal: false, normX); }
        e.Handled = true;
    }

    private void Ruler_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _guideDrag = GuideDragSource.None;
        HideGuidePreview();
    }

    /// <summary>Show the preview line for the FROM-ruler drag currently in
    /// progress. Called once on press so the line appears at the moment the
    /// gesture starts, not on the first move.</summary>
    private void ShowGuidePreview(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_guideDrag == GuideDragSource.None) { return; }
        // Full-viewport line, thin in the axis perpendicular to the ruler.
        // Positioned via Margin below; size stays fixed for the duration.
        if (_guideDrag == GuideDragSource.TopRuler)
        {
            GuidePreviewLine.Width = double.NaN;   // stretch to Grid column width
            GuidePreviewLine.Height = 0.5;
            GuidePreviewLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            GuidePreviewLine.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            GuidePreviewLine.Width = 0.5;
            GuidePreviewLine.Height = double.NaN;
            GuidePreviewLine.HorizontalAlignment = HorizontalAlignment.Left;
            GuidePreviewLine.VerticalAlignment = VerticalAlignment.Stretch;
        }
        GuidePreviewLine.Visibility = Visibility.Visible;
        GuideReadout.Visibility = Visibility.Visible;
        UpdateGuidePreview(e);
    }

    private void UpdateGuidePreview(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Position the preview at the pointer, in the coordinate space of
        // Grid.Column=2 (where the Rectangle lives). Grid col 2 starts at the
        // same X as the left ruler. Translating pointer via TransformToVisual
        // on the RulerCorner (which lives at Grid col 2's top-left) gives us
        // that space; then just set Margin.
        var pInCorner = e.GetCurrentPoint(RulerCorner).Position;
        if (_guideDrag == GuideDragSource.TopRuler)
        {
            // Horizontal line: pin Y = pointer Y, X unchanged.
            GuidePreviewLine.Margin = new Thickness(0, pInCorner.Y, 0, 0);
        }
        else
        {
            // Vertical line: pin X = pointer X, Y unchanged.
            GuidePreviewLine.Margin = new Thickness(pInCorner.X, 0, 0, 0);
        }

        UpdateGuideReadout(e, pInCorner);
    }

    /// <summary>Refreshes the floating "Y = 1.5 in" chip near the pointer,
    /// showing the position the guide would land at IF released now. Points
    /// are converted through the current ruler unit so what the user reads
    /// matches the tick numbers.</summary>
    private void UpdateGuideReadout(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e,
                                    Windows.Foundation.Point pInCorner)
    {
        // Position in ViewportHost content space, mapped to the current page
        // (whichever page the pointer is over). If the pointer is above the
        // first page or off any page, show a coordinate anyway so the user
        // sees the pointer's mapped value throughout the drag.
        var pInHost = e.GetCurrentPoint(ViewportHost).Position;
        double slotX = pInHost.X - ViewportHost.Padding.Left;
        double slotY = pInHost.Y - ViewportHost.Padding.Top;

        int pageIndex = ViewModel.PageAt(slotY);
        var slot = pageIndex >= 0
            ? ViewModel.PageSlots.FirstOrDefault(s => s.PageIndex == pageIndex)
            : null;

        double valuePts;
        string axisLabel;
        if (_guideDrag == GuideDragSource.TopRuler)
        {
            // Horizontal guide -> Y readout in the current page's coordinate
            // system. Convert slot DIPs to points using the page's actual size.
            axisLabel = "Y";
            if (slot is not null && slot.SlotHeight > 0)
            {
                double slotLocalY = slotY - ViewModel.SlotTopOf(pageIndex);
                var (_, pageHpt) = ViewModel.CurrentPagePoints();
                valuePts = pageHpt > 0 ? slotLocalY / slot.SlotHeight * pageHpt : 0;
            }
            else { valuePts = 0; }
        }
        else
        {
            axisLabel = "X";
            if (slot is not null && slot.SlotWidth > 0)
            {
                var (pageWpt, _) = ViewModel.CurrentPagePoints();
                valuePts = pageWpt > 0 ? slotX / slot.SlotWidth * pageWpt : 0;
            }
            else { valuePts = 0; }
        }

        double valueUnits = valuePts / PointsPerUnit(_rulerUnit);
        string unitAbbr = _rulerUnit switch
        {
            RulerUnit.Inches => "in",
            RulerUnit.Centimeters => "cm",
            RulerUnit.Millimeters => "mm",
            RulerUnit.Picas => "pc",
            _ => "pt",
        };
        GuideReadoutText.Text = $"{axisLabel} = {FormatRulerLabel(valueUnits)} {unitAbbr}";

        // Chip trails the pointer by a small offset so it's not right under
        // the cursor. Kept inside Grid col 2 via Margin.
        const double dxChip = 14;
        const double dyChip = 12;
        GuideReadout.Margin = new Thickness(pInCorner.X + dxChip, pInCorner.Y + dyChip, 0, 0);
    }

    private void HideGuidePreview()
    {
        GuidePreviewLine.Visibility = Visibility.Collapsed;
        GuideReadout.Visibility = Visibility.Collapsed;
    }

    private void ClearGuidesPage_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearGuidesOnPage(ViewModel.CurrentPageIndex);
    }

    private void ClearGuidesAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllGuides();
    }

    private void LockGuidesToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AreGuidesLocked = LockGuidesToggle.IsChecked;
    }

    /// <summary>Adds four margin guides (top, bottom, left, right) at a
    /// specified inset. Sensible defaults for a print layout are 0.5 in on
    /// every side; the user overrides for anything else. Optionally applies
    /// to every page so a whole document picks up the margin in one call.</summary>
    private async void AddMargins_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PageCount == 0) { return; }
        string unitAbbr = UnitAbbr(_rulerUnit);

        NumberBox NB(double v) => new()
        {
            Minimum = 0, Maximum = 1000, SmallChange = 0.05, LargeChange = 0.5,
            Value = v,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var top = NB(0.5); var bottom = NB(0.5); var left = NB(0.5); var right = NB(0.5);
        var allPages = new CheckBox { Content = "Apply to all pages" };

        Grid Row(string label, NumberBox nb)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(nb, 1);
            var uab = new TextBlock { Text = unitAbbr, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(uab, 2);
            grid.Children.Add(lbl); grid.Children.Add(nb); grid.Children.Add(uab);
            return grid;
        }

        var stack = new StackPanel { Spacing = 6, Width = 260 };
        stack.Children.Add(Row("Top",    top));
        stack.Children.Add(Row("Bottom", bottom));
        stack.Children.Add(Row("Left",   left));
        stack.Children.Add(Row("Right",  right));
        stack.Children.Add(allPages);

        var dlg = new ContentDialog
        {
            Title = "Add margin guides",
            Content = stack,
            PrimaryButtonText = "Add",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { return; }

        double f = PointsPerUnit(_rulerUnit);
        ViewModel.AddMarginGuides(ViewModel.CurrentPageIndex,
            top.Value * f, bottom.Value * f, left.Value * f, right.Value * f,
            allPages.IsChecked == true);
    }

    /// <summary>Adds N vertical column guides (2 per column: left + right
    /// edges), with an optional gutter between columns and side margins that
    /// bound the content band. Defaults: 3 columns, 0.25 in gutter, no side
    /// margin - a starting point for a magazine-style layout.</summary>
    private async void AddColumns_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PageCount == 0) { return; }
        string unitAbbr = UnitAbbr(_rulerUnit);

        var cols = new NumberBox
        {
            Minimum = 1, Maximum = 24, SmallChange = 1, LargeChange = 1, Value = 3,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var gutter = new NumberBox
        {
            Minimum = 0, Maximum = 100, SmallChange = 0.05, LargeChange = 0.25, Value = 0.25,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var leftInset = new NumberBox
        {
            Minimum = 0, Maximum = 1000, SmallChange = 0.05, LargeChange = 0.5, Value = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var rightInset = new NumberBox
        {
            Minimum = 0, Maximum = 1000, SmallChange = 0.05, LargeChange = 0.5, Value = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var allPages = new CheckBox { Content = "Apply to all pages" };

        Grid Row(string label, FrameworkElement input, string? unit)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(input, 1);
            grid.Children.Add(lbl); grid.Children.Add(input);
            if (unit is not null)
            {
                var uab = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(uab, 2);
                grid.Children.Add(uab);
            }
            return grid;
        }

        var stack = new StackPanel { Spacing = 6, Width = 300 };
        stack.Children.Add(Row("Columns",       cols,      null));
        stack.Children.Add(Row("Gutter",        gutter,    unitAbbr));
        stack.Children.Add(Row("Left inset",    leftInset, unitAbbr));
        stack.Children.Add(Row("Right inset",   rightInset,unitAbbr));
        stack.Children.Add(allPages);

        var dlg = new ContentDialog
        {
            Title = "Add column guides",
            Content = stack,
            PrimaryButtonText = "Add",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { return; }

        double f = PointsPerUnit(_rulerUnit);
        ViewModel.AddColumnGuides(ViewModel.CurrentPageIndex,
            (int)Math.Round(cols.Value),
            gutter.Value * f,
            leftInset.Value * f, rightInset.Value * f,
            allPages.IsChecked == true);
    }

    private static string UnitAbbr(RulerUnit u) => u switch
    {
        RulerUnit.Inches => "in",
        RulerUnit.Centimeters => "cm",
        RulerUnit.Millimeters => "mm",
        RulerUnit.Picas => "pc",
        _ => "pt",
    };

    /// <summary>Numeric guide placement, PageMaker-style. Shows a small
    /// dialog with orientation + position (in the current ruler unit),
    /// and adds the guide to the current page.</summary>
    private async void AddGuideAt_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PageCount == 0) { return; }

        string unitAbbr = _rulerUnit switch
        {
            RulerUnit.Inches => "in",
            RulerUnit.Centimeters => "cm",
            RulerUnit.Millimeters => "mm",
            RulerUnit.Picas => "pc",
            _ => "pt",
        };

        // Compact layout: orientation radio at top, then number + unit label.
        var horizontal = new RadioButton { Content = "Horizontal (across the page)", GroupName = "GuideOrient", IsChecked = true };
        var vertical   = new RadioButton { Content = "Vertical (down the page)",     GroupName = "GuideOrient" };
        var input = new Microsoft.UI.Xaml.Controls.NumberBox
        {
            Minimum = 0,
            Maximum = 10000,
            SmallChange = 0.1,
            LargeChange = 1,
            Value = 1,
            SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Inline,
        };
        var unitLabel = new TextBlock
        {
            Text = unitAbbr,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(input);
        row.Children.Add(unitLabel);

        var stack = new StackPanel { Spacing = 8, Width = 320 };
        stack.Children.Add(horizontal);
        stack.Children.Add(vertical);
        stack.Children.Add(new TextBlock { Text = "Position:" });
        stack.Children.Add(row);

        var dlg = new ContentDialog
        {
            Title = "Add guide",
            Content = stack,
            PrimaryButtonText = "Add",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) { return; }

        double valueUnits = input.Value;
        if (double.IsNaN(valueUnits)) { return; }
        double valuePts = valueUnits * PointsPerUnit(_rulerUnit);

        // Convert points -> normalized (0-1) on the current page.
        var (pageWpt, pageHpt) = ViewModel.CurrentPagePoints();
        if (pageWpt <= 0 || pageHpt <= 0) { return; }

        bool isHorizontal = horizontal.IsChecked == true;
        double norm = isHorizontal
            ? Math.Clamp(valuePts / pageHpt, 0, 1)
            : Math.Clamp(valuePts / pageWpt, 0, 1);

        ViewModel.AddGuide(ViewModel.CurrentPageIndex, isHorizontal, norm);
    }

    /// <summary>Formats a ruler label sensibly for the current unit. Integers
    /// stay integer; fractional shows enough decimal places for the unit but
    /// no more (0.5 not 0.500).</summary>
    private static string FormatRulerLabel(double v)
    {
        // Snap tiny FP dust from index math (0.000000001 back to 0).
        if (Math.Abs(v) < 1e-6) { return "0"; }
        // Integer? Show without decimal.
        if (Math.Abs(v - Math.Round(v)) < 1e-4) { return ((int)Math.Round(v)).ToString(); }
        // Otherwise show up to 3 decimals, trimming trailing zeros.
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Arrow-key scroll distance in DIPs, close to Acrobat's nudge.</summary>
    private const double ArrowScrollStep = 64;

    /// <summary>Small arrow-key nudge on a selected annotation, in normalized
    /// page-width units. 0.002 = ~1 pt on a Letter-width page - the
    /// "just move it a bit" tap every editor supports.</summary>
    private const double SmallNudgeStep = 0.002;

    /// <summary>Shift+arrow bigger nudge, in the same units. 5x the small step
    /// matches Word / Illustrator's convention for the "coarser step".</summary>
    private const double BigNudgeStep = 0.01;

    private void ScrollBy(double dx, double dy) =>
        PageScroller.ScrollTo(
            PageScroller.HorizontalOffset + dx,
            PageScroller.VerticalOffset + dy,
            new ScrollingScrollOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));

    private void UpdateZoomReadout() =>
        ZoomPercentText.Text = $"{Math.Round(PageScroller.ZoomFactor * 100)}%";

    // ---------------- Annotation tool switcher ----------------

    /// <summary>
    /// The rail's selection IS the armed tool, so picking one arms it.
    ///
    /// Suppressed while the rail is being brought into line with a tool change
    /// that came from somewhere else (a shortcut key, or placing a stamp handing
    /// over to Select), which would otherwise re-enter and reset the pointer
    /// interaction a second time.
    /// </summary>
    private void ToolRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToolSelection)
        {
            return;
        }

        if (ToolRailList.SelectedItem is ToolDefinition tool)
        {
            SetActiveTool(tool.Mode);
            ReturnFocusAfterPointerUse();
        }
    }

    private bool _suppressToolSelection;

    /// <summary>
    /// Hands the keyboard back to the canvas after a control was used with the
    /// POINTER, and only then.
    ///
    /// Clicking anything in a list leaves focus inside it, and a list eats the
    /// arrow keys: after clicking a tool, Down stepped to the next tool instead
    /// of scrolling the document. Returning focus unconditionally would break
    /// the other direction, since a keyboard user arrowing through the rail
    /// would be thrown out on the first press. The focus STATE distinguishes
    /// them: Pointer means a click put it there.
    /// </summary>
    private void ReturnFocusAfterPointerUse()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Pointer })
        {
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    // ---------------- Stamps ----------------

    /// <summary>The stamp a click will place, or null when none is chosen.</summary>
    private StampEntry? _selectedStamp;

    /// <summary>
    /// The built-in a click will place, or null.
    ///
    /// Exactly one of this and <see cref="_selectedStamp"/> is ever set:
    /// choosing in either row clears the other. Two armed stamps would leave
    /// the next click's result down to which branch was read first.
    /// </summary>
    private BuiltInStamp? _selectedBuiltIn;

    /// <summary>
    /// A preview tile for a built-in, drawn by the code that draws the stamp.
    ///
    /// Static and by ID, matching StampThumbnail, because that is what an
    /// x:Bind function in the item template can call.
    /// </summary>
    public static Microsoft.UI.Xaml.Media.ImageSource? BuiltInStampThumbnail(string id) =>
        BuiltInStamps.ById(id) is { } stamp
            ? StampRenderer.Thumbnail(stamp, StampTheme.Default, 240)
            : null;

    /// <summary>
    /// A thumbnail for the stamp picker.
    ///
    /// Explicit rather than binding the path string straight to Image.Source:
    /// x:Bind is strongly typed and will not reliably convert a string to an
    /// ImageSource, which shows as a picker full of blank squares with no
    /// error anywhere. Decoded to a small size because these are 64px tiles
    /// and a full-resolution signature scan would otherwise be held in memory
    /// once per thumbnail.
    /// </summary>
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage StampThumbnail(string path)
    {
        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
        {
            DecodePixelWidth = 128,
            DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
        };

        try
        {
            image.UriSource = new Uri(path);
        }
        catch (Exception ex)
        {
            // A stamp that cannot be shown must not take the picker down with
            // it; it simply appears blank and the rest still work.
            Diag.Log($"stamp thumbnail failed for {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }

        return image;
    }

    /// <summary>
    /// Reloads the stamp strip from disk and preselects one.
    ///
    /// Read every time the stamp tool is armed rather than cached, because the
    /// folder is deliberately an ordinary folder the user can drop files into,
    /// and a cached list would show a stamp that is gone or miss one just added.
    /// </summary>
    private void RefreshStamps()
    {
        var stamps = StampLibrary.List();

        // The guard is held across the SELECTION too, not just the rebind.
        //
        // This is the stack overflow ("a new guard page for the stack cannot be
        // created"). Arming the stamp tool calls UpdateToolRail, which calls
        // RefreshStamps; setting SelectedItem here fires StampChoice_SelectionChanged,
        // which arms the stamp tool again, and round it goes until the stack is
        // gone. Nothing in this method is a user action, so none of it may
        // re-enter the selection handler.
        _suppressStampSelection = true;
        try
        {
            StampChoices.ItemsSource = stamps;

            // The built-ins never change, so they are bound once. Rebinding
            // would throw away every rendered tile and redraw all seventeen on
            // each arming of the tool.
            BuiltInStampChoices.ItemsSource ??= BuiltInStamps.All;

            // The tool can be armed before the viewport has ever resized, so
            // the strip would keep XAML's unbounded default until the window
            // was touched.
            ResizeStampStrip();

            StampChoices.Visibility = stamps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            StampEmptyHint.Visibility = stamps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Preselect: this session's choice if there is one, otherwise the
            // one remembered from last time. Someone who keeps a single
            // signature should never have to pick it twice. A built-in and a
            // PNG are both candidates, and whichever wins clears the other row.
            if (_selectedBuiltIn is null && _selectedStamp is null)
            {
                _selectedBuiltIn = BuiltInStamps.FromEntryId(StampLibrary.LastUsedId());
            }

            var wanted = _selectedBuiltIn is not null
                ? null
                : _selectedStamp is not null
                    ? stamps.FirstOrDefault(s => s.Path == _selectedStamp.Path)
                    : StampLibrary.LastUsed(stamps);

            StampChoices.SelectedItem = wanted;
            _selectedStamp = wanted;

            BuiltInStampChoices.SelectedItem = _selectedBuiltIn;
        }
        finally
        {
            _suppressStampSelection = false;
        }
    }

    private bool _suppressStampSelection;

    private void StampChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStampSelection)
        {
            return;
        }

        if (StampChoices.SelectedItem is StampEntry entry)
        {
            _selectedStamp = entry;

            // Only one stamp is armed at a time, so choosing here un-chooses
            // the built-in row.
            _selectedBuiltIn = null;
            ClearOtherStampRow(BuiltInStampChoices);

            StampLibrary.RememberLastUsed(entry);
            // Choosing a stamp arms the tool: picking one and then having to
            // find the tool button as well would be a pointless second step.
            SetActiveTool(ToolMode.Stamp);
            ReturnFocusAfterPointerUse();
        }
    }

    private void BuiltInStampChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStampSelection)
        {
            return;
        }

        if (BuiltInStampChoices.SelectedItem is BuiltInStamp stamp)
        {
            _selectedBuiltIn = stamp;

            _selectedStamp = null;
            ClearOtherStampRow(StampChoices);

            StampLibrary.RememberLastUsedId(BuiltInStamps.EntryId(stamp));
            SetActiveTool(ToolMode.Stamp);
            ReturnFocusAfterPointerUse();
        }
    }

    /// <summary>
    /// Lets the stamp rows use whatever width the window has.
    ///
    /// They used to be pinned at 320 DIP, which is about four tiles. Seventeen
    /// built-ins want roughly 1300, so the strip scrolled constantly even on a
    /// maximised window while most of the bar sat empty beside it. Removing the
    /// cap entirely is not the answer either: the property bar sizes to its
    /// content, so an uncapped strip would push the bar wider than the window
    /// and carry its own labels off the edge.
    ///
    /// So the cap follows the viewport. The arithmetic is in BuiltInStamps
    /// where it can be tested.
    /// </summary>
    private void ResizeStampStrip()
    {
        double available = BuiltInStamps.StripMaxWidth(PageScroller.ViewportWidth);

        BuiltInStampChoices.MaxWidth = available;
        StampChoices.MaxWidth = available;
    }

    /// <summary>
    /// Deselects the other row without letting it re-enter this handler.
    ///
    /// The guard matters: clearing a ListView's selection raises
    /// SelectionChanged, and without it choosing a built-in would immediately
    /// run the PNG handler with a null selection, which is the same re-entry
    /// trap RefreshStamps already documents.
    /// </summary>
    private void ClearOtherStampRow(ListView other)
    {
        _suppressStampSelection = true;
        try
        {
            other.SelectedItem = null;
        }
        finally
        {
            _suppressStampSelection = false;
        }
    }

    private async void AddStamp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".png");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var added = await StampLibrary.ImportAsync(file);
        if (added is null)
        {
            ViewModel.Status = "Could not add that stamp.";
            return;
        }

        // Select the new one rather than whatever was selected before: adding a
        // stamp is only ever a prelude to using it.
        _selectedStamp = added;
        RefreshStamps();
    }

    /// <summary>
    /// Opens the stamp folder in Explorer, so stamps can be added, renamed or
    /// removed as ordinary files. They are the user's images, not something
    /// this app should be the only way to manage.
    /// </summary>
    private async void OpenStampFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = StampLibrary.EnsureFolder();
            await Windows.System.Launcher.LaunchFolderPathAsync(folder);
        }
        catch (Exception ex)
        {
            Diag.Log($"could not open the stamp folder: {ex.Message}");
        }
    }

    /// <summary>Decodes the armed stamp and places it at a page point.</summary>
    private async Task PlaceSelectedStampAsync(int pageIndex, double x, double y)
    {
        if (_selectedBuiltIn is null && _selectedStamp is null)
        {
            ViewModel.Status = "Choose a stamp first.";
            return;
        }

        // A built-in is drawn NOW, at the size it is being placed and with
        // today's date if it carries one, rather than decoded from a file. From
        // here down the two kinds are the same buffer of pixels.
        StampPixels? pixels;
        string name;

        if (_selectedBuiltIn is { } builtIn)
        {
            name = builtIn.Label.Length > 0 ? builtIn.Label : builtIn.Id;
            pixels = StampRenderer.Render(
                builtIn, StampTheme.Default, System.Globalization.CultureInfo.CurrentCulture);
        }
        else
        {
            name = _selectedStamp!.Name;
            pixels = await StampLibrary.DecodeAsync(_selectedStamp.Path);
        }

        if (pixels is null)
        {
            ViewModel.Status = $"Could not read {name}.";
            return;
        }

        if (!ViewModel.PlaceStamp(pageIndex, x, y, pixels))
        {
            return;
        }

        // Hand over to Select and pick up what was just placed.
        //
        // Staying on the stamp tool meant the next click dropped ANOTHER copy,
        // when what anyone wants immediately after placing something is to
        // nudge and size it. Placing a second copy is the rarer intent and is
        // still one click on the stamp button away.
        SetActiveTool(ToolMode.Select);
        ViewModel.SelectNewestAnnotation(pageIndex);
    }

    // ---------------- Text boxes ----------------

    private TextBox? _textEditor;
    private int _textEditorPage;

    // The box being edited, NORMALIZED. Carried so the commit writes the same
    // rectangle that was dragged, and the core wraps the text to its width.
    private double _boxLeft;
    private double _boxTop;
    private double _boxRight;
    private double _boxBottom;

    /// <summary>
    /// When set, the editor is re-editing an existing text box rather than
    /// placing a new one, and committing REPLACES that box.
    /// </summary>
    private ViewportViewModel.TextBoxEditTarget? _editingTarget;

    /// <summary>
    /// Opens a live editor filling the dragged box, so what is typed wraps
    /// exactly where it will land. Committed on Escape, on Enter without Shift,
    /// or when focus leaves it.
    ///
    /// Coordinates are NORMALIZED, and the editor is positioned by the same
    /// <c>normalized * OverlayScale</c> the ink canvas uses for everything else,
    /// so it sits on the page where a committed mark at the same point would.
    /// </summary>
    private void BeginTextEdit(int page, double normLeft, double normTop,
                              double normRight, double normBottom,
                              string? initialText = null,
                              ViewportViewModel.TextBoxEditTarget? editing = null)
    {
        CommitTextEdit();

        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(page);
        double widthDip = (normRight - normLeft) * scale;

        _textEditor = new TextBox
        {
            AcceptsReturn = true,
            // Wrap to the dragged width, matching how the box renders. A fixed
            // Width plus wrap is what makes the editor a true preview.
            TextWrapping = TextWrapping.Wrap,
            Width = System.Math.Max(40, widthDip),
            MinHeight = System.Math.Max(24, ViewModel.TextFontSize * scale * 1.6),
            Padding = new Thickness(3),
            // A clear accent frame plus rounded corners reads as "this is a box
            // you are editing", the way Word frames a text box. When the box has
            // its own outline, UpdateOpenEditorStyle overrides this with it.
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x6C, 0xB0)),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Colors.White),
            Foreground = HexBrush(ViewModel.InkColorHex),
            // Arial is metrically close to the PDF's Helvetica, so the editor
            // wraps in almost the same places the rendered text will.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Arial"),
            FontSize = System.Math.Max(8, ViewModel.TextFontSize * scale),
            Text = initialText ?? string.Empty,
        };

        // Host on the hit-testable editing layer, NOT the ink canvas: there the
        // editor receives pointer input, so clicking to place the caret and
        // dragging to select text work. The scrim dims the rest of the page.
        Canvas.SetLeft(_textEditor, normLeft * scale);
        Canvas.SetTop(_textEditor, (normTop * scale) + pageTop);
        EditCanvas.Children.Add(_textEditor);
        EditOverlay.Visibility = Visibility.Visible;

        _textEditorPage = page;
        _boxLeft = normLeft;
        _boxTop = normTop;
        _boxRight = normRight;
        _boxBottom = normBottom;

        // Set AFTER the initial CommitTextEdit above, so closing any prior
        // editor does not adopt this edit's target.
        _editingTarget = editing;

        _textEditor.KeyDown += TextEditor_KeyDown;
        // Keep the editor open when a click lands on the property bar, so its
        // font/size/style controls can be used mid-edit without committing the
        // box. Any other focus loss (clicking the page, another box) commits.
        _textEditor.LosingFocus += Editor_LosingFocus;
        _textEditor.LostFocus += (_, _) => CommitTextEdit();
        _textEditor.Focus(FocusState.Programmatic);

        // Caret at the end, so re-editing appends rather than overwriting.
        _textEditor.SelectionStart = _textEditor.Text.Length;

        // Apply the box's fill, outline and alignment, so the editor is a true
        // preview of what will be placed.
        UpdateOpenEditorStyle();
    }

    /// <summary>
    /// Fill mode: a click on a fillable field opens the text editor sized to
    /// that field, so what the user types is placed as a text box that sits
    /// inside the field's printed box. This reuses the ordinary text-box
    /// pipeline, so the value renders, saves, flattens, and can be adjusted
    /// later with the Select tool like any other text box.
    /// </summary>
    private void BeginFormFieldEdit(int page, PdfEditorApp.Viewport.FormField field)
    {
        double height = field.Bottom - field.Top;

        // Size the text to the field: a fraction of its height, clamped so a
        // very short or very tall field still gets a sensible size. TextFontSize
        // is normalized against the page WIDTH, the space the rect uses too.
        ViewModel.TextFontSize = System.Math.Clamp(height * 0.62, 0.012, 0.05);

        // A form value is plain text inside the field's own box, so give it no
        // fill or outline of ours, left-aligned like a typed entry.
        ViewModel.TextAlign = PdfEditorApp.Viewport.TextAlign.Left;
        ViewModel.TextFillHex = string.Empty;
        ViewModel.TextOutlineHex = string.Empty;

        // Inset a hair so the text does not sit against the field border.
        double padX = (field.Right - field.Left) * 0.03;
        double padY = height * 0.12;

        // BeginTextEdit commits any prior editor first, so set the field this
        // edit fills AFTER it, or the prior field's name would be cleared.
        BeginTextEdit(page,
            field.Left + padX, field.Top + padY,
            field.Right - padX, field.Bottom - padY,
            initialText: string.IsNullOrEmpty(field.Value) ? null : field.Value);
        _fillingFieldName = field.Name;
    }

    /// <summary>The form field the open editor is filling, or null for an ordinary text box.</summary>
    private string? _fillingFieldName;

    /// <summary>
    /// Brings an OPEN editor into line with the tool's current colour, size,
    /// alignment, fill and outline, so changing any of them in the property bar
    /// mid-edit is reflected at once rather than only after committing.
    /// </summary>
    private void UpdateOpenEditorStyle()
    {
        if (_textEditor is null)
        {
            return;
        }

        _textEditor.Foreground = HexBrush(ViewModel.InkColorHex);
        _textEditor.FontSize = System.Math.Max(8, ViewModel.TextFontSize * ViewModel.OverlayScale);
        _textEditor.TextAlignment = ViewModel.TextAlign switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            TextAlign.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };

        // Fill: the box's own background, or an opaque white so the box reads as
        // a clean editing surface lifted off the dimmed page when it has no fill.
        _textEditor.Background = string.IsNullOrEmpty(ViewModel.TextFillHex)
            ? new SolidColorBrush(Colors.White)
            : HexBrush(ViewModel.TextFillHex);

        // Outline: the box's own border when set; otherwise the accent editing
        // frame, so there is always a clear boundary while typing.
        if (string.IsNullOrEmpty(ViewModel.TextOutlineHex))
        {
            _textEditor.BorderThickness = new Thickness(1.5);
            _textEditor.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x6C, 0xB0));
        }
        else
        {
            double w = System.Math.Max(1, ViewModel.TextOutlineWidthNorm * ViewModel.OverlayScale);
            _textEditor.BorderThickness = new Thickness(w);
            _textEditor.BorderBrush = HexBrush(ViewModel.TextOutlineHex);
        }

        // Font family, bold and italic: WinUI resolves an installed font by
        // name, so the editor previews close to what the core will embed. Arial
        // stands in for the default (Helvetica) as elsewhere.
        _textEditor.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(
            string.IsNullOrEmpty(ViewModel.TextFontFamily) ? "Arial" : ViewModel.TextFontFamily);
        _textEditor.FontWeight = ViewModel.TextBold
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;
        _textEditor.FontStyle = ViewModel.TextItalic
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;
        // Underline/strikethrough are not previewed: a TextBox has no
        // TextDecorations (only TextBlock does). They still render on the
        // committed box, which is what the PDF keeps.
    }

    // ---------------- Text alignment, fill, outline ----------------

    /// <summary>Reflects the chosen alignment, and keeps the four buttons radio-style.</summary>
    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out TextAlign align))
        {
            ViewModel.TextAlign = align;
        }

        SyncTextStyleControls();
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    /// <summary>True while the combo is being aligned to the tool in code, so the
    /// programmatic selection is not treated as a user pick (which would re-resolve
    /// the file and steal focus back from the editor).</summary>
    private bool _suppressFontCombo;

    private void FontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontCombo)
        {
            return;
        }

        ViewModel.SelectFontFamily(FontFamilyCombo.SelectedItem as PdfEditorApp.Viewport.FontFamily);
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TextBold = BoldBtn.IsChecked == true;
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TextItalic = ItalicBtn.IsChecked == true;
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TextUnderline = UnderlineBtn.IsChecked == true;
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    private void Strikethrough_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TextStrikethrough = StrikethroughBtn.IsChecked == true;
        UpdateOpenEditorStyle();
        ReturnFocusAfterPointerUse();
    }

    private bool _suppressFillChange;
    private bool _suppressOutlineChange;

    private void FillFlyout_Opening(object? sender, object e)
    {
        _suppressFillChange = true;
        // Show the correct existing fill so the custom picker opens on THIS
        // object's actual value (shape or text box), not on the tool's leftover.
        string? current = ViewModel.HasSelectedShape
            ? ViewModel.ShapeFillHex
            : ViewModel.TextFillHex;
        FillPicker.Color = string.IsNullOrEmpty(current)
            ? Microsoft.UI.Colors.White
            : ColorFromHex(current);
        _suppressFillChange = false;

        // The gradient row lives in this flyout and is filled in with it, which
        // is the moment it can be looked at and the moment the selection is
        // known. There is no cheaper signal: the fill button is not part of the
        // property bar's section sync.
        SyncGradient();
    }

    private void FillColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressFillChange)
        {
            return;
        }

        var c = args.NewColor;
        // Preserve whatever alpha the fill-opacity slider already set, so
        // dragging opacity down and then picking a new colour keeps the same
        // opacity rather than jumping back to 100%.
        string? current = ViewModel.HasSelectedShape
            ? ViewModel.ShapeFillHex
            : ViewModel.TextFillHex;
        double alpha = string.IsNullOrEmpty(current) ? 1.0 : InkPresets.OpacityOf(current);
        byte alphaByte = (byte)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255);
        string hex = $"#{alphaByte:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        if (ViewModel.HasSelectedShape)
        {
            ViewModel.ShapeFillHex = hex;
            ViewModel.ApplyFillToSelectedShape();
        }
        else
        {
            ViewModel.TextFillHex = hex;
            SyncTextStyleControls();
            UpdateOpenEditorStyle();
            ViewModel.ApplyStyleToSelectedTextBox();
        }
        ShowFillOpacity();
    }

    private void NoFill_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasSelectedShape)
        {
            ViewModel.ShapeFillHex = null;
            ViewModel.ApplyFillToSelectedShape();
        }
        else
        {
            ViewModel.TextFillHex = "";
            SyncTextStyleControls();
            UpdateOpenEditorStyle();
            ViewModel.ApplyStyleToSelectedTextBox();
        }
        // Clearing the fill means the fill-opacity slider has nothing to act on.
        ShowFillOpacity();
        FillFlyout.Hide();
    }

    private void OutlineFlyout_Opening(object? sender, object e)
    {
        _suppressOutlineChange = true;
        OutlinePicker.Color = string.IsNullOrEmpty(ViewModel.TextOutlineHex)
            ? ColorFromHex("#FF1565C0")
            : ColorFromHex(ViewModel.TextOutlineHex);
        _suppressOutlineChange = false;
    }

    private void OutlineColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressOutlineChange)
        {
            return;
        }

        var c = args.NewColor;
        ViewModel.TextOutlineHex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
        SyncTextStyleControls();
        UpdateOpenEditorStyle();
        ViewModel.ApplyStyleToSelectedTextBox();
    }

    private void NoOutline_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TextOutlineHex = "";
        SyncTextStyleControls();
        UpdateOpenEditorStyle();
        ViewModel.ApplyStyleToSelectedTextBox();
        OutlineFlyout.Hide();
    }

    /// <summary>Align (multi-selection) button clicked; the Tag names one of the
    /// AlignMode values. Anchor + extras all shift to hit the picked edge/centre
    /// of the selection's bounding box, then each writes through to the document.
    /// Named AlignObjects_Click to avoid clashing with the text-alignment radio
    /// handler (Align_Click) that already exists for the Text tool.</summary>
    private void AlignObjects_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse(tag, out ViewportViewModel.AlignMode mode))
        {
            ViewModel.AlignSelected(mode);
        }
    }

    /// <summary>Distribute button clicked; Tag is "H" or "V". Needs 3+ objects.</summary>
    private void Distribute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.DistributeSelected(horizontal: tag == "H");
        }
    }

    /// <summary>A fill preset swatch was clicked; its Tag is the "#AARRGGBB" hex.
    /// The picker closes immediately so the fill is applied in one gesture, the
    /// way a user of Word or Acrobat expects. If a text box is selected, the
    /// fill is applied to IT; otherwise the pick sets the tool's fill for the
    /// next box.</summary>
    private void FillPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex && !string.IsNullOrEmpty(hex))
        {
            // Shape and text box share the fill picker. Route to whichever kind
            // is selected; if both are somehow set, prefer the shape (text-box
            // fill still applies below - both paths no-op unless their kind is
            // actually the selected annotation).
            if (ViewModel.HasSelectedShape)
            {
                ViewModel.ShapeFillHex = hex;
                ViewModel.ApplyFillToSelectedShape();
            }
            else
            {
                ViewModel.TextFillHex = hex;
                SyncTextStyleControls();
                UpdateOpenEditorStyle();
                ViewModel.ApplyStyleToSelectedTextBox();
            }
            // Picking a fill can be the first fill the shape has: the fill-
            // opacity slider was hidden a moment ago and now needs to appear.
            ShowFillOpacity();
            FillFlyout.Hide();
        }
    }

    /// <summary>An outline preset swatch was clicked; same shape as fill.</summary>
    private void OutlinePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex && !string.IsNullOrEmpty(hex))
        {
            ViewModel.TextOutlineHex = hex;
            SyncTextStyleControls();
            UpdateOpenEditorStyle();
            ViewModel.ApplyStyleToSelectedTextBox();
            OutlineFlyout.Hide();
        }
    }

    /// <summary>A thickness preset (1/2/3/5 pt) was picked; the outline width is
    /// stored normalized against a capture width of 1000, i.e. "pt / 1000".</summary>
    private void OutlineThickness_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag
            && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double pt)
            && pt > 0)
        {
            ViewModel.TextOutlineWidthNorm = pt / 1000.0;
            UpdateOpenEditorStyle();
            ViewModel.ApplyStyleToSelectedTextBox();
        }
    }

    /// <summary>Brings the alignment buttons and the fill/outline swatches into line with the tool.</summary>
    private void SyncTextStyleControls()
    {
        AlignLeftBtn.IsChecked = ViewModel.TextAlign == TextAlign.Left;
        AlignCenterBtn.IsChecked = ViewModel.TextAlign == TextAlign.Center;
        AlignRightBtn.IsChecked = ViewModel.TextAlign == TextAlign.Right;
        AlignJustifyBtn.IsChecked = ViewModel.TextAlign == TextAlign.Justify;

        BoldBtn.IsChecked = ViewModel.TextBold;
        ItalicBtn.IsChecked = ViewModel.TextItalic;
        UnderlineBtn.IsChecked = ViewModel.TextUnderline;
        StrikethroughBtn.IsChecked = ViewModel.TextStrikethrough;

        // Show the tool's font in the combo without treating it as a user pick.
        // Matches by name so a re-opened box shows its own font; an empty name
        // (the default) clears the selection. Only when the family is among the
        // loaded items, otherwise the selection is left as-is.
        _suppressFontCombo = true;
        FontFamilyCombo.SelectedItem = string.IsNullOrEmpty(ViewModel.TextFontFamily)
            ? null
            : FontFamilyCombo.Items.OfType<PdfEditorApp.Viewport.FontFamily>()
                .FirstOrDefault(f => string.Equals(f.Name, ViewModel.TextFontFamily, StringComparison.OrdinalIgnoreCase));
        _suppressFontCombo = false;

        bool hasFill = !string.IsNullOrEmpty(ViewModel.TextFillHex);
        FillSwatch.Background = hasFill ? HexBrush(ViewModel.TextFillHex) : new SolidColorBrush(Colors.Transparent);
        FillNoneSlash.Visibility = hasFill ? Visibility.Collapsed : Visibility.Visible;

        bool hasOutline = !string.IsNullOrEmpty(ViewModel.TextOutlineHex);
        OutlineSwatch.BorderBrush = hasOutline ? HexBrush(ViewModel.TextOutlineHex) : new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));
        OutlineNoneSlash.Visibility = hasOutline ? Visibility.Collapsed : Visibility.Visible;

        // The thickness radios sit inside the outline picker's flyout; they need
        // to open showing the current thickness ticked. The stored value is
        // normalized (pt / 1000), so multiplying gets the pt-integer back.
        int pt = (int)System.Math.Round(ViewModel.TextOutlineWidthNorm * 1000.0);
        Thickness1.IsChecked = pt == 1;
        Thickness2.IsChecked = pt == 2;
        Thickness3.IsChecked = pt == 3;
        Thickness5.IsChecked = pt == 5;
    }

    /// <summary>Clicking the dimmed area outside the box finishes the edit, keeping what was typed.</summary>
    private void EditScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CommitTextEdit();
        e.Handled = true;
    }

    /// <summary>
    /// Cancels the editor's focus loss when the click is on the property bar, so
    /// picking a font or toggling bold does not close the box being edited.
    /// </summary>
    private void Editor_LosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (args.NewFocusedElement is DependencyObject target && IsWithin(target, PropertyBar))
        {
            args.TryCancel();
        }
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private void TextEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape commits and keeps what was typed; Shift+Enter adds a line.
        // Plain Enter commits, which is what most people expect of a small text
        // box, with Shift+Enter as the deliberate multi-line gesture.
        if (e.Key == VirtualKey.Escape)
        {
            CommitTextEdit();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Whether Alt is held. Read live rather than tracked like Ctrl: Alt
    /// activates the menu bar, so the window can take focus away mid-chord and
    /// a tracked flag would be left stuck down.
    /// </summary>
    private static bool IsAltDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Writes the editor's text to the page and hands the result over as an
    /// ordinary loaded annotation, or just closes the editor if it is empty.
    /// </summary>
    private void CommitTextEdit()
    {
        if (_textEditor is null)
        {
            return;
        }

        var editor = _textEditor;
        var target = _editingTarget;
        var fillingField = _fillingFieldName;
        _textEditor = null; // First, so the LostFocus this triggers is a no-op.
        _editingTarget = null;
        _fillingFieldName = null;
        editor.KeyDown -= TextEditor_KeyDown;

        string text = editor.Text.TrimEnd('\r', '\n');
        EditCanvas.Children.Remove(editor);
        // Lower the isolation layer once nothing is being edited.
        if (EditCanvas.Children.Count == 0)
        {
            EditOverlay.Visibility = Visibility.Collapsed;
        }

        // Re-editing an existing box: replace it, in one undo step. Empty text
        // deletes it, which is the natural way to clear a box you no longer
        // want, so this path runs even when the text is blank.
        if (target is ViewportViewModel.TextBoxEditTarget t)
        {
            // The old box was already removed when editing began, so this only
            // adds the new one. Empty text leaves it removed.
            if (ViewModel.CommitEditedTextBox(t.PageIndex, t.Left, t.Top, t.Right, t.Bottom,
                                              text, ViewModel.InkColorHex, ViewModel.TextFontSize)
                && !string.IsNullOrWhiteSpace(text))
            {
                SetActiveTool(ToolMode.Select);
                ViewModel.SelectNewestAnnotation(t.PageIndex);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // A form-field fill removes the field's widget (so its box stops drawing
        // over the text) and places the value; it does not leave the box selected
        // for resizing, since the point is to move on to the next field.
        if (fillingField is not null)
        {
            ViewModel.FillFormField(fillingField, _textEditorPage, _boxLeft, _boxTop, _boxRight, _boxBottom,
                                    text, ViewModel.InkColorHex, ViewModel.TextFontSize);
            return;
        }

        if (ViewModel.PlaceTextBox(_textEditorPage, _boxLeft, _boxTop, _boxRight, _boxBottom,
                                   text, ViewModel.InkColorHex, ViewModel.TextFontSize))
        {
            SetActiveTool(ToolMode.Select);
            ViewModel.SelectNewestAnnotation(_textEditorPage);
        }
    }

    private void SetActiveTool(ToolMode tool)
    {
        // Switching tools abandons whatever the previous one was part way
        // through.
        //
        // Without this, drag state set by one tool keeps running under the
        // next. PointerMoved tests _isPanning FIRST, so a pan flag left set
        // makes every later tool pan instead of doing its own job: pick the
        // hand, then pick highlight or draw, and the page just keeps moving.
        // It reads as the hand tool having taken the app over and the other
        // tools being dead.
        ResetPointerInteraction();

        // A half-typed text box is finished, not abandoned: switching tools is
        // a natural way to say "done typing", and dropping it would lose what
        // was written.
        CommitTextEdit();

        ViewModel.ActiveTool = tool;
        UpdateToolRail();
        UpdateCursor();
    }

    /// <summary>
    /// Marks the active tool in the rail.
    ///
    /// Without this nothing on screen said which tool was armed, so the only
    /// way to find out was to drag on the page and see what happened. In a
    /// tool-driven canvas app that is the difference between confident and
    /// tentative use.
    /// </summary>
    /// <summary>Row height previewing a stroke width in the picker.</summary>
    public static double WidthPreview(double normalizedWidth) =>
        Math.Clamp(normalizedWidth * 800, 1.5, 14);

    /// <summary>
    /// Populates the pen pickers once and selects the current values. Done in
    /// code rather than bound because the two lists come from static preset
    /// tables that never change, and binding them would add view-model surface
    /// for no benefit.
    /// </summary>
    private void InitializePenPickers()
    {
        WidthChoices.ItemsSource = InkPresets.Widths;
        WidthChoices.SelectedIndex = 1;
        FontSizeChoices.ItemsSource = TextBoxPresets.Sizes;
        ShowPaletteFor(ViewModel.ActiveTool);

        // Only now is the opacity slider allowed to speak. Anything it raised
        // before this point came from the framework settling the control, not
        // from the user, and acting on it would rewrite the tool's colour at
        // startup. Same story for the fill-opacity slider.
        _suppressOpacityChange = false;
        _suppressFillOpacityChange = false;
    }

    /// <summary>
    /// Applies the opacity slider to whichever colour the armed tool uses.
    ///
    /// Opacity lives in the colour's alpha rather than in a property of its
    /// own, because alpha is what actually reaches the saved file; see
    /// <see cref="InkPresets.WithOpacity"/>.
    /// </summary>
    // ---------------- the Drop Shadow row ----------------
    //
    // The row shows the SELECTED SHAPE's own shadow, not a tool state, so
    // clicking from one shape to another shows each one's own. Everything that
    // decides a value lives in DropShadowPanel, in the viewport library, where
    // a test can reach it; what is left here is reading controls and writing
    // them back, which is all this file should ever hold.

    /// <summary>
    /// True while the row is being filled in FROM a shape. Setting a slider
    /// raises ValueChanged, and without this the act of showing a shape's
    /// shadow would immediately write it back, turning every selection into an
    /// edit and every edit into a history entry.
    /// </summary>
    private bool _syncingShadow;

    /// <summary>The colour the row is set to, "#RRGGBB". The swatch's brush is
    /// a picture of this rather than the value itself.</summary>
    private string _shadowColorHex = "#000000";

    /// <summary>A page width to lay the row out against when nothing is
    /// selected, so the sliders have sensible ends before there is a shape to
    /// ask. Letter, which is what most documents are.</summary>
    private const double FallbackPageWidthPts = 612;

    private static SolidColorBrush ShadowBrush(string hex)
    {
        var (r, g, b) = DropShadowPanel.RgbOf(hex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }

    /// <summary>
    /// Fills the row in from the selected shape.
    ///
    /// With nothing selected the controls are disabled and a line says why:
    /// there is nowhere to put a shadow, and a row that silently does nothing
    /// is worse than one that says so.
    /// </summary>
    private void SyncDropShadow()
    {
        double pageWpt = ViewModel.SelectedShapePageWidthPts;
        bool editable = ViewModel.HasSelectedShape && pageWpt > 0;
        double layoutWidth = pageWpt > 0 ? pageWpt : FallbackPageWidthPts;

        var c = DropShadowPanel.From(
            editable ? ViewModel.SelectedShapeShadow : null, layoutWidth);

        _syncingShadow = true;
        try
        {
            ShadowToggle.IsEnabled = editable;
            ShadowToggle.IsOn = c.Enabled;
            ShadowControls.Visibility = editable && c.Enabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            ShadowNoSelectionHint.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

            // The ends of the sliders are a fraction of the PAGE, so they are
            // set here rather than in the markup: the same shadow is a
            // different number of points on A3 as on A4.
            ShadowDistanceSlider.Maximum = System.Math.Round(DropShadowPanel.MaxDistancePts(layoutWidth));
            ShadowBlurSlider.Maximum = System.Math.Round(DropShadowPanel.MaxBlurPts(layoutWidth));

            ShadowAngleBox.Value = c.AngleDeg;
            ShadowDistanceSlider.Value = System.Math.Clamp(c.DistancePts, 0, ShadowDistanceSlider.Maximum);
            ShadowBlurSlider.Value = System.Math.Clamp(c.BlurPts, 0, ShadowBlurSlider.Maximum);
            ShadowOpacitySlider.Value = System.Math.Clamp(
                c.OpacityPercent, DropShadowPanel.MinOpacityPercent, 100);

            _shadowColorHex = c.ColorHex;
            ShadowSwatch.Background = ShadowBrush(c.ColorHex);
            UpdateShadowReadouts();
        }
        finally
        {
            _syncingShadow = false;
        }
    }

    private void UpdateShadowReadouts()
    {
        ShadowDistanceReadout.Text = $"{ShadowDistanceSlider.Value:F0}pt";
        ShadowBlurReadout.Text = $"{ShadowBlurSlider.Value:F0}pt";
        ShadowOpacityReadout.Text = $"{ShadowOpacitySlider.Value:F0}%";
    }

    /// <summary>The row as it stands, ready to become a shadow.</summary>
    private DropShadowControls ShadowRowNow() => new(
        Enabled: ShadowToggle.IsOn,
        // A NumberBox reads NaN while it is empty, which is a state a person
        // passes through on the way to typing a number.
        AngleDeg: double.IsFinite(ShadowAngleBox.Value) ? ShadowAngleBox.Value : 0,
        DistancePts: ShadowDistanceSlider.Value,
        BlurPts: ShadowBlurSlider.Value,
        OpacityPercent: (int)System.Math.Round(ShadowOpacitySlider.Value),
        ColorHex: _shadowColorHex);

    /// <summary>
    /// Applies the row to the selected shape. Switched off, this hands over
    /// null, and the core takes the shadow away rather than leaving an
    /// invisible one behind.
    /// </summary>
    private void PushDropShadow()
    {
        if (_syncingShadow)
        {
            return;
        }

        double pageWpt = ViewModel.SelectedShapePageWidthPts;
        if (pageWpt <= 0)
        {
            return;
        }

        UpdateShadowReadouts();
        ViewModel.ApplyShadowToSelectedShape(
            DropShadowPanel.ToShadow(ShadowRowNow(), pageWpt));
    }

    private void Shadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingShadow)
        {
            return;
        }

        ShadowControls.Visibility = ShadowToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        PushDropShadow();
    }

    private void Shadow_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        PushDropShadow();

    private void ShadowAngle_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        PushDropShadow();

    private void ShadowDirection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag
            || !double.TryParse(
                tag,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double angle))
        {
            return;
        }

        // Under the guard, so moving the box does not push twice.
        _syncingShadow = true;
        try
        {
            ShadowAngleBox.Value = angle;
        }
        finally
        {
            _syncingShadow = false;
        }

        PushDropShadow();
    }

    private void ShadowColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag)
        {
            return;
        }

        // The preset buttons carry the same "#AARRGGBB" tags the fill picker
        // uses, so the markup is the markup that already exists; the alpha is
        // dropped because opacity is a control of its own.
        _shadowColorHex = DropShadowPanel.RgbHexOf(tag);
        ShadowSwatch.Background = ShadowBrush(_shadowColorHex);
        PushDropShadow();
    }

    // ---------------- the Glow row ----------------
    //
    // The shadow's row minus direction and distance, sharing its guard because
    // the two are filled in together and a second flag would only be a second
    // thing to forget to set. Everything that decides a value lives in
    // GlowPanel, where a test can reach it.

    /// <summary>The colour the glow row is set to, "#RRGGBB".</summary>
    private string _glowColorHex = GlowPanel.Defaults.ColorHex;

    /// <summary>Fills the glow row in from the selected shape.</summary>
    private void SyncGlow()
    {
        double pageWpt = ViewModel.SelectedShapePageWidthPts;
        bool editable = ViewModel.HasSelectedShape && pageWpt > 0;
        double layoutWidth = pageWpt > 0 ? pageWpt : FallbackPageWidthPts;

        var c = GlowPanel.From(editable ? ViewModel.SelectedShapeGlow : null, layoutWidth);

        _syncingShadow = true;
        try
        {
            GlowToggle.IsEnabled = editable;
            GlowToggle.IsOn = c.Enabled;
            GlowControlsPanel.Visibility = editable && c.Enabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            GlowNoSelectionHint.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

            // The ends before the value, or a value past the old maximum is
            // clamped to it on the way in and the row shows the wrong number.
            GlowBlurSlider.Minimum = GlowPanel.MinBlurPts;
            GlowBlurSlider.Maximum = System.Math.Round(GlowPanel.MaxBlurPts(layoutWidth));
            GlowBlurSlider.Value = System.Math.Clamp(
                c.BlurPts, GlowBlurSlider.Minimum, GlowBlurSlider.Maximum);
            GlowOpacitySlider.Value = System.Math.Clamp(
                c.OpacityPercent, GlowPanel.MinOpacityPercent, 100);

            _glowColorHex = c.ColorHex;
            GlowSwatch.Background = ShadowBrush(c.ColorHex);
            UpdateGlowReadouts();
        }
        finally
        {
            _syncingShadow = false;
        }
    }

    private void UpdateGlowReadouts()
    {
        GlowBlurReadout.Text = $"{GlowBlurSlider.Value:F0}pt";
        GlowOpacityReadout.Text = $"{GlowOpacitySlider.Value:F0}%";
    }

    /// <summary>The glow row as it stands.</summary>
    private GlowControls GlowRowNow() => new(
        Enabled: GlowToggle.IsOn,
        BlurPts: GlowBlurSlider.Value,
        OpacityPercent: (int)System.Math.Round(GlowOpacitySlider.Value),
        ColorHex: _glowColorHex);

    /// <summary>
    /// Applies the row to the selected shape. Switched off, this hands over
    /// null, and the view model takes the glow out of the shape's list while
    /// leaving every other effect on it.
    /// </summary>
    private void PushGlow()
    {
        if (_syncingShadow)
        {
            return;
        }

        double pageWpt = ViewModel.SelectedShapePageWidthPts;
        if (pageWpt <= 0)
        {
            return;
        }

        UpdateGlowReadouts();
        ViewModel.ApplyGlowToSelectedShape(GlowPanel.ToGlow(GlowRowNow(), pageWpt));
    }

    private void Glow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingShadow)
        {
            return;
        }

        GlowControlsPanel.Visibility = GlowToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        PushGlow();
    }

    private void Glow_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => PushGlow();

    private void GlowColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag)
        {
            return;
        }

        // The same "#AARRGGBB" tags the fill picker uses; the alpha is dropped
        // because opacity is a control of its own.
        _glowColorHex = DropShadowPanel.RgbHexOf(tag);
        GlowSwatch.Background = ShadowBrush(_glowColorHex);
        PushGlow();
    }

    // ---------------- the gradient row ----------------
    //
    // In the FILL flyout, not the Effects one. A gradient is what the inside of
    // the shape is painted with; a shadow and a glow are marks made beside it.
    // The row is filled in when the flyout OPENS, which is the moment it can be
    // looked at and the moment the selection is known.

    private bool _syncingGradient;
    private string _gradientStartHex = GradientPanel.Defaults.StartHex;
    private string _gradientEndHex = GradientPanel.Defaults.EndHex;

    /// <summary>
    /// Fills the row in from the selected shape.
    ///
    /// Guarded, because setting a slider raises ValueChanged: without it,
    /// merely opening the flyout on a shape would write its own gradient back
    /// to it and turn every look into an edit.
    /// </summary>
    private void SyncGradient()
    {
        bool editable = ViewModel.HasSelectedShape;
        var c = GradientPanel.From(editable ? ViewModel.SelectedShapeGradient : null);

        _syncingGradient = true;
        try
        {
            GradientToggle.IsEnabled = editable;
            GradientToggle.IsOn = c.Enabled;
            GradientControlsPanel.Visibility = editable && c.Enabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            GradientNoSelectionHint.Visibility = editable
                ? Visibility.Collapsed
                : Visibility.Visible;

            _gradientStartHex = c.StartHex;
            _gradientEndHex = c.EndHex;
            GradientAngleSlider.Value =
                Math.Clamp(c.AngleDeg, 0, GradientPanel.MaxAngleDeg);

            ShowGradientRow();
        }
        finally
        {
            _syncingGradient = false;
        }
    }

    private GradientControls GradientRowNow() => new(
        Enabled: GradientToggle.IsOn,
        StartHex: _gradientStartHex,
        EndHex: _gradientEndHex,
        AngleDeg: (int)Math.Round(GradientAngleSlider.Value));

    /// <summary>
    /// The row's own readouts: the two stop swatches, the angle, and a strip
    /// showing the gradient the controls describe.
    ///
    /// The strip is the only place a gradient can be SEEN while it is being
    /// set. The page itself is drawn by PDFium from the shape's appearance
    /// stream, and PDFium cannot make a shading, so the paint only reaches the
    /// page once the file has been saved and reopened.
    /// </summary>
    private void ShowGradientRow()
    {
        var row = GradientRowNow();

        GradientStartSwatch.Background = ShadowBrush(row.StartHex);
        GradientEndSwatch.Background = ShadowBrush(row.EndHex);
        GradientAngleReadout.Text = $"{row.AngleDeg}\u00B0";
        GradientPreview.Background = GradientBrushFor(row);
    }

    /// <summary>
    /// The row's gradient as a XAML brush, through the SAME endpoints the
    /// renderer and the PDF writer use.
    ///
    /// A LinearGradientBrush measures its points as fractions of the box it
    /// fills, y down, which is exactly the model's own convention, so the strip
    /// shows the direction the shape will get rather than an impression of it.
    /// </summary>
    private static LinearGradientBrush GradientBrushFor(GradientControls row)
    {
        var g = GradientPanel.AtAngle(
            RenderColorOf(row.StartHex), RenderColorOf(row.EndHex), row.AngleDeg);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(g.X0, g.Y0),
            EndPoint = new Windows.Foundation.Point(g.X1, g.Y1),
        };

        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = ColorFromHex(row.StartHex) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = ColorFromHex(row.EndHex) });

        return brush;
    }

    private static RenderColor RenderColorOf(string hex)
    {
        var (a, r, g, b) = InkPresets.ParseHex(hex);
        return new RenderColor(a, r, g, b);
    }

    /// <summary>
    /// Applies the row to the selected shape. Switched off, this hands over
    /// null, and the view model takes the gradient off the tag and puts a solid
    /// back in its place.
    /// </summary>
    private void PushGradient()
    {
        if (_syncingGradient || !ViewModel.HasSelectedShape)
        {
            return;
        }

        ShowGradientRow();
        ViewModel.ApplyGradientToSelectedShape(GradientPanel.ToGradient(GradientRowNow()));
    }

    private void Gradient_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingGradient)
        {
            return;
        }

        GradientControlsPanel.Visibility = GradientToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        PushGradient();
    }

    private void Gradient_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        PushGradient();

    private void GradientStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _gradientStartHex = tag;
            PushGradient();
        }
    }

    private void GradientEnd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _gradientEndHex = tag;
            PushGradient();
        }
    }

    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOpacityChange)
        {
            return;
        }

        bool highlighting = ViewModel.ActiveTool == ToolMode.Highlight;
        string current = highlighting ? ViewModel.HighlightColorHex : ViewModel.InkColorHex;
        string updated = InkPresets.WithOpacity(current, e.NewValue / 100.0);

        if (highlighting)
        {
            ViewModel.HighlightColorHex = updated;
        }
        else
        {
            ViewModel.InkColorHex = updated;
        }

        // Universal: if an object is selected, the opacity change writes through
        // to it, not just to the tool's state for the next mark. The alpha lives
        // in the object's colour, so setting the tool colour with a new alpha and
        // re-styling is enough. Both apply methods no-op when nothing of their
        // kind is selected, so calling both is safe.
        ViewModel.ApplyStyleToSelectedShape(changeColor: true, changeWidth: false);
        ViewModel.ApplyStyleToSelectedTextBox();

        ShowOpacity();
    }

    /// <summary>
    /// Starts SET, so nothing the slider raises while the page is still being
    /// built is mistaken for a deliberate change. Cleared once the pickers are
    /// initialized.
    /// </summary>
    private bool _suppressOpacityChange = true;

    /// <summary>Brings the slider and its readout into line with either the
    /// selected object's colour (if there is one - text box or shape) or, when
    /// nothing is selected, the armed tool's colour. Selection wins so opening
    /// the slider on a 40 percent rectangle shows 40, not the tool's default.</summary>
    private void ShowOpacity()
    {
        string hex = ViewModel.ActiveTool == ToolMode.Highlight
            ? ViewModel.HighlightColorHex
            : ViewModel.InkColorHex;

        int percent = (int)Math.Round(InkPresets.OpacityOf(hex) * 100);

        // Saved and restored rather than cleared, so writing the slider during
        // startup does not switch it on early.
        bool prior = _suppressOpacityChange;
        _suppressOpacityChange = true;
        OpacitySlider.Value = Math.Clamp(percent, OpacitySlider.Minimum, OpacitySlider.Maximum);
        _suppressOpacityChange = prior;

        OpacityReadout.Text = $"{percent}%";

        ShowFillOpacity();
    }

    /// <summary>Mirrors <see cref="ShowOpacity"/> for the fill slider: reads
    /// the selected object's own fill (shape fill today, text-box fill when
    /// that later gets its own opacity path), reflects the alpha into the
    /// slider and readout, and hides the whole sub-section when nothing here
    /// has a fill.</summary>
    private void ShowFillOpacity()
    {
        string? fillHex = ViewModel.HasSelectedShape
            ? ViewModel.ShapeFillHex
            : (ViewModel.HasSelectedTextBox ? ViewModel.TextFillHex : null);

        if (string.IsNullOrEmpty(fillHex))
        {
            FillOpacitySection.Visibility = Visibility.Collapsed;
            return;
        }

        FillOpacitySection.Visibility = Visibility.Visible;
        int percent = (int)Math.Round(InkPresets.OpacityOf(fillHex) * 100);
        bool prior = _suppressFillOpacityChange;
        _suppressFillOpacityChange = true;
        FillOpacitySlider.Value = Math.Clamp(percent, FillOpacitySlider.Minimum, FillOpacitySlider.Maximum);
        _suppressFillOpacityChange = prior;
        FillOpacityReadout.Text = $"{percent}%";
    }

    /// <summary>
    /// Shows the corner slider when it would do something: a rounded rectangle
    /// is selected, or the shape tool is armed with that kind so the next drag
    /// will make one. Otherwise it is collapsed rather than left inert.
    /// </summary>
    /// <summary>Puts the corner slider on the selected shape's own radius.
    /// VISIBILITY is not decided here: that comes from PropertyBarLayout with
    /// every other section, which is the whole point of the record.</summary>
    private void SyncCornerRadius()
    {
        int percent = (int)Math.Round(ViewModel.ShapeCornerPercent);
        bool prior = _suppressCornerRadiusChange;
        _suppressCornerRadiusChange = true;
        CornerRadiusSlider.Value = Math.Clamp(percent, CornerRadiusSlider.Minimum, CornerRadiusSlider.Maximum);
        _suppressCornerRadiusChange = prior;
        CornerRadiusReadout.Text = $"{percent}%";
    }

    /// <summary>Starts SET so the value the slider raises while the page is
    /// still building up isn't mistaken for a deliberate change. Cleared once
    /// the pickers are initialized (same pattern as <see cref="_suppressOpacityChange"/>).</summary>
    private bool _suppressFillOpacityChange = true;

    /// <summary>Fill-opacity slider tick. Rebuilds the fill hex with the new
    /// alpha and re-applies through the type-appropriate path. No-ops when
    /// nothing with a fill is selected (the section itself is hidden then, so
    /// this handler mostly doesn't fire, but the guard makes it safe if it
    /// does).</summary>
    /// <summary>
    /// Corner radius, as a percentage of the roundest the selected box can be.
    /// Applies to the selection when there is a rounded rectangle picked, and in
    /// every case stays as the setting the NEXT rounded rectangle is drawn with,
    /// which is how the colour and width controls already behave.
    /// </summary>
    private void CornerRadius_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressCornerRadiusChange) { return; }

        ViewModel.ShapeCornerPercent = e.NewValue;
        ViewModel.ApplyCornerRadiusToSelectedShape();
        CornerRadiusReadout.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    /// <summary>Guards the same way the opacity sliders do: putting the control
    /// where a freshly selected shape already is must not be read as the user
    /// asking to change it.</summary>
    private bool _suppressCornerRadiusChange;

    private void FillOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressFillOpacityChange)
        {
            return;
        }

        if (ViewModel.HasSelectedShape && !string.IsNullOrEmpty(ViewModel.ShapeFillHex))
        {
            ViewModel.ShapeFillHex = InkPresets.WithOpacity(ViewModel.ShapeFillHex, e.NewValue / 100.0);
            ViewModel.ApplyFillToSelectedShape();
        }
        else if (ViewModel.HasSelectedTextBox && !string.IsNullOrEmpty(ViewModel.TextFillHex))
        {
            ViewModel.TextFillHex = InkPresets.WithOpacity(ViewModel.TextFillHex, e.NewValue / 100.0);
            SyncTextStyleControls();
            UpdateOpenEditorStyle();
            ViewModel.ApplyStyleToSelectedTextBox();
        }

        FillOpacityReadout.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    /// <summary>
    /// Points the colour picker at the palette belonging to the active tool.
    ///
    /// The picker used to always show the PEN colours, even with the
    /// highlighter armed. So the highlighter palette existed and was simply
    /// never on screen: you picked from opaque pen swatches and got whichever
    /// translucent highlight happened to share that colour's name, and Black,
    /// having no highlighter counterpart, silently did nothing at all.
    /// </summary>
    private void ShowPaletteFor(ToolMode tool)
    {
        bool highlighting = tool == ToolMode.Highlight;
        var palette = InkPresets.PaletteFor(highlighting);
        string wanted = highlighting ? ViewModel.HighlightColorHex : ViewModel.InkColorHex;

        // Rebinding raises SelectionChanged with nothing selected, which would
        // otherwise be read as the user clearing the colour.
        _suppressColorChange = true;
        ColorChoices.ItemsSource = palette;

        // Matched on RGB, NOT on the whole hex string. The opacity slider
        // rewrites the alpha component, so an exact string match stopped
        // finding the colour the moment opacity was touched and silently fell
        // back to the first swatch, changing the user's colour behind their
        // back.
        //
        // A custom colour matches NOTHING, and that must leave the tool's
        // colour alone: the old code defaulted the index to 0 and then applied
        // it, which is what snapped a hand-picked colour back to the first
        // swatch. Now no match means no preset is highlighted and nothing is
        // overwritten; the custom swatch is what shows the colour instead.
        int index = -1;
        for (int i = 0; i < palette.Count; i++)
        {
            if (SameColor(palette[i].Hex, wanted))
            {
                index = i;
                break;
            }
        }
        ColorChoices.SelectedIndex = index;
        _suppressColorChange = false;

        if (index >= 0)
        {
            ApplyColor(palette[index], highlighting);
        }

        UpdateCustomSwatch();
    }

    private bool _suppressColorChange;

    /// <summary>The colour the armed tool draws in, pen or highlighter.</summary>
    private string CurrentToolColorHex =>
        ViewModel.ActiveTool == ToolMode.Highlight
            ? ViewModel.HighlightColorHex
            : ViewModel.InkColorHex;

    /// <summary>The custom-colour swatch always shows the tool's live colour.</summary>
    private void UpdateCustomSwatch() =>
        CustomColorSwatch.Fill = HexBrush(CurrentToolColorHex);

    private bool _suppressCustomColor;

    /// <summary>Opens the picker on whatever colour the tool is currently using.</summary>
    private void CustomColorFlyout_Opening(object? sender, object e)
    {
        // Setting Color raises ColorChanged, which would otherwise read as the
        // user having picked the colour it already was.
        _suppressCustomColor = true;
        CustomColorPicker.Color = ColorFromHex(CurrentToolColorHex);
        _suppressCustomColor = false;
    }

    private void CustomColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressCustomColor)
        {
            return;
        }

        var c = args.NewColor;
        string hex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
        bool highlighting = ViewModel.ActiveTool == ToolMode.Highlight;

        // Through ApplyColor so the tool's OPACITY is preserved: the picker sets
        // hue only, and the separate opacity slider owns alpha.
        ApplyColor(new InkColor("Custom", hex), highlighting);
        ViewModel.ApplyStyleToSelectedShape(changeColor: true, changeWidth: false);

        // A custom colour is no preset, so nothing in the list is selected.
        _suppressColorChange = true;
        ColorChoices.SelectedIndex = -1;
        _suppressColorChange = false;

        UpdateCustomSwatch();
    }

    /// <summary>
    /// Applies a chosen colour to the tool it belongs to.
    ///
    /// The pen and highlighter keep SEPARATE colours: a highlighter has to
    /// stay translucent or it hides the text it is marking, so choosing one
    /// must never make the other opaque.
    /// </summary>
    private void ApplyColor(InkColor c, bool highlighting)
    {
        // Opacity is a property of the TOOL, not of the swatch, so changing
        // colour keeps whatever transparency was set. Otherwise every colour
        // change silently snapped opacity back to the preset's own alpha, and
        // choosing a different highlighter colour would undo the setting the
        // user had just made.
        string current = highlighting ? ViewModel.HighlightColorHex : ViewModel.InkColorHex;
        string hex = SameColor(c.Hex, current)
            ? current
            : InkPresets.WithOpacity(c.Hex, InkPresets.OpacityOf(current));

        if (highlighting)
        {
            ViewModel.HighlightColorHex = hex;
        }
        else
        {
            ViewModel.InkColorHex = hex;
        }

        ShowOpacity();
        UpdateOpenEditorStyle();
    }

    /// <summary>Compares two colours by RGB, ignoring their opacity.</summary>
    private static bool SameColor(string a, string b)
    {
        var x = InkPresets.ParseHex(a);
        var y = InkPresets.ParseHex(b);
        return x.R == y.R && x.G == y.G && x.B == y.B;
    }

    /// <summary>
    /// Abandons any in-progress pointer interaction and clears every drag flag.
    ///
    /// One place rather than per-flag cleanup at each call site, because the
    /// flags are read in priority order and a single stale one silently
    /// changes what every tool does.
    /// </summary>
    private void ResetPointerInteraction()
    {
        // Capture can be lost without a PointerReleased ever arriving, so the
        // toolbar's suppression has to be lifted here too or it stays hidden
        // for the rest of the session.
        _objectToolbarSuppressed = false;

        // Drop every drag-time visual (smart alignment lines, snap-flash)
        // FIRST, so a capture-lost mid-drag never leaves orange guides
        // stranded on the page. EndAnnotationMove below also calls this via
        // ClearDragTimeVisuals, but running once here covers the case where
        // _isMovingAnnotation was already cleared by a prior path.
        ViewModel.ClearDragTimeVisuals();

        // Tell the view model first, so a half-finished stroke or selection is
        // closed off properly rather than left hanging.
        if (_isMovingAnnotation)
        {
            ViewModel.EndAnnotationMove();
        }
        if (_isMarqueeing)
        {
            ViewModel.EndMarquee();
        }
        if (_isSelectingText)
        {
            ViewModel.EndTextSelection();
        }
        if (_isDrawing)
        {
            ViewModel.EndInkStroke();
        }
        if (_isDrawingShape)
        {
            ViewModel.EndShape();
        }

        // A half-drawn text box is abandoned, not opened: the preview goes and
        // the drag state clears, so switching tools mid-drag leaves nothing.
        if (_textBoxPreview is not null)
        {
            InkCanvas.Children.Remove(_textBoxPreview);
            _textBoxPreview = null;
        }

        _isPanning = false;
        _isDrawingShape = false;
        _isSizingText = false;
        _isMovingAnnotation = false;
        _isMarqueeing = false;
        _isSelectingText = false;
        _isDrawing = false;

        // The pan flag above drives which hand is showing, and capture can be
        // lost without a release - a flyout opening, the window deactivating
        // mid-drag. Without this the fist stays closed over a page that is no
        // longer being dragged.
        UpdateCursor();
        UpdateObjectToolbar();
    }

    /// <summary>
    /// Capture can be lost without a matching release, for instance when a
    /// flyout opens or the window loses activation mid-drag. That leaves the
    /// drag flags set, and the normal release handler ignores the event
    /// because the pointer id no longer matches. So this clears
    /// unconditionally: losing capture always ends the interaction.
    /// </summary>
    private void ViewportHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        ResetPointerInteraction();

    private void InkColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressColorChange && ColorChoices.SelectedItem is InkColor c)
        {
            ApplyColor(c, ViewModel.ActiveTool == ToolMode.Highlight);
            // If the current selection is a shape, apply the new colour to it,
            // not just to the tool's state for the next mark.
            ViewModel.ApplyStyleToSelectedShape(changeColor: true, changeWidth: false);
            UpdateCustomSwatch();
            ReturnFocusAfterPointerUse();
        }
    }

    private void InkWidth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthChoices.SelectedItem is InkWidth w)
        {
            ViewModel.InkWidth = w.Value;
            ViewModel.ApplyStyleToSelectedShape(changeColor: false, changeWidth: true);
            ReturnFocusAfterPointerUse();
        }
    }

    /// <summary>Points the font-size picker at the tool's current size.</summary>
    private void ShowFontSize()
    {
        var sizes = TextBoxPresets.Sizes;
        int index = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            if (System.Math.Abs(sizes[i].Value - ViewModel.TextFontSize) < 1e-6)
            {
                index = i;
                break;
            }
        }

        FontSizeChoices.SelectedIndex = index;
    }

    private void FontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeChoices.SelectedItem is FontSize s)
        {
            ViewModel.TextFontSize = s.Value;
            UpdateOpenEditorStyle();
            ReturnFocusAfterPointerUse();
        }
    }

    /// <summary>
    /// The kind is carried on each item's Tag and parsed back to the enum,
    /// rather than the list being bound to the enum's values. The items are
    /// hand-drawn geometry, one per kind, so there is nothing to template over.
    /// </summary>
    /// <summary>Set while UpdateToolRail writes the picker's selection back, so
    /// its own write is not read as the user choosing a shape.</summary>
    private bool _suppressShapeKindChange;

    private void ShapeKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressShapeKindChange) { return; }

        if (ShapeChoices.SelectedItem is FrameworkElement { Tag: string tag }
            && Enum.TryParse(tag, out ShapeKind kind))
        {
            ViewModel.ActiveShapeKind = kind;
            // Switching to or away from the rounded kind changes which sections
            // are relevant, so recompute the whole bar rather than poking one
            // section. UpdateToolRail writes back to ShapeChoices, hence the
            // guard below on its assignment.
            UpdateToolRail();
            ReturnFocusAfterPointerUse();
        }
    }

    /// <summary>
    /// Brings the rail and the property bar into line with the armed tool.
    ///
    /// Which sections of the property bar appear is read from the tool's
    /// declared <see cref="ToolOptions"/> rather than from a list of tool names
    /// here, so a new tool decides what it offers in the one record that
    /// defines it.
    /// </summary>
    private void UpdateToolRail()
    {
        var tool = ToolCatalog.For(ViewModel.ActiveTool);

        // Setting the selection re-enters SelectionChanged, which would arm the
        // tool again and reset the pointer interaction a second time.
        _suppressToolSelection = true;
        ToolRailList.SelectedItem = tool;
        _suppressToolSelection = false;

        PropertyBarToolName.Text = tool.Name;

        // EVERY section's visibility comes from one pure function, so a new
        // section cannot be wired up in the wrong place. The corner slider
        // shipped invisible because its visibility was decided in a fill-flyout
        // handler instead of here; a section that is a field of
        // PropertyBarSections cannot be left out of the decision.
        var sections = PropertyBarLayout.For(new PropertyBarState(
            ToolOptions: tool.Options,
            ActiveShapeKind: ViewModel.ActiveShapeKind,
            ToolIsShape: ViewModel.ActiveTool == ToolMode.Shape,
            HasSelectedShape: ViewModel.HasSelectedShape,
            HasSelectedTextBox: ViewModel.HasSelectedTextBox,
            HasSelectedRoundedRect: ViewModel.HasSelectedRoundedRect,
            HasMultiSelection: ViewModel.HasMultiSelection,
            HasSelectedAnnotation: ViewModel.HasSelectedAnnotationLoaded));

        ColorSection.Visibility = Show(sections.Color);
        WidthSection.Visibility = Show(sections.Width);
        OpacitySection.Visibility = Show(sections.Opacity);
        StampSection.Visibility = Show(sections.Stamp);
        AlignSection.Visibility = Show(sections.Align);
        CornerRadiusSection.Visibility = Show(sections.CornerRadius);
        ShapeSection.Visibility = Show(sections.Shape);
        FontSizeSection.Visibility = Show(sections.FontSize);
        FontSection.Visibility = Show(sections.Font);
        TextStyleSection.Visibility = Show(sections.TextStyle);
        TextAlignRow.Visibility = Show(sections.TextAlign);
        OutlineButton.Visibility = Show(sections.Outline);
        EffectsSection.Visibility = Show(sections.Effects);
        if (sections.Effects) { SyncDropShadow(); SyncGlow(); }
        PropertyBarRow2.Visibility = Show(sections.Row2);
        PropertyBar.Visibility = Show(sections.Bar);

        bool color = sections.Color;
        if (sections.CornerRadius) { SyncCornerRadius(); }
        if (sections.Shape)
        {
            // Assigning this re-enters ShapeKind_SelectionChanged, which calls
            // back here. Without the guard that is an infinite loop.
            _suppressShapeKindChange = true;
            ShapeChoices.SelectedIndex = (int)ViewModel.ActiveShapeKind;
            _suppressShapeKindChange = false;
        }
        if (sections.FontSize)
        {
            ViewModel.EnsureFontsLoaded();
            ShowFontSize();
            SyncTextStyleControls();
        }

        if (color)
        {
            ShowPaletteFor(ViewModel.ActiveTool);
            ShowOpacity();
        }

        if (sections.Stamp)
        {
            RefreshStamps();
        }
    }

    private static Visibility Show(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- File menu ----------------

    /// <summary>
    /// A blank document, after offering to save whatever is open.
    /// </summary>
    private void New_Click(object sender, RoutedEventArgs e)
    {
        // A new TAB, not a replacement. Nothing is discarded, so there is
        // nothing to prompt about.
        //
        // startBlank: New means "give me a document", which is the one path
        // that still makes one. Launching and + show the empty state instead.
        if (App.Window is MainWindow w) { w.AddDocumentTab(null, startBlank: true); }
    }

    /// <summary>
    /// Closes the open document, which means going back to a blank page: this
    /// is a single-document app, so there is nothing behind it to reveal.
    /// </summary>
    private async void CloseDocument_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow w) { await w.CloseDocumentTab(this); }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        OpenPickedFile(file.Path);
    }

    /// <summary>
    /// Puts a chosen file on screen, wherever it came from.
    ///
    /// Shared by the picker, the empty state's button and a dropped file, so
    /// all three land in the same place and cannot drift apart.
    ///
    /// Opens in its own TAB, so the document you were working on is still
    /// there. Before tabs this replaced it, and originally did so without even
    /// offering to save. The exception is a tab with nothing in it: opening
    /// from the empty state should use the tab already in front rather than
    /// leave an empty one behind it.
    /// </summary>
    private async void OpenPickedFile(string path)
    {
        if (App.Window is MainWindow w && (ViewModel.HasDocumentPath || ViewModel.IsDirty))
        {
            w.AddDocumentTab(path);
        }
        else
        {
            await LoadDocumentAsync(path);
        }
        Debug.WriteLine($"[MainPage] Opened \"{path}\"");
    }

    /// <summary>
    /// Whether this run has already asked about leftover work.
    ///
    /// Static, because the question belongs to the SESSION and not to a page:
    /// every tab is a MainPage and each one runs the same Loaded handler, so
    /// without this, opening a second tab would ask again about work the
    /// reader has already dealt with.
    /// </summary>
    private static bool _recoveryOffered;

    /// <summary>
    /// Offers back whatever a previous run left behind, and says whether
    /// something was restored.
    ///
    /// Anything still in the recovery folder belongs to a run that did not shut
    /// down cleanly, because a clean shutdown clears its own snapshot.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> OfferRecoveryAsync()
    {
        if (_recoveryOffered)
        {
            return false;
        }

        _recoveryOffered = true;

        // Snapshot PDFs whose manifest never got written. Nothing will ever
        // offer them, so they would otherwise pile up a document at a time.
        RecoveryStore.SweepOrphans();

        var pending = RecoveryStore.Pending();
        if (pending.Count == 0)
        {
            return false;
        }

        var choices = new List<RecoveryChoice>();
        foreach (var record in pending)
        {
            choices.Add(new RecoveryChoice(
                CrashRecovery.DescribeDocument(record.OriginalPath),
                $"{record.PageCount} pages, last saved {CrashRecovery.DescribeAge(record.SavedAtTicks, DateTime.UtcNow)}",
                record));
        }

        RecoveryList.ItemsSource = choices;
        RecoveryList.SelectedIndex = 0;
        RecoveryDialog.XamlRoot = XamlRoot;

        // Without this the offer is invisible in a trace, and whether it
        // appeared is the single thing worth knowing when someone reports that
        // their work did not come back.
        Diag.Log($"recovery: offering {choices.Count} document(s)");

        ContentDialogResult answer;
        try
        {
            answer = await RecoveryDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // See LoadDocumentAsync: ShowAsync throws if another dialog is
            // already open, and this runs from an async void Loaded handler
            // where an escaping exception would take the process down. The
            // snapshots are kept, so the offer comes back next launch.
            Diag.Log($"recovery: prompt could not be shown: {ex.GetType().Name}");
            return false;
        }

        if (RecoveryList.SelectedItem is not RecoveryChoice picked)
        {
            return false;
        }

        if (answer == ContentDialogResult.Secondary)
        {
            // Explicitly thrown away, which is the only way a snapshot is
            // deleted without the work first reaching the user's own file.
            RecoveryStore.Discard(picked.Record);
            Diag.Log("recovery: discarded at the reader's request");
            return false;
        }

        if (answer != ContentDialogResult.Primary)
        {
            // Not now. The snapshots stay, and the offer comes back.
            return false;
        }

        return ViewModel.RestoreFrom(picked.Record);
    }

    /// <summary>
    /// Opens a document into THIS page, asking for a password if it needs one.
    ///
    /// The single way a file reaches this page's view model. An encrypted PDF
    /// is not a broken one, and the difference is only useful if every entry
    /// point acts on it: the picker, a dropped file, the recent list, the
    /// welcome screen, a tab opened for a path and the headless auto-open all
    /// come through here.
    ///
    /// Nothing typed into the prompt is kept. It is not stored, not carried to
    /// the next document, and not written to the trace; it exists for the
    /// length of one call and is overwritten before the box is shown again.
    /// </summary>
    private async System.Threading.Tasks.Task<DocumentOpenOutcome> LoadDocumentAsync(string path)
    {
        var outcome = ViewModel.OpenDocument(path);
        if (outcome != DocumentOpenOutcome.NeedsPassword)
        {
            return outcome;
        }

        PasswordPrompt.Text =
            $"“{System.IO.Path.GetFileName(path)}” is protected. Enter its password to open it.";
        PasswordError.Visibility = Visibility.Collapsed;
        PasswordEntry.Password = string.Empty;
        PasswordDialog.XamlRoot = XamlRoot;

        // Keeps asking, the way every other reader does. There is no attempt
        // limit: PDFium does not impose one, the document is local, and locking
        // someone out of their own file after three tries would only mean
        // reopening it to try again.
        while (true)
        {
            ContentDialogResult answer;
            try
            {
                answer = await PasswordDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // ShowAsync throws if another ContentDialog is already open on
                // this root, which two tabs both auto-opening protected files
                // would do. Two of the callers here are async void, so an
                // escaping exception is raised where nothing observes it and
                // takes the process down with no message. Treated as a cancel.
                Diag.Log($"password prompt could not be shown: {ex.GetType().Name}");
                ViewModel.AbandonOpen();
                return DocumentOpenOutcome.NeedsPassword;
            }

            if (answer != ContentDialogResult.Primary)
            {
                // Cancelled. The tab is left empty rather than named after a
                // document it does not have, and the file is not in the recent
                // list because it never opened.
                ViewModel.AbandonOpen();
                return DocumentOpenOutcome.NeedsPassword;
            }

            outcome = ViewModel.OpenDocument(path, password: PasswordEntry.Password);
            PasswordEntry.Password = string.Empty;

            if (outcome != DocumentOpenOutcome.NeedsPassword)
            {
                return outcome;
            }

            PasswordError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Accepts a dragged PDF, and says so while it hovers.
    ///
    /// Without setting AcceptedOperation the cursor shows the "no" symbol and
    /// the drop never fires, so a drop target that looks inert is the default
    /// rather than something you have to break.
    /// </summary>
    private void EmptyState_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    /// <summary>
    /// Opens the first PDF among the dropped files, through the same path the
    /// picker uses.
    ///
    /// Filtered by extension rather than trusting the drop: a folder, an image
    /// or a Word file can all be dropped here, and handing any of them to
    /// PDFium would be an error dialog rather than an answer.
    /// </summary>
    private async void EmptyState_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file
                && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenPickedFile(file.Path);
                return;
            }
        }

        Diag.Log("drop: nothing among the dropped items was a PDF");
    }

    /// <summary>
    /// The file this page should open once it loads, set by the window when it
    /// creates a tab for a specific document. Null means no document, which
    /// shows the empty state.
    /// </summary>
    public string? InitialDocumentPath { get; set; }

    /// <summary>
    /// Whether this page should make a blank document for itself on load.
    ///
    /// Only File > New sets it. Launching and the tab strip's + button both
    /// land on the empty state instead, because neither is a request for a
    /// document: one is starting the app and the other is making room for one.
    /// </summary>
    public bool StartBlank { get; set; }

    /// <summary>
    /// Raised whenever this document's title changes, so the window can retitle
    /// the tab and, if this is the active one, the window itself.
    ///
    /// An event rather than the page reaching for the window: with tabs there
    /// are several pages and only one is showing, so a page that wrote straight
    /// to the title bar would let a background document rename the window.
    /// </summary>
    public event Action<MainPage>? DocumentTitleChanged;

    /// <summary>Title for the tab and the window, unsaved marker included.</summary>
    public string DocumentTitle => ViewModel.WindowTitle;

    /// <summary>What this document's TAB says: the file name, not the window title.</summary>
    public string TabTitle => ViewModel.TabTitle;

    /// <summary>
    /// Raised when a quick-action's availability changes, so the title bar can
    /// grey its buttons for the document actually in front.
    ///
    /// Separate from DocumentTitleChanged because these move independently: a
    /// first edit enables Undo without renaming anything.
    /// </summary>
    public event Action<MainPage>? CommandStateChanged;

    public bool CanUndo => ViewModel.CanUndo;
    public bool CanRedo => ViewModel.CanRedo;
    public bool CanSave => ViewModel.HasDocumentPath || ViewModel.IsDirty;

    // The title bar lives in the Window and these handlers live here, so the
    // window calls in rather than duplicating any of it.
    public void RunOpen() => OpenFile_Click(this, null!);
    public void RunSave() => Save_Click(this, null!);
    public void RunUndo() => ViewModel.Undo();
    public void RunRedo() => ViewModel.Redo();

    private void PushWindowTitle() => DocumentTitleChanged?.Invoke(this);

    // ---------------- Go to page ----------------

    /// <summary>
    /// Jumps to a typed page number. Enter commits; anything unparseable or out
    /// of range simply restores the current page rather than complaining, since
    /// the box is showing that number the rest of the time anyway.
    /// </summary>
    private void PageJumpBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (int.TryParse(PageJumpBox.Text, out int oneBased) && ViewModel.PageCount > 0)
        {
            ViewModel.GoToPage(Math.Clamp(oneBased - 1, 0, ViewModel.PageCount - 1));
        }

        SyncPageJumpBox();
        RootGrid.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void PageJumpBox_LostFocus(object sender, RoutedEventArgs e) => SyncPageJumpBox();

    /// <summary>
    /// Puts the current page back in the box. Skipped while it has focus, or
    /// scrolling would rewrite the number under someone mid-type.
    /// </summary>
    private void SyncPageJumpBox()
    {
        if (PageJumpBox.FocusState == FocusState.Unfocused)
        {
            PageJumpBox.Text = (ViewModel.CurrentPageIndex + 1).ToString();
        }
    }

    // ---------------- Settings ----------------

    /// <summary>
    /// Settings, built in code rather than as XAML.
    ///
    /// Every control here reflects state that already lives somewhere else, so
    /// building it fresh each time is what keeps it honest: a XAML dialog would
    /// need its own copy of each value and a way to push changes back, and the
    /// two would drift.
    ///
    /// Nothing here persists between runs yet. That needs a settings store in
    /// the same per-user folder as stamps and signatures, and is the next piece
    /// rather than something to fake with a half-written file.
    /// </summary>
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var tabs = new Pivot { Margin = new Thickness(0, 8, 0, 0) };
        tabs.Items.Add(new PivotItem { Header = "View", Content = Scrollable(BuildViewSettings()) });
        tabs.Items.Add(new PivotItem { Header = "About", Content = Scrollable(BuildAboutPane()) });

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Settings",
            Content = new Grid { Width = 420, Height = 400, Children = { tabs } },
            CloseButtonText = "Close",
        }.ShowAsync();
    }

    /// <summary>
    /// Lets a settings pane be taller than the dialog.
    ///
    /// Without this the panes are a plain StackPanel in a fixed-height Grid,
    /// so anything past the bottom edge is not merely hard to reach, it is
    /// invisible and unreachable. The View pane had already grown past it: the
    /// "remember where I stopped reading" switch was added, built, deployed,
    /// and could not be found, because it was below the fold of a container
    /// that does not scroll.
    /// </summary>
    private static ScrollViewer Scrollable(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>
    /// Puts the saved settings into effect. Called once the page is loaded,
    /// and again whenever a setting changes, so there is one path from stored
    /// state to visible state rather than two that can disagree.
    /// </summary>
    private void ApplySettings()
    {
        var s = SettingsStore.Current;

        if (App.Window is MainWindow window)
        {
            Theming.Apply(window, s.Theme);
            window.ApplyThemeChrome(s.Theme);
        }

        RootGrid.Background = Theming.CanvasBrush(s.Theme);

        // Every surface takes the tint, not only the canvas. Painting one and
        // leaving the rail, the property bar and the status bar in Fluent's own
        // greys is what made Midnight blue read as "grey app with a blue hole in
        // it" rather than a blue app.
        //
        // EVERY theme paints, including Light and Dark. There is no case that
        // leaves a surface alone, because both ways of leaving one alone have
        // already gone wrong: skipping the assignment kept the last theme's
        // paint, and putting back a brush captured at startup put back the
        // startup theme's paint.
        var chrome = Theming.ChromeBrush(s.Theme);
        ToolRail.Background = chrome;
        PropertyBar.Background = chrome;
        StatusBar.Background = chrome;
        ThumbnailPanel.Background = chrome;
        BookmarkPanel.Background = chrome;

        // The rulers count as chrome too. They were on Fluent's own white,
        // which in Sepia put a white strip between a cream rail and a brown
        // canvas: three different surfaces where there should have been two.
        TopRuler.Background = chrome;
        LeftRuler.Background = chrome;
        RulerCorner.Background = chrome;

        ApplyStatusBarDock(s.StatusBarDock);

        if (RulersToggle.IsChecked != s.ShowRulers)
        {
            RulersToggle.IsChecked = s.ShowRulers;
        }

        // Outside the rulers check, which it was wrongly nested inside: night
        // mode was then only restored when the rulers setting also happened to
        // differ, so a saved dark session came back light.
        NightModeToggle.IsChecked = s.NightMode;
        ViewModel.IsNightMode = s.NightMode;
        ApplyPageSheet(s.NightMode);

        // Guarded, because ticking these raises Click, and that handler saves.
        // Without it, restoring the mode overwrites the very setting it just
        // read. The view model is told directly afterwards, so the menu and the
        // viewport still agree.
        _applyingSettings = true;
        try
        {
            SinglePageViewItem.IsChecked = s.PageViewMode == PageViewMode.SinglePage;
            ContinuousViewItem.IsChecked = s.PageViewMode == PageViewMode.Continuous;
        }
        finally
        {
            _applyingSettings = false;
        }

        ViewModel.SetPageViewMode(s.PageViewMode);
        SyncBarViewState();

        // Unconditionally, not only when the toggle changed: this also runs on
        // load, when the chrome has never been decided at all.
        UpdateChromeForDocument();

        // How the user searches, restored onto the view model. The toggles read
        // these back through their two-way bindings, so the menu shows the
        // stored state without being set separately. Assigning the same value
        // raises nothing, so this cannot restart a search that is already
        // running when some other setting changes.
        ViewModel.SearchMatchCase = s.SearchMatchCase;
        ViewModel.SearchWholeWord = s.SearchWholeWord;

        RulerUnit_Click(new MenuFlyoutItem { Tag = s.RulerUnit }, null!);

        // The ticks are drawn shapes, not themed controls, so they keep the
        // colours they were painted with until something repaints them.
        RedrawRulers();
    }

    // ---------------- Moving the status bar ----------------

    private bool _draggingStatusBar;

    private void StatusGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingStatusBar = true;
        StatusGrip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// Re-anchors live while dragging, rather than moving the bar with the
    /// pointer and snapping on release. The bar jumping to each anchor as you
    /// cross into it SHOWS where it will land, so there is no guessing about
    /// what letting go will do.
    /// </summary>
    private void StatusGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingStatusBar) { return; }

        var p = e.GetCurrentPoint(RootGrid).Position;
        var dock = AppSettings.NearestDock(p.X, p.Y, RootGrid.ActualWidth, RootGrid.ActualHeight);
        if (dock != SettingsStore.Current.StatusBarDock)
        {
            SettingsStore.Update(s => s with { StatusBarDock = dock });
            ApplyStatusBarDock(dock);
        }
        e.Handled = true;
    }

    private void StatusGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingStatusBar = false;
        StatusGrip.ReleasePointerCapture(e.Pointer);
    }

    private void ApplyStatusBarDock(BarDock dock)
    {
        StatusBar.HorizontalAlignment = dock switch
        {
            BarDock.BottomLeft or BarDock.TopLeft => HorizontalAlignment.Left,
            BarDock.BottomRight or BarDock.TopRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };

        bool top = dock is BarDock.TopCentre or BarDock.TopLeft or BarDock.TopRight;
        StatusBar.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // Kept off the edge on whichever side it is now on, and clear of the
        // rulers when it sits at the top.
        StatusBar.Margin = new Thickness(16, top ? 16 : 0, 16, top ? 0 : 16);
    }

    // ---------------- Reading position ----------------

    /// <summary>
    /// Coalesces position saves. Scrolling raises ViewChanged continuously, and
    /// writing the settings file on every frame would put a disk write in the
    /// middle of a pan.
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _positionSaveTimer;

    private const int PositionSaveDelayMs = 1200;

    /// <summary>
    /// Where the reader is right now, or null if there is nothing to remember.
    ///
    /// Expressed as a page and a fraction into that page rather than a scroll
    /// offset, because an offset only means something at the zoom and window
    /// size it was taken at, and pages are not all the same height.
    /// </summary>
    private ReadingPosition? CurrentReadingPosition()
    {
        if (ViewModel.PageCount == 0 || PageScroller.ZoomFactor <= 0)
        {
            return null;
        }

        int page = ViewModel.CurrentPageIndex;
        double height = ViewModel.SlotHeightOf(page);
        if (height <= 0)
        {
            return null;
        }

        // The ScrollView reports a ZOOMED offset; slot space is unzoomed.
        double slotY = PageScroller.VerticalOffset / PageScroller.ZoomFactor;
        double fraction = (slotY - ViewModel.SlotTopOf(page)) / height;

        return new ReadingPosition(
            page, Math.Clamp(fraction, 0, 1), PageScroller.ZoomFactor, 0, ViewModel.PageCount);
    }

    /// <summary>
    /// Fills the welcome screen's recent list.
    ///
    /// Rebuilt every time the screen appears rather than cached: files get
    /// moved and deleted between runs, and a row that fails when clicked is a
    /// worse first impression than a shorter list.
    /// </summary>
    private void RefreshWelcome()
    {
        var rows = WelcomeList.Build(
            RecentFilesStore.Load(),
            SettingsStore.Current.ReadingPositions,
            System.IO.File.Exists);

        WelcomeRecent.ItemsSource = rows;
        WelcomeRecentSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The subtitle carries the one useful fact rather than repeating the
        // buttons underneath it.
        WelcomeSubtitle.Text = rows.Count > 0
            ? "Continue reading, or open something new"
            : "Open a document to begin";
    }

    private void WelcomeRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && path.Length > 0)
        {
            OpenPickedFile(path);
        }
    }

    private void WelcomeNew_Click(object sender, RoutedEventArgs e) => New_Click(sender, e);

    /// <summary>Stores where the reader is, against the open file's path.</summary>
    private void SaveReadingPosition()
    {
        // Switched off means nothing is recorded, not merely nothing restored.
        if (!SettingsStore.Current.RememberReadingPosition)
        {
            return;
        }

        // An unsaved document has no path to be remembered against, and a
        // document with no position is one we would restore to the top anyway.
        if (!ViewModel.HasDocumentPath
            || ViewModel.DocumentPath is not { Length: > 0 } path
            || CurrentReadingPosition() is not { } position)
        {
            return;
        }

        SettingsStore.Update(s => s with
        {
            ReadingPositions = ReadingPositions.Remember(
                s.ReadingPositions, path, position, DateTime.UtcNow),
        });

        Diag.Log($"reading position saved: page {position.PageIndex} " +
                 $"+{position.PageFraction:F2} zoom {position.Zoom:F2}");
    }

    /// <summary>Queues a save, restarting the wait so a continuous scroll writes once.</summary>
    private void QueueReadingPositionSave()
    {
        if (_positionSaveTimer is null)
        {
            // Same shape as the search debounce: one non-repeating timer,
            // stopped and restarted, so a scroll that never settles never
            // writes and a scroll that stops writes exactly once.
            _positionSaveTimer = DispatcherQueue.CreateTimer();
            _positionSaveTimer.IsRepeating = false;
            _positionSaveTimer.Interval = TimeSpan.FromMilliseconds(PositionSaveDelayMs);
            _positionSaveTimer.Tick += (_, _) => SaveReadingPosition();
        }

        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    /// <summary>
    /// Puts the reader back where they were, if we have seen this file before.
    ///
    /// Takes precedence over the default-view setting, which is about how a
    /// document you have never opened should appear. Once you have read to page
    /// 180, "fit page on page 1" is no longer the right answer.
    /// </summary>
    /// <returns>True if a position was restored.</returns>
    private bool RestoreReadingPosition()
    {
        if (!SettingsStore.Current.RememberReadingPosition)
        {
            return false;
        }

        if (ViewModel.DocumentPath is not { Length: > 0 } path)
        {
            return false;
        }

        if (ReadingPositions.For(SettingsStore.Current.ReadingPositions, path)
            is not { } position)
        {
            Diag.Log("reading position: none stored for this document");
            return false;
        }

        if (position.PageIndex >= ViewModel.PageCount)
        {
            // The file has been edited elsewhere since it was last read.
            Diag.Log($"reading position: page {position.PageIndex} is past the end " +
                     $"({ViewModel.PageCount} pages), ignoring");
            return false;
        }

        // The zoom is restored even when the place is not, because reading at
        // 150% is a preference about the document, not about the page.
        if (position.Zoom > 0)
        {
            PageScroller.ZoomTo((float)position.Zoom, null,
                new ScrollingZoomOptions(ScrollingAnimationMode.Disabled,
                                         ScrollingSnapPointsMode.Ignore));
        }

        if (!ReadingPositions.IsWorthRestoring(position))
        {
            return position.Zoom > 0;
        }

        double slotY = ViewModel.SlotTopOf(position.PageIndex)
                       + (position.PageFraction * ViewModel.SlotHeightOf(position.PageIndex));

        double zoom = position.Zoom > 0 ? position.Zoom : PageScroller.ZoomFactor;
        PageScroller.ScrollTo(
            PageScroller.HorizontalOffset,
            slotY * zoom,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled,
                                       ScrollingSnapPointsMode.Ignore));

        ViewModel.Status = $"Resumed at page {position.PageIndex + 1}.";
        Diag.Log($"reading position restored: page {position.PageIndex} +{position.PageFraction:F2} zoom {zoom:F2}");
        return true;
    }

    /// <summary>The saved default view, applied once a document has pages to fit.</summary>
    private void ApplyDefaultView()
    {

        switch (SettingsStore.Current.DefaultView)
        {
            case DefaultView.FitWidth: ZoomFitWidth_Click(this, null!); break;
            // Through the same handler the 100% menu item uses, so there is one
            // definition of what "actual size" does.
            case DefaultView.ActualSize:
                ZoomPreset_Click(new MenuFlyoutItem { Tag = "1.0" }, null!);
                break;
            default: ZoomFitPage_Click(this, null!); break;
        }
    }

    private UIElement BuildViewSettings()
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(0, 12, 0, 0) };

        // Theme first: it is the one people come to this dialog for.
        var themes = new ComboBox { Header = "Theme", Width = 220 };
        var themeValues = new[]
        {
            (AppTheme.System, "Use system setting"),
            (AppTheme.Light, "Light"),
            (AppTheme.Dark, "Dark"),
            (AppTheme.Sepia, "Sepia"),
            (AppTheme.MidnightBlue, "Midnight blue"),
        };
        foreach (var (_, label) in themeValues) { themes.Items.Add(label); }
        themes.SelectedIndex = Array.FindIndex(themeValues, t => t.Item1 == SettingsStore.Current.Theme);
        themes.SelectionChanged += (_, _) =>
        {
            if (themes.SelectedIndex >= 0)
            {
                SettingsStore.Update(s => s with { Theme = themeValues[themes.SelectedIndex].Item1 });
                ApplySettings();
            }
        };
        panel.Children.Add(themes);

        // Lighter or darker than the theme ships, without inventing more
        // themes. 0 is what the theme is; the ends reach half way to white and
        // half way to black. Every surface moves together, so no position on
        // the slider can produce a combination the theme would not.
        var intensity = new Slider
        {
            Header = "Color intensity",
            Width = 220,
            Minimum = AppSettings.MinIntensity,
            Maximum = AppSettings.MaxIntensity,
            Value = SettingsStore.Current.ColorIntensity,
            StepFrequency = 5,
            TickFrequency = 25,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside,
            SnapsTo = SliderSnapsTo.StepValues,
        };
        intensity.ValueChanged += (_, _) =>
        {
            SettingsStore.Update(s => s with { ColorIntensity = (int)intensity.Value });
            ApplySettings();
        };
        panel.Children.Add(intensity);

        var reset = new HyperlinkButton { Content = "Reset to the theme's own colours" };
        reset.Click += (_, _) => intensity.Value = 0;
        panel.Children.Add(reset);

        var view = new ComboBox { Header = "When a document opens", Width = 220 };
        var viewValues = new[]
        {
            (DefaultView.FitPage, "Fit the whole page"),
            (DefaultView.FitWidth, "Fit the width"),
            (DefaultView.ActualSize, "Actual size (100%)"),
        };
        foreach (var (_, label) in viewValues) { view.Items.Add(label); }
        view.SelectedIndex = Array.FindIndex(viewValues, v => v.Item1 == SettingsStore.Current.DefaultView);
        view.SelectionChanged += (_, _) =>
        {
            if (view.SelectedIndex >= 0)
            {
                // Applied now as well as saved, so the choice can be SEEN
                // being made rather than only taking effect next time.
                SettingsStore.Update(s => s with { DefaultView = viewValues[view.SelectedIndex].Item1 });
                ApplyDefaultView();
            }
        };
        panel.Children.Add(view);

        var resume = new ToggleSwitch
        {
            Header = "Remember where I stopped reading",
            IsOn = SettingsStore.Current.RememberReadingPosition,
        };
        resume.Toggled += (_, _) =>
        {
            SettingsStore.Update(s => s with { RememberReadingPosition = resume.IsOn });

            // Turning it off forgets what was already stored, rather than
            // keeping a list of everywhere you have read against a setting that
            // says not to. Turning it back on starts collecting again.
            if (!resume.IsOn)
            {
                SettingsStore.Update(s => s with
                {
                    ReadingPositions = new Dictionary<string, ReadingPosition>(),
                });
            }
        };
        panel.Children.Add(resume);

        var rulers = new ToggleSwitch
        {
            Header = "Rulers",
            IsOn = RulersToggle.IsChecked == true,
        };
        // Driven through the existing command rather than setting state here,
        // so the menu item, the Ctrl+R chord and this switch cannot disagree.
        rulers.Toggled += (_, _) =>
        {
            RulersToggle.IsChecked = rulers.IsOn;
            RulersToggle_Click(RulersToggle, null!);
            SettingsStore.Update(s => s with { ShowRulers = rulers.IsOn });
        };
        panel.Children.Add(rulers);

        var units = new ComboBox { Header = "Ruler units", Width = 200 };
        foreach (var name in new[] { "Inches", "Centimeters", "Millimeters", "Points", "Picas" })
        {
            units.Items.Add(name);
        }
        units.SelectedItem = CheckedRulerUnitName();
        units.SelectionChanged += (_, _) =>
        {
            if (units.SelectedItem is string tag)
            {
                RulerUnit_Click(new MenuFlyoutItem { Tag = tag }, null!);
                SettingsStore.Update(s => s with { RulerUnit = tag });
            }
        };
        panel.Children.Add(units);

        var thumbs = new Button { Content = "Toggle the pages panel (F4)" };
        thumbs.Click += (_, _) => ToggleThumbnails_Click(this, null!);
        panel.Children.Add(thumbs);

        return panel;
    }

    private string CheckedRulerUnitName() =>
        UnitCentimeters.IsChecked ? "Centimeters"
        : UnitMillimeters.IsChecked ? "Millimeters"
        : UnitPoints.IsChecked ? "Points"
        : UnitPicas.IsChecked ? "Picas"
        : "Inches";

    private UIElement BuildAboutPane()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = AppInfo.Name,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock { Text = $"Version {AppInfo.Version}" });

        // The third-party notices are owed, not decorative: PDFium and the
        // vendored pdfium-render both carry licences that require attribution.
        panel.Children.Add(new TextBlock
        {
            Text = "Renders with PDFium. Text shaping by rustybuzz. "
                   + "PDF access through a patched copy of pdfium-render.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        // The log is where every question we have had today was answered, so
        // it is worth one click rather than a path to type.
        var log = new HyperlinkButton { Content = "Open the diagnostic log" };
        log.Click += (_, _) =>
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");
            if (System.IO.File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        };
        panel.Children.Add(log);

        return panel;
    }

    // ---------------- Signatures ----------------

    /// <summary>
    /// Set when a signature has been picked and is waiting for a click to say
    /// where it goes. Deliberately not a ToolMode: it survives one click and
    /// then clears, so making it a tool would leave the rail showing a mode
    /// that no longer exists.
    /// </summary>
    private SignatureShape? _pendingSignature;

    /// <summary>Placed width, as a fraction of the page. About a signature's worth of a form line.</summary>
    private const double SignaturePlaceWidth = 0.28;

    private void RefreshSignatureMenu()
    {
        SignatureMenu.Items.Clear();

        var save = new MenuFlyoutItem { Text = "Save selection as signature..." };
        save.Click += async (_, _) => await SaveSelectionAsSignatureAsync();
        SignatureMenu.Items.Add(save);

        var saved = SignatureLibrary.Load();
        if (saved.Count == 0)
        {
            // Says how to get one rather than showing an empty list.
            SignatureMenu.Items.Add(new MenuFlyoutSeparator());
            SignatureMenu.Items.Add(new MenuFlyoutItem
            {
                Text = "Draw one with the pen, select it, then save",
                IsEnabled = false,
            });
            return;
        }

        SignatureMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var signature in saved)
        {
            var place = new MenuFlyoutItem { Text = signature.Name };
            ToolTipService.SetToolTip(place, "Click on the page to place it");
            place.Click += (_, _) =>
            {
                _pendingSignature = signature;
                ViewModel.Status = $"Click where {signature.Name} should go.";
            };
            SignatureMenu.Items.Add(place);
        }

        SignatureMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Open signatures folder" };
        manage.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(SignatureLibrary.FolderPath);
            Process.Start(new ProcessStartInfo(SignatureLibrary.FolderPath) { UseShellExecute = true });
        };
        SignatureMenu.Items.Add(manage);
    }

    private async Task SaveSelectionAsSignatureAsync()
    {
        if (ViewModel.SelectionCount == 0)
        {
            ViewModel.Status = "Draw your signature with the pen, select it, then save it.";
            return;
        }

        var box = new TextBox { PlaceholderText = "My signature", Width = 260 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save signature",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(box.Text) ? "Signature" : box.Text.Trim();
        if (ViewModel.CaptureSelectionAsSignature(name) is { } signature
            && SignatureLibrary.Save(signature))
        {
            ViewModel.Status = $"Saved {name}.";
            RefreshSignatureMenu();
        }
    }

    /// <summary>
    /// Places the waiting signature, if there is one. Returns true when the
    /// click was consumed, so the caller does not also treat it as a tool
    /// gesture.
    /// </summary>
    private bool TryPlacePendingSignature(int pageIndex, double normX, double normY)
    {
        if (_pendingSignature is not { } signature)
        {
            return false;
        }

        _pendingSignature = null;
        return ViewModel.PlaceSignature(signature, pageIndex, normX, normY, SignaturePlaceWidth);
    }

    // ---------------- Recent files ----------------

    /// <summary>
    /// Rebuilds the Recent files submenu.
    ///
    /// Rebuilt rather than bound, because the list is read from disk and pruned
    /// of files that have gone, so what it contains is only known at the moment
    /// it is asked for.
    /// </summary>
    private void RefreshRecentMenu()
    {
        RecentMenu.Items.Clear();
        var recent = RecentFilesStore.Load();

        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "None yet", IsEnabled = false });
            return;
        }

        foreach (string path in recent)
        {
            // The file name is what identifies it at a glance; the full path
            // goes in the tooltip for two files sharing a name.
            var item = new MenuFlyoutItem { Text = System.IO.Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) =>
            {
                if (App.Window is MainWindow w) { w.AddDocumentTab(path); }
            };
            RecentMenu.Items.Add(item);
        }
    }

    private Printing.DocumentPrinter? _printer;

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        _printer ??= new Printing.DocumentPrinter(ViewModel);
        await _printer.ShowAsync(App.WindowHandle);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>
    /// Save, falling back to Save As when the document has no path yet, which
    /// is what a blank document started from scratch is. Ctrl+S therefore
    /// always does something rather than silently failing.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (!ViewModel.HasDocumentPath)
        {
            return await SaveAsAsync();
        }

        bool saved = ViewModel.SaveDocument();
        if (saved) { ViewModel.SaveGuidesToSidecar(); }
        Debug.WriteLine($"[MainPage] Save -> {(saved ? "ok" : "failed")}");
        return saved;
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    /// <summary>
    /// Runs the Save As picker and save. Returns true only when the user
    /// picked a file and it saved, so the close prompt can tell a completed
    /// save from a cancelled picker.
    /// </summary>
    private Task<bool> SaveAsAsync() => SaveAsAsync(flatten: false);

    private async Task<bool> SaveAsAsync(bool flatten)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = flatten ? "flattened" : "edited";
        picker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        bool saved = ViewModel.SaveDocumentAs(file.Path, flatten);
        // Ruler guides live in a sidecar next to the PDF; write it whenever
        // the PDF itself is saved so a reopen from the new location shows the
        // same guides. Non-fatal if it fails.
        if (saved) { ViewModel.SaveGuidesToSidecar(); }
        Debug.WriteLine($"[MainPage] Save As \"{file.Path}\" flatten={flatten} -> {(saved ? "ok" : "failed")}");
        return saved;
    }

    /// <summary>
    /// Flatten and Save As: burns the marks into the page instead of writing
    /// them as annotation objects.
    ///
    /// Confirmed first, because it is the one save that cannot be undone by
    /// reopening the file. A normal save keeps every mark editable; this one
    /// turns them into part of the page forever, which is occasionally exactly
    /// what you want (sending a form somewhere that mishandles annotations)
    /// and otherwise a trap.
    /// </summary>
    private async void FlattenSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Flatten annotations?",
            Content = "Highlights, drawings and notes will be burned into the page. "
                    + "In the flattened copy they can no longer be moved, recoloured or "
                    + "deleted, by this app or any other. Your current document is not "
                    + "changed.",
            PrimaryButtonText = "Flatten and save a copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await SaveAsAsync(flatten: true);
    }

    private void EmptyState_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        OpenFile_Click(this, null!);

    /// <summary>The empty state's own Open button. Its own handler rather than
    /// the menu's, the way the double-tap above already has one: the picker is
    /// shared, the control is not.</summary>
    private void EmptyStateOpen_Click(object sender, RoutedEventArgs e) =>
        OpenFile_Click(this, null!);

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Window.Close();

    /// <summary>
    /// About dialog. Reports the app version and the render core's, since the
    /// two ship together but are built separately and a mismatched pair is
    /// exactly the sort of thing a bug report needs to state.
    /// </summary>
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string version = asm.GetName().Version?.ToString(3) ?? "unknown";
        string informational = asm
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? version;

        string core = "not loaded";
        try
        {
            string dll = System.IO.Path.Combine(AppContext.BaseDirectory, "render_core.dll");
            if (System.IO.File.Exists(dll))
            {
                core = $"{new System.IO.FileInfo(dll).Length / 1024} KB, " +
                       $"{System.IO.File.GetLastWriteTime(dll):yyyy-MM-dd}";
            }
        }
        catch
        {
            // Version reporting must never be the thing that breaks the app.
        }

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = AppInfo.Name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        body.Children.Add(new TextBlock { Text = $"Version {informational}" });
        body.Children.Add(new TextBlock { Text = $"Render core: {core}", Opacity = 0.75 });
        body.Children.Add(new TextBlock
        {
            Text = "PDF rendering by PDFium.",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"About {AppInfo.Name}",
            Content = body,
            CloseButtonText = "Close",
        }.ShowAsync();
    }

    /// <summary>
    /// Decides whether the window may close, prompting to save unsaved edits.
    /// Returns true to proceed with closing, false to keep the window open.
    ///
    /// Called from MainWindow's Closing handler, which cancels the close up
    /// front and re-issues it only if this returns true, because a Closing
    /// handler cannot await a dialog before setting Cancel.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        // Written now rather than left to the debounce, which will not fire if
        // the window is closing. Closing a book is exactly when where you got
        // to matters most.
        SaveReadingPosition();

        if (!ViewModel.IsDirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Unsaved changes",
            Content = "This document has changes that have not been saved. "
                      + "Save them before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            // Save: only close if the save actually went through. A cancelled
            // picker leaves the document open and unsaved.
            ContentDialogResult.Primary => await SaveAsAsync(),
            // Don't save: discard and close.
            ContentDialogResult.Secondary => true,
            // Cancel (or dismissed): stay open.
            _ => false,
        };
    }

    // ---------------- View menu / zoom toolbar ----------------

    private const float ZoomStep = 1.25f;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomByFactor(ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomByFactor(1f / ZoomStep);
    /// <summary>
    /// Shows or hides the pages panel. It overlays the canvas rather than
    /// taking a column, so toggling it never resizes the viewport and so never
    /// disturbs the zoom or scroll position.
    /// </summary>
    private void ToggleThumbnails_Click(object sender, RoutedEventArgs e) => ToggleThumbnails();

    /// <summary>
    /// Shows or hides the pages panel.
    ///
    /// No inset arithmetic: the panel owns a grid column, so collapsing it
    /// collapses the column and the canvas column absorbs the space. Overlap
    /// is not expressible, and the ScrollView's own SizeChanged drives the
    /// refit, so this does not have to reason about layout timing either.
    /// </summary>
    // ---------------- Movable tool rail ----------------

    private bool _isDraggingRail;
    private double _railDragStartX;

    /// <summary>
    /// The rail docks to a SIDE rather than floating freely.
    ///
    /// A rail that can sit anywhere sits on top of the page, which is the
    /// overlap this layout was rebuilt to make impossible. Snapping to
    /// whichever half of the window it is dropped in keeps it movable while
    /// the grid keeps guaranteeing that nothing covers the document.
    /// </summary>
    private void RailGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingRail = true;
        _railDragStartX = e.GetCurrentPoint(RootGrid).Position.X;
        RailGrip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void RailGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingRail)
        {
            return;
        }

        // Nudge the rail with the pointer so the drag reads as direct
        // manipulation rather than a gesture with a delayed result.
        double dx = e.GetCurrentPoint(RootGrid).Position.X - _railDragStartX;
        ToolRail.RenderTransform = new TranslateTransform { X = dx };
        e.Handled = true;
    }

    private void RailGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingRail)
        {
            return;
        }

        _isDraggingRail = false;
        RailGrip.ReleasePointerCapture(e.Pointer);
        ToolRail.RenderTransform = null;

        DockRail(e.GetCurrentPoint(RootGrid).Position.X > RootGrid.ActualWidth / 2);
        e.Handled = true;
    }

    /// <summary>Moves the rail to the left (column 0) or right (column 3) edge.</summary>
    private void DockRail(bool right)
    {
        Grid.SetColumn(ToolRail, right ? 3 : 0);

        // The pages panel belongs beside the rail, not stranded on the far
        // side of the document from it.
        Grid.SetColumn(ThumbnailPanel, right ? 3 : 1);
        ThumbnailPanel.Margin = right ? new Thickness(12, 12, 0, 12) : new Thickness(0, 12, 12, 12);

        // Keep the resize grip on the edge that faces the document, and flip the
        // drag direction to match, so dragging inward always widens.
        _thumbDockedRight = right;
        ThumbnailResizeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        if (_autoFit)
        {
            DispatcherQueue.TryEnqueue(() => FitToWidth(animate: true));
        }
    }

    private void ToggleThumbnails()
    {
        bool show = ThumbnailPanel.Visibility != Visibility.Visible;
        ThumbnailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // The two panels share one grid column, so showing both would stack
        // them in the same space. Opening one closes the other.
        if (show)
        {
            BookmarkPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleBookmarks_Click(object sender, RoutedEventArgs e) => ToggleBookmarks();

    private void ToggleBookmarks()
    {
        bool show = BookmarkPanel.Visibility != Visibility.Visible;
        BookmarkPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            ThumbnailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItem item)
        {
            ViewModel.GoToBookmark(item);
        }
    }

    /// <summary>The bookmark builder, while it is open.</summary>
    private BookmarkWindow? _bookmarkWindow;

    /// <summary>
    /// Opens the bookmark builder, or brings back the one already open.
    ///
    /// A WINDOW rather than a dialog. A ContentDialog will not grow past its
    /// ContentDialogMaxWidth of 548, so the two-column layout was clipped: the
    /// preview, half the options and the closing note fell off the right-hand
    /// edge. It being modeless is the second win, not a side effect: the
    /// document stays reachable, so a style can be picked by selecting a
    /// heading on the page while the window is open.
    /// </summary>
    private async void StyleBookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasDocumentPath)
        {
            await ShowMessage(
                "Save first",
                "Bookmarks are written into the PDF, so the document needs a file to be written to. Save it and try again.");
            return;
        }

        if (_bookmarkWindow is not null)
        {
            _bookmarkWindow.Activate();
            return;
        }

        var window = new BookmarkWindow(ViewModel, () =>
        {
            BookmarkPanel.Visibility = Visibility.Visible;
            ThumbnailPanel.Visibility = Visibility.Collapsed;
        });

        window.Closed += (_, _) => _bookmarkWindow = null;
        _bookmarkWindow = window;
        window.Activate();
    }

    // ---------------- One bookmark at a time ----------------
    //
    // The two bulk features build an outline from scratch and replace whatever
    // the document had. This is the half a reader reaches for first: mark the
    // page in front of them, fix a title that came out wrong, drop one that did
    // not belong.

    /// <summary>
    /// Bookmarks the current page, named after the selected text.
    ///
    /// With nothing selected it asks for a name, pre-filled with the page it
    /// would mark, rather than refusing: wanting to mark a page you are looking
    /// at is at least as common as wanting to mark a heading you can select.
    /// </summary>
    private async void AddBookmark_Click(object sender, RoutedEventArgs e) => await AddBookmarkHere();

    private async Task AddBookmarkHere()
    {
        if (!ViewModel.HasDocumentPath)
        {
            await ShowMessage(
                "Save first",
                "Bookmarks are written into the PDF, so the document needs a file to be written to. Save it and try again.");
            return;
        }

        // The page the SELECTION is on, not the one in view: a reader who
        // selected a heading and then scrolled means the heading.
        int page = ViewModel.SelectionStart?.Page ?? ViewModel.CurrentPageIndex;
        string title = OutlineEdits.TitleFrom(ViewModel.GetSelectedText(), page);

        if (ViewModel.GetSelectedText() is null or "")
        {
            if (await AskForBookmarkName(title, page) is not { } named)
            {
                return;
            }
            title = named;
        }

        if (ViewModel.AddBookmark(title, page) is { } problem)
        {
            await ShowMessage("Could not add the bookmark", problem);
            return;
        }

        BookmarkPanel.Visibility = Visibility.Visible;
        ThumbnailPanel.Visibility = Visibility.Collapsed;
    }

    private async void RenameBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkOf(sender) is not (int index, BookmarkItem item))
        {
            return;
        }

        if (await AskForBookmarkName(item.Title, item.PageIndex) is not { } named)
        {
            return;
        }

        if (ViewModel.RenameBookmark(index, named) is { } problem)
        {
            await ShowMessage("Could not rename the bookmark", problem);
        }
    }

    private async void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkOf(sender) is not (int index, BookmarkItem _))
        {
            return;
        }

        // No confirmation. One entry is a small loss, undoing it is one more
        // bookmark, and a prompt on every delete is what makes people stop
        // reading prompts.
        if (ViewModel.DeleteBookmark(index) is { } problem)
        {
            await ShowMessage("Could not delete the bookmark", problem);
        }
    }

    /// <summary>
    /// Which row a context-menu item belongs to.
    ///
    /// By IDENTITY in the list rather than by the list's selection: a
    /// right-click opens the flyout without selecting the row, so the selection
    /// is whatever was clicked last and would rename the wrong entry.
    /// </summary>
    private (int Index, BookmarkItem Item)? BookmarkOf(object sender)
    {
        if (sender is not FrameworkElement { DataContext: BookmarkItem item })
        {
            return null;
        }

        int index = ViewModel.Bookmarks.IndexOf(item);
        return index >= 0 ? (index, item) : null;
    }

    /// <summary>The name the reader typed, or null if they cancelled.</summary>
    private async Task<string?> AskForBookmarkName(string current, int pageIndex)
    {
        BookmarkNameBox.Text = current;
        BookmarkNamePage.Text = $"Goes to page {pageIndex + 1}.";
        BookmarkNameDialog.XamlRoot = XamlRoot;

        // Selected, not just focused, so typing replaces the suggestion rather
        // than running on from it.
        BookmarkNameDialog.Opened += SelectBookmarkName;
        try
        {
            if (await BookmarkNameDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }
        }
        finally
        {
            BookmarkNameDialog.Opened -= SelectBookmarkName;
        }

        string typed = BookmarkNameBox.Text.Trim();
        return typed.Length > 0 ? typed : null;

        void SelectBookmarkName(ContentDialog _, ContentDialogOpenedEventArgs __)
        {
            BookmarkNameBox.Focus(FocusState.Programmatic);
            BookmarkNameBox.SelectAll();
        }
    }

    private async Task ShowMessage(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NoteAnnotation note })
        {
            ViewModel.DeleteNote(note);
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e) => ViewModel.StepSearchMatch(1);

    private void SearchPrev_Click(object sender, RoutedEventArgs e) => ViewModel.StepSearchMatch(-1);

    /// <summary>
    /// Stores how the user searches. The toggles are bound two-way, so the view
    /// model already has the new value and has restarted the search; this only
    /// has to make it outlast the session.
    ///
    /// Read off the toggles rather than off the view model, so it cannot depend
    /// on whether the binding has pushed the change through yet.
    /// </summary>
    private void SearchOption_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s with
        {
            SearchMatchCase = MatchCaseToggle.IsChecked,
            SearchWholeWord = WholeWordToggle.IsChecked,
        });

    /// <summary>
    /// Enter steps to the next match, Shift+Enter to the previous, which is
    /// what every find bar does and avoids reaching for the mouse mid-search.
    /// Handled on the box itself, since the canvas shortcuts deliberately do
    /// not fire while a text field has focus.
    /// </summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        bool back = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        ViewModel.StepSearchMatch(back ? -1 : 1);
        e.Handled = true;
    }

    private void ZoomFitPage_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.Page;
        _autoFit = true;
        FitToWidth(animate: true);
    }

    /// <summary>
    /// Back to 100%, where a page point is a screen point.
    ///
    /// Extracted from the Ctrl+1 case so the menu entry and the chord are one
    /// path. Inline in the key handler, it was the one zoom command a menu
    /// could not offer.
    /// </summary>
    private void ZoomActualSize_Click(object sender, RoutedEventArgs e)
    {
        // Turns auto-fit OFF, like the other explicit zooms: the user has just
        // named a zoom, so a window resize must not silently overrule it.
        _autoFit = false;
        PageScroller.ZoomTo(1.0f, null,
            new ScrollingZoomOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
    }

    private void ZoomFitWidth_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = FitMode.Width;
        _autoFit = true;
        FitToWidth(animate: true);
    }

    /// <summary>
    /// Jumps to an absolute zoom. Auto-fit is switched off, since asking for
    /// 200% and then having a window resize quietly undo it would be worse
    /// than not offering the preset at all.
    /// </summary>
    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item ||
            !double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double factor))
        {
            return;
        }

        _autoFit = false;
        PageScroller.ZoomTo(
            (float)Math.Clamp(factor, PageScroller.MinZoomFactor, PageScroller.MaxZoomFactor),
            null,
            new ScrollingZoomOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>Fit Width also re-arms fit tracking, so resizing keeps it fitted.</summary>
    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        // Toggles between showing the whole page and filling the width, which
        // are the two framings worth one click.
        _fitMode = _fitMode == FitMode.Page ? FitMode.Width : FitMode.Page;
        _autoFit = true;
        FitToWidth(animate: true);
    }

    /// <summary>
    /// Animated zoom about the viewport centre. ScrollView runs the animation
    /// on the compositor, so it stays smooth regardless of what the UI thread
    /// is doing.
    /// </summary>
    private void ZoomByFactor(float factor)
    {
        float target = (float)Math.Clamp((double)PageScroller.ZoomFactor * factor,
                                         (double)PageScroller.MinZoomFactor, (double)PageScroller.MaxZoomFactor);
        var centre = new System.Numerics.Vector2(
            (float)(PageScroller.ViewportWidth / 2),
            (float)(PageScroller.ViewportHeight / 2));

        PageScroller.ZoomTo(target, centre,
            new ScrollingZoomOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
    }

    // ---------------- Page operations ----------------

    private void Undo_Click(object sender, RoutedEventArgs e) => ViewModel.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => ViewModel.Redo();

    private void RotatePage_Click(object sender, RoutedEventArgs e) => ViewModel.RotateCurrentPage(90);

    private void DeletePage_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteCurrentPage();

    // ---------------- Ink stroke rendering (Polyline points don't bind cleanly via x:Bind) ----------------

    /// <summary>
    /// Rebuilds the ink canvas from EVERY stroke in the document.
    ///
    /// This used to mirror only the current page's collection, which is wrong
    /// in a continuous viewport: two or three pages are visible at once, so
    /// ink on a visible neighbouring page vanished the instant scrolling made
    /// another page current. The current-page collection still serves as the
    /// change signal (it churns on every edit and page change), but the canvas
    /// is always drawn from the full set. Stroke counts are small, so a full
    /// rebuild is cheaper than being clever.
    /// </summary>
    private void OnInkStrokesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildInkCanvas();

        // The candidate renderer is fed from the same signal, and draws nothing
        // unless its flag is on. RebuildInkCanvas above is untouched: this is a
        // second consumer of the change, not a change to the first.
        RefreshSkiaShapeLayer();
    }

    /// <summary>
    /// Hands the Skia layer a frame, or hides it.
    ///
    /// Exactly one renderer is visible. With the flag off the ink overlay draws
    /// as it always has and this returns immediately, which is the rollback.
    ///
    /// The surface covers the VIEWPORT, never the document. It sits outside the
    /// scroller and stretches to it, so its size is bounded by the window
    /// rather than by how long the file is, and zoom and scroll arrive as
    /// numbers in a ViewportProjection instead of as a compositor transform.
    /// </summary>
    private void RefreshSkiaShapeLayer()
    {
        bool on = SettingsStore.Current.UseSkiaShapeLayer;

        SkiaShapeCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        InkCanvas.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        if (!on || ViewModel.OverlayScale <= 0)
        {
            return;
        }

        SkiaShapeCanvas.Show(
            ShapeRenderList.From(
                ViewModel.AllInkStrokes, ViewModel.AllShapes, PreviewShape(), PreviewInkGuide()),
            ViewModel.OverlayScale,
            ViewModel.SlotTopOf,
            ViewModel.ViewTransformOf,
            CurrentViewportProjection());
    }

    /// <summary>
    /// The shape currently being dragged, as the annotation it is about to
    /// become, or null when nothing is in progress.
    ///
    /// Built from the SAME four values the overlay's preview reads, so the two
    /// renderers preview the same thing. Because a ShapeAnnotation derives its
    /// outline and head from the draft, the preview cannot disagree with the
    /// committed shape that replaces it at pointer-up.
    ///
    /// Freehand ink previews separately, through PreviewInkGuide below, because
    /// its guide follows a different rule.
    /// </summary>
    private ShapeAnnotation? PreviewShape() =>
        ViewModel.ShapeInProgress is { } draft
            ? new ShapeAnnotation(
                ViewModel.ActiveShapePage, draft, ViewModel.InkColorHex, ViewModel.InkWidth)
            {
                // The SAME fill the shape is about to be committed with, from
                // the same property EndShape reads. Without it a filled
                // rectangle is an empty outline right up until the pointer
                // lifts and then fills all at once.
                Fill = ShapeFill.FromHex(ViewModel.ShapeFillHex),
            }
            : null;

    /// <summary>
    /// The freehand stroke being drawn right now, as the thin red guide, or
    /// null when nothing is being drawn.
    ///
    /// Anchored to the page the stroke STARTED on, like the overlay's, because
    /// in a continuous view you can begin drawing on a visible page that is not
    /// the current one and the guide has to land where the ink will.
    ///
    /// A shape drag takes precedence, mirroring the overlay's own ternary. The
    /// two cannot actually both be live, since a shape drag fills _shapeDraft
    /// and freehand fills _currentStroke, and CurrentStrokeInProgress is null
    /// unless the latter is set. The guard is here so the two renderers resolve
    /// the preview by the SAME rule rather than by both happening to be right.
    /// </summary>
    private ShapeRenderItem? PreviewInkGuide() =>
        ViewModel.ShapeInProgress is null && ViewModel.CurrentStrokeInProgress is { } points
            ? ShapeRenderList.InkGuide(ViewModel.ActiveInkPage, points)
            : null;

    /// <summary>
    /// Where slot space currently sits on the viewport, as plain numbers.
    ///
    /// The origin comes from asking InkCanvas where its own (0,0) has ended up
    /// relative to the scroller, because InkCanvas IS slot space: its children
    /// are placed at slot DIPs and nothing transforms it. That one question
    /// answers scroll offset, zoom, centring and padding together, and it
    /// cannot drift from the overlay because it is measured from the overlay.
    /// Nothing is written back, so this changes no layout and no scrolling.
    /// </summary>
    private ViewportProjection CurrentViewportProjection()
    {
        double zoom = PageScroller.ZoomFactor;
        double device = XamlRoot?.RasterizationScale ?? 1.0;

        var origin = InkCanvas
            .TransformToVisual(PageScroller)
            .TransformPoint(new Windows.Foundation.Point(0, 0));

        return new ViewportProjection(zoom, device, origin.X, origin.Y);
    }

    private void RebuildInkCanvas()
    {
        InkCanvas.Children.Clear();
        _livePreviewStroke = null;

        foreach (var stroke in ViewModel.AllInkStrokes)
        {
            InkCanvas.Children.Add(BuildStrokePolyline(stroke));
        }

        // Shapes draw on the same canvas, from the same polyline builder: a
        // rectangle, ellipse, line and arrow are all outlines, and giving them
        // their own element type would mean a second way for the same mark to
        // be positioned wrongly.
        foreach (var shape in ViewModel.AllShapes)
        {
            InkCanvas.Children.Add(BuildStrokePolyline(
                new InkStrokeAnnotation(shape.PageIndex, shape.Outline, shape.ColorHex, shape.StrokeWidth)));

            if (shape.Head.Count == 3)
            {
                InkCanvas.Children.Add(BuildFilledHead(shape.Head, shape.PageIndex, shape.ColorHex));
            }
        }
    }

    /// <summary>
    /// Expands a stroke's normalized points into slot space and offsets them
    /// by its page's position in the stack, so the ink lands on the right page
    /// of the continuous view.
    ///
    /// The geometry itself lives in OverlayShapeBuilder now, so the parity
    /// harness measures the SAME construction rather than its own copy of it.
    /// This end is where the page's three numbers are resolved from the view
    /// model, which is the only part the harness cannot share.
    /// </summary>
    /// <remarks>
    /// Arguments NAMED, because scale and pageTop are both doubles and a
    /// transposition would compile, draw every mark at the wrong place and
    /// weight, and be invisible to the test assembly, which cannot call a
    /// WinUI builder.
    /// </remarks>
    private Polyline BuildStrokePolyline(InkStrokeAnnotation stroke) =>
        Rendering.OverlayShapeBuilder.Stroke(
            stroke,
            scale: ViewModel.OverlayScale,
            pageTop: ViewModel.SlotTopOf(stroke.PageIndex),
            view: ViewModel.ViewTransformOf(stroke.PageIndex));

    /// <summary>
    /// An arrow's head, as a filled triangle. A stroked outline is not the same
    /// shape and would not match what goes into the file.
    /// </summary>
    private Polygon BuildFilledHead(
        IReadOnlyList<(double X, double Y)> points, int pageIndex, string colorHex) =>
        Rendering.OverlayShapeBuilder.FilledHead(
            points,
            colorHex,
            scale: ViewModel.OverlayScale,
            pageTop: ViewModel.SlotTopOf(pageIndex),
            view: ViewModel.ViewTransformOf(pageIndex));

    private Polygon? _livePreviewHead;

    /// <summary>
    /// The live preview changed. Split exactly like OnInkStrokesCollectionChanged
    /// above: the overlay's preview is rebuilt as it always was, and the
    /// candidate renderer is a SECOND consumer of the same signal rather than a
    /// change to the first.
    ///
    /// The refresh sits out here, not inside UpdateInkPreview, because that
    /// method returns early when there is nothing in progress and THAT is the
    /// case which has to clear the preview off the Skia surface. A cancelled
    /// drag leaves a ghost otherwise.
    /// </summary>
    private void OnInkStrokeChanged()
    {
        UpdateInkPreview();
        RefreshSkiaShapeLayer();
    }

    private void UpdateInkPreview()
    {
        // A shape being dragged previews through the same path as ink, built
        // from ShapeGeometry: the preview and the committed shape come from one
        // function, so releasing the pointer cannot change what was drawn.
        var points = ViewModel.ShapeInProgress is { } draft
            ? ShapeGeometry.Outline(draft, ViewModel.InkWidth)
            : ViewModel.CurrentStrokeInProgress;

        if (_livePreviewHead is not null)
        {
            InkCanvas.Children.Remove(_livePreviewHead);
            _livePreviewHead = null;
        }

        if (points is null)
        {
            if (_livePreviewStroke is not null)
            {
                InkCanvas.Children.Remove(_livePreviewStroke);
                _livePreviewStroke = null;
            }

            return;
        }

        // The head is rebuilt each move rather than reshaped, because its three
        // points all move as the drag turns.
        if (ViewModel.ShapeInProgress is { Kind: ShapeKind.Arrow } arrow)
        {
            var head = ShapeGeometry.ArrowHeadTriangle(arrow, ViewModel.InkWidth);
            _livePreviewHead = BuildFilledHead(head, ViewModel.ActiveShapePage, ViewModel.InkColorHex);
            InkCanvas.Children.Add(_livePreviewHead);
        }

        double scale = ViewModel.OverlayScale;

        // The page the preview is anchored to, resolved once: the thickness
        // below and the points further down must use the SAME page's transform,
        // or a preview drawn on a turned page is the wrong weight for its own
        // geometry.
        int previewPage = ViewModel.ShapeInProgress is not null
            ? ViewModel.ActiveShapePage
            : ViewModel.ActiveInkPage;
        var previewView = ViewModel.ViewTransformOf(previewPage);

        if (_livePreviewStroke is null)
        {
            // A shape previews in ITS OWN colour and width, so what is on
            // screen during the drag is what lands on the page. Freehand ink
            // keeps the thin red guide, which reads clearly against a stroke
            // being laid down continuously.
            bool shaping = ViewModel.ShapeInProgress is not null;
            _livePreviewStroke = new Polyline
            {
                Stroke = shaping
                    ? new SolidColorBrush(ColorFromHex(ViewModel.InkColorHex))
                    : new SolidColorBrush(Colors.Red),
                // Scaled by the page's transform for the same reason a
                // committed stroke is. The freehand guide stays a flat 2: it is
                // a guide, not a preview of a weight.
                StrokeThickness = shaping
                    ? ViewModel.InkWidth * ViewModel.OverlayScale * previewView.Scale
                    : 2,
            };
            InkCanvas.Children.Add(_livePreviewStroke);
        }

        // The in-progress stroke is normalized on the way in, exactly like a
        // committed one, so the preview and the finished stroke share a
        // coordinate space and the line cannot jump when the pointer lifts.
        // Anchored to the page the stroke STARTED on, not the current page:
        // in continuous view you can start drawing on a visible page that is
        // not the current one, and the preview must land where the ink will.
        double pageTop = ViewModel.SlotTopOf(previewPage);

        // The same loop the committed builder runs, and the same one the parity
        // harness fills its polyline with. The element is reused rather than
        // rebuilt, which is why this refills rather than returning a new one.
        Rendering.OverlayShapeBuilder.ProjectInto(
            _livePreviewStroke, points,
            scale: scale, pageTop: pageTop, view: previewView);
    }

    /// <summary>
    /// Kept as a wrapper so the four unrelated call sites that read a tool
    /// colour do not have to change; the parse itself moved with the builders
    /// that use it.
    /// </summary>
    private static Color ColorFromHex(string hex) =>
        Rendering.OverlayShapeBuilder.ColorFromHex(hex);

    // ---------------- Keyboard: Space = hand tool, track Ctrl for wheel-zoom ----------------

    /// <summary>
    /// True when a text field has focus, so canvas shortcuts must not fire.
    ///
    /// This handler sits on the root Grid and KeyDown BUBBLES, so without this
    /// check every keystroke typed into the search box also ran a canvas
    /// command: Backspace deleted the selected annotation (and marked the
    /// event handled, so it did not even edit the text), Space armed the hand
    /// tool, Ctrl+C copied the page selection instead of the field. Losing an
    /// annotation while correcting a typo is the worst of those, and it was
    /// silent.
    /// </summary>
    private bool IsTextInputFocused =>
        FocusManager.GetFocusedElement(XamlRoot) is TextBox or RichEditBox or AutoSuggestBox or PasswordBox;

    /// <summary>
    /// DIAGNOSTIC (temporary). Reports every key seen at RootGrid with the
    /// modifier state, whether it had already been Handled, and where focus
    /// is. Registered with handledEventsToo, so a key consumed upstream still
    /// shows up here - that is the difference between "not routed" and
    /// "swallowed", which is exactly what is in question for Ctrl+G.
    /// </summary>
    private void DiagKeyDownSpy(object sender, KeyRoutedEventArgs e)
    {
        // Modifier keys themselves would triple the noise for no information.
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
                  or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
                  or VirtualKey.Menu)
        {
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool ctrlNow = (ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down)
                       == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shiftNow = (shift & Windows.UI.Core.CoreVirtualKeyStates.Down)
                        == Windows.UI.Core.CoreVirtualKeyStates.Down;

        object? focused = null;
        try { focused = FocusManager.GetFocusedElement(this.XamlRoot); } catch { }

        Diag.Log($"KEYSPY key={e.Key} handledAlready={e.Handled} " +
                 $"_isCtrlDown={_isCtrlDown} ctrlNow={ctrlNow} shiftNow={shiftNow} " +
                 $"textFocused={IsTextInputFocused} focus={focused?.GetType().Name ?? "null"}");
    }

    /// <summary>Runs a command resolved from the keyboard. One place, so a
    /// chord and its menu item cannot drift apart.</summary>
    private void Run(EditorCommand command)
    {
        switch (command)
        {
            case EditorCommand.Open: OpenFile_Click(this, null!); break;
            case EditorCommand.Save: Save_Click(this, null!); break;
            case EditorCommand.Print: Print_Click(this, null!); break;
            case EditorCommand.SaveAs: SaveAs_Click(this, null!); break;
            case EditorCommand.Undo: ViewModel.Undo(); break;
            case EditorCommand.Redo: ViewModel.Redo(); break;
            case EditorCommand.Group: ViewModel.GroupSelected(); break;
            case EditorCommand.Ungroup: ViewModel.UngroupSelected(); break;
            case EditorCommand.ToggleRulers:
                RulersToggle.IsChecked = !RulersToggle.IsChecked;
                SetRulersVisible(RulersToggle.IsChecked);
                break;
            case EditorCommand.RotateViewClockwise: ViewModel.RotateViewClockwise(); break;
            case EditorCommand.RotateViewCounterClockwise: ViewModel.RotateViewCounterClockwise(); break;

            // Ctrl+N and Ctrl+W were declared as accelerators on their menu
            // items and nowhere else, so they had never worked: a
            // MenuFlyoutItem's accelerator is dead until its flyout has been
            // opened once. The menu keeps them, because that is what prints
            // the chord beside the entry; this is what runs them.
            case EditorCommand.New: New_Click(this, null!); break;
            case EditorCommand.CloseDocument: CloseDocument_Click(this, null!); break;

            case EditorCommand.NextTab: (App.Window as MainWindow)?.StepTab(1); break;
            case EditorCommand.PreviousTab: (App.Window as MainWindow)?.StepTab(-1); break;

            case EditorCommand.FindNext: ViewModel.StepSearchMatch(1); break;
            case EditorCommand.FindPrevious: ViewModel.StepSearchMatch(-1); break;

            case EditorCommand.GoToPage: FocusPageJumpBox(); break;

            // Z-order and duplicate act on a selection and do nothing without
            // one, which the view model already guards. The object toolbar
            // follows, because the selection it is drawn around has moved.
            case EditorCommand.BringForward: ViewModel.BringSelectedForward(); UpdateObjectToolbar(); break;
            case EditorCommand.SendBackward: ViewModel.SendSelectedBackward(); UpdateObjectToolbar(); break;
            case EditorCommand.BringToFront: ViewModel.BringSelectedToFront(); UpdateObjectToolbar(); break;
            case EditorCommand.SendToBack: ViewModel.SendSelectedToBack(); UpdateObjectToolbar(); break;
            case EditorCommand.Duplicate: ViewModel.DuplicateSelected(); UpdateObjectToolbar(); break;

            case EditorCommand.NavigateBack: GoBackInHistory(); break;
            case EditorCommand.NavigateForward: GoForwardInHistory(); break;

            // Not awaited: this switch is called from a key handler, and the
            // handler has to return so the key is marked handled.
            case EditorCommand.AddBookmark: _ = AddBookmarkHere(); break;
        }
    }

    /// <summary>
    /// Puts the caret in the page number box, selected, ready to be typed over.
    ///
    /// The box is already there in the status bar and already does the jump;
    /// what was missing was any way to reach it without the mouse. Selecting
    /// its contents means the reader types a number rather than clearing one
    /// first.
    /// </summary>
    private void FocusPageJumpBox()
    {
        if (ViewModel.PageCount == 0)
        {
            return;
        }

        PageJumpBox.Focus(FocusState.Programmatic);
        PageJumpBox.SelectAll();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // DIAGNOSTIC (temporary): does the REAL handler get this key at all?
        // The spy above sees everything; this line only fires for keys that
        // actually reach the switch, so the two together locate the loss.
        // Control is logged too: suppressing it hid whether the modifier was
        // even arriving, which is half of "Ctrl+G does nothing".
        Diag.Log($"KEYDOWN key={e.Key} handled={e.Handled} _isCtrlDown={_isCtrlDown} textFocused={IsTextInputFocused}");

        // Modifier state is tracked even while typing, or releasing Ctrl in a
        // text field would leave the canvas thinking it is still held and the
        // next wheel scroll would zoom instead of pan.
        switch (e.Key)
        {
            case VirtualKey.Control:
            case VirtualKey.LeftControl:
            case VirtualKey.RightControl:
                _isCtrlDown = true;
                return;
        }

        if (IsTextInputFocused)
        {
            // Escape leaves the field and hands the canvas back, which is the
            // one command worth honouring from inside a text box.
            if (e.Key == VirtualKey.Escape)
            {
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            // F3 is the exception to this early return, and the find box is
            // exactly where it will be pressed: the reader has just typed a
            // query and wants the next hit without leaving the field. It types
            // no character, so nothing is taken away from the box.
            var inField = KeyboardCommands.Resolve((int)e.Key, _isCtrlDown, IsShiftDown(), textFocused: false);
            if (inField is EditorCommand.FindNext or EditorCommand.FindPrevious)
            {
                Run(inField);
                e.Handled = true;
            }

            return;
        }

        // Full screen first, because Escape is shared. PresentationKeys only
        // claims Escape while already presenting, so every other time it falls
        // straight through to the canvas below and still cancels a drag,
        // dismisses the editor and clears the selection.
        if (App.Window is MainWindow window)
        {
            var presentation = PresentationKeys.Resolve(
                (int)e.Key, window.IsFullScreen, IsTextInputFocused);

            if (presentation != PresentationAction.None)
            {
                window.SetFullScreen(presentation == PresentationAction.Enter);
                e.Handled = true;
                return;
            }
        }

        // The menu-command chords resolve through KeyboardCommands, which is
        // tested. These were declared ONLY as accelerators on MenuFlyoutItems,
        // whose accelerators are not live until the flyout opens, so keyboard
        // undo had never worked and nothing could tell. The menu items keep
        // their accelerators because that is what prints "Ctrl+Z" beside the
        // entry; this is what actually runs them.
        // Alt is passed too, and this runs BEFORE the arrow-key cases below:
        // unmodified the arrows nudge a selection or scroll, so Alt+Left has to
        // be claimed here or it never reaches the resolver at all.
        var command = KeyboardCommands.Resolve(
            (int)e.Key, _isCtrlDown, IsShiftDown(), IsTextInputFocused, IsAltDown());
        if (command != EditorCommand.None)
        {
            Run(command);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space when !_isSpaceHandActive:
                _isSpaceHandActive = true;
                UpdateCursor();
                break;
            case VirtualKey.A when _isCtrlDown:
                // Not in KeyboardCommands, for the same reason Ctrl+C is not:
                // in a text field Ctrl+A belongs to the field, and whether this
                // counts as handled depends on what has focus.
                if (!IsTextInputFocused && ViewModel.SelectAllOnPage())
                {
                    UpdateObjectToolbar();
                    e.Handled = true;
                }
                break;
            case VirtualKey.C when _isCtrlDown:
                // Selected annotation wins over selected text: if the user has
                // a shape or text box picked, they mean copy IT, not whatever
                // text underneath happens to be highlighted.
                if (!ViewModel.CopySelectedAnnotations()) { CopySelectedText(); }
                e.Handled = true;
                break;
            case VirtualKey.X when _isCtrlDown:
                if (ViewModel.CutSelectedAnnotations()) { e.Handled = true; }
                break;
            case VirtualKey.V when _isCtrlDown:
                if (ViewModel.PasteAnnotations()) { e.Handled = true; }
                break;
            // Ctrl+Shift+] used to be handled here alone. It now resolves
            // through KeyboardCommands with the other three z-order chords,
            // which runs above this switch, so this case had become
            // unreachable. Removed rather than left as a second declaration of
            // the same key, which is the shape that let Ctrl+Z sit dead for
            // months.
            case VirtualKey.F when _isCtrlDown:
                // Opens the find controls first: they are collapsed until
                // wanted, so focusing the box without showing it would put the
                // caret somewhere invisible.
                SetFindOpen(true);
                e.Handled = true;
                break;


            // NOT Tab. Tab is the focus-traversal key, and binding it here
            // meant keyboard users could never move focus anywhere in the app.
            // It also fired from any stray Tab the app received, including one
            // that arrived while a system dialog was being dismissed, which is
            // how the pages panel came to be open on launch.
            case VirtualKey.F4:
                ToggleThumbnails();
                e.Handled = true;
                break;

            case VirtualKey.F6:
                ToggleBookmarks();
                e.Handled = true;
                break;

            // Single-key tool switching is handled below, from the catalog,
            // rather than as cases here. A switch listing them meant the key
            // and the tool were declared apart from each other, and the Stamp
            // tool was simply forgotten when it was added.

            // Acrobat's zoom keys: Ctrl+0 fit page, Ctrl+1 actual size,
            // Ctrl+2 fit width. Matching them means zoom muscle memory from
            // Acrobat transfers wholesale.
            case VirtualKey.Number0 when _isCtrlDown:
                ZoomFitPage_Click(this, null!);
                e.Handled = true;
                break;
            case VirtualKey.Number1 when _isCtrlDown:
                ZoomActualSize_Click(this, null!);
                e.Handled = true;
                break;
            case VirtualKey.Number2 when _isCtrlDown:
                ZoomFitWidth_Click(this, null!);
                e.Handled = true;
                break;

            // Arrow keys: if an annotation is selected they NUDGE it (Illustrator
            // / Word style, expected keyboard polish). Only when nothing is
            // selected do they fall back to the Acrobat-style scroll nudge, so
            // the keyboard can still move within a page when no object is picked.
            // Shift-arrow multiplies the step, matching every editor's "bigger
            // nudge" convention.
            case VirtualKey.Down:
            case VirtualKey.Up:
                if (ViewModel.HasSelectedAnnotationLoaded)
                {
                    double stepD = IsShiftDown() ? BigNudgeStep : SmallNudgeStep;
                    ViewModel.NudgeSelected(0, e.Key == VirtualKey.Down ? stepD : -stepD);
                }
                else
                {
                    ScrollBy(0, e.Key == VirtualKey.Down ? ArrowScrollStep : -ArrowScrollStep);
                }
                e.Handled = true;
                break;
            case VirtualKey.Right:
            case VirtualKey.Left:
                if (ViewModel.HasSelectedAnnotationLoaded)
                {
                    double stepR = IsShiftDown() ? BigNudgeStep : SmallNudgeStep;
                    ViewModel.NudgeSelected(e.Key == VirtualKey.Right ? stepR : -stepR, 0);
                }
                else
                {
                    ScrollBy(e.Key == VirtualKey.Right ? ArrowScrollStep : -ArrowScrollStep, 0);
                }
                e.Handled = true;
                break;

            case VirtualKey.Home:
                ViewModel.GoToPage(0);
                e.Handled = true;
                break;
            case VirtualKey.End:
                ViewModel.GoToPage(ViewModel.PageCount - 1);
                e.Handled = true;
                break;

            // Page navigation, all animated: a jump that teleports gives no
            // sense of where you moved to, which in a continuous document is
            // most of how you stay oriented.
            case VirtualKey.PageDown:
                ViewModel.GoToPage(ViewModel.CurrentPageIndex + 1);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                ViewModel.GoToPage(ViewModel.CurrentPageIndex - 1);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                // Selected guide wins over selected annotation: a user who
                // just clicked a guide expects Delete to remove THAT.
                if (!ViewModel.DeleteSelectedGuide())
                {
                    ViewModel.DeleteSelectedAnnotation();
                }
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                ViewModel.ClearAnnotationSelection();
                e.Handled = true;
                break;
        }

        // Single-key tool switching, Photoshop-style, straight from the
        // catalog, so a tool's key is declared next to the tool itself. Last,
        // and only for keys nothing above claimed, so a shortcut can never
        // shadow a real command.
        if (!e.Handled && !_isCtrlDown
            && ToolCatalog.ForShortcut((char)e.Key) is { } picked)
        {
            SetActiveTool(picked.Mode);
            e.Handled = true;
        }
    }

    private void CopySelectedText()
    {
        var text = ViewModel.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void RootGrid_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            // Released unconditionally, including from a text field: the hand
            // tool must never stay armed because focus moved mid-gesture.
            case VirtualKey.Space:
                _isSpaceHandActive = false;
                UpdateCursor();
                break;
            case VirtualKey.Control:
            case VirtualKey.LeftControl:
            case VirtualKey.RightControl:
                _isCtrlDown = false;
                break;
        }
    }

    /// <summary>
    /// The cursor says what the current tool will do. An arrow over every
    /// tool leaves the pointer lying about the mode it is in, which is the
    /// main feedback a canvas app has between clicks.
    /// </summary>
    /// <summary>
    /// Cursor for what lies under the pointer, or null to use the tool's own.
    ///
    /// Resize handles need this: without it the pointer keeps the tool's
    /// cursor over them, nothing suggests they can be dragged, and the whole
    /// feature reads as broken even though it works.
    /// </summary>
    private InputSystemCursorShape? HoverCursor(PointerRoutedEventArgs e)
    {
        // The hand overrides everything - never override the pan cursor. This
        // covers the Hand TOOL as well as Space, because press routing below
        // does: with the hand armed a drag pans, so offering a resize or
        // guide-move cursor would advertise a gesture that cannot happen.
        if (!ToolWantsPointer) { return null; }

        var content = ContentPoint(e);
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

        // Guides can be picked and dragged under ANY tool, so the hover
        // cursor for them ignores the Select-tool gate below. SizeAll is
        // the standard "move me" cursor - what Illustrator shows over a
        // guide it can pick up.
        if (ViewModel.PickGuideAt(content.Page, nx, ny) is not null)
        {
            return InputSystemCursorShape.SizeAll;
        }

        if (ViewModel.ActiveTool != ToolMode.Select)
        {
            return null;
        }

        return ViewModel.GripUnder(content.Page, nx, ny) switch
        {
            // Diagonals matching the corner, as every editor does, so the
            // cursor says which way the drag will go.
            LoadedAnnotationPicker.Grip.TopLeft or LoadedAnnotationPicker.Grip.BottomRight
                => InputSystemCursorShape.SizeNorthwestSoutheast,
            LoadedAnnotationPicker.Grip.TopRight or LoadedAnnotationPicker.Grip.BottomLeft
                => InputSystemCursorShape.SizeNortheastSouthwest,
            LoadedAnnotationPicker.Grip.Top or LoadedAnnotationPicker.Grip.Bottom
                => InputSystemCursorShape.SizeNorthSouth,
            LoadedAnnotationPicker.Grip.Left or LoadedAnnotationPicker.Grip.Right
                => InputSystemCursorShape.SizeWestEast,
            // No dedicated rotate cursor in the platform set; the hand reads as
            // "grab this to turn it".
            LoadedAnnotationPicker.Grip.Rotate => InputSystemCursorShape.Hand,
            _ => ViewModel.IsOverSelection(content.Page, nx, ny)
                ? InputSystemCursorShape.SizeAll
                : null,
        };
    }

    private void UpdateCursor(PointerRoutedEventArgs? e = null)
    {
        if (e is not null && HoverCursor(e) is InputSystemCursorShape hover)
        {
            Apply(hover, () => InputSystemCursor.Create(hover));
            return;
        }

        var wanted = CursorPolicy.Resolve(ViewModel.ActiveTool, _isSpaceHandActive, _isPanning);
        Apply(wanted, () => AppCursors.Create(wanted));
    }

    /// <summary>
    /// Assigns the cursor only when it actually changes.
    ///
    /// UpdateCursor runs on every PointerMoved, so without this the viewport
    /// built and handed over a cursor object dozens of times a second. The key
    /// is the boxed enum the cursor was chosen from - either a
    /// <see cref="ViewportCursor"/> or an <see cref="InputSystemCursorShape"/>,
    /// which never collide because they are different types.
    ///
    /// Reusing one cursor INSTANCE instead would look like the obvious saving
    /// and is the thing to avoid: assigning to ProtectedCursor hands a
    /// disposable object to the framework, and the hand tool showed nothing at
    /// all until every assignment got a freshly made cursor.
    /// </summary>
    private void Apply(object key, Func<InputCursor> make)
    {
        if (Equals(_appliedCursorKey, key))
        {
            return;
        }

        _appliedCursorKey = key;
        ViewportHost.SetCursor(make());
    }

    // ---------------- Tool gestures ----------------
    //
    // ScrollView owns panning and zooming entirely, including middle-drag,
    // Ctrl+wheel, pinch and inertia, so none of that is handled here any
    // more. What remains is only the tool gestures, and because they are
    // measured against PageImage the coordinates are already page-bitmap
    // pixels: ScrollView has applied its own transform by then, so the old
    // manual inverse-transform is gone.

    /// <summary>
    /// Pointer position as a point local to the page under it, in slot space.
    ///
    /// ViewportHost is the ScrollView's content, so a point measured against
    /// it is already UNZOOMED: the ScrollView applies zoom above this, which
    /// is exactly why slot space is worth keeping. Subtracting the host's
    /// padding lands on the page stack, and the view model resolves which page
    /// that is. Picking the page here, rather than trusting whatever the
    /// scroll position last made current, is what lets a drag on page 7 edit
    /// page 7.
    /// </summary>
    private readonly record struct PagePoint(int Page, double X, double Y);

    private PagePoint ContentPoint(PointerRoutedEventArgs e) =>
        ContentPointAt(e.GetCurrentPoint(ViewportHost).Position);

    private PagePoint ContentPointAt(Point p)
    {
        double slotX = p.X - ViewportHost.Padding.Left;
        double slotY = p.Y - ViewportHost.Padding.Top;

        return ViewModel.HitTestSlotSpace(slotX, slotY, out int pageIndex, out double localX, out double localY)
            ? new PagePoint(pageIndex, localX, localY)
            : new PagePoint(ViewModel.CurrentPageIndex, slotX, slotY);
    }

    /// <summary>
    /// Double-click a text box to edit its words again.
    ///
    /// The one gesture that makes placed text modifiable rather than frozen.
    /// Only a loaded TEXT box responds; a shape or highlight under the pointer
    /// returns null and the double-click does nothing, leaving select-and-move
    /// as the way to handle those.
    /// </summary>
    private void ViewportHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.ActiveTool is not (ToolMode.Select or ToolMode.Text))
        {
            return;
        }

        var content = ContentPointAt(e.GetPosition(ViewportHost));
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

        if (ViewModel.HitLoadedTextBox(content.Page, nx, ny) is not ViewportViewModel.TextBoxEditTarget target)
        {
            return;
        }

        if (OpenTextBoxEditor(target))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the editor on a text box that has already been hit-tested.
    ///
    /// Shared by the double-click gesture and by the context menu's Edit text,
    /// which are one operation reached two ways. The dozen lines below are the
    /// ones that make a re-edit faithful rather than approximate - the font is
    /// re-embedded, the decorations survive, the box comes off the page before
    /// the editor goes over it - and a second copy of them would have drifted
    /// from this one within a release.
    /// </summary>
    /// <returns>True when the editor actually opened.</returns>
    private bool OpenTextBoxEditor(ViewportViewModel.TextBoxEditTarget target)
    {
        // The preceding clicks may have started a select-and-move; abandon it,
        // and drop the marquee, so the box is edited rather than dragged.
        ResetPointerInteraction();
        ViewModel.ClearAnnotationSelection();

        // The tool takes on the box's colour and size, so the property bar shows
        // what is being edited and the rewritten box keeps them unless changed.
        ViewModel.InkColorHex = target.ColorHex;
        ViewModel.TextFontSize = target.FontSizeNorm;
        ViewModel.TextAlign = target.Align;
        ViewModel.TextFillHex = target.FillHex;
        ViewModel.TextOutlineHex = target.OutlineHex;
        if (target.OutlineWidthNorm > 0)
        {
            ViewModel.TextOutlineWidthNorm = target.OutlineWidthNorm;
        }

        // Restore the font the box was drawn in and its decorations, so
        // re-committing re-embeds the SAME font (a Burmese/Hindi box would
        // otherwise fall back to the default and break) and the toolbar shows
        // what is being edited. Set before UpdateToolRail below, which syncs the
        // style buttons from these.
        ViewModel.RestoreTextFont(target.FontPath);
        ViewModel.TextUnderline = target.Underline;
        ViewModel.TextStrikethrough = target.Strikethrough;

        // Switch to the Text tool so the property bar shows the font/size/style
        // controls while the box is being edited. Re-editing used to leave the
        // previous tool active, so the toolbar had nothing to say and vanished.
        ViewModel.ActiveTool = ToolMode.Text;
        UpdateToolRail();

        // Remove the box from the page FIRST, so the editor is the only layer.
        // Without this the rendered box stayed under the editor, which is the
        // "two layers" the edit showed.
        if (!ViewModel.BeginLoadedTextBoxEdit(target.PageIndex, target.Index))
        {
            return false;
        }

        BeginTextEdit(target.PageIndex, target.Left, target.Top, target.Right, target.Bottom,
                      initialText: target.Text, editing: target);
        return true;
    }

    // ---------------- Right-click menu ----------------

    /// <summary>Where the last right-click landed, in normalized page-local
    /// coordinates. Edit text needs it after the fact: the command runs once
    /// the menu has closed, and the box it opens is the one that was under the
    /// cursor when the menu was asked for, not whatever is under it now.</summary>
    private (int Page, double X, double Y) _contextPoint;

    /// <summary>
    /// Right-click: select what is under the pointer, then offer what applies
    /// to it.
    ///
    /// Under EVERY tool, not just Select. Right-click is not a drawing gesture
    /// in any of them, so there is nothing to conflict with, and the moment a
    /// user most wants to delete the stroke they just drew is while the pen is
    /// still armed.
    /// </summary>
    private void ViewportHost_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Nothing to talk about with no document, and nothing to interrupt a
        // gesture already in flight or a text box mid-edit: a menu appearing
        // over either would act on a selection that is still being changed.
        if (ViewModel.PageCount == 0 || _textEditor is not null
            || _isPanning || _isMovingAnnotation || _isMarqueeing || _isSelectingText
            || _isDrawing || _isDrawingShape || _isSizingText)
        {
            return;
        }

        var content = ContentPointAt(e.GetPosition(ViewportHost));
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;
        _contextPoint = (content.Page, nx, ny);

        // A right-click ON the selection leaves it alone; anywhere else re-picks
        // what is under the pointer. That is the convention every editor uses,
        // and the reason for the first half is Group: without it, right-clicking
        // one of three selected objects would collapse the selection to that one
        // and then offer a greyed-out Group.
        bool onObject = ViewModel.IsOverSelectedObject(content.Page, nx, ny)
                     || ViewModel.SelectAnnotationAt(content.Page, nx, ny);

        var items = ContextMenuModel.For(new ContextTarget
        {
            DocumentOpen = true,
            OnObject = onObject,
            SelectionCount = ViewModel.SelectionCount,
            CanUngroup = ViewModel.CanUngroupSelection,
            IsTextBox = ViewModel.HasSelectedTextBox,
            ClipboardHasContent = ViewModel.HasClipboardContent,
        });

        if (items.Count == 0)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var row = new MenuFlyoutItem { Text = item.Label, IsEnabled = item.Enabled };
            if (item.Accelerator.Length > 0)
            {
                // Text only. A real KeyboardAccelerator here would be dead
                // weight: accelerators declared on flyout items are not live
                // until the flyout is open, which is the trap that left Ctrl+Z
                // unimplemented for months. The window's key handler owns these
                // chords, and ContextMenuModel's tests hold this column to what
                // that handler really listens for.
                row.KeyboardAcceleratorTextOverride = item.Accelerator;
            }

            var command = item.Command;
            row.Click += (_, _) => RunContextCommand(command);
            flyout.Items.Add(row);
        }

        // The floating object toolbar is anchored to the selection, which is
        // exactly where this menu opens, so it would sit under it. Hide it for
        // as long as the menu is up.
        _objectToolbarSuppressed = true;
        UpdateObjectToolbar();
        flyout.Closed += (_, _) =>
        {
            _objectToolbarSuppressed = false;
            UpdateObjectToolbar();
        };

        // Anchored to the SCROLLER, not to the content under the pointer. The
        // content is panned and zoomed by the compositor, so a position measured
        // in its space is a position in a frame that moves independently of the
        // window; the scroller's own space is the viewport, one pixel per pixel,
        // whatever the page is doing inside it.
        flyout.ShowAt(PageScroller, new FlyoutShowOptions { Position = e.GetPosition(PageScroller) });
        e.Handled = true;
    }

    /// <summary>
    /// Runs a menu row. Every arm calls the same method the toolbar button or
    /// the keyboard chord calls, so the menu is a second door onto the existing
    /// commands rather than a second implementation of them.
    /// </summary>
    private void RunContextCommand(ContextCommand command)
    {
        switch (command)
        {
            case ContextCommand.EditText:
                if (ViewModel.HitLoadedTextBox(_contextPoint.Page, _contextPoint.X, _contextPoint.Y)
                    is ViewportViewModel.TextBoxEditTarget target)
                {
                    OpenTextBoxEditor(target);
                }
                break;

            case ContextCommand.Cut: ViewModel.CutSelectedAnnotations(); break;
            case ContextCommand.Copy: ViewModel.CopySelectedAnnotations(); break;
            case ContextCommand.Paste: ViewModel.PasteAnnotations(); break;
            case ContextCommand.Delete: ViewModel.DeleteSelectedAnnotation(); break;

            case ContextCommand.BringToFront: ViewModel.BringSelectedToFront(); break;
            case ContextCommand.BringForward: ViewModel.BringSelectedForward(); break;
            case ContextCommand.SendBackward: ViewModel.SendSelectedBackward(); break;
            case ContextCommand.SendToBack: ViewModel.SendSelectedToBack(); break;

            case ContextCommand.Group: ViewModel.GroupSelected(); break;
            case ContextCommand.Ungroup: ViewModel.UngroupSelected(); break;

            case ContextCommand.SelectAllOnPage: ViewModel.SelectAllOnPage(); break;
            case ContextCommand.RotatePage: ViewModel.RotateCurrentPage(90); break;
        }

        UpdateObjectToolbar();
    }

    private bool ToolWantsPointer =>
        !_isSpaceHandActive && ViewModel.ActiveTool != ToolMode.Hand;

    private void ViewportHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A click on the page hands the keyboard back to the canvas.
        //
        // RootGrid_KeyDown drops every key while a text field has focus, which
        // is what stops Backspace deleting an annotation while someone corrects
        // a typo. The find box is a text field, and nothing ever took focus off
        // it: not Enter, which steps through matches and has to stay repeatable,
        // and not clicking the page. So after using Find, Ctrl+C, Ctrl+V and
        // Delete were all silently dead on the canvas until the user happened to
        // press Escape, which the key handler documents as the only way out.
        //
        // Clicking the page is the unambiguous signal that they have moved on
        // from the box. The page-jump field gets the same treatment for free,
        // since the condition is about text focus rather than about Find.
        //
        // NOT while an in-place text editor is open: that editor IS the text
        // field in use, and taking its focus would commit the edit on the very
        // click meant to place the caret in it.
        if (_textEditor is null && IsTextInputFocused)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }

        var current = e.GetCurrentPoint(ViewportHost);
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;   // let ScrollView handle touch/pen pan + pinch
        }

        // Get the floating toolbar out of the way for the whole gesture. It is
        // anchored to the selection, so left up it would slide around under the
        // cursor mid-drag and could end up beneath the pointer.
        _objectToolbarSuppressed = true;
        UpdateObjectToolbar();

        if (!current.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Form fill mode intercepts a click on a fillable field and opens the
        // text editor on it, whatever tool is active. A click that misses every
        // field falls through to the normal handling below (pan/select), so the
        // user can still move around the page.
        if (ViewModel.FormFillMode)
        {
            var fc = ContentPoint(e);
            double fnx = fc.X / ViewModel.OverlayScale;
            double fny = fc.Y / ViewModel.OverlayScale;
            if (ViewModel.FillableFieldAt(fc.Page, fnx, fny) is { } field)
            {
                BeginFormFieldEdit(fc.Page, field);
                e.Handled = true;
                return;
            }
        }

        // Hand tool and Space-hand pan by dragging.
        //
        // This used to just leave the event unhandled "so ScrollView pans on
        // the compositor", which was wrong: ScrollView pans by drag for TOUCH
        // and pen, not for a left mouse drag, so with a mouse the event was
        // simply dropped and the hand tool never moved anything. Panning is
        // driven explicitly below instead.
        if (!ToolWantsPointer)
        {
            _isPanning = true;
            _dragPointerId = current.PointerId;
            // Measured against the SCROLLER, not the content: content moves as
            // we scroll it, so measuring there would feed the scroll back into
            // the delta and the page would run away from the pointer.
            _panLastPoint = e.GetCurrentPoint(PageScroller).Position;
            _panTarget = new Point(PageScroller.HorizontalOffset, PageScroller.VerticalOffset);
            ViewportHost.CapturePointer(e.Pointer);
            // Close the hand. Nothing else recomputes the cursor between here
            // and the release: PointerMoved deliberately skips it while a drag
            // is running, so without this the hand never shuts.
            UpdateCursor();
            e.Handled = true;
            return;
        }

        var content = ContentPoint(e);

        // Normalized page-local coordinates, which is the space annotations
        // live in.
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

        // A waiting signature takes the click before any tool sees it, whatever
        // tool is armed: the user picked it from a menu and is now pointing at
        // where it goes, so the pen or the select tool acting instead would be
        // the wrong answer to a question they already asked.
        if (TryPlacePendingSignature(content.Page, nx, ny))
        {
            UpdateObjectToolbar();
            e.Handled = true;
            return;
        }

        Diag.Log($"press tool={ViewModel.ActiveTool} raw=({e.GetCurrentPoint(ViewportHost).Position.X:F0},{e.GetCurrentPoint(ViewportHost).Position.Y:F0}) " +
                 $"page={content.Page} local=({content.X:F1},{content.Y:F1}) norm=({nx:F3},{ny:F3})");

        // Guide hit-test comes FIRST, regardless of tool, because a guide
        // sits ON TOP of whatever else is there and Illustrator/PageMaker
        // both let you grab a guide with any tool active. A hit selects the
        // guide (highlighted red), captures the pointer, and starts a
        // drag-move; a miss falls through so the click reaches the normal
        // tool path. Any other press clears the guide selection so the
        // highlight doesn't linger.
        if (ViewModel.PickGuideAt(content.Page, nx, ny) is { } guideHit)
        {
            ViewModel.SelectGuide(content.Page, guideHit);
            ViewModel.BeginGuideDrag();
            _dragPointerId = current.PointerId;
            ViewportHost.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        else
        {
            ViewModel.ClearGuideSelection();
        }

        switch (ViewModel.ActiveTool)
        {
            case ToolMode.Select:
                // A click on an existing mark picks it up; a click on empty
                // space falls through to text selection. That is what makes
                // annotations objects rather than paint.
                if (ViewModel.SelectAnnotationAt(content.Page, nx, ny))
                {
                    // Ctrl-drag clones the picked mark IN PLACE; the drag that
                    // follows moves the clone, so the original stays put. Every
                    // editor uses this convention (Word, Illustrator, PowerPoint).
                    // The clone becomes the new anchor via DuplicateSelectedForDrag,
                    // so the rest of the drag path applies unchanged.
                    var ctrlState = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
                    bool ctrl = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down)
                                == Windows.UI.Core.CoreVirtualKeyStates.Down;
                    if (ctrl)
                    {
                        ViewModel.DuplicateSelectedForDrag();
                    }

                    _isMovingAnnotation = true;
                    _dragPointerId = current.PointerId;
                    ViewportHost.CapturePointer(e.Pointer);
                    e.Handled = true;
                    break;
                }

                // Empty-area drag with Shift = SELECTION MARQUEE: draw a
                // rectangle over the annotations, on release every one it
                // touches is added to the multi-selection (the extras). Plain
                // drag still starts text selection so people can pick page text
                // the way they always could. The marquee reuses the same
                // preview overlay the highlight-tool marquee uses.
                var shiftState = Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool shiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down)
                                 == Windows.UI.Core.CoreVirtualKeyStates.Down;
                if (shiftDown)
                {
                    _isMarqueeing = true;
                    _isAnnotationMarquee = true;
                    _dragPointerId = current.PointerId;
                    ViewportHost.CapturePointer(e.Pointer);
                    ViewModel.BeginSelectionMarquee(content.Page, content.X, content.Y);
                    e.Handled = true;
                    break;
                }

                _isSelectingText = true;
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);
                ViewModel.BeginTextSelection(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Highlight:
                ViewModel.ClearAnnotationSelection();
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);

                // A scanned page has no characters to select, so highlighting
                // by text run is impossible there. Marking a REGION still is,
                // and is what a reader wants, so the tool falls back to a
                // rectangular marquee rather than silently doing nothing.
                if (ViewModel.PageHasText(content.Page))
                {
                    _isSelectingText = true;
                    ViewModel.BeginTextSelection(content.Page, content.X, content.Y);
                }
                else
                {
                    _isMarqueeing = true;
                    ViewModel.BeginMarquee(content.Page, content.X, content.Y);
                }

                e.Handled = true;
                break;

            case ToolMode.Draw:
                _isDrawing = true;
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);
                ViewModel.BeginInkStroke(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Shape:
                _isDrawingShape = true;
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);
                ViewModel.BeginShape(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Text:
                // Drag out the box; the editor opens at the size on release. A
                // plain click (no drag) still works, via a default width below.
                _isSizingText = true;
                _dragPointerId = current.PointerId;
                _textDragPage = content.Page;
                _textDragStartX = nx;
                _textDragStartY = ny;
                BeginTextBoxPreview(content.Page, nx, ny);
                ViewportHost.CapturePointer(e.Pointer);
                e.Handled = true;
                break;

            case ToolMode.Note:
                ViewModel.AddNoteAt(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Stamp:
                // Fire and forget: decoding is async, and a pointer handler
                // cannot await without letting the gesture continue underneath
                // it. Nothing later in this handler depends on the result.
                _ = PlaceSelectedStampAsync(content.Page, content.X, content.Y);
                e.Handled = true;
                break;
        }
    }

    private void ViewportHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Cursor first, and BEFORE the drag guard below. That guard only lets
        // through the pointer that is mid-drag, and a hovering pointer has no
        // drag id, so putting the cursor update after it meant the resize
        // handles never changed the cursor at all.
        if (!_isPanning && !_isMovingAnnotation && !_isMarqueeing
            && !_isSelectingText && !_isDrawing)
        {
            UpdateCursor(e);
        }

        if (e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        var content = ContentPoint(e);

        // Guide drag: whatever tool is armed, if a guide is being moved,
        // update its position from the pointer's page-local coords. The
        // capture keeps events flowing here even after the pointer leaves
        // the page onto a ruler (that's what makes drag-off-to-delete work
        // at release).
        if (ViewModel.IsDraggingGuide)
        {
            double gnx = content.X / ViewModel.OverlayScale;
            double gny = content.Y / ViewModel.OverlayScale;
            ViewModel.DragGuideTo(content.Page, gnx, gny);
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            var now = e.GetCurrentPoint(PageScroller).Position;

            // Accumulate against our OWN target, not the scroller's reported
            // offset. ScrollTo is asynchronous, so the reported offset still
            // lags the previous call when the next move arrives; computing
            // from it discards part of every delta and the page drifts behind
            // the pointer, which is what made panning feel sticky. Tracking
            // the intended position means every pixel of pointer movement is
            // applied exactly once.
            _panTarget = new Point(
                _panTarget.X - (now.X - _panLastPoint.X),
                _panTarget.Y - (now.Y - _panLastPoint.Y));

            PageScroller.ScrollTo(
                Math.Clamp(_panTarget.X, 0, Math.Max(0, PageScroller.ExtentWidth * PageScroller.ZoomFactor - PageScroller.ViewportWidth)),
                Math.Clamp(_panTarget.Y, 0, Math.Max(0, PageScroller.ExtentHeight * PageScroller.ZoomFactor - PageScroller.ViewportHeight)),
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));

            _panLastPoint = now;
            e.Handled = true;
            return;
        }

        if (_isMovingAnnotation)
        {
            ViewModel.MoveSelectedAnnotationTo(
                content.X / ViewModel.OverlayScale,
                content.Y / ViewModel.OverlayScale);
            e.Handled = true;
        }
        else if (_isMarqueeing)
        {
            ViewModel.UpdateMarquee(content.X, content.Y);
            e.Handled = true;
        }
        else if (_isSelectingText)
        {
            ViewModel.UpdateTextSelection(content.Page, content.X, content.Y);
            e.Handled = true;
        }
        else if (_isDrawing)
        {
            ViewModel.ExtendInkStroke(content.X, content.Y);
            e.Handled = true;
        }
        else if (_isDrawingShape)
        {
            // Shift held during a shape draw constrains the endpoint:
            // rect/ellipse becomes a square/circle, line/arrow snaps to 45°.
            ViewModel.ExtendShape(content.X, content.Y, constrain: IsShiftDown());
            e.Handled = true;
        }
        else if (_isSizingText)
        {
            UpdateTextBoxPreview(content.X / ViewModel.OverlayScale, content.Y / ViewModel.OverlayScale);
            e.Handled = true;
        }
    }

    private void ViewportHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Lifted BEFORE the pointer-id check. A press that never became a drag
        // still suppressed the toolbar, and bailing out below would leave it
        // hidden until the next selection change.
        _objectToolbarSuppressed = false;
        UpdateObjectToolbar();

        if (e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        // Guide drag release: if the pointer landed OFF the page (on the
        // ruler bar area, above/beside the page), delete the guide - that's
        // the drag-off-to-delete convention every editor with guides uses.
        // Otherwise the position from the last DragGuideTo is kept.
        if (ViewModel.IsDraggingGuide)
        {
            var pInHost = e.GetCurrentPoint(ViewportHost).Position;
            double slotX = pInHost.X - ViewportHost.Padding.Left;
            double slotY = pInHost.Y - ViewportHost.Padding.Top;
            bool offPage = slotX < 0 || slotY < 0 || ViewModel.PageAt(slotY) < 0;
            ViewModel.EndGuideDrag(offPage);
            ViewportHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            UpdateCursor();   // and open it again
            e.Handled = true;
        }
        else if (_isMovingAnnotation)
        {
            _isMovingAnnotation = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndAnnotationMove();
            e.Handled = true;
        }
        else if (_isMarqueeing)
        {
            bool wasAnnotation = _isAnnotationMarquee;
            _isMarqueeing = false;
            _isAnnotationMarquee = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            if (wasAnnotation)
            {
                ViewModel.EndSelectionMarquee();
            }
            else
            {
                ViewModel.EndMarquee();
            }
            e.Handled = true;
        }
        else if (_isSelectingText)
        {
            _isSelectingText = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndTextSelection();
            e.Handled = true;
        }
        else if (_isDrawing)
        {
            _isDrawing = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndInkStroke();
            e.Handled = true;
        }
        else if (_isDrawingShape)
        {
            _isDrawingShape = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndShape();
            e.Handled = true;
        }
        else if (_isSizingText)
        {
            _isSizingText = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            var end = ContentPoint(e);
            EndTextBoxSizing(end.X / ViewModel.OverlayScale, end.Y / ViewModel.OverlayScale);
            e.Handled = true;
        }
    }

    // ---------------- Text box sizing (drag to set width) ----------------

    private bool _isSizingText;
    private int _textDragPage;
    private double _textDragStartX;   // normalized
    private double _textDragStartY;   // normalized
    private Rectangle? _textBoxPreview;

    /// <summary>A drag shorter than this is treated as a click: a default box.</summary>
    private const double MinTextDrag = 0.02;

    /// <summary>Default box width, as a fraction of page width, for a plain click.</summary>
    private const double DefaultTextWidth = 0.42;

    private void BeginTextBoxPreview(int page, double nx, double ny)
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _textBoxPreview = new Rectangle
        {
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x78, 0xD4)),
            IsHitTestVisible = false,
        };

        var box = CardRect(page, nx, ny, nx, ny);
        Canvas.SetLeft(_textBoxPreview, box.Left);
        Canvas.SetTop(_textBoxPreview, box.Top);
        InkCanvas.Children.Add(_textBoxPreview);
    }

    /// <summary>
    /// A normalized rectangle on a page, as a slot-space rectangle on its card.
    ///
    /// Both opposite corners go through the same ToCard the strokes use, and
    /// the bounds are taken AFTERWARDS. Taking them first, in normalized space,
    /// is what the preview used to do, and it is wrong the moment the page is
    /// turned: the corner that was top-left is not top-left any more.
    ///
    /// Every view rotation is a quarter turn, so an axis-aligned rectangle maps
    /// to an axis-aligned rectangle exactly and nothing needs a RenderTransform;
    /// at 90 and 270 the width and height simply swap.
    /// </summary>
    private (double Left, double Top, double Width, double Height) CardRect(
        int page, double nx1, double ny1, double nx2, double ny2)
    {
        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(page);
        var view = ViewModel.ViewTransformOf(page);

        var (ax, ay) = view.ToCard(nx1 * scale, ny1 * scale);
        var (bx, by) = view.ToCard(nx2 * scale, ny2 * scale);

        return (System.Math.Min(ax, bx),
                System.Math.Min(ay, by) + pageTop,
                System.Math.Abs(bx - ax),
                System.Math.Abs(by - ay));
    }

    private void UpdateTextBoxPreview(double nx, double ny)
    {
        if (_textBoxPreview is null)
        {
            return;
        }

        var box = CardRect(_textDragPage, _textDragStartX, _textDragStartY, nx, ny);
        Canvas.SetLeft(_textBoxPreview, box.Left);
        Canvas.SetTop(_textBoxPreview, box.Top);
        _textBoxPreview.Width = box.Width;
        _textBoxPreview.Height = box.Height;
    }

    private void EndTextBoxSizing(double nx, double ny)
    {
        if (_textBoxPreview is not null)
        {
            InkCanvas.Children.Remove(_textBoxPreview);
            _textBoxPreview = null;
        }

        double left = System.Math.Min(_textDragStartX, nx);
        double top = System.Math.Min(_textDragStartY, ny);
        double right = System.Math.Max(_textDragStartX, nx);
        double bottom = System.Math.Max(_textDragStartY, ny);

        // A click, or a drag too small to be a deliberate box, opens a
        // default-width box anchored where the pointer went down. The height is
        // only a minimum; the box grows to fit what is typed.
        if (right - left < MinTextDrag)
        {
            left = _textDragStartX;
            top = _textDragStartY;
            right = System.Math.Min(1.0, left + DefaultTextWidth);
            bottom = top + (ViewModel.TextFontSize * TextBoxPlacement.LineHeight) + (2 * ViewModel.TextFontSize * TextBoxPlacement.Padding);
        }

        BeginTextEdit(_textDragPage, left, top, right, bottom);
    }

    // ---------------- Thumbnail sidebar ----------------

    /// <summary>
    /// Fires the first time a thumbnail's Image container is realized.
    /// ListView only realizes containers for items actually on screen (plus
    /// a small buffer), so this is what makes thumbnail rendering
    /// virtualized: a 300-page document never renders more than a handful.
    /// </summary>
    /// <summary>
    /// The virtualization hook for thumbnail rendering.
    ///
    /// This must NOT be an Image.Loaded handler reading DataContext, which is
    /// what it used to be. ListView recycles containers, and on a recycled
    /// container Loaded fires BEFORE the item is bound, so DataContext is
    /// still null and the render is never requested. Freshly created
    /// containers happened to work, which is why thumbnails appeared on the
    /// document opened at startup but never on one opened via File > Open:
    /// by then every container was a recycled one.
    ///
    /// ContainerContentChanging is the purpose-built callback. It hands over
    /// the item directly, and it fires on every bind and rebind.
    /// </summary>
    private void ThumbnailList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        if (args.Item is PageThumbnail thumbnail)
        {
            ViewModel.EnsureThumbnailRendered(thumbnail.PageIndex);
        }
    }

    /// <summary>
    /// Navigates to a clicked thumbnail.
    ///
    /// ItemClick, NOT SelectionChanged. SelectedIndex is bound to the current
    /// page, and scrolling changes the current page, so SelectionChanged would
    /// fire from scrolling and navigate back to the top of that page: the
    /// viewport would fight every attempt to scroll freely. ItemClick fires
    /// only for a real click, which breaks that loop at the source.
    /// </summary>
    private void ThumbnailList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PageThumbnail thumbnail)
        {
            ViewModel.GoToPage(thumbnail.PageIndex);
        }
    }

    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Ctrl+wheel over the thumbnail list zooms the cards instead of scrolling,
    /// like an image viewer. Without Ctrl the wheel scrolls the list normally.
    /// </summary>
    private void ThumbnailList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsCtrlDown())
        {
            return;
        }

        int delta = e.GetCurrentPoint(ThumbnailList).Properties.MouseWheelDelta;
        ViewModel.AdjustThumbnailSize(delta > 0 ? 16 : -16);
        e.Handled = true;
    }

    // ---------------- Thumbnail pane resize grip ----------------

    private bool _thumbResizing;
    private bool _thumbDockedRight;
    private double _thumbResizeStartX;
    private double _thumbResizeStartWidth;

    /// <summary>
    /// The panel's inner edge is a thin grip; dragging it toward the document
    /// widens the thumbnails, away from it narrows them.
    /// </summary>
    private void ThumbnailResize_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement grip)
        {
            return;
        }

        _thumbResizing = true;
        _thumbResizeStartX = e.GetCurrentPoint(null).Position.X;
        _thumbResizeStartWidth = ViewModel.ThumbnailDisplayWidth;
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ThumbnailResize_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbResizing)
        {
            return;
        }

        double x = e.GetCurrentPoint(null).Position.X;
        // Dragging toward the document widens. That is rightward while docked
        // left (grip on the right edge) and leftward while docked right, so the
        // sign flips with the dock side.
        double delta = _thumbDockedRight
            ? _thumbResizeStartX - x
            : x - _thumbResizeStartX;
        ViewModel.ThumbnailDisplayWidth = Math.Clamp(
            _thumbResizeStartWidth + delta,
            ViewportViewModel.MinThumbnailWidth,
            ViewportViewModel.MaxThumbnailWidth);
        e.Handled = true;
    }

    private void ThumbnailResize_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbResizing)
        {
            return;
        }

        _thumbResizing = false;
        (sender as FrameworkElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // ---------------- Page organising ----------------

    /// <summary>
    /// A drag-reorder in the thumbnail list moved an item, so rebuild the
    /// document to match the new sequence.
    ///
    /// Only a user MOVE is acted on; the app's own Clear/Add while rebuilding
    /// the thumbnails raises Reset/Add, and would otherwise loop. The rebuild is
    /// deferred to after this event, because clearing the collection the list is
    /// mid-reorder on, from inside its own change notification, is not safe.
    /// </summary>
    private void Thumbnails_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move || ViewModel.IsRebuildingPages)
        {
            return;
        }

        var order = ViewModel.Thumbnails.Select(t => t.PageIndex).ToList();
        DispatcherQueue.TryEnqueue(() => ViewModel.RebuildPages(order));
    }

    /// <summary>The page a thumbnail context-menu item belongs to.</summary>
    private static int PageOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is PageThumbnail t ? t.PageIndex : -1;

    private void PageRotate_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p >= 0)
        {
            ViewModel.RotatePage(p, 90);
        }
    }

    private void PageDuplicate_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p >= 0)
        {
            ViewModel.DuplicatePage(p);
        }
    }

    private void PageMoveUp_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p > 0)
        {
            ViewModel.MovePage(p, p - 1);
        }
    }

    private void PageMoveDown_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p >= 0 && p < ViewModel.PageCount - 1)
        {
            ViewModel.MovePage(p, p + 1);
        }
    }

    private void PageDelete_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p >= 0)
        {
            ViewModel.DeletePage(p);
        }
    }

    // ---------------- Rotate Pages dialog ----------------

    /// <summary>The page "Current page" means, set by whichever entry opened the dialog.</summary>
    private int _rotateDialogPage;

    /// <summary>Opened from a page's context menu: defaults to rotating that page.</summary>
    private async void PageRotateDialog_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        await ShowRotatePagesDialog(p >= 0 ? p : ViewModel.CurrentPageIndex, defaultCurrentPage: true);
    }

    /// <summary>Opened from the menu: defaults to rotating the whole document.</summary>
    private async void RotatePagesMenu_Click(object sender, RoutedEventArgs e) =>
        await ShowRotatePagesDialog(ViewModel.CurrentPageIndex, defaultCurrentPage: false);

    private async System.Threading.Tasks.Task ShowRotatePagesDialog(int page, bool defaultCurrentPage)
    {
        if (ViewModel.PageCount == 0)
        {
            return;
        }

        _rotateDialogPage = page;

        RotateDirectionCombo.SelectedIndex = 0;             // clockwise 90
        RotateParityCombo.SelectedIndex = 0;
        RotateOrientationCombo.SelectedIndex = 0;
        RotateFrom.Text = "1";
        RotateTo.Text = ViewModel.PageCount.ToString();

        if (defaultCurrentPage)
        {
            RotateRangeCurrent.IsChecked = true;
        }
        else
        {
            RotateRangeAll.IsChecked = true;
        }

        RotatePagesDialog.XamlRoot = XamlRoot;
        if (await RotatePagesDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        int degrees = int.Parse((string)((ComboBoxItem)RotateDirectionCombo.SelectedItem).Tag);

        RotateRange range =
            RotateRangeCurrent.IsChecked == true ? RotateRange.CurrentPage :
            RotateRangePages.IsChecked == true ? RotateRange.PageRange :
            RotateRange.All;

        int.TryParse(RotateFrom.Text, out int from);
        int.TryParse(RotateTo.Text, out int to);

        var pages = RotationPlan.SelectPages(
            ViewModel.PageCount, range, _rotateDialogPage, from, to,
            (RotateParity)RotateParityCombo.SelectedIndex,
            (RotateOrientation)RotateOrientationCombo.SelectedIndex,
            ViewModel.PageIsLandscape());

        if (pages.Count > 0)
        {
            ViewModel.RotatePages(pages, degrees);
        }
    }

    // ---------------- Insert / extract pages ----------------

    private async void InsertFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PageCount == 0)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            // After the current page, so the inserted content follows what is
            // on screen.
            ViewModel.InsertPagesFromFile(file.Path, ViewModel.CurrentPageIndex + 1);
        }
    }

    private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PageCount > 0)
        {
            ViewModel.InsertBlankPage(ViewModel.CurrentPageIndex + 1);
        }
    }

    private async void ExtractPagesMenu_Click(object sender, RoutedEventArgs e) =>
        await ShowExtractDialog(null);

    private async void PageExtract_Click(object sender, RoutedEventArgs e) =>
        await ShowExtractDialog(PageOf(sender));

    private void PageInsertBlankAfter_Click(object sender, RoutedEventArgs e)
    {
        int p = PageOf(sender);
        if (p >= 0)
        {
            ViewModel.InsertBlankPage(p + 1);
        }
    }

    private async System.Threading.Tasks.Task ShowExtractDialog(int? contextPage)
    {
        int count = ViewModel.PageCount;
        if (count == 0)
        {
            return;
        }

        int def = ((contextPage >= 0 ? contextPage : null) ?? ViewModel.CurrentPageIndex) + 1;
        ExtractFrom.Text = def.ToString();
        ExtractTo.Text = (contextPage >= 0 ? def : count).ToString();
        ExtractOfLabel.Text = $"of {count}";
        ExtractDeleteAfter.IsChecked = false;
        ExtractSeparate.IsChecked = false;

        ExtractPagesDialog.XamlRoot = XamlRoot;
        if (await ExtractPagesDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        int.TryParse(ExtractFrom.Text, out int from);
        int.TryParse(ExtractTo.Text, out int to);
        int lo = System.Math.Clamp(System.Math.Min(from, to), 1, count);
        int hi = System.Math.Clamp(System.Math.Max(from, to), 1, count);

        var indices = new System.Collections.Generic.List<int>();
        for (int i = lo; i <= hi; i++)
        {
            indices.Add(i - 1);
        }

        if (indices.Count == 0)
        {
            return;
        }

        bool separate = ExtractSeparate.IsChecked == true;
        bool ok;

        if (separate)
        {
            // One PDF per page, into a chosen folder.
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, App.WindowHandle);
            folderPicker.FileTypeFilter.Add("*");

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            ok = true;
            foreach (int i in indices)
            {
                string p = System.IO.Path.Combine(folder.Path, $"page {i + 1}.pdf");
                ok &= ViewModel.ExtractPagesToFile(new[] { i }, p);
            }
        }
        else
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, App.WindowHandle);
            savePicker.SuggestedFileName = $"pages {lo}-{hi}";
            savePicker.FileTypeChoices.Add("PDF", new System.Collections.Generic.List<string> { ".pdf" });

            var file = await savePicker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            ok = ViewModel.ExtractPagesToFile(indices, file.Path);
        }

        // Delete-after only runs once the extract succeeded, so a failed write
        // never loses the pages.
        if (ok && ExtractDeleteAfter.IsChecked == true)
        {
            ViewModel.DeletePages(indices);
        }

        ViewModel.Status = ok ? "Pages extracted." : "Could not extract the pages.";
    }
}
