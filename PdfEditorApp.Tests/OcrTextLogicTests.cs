using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The plain logic behind OCR: reading the Myanmar model's scores, settling
/// zero against wa, dropping specks, and finding lines the layout pass skipped.
/// </summary>
public class OcrTextLogicTests
{
    private static float[] Scores(params string[] steps)
    {
        var vocab = CtcLineReader.Vocabulary;
        var scores = new float[steps.Length * CtcLineReader.ClassCount];
        for (int t = 0; t < steps.Length; t++)
        {
            int index = steps[t] == "" ? CtcLineReader.BlankIndex : vocab.ToList().IndexOf(steps[t]);
            Assert.True(index >= 0, $"'{steps[t]}' is not in the vocabulary");
            scores[t * CtcLineReader.ClassCount + index] = 1f;
        }
        return scores;
    }

    [Fact]
    public void the_vocabulary_is_mmpdfkits_272_classes_with_the_blank_where_training_put_it()
    {
        Assert.Equal(CtcLineReader.ClassCount, CtcLineReader.Vocabulary.Count);
        Assert.Equal("<blank>", CtcLineReader.Vocabulary[CtcLineReader.BlankIndex]);
        Assert.Equal(" ", CtcLineReader.Vocabulary[0]);
        Assert.Contains("က", CtcLineReader.Vocabulary);
        Assert.Contains("ꩠ", CtcLineReader.Vocabulary);
    }

    [Fact]
    public void repeats_collapse_a_blank_separates_them_and_spaces_split_words_with_their_positions()
    {
        // က held for two steps, a blank, က again, a space, then ခ: "ကက ခ".
        var words = CtcLineReader.Read(Scores("က", "က", "", "က", " ", "ခ"), 6, CtcLineReader.ClassCount);

        Assert.Equal(2, words.Count);
        Assert.Equal("ကက", words[0].Text);
        Assert.Equal(0.0, words[0].Start, 6);
        Assert.Equal(4.0 / 6, words[0].End, 6);
        Assert.Equal("ခ", words[1].Text);
        Assert.Equal(5.0 / 6, words[1].Start, 6);
        Assert.Equal(1.0, words[1].End, 6);
    }

    [Fact]
    public void a_line_of_only_blanks_reads_as_nothing()
    {
        Assert.Empty(CtcLineReader.Read(Scores("", "", ""), 3, CtcLineReader.ClassCount));
        Assert.Empty(CtcLineReader.Read(ReadOnlySpan<float>.Empty, 0, CtcLineReader.ClassCount));
    }

    [Theory]
    [InlineData("ကီလို မီတာ ၆ဝ မှ ၁ဝဝဝ", "ကီလို မီတာ ၆၀ မှ ၁၀၀၀")]
    [InlineData("၀တ္ထု", "ဝတ္ထု")]
    [InlineData("၀င်", "ဝင်")]
    [InlineData("ဝ၁", "၀၁")]
    [InlineData("၁၀", "၁၀")]
    [InlineData("ဝတ္ထု ၂၀၂၆", "ဝတ္ထု ၂၀၂၆")]
    [InlineData("ဝ", "ဝ")]
    [InlineData("( ၀ )", "( ၀ )")]
    public void zero_and_wa_are_settled_by_what_the_circle_stands_next_to(string read, string expected)
    {
        Assert.Equal(expected, MyanmarDigits.Fix(read));
    }

    private static OcrWord Word(string text, double top, double height, double confidence = double.NaN) =>
        new(text, 0.1, top, 0.2, top + height, confidence);

    [Fact]
    public void specks_marks_and_unsure_scraps_are_dropped_and_real_words_kept()
    {
        var words = new List<OcrWord>
        {
            Word("မင်္ဂလာပါ", 0.10, 0.02),
            Word("ဆရာမ", 0.13, 0.02),
            Word("။", 0.16, 0.018),            // a real full stop on its own
            Word("ိ", 0.20, 0.02),             // a vowel sign alone
            Word("ဒ", 0.30, 0.004),            // below the minimum height
            Word("က", 0.40, 0.006),            // short and far smaller than the words around it
            Word("of", 0.50, 0.02, confidence: 12), // short and unsure
            Word("to", 0.53, 0.02, confidence: 91), // short and sure
            Word("~", 0.56, 0.02),             // a stray symbol nobody vouched for
        };

        var kept = OcrWordFilter.WithoutSpecks(words).Select(w => w.Text).ToArray();

        Assert.Equal(new[] { "မင်္ဂလာပါ", "ဆရာမ", "။", "to" }, kept);
    }

    [Fact]
    public void a_band_of_ink_no_found_line_covers_is_reported_and_covered_ones_are_not()
    {
        // Rows 10-29 text (found), 40-59 text (MISSED), 70-89 text (found),
        // 95-96 a speck, 100-399 a picture.
        var ink = new bool[420];
        void Mark(int from, int to) { for (int y = from; y < to; y++) { ink[y] = true; } }
        Mark(10, 30); Mark(40, 60); Mark(70, 90); Mark(95, 97); Mark(100, 400);

        // Found lines are padded, so the first one clips two rows of the missed band.
        var found = new List<(int, int)> { (6, 42), (66, 94) };

        var missed = OcrLineGaps.Uncovered(ink, found, minHeight: 8, maxHeight: 200);

        Assert.Equal(new[] { (40, 60) }, missed.ToArray());
    }
}
