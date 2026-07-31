using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One installed font family and the files that provide its regular, bold,
/// italic and bold-italic variants (any of which may be missing). The core
/// embeds whichever file the picker resolves for the chosen style.
/// </summary>
public sealed class FontFamily
{
    public string Name { get; }
    public string? RegularPath { get; set; }
    public string? BoldPath { get; set; }
    public string? ItalicPath { get; set; }
    public string? BoldItalicPath { get; set; }

    public FontFamily(string name) => Name = name;

    /// <summary>
    /// The best file for the requested style: the exact variant if present, else
    /// the nearest available (bold-italic falls back to bold, then italic, then
    /// regular; a missing regular falls back to any variant). Null only for an
    /// empty family.
    /// </summary>
    public string? ResolvePath(bool bold, bool italic)
    {
        string?[] order = (bold, italic) switch
        {
            (true, true) => new[] { BoldItalicPath, BoldPath, ItalicPath, RegularPath },
            (true, false) => new[] { BoldPath, BoldItalicPath, RegularPath, ItalicPath },
            (false, true) => new[] { ItalicPath, BoldItalicPath, RegularPath, BoldPath },
            _ => new[] { RegularPath, ItalicPath, BoldPath, BoldItalicPath },
        };
        return order.FirstOrDefault(p => p is not null);
    }
}

/// <summary>What a single font file describes: its family and whether it is a
/// bold and/or italic cut.</summary>
public readonly record struct FontFace(string Family, bool Bold, bool Italic);

/// <summary>
/// Reads the installed fonts by parsing font FILES directly (the sfnt 'name'
/// and 'head' tables), so each family carries the actual file paths the core
/// needs to embed. Parsing is a testable pure function over a stream; the
/// enumeration is the thin OS-directory scan around it.
///
/// Only the tables needed are read (the file header, the table directory, and
/// the one 'name'/'head' table), not the whole font, so scanning hundreds of
/// files stays cheap.
/// </summary>
public static class FontCatalog
{
    private const int TtcTag = 0x74746366; // "ttcf"

    /// <summary>Enumerates families from the given font files (or the OS font
    /// directories when <paramref name="files"/> is null), newest wins on a
    /// duplicate variant. Files that fail to parse are skipped.</summary>
    public static IReadOnlyList<FontFamily> Enumerate(IEnumerable<string>? files = null)
    {
        files ??= DefaultFontFiles();
        var byName = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in files)
        {
            FontFace face;
            try
            {
                using var stream = File.OpenRead(path);
                if (!TryParseFace(stream, out face))
                {
                    continue;
                }
            }
            catch
            {
                continue; // unreadable / locked / not a font: skip
            }

            if (!byName.TryGetValue(face.Family, out var family))
            {
                family = new FontFamily(face.Family);
                byName[face.Family] = family;
            }

            switch (face.Bold, face.Italic)
            {
                case (true, true): family.BoldItalicPath = path; break;
                case (true, false): family.BoldPath = path; break;
                case (false, true): family.ItalicPath = path; break;
                default: family.RegularPath = path; break;
            }
        }

        return byName.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The .ttf/.otf/.ttc files in the machine and per-user font
    /// directories. Public so the enumeration source can be unit-tested.</summary>
    public static IEnumerable<string> DefaultFontFiles()
    {
        var dirs = new List<string>();
        string? windir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrEmpty(windir))
        {
            dirs.Add(Path.Combine(windir, "Fonts"));
        }
        string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local))
        {
            dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }

        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(dir)
                    .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }
            foreach (string f in found)
            {
                yield return f;
            }
        }
    }

    /// <summary>
    /// Parses a font file's family name and bold/italic flags from its 'name'
    /// and 'head' tables. Handles a TrueType/OpenType file or the first font of
    /// a TrueType Collection (.ttc). Returns false if the data is not a font or
    /// carries no usable family name.
    /// </summary>
    public static bool TryParseFace(Stream stream, out FontFace face)
    {
        face = default;

        // A TTC starts with "ttcf"; use its first embedded font's table directory.
        uint magic = ReadU32(stream, 0);
        long sfntOffset = 0;
        if (magic == TtcTag)
        {
            // ttcf: version(4) at 4, numFonts(4) at 8, offsets[] at 12.
            sfntOffset = ReadU32(stream, 12);
            magic = ReadU32(stream, sfntOffset);
        }

        // Accept 0x00010000 (TrueType), "OTTO" (CFF), "true", "typ1".
        if (magic != 0x00010000 && magic != 0x4F54544F /*OTTO*/
            && magic != 0x74727565 /*true*/ && magic != 0x74797031 /*typ1*/)
        {
            return false;
        }

        int numTables = ReadU16(stream, sfntOffset + 4);
        long nameOffset = 0, headOffset = 0;
        for (int i = 0; i < numTables; i++)
        {
            long rec = sfntOffset + 12 + i * 16;
            uint tag = ReadU32(stream, rec);
            uint off = ReadU32(stream, rec + 8);
            if (tag == 0x6E616D65) // "name"
            {
                nameOffset = off;
            }
            else if (tag == 0x68656164) // "head"
            {
                headOffset = off;
            }
        }
        if (nameOffset == 0)
        {
            return false;
        }

        string? family = ReadNameRecord(stream, nameOffset, wantFamily: true);
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        // Style: the 'head' macStyle bits are the most reliable (bit0 bold,
        // bit1 italic); fall back to the subfamily name if head is missing.
        bool bold, italic;
        if (headOffset != 0)
        {
            int macStyle = ReadU16(stream, headOffset + 44);
            bold = (macStyle & 0x1) != 0;
            italic = (macStyle & 0x2) != 0;
        }
        else
        {
            string sub = ReadNameRecord(stream, nameOffset, wantFamily: false) ?? "";
            bold = sub.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            italic = sub.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                  || sub.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        }

        face = new FontFace(family.Trim(), bold, italic);
        return true;
    }

    /// <summary>Reads name ID 1 (family) or 2 (subfamily), preferring the
    /// Windows/UTF-16BE English record, then any Windows record, then Mac.</summary>
    private static string? ReadNameRecord(Stream s, long nameOffset, bool wantFamily)
    {
        int wantId = wantFamily ? 1 : 2;
        int count = ReadU16(s, nameOffset + 2);
        int storageOffset = ReadU16(s, nameOffset + 4);

        string? best = null;
        int bestRank = -1;
        for (int i = 0; i < count; i++)
        {
            long rec = nameOffset + 6 + i * 12;
            int platform = ReadU16(s, rec);
            int encoding = ReadU16(s, rec + 2);
            int language = ReadU16(s, rec + 4);
            int nameId = ReadU16(s, rec + 6);
            int length = ReadU16(s, rec + 8);
            int offset = ReadU16(s, rec + 10);
            if (nameId != wantId || length == 0)
            {
                continue;
            }

            // Rank candidates: Windows en-US highest, then any Windows, then Mac.
            int rank = platform switch
            {
                3 when language == 0x409 => 3,
                3 => 2,
                1 => 1,
                _ => 0,
            };
            if (rank <= bestRank)
            {
                continue;
            }

            byte[] buf = new byte[length];
            s.Seek(nameOffset + storageOffset + offset, SeekOrigin.Begin);
            if (s.Read(buf, 0, length) != length)
            {
                continue;
            }
            // Windows/UTF-16BE (platform 3) and Unicode (0); Mac (1) is Latin-1.
            string value = platform == 1
                ? Encoding.Latin1.GetString(buf)
                : Encoding.BigEndianUnicode.GetString(buf);
            best = value;
            bestRank = rank;
        }
        return best;
    }

    private static int ReadU16(Stream s, long offset)
    {
        s.Seek(offset, SeekOrigin.Begin);
        int a = s.ReadByte();
        int b = s.ReadByte();
        if (a < 0 || b < 0)
        {
            throw new EndOfStreamException();
        }
        return (a << 8) | b;
    }

    private static uint ReadU32(Stream s, long offset)
    {
        s.Seek(offset, SeekOrigin.Begin);
        int a = s.ReadByte();
        int b = s.ReadByte();
        int c = s.ReadByte();
        int d = s.ReadByte();
        if (a < 0 || b < 0 || c < 0 || d < 0)
        {
            throw new EndOfStreamException();
        }
        return (uint)((a << 24) | (b << 16) | (c << 8) | d);
    }
}
