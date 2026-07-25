using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// A sticky-note marker at a point on a page. Unlike highlights/ink strokes
/// (immutable once drawn), a note's text is edited after creation, so this
/// needs to be an ObservableObject rather than a plain record — it lives in
/// the app project (not the classlib) for that reason.
/// </summary>
public partial class NoteAnnotation : ObservableObject
{
    public int PageIndex { get; }
    public double X { get; }
    public double Y { get; }

    /// <summary>
    /// Slot width, so the marker can convert its normalized position without
    /// living inside the scaled overlay. A note marker is a real control with
    /// an intrinsic size, and the overlay's 800x transform would blow it up to
    /// cover the whole page.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    [ObservableProperty]
    public partial string Text { get; set; }

    public NoteAnnotation(int pageIndex, double x, double y, string text)
    {
        PageIndex = pageIndex;
        X = x;
        Y = y;
        Text = text;
    }
}
