using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Drops what a recogniser reads out of dirt: specks, fold marks and grain on a
/// scan, which it will happily turn into one- and two-character "words".
/// </summary>
/// <remarks>
/// Measured on a near-blank scanned page: Tesseract's sparse mode read the
/// grain as roughly 600 lines of single marks. Written into the page, that is
/// junk every search and every copy picks up.
/// </remarks>
public static class OcrWordFilter
{
    /// <summary>Below this height, as a fraction of the page, nothing is text:
    /// about 5 pt on an A4 page.</summary>
    public const double MinHeight = 0.006;

    /// <summary>A short word this much smaller than the page's typical word is a speck.</summary>
    public const double ShortWordShare = 0.4;

    /// <summary>A short word read with less confidence than this is not kept.</summary>
    public const double MinConfidence = 30;

    public static IReadOnlyList<OcrWord> WithoutSpecks(IReadOnlyList<OcrWord> words)
    {
        var real = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Height >= MinHeight).ToList();
        if (real.Count == 0)
        {
            return real;
        }

        // The page's typical word height, from words long enough to be sure of.
        var sure = real.Where(w => TextLength(w.Text) >= 3).Select(w => w.Height).ToList();
        double typical = Median(sure.Count > 0 ? sure : real.Select(w => w.Height).ToList());

        return real.Where(w =>
        {
            string text = w.Text.Trim();
            if (OnlyMarks(text))
            {
                // A vowel sign or asat on its own is never a word.
                return false;
            }

            if (TextLength(text) > 2)
            {
                return true;
            }

            if (w.Height < typical * ShortWordShare)
            {
                return false;
            }

            if (!double.IsNaN(w.Confidence) && w.Confidence < MinConfidence)
            {
                return false;
            }

            // A lone symbol with no confidence to vouch for it. Myanmar's own
            // section and full stops (၊ ။) are real words on their own.
            return !(TextLength(text) == 1 && IsStraySymbol(text[0]) && double.IsNaN(w.Confidence));
        }).ToList();
    }

    private static int TextLength(string text) => new StringInfo(text.Trim()).LengthInTextElements;

    private static bool OnlyMarks(string text) =>
        text.Length > 0 && text.All(c => CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

    private static bool IsStraySymbol(char c) =>
        c != '၊' && c != '။' && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
