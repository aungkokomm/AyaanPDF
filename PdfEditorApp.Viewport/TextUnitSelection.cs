namespace PdfEditorApp.Viewport;

/// <summary>Which unit of the document's own text is selected.</summary>
public enum TextUnitKind
{
    /// <summary>A whole visual line.</summary>
    Line,

    /// <summary>One word, which is what is offered when the line refuses.</summary>
    Word,
}

/// <summary>
/// The piece of the document's own text a click selected, and everything the
/// box and the editor need to know about it.
///
/// ⚠️ ONE TYPE FOR TWO UNITS, on purpose. The gesture is the same whichever it
/// turns out to be, the box is the same, and the editor is the same; only the
/// core call at commit differs. Keeping them apart above this line meant two
/// editors that were duplicates of each other, and they drifted.
///
/// WHICH UNIT A CLICK GETS is decided by what can actually be edited: the LINE
/// when the line is editable, and the WORD under the click when it is not. That
/// is not a preference. A justified line refuses by design, and so do a
/// mixed-style line and two labels sharing a baseline, but the WORDS on all
/// three are perfectly editable. Offering only the line would make a typo in
/// justified body text uncorrectable.
///
/// <paramref name="Word"/> and <paramref name="Line"/> are the snapshot the
/// selection came from, carried so the commit can call the right core write
/// with the identity that write needs. Exactly one is set.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </summary>
public sealed record TextUnitSelection(
    int Page,
    TextUnitKind Kind,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double FontSizePts,
    string Text,
    bool CanEdit,
    string RefusalReason,
    WordClusterSnapshot? Word,
    LineSnapshot? Line)
{
    /// <summary>The whole line under a point.</summary>
    public static TextUnitSelection From(int page, LineSnapshot line) =>
        new(page, TextUnitKind.Line,
            line.Left, line.Top, line.Right, line.Bottom, line.FontSizePts,
            line.Text, line.CanEdit, line.RefusalReason, null, line);

    /// <summary>One word, which is what a refused line falls back to.</summary>
    public static TextUnitSelection From(int page, WordClusterSnapshot word) =>
        new(page, TextUnitKind.Word,
            word.Left, word.Top, word.Right, word.Bottom, word.FontSizePts,
            word.Text, word.CanEdit, word.RefusalReason, word, null);

    /// <summary>
    /// Whether a point in normalized page coordinates is inside the box.
    ///
    /// ⚠️ PADDED THE SAME WAY THE FRAME IS DRAWN, and that is the whole
    /// requirement. The frame sits off the glyphs by a fraction of the type's
    /// own height, because a rule at the tight bounds cuts through the feet of
    /// the letters. If this tested the tight bounds instead, the strip between
    /// the letters and the rule the reader can see would look like the inside of
    /// the box and behave like the outside: clicking there would dismiss the
    /// selection rather than put a caret in it.
    /// </summary>
    public bool Contains(int page, double normX, double normY)
    {
        double height = Bottom - Top;
        double padX = height * FramePadXFactor;
        double padY = height * FramePadYFactor;

        return page == Page
            && normX >= Left - padX && normX <= Right + padX
            && normY >= Top - padY && normY <= Bottom + padY;
    }

    /// <summary>
    /// How far the frame stands off the glyphs, as a fraction of the unit's
    /// height. Kept here beside the hit test that has to agree with it; the
    /// drawing code multiplies by the same numbers.
    /// </summary>
    public const double FramePadXFactor = 0.22;
    public const double FramePadYFactor = 0.30;

    /// <summary>What the status bar says about this selection.</summary>
    public string Description
    {
        get
        {
            string shown = Text.Length > 48 ? Text[..48] + "…" : Text;
            string what = Kind == TextUnitKind.Line ? "Line" : "Word";
            string head = $"{what}: “{shown}”";

            return CanEdit
                ? head + "  •  click again where you want to type"
                : head + "  •  " + RefusalReason;
        }
    }
}
