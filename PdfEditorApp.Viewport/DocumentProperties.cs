using System.Globalization;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What a document says about itself, as the core reads it off the open
/// document: the Info fields, and the facts File > Document properties shows
/// beside them.
/// </summary>
public sealed record DocumentInfo(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    string Version,
    int PageCount,
    double PageWidth,
    double PageHeight,
    bool Tagged,
    int SecurityRevision,
    uint Permissions)
{
    /// <summary>PDFium reports -1 for a file with no security handler.</summary>
    public bool IsEncrypted => SecurityRevision >= 0;

    /// <summary>From the NUL-separated pairs <c>get_document_properties</c> returns.</summary>
    public static DocumentInfo FromPairs(IReadOnlyDictionary<string, string> p)
    {
        string S(string key) => p.TryGetValue(key, out var v) ? v : string.Empty;
        double D(string key) => double.TryParse(S(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        int I(string key, int fallback) => int.TryParse(S(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        return new DocumentInfo(
            S("Title"), S("Author"), S("Subject"), S("Keywords"), S("Creator"), S("Producer"),
            PdfDate.Parse(S("CreationDate")), PdfDate.Parse(S("ModDate")),
            S("Version"), I("Pages", 0), D("PageWidth"), D("PageHeight"),
            S("Tagged") == "1", I("Security", -1),
            uint.TryParse(S("Permissions"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var perms) ? perms : uint.MaxValue);
    }

    /// <summary>This document with the edits waiting to be written laid over it.</summary>
    public DocumentInfo With(InfoEdits? edits) => edits is null
        ? this
        : this with
        {
            Title = edits.Title ?? Title,
            Author = edits.Author ?? Author,
            Subject = edits.Subject ?? Subject,
            Keywords = edits.Keywords ?? Keywords,
        };
}

/// <summary>
/// The four fields Document properties edits. Null means "leave as the file
/// has it"; an empty string removes the field.
/// </summary>
public sealed record InfoEdits(string? Title, string? Author, string? Subject, string? Keywords)
{
    public bool HasChanges => Title is not null || Author is not null || Subject is not null || Keywords is not null;

    /// <summary>
    /// What the dialog changed, field by field. Compared trimmed, so a stray
    /// space is not an edit, and a field left as it was stays null rather than
    /// being written back with the same value.
    /// </summary>
    public static InfoEdits Between(DocumentInfo before, string title, string author, string subject, string keywords)
    {
        static string? Changed(string was, string now) =>
            string.Equals(was.Trim(), now.Trim(), StringComparison.Ordinal) ? null : now.Trim();

        return new InfoEdits(
            Changed(before.Title, title),
            Changed(before.Author, author),
            Changed(before.Subject, subject),
            Changed(before.Keywords, keywords));
    }

    /// <summary>Later edits win field by field; earlier ones fill the gaps.</summary>
    public InfoEdits Then(InfoEdits later) => new(
        later.Title ?? Title,
        later.Author ?? Author,
        later.Subject ?? Subject,
        later.Keywords ?? Keywords);
}

/// <summary>
/// What every save stamps on the file it writes, as the NUL-separated pairs
/// <c>write_document_info</c> reads.
/// </summary>
public static class DocumentInfoStamp
{
    /// <summary>
    /// The edits waiting (if any), the producer, the modified date in both the
    /// Info and the XMP form, and the creator a document PDFium made should
    /// carry instead of "PDFium".
    /// </summary>
    public static byte[] Pairs(InfoEdits? edits, string producer, string creator, DateTimeOffset now)
    {
        var fields = new List<string>();
        void Add(string key, string? value)
        {
            if (value is not null)
            {
                fields.Add(key);
                fields.Add(value);
            }
        }

        Add("Title", edits?.Title);
        Add("Author", edits?.Author);
        Add("Subject", edits?.Subject);
        Add("Keywords", edits?.Keywords);
        Add("Producer", producer);
        Add("CreatorIfPdfium", creator);
        Add("ModDate", PdfDate.Format(now));
        Add("XmpDate", PdfDate.FormatXmp(now));
        return NulFields.Join(fields);
    }
}

/// <summary>
/// PDF dates: "D:YYYYMMDDHHmmSSOHH'mm'", everything after the year optional.
/// </summary>
public static class PdfDate
{
    /// <summary>
    /// The moment a PDF date names, or null for anything unreadable. A date
    /// with no offset is taken as local time, which is what Acrobat does with
    /// one; "Z" and "Z00'00'" both mean UTC.
    /// </summary>
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        int digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits]))
        {
            digits++;
        }
        if (digits < 4 || digits % 2 != 0)
        {
            return null;
        }

        int Part(int at, int fallback) => digits >= at + 2 ? int.Parse(s.AsSpan(at, 2), CultureInfo.InvariantCulture) : fallback;

        int year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        int month = Part(4, 1), day = Part(6, 1), hour = Part(8, 0), minute = Part(10, 0), second = Part(12, 0);

        string rest = s[digits..];
        TimeSpan offset;
        if (rest.Length == 0)
        {
            try
            {
                var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
                return new DateTimeOffset(local);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        else if (rest[0] == 'Z')
        {
            offset = TimeSpan.Zero;
        }
        else if (rest[0] is '+' or '-')
        {
            string numbers = new(rest[1..].Where(char.IsAsciiDigit).ToArray());
            int oh = numbers.Length >= 2 ? int.Parse(numbers.AsSpan(0, 2), CultureInfo.InvariantCulture) : 0;
            int om = numbers.Length >= 4 ? int.Parse(numbers.AsSpan(2, 2), CultureInfo.InvariantCulture) : 0;
            offset = new TimeSpan(oh, om, 0);
            if (rest[0] == '-')
            {
                offset = -offset;
            }
        }
        else
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"D:20260918153000+06'30'", or a trailing "Z" at UTC.</summary>
    public static string Format(DateTimeOffset when)
    {
        string stamp = when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (when.Offset == TimeSpan.Zero)
        {
            return $"D:{stamp}Z";
        }

        var o = when.Offset.Duration();
        char sign = when.Offset < TimeSpan.Zero ? '-' : '+';
        return $"D:{stamp}{sign}{o.Hours:00}'{o.Minutes:00}'";
    }

    /// <summary>The same moment in the ISO 8601 form XMP uses.</summary>
    public static string FormatXmp(DateTimeOffset when) =>
        when.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}

/// <summary>One font the file uses.</summary>
public sealed record FontFact(string Name, string Kind, bool Embedded, bool Subset)
{
    public string Describe() =>
        $"{Name} ({Kind}, {(Embedded ? (Subset ? "embedded subset" : "embedded") : "not embedded")})";
}

/// <summary>How Document properties words what it reports.</summary>
public static class DocumentFacts
{
    /// <summary>"3.2 MB", "840 KB", "512 bytes".</summary>
    public static string FileSize(long bytes, CultureInfo culture) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => string.Format(culture, "{0:0} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(culture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(culture, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    private static readonly (string Name, double W, double H)[] NamedSizes =
    [
        ("A3", 841.89, 1190.55),
        ("A4", 595.28, 841.89),
        ("A5", 419.53, 595.28),
        ("Letter", 612, 792),
        ("Legal", 612, 1008),
        ("Tabloid", 792, 1224),
    ];

    /// <summary>"8.27 × 11.69 in (A4)", from a page size in points.</summary>
    public static string PageSize(double widthPt, double heightPt, CultureInfo culture)
    {
        if (widthPt <= 0 || heightPt <= 0)
        {
            return string.Empty;
        }

        string inches = string.Format(culture, "{0:0.##} × {1:0.##} in", widthPt / 72, heightPt / 72);
        foreach (var (name, w, h) in NamedSizes)
        {
            bool upright = Math.Abs(widthPt - w) < 3 && Math.Abs(heightPt - h) < 3;
            bool turned = Math.Abs(widthPt - h) < 3 && Math.Abs(heightPt - w) < 3;
            if (upright || turned)
            {
                return $"{inches} ({name}{(turned ? ", landscape" : string.Empty)})";
            }
        }
        return inches;
    }

    /// <summary>"5 Aug 2025, 14:31", in local time.</summary>
    public static string When(DateTimeOffset? when, CultureInfo culture) =>
        when is { } w ? w.ToLocalTime().ToString("d MMM yyyy, HH:mm", culture) : "Unknown";

    /// <summary>"5 Aug 2025, 14:31 in Microsoft Word".</summary>
    public static string Created(DateTimeOffset? when, string creator, CultureInfo culture)
    {
        string app = creator.Trim();
        if (when is null)
        {
            return app.Length > 0 ? $"In {app}" : "Unknown";
        }
        return app.Length > 0 ? $"{When(when, culture)} in {app}" : When(when, culture);
    }

    /// <summary>
    /// "None", or "Encrypted" and what the file forbids. The bits are the /P
    /// entry's, numbered from 1 as the PDF specification numbers them.
    /// </summary>
    public static string Security(int revision, uint permissions)
    {
        if (revision < 0)
        {
            return "None";
        }

        var refused = new List<string>();
        if ((permissions & (1u << 2)) == 0) { refused.Add("printing"); }
        if ((permissions & (1u << 4)) == 0) { refused.Add("copying"); }
        if ((permissions & (1u << 3)) == 0) { refused.Add("changes"); }

        return refused.Count switch
        {
            0 => "Encrypted",
            1 => $"Encrypted. {Capitalised(refused[0])} is not allowed",
            _ => $"Encrypted. {Capitalised(string.Join(", ", refused.Take(refused.Count - 1)))} and {refused[^1]} are not allowed",
        };
    }

    private static string Capitalised(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The rows <c>list_document_fonts</c> returns, four fields each.</summary>
    public static List<FontFact> Fonts(byte[] buffer)
    {
        var fields = NulFields.Parse(buffer);
        var fonts = new List<FontFact>();
        for (int i = 0; i + 3 < fields.Count; i += 4)
        {
            fonts.Add(new FontFact(fields[i], fields[i + 1], fields[i + 2] == "1", fields[i + 3] == "1"));
        }
        return fonts;
    }

    /// <summary>"14 fonts, all embedded", "3 fonts, 1 not embedded".</summary>
    public static string FontsSummary(IReadOnlyList<FontFact> fonts)
    {
        if (fonts.Count == 0)
        {
            return "None";
        }

        string count = fonts.Count == 1 ? "1 font" : $"{fonts.Count} fonts";
        int missing = fonts.Count(f => !f.Embedded);
        return missing switch
        {
            0 => fonts.Count == 1 ? $"{count}, embedded" : $"{count}, all embedded",
            _ when missing == fonts.Count => fonts.Count == 1 ? $"{count}, not embedded" : $"{count}, none embedded",
            _ => $"{count}, {missing} not embedded",
        };
    }
}

/// <summary>Which documents' tabs show the title, kept per file in the settings.</summary>
public static class TabTitles
{
    public static bool IsOn(IReadOnlyList<string> paths, string? path) =>
        path is not null && paths.Contains(ReadingPositions.Key(path), StringComparer.Ordinal);

    /// <summary>The list with this document switched on or off, keyed as reading positions are.</summary>
    public static List<string> Set(IReadOnlyList<string> paths, string path, bool on)
    {
        string key = ReadingPositions.Key(path);
        var next = paths.Where(p => !string.Equals(p, key, StringComparison.Ordinal)).ToList();
        if (on)
        {
            next.Add(key);
        }
        return next;
    }
}

/// <summary>Facts read off the file's bytes rather than asked of PDFium.</summary>
public static class FileFacts
{
    /// <summary>
    /// Whether the file is linearized ("fast web view"). The dictionary that
    /// says so has to be the first object in the file, so the first kilobyte
    /// decides it.
    /// </summary>
    public static bool IsLinearized(ReadOnlySpan<byte> head) =>
        head[..Math.Min(head.Length, 1024)].IndexOf("/Linearized"u8) >= 0;
}

/// <summary>The NUL-separated UTF-8 fields the document-properties calls use.</summary>
public static class NulFields
{
    public static List<string> Parse(byte[] buffer)
    {
        var fields = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == 0)
            {
                fields.Add(Encoding.UTF8.GetString(buffer, start, i - start));
                start = i + 1;
            }
        }
        if (start < buffer.Length)
        {
            fields.Add(Encoding.UTF8.GetString(buffer, start, buffer.Length - start));
        }
        return fields;
    }

    /// <summary>Pairs read as a dictionary, the first of a repeated key winning.</summary>
    public static Dictionary<string, string> Pairs(byte[] buffer)
    {
        var fields = Parse(buffer);
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < fields.Count; i += 2)
        {
            pairs.TryAdd(fields[i], fields[i + 1]);
        }
        return pairs;
    }

    public static byte[] Join(IEnumerable<string> fields)
    {
        var bytes = new List<byte>();
        foreach (var field in fields)
        {
            // A NUL inside a value would shift every field after it by one.
            bytes.AddRange(Encoding.UTF8.GetBytes(field.Replace("\0", string.Empty)));
            bytes.Add(0);
        }
        return [.. bytes];
    }
}
