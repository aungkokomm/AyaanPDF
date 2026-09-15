using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One page in the page picker's grid. Its bitmap is drawn when a container
/// shows it and dropped when that container is recycled, so a 40,000-page book
/// holds only the thumbnails on screen.
/// </summary>
public partial class PickerPage : ObservableObject
{
    public PickerPage(int index)
    {
        Index = index;
    }

    /// <summary>Zero-based.</summary>
    public int Index { get; }

    /// <summary>1-based, for display.</summary>
    public string Label => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string AccessibleName => $"Page {Index + 1}";

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }

    /// <summary>
    /// Whether a container is showing this page right now. A render queued
    /// while it was on screen is skipped if it has scrolled away since, so
    /// flinging through a book does not draw every page it passed.
    /// </summary>
    internal bool IsRealized { get; set; }

    internal bool IsRendering { get; set; }
}
