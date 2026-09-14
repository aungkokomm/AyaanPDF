using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decodes the buffer render_core::recover_page_text writes.
///
/// The pure half, here rather than beside the marshalling for the reason
/// <see cref="LineReader"/> is: the app is a WinUI project a test assembly
/// cannot load, and a wire format checked only by running the app is a wire
/// format nobody checks.
///
/// Little-endian: a line count, then per line the baseline in PDF user space,
/// four normalized bounds, a normalized baseline, a font size in points, two
/// length-prefixed UTF-8 strings, and a cluster count followed by a first byte,
/// a last byte, a left and a right for each, then the line's paragraph number.
/// </summary>
public static class RecoveredLineReader
{
    /// <summary>
    /// Every fixed field of one line: a PDF baseline, four bounds, a
    /// normalized baseline and a size.
    /// </summary>
    private const int FixedFieldBytes = 7 * 4;

    /// <summary>
    /// Decodes the buffer, stopping at the first thing that does not make sense
    /// rather than throwing, exactly as <see cref="LineReader.Parse"/> does and
    /// for the same reason: a disagreement about the layout is a bug, but not
    /// one worth taking the app down for.
    /// </summary>
    public static IReadOnlyList<RecoveredLine> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<RecoveredLine>();
        }

        int at = 0;
        uint count = ReadU32(bytes, ref at);
        var found = new List<RecoveredLine>((int)Math.Min(count, 4096));

        for (uint i = 0; i < count; i++)
        {
            if (at + FixedFieldBytes > bytes.Length) { break; }

            double pdfBaseline = ReadF32(bytes, ref at);
            double left = ReadF32(bytes, ref at);
            double top = ReadF32(bytes, ref at);
            double right = ReadF32(bytes, ref at);
            double bottom = ReadF32(bytes, ref at);
            double baseline = ReadF32(bytes, ref at);
            double size = ReadF32(bytes, ref at);

            if (!ReadString(bytes, ref at, out string text, out byte[] raw)) { break; }
            if (!ReadString(bytes, ref at, out string font, out _)) { break; }
            if (!ReadString(bytes, ref at, out string fontPath, out _)) { break; }

            if (at + 4 > bytes.Length) { break; }
            uint clusterCount = ReadU32(bytes, ref at);
            if (at + (long)clusterCount * 16 > bytes.Length) { break; }

            // ⚠️ THE CORE COUNTS BYTES AND THIS APP COUNTS CHARS, and the two
            // differ by a factor of three on every Burmese letter. Converting
            // here, once, is what lets everything above this line index the
            // string the way the rest of the app does.
            var chars = CharOffsets(raw, text);

            var clusters = new List<RecoveredCluster>((int)clusterCount);
            for (uint c = 0; c < clusterCount; c++)
            {
                int from = (int)ReadU32(bytes, ref at);
                int to = (int)ReadU32(bytes, ref at);
                double cl = ReadF32(bytes, ref at);
                double cr = ReadF32(bytes, ref at);

                if (!chars.TryGetValue(from, out int fromChar)) { continue; }
                if (!chars.TryGetValue(to, out int toChar)) { continue; }
                clusters.Add(new RecoveredCluster(fromChar, toChar, cl, cr));
            }

            // u32::MAX for a line in no paragraph, which is -1 here.
            if (at + 4 > bytes.Length) { break; }
            int paragraph = unchecked((int)ReadU32(bytes, ref at));

            found.Add(new RecoveredLine(
                pdfBaseline, left, top, right, bottom, baseline, size,
                text, font, fontPath, clusters, paragraph));
        }

        return found;
    }

    /// <summary>
    /// Which char offset each UTF-8 byte offset of <paramref name="text"/>
    /// stands at. Only offsets that begin a character are listed, so a cluster
    /// naming any other is dropped rather than guessed at.
    /// </summary>
    private static Dictionary<int, int> CharOffsets(byte[] utf8, string text)
    {
        var map = new Dictionary<int, int>(text.Length + 1) { [0] = 0 };

        int b = 0, c = 0;
        while (b < utf8.Length && c < text.Length)
        {
            // How many bytes this character took, from its lead byte.
            int lead = utf8[b];
            int width = lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;

            b += width;

            // Above the basic plane one character is a surrogate PAIR, which is
            // two chars in a .NET string and one in the core's counting.
            c += width == 4 ? 2 : 1;

            map[b] = c;
        }
        return map;
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

    private static bool ReadString(byte[] bytes, ref int at, out string value, out byte[] raw)
    {
        value = string.Empty;
        raw = Array.Empty<byte>();

        if (at + 4 > bytes.Length) { return false; }
        int length = (int)ReadU32(bytes, ref at);
        if (length < 0 || at + length > bytes.Length) { return false; }

        raw = new byte[length];
        Array.Copy(bytes, at, raw, 0, length);
        value = Encoding.UTF8.GetString(raw);
        at += length;
        return true;
    }
}
