using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Hindi meanings for English words, from the user's own English-Hindi word
/// list, converted from Kruti Dev to Unicode by tools/build_hindi_glosses.py.
/// </summary>
/// <remarks>
/// ⚠️ KEYED BY WORD ALONE. The word list has no parts of speech, so unlike
/// <see cref="MyanmarGlosses"/> a meaning cannot be put under the English
/// sense it translates; it is looked up by the dictionary word the English
/// lookup settled on ("running" by "running" and by "run").
/// </remarks>
public sealed class HindiGlosses
{
    private const char Separator = (char)0x1F;

    private readonly Dictionary<string, string[]> _glosses = new(StringComparer.Ordinal);

    private HindiGlosses()
    {
    }

    /// <summary>How many words have Hindi meanings.</summary>
    public int Count => _glosses.Count;

    /// <summary>Reads the file tools/build_hindi_glosses.py writes, already decompressed.</summary>
    public static HindiGlosses Load(TextReader reader)
    {
        var glosses = new HindiGlosses();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || fields[0] != "H" || fields[2].Length == 0)
            {
                continue;
            }

            glosses._glosses[fields[1]] = fields[2].Split(Separator);
        }

        return glosses;
    }

    /// <summary>The Hindi meanings for <paramref name="headword"/>, or empty when there are none.</summary>
    public IReadOnlyList<string> For(string headword) =>
        _glosses.TryGetValue(headword.ToLowerInvariant(), out var found) ? found : Array.Empty<string>();
}
