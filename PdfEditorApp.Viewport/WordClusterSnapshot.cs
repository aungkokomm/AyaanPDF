using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>Why a word cannot be rewritten. Mirrors the core's codes exactly.</summary>
public enum ClusterRefusal
{
    /// <summary>It can.</summary>
    None = 0,

    /// <summary>Rotated or skewed. Copying such a matrix onto a replacement was
    /// measured to move and resize the word, so it is left alone.</summary>
    NotUpright = 1,

    /// <summary>Two fonts or two sizes inside one word, so there is no single
    /// style a replacement could be written in.</summary>
    MixedStyle = 2,

    /// <summary>The word's objects are not consecutive on the page. Measured on
    /// Burmese, where stacked marks arrive out of reading order.</summary>
    SplitObjects = 3,

    /// <summary>PDFium reports no font name for the object, so nothing
    /// identifies a font that could stand in for it.</summary>
    NoFontName = 4,

    /// <summary>No text object owns these characters.</summary>
    NoObjects = 5,

    /// <summary>The word begins part-way through one object and ends part-way
    /// through another, so there is no single string to splice it into.</summary>
    PartialSpan = 6,

    /// <summary>
    /// The line this word sits on is JUSTIFIED, and re-spacing one is not
    /// built.
    ///
    /// ⚠️ THE SAME OBJECTION AS THE LINE'S, AND THE SAME NUMBER. The word is
    /// the app's fallback when a line declines, so without this the reader is
    /// handed the word instead and edits a justified paragraph one word at a
    /// time, leaving the right margin ragged. The word is told this by the
    /// line; it measures nothing of its own.
    /// </summary>
    Justified = 7,
}

/// <summary>
/// ONE WORD of the document's own text, and the objects that draw it.
///
/// The unit the reader works in, and deliberately not a PDF text object. A
/// producer decides for itself where one object ends: measured on real files,
/// Chromium emits ONE OBJECT PER GLYPH, 565 of them for four short paragraphs,
/// while other producers emit one per run. Offering those to a reader would
/// mean offering to edit one letter at a time on some documents and one
/// paragraph at a time on others, for reasons that are nothing to do with them.
///
/// PDFium's character stream is what makes this possible: it already
/// reassembles the objects into readable text with word spacing, and every
/// character can name the object that draws it.
///
/// <paramref name="ObjectIndices"/> are positions in the page's CONTENT, which
/// is what the core's write takes. They are NOT annotation indices, and nothing
/// that edits an annotation may be handed one.
///
/// Bounds are normalized the way the whole app draws: top-left origin, BOTH
/// axes divided by the page WIDTH.
/// </summary>
/// <param name="Baseline">
/// Where the word sits on its line, in the same units as the bounds. What
/// groups words into lines, and later into paragraphs, without needing a second
/// pass over the page.
/// </param>
/// <param name="PrefixChars">
/// How many characters of the first object come BEFORE this word.
///
/// ⚠️ THIS IS WHAT IDENTIFIES THE WORD, together with the object list and not
/// instead of it. A producer that emits one text object per LINE gives every
/// word on that line the same objects: a real invoice had TELECOM,
/// INTERNATIONAL, MYANMAR and COMPANY all claiming object 15. Sending only the
/// objects back would edit whichever of them came first.
/// </param>
/// <param name="Refusal">
/// Why this word cannot be rewritten, or <see cref="ClusterRefusal.None"/>. It
/// travels WITH the word because a word that cannot be edited can still be
/// selected and read, and the app can say why rather than doing nothing.
/// </param>
public sealed record WordClusterSnapshot(
    int FirstObjectIndex,
    IReadOnlyList<int> ObjectIndices,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Baseline,
    double FontSizePts,
    uint ColorRgb,
    ClusterRefusal Refusal,
    int PrefixChars,
    string Text,
    string FontName)
{
    /// <summary>Whether the core would accept a rewrite of this word.</summary>
    public bool CanEdit => Refusal == ClusterRefusal.None;

    /// <summary>
    /// What to tell a reader who tried to edit this word, in their terms rather
    /// than the core's.
    /// </summary>
    public string RefusalReason => Refusal switch
    {
        ClusterRefusal.None => string.Empty,
        ClusterRefusal.NotUpright => "This text is rotated, and editing rotated text is not supported yet.",
        ClusterRefusal.MixedStyle => "This word is set in more than one font or size.",
        ClusterRefusal.SplitObjects => "This script stores its marks out of order, and editing it would scramble the text.",
        ClusterRefusal.NoFontName => "This text does not name its font, so there is no way to match it.",
        ClusterRefusal.PartialSpan => "This word is split across two pieces of the page and cannot be replaced cleanly.",
        ClusterRefusal.Justified => "This line is justified, and editing justified text is not supported yet.",
        _ => "This text cannot be edited.",
    };
}
