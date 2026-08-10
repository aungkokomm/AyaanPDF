using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Serializes an outline for <c>write_outline</c>.
///
/// Deliberately the same layout <c>get_bookmarks</c> produces, so an outline
/// can be read out of a document and written back without translation, which is
/// what will make editing existing bookmarks possible later without a second
/// format to keep in step.
/// </summary>
public static class BookmarkWriter
{
    /// <summary>
    /// Packs detected headings into the buffer the core expects.
    ///
    /// Levels are 1-based coming in (the first heading is level 1) and depths
    /// are 0-based going out, matching what the reader reports. Getting that
    /// off by one would put every entry one level too deep.
    /// </summary>
    public static byte[] Serialise(IReadOnlyList<DetectedHeading> headings)
    {
        var bytes = new List<byte>(headings.Count * 32);
        bytes.AddRange(BitConverter.GetBytes((uint)headings.Count));

        foreach (var h in headings)
        {
            bytes.AddRange(BitConverter.GetBytes(Math.Max(0, h.Level - 1)));
            bytes.AddRange(BitConverter.GetBytes(h.PageIndex));

            // The LENGTH IS IN BYTES, not characters. A Devanagari or Burmese
            // title is several bytes per character, and a character count would
            // truncate every one of them.
            byte[] utf8 = Encoding.UTF8.GetBytes(h.Title ?? string.Empty);
            bytes.AddRange(BitConverter.GetBytes((uint)utf8.Length));
            bytes.AddRange(utf8);
        }

        return bytes.ToArray();
    }
}
