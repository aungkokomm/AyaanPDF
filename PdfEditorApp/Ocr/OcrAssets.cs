using System;
using System.IO;
using System.Linq;

namespace PdfEditorApp.Ocr;

/// <summary>Where the OCR models are, and which font carries each script in the text layer.</summary>
internal static class OcrAssets
{
    /// <summary>Beside the exe: the models are read at runtime, not packaged.</summary>
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr");

    public static string Tessdata { get; } = Path.Combine(Folder, "tessdata");

    /// <summary>mmpdfkit's Myanmar line model, converted to float convolutions
    /// by tools/ocr/fetch_ocr_models.py.</summary>
    public static string MyanmarModel { get; } = Path.Combine(Folder, "myanmar-crnn-ocr.onnx");

    public static bool HasTesseractLanguage(string code) =>
        File.Exists(Path.Combine(Tessdata, code + ".traineddata"));

    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>
    /// A font whose glyphs cover the text, for the invisible layer, or null for
    /// Helvetica. The words are never drawn, but a reader maps each character
    /// through the font to extract it, so a font without the script would leave
    /// the page unsearchable. Helvetica embeds nothing, so plain Latin text
    /// costs the file no font at all.
    /// </summary>
    public static string? FontFor(string text)
    {
        if (text.Any(IsMyanmar))
        {
            return FirstExisting("mmrtext.ttf", "Pyidaungsu.ttf");
        }
        if (text.Any(IsDevanagari))
        {
            return FirstExisting("Nirmala.ttf", "mangal.ttf");
        }
        // Beyond Latin-1, Helvetica's encoding cannot carry the character.
        return text.Any(c => c > 'ÿ') ? FirstExisting("arial.ttf") : null;
    }

    private static bool IsMyanmar(char c) =>
        (c >= 'က' && c <= '႟') || (c >= 'ꩠ' && c <= 'ꩿ') || (c >= 'ꧠ' && c <= '꧿');

    private static bool IsDevanagari(char c) =>
        (c >= 'ऀ' && c <= 'ॿ') || (c >= '꣠' && c <= 'ꣿ');

    private static string? FirstExisting(params string[] names) =>
        names.Select(n => Path.Combine(FontsFolder, n)).FirstOrDefault(File.Exists);
}
