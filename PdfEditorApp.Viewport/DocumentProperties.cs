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
    public static byte[] Pairs(
        InfoEdits? edits, string producer, string creator, DateTimeOffset now,
        bool removePersonal = false, bool removeDates = false, CatalogEdits? catalog = null,
        bool removeAttachments = false)
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

        // Makes the core rewrite the file whole rather than append to it, so
        // what is removed is not left behind in the earlier bytes.
        if (removePersonal)
        {
            Add("RemovePersonal", "1");
            if (removeAttachments)
            {
                Add("RemoveAttachments", "1");
            }
        }

        Add("Title", edits?.Title);
        Add("Author", edits?.Author);
        Add("Subject", edits?.Subject);
        Add("Keywords", edits?.Keywords);
        Add("Producer", producer);
        Add("CreatorIfPdfium", creator);

        // Removing the dates means not stamping a new one either.
        if (removeDates)
        {
            Add("RemoveDates", "1");
        }
        else
        {
            Add("ModDate", PdfDate.Format(now));
            Add("XmpDate", PdfDate.FormatXmp(now));
        }

        catalog?.AddPairs(Add);
        return NulFields.Join(fields);
    }
}

/// <summary>
/// Where a file asks to open: a 0-based page and a zoom token ("" for the
/// reader's own zoom, "page", "width", "actual" or "percent:NNN").
/// </summary>
public sealed record OpenView(int Page, string Zoom);

/// <summary>
/// What the file's catalog says about its language and how it opens, as
/// <c>read_catalog_settings</c> reports it. Empty strings mean "not set".
/// </summary>
public sealed record CatalogSettings(
    string Language,
    string PageMode,
    string PageLayout,
    bool ShowTitle,
    OpenView? Open,
    bool OpenActionOther)
{
    public static readonly CatalogSettings Empty = new(string.Empty, string.Empty, string.Empty, false, null, false);

    public static CatalogSettings FromPairs(IReadOnlyDictionary<string, string> p)
    {
        string S(string key) => p.TryGetValue(key, out var v) ? v : string.Empty;
        OpenView? open = int.TryParse(S("OpenPage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) && page >= 0
            ? new OpenView(page, S("OpenZoom"))
            : null;
        return new CatalogSettings(S("Lang"), S("PageMode"), S("PageLayout"), S("DisplayDocTitle") == "1", open, S("OpenActionOther") == "1");
    }

    /// <summary>These settings with the edits waiting to be written laid over them.</summary>
    public CatalogSettings With(CatalogEdits? edits) => edits is null
        ? this
        : this with
        {
            Language = edits.Language ?? Language,
            PageMode = edits.PageMode ?? PageMode,
            PageLayout = edits.PageLayout ?? PageLayout,
            ShowTitle = edits.ShowTitle ?? ShowTitle,
            Open = edits.OpenChanged ? edits.Open : Open,
            OpenActionOther = !edits.OpenChanged && OpenActionOther,
        };
}

/// <summary>
/// Changes to the catalog. Null, or OpenChanged false, means "leave as the
/// file has it", so a script the file runs on opening is never replaced by a
/// change to its language.
/// </summary>
public sealed record CatalogEdits(
    string? Language, string? PageMode, string? PageLayout, bool? ShowTitle, bool OpenChanged, OpenView? Open)
{
    public bool HasChanges =>
        Language is not null || PageMode is not null || PageLayout is not null || ShowTitle is not null || OpenChanged;

    public static CatalogEdits Between(CatalogSettings before, CatalogSettings after)
    {
        static string? Changed(string was, string now) =>
            string.Equals(was, now, StringComparison.Ordinal) ? null : now;

        return new CatalogEdits(
            Changed(before.Language, after.Language),
            Changed(before.PageMode, after.PageMode),
            Changed(before.PageLayout, after.PageLayout),
            before.ShowTitle == after.ShowTitle ? null : after.ShowTitle,
            !Equals(before.Open, after.Open),
            after.Open);
    }

    /// <summary>Later edits win field by field; earlier ones fill the gaps.</summary>
    public CatalogEdits Then(CatalogEdits later) => new(
        later.Language ?? Language,
        later.PageMode ?? PageMode,
        later.PageLayout ?? PageLayout,
        later.ShowTitle ?? ShowTitle,
        OpenChanged || later.OpenChanged,
        later.OpenChanged ? later.Open : Open);

    /// <summary>The pairs <c>write_document_info</c> reads for these changes.</summary>
    public void AddPairs(Action<string, string?> add)
    {
        add("Lang", Language);
        add("PageMode", PageMode);
        add("PageLayout", PageLayout);
        add("DisplayDocTitle", ShowTitle is null ? null : ShowTitle.Value ? "1" : "0");
        if (OpenChanged)
        {
            add("OpenPage", Open is null ? "-1" : Open.Page.ToString(CultureInfo.InvariantCulture));
            add("OpenZoom", Open?.Zoom ?? string.Empty);
        }
    }
}

/// <summary>
/// Everything Document properties has changed and a save has yet to write.
/// One value, so undo can put all of it back in one step.
/// </summary>
public sealed record DocumentPropertiesState(
    InfoEdits? Info, CatalogEdits? Catalog, bool RemovePersonal, bool RemoveDates, bool RemoveAttachments = false)
{
    public static readonly DocumentPropertiesState None = new(null, null, false, false);
}

/// <summary>The languages Document properties offers, by BCP 47 tag.</summary>
public static class DocumentLanguages
{
    public static readonly (string Tag, string Name)[] Common =
    [
        ("en", "English"), ("hi", "Hindi"), ("my", "Burmese"), ("bn", "Bengali"),
        ("ta", "Tamil"), ("te", "Telugu"), ("mr", "Marathi"), ("ne", "Nepali"),
        ("ur", "Urdu"), ("th", "Thai"), ("zh-Hans", "Chinese (Simplified)"),
        ("zh-Hant", "Chinese (Traditional)"), ("ja", "Japanese"), ("ko", "Korean"),
        ("ar", "Arabic"), ("fr", "French"), ("de", "German"), ("es", "Spanish"),
        ("pt", "Portuguese"), ("ru", "Russian"),
    ];

    /// <summary>"Hindi (hi)", "English (United States) (en-US)", or "Not set".</summary>
    public static string Describe(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Not set";
        }
        foreach (var (t, name) in Common)
        {
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} ({tag})";
            }
        }
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            if (!string.IsNullOrEmpty(culture.EnglishName) && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
            {
                return $"{culture.EnglishName} ({tag})";
            }
        }
        catch (CultureNotFoundException)
        {
        }
        return tag;
    }

    /// <summary>"Not set", the common languages, and the file's own when it is none of them.</summary>
    public static List<(string Tag, string Label)> Choices(string current)
    {
        var choices = new List<(string, string)> { (string.Empty, "Not set") };
        choices.AddRange(Common.Select(c => (c.Tag, $"{c.Name} ({c.Tag})")));
        if (!string.IsNullOrWhiteSpace(current)
            && !Common.Any(c => string.Equals(c.Tag, current, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Insert(1, (current, Describe(current)));
        }
        return choices;
    }

    /// <summary>The language a script the core detected suggests, or null.</summary>
    public static string? ForScript(int script) => script switch
    {
        1 => "hi",
        2 => "my",
        3 => "bn",
        4 => "ta",
        5 => "th",
        6 => "ar",
        _ => null,
    };

    /// <summary>The languages written in each script the core detects.</summary>
    private static readonly Dictionary<int, string[]> ScriptLanguages = new()
    {
        [1] = ["hi", "mr", "ne", "sa", "kok", "mai", "bho", "doi"],
        [2] = ["my", "shn", "mnw", "ksw", "kjp", "pi"],
        [3] = ["bn", "as", "mni"],
        [4] = ["ta"],
        [5] = ["th"],
        [6] = ["ar", "ur", "fa", "ps", "sd", "ks"],
    };

    /// <summary>
    /// Whether a language suits text in the script the core detected: Marathi
    /// is written in Devanagari too, so it is not "wrong" for Devanagari text
    /// the way English is. True when the script is unknown or Latin.
    /// </summary>
    public static bool Fits(string tag, int script) =>
        !ScriptLanguages.TryGetValue(script, out var languages)
        || languages.Any(language => SameLanguage(tag, language));

    /// <summary>Whether a tag is that language, whatever its region ("EN-US" is "en").</summary>
    public static bool SameLanguage(string tag, string language)
    {
        string primary = tag.Split('-', '_')[0];
        return string.Equals(primary, language, StringComparison.OrdinalIgnoreCase);
    }

    public static string NameOf(string language) =>
        Common.FirstOrDefault(c => string.Equals(c.Tag, language, StringComparison.OrdinalIgnoreCase)).Name ?? language;
}

/// <summary>How the "When this file opens" choices read, and what Ayaan does with them.</summary>
public static class OpenSettingsText
{
    public static readonly (string Token, string Label)[] Zooms =
    [
        ("", "Reader's choice"), ("page", "Fit page"), ("width", "Fit width"), ("actual", "Actual size"),
        ("percent:50", "50%"), ("percent:75", "75%"), ("percent:125", "125%"),
        ("percent:150", "150%"), ("percent:200", "200%"),
    ];

    public static readonly (string Mode, string Label)[] Panels =
    [
        ("", "Reader's choice"), ("UseNone", "Page only"), ("UseOutlines", "Bookmarks panel"),
        ("UseThumbs", "Pages panel"), ("FullScreen", "Full screen"),
    ];

    public static readonly (string Layout, string Label)[] Layouts =
    [
        ("", "Reader's choice"), ("SinglePage", "One page at a time"), ("OneColumn", "Continuous"),
    ];

    /// <summary>The list, with the file's own value added when it is none of them.</summary>
    public static List<(string Value, string Label)> WithCurrent((string, string)[] list, string current)
    {
        var choices = list.ToList();
        if (!choices.Any(c => string.Equals(c.Item1, current, StringComparison.Ordinal)))
        {
            choices.Add((current, LabelForOther(current)));
        }
        return choices;
    }

    private static string LabelForOther(string value)
    {
        if (value.StartsWith("percent:", StringComparison.Ordinal))
        {
            return $"{value["percent:".Length..]}%";
        }
        return value switch
        {
            "UseOC" => "Layers panel (Ayaan shows the page)",
            "UseAttachments" => "Attachments panel (Ayaan shows the page)",
            "TwoColumnLeft" or "TwoColumnRight" => "Two pages, continuous (Ayaan shows one)",
            "TwoPageLeft" or "TwoPageRight" => "Two pages at a time (Ayaan shows one)",
            _ => value,
        };
    }

    /// <summary>Which panel a page mode opens in Ayaan. Page only is left alone: most files say it by default.</summary>
    public static string PanelToShow(string pageMode) => pageMode switch
    {
        "UseOutlines" => "Bookmarks",
        "UseThumbs" => "Pages",
        "FullScreen" => "FullScreen",
        _ => string.Empty,
    };

    /// <summary>Ayaan's view for a page layout, or null to leave the reader's own.</summary>
    public static PageViewMode? LayoutToShow(string pageLayout) => pageLayout switch
    {
        "SinglePage" or "TwoPageLeft" or "TwoPageRight" => PageViewMode.SinglePage,
        "OneColumn" or "TwoColumnLeft" or "TwoColumnRight" => PageViewMode.Continuous,
        _ => null,
    };

    /// <summary>A fixed zoom factor for a token, or null for a fit or the reader's own.</summary>
    public static double? ZoomFactor(string token)
    {
        if (token == "actual")
        {
            return 1.0;
        }
        if (token.StartsWith("percent:", StringComparison.Ordinal)
            && double.TryParse(token["percent:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            && percent > 0)
        {
            return percent / 100.0;
        }
        return null;
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

    /// <summary>
    /// The dialog's details as plain text, one "Label: value" a line. A line
    /// with no label continues the one above it, indented; empty values are
    /// left out rather than printed as a bare label.
    /// </summary>
    public static string AsText(IEnumerable<(string Label, string Value)> lines) =>
        string.Join(Environment.NewLine, lines
            .Where(line => line.Value.Length > 0)
            .Select(line => line.Label.Length == 0 ? "  " + line.Value : $"{line.Label}: {line.Value}"));
}

/// <summary>
/// The personal info a file carries, as <c>find_personal_info</c> reports it:
/// key/value pairs in order, a key repeating once per value.
/// </summary>
public sealed record PersonalInfo(IReadOnlyList<(string Key, string Value)> Found)
{
    public static PersonalInfo FromBuffer(byte[] buffer)
    {
        var fields = NulFields.Parse(buffer);
        var found = new List<(string, string)>();
        for (int i = 0; i + 1 < fields.Count; i += 2)
        {
            found.Add((fields[i], fields[i + 1]));
        }
        return new PersonalInfo(found);
    }

    private IEnumerable<string> All(string key) =>
        Found.Where(f => f.Key == key).Select(f => f.Value.Trim()).Where(v => v.Length > 0);

    private int Count(string key) =>
        int.TryParse(All(key).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>
    /// What the dialog lists under "Remove personal info", one line each, or
    /// nothing when the file is clean. <paramref name="title"/> is checked too:
    /// it is kept, being the document's own, but a title like "Microsoft Word -
    /// salaries.docx" gives the source file away and deserves a mention.
    /// </summary>
    public List<string> Lines(string title)
    {
        var lines = new List<string>();

        var authors = All("Author").Concat(All("XmpAuthor")).Distinct(StringComparer.Ordinal).ToList();
        if (authors.Count > 0)
        {
            lines.Add($"Author: {string.Join(", ", authors)}");
        }

        var tools = All("Creator").Concat(All("XmpTool")).Distinct(StringComparer.Ordinal).ToList();
        if (tools.Count > 0)
        {
            lines.Add($"Made with: {string.Join("; ", tools)}");
        }

        foreach (var custom in All("Custom").Distinct(StringComparer.Ordinal).Take(6))
        {
            int colon = custom.IndexOf(": ", StringComparison.Ordinal);
            lines.Add(colon > 0
                ? $"{custom[..colon]}: {custom[(colon + 2)..]} (custom property)"
                : $"{custom} (custom property)");
        }

        int steps = Count("History");
        if (steps > 0)
        {
            lines.Add(steps == 1 ? "Editing history: 1 step" : $"Editing history: {steps} steps");
        }

        foreach (var path in All("FilePath").Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
        {
            lines.Add($"Original file: {path}");
        }

        var commenters = All("CommentAuthor").ToList();
        int comments = Count("Comments");
        if (commenters.Count > 0)
        {
            string count = comments == 1 ? "1 comment" : $"{comments} comments";
            lines.Add($"Comment authors: {string.Join(", ", commenters.Take(5))}{(commenters.Count > 5 ? " and others" : string.Empty)} ({count})");
        }

        int attached = Count("ObjectMetadata");
        if (attached > 0)
        {
            lines.Add(attached == 1
                ? "Hidden details on 1 page or picture"
                : $"Hidden details on {attached} pages or pictures");
        }

        if (TitleNamesAFile(title))
        {
            lines.Add($"The title names the original file: “{title.Trim()}”. It is kept; change it above if you'd rather it didn't.");
        }

        return lines;
    }

    private static readonly string[] ProgramPrefixes =
        ["Microsoft Word - ", "Microsoft Excel - ", "Microsoft PowerPoint - "];

    private static readonly string[] DocumentExtensions =
        [".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".pages"];

    /// <summary>A title a program made from the source file's name.</summary>
    public static bool TitleNamesAFile(string title)
    {
        string t = title.Trim();
        return ProgramPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || DocumentExtensions.Any(e => t.EndsWith(e, StringComparison.OrdinalIgnoreCase));
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
