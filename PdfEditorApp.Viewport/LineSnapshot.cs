namespace PdfEditorApp.Viewport;

/// <summary>Why a line cannot be retyped. Mirrors the core's codes exactly.</summary>
public enum LineRefusal
{
    /// <summary>It can.</summary>
    None = 0,

    /// <summary>Some word on the line is rotated or skewed.</summary>
    NotUpright = 1,

    /// <summary>The line is not set in one font at one size. Measured on a real
    /// page: three styles across one line and not one of its words mixed on its
    /// own, so the word path had nothing to object to.</summary>
    MixedStyle = 2,

    /// <summary>The objects that draw the line do not run left to right in
    /// index order.</summary>
    OutOfOrder = 3,

    /// <summary>PDFium reports no font name, so nothing could stand in.</summary>
    NoFontName = 4,

    /// <summary>No text object owns the line.</summary>
    NoObjects = 5,

    /// <summary>The line starts part-way through one object and ends part-way
    /// through another.</summary>
    PartialSpan = 6,

    /// <summary>The line is justified. Its right margin is shared with the rest
    /// of its block, which means its spaces were stretched to reach it, and
    /// retyping would set them all to the font's own width.</summary>
    Justified = 7,

    /// <summary>Two runs of text that merely share a baseline: a label either
    /// side of a form field, a tab stop, a table row.</summary>
    Gapped = 8,

    /// <summary>Some object inside the line's range is drawn by another
    /// line.</summary>
    ForeignObject = 9,

    /// <summary>A script that has to be shaped to be read. Not a judgement
    /// about difficulty: PDFium's reading of these was measured not to
    /// reproduce the source.</summary>
    ComplexScript = 11,
}

/// <summary>Which writer, if any, will be asked to retype a line.</summary>
///
/// <remarks>
/// ⚠️ THE ONE CAPABILITY DECISION, AND EVERYTHING READS IT. The frame colour,
/// what a click selects, and which core call the edit makes are all derived
/// from this and from nothing else. They used to be derived from
/// <see cref="LineRefusal"/> directly, which is the OBJECT writer's rule set,
/// and that made the app paint a line "refused" and refuse to write it while a
/// writer that handles it sat behind the gate untried.
/// </remarks>
public enum LineWriter
{
    /// <summary>No writer will take it. This is the only state that refuses.</summary>
    None = 0,

    /// <summary>The writer that replaces the objects drawing the line.</summary>
    ObjectWriter = 1,

    /// <summary>
    /// The writer that splices the line where it stands, leaving the
    /// producer's own pieces alone.
    /// </summary>
    BlockWriter = 2,

    /// <summary>
    /// The writer for a line the file's own tables cannot spell out, which was
    /// read from the FONT instead and is put back the same way.
    /// </summary>
    /// <remarks>
    /// ⚠️ ADDRESSED BY BASELINE AND TEXT, NOT BY OBJECTS. The producer drew
    /// these lines as dozens of separate placements, so there is no object
    /// range to hand a writer; the recovery writer empties every placement the
    /// line is drawn by and writes one run in their place. That is why a line
    /// routed here carries <see cref="LineSnapshot.Recovered"/> and the other
    /// two do not.
    /// </remarks>
    RecoveryWriter = 3,
}

/// <summary>
/// ONE VISUAL LINE of the document's own text, and the range of page objects
/// that draws it.
///
/// The second editing unit, above <see cref="WordClusterSnapshot"/> and
/// deliberately not built out of it. A word can only ever be swapped for a
/// word; a line is one string, so retyping it may change the number of words.
///
/// ⚠️ WHERE A LINE COMES FROM. Not from grouping words by baseline, which was
/// measured giving 25 groups on a page that has 20 lines: one rotated line
/// shattered into six fragments in reverse reading order and two columns
/// interleaved. PDFium's own character stream carries a break where the
/// producer put one, and the walk that builds the words already reads it.
///
/// <paramref name="FirstObject"/> and <paramref name="LastObject"/> bound the
/// range in the page's CONTENT. They are not annotation indices, and nothing
/// that edits an annotation may be handed one.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </summary>
/// <param name="PrefixChars">
/// How many characters of the first object come before the line. Usually zero,
/// and not when one object holds more than one line.
/// </param>
/// <param name="Text">
/// What the range actually draws. Assembled from the OBJECTS rather than from
/// the character stream, because the stream synthesizes separators PDFium never
/// stored and the reader retypes what they are shown.
/// </param>
public sealed record LineSnapshot(
    int FirstObject,
    int LastObject,
    int PrefixChars,
    int Words,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Baseline,
    double FontSizePts,
    uint ColorRgb,
    LineRefusal Refusal,
    string Text,
    string FontName,
    RecoveredLine? Recovered = null)
{
    /// <summary>Which writer will be asked to retype this line.</summary>
    ///
    /// <remarks>
    /// ⚠️ <see cref="LineRefusal"/> IS THE OBJECT WRITER'S RULE SET, NOT THE
    /// APP'S. Four of its refusals are only refusals for that writer, because
    /// it rebuilds a line into a single object and so needs one font, one size,
    /// whole objects, and even spacing. The block writer needs none of that: it
    /// changes the characters that moved inside the one piece that draws them
    /// and leaves every other operator as the producer wrote it. Justified is
    /// the plainest case of all, since Path A exists precisely to re-solve a
    /// justified line's spacing.
    ///
    /// ⚠️ AND THE REST STAY REFUSED ON PURPOSE. A shaped script is read back
    /// by PDFium in reading order while the stream draws it in visual order, so
    /// the two can agree on length and disagree on meaning; that was measured,
    /// and it is why complex script is refused here rather than attempted.
    /// Rotated, gapped, out-of-order and foreign-object lines are refused
    /// because neither writer handles them.
    ///
    /// ⚠️ EXCEPT WHEN THE LINE WAS RECOVERED, which overrules everything
    /// above. A recovered line did not come from PDFium at all: it was proven
    /// against the FONT, character by character, by reshaping the candidate
    /// text and demanding the page's own glyph ids back. The refusal PDFium's
    /// reading earned is a fact about PDFium's reading and says nothing about
    /// this one.
    /// </remarks>
    public LineWriter Route => Recovered is not null ? LineWriter.RecoveryWriter : Refusal switch
    {
        LineRefusal.None => LineWriter.ObjectWriter,

        LineRefusal.Justified => LineWriter.BlockWriter,
        LineRefusal.MixedStyle => LineWriter.BlockWriter,
        LineRefusal.PartialSpan => LineWriter.BlockWriter,
        LineRefusal.NoFontName => LineWriter.BlockWriter,

        _ => LineWriter.None,
    };

    /// <summary>Whether any writer will accept a retype of this line.</summary>
    ///
    /// ⚠️ DERIVED FROM <see cref="Route"/> AND NOT FROM THE REFUSAL, so the
    /// frame the reader sees and the call the edit makes cannot disagree.
    public bool CanEdit => Route != LineWriter.None;

    /// <summary>
    /// What to tell a reader who tried to retype this line, in their terms
    /// rather than the core's.
    /// </summary>
    public string RefusalReason => Refusal switch
    {
        LineRefusal.None => string.Empty,
        LineRefusal.NotUpright => "This line is rotated, and editing rotated text is not supported yet.",
        LineRefusal.MixedStyle => "This line is set in more than one font or size, so it cannot be retyped as one piece.",
        LineRefusal.OutOfOrder => "This line is stored out of reading order, and retyping it would scramble the text.",
        LineRefusal.NoFontName => "This line does not name its font, so there is no way to match it.",
        LineRefusal.PartialSpan => "This line shares a piece of the page with another one and cannot be replaced cleanly.",
        LineRefusal.Justified => "This line is justified. Retyping it would even out the spacing and the right margin would stop lining up.",
        LineRefusal.Gapped => "This is two separate pieces of text that happen to share a line. Edit a word instead.",
        LineRefusal.ForeignObject => "Another line is stored inside this one, so it cannot be replaced on its own.",
        LineRefusal.ComplexScript => "This script is not read back faithfully enough to retype. Editing it could scramble the text.",
        _ => "This line cannot be retyped.",
    };
}
