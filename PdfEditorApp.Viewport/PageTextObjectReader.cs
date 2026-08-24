using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decodes the buffer render_core::get_page_text_objects writes.
///
/// The pure half, here rather than beside the marshalling for the reason
/// <see cref="StyledRunReader"/> is: the app is a WinUI project a test assembly
/// cannot load, and a wire format checked only by running the app is a wire
/// format nobody checks.
///
/// Little-endian: an object count, then per object an object index, four
/// normalized bounds, a font size in points, a packed colour, a flags word, and
/// two length-prefixed UTF-8 strings.
/// </summary>
public static class PageTextObjectReader
{
    /// <summary>Bit 0 of the flags word: the font travels with the document.</summary>
    private const uint FontEmbedded = 1;

    /// <summary>The eight fixed fields that precede the first string.</summary>
    private const int FixedFieldBytes = 32;

    /// <summary>
    /// Decodes the buffer, stopping at the first thing that does not make sense
    /// rather than throwing.
    ///
    /// A short or malformed buffer means the core and this build disagree about
    /// the layout, which is a bug, but not one worth taking the app down for:
    /// the objects decoded so far are still correct and the page simply offers
    /// fewer of them. A count larger than the buffer can hold is the same case
    /// and must not be trusted into an over-read.
    /// </summary>
    public static IReadOnlyList<PageTextSnapshot> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<PageTextSnapshot>();
        }

        int at = 0;
        uint count = ReadU32(bytes, ref at);
        var found = new List<PageTextSnapshot>((int)Math.Min(count, 4096));

        for (uint i = 0; i < count; i++)
        {
            if (at + FixedFieldBytes > bytes.Length) { break; }

            int objectIndex = (int)ReadU32(bytes, ref at);
            double left = ReadF32(bytes, ref at);
            double top = ReadF32(bytes, ref at);
            double right = ReadF32(bytes, ref at);
            double bottom = ReadF32(bytes, ref at);
            double sizePts = ReadF32(bytes, ref at);
            uint color = ReadU32(bytes, ref at);
            bool embedded = (ReadU32(bytes, ref at) & FontEmbedded) == FontEmbedded;

            if (!ReadString(bytes, ref at, out string fontName)) { break; }
            if (!ReadString(bytes, ref at, out string text)) { break; }

            found.Add(new PageTextSnapshot(
                objectIndex, left, top, right, bottom,
                sizePts, color, embedded, fontName, text));
        }

        return found;
    }

    private static uint ReadU32(byte[] bytes, ref int at)
    {
        uint value = BitConverter.ToUInt32(bytes, at);
        at += 4;
        return value;
    }

    private static double ReadF32(byte[] bytes, ref int at)
    {
        float value = BitConverter.ToSingle(bytes, at);
        at += 4;
        return value;
    }

    private static bool ReadString(byte[] bytes, ref int at, out string value)
    {
        value = string.Empty;
        if (at + 4 > bytes.Length) { return false; }

        long length = ReadU32(bytes, ref at);
        if (at + length > bytes.Length) { return false; }

        value = Encoding.UTF8.GetString(bytes, at, (int)length);
        at += (int)length;
        return true;
    }
}
