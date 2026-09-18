using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>Which recogniser reads a page.</summary>
public enum OcrEngineKind
{
    /// <summary>Tesseract, for English and Hindi.</summary>
    Tesseract,

    /// <summary>Tesseract's line finding with mmpdfkit's Myanmar line model reading.
    /// The model reads English letters too, so Myanmar pages with English words need nothing else.</summary>
    Myanmar,

    /// <summary>Windows' built-in OCR: the Fast choice, English only.</summary>
    Windows,
}

/// <summary>
/// What a recognition run will use, decided from the languages chosen.
/// </summary>
/// <param name="TesseractLanguages">Tesseract's codes joined by '+', the main language first.</param>
public sealed record OcrPlan(OcrEngineKind Engine, string TesseractLanguages)
{
    /// <summary>The languages shipped with the app, in the order they are offered.</summary>
    public static IReadOnlyList<(string Code, string Name)> Bundled { get; } = new[]
    {
        ("eng", "English"),
        ("hin", "Hindi"),
        ("mya", "Myanmar"),
    };

    /// <summary>Every language Recognize text knows, in the order they are offered: the shipped three, then the downloadable ones.</summary>
    public static IEnumerable<string> Offered =>
        Bundled.Select(b => b.Code).Concat(OcrLanguageList.All.Select(l => l.Code));

    /// <summary>At most this many languages are read in one run. Each one makes Tesseract slower.</summary>
    public const int MostLanguages = 3;

    /// <summary>
    /// The stored "hin+eng" form back into codes, in the order they are offered.
    /// Unknown codes are dropped; nothing known at all reads as English.
    /// </summary>
    public static IReadOnlyList<string> ParseLanguages(string? stored)
    {
        var chosen = (stored ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToHashSet();
        var known = Offered.Where(chosen.Contains).ToList();
        return known.Count > 0 ? known : new[] { "eng" };
    }

    public static string StoreLanguages(IEnumerable<string> codes) =>
        string.Join('+', Offered.Where(codes.Contains));

    /// <summary>What stops a run with these choices, in words a window can show, or null.</summary>
    public static string? Problem(IReadOnlyCollection<string> codes, bool fast)
    {
        if (codes.Count == 0)
        {
            return "Choose at least one language.";
        }
        // Myanmar has a reader of its own, which reads English letters too but
        // nothing else.
        if (codes.Contains("mya") && codes.FirstOrDefault(c => c is not "mya" and not "eng") is { } other)
        {
            return $"Myanmar and {OcrLanguageCatalog.NameOf(other)} can't be recognised in one pass yet. Choose one of them.";
        }
        if (fast && !(codes.Count == 1 && codes.Contains("eng")))
        {
            return "Fast works for English only. Choose Accurate for other languages.";
        }
        if (codes.Count > MostLanguages)
        {
            return "Choose up to three languages at a time. Each one makes reading slower.";
        }
        return null;
    }

    /// <summary>The plan for choices <see cref="Problem"/> has passed.</summary>
    public static OcrPlan For(IReadOnlyCollection<string> codes, bool fast)
    {
        if (codes.Contains("mya"))
        {
            return new OcrPlan(OcrEngineKind.Myanmar, "mya");
        }
        if (fast)
        {
            return new OcrPlan(OcrEngineKind.Windows, "eng");
        }
        // The main languages first and English last: Tesseract leans on the
        // first one it is given, and English is mostly the odd word in a page
        // of something else.
        var main = Offered.Where(c => c != "eng" && codes.Contains(c)).ToList();
        if (codes.Contains("eng") || main.Count == 0)
        {
            main.Add("eng");
        }
        return new OcrPlan(OcrEngineKind.Tesseract, string.Join('+', main));
    }
}
