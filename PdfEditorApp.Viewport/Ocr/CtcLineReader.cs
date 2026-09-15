using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Turns the scores of the Myanmar line model into words, and says where along
/// the line each word was.
/// </summary>
/// <remarks>
/// <para>
/// The model is mmpdfkit's (github.com/kaungsithu/mmpdfkit, MIT; the model file
/// itself is Apache 2.0, huggingface.co/ksithu/myanmar-crnn-ocr). Its character
/// list and its decoding are copied from mmpdfkit's ocr.py so the scores mean
/// exactly what they meant in training.
/// </para>
/// <para>
/// Each score row is one step along the line, left to right, so a character's
/// step is also its position: that is what lets a word be placed on the page
/// without a second recogniser to find it.
/// </para>
/// </remarks>
public static class CtcLineReader
{
    /// <summary>Where "&lt;blank&gt;" sorts in the vocabulary: after the ten
    /// punctuation marks below it, the ten digits and ':' ';'.</summary>
    public const int BlankIndex = 22;

    public const int ClassCount = 272;

    public static IReadOnlyList<string> Vocabulary { get; } = BuildVocabulary();

    /// <summary>A word read off a line, its span as fractions of the line's width.</summary>
    public readonly record struct LineWord(string Text, double Start, double End);

    /// <summary>
    /// Greedy CTC decoding, as mmpdfkit does it: the best class at each step,
    /// repeats collapsed, a blank ending a repeat. Spaces split words.
    /// </summary>
    /// <param name="scores">Row by row, <paramref name="steps"/> rows of <paramref name="classes"/>.</param>
    public static IReadOnlyList<LineWord> Read(ReadOnlySpan<float> scores, int steps, int classes)
    {
        if (steps <= 0 || classes <= 0 || scores.Length < steps * classes)
        {
            return Array.Empty<LineWord>();
        }

        var words = new List<LineWord>();
        var current = new StringBuilder();
        int firstStep = -1;
        int lastStep = -1;
        int previous = -1;

        void EndWord()
        {
            if (current.Length > 0)
            {
                words.Add(new LineWord(current.ToString(), (double)firstStep / steps, (double)(lastStep + 1) / steps));
                current.Clear();
            }
            firstStep = -1;
        }

        for (int t = 0; t < steps; t++)
        {
            ReadOnlySpan<float> step = scores.Slice(t * classes, classes);
            int best = 0;
            for (int c = 1; c < step.Length; c++)
            {
                if (step[c] > step[best]) { best = c; }
            }

            if (best == BlankIndex)
            {
                previous = -1;
                continue;
            }
            if (best == previous)
            {
                // The same character held across steps; it still reaches this far.
                if (current.Length > 0) { lastStep = t; }
                continue;
            }
            previous = best;

            string symbol = best < Vocabulary.Count ? Vocabulary[best] : "";
            if (symbol == " ")
            {
                EndWord();
                continue;
            }

            if (firstStep < 0) { firstStep = t; }
            lastStep = t;
            current.Append(symbol);
        }

        EndWord();
        return words;
    }

    private static string[] BuildVocabulary()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "<blank>" };
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789") { set.Add(c.ToString()); }
        foreach (char c in ".,!?;:()[]{}\"'/- ") { set.Add(c.ToString()); }
        for (int c = 0x1000; c <= 0x109F; c++) { set.Add(((char)c).ToString()); }
        for (int c = 0xAA60; c <= 0xAA7F; c++) { set.Add(((char)c).ToString()); }

        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }
}
