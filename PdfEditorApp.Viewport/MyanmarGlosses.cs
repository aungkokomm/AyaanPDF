using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Myanmar meanings for English words, from the AKK English-Myanmar
/// dictionary, reduced by tools/build_myanmar_glosses.py to single words and
/// their first few glosses for each part of speech.
/// </summary>
/// <remarks>
/// ⚠️ LOOKED UP BY THE HEADWORD AND PART OF SPEECH DEFINE ALREADY FOUND, never
/// by the word as written: "running" is glossed as the verb "run", so each
/// Myanmar line translates the English meaning it is shown with. The glosses
/// keep the AKK dictionary's own order, the order the AKK app shows them in.
/// </remarks>
public sealed class MyanmarGlosses
{
    /// <summary>Most glosses kept for one word and part of speech.</summary>
    public const int MaxGlosses = 3;

    private const char Separator = (char)0x1F;

    private readonly Dictionary<(string Word, string Pos), string[]> _glosses = new();

    private MyanmarGlosses()
    {
    }

    /// <summary>How many word and part-of-speech pairs have glosses.</summary>
    public int Count => _glosses.Count;

    /// <summary>Reads the file tools/build_myanmar_glosses.py writes, already decompressed.</summary>
    public static MyanmarGlosses Load(TextReader reader)
    {
        var glosses = new MyanmarGlosses();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 4 || fields[0] != "G" || fields[3].Length == 0)
            {
                continue;
            }

            glosses._glosses[(fields[1], fields[2])] = fields[3].Split(Separator);
        }

        return glosses;
    }

    /// <summary>
    /// The Myanmar glosses for <paramref name="headword"/> as
    /// <paramref name="partOfSpeech"/>, named the way <see cref="WordSense"/>
    /// names it (noun, verb, adjective, adverb), or empty when there are none.
    /// </summary>
    public IReadOnlyList<string> For(string headword, string partOfSpeech)
    {
        string? pos = partOfSpeech switch
        {
            "noun" => "n",
            "verb" => "v",
            "adjective" => "a",
            "adverb" => "r",
            _ => null,
        };

        return pos is not null && _glosses.TryGetValue((headword.ToLowerInvariant(), pos), out var found)
            ? found
            : Array.Empty<string>();
    }

    /// <summary>
    /// Every part of speech the file has for <paramref name="word"/>, in the
    /// order noun, verb, adjective, adverb: for a word the English dictionary
    /// has no entry for, so no part of speech was settled on to look up by.
    /// </summary>
    public IReadOnlyList<(string PartOfSpeech, IReadOnlyList<string> Meanings)> ForWord(string word)
    {
        var found = new List<(string, IReadOnlyList<string>)>();
        foreach (string partOfSpeech in new[] { "noun", "verb", "adjective", "adverb" })
        {
            if (For(word, partOfSpeech) is { Count: > 0 } meanings)
            {
                found.Add((partOfSpeech, meanings));
            }
        }

        return found;
    }
}
