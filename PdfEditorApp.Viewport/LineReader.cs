using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decodes the buffer render_core::get_page_lines writes.
///
/// The pure half, here rather than beside the marshalling for the reason
/// <see cref="WordClusterReader"/> is: the app is a WinUI project a test
/// assembly cannot load, and a wire format checked only by running the app is a
/// wire format nobody checks.
///
/// Little-endian: a line count, then per line the first and last object index,
/// the character offset inside the first object, the word count, four bounds, a
/// baseline, a font size, a packed colour, a refusal code, and two
/// length-prefixed UTF-8 strings.
/// </summary>
public static class LineReader
{
    /// <summary>
    /// Every fixed field of one line: four counts, five bounds, a size, a
    /// colour and a refusal.
    ///
    /// ⚠️ COUNTED, not estimated. Four short, and the refusal read runs off the
    /// end of a truncated buffer and throws where the whole point of this
    /// decoder is that it stops instead.
    /// </summary>
    private const int FixedFieldBytes = (4 * 4) + (6 * 4) + 4 + 4;

    /// <summary>
    /// Decodes the buffer, stopping at the first thing that does not make sense
    /// rather than throwing.
    ///
    /// A short or malformed buffer means the core and this build disagree about
    /// the layout, which is a bug, but not one worth taking the app down for:
    /// the lines decoded so far are still correct and the page simply offers
    /// fewer of them.
    /// </summary>
    /// <summary>
    /// Whether this page holds text the core can only read by reshaping it.
    ///
    /// ⚠️ THIS REFUSAL IS THE SIGNAL, AND IT IS FREE. The core cannot decide
    /// for itself whether a document is worth the seventeen seconds that
    /// reading it costs: finding out means either serialising the whole file or
    /// asking PDFium for a page, and asking for a page parses it. The lines are
    /// already read by the time anyone looks at this, and they already say so.
    /// </summary>
    public static bool NeedsReshaping(IReadOnlyList<LineSnapshot>? lines)
    {
        if (lines is null) { return false; }

        foreach (var line in lines)
        {
            if (line.Refusal == LineRefusal.ComplexScript) { return true; }
        }
        return false;
    }

    public static IReadOnlyList<LineSnapshot> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<LineSnapshot>();
        }

        int at = 0;
        uint count = ReadU32(bytes, ref at);
        var found = new List<LineSnapshot>((int)Math.Min(count, 4096));

        for (uint i = 0; i < count; i++)
        {
            if (at + FixedFieldBytes > bytes.Length) { break; }

            int first = (int)ReadU32(bytes, ref at);
            int last = (int)ReadU32(bytes, ref at);
            int prefix = (int)ReadU32(bytes, ref at);
            int words = (int)ReadU32(bytes, ref at);

            double left = ReadF32(bytes, ref at);
            double top = ReadF32(bytes, ref at);
            double right = ReadF32(bytes, ref at);
            double bottom = ReadF32(bytes, ref at);
            double baseline = ReadF32(bytes, ref at);
            double size = ReadF32(bytes, ref at);
            uint color = ReadU32(bytes, ref at);
            uint refusal = ReadU32(bytes, ref at);

            if (!ReadString(bytes, ref at, out string text)) { break; }
            if (!ReadString(bytes, ref at, out string font)) { break; }

            found.Add(new LineSnapshot(
                first, last, prefix, words, left, top, right, bottom, baseline,
                size, color, AsRefusal(refusal), text, font));
        }

        return found;
    }

    /// <summary>
    /// A refusal code this build does not know is still a refusal.
    ///
    /// ⚠️ An unknown reason must never read as "editable". A newer core adding
    /// a case an older app has not heard of would otherwise let through exactly
    /// the write the core had just decided to forbid.
    /// </summary>
    private static LineRefusal AsRefusal(uint code) => code switch
    {
        0 => LineRefusal.None,
        1 => LineRefusal.NotUpright,
        2 => LineRefusal.MixedStyle,
        3 => LineRefusal.OutOfOrder,
        4 => LineRefusal.NoFontName,
        5 => LineRefusal.NoObjects,
        6 => LineRefusal.PartialSpan,
        7 => LineRefusal.Justified,
        8 => LineRefusal.Gapped,
        9 => LineRefusal.ForeignObject,
        11 => LineRefusal.ComplexScript,
        _ => LineRefusal.NoObjects,
    };

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
        if (length < 0 || at + length > bytes.Length) { return false; }

        value = Encoding.UTF8.GetString(bytes, at, (int)length);
        at += (int)length;
        return true;
    }
}
