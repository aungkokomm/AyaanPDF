using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Adding, renaming and removing one entry of an outline.
///
/// The two bulk features build an outline from scratch and replace whatever the
/// document had. This is the other half, and the one a reader reaches for
/// first: mark the page in front of them, fix a title that came out wrong,
/// drop one that did not belong.
///
/// Every operation takes the outline as it stands and returns a whole new one,
/// because that is what the writer accepts: an outline is written as a tree in
/// one go, and there is no "insert a bookmark" in the file format.
/// </summary>
public static class OutlineEdits
{
    /// <summary>
    /// The longest title a bookmark keeps.
    ///
    /// A reader who selects a paragraph and presses the shortcut means the
    /// first line of it. The whole paragraph in the panel would push every
    /// other entry off the side, and no outline pane in any reader shows more
    /// than a line.
    /// </summary>
    public const int LongestTitle = 120;

    /// <summary>
    /// A title from whatever text was selected, or a fallback naming the page.
    ///
    /// The fallback is not a placeholder to be replaced later: a bookmark on
    /// page 412 called "Page 412" is a true statement and a usable landmark,
    /// which "Untitled" is not.
    /// </summary>
    public static string TitleFrom(string? selectedText, int pageIndex)
    {
        // Repaired first. A selection made in a document that records its text
        // in painting order arrives with its Devanagari vowel signs in front of
        // their consonants, and a bookmark named after it would be unreadable.
        string tidied = Tidy(DevanagariText.Repair(selectedText ?? string.Empty));

        if (tidied.Length == 0)
        {
            return $"Page {pageIndex + 1}";
        }

        if (tidied.Length <= LongestTitle)
        {
            return tidied;
        }

        // Cut at a word boundary where there is one nearby, so a truncated
        // title does not end mid-word. The ellipsis says it was cut.
        string cut = tidied[..LongestTitle];
        int lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > LongestTitle / 2)
        {
            cut = cut[..lastSpace];
        }

        return cut.TrimEnd() + "…";
    }

    /// <summary>
    /// The outline with one more entry, placed in page order.
    ///
    /// After the last entry on an earlier page, rather than appended: a
    /// document's outline reads in page order, and a bookmark for page 12 at
    /// the bottom of a list that reaches page 400 is one nobody will find
    /// again. Added at the top level, because a new landmark has no parent
    /// until the reader says otherwise.
    /// </summary>
    public static IReadOnlyList<DetectedHeading> Add(
        IReadOnlyList<Bookmark> existing, string title, int pageIndex)
    {
        var headings = ToHeadings(existing);

        int at = headings.Count;
        for (int i = 0; i < headings.Count; i++)
        {
            // The first entry that belongs AFTER the new one. An entry with no
            // target sorts nowhere, so it never displaces anything.
            if (headings[i].PageIndex > pageIndex)
            {
                at = i;
                break;
            }
        }

        headings.Insert(at, new DetectedHeading(title, 1, pageIndex));
        return Repair(headings);
    }

    /// <summary>The outline with one entry's title replaced. An empty or
    /// whitespace title is refused: it would show as a blank row.</summary>
    public static IReadOnlyList<DetectedHeading> Rename(
        IReadOnlyList<Bookmark> existing, int index, string title)
    {
        var headings = ToHeadings(existing);
        string tidied = Tidy(title);

        if (index < 0 || index >= headings.Count || tidied.Length == 0)
        {
            return headings;
        }

        headings[index] = headings[index] with { Title = tidied };
        return Repair(headings);
    }

    /// <summary>
    /// The outline without one entry.
    ///
    /// Its children come UP a level rather than going with it. Deleting a
    /// chapter heading should not silently take every section under it: that
    /// is a great deal of destruction from one keystroke, and the reader who
    /// wanted it can delete them too.
    /// </summary>
    public static IReadOnlyList<DetectedHeading> Remove(IReadOnlyList<Bookmark> existing, int index)
    {
        var headings = ToHeadings(existing);

        if (index < 0 || index >= headings.Count)
        {
            return headings;
        }

        headings.RemoveAt(index);
        return Repair(headings);
    }

    /// <summary>
    /// Levels made expressible as a tree again, and consecutive duplicates
    /// left alone.
    ///
    /// Removing a parent can leave a child two levels deeper than anything
    /// above it, which is an outline entry a PDF cannot hold. The shared repair
    /// pulls it up. Its duplicate-dropping does not apply here, since a reader
    /// who deliberately made two bookmarks with the same name meant to.
    /// </summary>
    private static IReadOnlyList<DetectedHeading> Repair(List<DetectedHeading> headings)
    {
        var fixedUp = new List<DetectedHeading>(headings.Count);
        int previous = 0;

        foreach (var heading in headings)
        {
            int level = Math.Min(heading.Level, previous + 1);
            fixedUp.Add(heading with { Level = level });
            previous = level;
        }

        return fixedUp;
    }

    /// <summary>
    /// The panel's list back into what the writer takes. Depth counts from
    /// zero and level from one, which is the one difference between them.
    /// </summary>
    private static List<DetectedHeading> ToHeadings(IReadOnlyList<Bookmark> marks) =>
        marks.Select(m => new DetectedHeading(m.Title, m.Depth + 1, m.PageIndex)).ToList();

    private static string Tidy(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
