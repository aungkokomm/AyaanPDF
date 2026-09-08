using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Everything the editing subsystems need to know about ONE PAGE'S TEXT.
/// </summary>
/// <remarks>
/// ⚠️ ONE ANSWER, ASKED ONCE, READ BY EVERYONE. Selection, the caret, the input
/// method and the writer each used to work out the nature of a page's text for
/// themselves: selection by asking two readers and believing whichever answered,
/// the caret by inferring shaping from which code path it happened to be on, the
/// writer by keeping its own copy of the core's font table with a comment saying
/// the two must not drift. Four subsystems, four independent answers to one
/// question about one page.
///
/// ⚠️ AND IT NAMES NO SCRIPT. Every field is DERIVED from what the page turned
/// out to contain, never declared from a list of languages. Recovery proves a
/// reading by reshaping candidate text and demanding the page's own glyph ids
/// back, so nothing here has to know what Burmese or Devanagari are, and adding
/// a third script must not add a branch.
///
/// ⚠️ IT IS CHEAP, AND THAT IS WHAT MAKES IT SAFE TO THROW AWAY. The expensive
/// thing is the per-face index the core builds, which stays where it is, keyed
/// by document handle, and is carried across a rewrite by `adopt_recovery`.
/// This is derived from that in microseconds, so a page change, a write or a new
/// handle can simply drop it and ask again rather than migrating anything.
/// </remarks>
public sealed record PageTextContext(
    int Page,
    IReadOnlyList<LineSnapshot> Lines,
    bool Shaped,
    bool Settled,
    bool RecoveryOwnsText,
    TextDirection Direction,
    IReadOnlyDictionary<string, string> Faces)
{
    /// <summary>A page whose text PDFium reads correctly, which is most of them.</summary>
    public static PageTextContext Plain(int page, IReadOnlyList<LineSnapshot> lines) =>
        new(page, lines,
            Shaped: false, Settled: true, RecoveryOwnsText: false,
            TextDirection.LeftToRight, EmptyFaces);

    /// <summary>
    /// A page that needs reshaping and has not been read yet.
    /// </summary>
    /// <remarks>
    /// ⚠️ SHAPED AND UNSETTLED IS NOT THE SAME AS RECOVERY OWNING THE TEXT, and
    /// keeping them apart is the whole reason there are three flags rather than
    /// one. While the background read is running, and on any line it finally
    /// declines, PDFium's fragments are all the page has, and something that
    /// treated "this page is shaped" as "recovery owns this text" would take
    /// them away and leave nothing.
    /// </remarks>
    public static PageTextContext Preparing(int page, IReadOnlyList<LineSnapshot> lines) =>
        new(page, lines,
            Shaped: true, Settled: false, RecoveryOwnsText: false,
            TextDirection.LeftToRight, EmptyFaces);

    /// <summary>
    /// Whether an edit on this page should host a composing input method.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE SEAM, NOT A DECISION. Every edit hosts one today and this returns
    /// so, unconditionally: a composing keyboard is how the reader writes Hindi,
    /// and a page that does not need one is not harmed by having it. It exists
    /// so that the question has somewhere to live when it stops having one
    /// answer, rather than a branch appearing in the input code later.
    /// </remarks>
    public bool HostsComposition => true;

    /// <summary>
    /// The face recovery proved a font's reading with, or null when it did not
    /// read that font.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE CORE'S ANSWER, NOT A SECOND COPY OF ITS TABLE. `SystemFontMatch`
    /// keeps its own family-to-file list and its own comment warning that it
    /// must not drift from `recover::installed`, because a font offered here
    /// that the core will not accept turns a clean refusal into a failed write
    /// after the reader has finished typing. Asking what the core ACTUALLY read
    /// the page with cannot drift from itself.
    /// </remarks>
    public string? FaceFor(string fontName) =>
        fontName is not null && Faces.TryGetValue(fontName, out string? path) ? path : null;

    private static readonly IReadOnlyDictionary<string, string> EmptyFaces =
        new Dictionary<string, string>();
}
