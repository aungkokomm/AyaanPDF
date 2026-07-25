using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private bool _isMovingAnnotation;
    private uint _dragPointerId;
    private Polyline? _livePreviewStroke;

    public MainPage()
    {
        InitializeComponent();
        ViewModel.InkStrokeChanged += OnInkStrokeChanged;
        ViewModel.InkStrokes.CollectionChanged += OnInkStrokesCollectionChanged;
        ViewModel.LayoutRebuilt += OnLayoutRebuilt;
        Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);

        // Lets the app be driven headlessly for diagnosis: set
        // PDFEDITOR_AUTOOPEN to a PDF path and it loads on startup.
        Loaded += (_, _) =>
        {
            string probe = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOOPEN") ?? "";
            Diag.Log($"Loaded: thumbnailPanel={ThumbnailPanel.Visibility}");

            if (probe.Length > 0 && System.IO.File.Exists(probe))
            {
                ViewModel.OpenDocument(probe);
            }
        };
        Unloaded += (_, _) =>
        {
            ViewModel.InkStrokeChanged -= OnInkStrokeChanged;
            ViewModel.InkStrokes.CollectionChanged -= OnInkStrokesCollectionChanged;
            ViewModel.LayoutRebuilt -= OnLayoutRebuilt;
            ViewModel.Dispose();
        };
    }

    /// <summary>
    /// Positions an overlay element at (left, top) within its Grid panel.
    /// Used from x:Bind in the overlay templates: unlike Canvas.Left/Top set
    /// inside a DataTemplate, this is compiled, so a mistake is a build error
    /// rather than a silently mispositioned rectangle.
    /// </summary>
    public static Thickness Offset(double left, double top) => new(left, top, 0, 0);

    // ---------------- ScrollView-driven zoom ----------------

    /// <summary>
    /// Pages are laid out at a FIXED slot width, independent of the window, so
    /// fitting is purely a zoom decision: the zoom that maps that width onto
    /// the viewport. Nothing the renderer does can move it, and a window
    /// resize never rebuilds the layout or disturbs the scroll position.
    /// </summary>
    /// <summary>
    /// How the viewport is currently auto-fitting. Fit-page is the default,
    /// because a page that spans the viewport exactly reads as a region of
    /// the window rather than a sheet on a canvas.
    /// </summary>
    private enum FitMode { Page, Width }

    private FitMode _fitMode = FitMode.Page;

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
            PageScroller.ZoomFactor);

    private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (XamlRoot is not null)
        {
            ViewModel.RasterizationScale = XamlRoot.RasterizationScale;
        }

        ViewModel.SetViewportSize(PageScroller.ViewportWidth, PageScroller.ViewportHeight);

        if (_autoFit)
        {
            FitToWidth();
        }

        UpdateZoomReadout();
        PushVisibleWindow();
    }

    private void UpdateZoomReadout() =>
        ZoomPercentText.Text = $"{Math.Round(PageScroller.ZoomFactor * 100)}%";

    // ---------------- Annotation tool switcher ----------------

    private void HandTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Hand);
    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Select);
    private void HighlightTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Highlight);
    private void NoteTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Note);
    private void DrawTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Draw);

    private void SetActiveTool(ToolMode tool)
    {
        ViewModel.ActiveTool = tool;
        UpdateCursor();
    }

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
    private async Task<bool> SaveAsAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "edited";
        picker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        bool saved = ViewModel.SaveDocumentAs(file.Path);
        Debug.WriteLine($"[MainPage] Save As \"{file.Path}\" -> {(saved ? "ok" : "failed")}");
        return saved;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Window.Close();

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

    private void ToggleThumbnails() =>
        ThumbnailPanel.Visibility =
            ThumbnailPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

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

    private void OnInkStrokesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Page switch: ViewModel.InkStrokes was cleared and is about to
            // be repopulated with the new page's strokes (as Add events) —
            // without this, the previous page's Polylines would linger on
            // screen forever, accumulating across every page navigation.
            InkCanvas.Children.Clear();
            return;
        }

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (InkStrokeAnnotation stroke in e.NewItems)
        {
            InkCanvas.Children.Add(BuildStrokePolyline(stroke));
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

    private void OnInkStrokeChanged()
    {
        var points = ViewModel.CurrentStrokeInProgress;

        if (points is null)
        {
            if (_livePreviewStroke is not null)
            {
                InkCanvas.Children.Remove(_livePreviewStroke);
                _livePreviewStroke = null;
            }

            return;
        }

        double scale = ViewModel.OverlayScale;

        if (_livePreviewStroke is null)
        {
            _livePreviewStroke = new Polyline
            {
                Stroke = new SolidColorBrush(Colors.Red),
                StrokeThickness = 2,
            };
            InkCanvas.Children.Add(_livePreviewStroke);
        }

        // The in-progress stroke is normalized on the way in, exactly like a
        // committed one, so the preview and the finished stroke share a
        // coordinate space and the line cannot jump when the pointer lifts.
        double pageTop = ViewModel.SlotTopOf(ViewModel.CurrentPageIndex);

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

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space when !_isSpaceHandActive:
                _isSpaceHandActive = true;
                UpdateCursor();
                break;
            case VirtualKey.Control:
            case VirtualKey.LeftControl:
            case VirtualKey.RightControl:
                _isCtrlDown = true;
                break;
            case VirtualKey.C when _isCtrlDown:
                CopySelectedText();
                break;
            case VirtualKey.F when _isCtrlDown:
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.Tab:
                ToggleThumbnails();
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

    private void UpdateCursor()
    {
        var shape = _isSpaceHandActive || ViewModel.ActiveTool == ToolMode.Hand
            ? InputSystemCursorShape.Hand
            : InputSystemCursorShape.Arrow;
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

    private PagePoint ContentPoint(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(ViewportHost).Position;
        double slotX = p.X - ViewportHost.Padding.Left;
        double slotY = p.Y - ViewportHost.Padding.Top;

        return ViewModel.HitTestSlotSpace(slotX, slotY, out int pageIndex, out double localX, out double localY)
            ? new PagePoint(pageIndex, localX, localY)
            : new PagePoint(ViewModel.CurrentPageIndex, slotX, slotY);
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

        // Hand tool, Space-hand and middle-drag are all just panning: leave
        // the event unhandled so ScrollView performs it on the compositor.
        if (!ToolWantsPointer || !current.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var content = ContentPoint(e);

        // Normalized page-local coordinates, which is the space annotations
        // live in.
        double nx = content.X / ViewModel.OverlayScale;
        double ny = content.Y / ViewModel.OverlayScale;

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
                _isSelectingText = true;
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);
                ViewModel.BeginTextSelection(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Draw:
                _isDrawing = true;
                _dragPointerId = current.PointerId;
                ViewportHost.CapturePointer(e.Pointer);
                ViewModel.BeginInkStroke(content.Page, content.X, content.Y);
                e.Handled = true;
                break;

            case ToolMode.Note:
                ViewModel.AddNoteAt(content.Page, content.X, content.Y);
                e.Handled = true;
                break;
        }
    }

    private void ViewportHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        var content = ContentPoint(e);

        if (_isMovingAnnotation)
        {
            ViewModel.MoveSelectedAnnotationTo(
                content.X / ViewModel.OverlayScale,
                content.Y / ViewModel.OverlayScale);
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
    }

    private void ViewportHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        if (_isMovingAnnotation)
        {
            _isMovingAnnotation = false;
            ViewportHost.ReleasePointerCapture(e.Pointer);
            ViewModel.EndAnnotationMove();
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

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listView = (ListView)sender;
        if (listView.SelectedIndex >= 0)
        {
            ViewModel.GoToPage(listView.SelectedIndex);
        }
    }
}
