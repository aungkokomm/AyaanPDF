using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Tesseract;
using TessPage = Tesseract.Page;

namespace PdfEditorApp.Ocr;

/// <summary>
/// Tesseract: the reader for English and Hindi, and the line finder for Myanmar.
/// </summary>
/// <remarks>
/// One per thread. An engine holds state for the page it is on and is not safe
/// to share, so parallel pages each get their own.
/// </remarks>
internal sealed class TesseractRecognizer : IDisposable
{
    private readonly TesseractEngine _engine;

    /// <param name="languages">Tesseract's codes joined by '+', e.g. "hin+eng".</param>
    public TesseractRecognizer(string languages, int dpi)
    {
        _engine = new TesseractEngine(OcrAssets.TessdataFor(languages), languages, EngineMode.LstmOnly);
        _engine.SetVariable("user_defined_dpi", dpi.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Every word on the page, with Tesseract's confidence in it.</summary>
    public IReadOnlyList<OcrWord> Read(GrayImage page, PageSegMode mode)
    {
        var words = new List<OcrWord>();
        using Pix pix = ToPix(page);
        using TessPage result = _engine.Process(pix, mode);
        using ResultIterator it = result.GetIterator();
        it.Begin();
        do
        {
            if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out Rect box))
            {
                continue;
            }
            string? text = it.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            words.Add(new OcrWord(
                text,
                (double)box.X1 / page.Width,
                (double)box.Y1 / page.Height,
                (double)box.X2 / page.Width,
                (double)box.Y2 / page.Height,
                it.GetConfidence(PageIteratorLevel.Word)));
        }
        while (it.Next(PageIteratorLevel.Word));
        return words;
    }

    /// <summary>
    /// The text lines Tesseract's layout analysis finds, in pixels, without
    /// reading any of them. Layout alone is a fraction of the cost of reading.
    /// </summary>
    public IReadOnlyList<Rect> FindLines(GrayImage page, PageSegMode mode)
    {
        var lines = new List<Rect>();
        using Pix pix = ToPix(page);
        using TessPage result = _engine.Process(pix, mode);
        using PageIterator it = result.AnalyseLayout();
        it.Begin();
        do
        {
            if (it.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect box) && box.Width > 2 && box.Height > 2)
            {
                lines.Add(box);
            }
        }
        while (it.Next(PageIteratorLevel.TextLine));
        return lines;
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// The pixels go straight into Leptonica's buffer: 8 bits a pixel, packed
    /// into 32-bit words with the first pixel in the most significant byte.
    /// Loading from memory instead writes a temp file per page on Windows,
    /// which measured as a large share of Tesseract's time.
    /// </summary>
    private static Pix ToPix(GrayImage page)
    {
        Pix pix = Pix.Create(page.Width, page.Height, 8);
        PixData data = pix.GetData();
        int wordsPerLine = data.WordsPerLine;
        var row = new int[wordsPerLine];
        for (int y = 0; y < page.Height; y++)
        {
            Array.Clear(row);
            int offset = y * page.Width;
            for (int x = 0; x < page.Width; x++)
            {
                row[x >> 2] |= page.Pixels[offset + x] << (24 - 8 * (x & 3));
            }
            Marshal.Copy(row, 0, data.Data + y * wordsPerLine * 4, wordsPerLine);
        }
        return pix;
    }
}
