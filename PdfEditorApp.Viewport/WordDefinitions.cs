using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>One part of speech of one dictionary word, with its definitions in order of use.</summary>
public sealed record WordSense(string Headword, string PartOfSpeech, IReadOnlyList<string> Definitions);

/// <summary>What Define shows for a looked-up word.</summary>
public sealed record WordDefinition(string Word, IReadOnlyList<WordSense> Senses);

/// <summary>
/// The offline English dictionary Define reads: Princeton WordNet 3.0, reduced
/// by tools/build_dictionary.py to single words and their first definitions.
/// </summary>
/// <remarks>
/// ⚠️ A SELECTED WORD IS RARELY THE DICTIONARY'S HEADWORD. A page says
/// "running", "went" and "children"; the dictionary lists run, go and child.
/// Lookup follows WordNet's own "morphy" method: the word as written, then the
/// irregular forms WordNet lists, then its regular suffix rules, plus the
/// doubled consonant those rules do not undo (stopped, running, bigger).
/// </remarks>
public sealed class WordDefinitions
{
    /// <summary>Most parts of speech a popup shows, so it stays a glance.</summary>
    public const int MaxSenses = 3;

    private static readonly (string Suffix, string Ending)[] NounRules =
        [("s", ""), ("ses", "s"), ("xes", "x"), ("zes", "z"), ("ches", "ch"), ("shes", "sh"), ("men", "man"), ("ies", "y")];

    private static readonly (string Suffix, string Ending)[] VerbRules =
        [("s", ""), ("ies", "y"), ("es", "e"), ("es", ""), ("ed", "e"), ("ed", ""), ("ing", "e"), ("ing", "")];

    private static readonly (string Suffix, string Ending)[] AdjectiveRules =
        [("er", ""), ("est", ""), ("er", "e"), ("est", "e")];

    private readonly Dictionary<string, List<(string Pos, string[] Definitions)>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Pos, string Form), List<string>> _irregular = new();

    private WordDefinitions()
    {
    }

    /// <summary>How many distinct words the dictionary holds.</summary>
    public int WordCount => _entries.Count;

    /// <summary>Reads the file tools/build_dictionary.py writes, already decompressed.</summary>
    public static WordDefinitions Load(TextReader reader)
    {
        var dictionary = new WordDefinitions();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 2 || line[1] != '\t')
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (line[0] == 'D' && fields.Length == 4)
            {
                if (!dictionary._entries.TryGetValue(fields[1], out var blocks))
                {
                    blocks = new List<(string, string[])>(1);
                    dictionary._entries[fields[1]] = blocks;
                }
                blocks.Add((fields[2], fields[3].Split('')));
            }
            else if (line[0] == 'X' && fields.Length == 4)
            {
                var key = (fields[1], fields[2]);
                if (!dictionary._irregular.TryGetValue(key, out var bases))
                {
                    bases = new List<string>(1);
                    dictionary._irregular[key] = bases;
                }
                bases.Add(fields[3]);
            }
        }

        return dictionary;
    }

    /// <summary>
    /// The definitions for <paramref name="word"/>, or null when the dictionary
    /// has nothing for it. The word as written comes first, then the base forms
    /// it was derived from, at most <see cref="MaxSenses"/> parts of speech.
    /// </summary>
    public WordDefinition? Lookup(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        string form = word.Trim().ToLowerInvariant().Replace('’', '\'');
        if (form.EndsWith("'s", StringComparison.Ordinal))
        {
            form = form[..^2];
        }

        var senses = new List<WordSense>();
        var seen = new HashSet<(string, string)>();

        void Add(string headword, string? onlyPos)
        {
            if (!_entries.TryGetValue(headword, out var blocks))
            {
                return;
            }

            foreach (var (pos, definitions) in blocks)
            {
                if ((onlyPos is null || onlyPos == pos) && seen.Add((headword, pos)))
                {
                    senses.Add(new WordSense(headword, NameOf(pos), definitions));
                }
            }
        }

        Add(form, null);

        foreach (var (pos, rules) in new[] { ("n", NounRules), ("v", VerbRules), ("a", AdjectiveRules), ("r", Array.Empty<(string, string)>()) })
        {
            if (_irregular.TryGetValue((pos, form), out var bases))
            {
                foreach (string b in bases)
                {
                    Add(b, pos);
                }
            }

            foreach (var (suffix, ending) in rules)
            {
                if (form.Length <= suffix.Length || !form.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string stem = form[..^suffix.Length];
                Add(stem + ending, pos);

                // stopped, running, bigger: the rule leaves "stopp", "runn",
                // "bigg", and the doubled letter has to go too.
                if (ending.Length == 0 && pos is "v" or "a" && stem.Length >= 3
                    && stem[^1] == stem[^2] && !IsVowel(stem[^1]))
                {
                    Add(stem[..^1], pos);
                }
            }
        }

        if (senses.Count == 0)
        {
            return null;
        }

        return new WordDefinition(word.Trim(), senses.Count > MaxSenses ? senses.GetRange(0, MaxSenses) : senses);
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static string NameOf(string pos) => pos switch
    {
        "n" => "noun",
        "v" => "verb",
        "a" => "adjective",
        "r" => "adverb",
        _ => pos,
    };
}
