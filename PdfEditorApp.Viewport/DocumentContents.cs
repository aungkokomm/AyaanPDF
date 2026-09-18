using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// An old font that draws Hindi or Burmese letters over character codes that
/// mean something else, so the page looks right and its text does not.
/// </summary>
/// <param name="Name">How the reader knows it: "Kruti Dev", "Zawgyi".</param>
/// <param name="Language">The language it was made for, in English.</param>
/// <param name="OcrLanguage">The Recognize text language that reads it back.</param>
/// <param name="LatinCoded">True when its codes are English letters (Kruti Dev,
/// Win Innwa); false for Zawgyi, whose codes are Burmese ones used its own way.</param>
public sealed record LegacyFont(string Name, string Language, string OcrLanguage, bool LatinCoded)
{
    /// <summary>What it means for the reader, in one sentence.</summary>
    public string Warning => LatinCoded
        ? $"Text in {Name}, an old {Language} font that isn't Unicode, looks right, but search and copy see English letters."
        : $"Text in {Name}, the old {Language} font that isn't Unicode, looks right, but a search in Unicode {Language} won't find it and copied text comes out in {Name}.";
}

/// <summary>Old non-Unicode Hindi and Burmese fonts, known by name.</summary>
/// <remarks>
/// The names are the ones real PDFs carry, from a census of 1,548 files on the
/// test machine: KrutiDev010 and 020, Walkman-Chanakya-901, DV-TTRadhika,
/// BRHDevanagari, Zawgyi-One, the Win family (WinInnwa, WinResearcher,
/// WinYadanapon and eight more) and the Wwin family (Wwin_Burmese,
/// Wwin_Tagaung and more). The Win fonts are listed one by one because
/// Wingdings starts the same way. A name with "Uni" in it is left alone:
/// ChanakyaBBTUni is a Unicode font named after the old one.
/// </remarks>
public static class LegacyFonts
{
    private static LegacyFont Hindi(string name) => new(name, "Hindi", "hin", true);

    private static LegacyFont Burmese(string name) => new(name, "Burmese", "mya", true);

    // Longer prefixes before the shorter ones they start with.
    private static readonly (string Prefix, LegacyFont Font)[] Known =
    [
        ("krutidev", Hindi("Kruti Dev")),
        ("walkmanchanakya", Hindi("Walkman Chanakya")),
        ("chanakya", Hindi("Chanakya")),
        ("devlys", Hindi("DevLys")),
        ("shreedev", Hindi("Shree Dev")),
        ("dvtt", Hindi("DV-TT")),
        ("brhdevanagari", Hindi("BRH Devanagari")),
        ("apsdv", Hindi("APS-DV")),
        ("zawgyi", new("Zawgyi", "Burmese", "mya", false)),
        ("wininnwa", Burmese("Win Innwa")),
        ("wininnlay", Burmese("Win Innlay")),
        ("winresearch", Burmese("Win Research")),
        ("winkalaw", Burmese("Win Kalaw")),
        ("winmonotype", Burmese("Win Monotype")),
        ("winpyinoolwin", Burmese("Win Pyin Oo Lwin")),
        ("wintaungyi", Burmese("Win Taungyi")),
        ("winyadanapon", Burmese("Win Yadanapon")),
        ("winamarapura", Burmese("Win Amarapura")),
        ("winhaka", Burmese("Win Haka")),
        ("winmandalay", Burmese("Win Mandalay")),
        ("wwinbur", Burmese("Wwin Burmese")),
        ("wwin", Burmese("Wwin")),
    ];

    /// <summary>The old font a PDF font name is, or null for any other font.</summary>
    public static LegacyFont? Recognise(string fontName)
    {
        string key = new string(fontName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (key.Contains("uni", StringComparison.Ordinal))
        {
            return null;
        }
        foreach (var (prefix, font) in Known)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return font;
            }
        }
        return null;
    }

    /// <summary>The old fonts among these names, each once, in the order met.</summary>
    public static List<LegacyFont> In(IEnumerable<string> fontNames) =>
        fontNames.Select(Recognise).OfType<LegacyFont>().Distinct().ToList();
}

/// <summary>One page as the core's page check found it.</summary>
public sealed record PageFacts(int Page, bool HasText, bool Recognised, IReadOnlyList<string> Fonts);

/// <summary>The pages set in one old font, and which of them still need reading.</summary>
public sealed record LegacyFontPages(LegacyFont Font, IReadOnlyList<int> Pages, IReadOnlyList<int> NotRecognised);

/// <summary>What the page check found across the document.</summary>
public sealed record PageFindings(int PageCount, IReadOnlyList<int> WithoutText, IReadOnlyList<LegacyFontPages> Legacy)
{
    /// <summary>"Every page has text." or "38 of 359 pages have no text: 12-40, 88."</summary>
    public string TextLine => WithoutText.Count switch
    {
        0 => PageCount == 1 ? "The page has text." : "Every page has text.",
        _ when WithoutText.Count == PageCount => PageCount == 1
            ? "The page has no text. It is probably a scan."
            : $"None of the {PageCount} pages has text. It is probably a scan.",
        1 => $"1 of {PageCount} pages has no text: {PageSurvey.ShortList(WithoutText, PageCount)}.",
        _ => $"{WithoutText.Count} of {PageCount} pages have no text: {PageSurvey.ShortList(WithoutText, PageCount)}.",
    };

    /// <summary>
    /// The pages in old fonts that still need reading, for one Recognize text
    /// run: every old font of one language together (a Hindi book mixes Kruti
    /// Dev with Walkman Chanakya), the language with the most such pages when
    /// there are two, since Hindi and Myanmar cannot be read in one pass. Null
    /// when every such page has been recognised.
    /// </summary>
    public OcrRequest? LegacyRequest()
    {
        var best = Legacy
            .GroupBy(g => g.Font.OcrLanguage)
            .Select(g => (Language: g.Key, Names: g.Select(x => x.Font.Name).ToList(),
                          Pages: g.SelectMany(x => x.NotRecognised).Distinct().Order().ToList()))
            .Where(g => g.Pages.Count > 0)
            .OrderByDescending(g => g.Pages.Count)
            .FirstOrDefault();
        if (best.Pages is null)
        {
            return null;
        }

        // They HAVE text, the old font's, so "skip pages that already have
        // text" would skip every one. Hindi is read with English, which such
        // books mix in; Myanmar reads alone.
        return new OcrRequest(
            best.Pages,
            $"Pages in {string.Join(" and ", best.Names)}",
            best.Language == "hin" ? ["hin", "eng"] : [best.Language],
            true);
    }

    /// <summary>"Kruti Dev on 212 pages, 40 of them recognised."</summary>
    public static string LegacyLine(LegacyFontPages group)
    {
        int count = group.Pages.Count;
        int done = count - group.NotRecognised.Count;
        string pages = count == 1 ? "1 page" : $"{count} pages";
        return done switch
        {
            0 => $"{group.Font.Name} on {pages}.",
            _ when done == count => $"{group.Font.Name} on {pages}, all with recognised text.",
            _ => $"{group.Font.Name} on {pages}, {done} of them with recognised text.",
        };
    }
}

/// <summary>Reads and sums up the core's page check.</summary>
public static class PageSurvey
{
    /// <summary>
    /// The core's fields: per page its index, "1" when it has text, "1" when
    /// this app recognised it, a font count, then that many font names.
    /// </summary>
    public static List<PageFacts> Parse(byte[] buffer)
    {
        var fields = NulFields.Parse(buffer);
        var pages = new List<PageFacts>();
        int at = 0;
        while (at + 4 <= fields.Count
               && int.TryParse(fields[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
               && int.TryParse(fields[at + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fonts)
               && fonts >= 0
               && at + 4 + fonts <= fields.Count)
        {
            pages.Add(new PageFacts(page, fields[at + 1] == "1", fields[at + 2] == "1", fields.GetRange(at + 4, fonts)));
            at += 4 + fonts;
        }
        return pages;
    }

    public static PageFindings Summarise(IReadOnlyList<PageFacts> pages, int pageCount)
    {
        var withoutText = pages.Where(p => !p.HasText).Select(p => p.Page).Order().ToList();

        var byFont = new Dictionary<LegacyFont, (List<int> Pages, List<int> Open)>();
        var order = new List<LegacyFont>();
        foreach (var page in pages.OrderBy(p => p.Page))
        {
            foreach (var font in LegacyFonts.In(page.Fonts))
            {
                if (!byFont.TryGetValue(font, out var group))
                {
                    group = (new List<int>(), new List<int>());
                    byFont[font] = group;
                    order.Add(font);
                }
                group.Pages.Add(page.Page);
                if (!page.Recognised)
                {
                    group.Open.Add(page.Page);
                }
            }
        }

        var legacy = order.Select(f => new LegacyFontPages(f, byFont[f].Pages, byFont[f].Open)).ToList();
        return new PageFindings(pageCount, withoutText, legacy);
    }

    /// <summary>
    /// Page numbers for a sentence: "12-40, 88", and "3, 5, 7, 9, 11, 13 and
    /// 40 more" when there are too many runs to read.
    /// </summary>
    public static string ShortList(IReadOnlyList<int> pages, int pageCount, int maxRuns = 6)
    {
        var sorted = pages.Where(p => p >= 0 && p < pageCount).Distinct().Order().ToArray();
        var runs = PageSelection.Runs(sorted).ToList();
        if (runs.Count <= maxRuns)
        {
            return PageSelection.Format(sorted, pageCount);
        }

        var shown = runs.Take(maxRuns).ToList();
        int more = sorted.Length - shown.Sum(r => r.Count);
        string head = string.Join(", ", shown.Select(r => r.Count == 1 ? $"{r.First + 1}" : $"{r.First + 1}-{r.First + r.Count}"));
        return $"{head} and {more} more";
    }
}

/// <summary>What a PDF holds besides its pages, as <c>document_contents</c> counts it.</summary>
public sealed record DocumentContents(int Bookmarks, int Comments, int Links, int FormFields, int Signatures, int Attachments)
{
    public static DocumentContents FromPairs(IReadOnlyDictionary<string, string> p)
    {
        int N(string key) => p.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        return new DocumentContents(N("Bookmarks"), N("Comments"), N("Links"), N("FormFields"), N("Signatures"), N("Attachments"));
    }

    /// <summary>"48 bookmarks, 12 comments, a form with 9 fields, 3 attached files", or "Only pages".</summary>
    public string Describe()
    {
        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        var parts = new List<string>();
        if (Bookmarks > 0) { parts.Add(Count(Bookmarks, "bookmark", "bookmarks")); }
        if (Comments > 0) { parts.Add(Count(Comments, "comment", "comments")); }
        if (Links > 0) { parts.Add(Count(Links, "link", "links")); }
        if (FormFields > 0)
        {
            string form = FormFields == 1 ? "a form with 1 field" : $"a form with {FormFields} fields";
            if (Signatures > 0)
            {
                form += Signatures == 1 ? ", 1 for a signature" : $", {Signatures} for signatures";
            }
            parts.Add(form);
        }
        if (Attachments > 0) { parts.Add(Count(Attachments, "attached file", "attached files")); }
        return parts.Count == 0 ? "Only pages" : string.Join(", ", parts);
    }
}

/// <summary>A file attached to the document, as <c>list_attachments</c> reports it.</summary>
public sealed record AttachedFile(int Index, string Name, long Size)
{
    public static List<AttachedFile> FromBuffer(byte[] buffer)
    {
        var fields = NulFields.Parse(buffer);
        var files = new List<AttachedFile>();
        for (int i = 0; i + 1 < fields.Count; i += 2)
        {
            long.TryParse(fields[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
            files.Add(new AttachedFile(i / 2, fields[i], size));
        }
        return files;
    }
}

/// <summary>
/// What Document properties asks Recognize text to read: which pages, why
/// (the label the choice carries), and, when the reason decides it, the
/// language and whether pages that already have text are read anyway.
/// </summary>
public sealed record OcrRequest(IReadOnlyList<int> Pages, string What, IReadOnlyList<string>? Languages, bool ReadPagesWithText)
{
    /// <summary>The pages with no text, in the reader's own languages.</summary>
    public static OcrRequest ForPagesWithoutText(IReadOnlyList<int> pages) =>
        new(pages, "Pages with no text", null, false);
}
