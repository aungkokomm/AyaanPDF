using System;
using System.Collections.Generic;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One part of speech of one dictionary word, with its definitions in order of
/// use, and a short example of the first definition when WordNet gives one.
/// </summary>
public sealed record WordSense(
    string Headword, string PartOfSpeech, IReadOnlyList<string> Definitions, string? Example = null);

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

    /// <summary>Words Define answers "no definition" for, whatever the file holds.</summary>
    /// <remarks>
    /// ⚠️ WORDNET HAS NO GRAMMAR WORDS. It holds nouns, verbs, adjectives and
    /// adverbs only, so where an article, pronoun, preposition, conjunction or
    /// form of "be" seems to be in it, what matched is an abbreviation or a
    /// symbol spelt the same way: "a" is the angstrom, "I" iodine, "me" Maine,
    /// "at" astatine, "who" the World Health Organization, "may" the month.
    /// Every one of those is the wrong answer for a word read in a sentence.
    /// Small words WordNet really defines as adverbs (in, up, so, but, by) are
    /// deliberately not listed.
    /// </remarks>
    private static readonly HashSet<string> GrammarWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the",
        "i", "me", "my", "myself", "we", "us", "our", "ours", "you", "your", "yours",
        "he", "him", "his", "she", "her", "hers", "it", "its", "they", "them", "their", "theirs",
        "this", "that", "these", "those", "who", "whom", "whose", "which", "what",
        "and", "or", "nor", "if", "of", "to", "at", "for", "from", "with", "as", "into", "onto", "upon", "than",
        "am", "is", "are", "was", "were", "be", "been", "does", "may",
    };

    private readonly Dictionary<string, List<(string Pos, string[] Definitions, string? Example)>> _entries = new(StringComparer.Ordinal);
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
            if (line[0] == 'D' && fields.Length is 4 or 5)
            {
                if (!dictionary._entries.TryGetValue(fields[1], out var blocks))
                {
                    blocks = new List<(string, string[], string?)>(1);
                    dictionary._entries[fields[1]] = blocks;
                }
                blocks.Add((fields[2], fields[3].Split(''), fields.Length > 4 && fields[4].Length > 0 ? fields[4] : null));
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

        if (GrammarWords.Contains(form))
        {
            return null;
        }

        var senses = new List<WordSense>();
        var seen = new HashSet<(string, string)>();

        void Add(string headword, Func<string, bool> wanted)
        {
            if (!_entries.TryGetValue(headword, out var blocks))
            {
                return;
            }

            foreach (var (pos, definitions, example) in blocks)
            {
                if (wanted(pos) && seen.Add((headword, pos)))
                {
                    senses.Add(new WordSense(headword, NameOf(pos), definitions, example));
                }
            }
        }

        Add(form, _ => true);

        // The base words the page's word may be a form of, each with the parts
        // of speech an irregular form or a rule allows it, in first-found order.
        var bases = new List<(string Headword, List<string> Parts)>();
        void Candidate(string headword, string pos)
        {
            int at = bases.FindIndex(b => b.Headword == headword);
            if (at < 0)
            {
                bases.Add((headword, [pos]));
            }
            else if (!bases[at].Parts.Contains(pos))
            {
                bases[at].Parts.Add(pos);
            }
        }

        foreach (var (pos, rules) in new[] { ("n", NounRules), ("v", VerbRules), ("a", AdjectiveRules), ("r", Array.Empty<(string, string)>()) })
        {
            if (_irregular.TryGetValue((pos, form), out var irregular))
            {
                foreach (string b in irregular)
                {
                    Candidate(b, pos);
                }
            }

            foreach (var (suffix, ending) in rules)
            {
                if (form.Length <= suffix.Length || !form.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                // ⚠️ A NOUN OR ADJECTIVE RULE MUST LEAVE A REAL-SIZED WORD.
                // Otherwise "has", "was" and "its" become "ha", "wa" and "it",
                // which WordNet holds only as abbreviations. Verbs may keep two
                // letters, for "goes".
                string stem = form[..^suffix.Length];
                if ((stem + ending).Length < (pos == "v" ? 2 : 3))
                {
                    continue;
                }

                Candidate(stem + ending, pos);

                // stopped, running, bigger: the rule leaves "stopp", "runn",
                // "bigg", and the doubled letter has to go too.
                if (ending.Length == 0 && pos is "v" or "a" && stem.Length >= 3
                    && stem[^1] == stem[^2] && !IsVowel(stem[^1]))
                {
                    Candidate(stem[..^1], pos);
                }
            }
        }

        // ⚠️ IN THE DICTIONARY'S ORDER, NOT THE RULES'. The rules are tried
        // noun first, and adding senses as each rule matched put the noun "say"
        // (the chance to speak) above the verb for "says". A base word's parts
        // of speech go in the order the file lists them, most used first.
        foreach (var (headword, parts) in bases)
        {
            Add(headword, parts.Contains);
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
