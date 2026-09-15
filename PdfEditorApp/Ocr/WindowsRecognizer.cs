using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Ocr;

/// <summary>
/// Windows' built-in OCR: the Fast choice for English.
/// </summary>
/// <remarks>
/// About five times quicker than Tesseract on this project's benchmark, and
/// just as exact on clean pages, but it misread blurry phone scans ("thc
/// plcasurc", dropped bullets) that Tesseract read correctly. That is why
/// Accurate is the default and this is the choice.
/// </remarks>
internal sealed class WindowsRecognizer
{
    private readonly Windows.Media.Ocr.OcrEngine _engine;

    private WindowsRecognizer(Windows.Media.Ocr.OcrEngine engine) => _engine = engine;

    /// <summary>Null when Windows has no OCR installed for that language.</summary>
    public static WindowsRecognizer? TryCreate(string languageTag)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));
        return engine is null ? null : new WindowsRecognizer(engine);
    }

    public async Task<IReadOnlyList<OcrWord>> ReadAsync(GrayImage page)
    {
        using var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            page.Pixels.AsBuffer(), Windows.Graphics.Imaging.BitmapPixelFormat.Gray8, page.Width, page.Height);
        Windows.Media.Ocr.OcrResult result = await _engine.RecognizeAsync(bitmap);

        var words = new List<OcrWord>();
        foreach (Windows.Media.Ocr.OcrLine line in result.Lines)
        {
            foreach (Windows.Media.Ocr.OcrWord word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text))
                {
                    continue;
                }
                var box = word.BoundingRect;
                words.Add(new OcrWord(
                    word.Text.Trim(),
                    box.X / page.Width,
                    box.Y / page.Height,
                    (box.X + box.Width) / page.Width,
                    (box.Y + box.Height) / page.Height));
            }
        }
        return words;
    }
}
