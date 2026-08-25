using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decodes the buffer render_core::get_page_word_clusters writes.
///
/// The pure half, here rather than beside the marshalling for the reason
/// <see cref="PageTextObjectReader"/> is: the app is a WinUI project a test
/// assembly cannot load, and a wire format checked only by running the app is a
/// wire format nobody checks.
///
/// Little-endian: a cluster count, then per cluster the first object's index,
/// an object count and that many indices, four bounds, a baseline, a font size,
/// a packed colour, a refusal code, and two length-prefixed UTF-8 strings.
/// </summary>
public static class WordClusterReader
{
    /// <summary>The fixed fields between the object list and the first string.</summary>
    private const int FixedFieldBytes = 32;

    /// <summary>
    /// Decodes the buffer, stopping at the first thing that does not make sense
    /// rather than throwing.
    ///
    /// A short or malformed buffer means the core and this build disagree about
    /// the layout, which is a bug, but not one worth taking the app down for:
    /// the clusters decoded so far are still correct and the page simply offers
    /// fewer words. A count larger than the buffer can hold is the same case and
    /// must not be trusted into an over-read.
    /// </summary>
    public static IReadOnlyList<WordClusterSnapshot> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<WordClusterSnapshot>();
        }

        int at = 0;
        uint count = ReadU32(bytes, ref at);
        var found = new List<WordClusterSnapshot>((int)Math.Min(count, 4096));

        for (uint i = 0; i < count; i++)
        {
            if (at + 8 > bytes.Length) { break; }

            int first = (int)ReadU32(bytes, ref at);
            long objectCount = ReadU32(bytes, ref at);

            // Four bytes per index, then the fixed fields. Checked before any of
            // it is read, so a lying count cannot walk off the end.
            if (objectCount < 0
                || at + (objectCount * 4) + FixedFieldBytes > bytes.Length)
            {
                break;
            }

            var objects = new int[objectCount];
            for (long o = 0; o < objectCount; o++)
            {
                objects[o] = (int)ReadU32(bytes, ref at);
            }

            double left = ReadF32(bytes, ref at);
            double top = ReadF32(bytes, ref at);
            double right = ReadF32(bytes, ref at);
            double bottom = ReadF32(bytes, ref at);
            double baseline = ReadF32(bytes, ref at);
            double size = ReadF32(bytes, ref at);
            uint color = ReadU32(bytes, ref at);
            uint refusal = ReadU32(bytes, ref at);
            int prefix = (int)ReadU32(bytes, ref at);

            if (!ReadString(bytes, ref at, out string text)) { break; }
            if (!ReadString(bytes, ref at, out string font)) { break; }

            found.Add(new WordClusterSnapshot(
                first, objects, left, top, right, bottom, baseline, size, color,
                AsRefusal(refusal), prefix, text, font));
        }

        return found;
    }

    /// <summary>
    /// A refusal code this build does not know is still a refusal.
    ///
    /// An unknown reason must never read as "editable": a newer core adding a
    /// case an older app has not heard of would otherwise let a write through
    /// that the core had just decided to forbid.
    /// </summary>
    private static ClusterRefusal AsRefusal(uint code) => code switch
    {
        0 => ClusterRefusal.None,
        1 => ClusterRefusal.NotUpright,
        2 => ClusterRefusal.MixedStyle,
        3 => ClusterRefusal.SplitObjects,
        4 => ClusterRefusal.NoFontName,
        5 => ClusterRefusal.NoObjects,
        6 => ClusterRefusal.PartialSpan,
        _ => ClusterRefusal.NoObjects,
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
