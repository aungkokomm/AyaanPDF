using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

// ⚠️⚠️ THE OVERLAY IS THE LOCATION OF THE PAGE'S OWN TEXT. IT IS NOT AN EDITOR.
//
// A SETTLED PRODUCT REQUIREMENT, not a preference and not open again. Editing
// must feel like editing the PDF's own text, in place:
//
//   click the text  ->  that text becomes editable where it already is
//
// and explicitly NOT:
//
//   click the text  ->  a separate box appears  ->  type into that box
//
// The second reads as editing some other object that happens to sit on top of
// the page. The page must stay visually stable, and the thing the reader is
// interacting with must be the text itself.
//
// A bounding box is welcome as an affordance: it can show what is selected and
// what the structure is. What it may never become is a second editing surface.
//
// What that requires of THIS model, which is why the note lives here:
//
//   - the caret is positioned from real character geometry, so every character
//     carries its own bounds (see TextCharacter), not just the line;
//   - the editing surface has to coincide with the original text, so the
//     bounds are the drawn glyphs' own, never a box drawn to hold an editor;
//   - PdfPig supplies that geometry and Skia draws the interaction over it;
//   - nothing here may be shaped around a modal editor or a floating
//     replacement text box, and the existing modal interaction is NOT to be
//     preserved merely because it is already written.
//
// No caret and no typing yet. This note exists so that when they are added,
// they are added in the right place.

/// <summary>Why a line of the document's own text can or cannot be offered as
/// an editable region.</summary>
public enum TextRegionStatus
{
    /// <summary>Every character was placed. The line can be offered.</summary>
    Ok = 0,

    /// <summary>
    /// A script that has to be shaped to be read.
    ///
    /// ⚠️ AN EXPLICIT NON-GOAL, NOT A BUG TO FIX HERE. Measured over the
    /// corpus: 1.2% of Myanmar characters could be placed, against 98% of
    /// Latin. The two readers disagree about both the order and the content of
    /// shaped text, and guessing between them would put a caret on the wrong
    /// glyph. These lines keep the behaviour they already have.
    /// </summary>
    ShapedScript = 1,

    /// <summary>
    /// The characters on this line could not be matched to the line's own text.
    ///
    /// ⚠️ USUALLY OUR FAULT, NOT THE READER'S. Measured on real invoices: our
    /// line grouping merges table columns that the page's geometry keeps apart,
    /// so the line claims text that physically sits on another column. Marked
    /// rather than guessed at, and not offered.
    /// </summary>
    Unmapped = 2,
}

/// <summary>One character of the document's own text, and where it sits.</summary>
/// <remarks>
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH. <paramref name="Offset"/> is the index of
/// this character in its line's text, which is what an edit is addressed by.
/// </remarks>
/// <param name="ColorHex">
/// The colour the page draws this glyph in, as #RRGGBB.
///
/// ⚠️ CARRIED BECAUSE AN EDIT HAS TO MATCH IT. While a line is being edited the
/// changed tail is redrawn by this app rather than by the page, and drawing it
/// in an assumed black over a page that sets its headings in colour would make
/// the reader's own text change colour as they typed in it.
/// </param>
public sealed record TextCharacter(
    string Text,
    int Offset,
    double Left,
    double Top,
    double Right,
    double Bottom,
    string FontName,
    double PointSize,
    string ColorHex);

/// <summary>A stretch of one line set in a single font at a single size.</summary>
public sealed record TextRun(
    string Text,
    double Left,
    double Top,
    double Right,
    double Bottom,
    string FontName,
    double PointSize,
    IReadOnlyList<TextCharacter> Characters);

/// <summary>
/// One visual line, tied to the range of page objects that draws it.
/// </summary>
/// <remarks>
/// ⚠️ THE OBJECT RANGE IS WHAT THE WRITER SPEAKS. The geometry here comes from
/// the read side; <paramref name="FirstObject"/> and <paramref name="LastObject"/>
/// come from the line the core already published, and they are carried through
/// unchanged so that a later phase can hand them straight to the writer without
/// inventing an anchor.
/// </remarks>
public sealed record TextLine(
    string Text,
    int FirstObject,
    int LastObject,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Baseline,
    TextRegionStatus Status,
    IReadOnlyList<TextRun> Runs)
{
    /// <summary>Every character of the line, in reading order.</summary>
    public IEnumerable<TextCharacter> Characters
    {
        get
        {
            foreach (var run in Runs)
            {
                foreach (var c in run.Characters) { yield return c; }
            }
        }
    }

    /// <summary>Whether this line may be offered as editable.</summary>
    public bool CanOffer => Status == TextRegionStatus.Ok;
}

/// <summary>
/// A block of lines that belong together on the page: a paragraph, a heading,
/// a table cell.
/// </summary>
public sealed record TextRegion(
    double Left,
    double Top,
    double Right,
    double Bottom,
    IReadOnlyList<TextLine> Lines)
{
    /// <summary>
    /// Whether any line in this region can be edited.
    ///
    /// A region with nothing offerable in it is not drawn: a box around text
    /// that cannot be clicked is a promise the app does not keep.
    /// </summary>
    public bool CanOffer
    {
        get
        {
            foreach (var line in Lines)
            {
                if (line.CanOffer) { return true; }
            }
            return false;
        }
    }
}
