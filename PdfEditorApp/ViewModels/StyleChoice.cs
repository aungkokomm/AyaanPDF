using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// One style the document uses, as a row the reader can tick.
///
/// A display wrapper around <see cref="StyleTally"/>, which stays a plain value
/// in the testable project.
/// </summary>
public sealed partial class StyleChoice : ObservableObject
{
    /// <summary>
    /// What the sample is drawn at, whatever the document sets it in.
    ///
    /// The real size would make a 48pt title tower over the row and a 6pt
    /// footnote unreadable, so it is squeezed into a range that still shows
    /// which of two styles is the bigger.
    /// </summary>
    private const double SmallestPreview = 11;
    private const double LargestPreview = 20;

    public StyleChoice(StyleTally tally)
    {
        Tally = tally;
        Describe = tally.Describe();
        Reach = tally.DescribeReach();
        Sample = tally.Sample;

        // The FONT cannot be honoured: the document's font is very often not
        // installed on this machine, and substituting silently would show a
        // sample that misrepresents the page. Size, weight and colour can be,
        // and between them they are what a reader recognises a heading by.
        PreviewSize = Math.Clamp(tally.Style.SizePoints, SmallestPreview, LargestPreview);
        PreviewWeight = tally.Style.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        PreviewBrush = new SolidColorBrush(ColorHelper.FromArgb(
            255,
            (byte)((tally.Style.ColorRgb >> 16) & 0xFF),
            (byte)((tally.Style.ColorRgb >> 8) & 0xFF),
            (byte)(tally.Style.ColorRgb & 0xFF)));
    }

    public StyleTally Tally { get; }

    public TextStyle Style => Tally.Style;

    public string Describe { get; }

    public string Reach { get; }

    public string Sample { get; }

    public double PreviewSize { get; }

    public Windows.UI.Text.FontWeight PreviewWeight { get; }

    public Brush PreviewBrush { get; }

    /// <summary>Whether text in this style becomes a bookmark.</summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    /// <summary>
    /// How deep its bookmarks sit, from 1. Shown as an index into a combo, so
    /// this is the level minus one.
    /// </summary>
    [ObservableProperty]
    public partial int LevelIndex { get; set; }

    public int Level => LevelIndex + 1;
}
