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
        var known = Bundled.Select(b => b.Code).Where(chosen.Contains).ToList();
        return known.Count > 0 ? known : new[] { "eng" };
    }

    public static string StoreLanguages(IEnumerable<string> codes) =>
        string.Join('+', Bundled.Select(b => b.Code).Where(codes.Contains));

    /// <summary>What stops a run with these choices, in words a window can show, or null.</summary>
    public static string? Problem(IReadOnlyCollection<string> codes, bool fast)
    {
        if (codes.Count == 0)
        {
            return "Choose at least one language.";
        }
        if (codes.Contains("mya") && codes.Contains("hin"))
        {
            return "Myanmar and Hindi can't be recognised in one pass yet. Choose one of them.";
        }
        if (fast && !(codes.Count == 1 && codes.Contains("eng")))
        {
            return "Fast works for English only. Choose Accurate for Hindi or Myanmar.";
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
        // The main language first: Tesseract leans on the first one it is given.
        return new OcrPlan(OcrEngineKind.Tesseract, codes.Contains("hin")
            ? string.Join('+', new[] { "hin", "eng" }.Where(codes.Contains))
            : "eng");
    }
}
