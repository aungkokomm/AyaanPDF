using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What the scan found, and what that says about the document.
///
/// This exists because of a question that has no good answer otherwise: when
/// bookmarking by style finds nothing, is the feature weak or is the file
/// unreadable? The scan already knows. It has been over every page and can say
/// whether there was any text there at all, whether the text it found could be
/// read as characters, and how many distinct styles came back.
///
/// Saying so turns a dead end into an answer. "No text on 812 of 900 pages"
/// tells a reader to run OCR; silence tells them the feature is broken.
/// </summary>
/// <param name="PagesWithText">Pages that reported at least one character.</param>
/// <param name="PagesWithRuns">Pages that gave at least one run worth reading.</param>
/// <param name="Characters">Characters across the whole document.</param>
public readonly record struct ScanDiagnosis(
    int Pages, int PagesWithText, int PagesWithRuns, int Characters, int Styles)
{
    /// <summary>
    /// How much of a document may be missing its text layer before it is worth
    /// calling a scan. Not all of it: a real scan often has a text page or two
    /// (a cover sheet, an inserted page), and a book with forty plates in the
    /// middle is not a scanned book.
    /// </summary>
    public const double MostlyMissing = 0.6;

    public static ScanDiagnosis Of(IReadOnlyList<StyledRunPage> pages, int styles)
    {
        int withText = 0;
        int withRuns = 0;
        long characters = 0;

        foreach (var page in pages)
        {
            if (page.CharCount > 0) { withText++; }
            if (page.Runs.Count > 0) { withRuns++; }
            characters += page.CharCount;
        }

        return new ScanDiagnosis(
            pages.Count, withText, withRuns,
            (int)Math.Min(characters, int.MaxValue), styles);
    }

    /// <summary>
    /// Nothing to read anywhere. Almost always a scan: pictures of pages, with
    /// no text layer behind them.
    /// </summary>
    public bool LooksScanned =>
        Pages > 0 && PagesWithText <= Pages * (1 - MostlyMissing);

    /// <summary>
    /// Text is there and none of it survives as characters.
    ///
    /// A font with no /ToUnicode map. Worth telling apart from a scan, because
    /// OCR fixes a scan and nothing fixes this: the document defeats every
    /// reader's search, not only this one.
    /// </summary>
    public bool LooksUnreadable =>
        PagesWithText > 0 && PagesWithRuns == 0;

    /// <summary>The line the dialog shows once the scan finishes.</summary>
    public string Describe()
    {
        if (Pages == 0)
        {
            return "Nothing to scan.";
        }

        if (LooksScanned)
        {
            return PagesWithText == 0
                ? $"No text on any of the {Pages} pages. This document is a scan: it holds pictures of "
                  + "pages, with no text behind them. Bookmarking by style needs text, so this needs OCR first."
                : $"No text on {Pages - PagesWithText} of {Pages} pages. Most of this document is a scan, "
                  + "so only the pages that do have text can be bookmarked this way.";
        }

        if (LooksUnreadable)
        {
            return $"There is text on {PagesWithText} pages, but none of it comes back as readable "
                 + "characters. This document's fonts carry no Unicode mapping, which defeats searching "
                 + "and copying in any reader, not just this one.";
        }

        if (Styles == 0)
        {
            return $"Text found on {PagesWithText} pages, but no styles came back. Every run may have "
                 + "been longer than a heading.";
        }

        string reach = PagesWithText == Pages
            ? $"all {Pages} pages"
            : $"{PagesWithText} of {Pages} pages";

        return $"{Styles} {(Styles == 1 ? "style" : "styles")} across {reach}, biggest first. "
             + "Tick the ones your headings are set in.";
    }
}
