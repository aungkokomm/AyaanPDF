namespace PdfEditorApp.Viewport;

/// <summary>
/// Decides whether a selection is one English word Define can look up.
/// </summary>
/// <remarks>
/// ⚠️ ENGLISH ONLY, AND ONE WORD. The dictionary is English, and Ayaan's
/// readers also select Hindi and Burmese. Anything outside the ASCII letters
/// is refused rather than guessed at, so a Devanagari or Myanmar selection
/// simply does not offer Define. Punctuation hugging the word (a full stop,
/// quotes, brackets) is trimmed, because selecting a word at the end of a
/// sentence usually takes its full stop with it.
/// </remarks>
public static class EnglishWord
{
    /// <summary>Longer than any dictionary word; a longer run is not one.</summary>
    public const int MaxLength = 40;

    private static readonly char[] Surrounding =
    [
        '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '<', '>',
        '*', '_', '-', '–', '—', '‘', '’', '“', '”', '…',
    ];

    /// <summary>
    /// The word in <paramref name="selected"/>, or false when the selection is
    /// not exactly one English word.
    /// </summary>
    /// <remarks>
    /// Inside the word, an apostrophe or a hyphen is allowed between two
    /// letters (don't, well-being). A curly apostrophe is read as a straight
    /// one, which is how the dictionary spells it.
    /// </remarks>
    public static bool TryNormalize(string? selected, out string word)
    {
        word = string.Empty;
        if (string.IsNullOrWhiteSpace(selected))
        {
            return false;
        }

        string candidate = selected.Trim().Trim(Surrounding).Replace('’', '\'');
        if (candidate.Length == 0 || candidate.Length > MaxLength)
        {
            return false;
        }

        for (int i = 0; i < candidate.Length; i++)
        {
            char c = candidate[i];
            if (IsAsciiLetter(c))
            {
                continue;
            }

            bool joiner = (c == '\'' || c == '-')
                && i > 0 && i < candidate.Length - 1
                && IsAsciiLetter(candidate[i - 1]) && IsAsciiLetter(candidate[i + 1]);
            if (!joiner)
            {
                return false;
            }
        }

        word = candidate;
        return true;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
