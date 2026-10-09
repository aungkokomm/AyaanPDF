using System.Text.RegularExpressions;

namespace PdfEditorApp.Viewport;

/// <summary>
/// A retyped line as it can be written into a page.
/// </summary>
/// <remarks>
/// ⚠️ TWO SPACES ARE WRITTEN AS ONE. PDFium, like every PDF reader, reads a run
/// of spaces back as a single space, so the core's check that the page now says
/// what was typed failed and the whole edit was refused, with a message blaming
/// the letters. The reader typed " and to " after "from", landing a second space
/// before "nine": refused as typed, written at once with one space.
/// </remarks>
public static partial class TypedLine
{
    /// <summary>The text to write for what the reader typed.</summary>
    public static string ForWriting(string typed) => Spaces().Replace(typed.Trim(), " ");

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
