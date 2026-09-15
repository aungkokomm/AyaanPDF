using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Hindi meanings for English words, from the user's own English-Hindi
/// dictionary, cleaned and ranked by tools/build_hindi_glosses.py.
/// </summary>
/// <remarks>
/// ⚠️ LOOKED UP BY THE HEADWORD AND PART OF SPEECH DEFINE ALREADY FOUND, never
/// by the word as written, exactly as <see cref="MyanmarGlosses"/> is: "running"
/// is glossed as the verb "run", so each Hindi line translates the English
/// meaning it is shown with.
/// </remarks>
public sealed class HindiGlosses
{
    /// <summary>Most meanings kept for one word and part of speech.</summary>
    public const int MaxMeanings = 3;

    private const char Separator = (char)0x1F;

    private readonly Dictionary<(string Word, string Pos), string[]> _glosses = new();

    private HindiGlosses()
    {
    }

    /// <summary>How many word and part-of-speech pairs have Hindi meanings.</summary>
    public int Count => _glosses.Count;

    /// <summary>Reads the file tools/build_hindi_glosses.py writes, already decompressed.</summary>
    public static HindiGlosses Load(TextReader reader)
    {
        var glosses = new HindiGlosses();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 4 || fields[0] != "H" || fields[3].Length == 0)
            {
                continue;
            }

            glosses._glosses[(fields[1], fields[2])] = fields[3].Split(Separator);
        }

        return glosses;
    }

    /// <summary>
    /// The Hindi meanings for <paramref name="headword"/> as
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
}
