using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A block of the document's own text that is selected: the box the reader can
/// see, and the lines inside it that will move when they drag it.
/// </summary>
/// <remarks>
/// ⚠️ THE BOX AND THE LINES ARE ONE THING, AND THAT IS WHY THIS EXISTS. There
/// used to be two answers to "what is a paragraph": the app drew a box from its
/// own page segmentation, and the core moved a different set of lines worked
/// out from the leading and the left edge. They agree often enough to look
/// right and not often enough to be right. A box that does not say what a drag
/// will do is worse than no box, so the box now carries its own lines and those
/// are exactly what gets sent.
///
/// ⚠️ AND IT IS NOT <see cref="TextUnitSelection"/>. That is the ANCHOR: the one
/// line or word a caret goes into and that gets retyped. This is what MOVES.
/// A selection has one anchor and one or more of these, and after a shift-click
/// it has several.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </remarks>
public sealed record TextBlockSelection(
    int Page,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double LineHeight,
    IReadOnlyList<double> Baselines)
{
    /// <summary>A whole block of the page: a paragraph, a heading, a cell.</summary>
    public static TextBlockSelection Of(int page, TextRegion region) =>
        new(page, region.Left, region.Top, region.Right, region.Bottom,
            HeightOf(region.Lines), region.Lines.Select(l => l.Baseline).ToList());

    /// <summary>
    /// One line on its own, which is what Alt asks for when the block is wrong.
    /// </summary>
    /// <remarks>
    /// Not a lesser kind of block: the segmenter itself makes a block of one
    /// out of every line no larger block claimed, so this is the same shape it
    /// already produces.
    /// </remarks>
    public static TextBlockSelection Of(int page, TextLine line) =>
        new(page, line.Left, line.Top, line.Right, line.Bottom,
            line.Bottom - line.Top, new[] { line.Baseline });

    /// <summary>
    /// The paragraph a recovered line is in: every line the core gave the same
    /// number, framed from their own edges.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE CORE'S PARAGRAPH, NOT THE SEGMENTER'S. On a Burmese page the
    /// regions are built from glyphs PDFium reads as scrambled fragments, and on
    /// the reader's Pyidaungsu page that frame stopped short of the lines and
    /// split the paragraph. The core numbers recovered lines by the rule its
    /// rewrap uses, so the box a click draws is the paragraph an edit reflows.
    /// An anchor numbered -1 is a block of one.
    /// </remarks>
    public static TextBlockSelection Of(
        int page, IEnumerable<RecoveredLine> lines, RecoveredLine anchor)
    {
        var members = lines
            .Where(l => anchor.Paragraph >= 0 && l.Paragraph == anchor.Paragraph && l != anchor)
            .Append(anchor)
            .OrderBy(l => l.Baseline)
            .ToList();

        // One baseline per line of type, however many runs draw it.
        var baselines = new List<double>();
        foreach (var line in members)
        {
            if (baselines.Count == 0 || line.Baseline - baselines[^1] > SameBaseline)
            {
                baselines.Add(line.Baseline);
            }
        }

        double height = members
            .Where(l => l.Bottom > l.Top)
            .Select(l => l.Bottom - l.Top)
            .FirstOrDefault();

        return new(page,
            members.Min(l => l.Left), members.Min(l => l.Top),
            members.Max(l => l.Right), members.Max(l => l.Bottom),
            height, baselines);
    }

    /// <summary>
    /// The first line's height, which is what the frame is padded by.
    /// </summary>
    /// <remarks>
    /// The FIRST rather than the tallest or the mean: a block whose padding
    /// grew with its biggest line would stand further off a heading than off
    /// the body text under it, and the reader would read that as a mistake.
    /// </remarks>
    private static double HeightOf(IReadOnlyList<TextLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Bottom > line.Top) { return line.Bottom - line.Top; }
        }
        return 0;
    }

    /// <summary>The box as it is DRAWN: the tight bounds, stood off the type.</summary>
    /// <remarks>
    /// ⚠️ THE SAME NUMBERS THE HIT TEST USES, taken from the one place rather
    /// than repeated. A frame the reader can see and a box the pointer can
    /// enter that disagree by a few points is a click that lands inside the
    /// rule and dismisses the selection.
    /// </remarks>
    public (double Left, double Top, double Right, double Bottom) Frame
    {
        get
        {
            double padX = LineHeight * TextUnitSelection.FramePadXFactor;
            double padY = LineHeight * TextUnitSelection.FramePadYFactor;
            return (Left - padX, Top - padY, Right + padX, Bottom + padY);
        }
    }

    /// <summary>Whether a point falls inside the drawn box.</summary>
    public bool Contains(int page, double normX, double normY)
    {
        if (page != Page) { return false; }
        var (l, t, r, b) = Frame;
        return normX >= l && normX <= r && normY >= t && normY <= b;
    }

    /// <summary>
    /// Whether this is the same block as another, which is what a shift-click
    /// has to know so clicking one twice does not select it twice.
    /// </summary>
    /// <remarks>
    /// By the LINES it holds, not by the box. Two blocks with the same lines
    /// are the same block whatever rounding did to their bounds, and a block
    /// can only be built out of lines that are on the page.
    /// </remarks>
    public bool IsSameAs(TextBlockSelection other)
    {
        if (Page != other.Page || Baselines.Count != other.Baselines.Count)
        {
            return false;
        }
        for (int i = 0; i < Baselines.Count; i++)
        {
            if (System.Math.Abs(Baselines[i] - other.Baselines[i]) > SameBaseline)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// How close two baselines must be to be the same line of type, as a
    /// fraction of the page width. About a point on A4.
    /// </summary>
    public const double SameBaseline = 0.002;
}
