using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decodes the buffer render_core::get_page_links writes.
///
/// The pure half, here rather than beside the marshalling for the reason
/// <see cref="WordClusterReader"/> is: the app is a WinUI project a test
/// assembly cannot load, and a wire format checked only by running the app is a
/// wire format nobody checks.
///
/// Little-endian: a link count, then per link an annotation index, a kind, four
/// bounds, a target page, and a length-prefixed UTF-8 URI.
/// </summary>
public static class LinkReader
{
    /// <summary>Everything before the URI: index, kind, four bounds, target page.</summary>
    private const int FixedFieldBytes = 28;

    /// <summary>
    /// Decodes the buffer, stopping at the first thing that does not make sense
    /// rather than throwing.
    ///
    /// A short or malformed buffer means the core and this build disagree about
    /// the layout, which is a bug, but not one worth taking the app down for:
    /// the links decoded so far are still correct and the page simply offers
    /// fewer. A count larger than the buffer can hold is the same case and must
    /// not be trusted into an over-read.
    /// </summary>
    public static IReadOnlyList<LinkSnapshot> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<LinkSnapshot>();
        }

        int at = 0;
        uint count = ReadU32(bytes, ref at);
        var found = new List<LinkSnapshot>((int)Math.Min(count, 4096));

        for (uint i = 0; i < count; i++)
        {
            if (at + FixedFieldBytes > bytes.Length) { break; }

            int index = (int)ReadU32(bytes, ref at);
            uint kind = ReadU32(bytes, ref at);
            double left = ReadF32(bytes, ref at);
            double top = ReadF32(bytes, ref at);
            double right = ReadF32(bytes, ref at);
            double bottom = ReadF32(bytes, ref at);
            int target = ReadI32(bytes, ref at);

            if (!ReadString(bytes, ref at, out string uri)) { break; }

            found.Add(new LinkSnapshot(
                index, AsKind(kind), left, top, right, bottom, target, uri));
        }

        return found;
    }

    /// <summary>
    /// A kind this build does not know is NOT a URI link.
    ///
    /// Same rule as the word reader's unknown refusal: a newer core adding a
    /// kind an older app has not heard of must fall to the read-only side, or
    /// the app would offer to retarget something the core will refuse to write.
    /// </summary>
    private static LinkKind AsKind(uint code) => code switch
    {
        0 => LinkKind.Uri,
        1 => LinkKind.Internal,
        _ => LinkKind.Other,
    };

    private static uint ReadU32(byte[] bytes, ref int at)
    {
        uint value = BitConverter.ToUInt32(bytes, at);
        at += 4;
        return value;
    }

    private static int ReadI32(byte[] bytes, ref int at)
    {
        int value = BitConverter.ToInt32(bytes, at);
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
