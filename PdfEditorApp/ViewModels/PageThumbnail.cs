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
