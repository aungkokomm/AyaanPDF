using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Puts a line back together out of the pieces a document broke it into.
///
/// A run arrives from the core as text uninterrupted by a change of font, size
/// or colour. In a well-behaved document that is a heading. In a badly behaved
/// one it is a WORD: measured on a real book, 4765 runs across seven pages with
/// a median length of three characters and not one of them holding more than a
/// single word, because the producer set every word as its own text object in
/// its own subset font.
///
/// Bookmarking that document by style offered five thousand one-word bookmarks.
/// The fault is here rather than in the file: a heading is a line, not a word,
/// so pieces of one line set the same way are one run.
///
/// Only within a LINE. Joining across lines is a separate question with a
/// separate answer, because a heading that wrapped and two headings in a row
/// look identical from here and only the reader knows which their document has.
/// </summary>
public static class StyledRunMerge
{
    /// <summary>
    /// How far apart two pieces can be, in characters, and still be the same
    /// line of text.
    ///
    /// The gap is whatever fell between them and never reached a run: the
    /// spaces, and any punctuation set in a different font. Wider than that and
    /// something was said in between that is not part of this.
    /// </summary>
    public const int LargestGap = 4;

    /// <summary>
    /// The longest run of punctuation that can still be a mark between words
    /// rather than something the document said.
    ///
    /// A mark is short: a colon, a dash, a bullet. A long one is the row of
    /// leader dots that carries the eye from a contents entry to its page
    /// number, and the two ends of that are not one heading.
    /// </summary>
    public const int LongestSeparator = 8;

    /// <summary>
    /// Runs with the pieces of each line joined, in the order they arrived.
    /// </summary>
    public static IReadOnlyList<StyledRun> JoinLines(IReadOnlyList<StyledRun> runs) =>
        BridgeSeparators(JoinAdjacent(runs));

    /// <summary>
    /// Joins the neighbouring pieces of a line that are set the same way.
    /// </summary>
    private static IReadOnlyList<StyledRun> JoinAdjacent(IReadOnlyList<StyledRun> runs)
    {
        var joined = new List<StyledRun>(runs.Count);

        foreach (var run in runs)
        {
            if (joined.Count > 0 && CanJoin(joined[^1], run))
            {
                joined[^1] = Join(joined[^1], run);
                continue;
            }

            joined.Add(run);
        }

        return joined;
    }

    /// <summary>
    /// Closes a line back up over the punctuation a document set in some other
    /// font.
    ///
    /// The measured case: a chapter title whose two halves are 12pt in one
    /// subset font with the colon between them at 12.5pt in ANOTHER, and a
    /// running header that is four words in one font with dashes in a second.
    /// The pass above cannot touch either, because a change of style is exactly
    /// what it refuses to join across, and it is right to refuse: that is the
    /// only signal the styles list has. A mark is not a change of voice though,
    /// so letting it split the line gives two bookmarks where the page shows
    /// one heading.
    ///
    /// A separator only stops being its own run when the SAME style resumes on
    /// the far side of it. Standing alone it is something the document said.
    /// </summary>
    private static IReadOnlyList<StyledRun> BridgeSeparators(IReadOnlyList<StyledRun> runs)
    {
        var bridged = new List<StyledRun>(runs.Count);

        for (int at = 0; at < runs.Count; at++)
        {
            var run = runs[at];

            if (bridged.Count > 0 && at + 1 < runs.Count && IsSeparator(run))
            {
                var open = bridged[^1];
                var resumed = runs[at + 1];

                if (open.Style == resumed.Style
                    && Adjacent(open, run)
                    && Adjacent(run, resumed))
                {
                    // The words say what the style is. Taking the colon's
                    // 12.5pt would file the heading under a style of one.
                    bridged[^1] = Join(Join(open, run), resumed);
                    at++;
                    continue;
                }
            }

            bridged.Add(run);
        }

        return bridged;
    }

    private static bool IsSeparator(StyledRun run) =>
        run.Text.Length <= LongestSeparator && !run.Text.Any(char.IsLetterOrDigit);

    private static bool CanJoin(StyledRun open, StyledRun next) =>
        Adjacent(open, next) && open.Style == next.Style;

    /// <summary>
    /// Whether one run carries straight on from another: same page, same line,
    /// and no more between them than the spaces that never reached a run.
    /// </summary>
    private static bool Adjacent(StyledRun open, StyledRun next) =>
        open.PageIndex == next.PageIndex
        && open.LineIndex == next.LineIndex
        && next.CharStart >= open.CharEnd
        && next.CharStart - open.CharEnd <= LargestGap;

    /// <summary>
    /// Joins two pieces, putting back a space only where one was taken away.
    ///
    /// Directly adjacent pieces are joined with NOTHING between them: there was
    /// no separator in the document, so inserting one would break a word in
    /// half. That also matters for Devanagari, where a vowel sign and its
    /// consonant can fall either side of a font change, and a space between
    /// them would put the sign beyond repair.
    /// </summary>
    private static StyledRun Join(StyledRun open, StyledRun next)
    {
        bool somethingBetween = next.CharStart > open.CharEnd;

        return open with
        {
            CharCount = next.CharEnd - open.CharStart,
            Text = somethingBetween ? $"{open.Text} {next.Text}" : open.Text + next.Text,
        };
    }
}
