using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PdfEditorApp.Viewport;
using Tesseract;

namespace PdfEditorApp.Ocr;

/// <summary>
/// Myanmar: Tesseract finds the lines, mmpdfkit's line model reads them.
/// </summary>
/// <remarks>
/// <para>
/// Measured on this project's benchmark (300 DPI): Tesseract reading Myanmar
/// itself got 3.1% of characters wrong at best and dropped the medial ha;
/// its default layout got 35% wrong by cutting the marks above and below the
/// letters into lines of their own. The line model reading the lines Tesseract's
/// sparse layout finds got 0.1%.
/// </para>
/// <para>
/// Lines that layout skips altogether (it dropped two whole sentences on a
/// 14 pt page) are found from the rows that still hold ink.
/// </para>
/// </remarks>
internal sealed class MyanmarRecognizer : IDisposable
{
    private readonly TesseractRecognizer _layout;
    private readonly SessionOptions _options;
    private readonly InferenceSession _session;
    private readonly int _dpi;

    /// <param name="threads">Threads for the model within one page. Pages in
    /// parallel barely helped this model (it is memory-bound); threads inside a
    /// page did.</param>
    public MyanmarRecognizer(int dpi, int threads)
    {
        _dpi = dpi;
        _layout = new TesseractRecognizer("mya", dpi);
        _options = new SessionOptions { IntraOpNumThreads = Math.Max(1, threads), InterOpNumThreads = 1 };
        _session = new InferenceSession(OcrAssets.MyanmarModel, _options);
    }

    public IReadOnlyList<OcrWord> Read(GrayImage page)
    {
        int minHeight = Math.Max(1, (int)Math.Ceiling(OcrWordFilter.MinHeight * page.Height));
        var found = _layout.FindLines(page, PageSegMode.SparseText)
            .Where(b => b.Height >= minHeight)
            .Select(b => new LineBox(b.X1, b.Y1, b.X2, b.Y2, FromLayout: true))
            .ToList();

        // Lines layout skipped: bands of inked rows no found line covers.
        int threshold = page.OtsuThreshold();
        var covered = found.Select(b => (Math.Max(0, b.Top - Pad(b)), Math.Min(page.Height, b.Bottom + Pad(b)))).ToList();
        var missed = OcrLineGaps.Uncovered(page.RowsWithInk(threshold), covered, Math.Max(minHeight, _dpi / 16), _dpi);
        foreach (var (top, bottom) in missed)
        {
            if (page.InkColumns(top, bottom, threshold) is (int left, int right))
            {
                found.Add(new LineBox(left, top, right + 1, bottom, FromLayout: false));
            }
        }

        var words = new List<OcrWord>();
        foreach (LineBox line in found.OrderBy(b => b.Top).ThenBy(b => b.Left))
        {
            int pad = Pad(line);
            int left = Math.Max(0, line.Left - pad);
            int top = Math.Max(0, line.Top - pad);
            int right = Math.Min(page.Width, line.Right + pad);
            int bottom = Math.Min(page.Height, line.Bottom + pad);
            GrayImage crop = page.Crop(left, top, right - left, bottom - top);

            var read = ReadLine(crop);

            // A band found only by its ink could be a picture's edge. It has to
            // read like a line of text to be kept.
            if (!line.FromLayout && read.Sum(w => w.Text.Length) < 6 && read.Count < 2)
            {
                continue;
            }

            foreach (var w in read)
            {
                words.Add(new OcrWord(
                    MyanmarDigits.Fix(w.Text),
                    (left + w.Start * crop.Width) / page.Width,
                    (double)line.Top / page.Height,
                    (left + w.End * crop.Width) / page.Width,
                    (double)line.Bottom / page.Height));
            }
        }

        return OcrWordFilter.WithoutSpecks(words);
    }

    public void Dispose()
    {
        _session.Dispose();
        _options.Dispose();
        _layout.Dispose();
    }

    private static int Pad(LineBox line) => Math.Max(2, (line.Bottom - line.Top) / 5);

    private IReadOnlyList<CtcLineReader.LineWord> ReadLine(GrayImage crop)
    {
        // "Normalise to 32px tall, double width": two Lanczos-4 resizes, exactly
        // as mmpdfkit's ocr.py prepares a line. Without the doubling, accuracy
        // fell from 0.1% to 6.7% wrong on the benchmark.
        GrayImage line = crop;
        if (crop.Height != 32)
        {
            int width = Math.Max(1, (int)Math.Round(crop.Width * 32.0 / crop.Height, MidpointRounding.ToEven));
            line = crop.Resize(width, 32);
        }
        line = line.Resize(line.Width * 2, 32);

        var input = new float[32 * line.Width];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = line.Pixels[i] / 255f;
        }

        var tensor = new DenseTensor<float>(input, new[] { 1, 1, 32, line.Width });
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", tensor) });
        Tensor<float> logits = results.First().AsTensor<float>();

        var dims = logits.Dimensions;
        int steps = dims[0] == 1 ? dims[1] : dims[0];
        int classes = dims[2];
        // (1, T, C) and (T, 1, C) both lay step t's scores at t * C.
        ReadOnlySpan<float> scores = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray();
        return CtcLineReader.Read(scores, steps, classes);
    }

    private readonly record struct LineBox(int Left, int Top, int Right, int Bottom, bool FromLayout);
}
