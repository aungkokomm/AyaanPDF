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
    private bool _isCtrlDown;
    private bool _isSelectingText;
    private bool _isDrawing;
    private bool _isDrawingShape;
    private bool _isMovingAnnotation;
    private bool _isMarqueeing;
    private bool _isPanning;
    private Point _panLastPoint;
    private Point _panTarget;
    private uint _dragPointerId;
    private Polyline? _livePreviewStroke;

    public MainPage()
    {
        InitializeComponent();
        ViewModel.InkStrokeChanged += OnInkStrokeChanged;
        ViewModel.InkStrokes.CollectionChanged += OnInkStrokesCollectionChanged;
        // Shapes are a SEPARATE collection but share the ink canvas, so without
        // this a finished shape was added to the model and nothing ever redrew
        // it: the drag preview vanished on mouse-up and left an empty page.
        ViewModel.Shapes.CollectionChanged += OnInkStrokesCollectionChanged;
        ViewModel.LayoutRebuilt += OnLayoutRebuilt;
        ViewModel.ScrollToPageRequested += OnScrollToPageRequested;
        Loaded += (_, _) =>
        {
            RootGrid.Focus(FocusState.Programmatic);
            InitializePenPickers();
            UpdateToolRail();
            UpdateCursor();
        };

        // Lets the app be driven headlessly for diagnosis: set
        // PDFEDITOR_AUTOOPEN to a PDF path and it loads on startup, and
        // PDFEDITOR_AUTOZOOM to a factor to zoom there once it has settled.
        // Deep zoom is where the interesting render behaviour lives, and
        // without this it can only be reached by hand.
        Loaded += async (_, _) =>
        {
            string probe = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOOPEN") ?? "";
            if (probe.Length > 0 && System.IO.File.Exists(probe))
            {
                ViewModel.OpenDocument(probe);
            }

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
        var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Path xmlns=\"{ns}\" Data=\"{data}\" />");
        path.Fill = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        path.Stretch = Stretch.Uniform;
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
            FitToWidth();
            PushVisibleWindow();
        });
    }

    /// <summary>Available content width in DIPs, excluding the canvas padding.</summary>
    private double AvailableContentWidth =>
        PageScroller.ViewportWidth - ViewportHost.Padding.Left - ViewportHost.Padding.Right;

    private void OnScrollToPageRequested(int pageIndex, bool animate) =>
        DispatcherQueue.TryEnqueue(() => ScrollToPage(pageIndex, animate));

    /// <summary>
    /// Brings a page to the top of the viewport, animated by default.
    ///
    /// The page top is in SLOT space (unzoomed DIPs), and the ScrollView wants
    /// a zoomed offset, so it is multiplied by the zoom factor. The host's top
    /// padding is part of the content, so it is included; a small lead-in is
    /// subtracted so the page does not sit flush against the top edge, which
    /// looks like it has been cut off rather than scrolled to.
    /// </summary>
    private void ScrollToPage(int pageIndex, bool animate)
    {
        double slotTop = ViewModel.SlotTopOf(pageIndex);
        double zoom = PageScroller.ZoomFactor;

        const double LeadIn = 12;
        double target = (slotTop + ViewportHost.Padding.Top) * zoom - LeadIn;
        target = Math.Max(0, target);

        Diag.Log($"scrollToPage {pageIndex}: from {PageScroller.VerticalOffset:F0} to {target:F0} animate={animate}");

        PageScroller.ScrollTo(
            PageScroller.HorizontalOffset,
            target,
            new ScrollingScrollOptions(
                animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled,
                ScrollingSnapPointsMode.Ignore));
    }

    private void PageScroller_ViewChanged(ScrollView sender, object args)
    {
        UpdateZoomReadout();

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

        if (_autoFit)
        {
            FitToWidth();
        }

        UpdateZoomReadout();
        PushVisibleWindow();
    }

    /// <summary>Arrow-key scroll distance in DIPs, close to Acrobat's nudge.</summary>
    private const double ArrowScrollStep = 64;

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
            Diag.Log($"stamp thumbnail failed for {path}: {ex.Message}");
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

            StampChoices.Visibility = stamps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            StampEmptyHint.Visibility = stamps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Preselect: this session's choice if there is one, otherwise the
            // one remembered from last time. Someone who keeps a single
            // signature should never have to pick it twice.
            var wanted = _selectedStamp is not null
                ? stamps.FirstOrDefault(s => s.Path == _selectedStamp.Path)
                : StampLibrary.LastUsed(stamps);

            if (wanted is not null)
            {
                StampChoices.SelectedItem = wanted;
                _selectedStamp = wanted;
            }
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
            StampLibrary.RememberLastUsed(entry);
            // Choosing a stamp arms the tool: picking one and then having to
            // find the tool button as well would be a pointless second step.
            SetActiveTool(ToolMode.Stamp);
            ReturnFocusAfterPointerUse();
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
        if (_selectedStamp is null)
        {
            ViewModel.Status = "Choose a stamp first.";
            return;
        }

        var pixels = await StampLibrary.DecodeAsync(_selectedStamp.Path);
        if (pixels is null)
        {
            ViewModel.Status = $"Could not read {_selectedStamp.Name}.";
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
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            Foreground = HexBrush(ViewModel.InkColorHex),
            // Arial is metrically close to the PDF's Helvetica, so the editor
            // wraps in almost the same places the rendered text will.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Arial"),
            FontSize = System.Math.Max(8, ViewModel.TextFontSize * scale),
            Text = initialText ?? string.Empty,
        };

        Canvas.SetLeft(_textEditor, normLeft * scale);
        Canvas.SetTop(_textEditor, (normTop * scale) + pageTop);
        InkCanvas.Children.Add(_textEditor);

        _textEditorPage = page;
        _boxLeft = normLeft;
        _boxTop = normTop;
        _boxRight = normRight;
        _boxBottom = normBottom;

        // Set AFTER the initial CommitTextEdit above, so closing any prior
        // editor does not adopt this edit's target.
        _editingTarget = editing;

        _textEditor.KeyDown += TextEditor_KeyDown;
        _textEditor.LostFocus += (_, _) => CommitTextEdit();
        _textEditor.Focus(FocusState.Programmatic);

        // Caret at the end, so re-editing appends rather than overwriting.
        _textEditor.SelectionStart = _textEditor.Text.Length;
    }

    /// <summary>
    /// Brings an OPEN editor into line with the tool's current colour and size,
    /// so changing either in the property bar mid-edit is reflected at once
    /// rather than only after committing.
    /// </summary>
    private void UpdateOpenEditorStyle()
    {
        if (_textEditor is null)
        {
            return;
        }

        _textEditor.Foreground = HexBrush(ViewModel.InkColorHex);
        _textEditor.FontSize = System.Math.Max(8, ViewModel.TextFontSize * ViewModel.OverlayScale);
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
        _textEditor = null; // First, so the LostFocus this triggers is a no-op.
        _editingTarget = null;
        editor.KeyDown -= TextEditor_KeyDown;

        string text = editor.Text.TrimEnd('\r', '\n');
        InkCanvas.Children.Remove(editor);

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
        // startup.
        _suppressOpacityChange = false;
    }

    /// <summary>
    /// Applies the opacity slider to whichever colour the armed tool uses.
    ///
    /// Opacity lives in the colour's alpha rather than in a property of its
    /// own, because alpha is what actually reaches the saved file; see
    /// <see cref="InkPresets.WithOpacity"/>.
    /// </summary>
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

        ShowOpacity();
    }

    /// <summary>
    /// Starts SET, so nothing the slider raises while the page is still being
    /// built is mistaken for a deliberate change. Cleared once the pickers are
    /// initialized.
    /// </summary>
    private bool _suppressOpacityChange = true;

    /// <summary>Brings the slider and its readout into line with the armed tool's colour.</summary>
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
            UpdateCustomSwatch();
            ReturnFocusAfterPointerUse();
        }
    }

    private void InkWidth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthChoices.SelectedItem is InkWidth w)
        {
            ViewModel.InkWidth = w.Value;
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
    private void ShapeKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShapeChoices.SelectedItem is FrameworkElement { Tag: string tag }
            && Enum.TryParse(tag, out ShapeKind kind))
        {
            ViewModel.ActiveShapeKind = kind;
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

        bool color = tool.Offers(ToolOptions.Color);
        ColorSection.Visibility = Show(color);
        WidthSection.Visibility = Show(tool.Offers(ToolOptions.Width));

        // Opacity rides on the colour, so it is only meaningful where there is
        // a colour to apply it to.
        OpacitySection.Visibility = Show(color);

        bool stamps = tool.Offers(ToolOptions.Stamp);
        StampSection.Visibility = Show(stamps);

        bool shapes = tool.Offers(ToolOptions.Shape);
        ShapeSection.Visibility = Show(shapes);
        if (shapes)
        {
            ShapeChoices.SelectedIndex = (int)ViewModel.ActiveShapeKind;
        }

        bool fontSize = tool.Offers(ToolOptions.FontSize);
        FontSizeSection.Visibility = Show(fontSize);
        if (fontSize)
        {
            ShowFontSize();
        }

        // A bar with every section collapsed is an empty pill floating over the
        // page, so the whole thing goes when the tool offers nothing.
        PropertyBar.Visibility = Show(tool.Options != ToolOptions.None);

        if (color)
        {
            ShowPaletteFor(ViewModel.ActiveTool);
            ShowOpacity();
        }

        if (stamps)
        {
            RefreshStamps();
        }
    }

    private static Visibility Show(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- File menu ----------------

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

        ViewModel.OpenDocument(file.Path);
        Debug.WriteLine($"[MainPage] Opened \"{file.Path}\"");
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

        if (_autoFit)
        {
            DispatcherQueue.TryEnqueue(() => FitToWidth(animate: true));
        }
    }

    private void ToggleThumbnails() =>
        ThumbnailPanel.Visibility =
            ThumbnailPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

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
    private void OnInkStrokesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildInkCanvas();

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
    /// </summary>
    private Polyline BuildStrokePolyline(InkStrokeAnnotation stroke)
    {
        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(stroke.PageIndex);

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(ColorFromHex(stroke.ColorHex)),
            StrokeThickness = stroke.StrokeWidth * scale,
        };

        foreach (var (x, y) in stroke.Points)
        {
            polyline.Points.Add(new Point(x * scale, y * scale + pageTop));
        }

        return polyline;
    }

    /// <summary>
    /// An arrow's head, as a filled triangle. A stroked outline is not the same
    /// shape and would not match what goes into the file.
    /// </summary>
    private Polygon BuildFilledHead(
        IReadOnlyList<(double X, double Y)> points, int pageIndex, string colorHex)
    {
        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(pageIndex);

        var brush = new SolidColorBrush(ColorFromHex(colorHex));
        var polygon = new Polygon { Fill = brush, Stroke = brush, StrokeThickness = 0.5 };

        foreach (var (x, y) in points)
        {
            polygon.Points.Add(new Point(x * scale, (y * scale) + pageTop));
        }

        return polygon;
    }

    private Polygon? _livePreviewHead;

    private void OnInkStrokeChanged()
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
                StrokeThickness = shaping ? ViewModel.InkWidth * ViewModel.OverlayScale : 2,
            };
            InkCanvas.Children.Add(_livePreviewStroke);
        }

        // The in-progress stroke is normalized on the way in, exactly like a
        // committed one, so the preview and the finished stroke share a
        // coordinate space and the line cannot jump when the pointer lifts.
        // Anchored to the page the stroke STARTED on, not the current page:
        // in continuous view you can start drawing on a visible page that is
        // not the current one, and the preview must land where the ink will.
        double pageTop = ViewModel.SlotTopOf(
            ViewModel.ShapeInProgress is not null ? ViewModel.ActiveShapePage : ViewModel.ActiveInkPage);

        _livePreviewStroke.Points.Clear();
        foreach (var (x, y) in points)
        {
            _livePreviewStroke.Points.Add(new Point(x * scale, y * scale + pageTop));
        }
    }

    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = Convert.ToByte(hex.Substring(0, 2), 16);
        byte r = Convert.ToByte(hex.Substring(2, 2), 16);
        byte g = Convert.ToByte(hex.Substring(4, 2), 16);
        byte b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }

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

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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
            }

            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space when !_isSpaceHandActive:
                _isSpaceHandActive = true;
                UpdateCursor();
                break;
            case VirtualKey.C when _isCtrlDown:
                CopySelectedText();
                break;
            case VirtualKey.F when _isCtrlDown:
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
                e.Handled = true;
                break;

            // Ctrl+O and Ctrl+S are handled HERE, not only as accelerators on
            // the menu items: a KeyboardAccelerator inside a MenuFlyout is only
            // live while that flyout is open, so from the canvas they did
            // nothing at all.
            case VirtualKey.O when _isCtrlDown:
                OpenFile_Click(this, null!);
                e.Handled = true;
                break;
            case VirtualKey.S when _isCtrlDown:
                SaveAs_Click(this, null!);
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
                _autoFit = false;
                PageScroller.ZoomTo(1.0f, null,
                    new ScrollingZoomOptions(ScrollingAnimationMode.Enabled, ScrollingSnapPointsMode.Ignore));
                e.Handled = true;
                break;
            case VirtualKey.Number2 when _isCtrlDown:
                ZoomFitWidth_Click(this, null!);
                e.Handled = true;
                break;

            // Arrow keys nudge the scroll, as in Acrobat. Without these the
            // keyboard could jump pages but not move within one.
            case VirtualKey.Down:
            case VirtualKey.Up:
                ScrollBy(0, e.Key == VirtualKey.Down ? ArrowScrollStep : -ArrowScrollStep);
                e.Handled = true;
                break;
            case VirtualKey.Right:
            case VirtualKey.Left:
                ScrollBy(e.Key == VirtualKey.Right ? ArrowScrollStep : -ArrowScrollStep, 0);
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
                ViewModel.DeleteSelectedAnnotation();
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
        if (ViewModel.ActiveTool != ToolMode.Select || _isSpaceHandActive)
        {
            return null;
        }

        var content = ContentPoint(e);
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

        return ViewModel.GripUnder(content.Page, nx, ny) switch
        {
            // Diagonals matching the corner, as every editor does, so the
            // cursor says which way the drag will go.
            LoadedAnnotationPicker.Grip.TopLeft or LoadedAnnotationPicker.Grip.BottomRight
                => InputSystemCursorShape.SizeNorthwestSoutheast,
            LoadedAnnotationPicker.Grip.TopRight or LoadedAnnotationPicker.Grip.BottomLeft
                => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => ViewModel.IsOverSelection(content.Page, nx, ny)
                ? InputSystemCursorShape.SizeAll
                : null,
        };
    }

    private void UpdateCursor(PointerRoutedEventArgs? e = null)
    {
        if (e is not null && HoverCursor(e) is InputSystemCursorShape hover)
        {
            ViewportHost.SetCursorShape(hover);
            return;
        }

        var shape = (_isSpaceHandActive, ViewModel.ActiveTool) switch
        {
            (true, _) => InputSystemCursorShape.SizeAll,
            (_, ToolMode.Hand) => InputSystemCursorShape.SizeAll,
            (_, ToolMode.Select) => InputSystemCursorShape.IBeam,
            (_, ToolMode.Highlight) => InputSystemCursorShape.IBeam,
            (_, ToolMode.Draw) => InputSystemCursorShape.Cross,
            (_, ToolMode.Shape) => InputSystemCursorShape.Cross,
            (_, ToolMode.Text) => InputSystemCursorShape.IBeam,
            (_, ToolMode.Note) => InputSystemCursorShape.Cross,
            (_, ToolMode.Stamp) => InputSystemCursorShape.Cross,
            _ => InputSystemCursorShape.Arrow,
        };
        ViewportHost.SetCursorShape(shape);
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

        // The preceding clicks may have started a select-and-move; abandon it,
        // and drop the marquee, so the box is edited rather than dragged.
        ResetPointerInteraction();
        ViewModel.ClearAnnotationSelection();

        // The tool takes on the box's colour and size, so the property bar shows
        // what is being edited and the rewritten box keeps them unless changed.
        ViewModel.InkColorHex = target.ColorHex;
        ViewModel.TextFontSize = target.FontSizeNorm;
        UpdateToolRail();

        // Remove the box from the page FIRST, so the editor is the only layer.
        // Without this the rendered box stayed under the editor, which is the
        // "two layers" the edit showed.
        if (!ViewModel.BeginLoadedTextBoxEdit(target.PageIndex, target.Index))
        {
            return;
        }

        BeginTextEdit(target.PageIndex, target.Left, target.Top, target.Right, target.Bottom,
                      initialText: target.Text, editing: target);
        e.Handled = true;
    }

    private bool ToolWantsPointer =>
        !_isSpaceHandActive && ViewModel.ActiveTool != ToolMode.Hand;

    private void ViewportHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(ViewportHost);
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;   // let ScrollView handle touch/pen pan + pinch
        }

        if (!current.Properties.IsLeftButtonPressed)
        {
            return;
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
            e.Handled = true;
            return;
        }

        var content = ContentPoint(e);

        // Normalized page-local coordinates, which is the space annotations
        // live in.
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

        Diag.Log($"press tool={ViewModel.ActiveTool} raw=({e.GetCurrentPoint(ViewportHost).Position.X:F0},{e.GetCurrentPoint(ViewportHost).Position.Y:F0}) " +
                 $"page={content.Page} local=({content.X:F1},{content.Y:F1}) norm=({nx:F3},{ny:F3})");

        switch (ViewModel.ActiveTool)
        {
            case ToolMode.Select:
                // A click on an existing mark picks it up; a click on empty
                // space falls through to text selection. That is what makes
                // annotations objects rather than paint.
                if (ViewModel.SelectAnnotationAt(content.Page, nx, ny))
                {
                    _isMovingAnnotation = true;
                    _dragPointerId = current.PointerId;
                    ViewportHost.CapturePointer(e.Pointer);
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
            ViewModel.ExtendShape(content.X, content.Y);
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
        if (e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
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
            _isMarqueeing = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndMarquee();
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

        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(page);
        Canvas.SetLeft(_textBoxPreview, nx * scale);
        Canvas.SetTop(_textBoxPreview, (ny * scale) + pageTop);
        InkCanvas.Children.Add(_textBoxPreview);
    }

    private void UpdateTextBoxPreview(double nx, double ny)
    {
        if (_textBoxPreview is null)
        {
            return;
        }

        double scale = ViewModel.OverlayScale;
        double pageTop = ViewModel.SlotTopOf(_textDragPage);

        double left = System.Math.Min(_textDragStartX, nx);
        double top = System.Math.Min(_textDragStartY, ny);
        Canvas.SetLeft(_textBoxPreview, left * scale);
        Canvas.SetTop(_textBoxPreview, (top * scale) + pageTop);
        _textBoxPreview.Width = System.Math.Abs(nx - _textDragStartX) * scale;
        _textBoxPreview.Height = System.Math.Abs(ny - _textDragStartY) * scale;
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
}
