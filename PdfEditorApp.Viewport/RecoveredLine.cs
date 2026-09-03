using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Where one CLUSTER of a recovered line is drawn, and which characters of the
/// line's text it draws.
/// </summary>
/// <remarks>
/// ⚠️ A CLUSTER, NOT A CHARACTER, AND THE SCRIPT FORCES IT. Burmese draws
/// မြ as one unit with the medial before the consonant it follows, so the page
/// has no position between the two of them. A caret offered one would be
/// standing somewhere the page does not have, and the character it appeared to
/// be beside would not be the character an edit there would change.
///
/// <see cref="From"/> and <see cref="To"/> are BYTE offsets into the UTF-8 the
/// core read, which is not the same as character offsets into a .NET string.
/// <see cref="RecoveredLineReader"/> converts them on the way in, so everything
/// above it counts the way the rest of the app counts.
/// </remarks>
public sealed record RecoveredCluster(int From, int To, double Left, double Right);

/// <summary>
/// ONE VISUAL LINE of a page whose script the file's own tables cannot spell
/// out, read from the FONT instead, with a box for every cluster.
/// </summary>
/// <remarks>
/// ⚠️ THIS IS NOT A <see cref="LineSnapshot"/> AND MUST NOT BE MISTAKEN FOR
/// ONE. A line snapshot is addressed by the page OBJECTS that draw it; a
/// recovered line has no such address, because the producer drew it as dozens
/// of separate placements and the writer for it puts them all back at once.
/// Its identity is <see cref="PdfBaseline"/> together with <see cref="Text"/>,
/// and both must be handed back exactly as they came for the core to accept a
/// retype.
///
/// ⚠️ TWO COORDINATE SYSTEMS, ON PURPOSE. <see cref="PdfBaseline"/> is in PDF
/// user space because that is the identity the core takes back. Everything else
/// is normalized the way the whole app draws: top-left origin, BOTH axes
/// divided by the page WIDTH.
/// </remarks>
public sealed record RecoveredLine(
    double PdfBaseline,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Baseline,
    double FontSizePts,
    string Text,
    string FontName,
    IReadOnlyList<RecoveredCluster> Clusters)
{
    /// <summary>
    /// Whether the core could prove what this line says.
    /// </summary>
    /// <remarks>
    /// ⚠️ A LINE THAT COULD NOT BE READ IS STILL REPORTED, and that is
    /// deliberate: the caller can see that the page has a line there which
    /// recovery declined, rather than silently seeing nothing at all.
    /// </remarks>
    public bool WasRead => Text.Length > 0 && Clusters.Count > 0;
}
