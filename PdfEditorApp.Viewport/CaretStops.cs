using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Which way a run of text runs.
/// </summary>
/// <remarks>
/// ⚠️ THE ONE PLACE DIRECTION IS ALLOWED TO EXIST. Everything else in the
/// caret model asks a stop list where a position is and gets an x back, so a
/// right-to-left run needs no second code path anywhere: only
/// <see cref="CaretStops.Of"/> has to know that "before this cluster" is its
/// RIGHT edge rather than its left. Nothing here is exercised by a
/// right-to-left file yet, and that is the point of having it now: retro-fitting
/// direction after the fact would touch every one of those callers.
/// </remarks>
public enum TextDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// One position a caret may actually occupy: where it is in the TEXT, where it
/// is on the PAGE, and which piece of the line it belongs to.
/// </summary>
/// <remarks>
/// ⚠️ LOGICAL AND VISUAL ARE SEPARATE ON PURPOSE, and keeping them apart is
/// the whole design. <see cref="Offset"/> counts characters in the string the
/// writer will be handed; <see cref="X"/> is where the page draws that boundary.
/// For Latin, Devanagari and Burmese the two orders agree, so sorting by either
/// gives the same list and nothing is lost by having both. For a right-to-left
/// run they disagree completely: the arrow keys must walk <see cref="X"/> and
/// backspace must use <see cref="Offset"/>.
///
/// ⚠️ AND <see cref="Piece"/> IS WHAT KEEPS THE WRITER OUT OF THIS. A visual
/// line of a recovered page is several separately placed pieces, and each is
/// still a whole unit to the recovery and the writer. The caret roams the line;
/// a commit still goes back one piece at a time, exactly as it did.
/// </remarks>
public readonly record struct CaretStop(int Offset, double X, int Piece);

/// <summary>
/// Turning cluster geometry into the positions a caret may stand at.
/// </summary>
/// <remarks>
/// ⚠️ THE SHAPER'S CLUSTERS ARE THE ONLY AUTHORITY HERE, and that is a
/// correction. The buffer used to move the caret by .NET text elements while
/// the drawing placed it by the shaper's clusters, and the two segmentations do
/// not agree. Measured against the page's own faces, with the reader's own
/// words:
///
/// <code>
///   word           shaper stops        StringInfo stops
///   dharmakshetra  [0,1,4,8,11]   5    [0,1,3,4,6,8,10,11]  8
///   putra          [0,2,5]        3    [0,2,4,5]            4
///   samaapt        [0,1,3,6]      4    [0,1,3,5,6]          5
///   kyawnaw        [0,3,5,9]      4    [0,3,5,7,9]          5
///   akhara         [0,1,4,6]      4    [0,1,3,4,5,6]        6
/// </code>
///
/// Every extra StringInfo stop is an offset the page has no position for, so
/// <c>EditGlyphs.XOf</c> drew it at the NEXT cluster's edge and the caret did
/// not appear to move. Three of dharmakshetra's eight presses were dead. It is
/// not only cosmetic: a backspace at one of those offsets removes characters
/// from inside the cluster the caret looked like it was standing after, and the
/// live tail redraws the conjunct split in half.
/// </remarks>
public static class CaretStops
{
    /// <summary>
    /// The stops of ONE piece, one per cluster boundary plus the end of its
    /// text.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE TRAILING STOP IS FOUND BY OFFSET, NOT BY POSITION IN THE LIST.
    /// Clusters arrive in the order the page DRAWS them, which for a
    /// right-to-left run is the reverse of the order the text reads in, so
    /// "the last one in the list" is the wrong cluster to hang the end of the
    /// text on. Asking for the highest offset is right either way.
    /// </remarks>
    public static List<CaretStop> Of(
        IReadOnlyList<EditGlyph> clusters,
        int textLength,
        TextDirection direction = TextDirection.LeftToRight,
        int piece = 0)
    {
        var stops = new List<CaretStop>(clusters.Count + 1);
        if (clusters.Count == 0) { return stops; }

        bool ltr = direction == TextDirection.LeftToRight;

        var lastLogical = clusters[0];
        foreach (var c in clusters)
        {
            stops.Add(new CaretStop(c.Offset, ltr ? c.Left : c.Right, piece));
            if (c.Offset >= lastLogical.Offset) { lastLogical = c; }
        }

        stops.Add(new CaretStop(
            Math.Max(textLength, lastLogical.Offset),
            ltr ? lastLogical.Right : lastLogical.Left,
            piece));

        return stops;
    }

    /// <summary>
    /// The offsets alone, ascending and without repeats: what the buffer is
    /// told, so that it can move the caret without knowing what a cluster is.
    /// </summary>
    public static List<int> OffsetsOf(IReadOnlyList<CaretStop> stops)
    {
        var offsets = new List<int>(stops.Count);
        foreach (var s in stops)
        {
            if (!offsets.Contains(s.Offset)) { offsets.Add(s.Offset); }
        }
        offsets.Sort();
        return offsets;
    }

    /// <summary>
    /// The same stops ordered the way the page draws them, left to right.
    /// </summary>
    /// <remarks>
    /// ⚠️ THIS IS THE ORDER THE ARROW KEYS WALK, and for a right-to-left run
    /// it is not the order the text reads in. Left means visual left, which is
    /// what the key is called and what a reader's hand means by it.
    /// </remarks>
    public static List<CaretStop> InVisualOrder(IReadOnlyList<CaretStop> stops)
    {
        var visual = new List<CaretStop>(stops);
        visual.Sort(static (a, b) =>
            a.X != b.X ? a.X.CompareTo(b.X)
            : a.Piece != b.Piece ? a.Piece.CompareTo(b.Piece)
            : a.Offset.CompareTo(b.Offset));
        return visual;
    }

    /// <summary>The stop nearest an x, which is what a click means.</summary>
    public static int NearestTo(IReadOnlyList<CaretStop> visual, double x)
    {
        int best = -1;
        double nearest = double.MaxValue;
        for (int i = 0; i < visual.Count; i++)
        {
            double gap = Math.Abs(visual[i].X - x);
            if (gap < nearest) { nearest = gap; best = i; }
        }
        return best;
    }

    /// <summary>Where a caret at an offset in a piece sits in the visual list.</summary>
    public static int IndexOf(IReadOnlyList<CaretStop> visual, int piece, int offset)
    {
        for (int i = 0; i < visual.Count; i++)
        {
            if (visual[i].Piece == piece && visual[i].Offset == offset) { return i; }
        }
        return -1;
    }
}
