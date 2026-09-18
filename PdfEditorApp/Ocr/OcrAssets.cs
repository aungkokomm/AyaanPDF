using System;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;

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

    /// <summary>Shipped with the app, or downloaded and whole.</summary>
    public static bool HasTesseractLanguage(string code) =>
        File.Exists(Path.Combine(Tessdata, code + ".traineddata")) || OcrLanguageStore.IsInstalled(code);

    private static readonly object CopyLock = new();

    /// <summary>
    /// The folder Tesseract reads a run's languages from. It takes one folder
    /// for all of them, so a run of shipped languages reads from beside the
    /// exe, and a run with a downloaded one reads from the user's folder with
    /// the shipped ones it needs copied in beside it.
    /// </summary>
    /// <param name="languages">Tesseract's codes joined by '+', e.g. "ben+eng".</param>
    public static string TessdataFor(string languages)
    {
        var codes = languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.All(c => File.Exists(Path.Combine(Tessdata, c + ".traineddata"))))
        {
            return Tessdata;
        }

        // Each page worker makes its own engine, so several can ask at once.
        lock (CopyLock)
        {
            Directory.CreateDirectory(OcrLanguageStore.Folder);
            foreach (string code in codes)
            {
                var shipped = new FileInfo(Path.Combine(Tessdata, code + ".traineddata"));
                var copy = new FileInfo(OcrLanguageStore.PathOf(code));
                if (shipped.Exists && (!copy.Exists || copy.Length != shipped.Length))
                {
                    File.Copy(shipped.FullName, copy.FullName, overwrite: true);
                }
            }
        }
        return OcrLanguageStore.Folder;
    }

    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>
    /// A font whose glyphs cover the text, for the invisible layer, or null for
    /// Helvetica. The words are never drawn, but a reader maps each character
    /// through the font to extract it, so a font without the script would leave
    /// the page unsearchable. Helvetica embeds nothing, so plain Latin text
    /// costs the file no font at all. Which font carries which script is
    /// <see cref="OcrScripts"/>'s, proved per script in the core.
    /// </summary>
    public static string? FontFor(string text) =>
        OcrScripts.ScriptOf(text) is { } script ? FirstExisting(OcrScripts.FontsFor(script)) : null;

    /// <summary>Whether this PC has a font for a script, without which its words would not be searchable.</summary>
    public static bool HasFontFor(string script) => FirstExisting(OcrScripts.FontsFor(script)) is not null;

    private static string? FirstExisting(System.Collections.Generic.IEnumerable<string> names) =>
        names.Select(n => Path.Combine(FontsFolder, n)).FirstOrDefault(File.Exists);
}
