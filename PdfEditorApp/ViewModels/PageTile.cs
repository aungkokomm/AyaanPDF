using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One rendered tile placed on a page card.
///
/// Position and size are in SLOT-SPACE DIPs, already multiplied, for the same
/// reason every other overlay is: a RenderTransform runs after layout, so
/// geometry expressed in page fractions lays out sub-pixel and never draws.
///
/// The bitmap is settable because a tile is first shown at a COARSER level
/// while its exact level renders. Swapping the bitmap in place, rather than
/// replacing the tile, means the card never goes blank mid-zoom.
/// </summary>
public partial class PageTile : ObservableObject
{
    public TileAddress Address { get; }

    public double Left { get; }

    public double Top { get; }

    public double Size { get; }

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }

    /// <summary>True once the tile's own level has rendered, as opposed to a coarse stand-in.</summary>
    public bool IsExact { get; set; }

    public PageTile(TilePlacement placement)
    {
        Address = placement.Address;
        Left = placement.Left;
        Top = placement.Top;
        Size = placement.Size;
    }
}
