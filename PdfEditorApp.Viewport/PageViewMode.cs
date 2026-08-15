namespace PdfEditorApp.Viewport;

/// <summary>
/// How many pages the viewport puts on screen at once.
///
/// A property of how someone is reading rather than of the document, so it is
/// remembered between sessions the same way the theme is. Someone reading
/// slides one at a time wants that for the next deck too.
/// </summary>
public enum PageViewMode
{
    /// <summary>
    /// The whole document as one scrolling stack. What a reference document,
    /// a report or anything read by searching wants.
    /// </summary>
    Continuous,

    /// <summary>
    /// One page at a time, with nothing above or below it.
    ///
    /// The mode for anything laid out as pages rather than as flowing text:
    /// slides, forms, scans, sheet music. Scrolling stays within the page and
    /// running off the end of the document does not scroll into the next one,
    /// because the next one is not there.
    /// </summary>
    SinglePage,
}
