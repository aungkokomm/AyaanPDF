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
        // PDFEDITOR_AUTOOPEN to a PDF path and it loads on startup.
        Loaded += (_, _) =>
        {
            string probe = Environment.GetEnvironmentVariable("PDFEDITOR_AUTOOPEN") ?? "";
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
            ViewModel.ScrollToPageRequested -= OnScrollToPageRequested;
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
            PageScroller.ZoomFactor);

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

    private void HandTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Hand);
    private void SelectTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Select);
    private void HighlightTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Highlight);
    private void NoteTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Note);
    private void DrawTool_Click(object sender, RoutedEventArgs e) => SetActiveTool(ToolMode.Draw);

    private void SetActiveTool(ToolMode tool)
    {
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
        ColorChoices.ItemsSource = InkPresets.Colors;
        WidthChoices.ItemsSource = InkPresets.Widths;
        ColorChoices.SelectedIndex = 0;
        WidthChoices.SelectedIndex = 1;
        UpdatePenSwatch();
    }

    private void InkColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorChoices.SelectedItem is InkColor c)
        {
            // The pen and the highlighter are separate colours: a highlighter
            // must stay translucent or it covers the text it marks, so picking
            // a pen colour maps to the matching translucent highlight rather
            // than making the highlighter opaque. Matched by NAME, since the
            // two lists are different lengths.
            ViewModel.InkColorHex = c.Hex;
            ViewModel.HighlightColorHex = InkPresets.HighlightFor(c).Hex;
            UpdatePenSwatch();
        }
    }

    private void InkWidth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthChoices.SelectedItem is InkWidth w)
        {
            ViewModel.InkWidth = w.Value;
        }
    }

    private void UpdatePenSwatch() =>
        PenSwatch.Background = HexBrush(ViewModel.InkColorHex);

    private void UpdateToolRail()
    {
        // Pen options are only meaningful for tools that lay down colour.
        PenOptionsButton.Visibility =
            ViewModel.ActiveTool is ToolMode.Draw or ToolMode.Highlight
                ? Visibility.Visible
                : Visibility.Collapsed;

        var active = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var idle = new SolidColorBrush(Colors.Transparent);

        HandToolButton.Background = ViewModel.ActiveTool == ToolMode.Hand ? active : idle;
        SelectToolButton.Background = ViewModel.ActiveTool == ToolMode.Select ? active : idle;
        HighlightToolButton.Background = ViewModel.ActiveTool == ToolMode.Highlight ? active : idle;
        DrawToolButton.Background = ViewModel.ActiveTool == ToolMode.Draw ? active : idle;
        NoteToolButton.Background = ViewModel.ActiveTool == ToolMode.Note ? active : idle;
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

    /// <summary>
    /// Shows or hides the pages panel.
    ///
    /// No inset arithmetic: the panel owns a grid column, so collapsing it
    /// collapses the column and the canvas column absorbs the space. Overlap
    /// is not expressible, and the ScrollView's own SizeChanged drives the
    /// refit, so this does not have to reason about layout timing either.
    /// </summary>
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
        // Anchored to the page the stroke STARTED on, not the current page:
        // in continuous view you can start drawing on a visible page that is
        // not the current one, and the preview must land where the ink will.
        double pageTop = ViewModel.SlotTopOf(ViewModel.ActiveInkPage);

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

            // NOT Tab. Tab is the focus-traversal key, and binding it here
            // meant keyboard users could never move focus anywhere in the app.
            // It also fired from any stray Tab the app received, including one
            // that arrived while a system dialog was being dismissed, which is
            // how the pages panel came to be open on launch.
            case VirtualKey.F4:
                ToggleThumbnails();
                e.Handled = true;
                break;

            // Single-key tool switching, Photoshop-style. Guarded by the
            // text-focus check above, so typing "h" in the search box does not
            // switch tools.
            case VirtualKey.H when !_isCtrlDown:
                SetActiveTool(ToolMode.Hand);
                e.Handled = true;
                break;
            case VirtualKey.V when !_isCtrlDown:
                SetActiveTool(ToolMode.Select);
                e.Handled = true;
                break;
            case VirtualKey.B when !_isCtrlDown:
                SetActiveTool(ToolMode.Draw);
                e.Handled = true;
                break;
            case VirtualKey.U when !_isCtrlDown:
                SetActiveTool(ToolMode.Highlight);
                e.Handled = true;
                break;
            case VirtualKey.N when !_isCtrlDown:
                SetActiveTool(ToolMode.Note);
                e.Handled = true;
                break;

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
    private void UpdateCursor()
    {
        var shape = (_isSpaceHandActive, ViewModel.ActiveTool) switch
        {
            (true, _) => InputSystemCursorShape.SizeAll,
            (_, ToolMode.Hand) => InputSystemCursorShape.SizeAll,
            (_, ToolMode.Select) => InputSystemCursorShape.IBeam,
            (_, ToolMode.Highlight) => InputSystemCursorShape.IBeam,
            (_, ToolMode.Draw) => InputSystemCursorShape.Cross,
            (_, ToolMode.Note) => InputSystemCursorShape.Cross,
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
        Diag.Log($"PRESS arrived: device={e.Pointer.PointerDeviceType} left={current.Properties.IsLeftButtonPressed} tool={ViewModel.ActiveTool}");

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
