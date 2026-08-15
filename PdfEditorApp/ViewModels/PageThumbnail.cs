using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One entry in the thumbnail sidebar. Bitmap starts null — it's rendered
/// lazily by <see cref="ViewportViewModel.EnsureThumbnailRendered"/> the
/// first time MainPage's ListView actually realizes this item's container,
/// so a 300-page document doesn't render 300 thumbnails up front.
/// </summary>
public partial class PageThumbnail : ObservableObject
{
    public int PageIndex { get; }

    /// <summary>1-based, for display.</summary>
    public int DisplayNumber => PageIndex + 1;

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }

    /// <summary>
    /// The card's on-screen width, in DIPs, so all thumbnails resize together
    /// when the panel is zoomed or dragged. Height follows at US-Letter aspect.
    /// </summary>
    [ObservableProperty]
    public partial double CardWidth { get; set; } = 126.0;

    public double CardHeight => CardWidth * (11.0 / 8.5);

    partial void OnCardWidthChanged(double value)
    {
        OnPropertyChanged(nameof(CardHeight));
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
    }

    /// <summary>
    /// The quarter turn the view is shown at, so the panel matches the page.
    ///
    /// A dark strip of upright thumbnails beside a page lying on its side makes
    /// the panel useless for finding your place, which is the only thing it is
    /// for.
    /// </summary>
    [ObservableProperty]
    public partial double ViewRotation { get; set; }

    partial void OnViewRotationChanged(double value)
    {
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
    }

    /// <summary>
    /// The image is measured BEFORE it is turned, so on a quarter turn its
    /// width and height are swapped: turned about its centre it then lands back
    /// inside the card instead of hanging over the edges.
    /// </summary>
    public double ImageWidth => Turned ? CardHeight : CardWidth;

    public double ImageHeight => Turned ? CardWidth : CardHeight;

    private bool Turned => ViewRotation is 90 or 270;

    private bool _isRendering;

    public PageThumbnail(int pageIndex)
    {
        PageIndex = pageIndex;
    }

    /// <summary>
    /// Claims the right to render this thumbnail, returning false if a render
    /// is already in flight. Rendering is asynchronous, so <see cref="Bitmap"/>
    /// stays null for its whole duration — scrolling the sidebar back and
    /// forth re-raises Loaded and would otherwise kick off several concurrent
    /// renders of the same page, all serialized behind render_core's global
    /// PDFium lock. Called only from the UI thread, so a plain bool suffices.
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
}
