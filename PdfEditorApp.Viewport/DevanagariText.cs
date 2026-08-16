using System;
using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Puts Devanagari extracted in DRAWING order back into reading order.
///
/// Some PDFs record their text in the order the glyphs are painted rather than
/// the order they are read. In Devanagari the short-i sign is painted to the
/// LEFT of the consonant it belongs to, so "पिपासा" comes out of such a file as
/// "िपपासा": the sign first, its consonant second. Every reader shows that
/// document's copied text the same way, and a bookmark made from it reads as
/// nonsense.
///
/// Measured on a real book before this was written: every short-i sign in the
/// extracted text was preceded by a SPACE and never by a consonant, which is
/// the fingerprint. A correctly encoded document cannot look like that, because
/// a vowel sign has nothing to attach to at the start of a word.
///
/// ⚠️ This does NOT rescue text whose font maps its conjuncts into the private
/// use area; those characters are not in the file to recover. It fixes the
/// order of what IS there.
/// </summary>
public static class DevanagariText
{
    private const char ShortI = 'ि';   // DEVANAGARI VOWEL SIGN I
    private const char Halant = '्';   // VIRAMA, which joins a conjunct
    private const char Nukta = '़';

    /// <summary>
    /// The text in reading order, or the same string when nothing needed
    /// moving.
    ///
    /// Safe to run on text that is already correct, and safe to run twice: once
    /// a sign sits after its consonant it no longer looks misplaced.
    /// </summary>
    public static string ToLogicalOrder(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(ShortI) < 0)
        {
            // The overwhelmingly common case, including every document not
            // written in an Indic script.
            return text ?? string.Empty;
        }

        StringBuilder? built = null;
        int copied = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ShortI || !IsStranded(text, i))
            {
                continue;
            }

            int after = EndOfCluster(text, i + 1);
            if (after < 0)
            {
                // A sign with no consonant behind it to belong to. Leaving it
                // alone is the only honest answer; moving it somewhere would
                // invent a word.
                continue;
            }

            built ??= new StringBuilder(text.Length);
            built.Append(text, copied, i - copied);      // up to the sign
            built.Append(text, i + 1, after - (i + 1));  // the consonant cluster
            built.Append(ShortI);                        // then the sign
            copied = after;
        }

        if (built is null)
        {
            return text;
        }

        built.Append(text, copied, text.Length - copied);
        return built.ToString();
    }

    /// <summary>
    /// Whether this sign is sitting where a correctly encoded document could
    /// never put it: at the start of a cluster, with nothing before it that it
    /// could attach to.
    ///
    /// This is the whole safety of the repair. In correct text "किताब" also has
    /// the sign followed by a consonant, so swapping on that alone would break
    /// good documents; there the sign is PRECEDED by क, and this leaves it be.
    /// </summary>
    private static bool IsStranded(string text, int at) =>
        at == 0 || !AttachesToCluster(text[at - 1]);

    /// <summary>
    /// One past the end of the consonant cluster starting at
    /// <paramref name="from"/>, or -1 if there is no consonant there.
    ///
    /// A cluster is a consonant, then any number of virama-joined consonants,
    /// so a sign painted before "स्थ" lands after the whole of it rather than
    /// inside it.
    /// </summary>
    private static int EndOfCluster(string text, int from)
    {
        if (from >= text.Length || !IsConsonant(text[from]))
        {
            return -1;
        }

        int at = from + 1;
        if (at < text.Length && text[at] == Nukta)
        {
            at++;
        }

        while (at + 1 < text.Length && text[at] == Halant && IsConsonant(text[at + 1]))
        {
            at += 2;
            if (at < text.Length && text[at] == Nukta)
            {
                at++;
            }
        }

        return at;
    }

    private static bool IsConsonant(char c) =>
        (c >= 'क' && c <= 'ह')   // क .. ह
        || (c >= 'क़' && c <= 'य़') // the nukta forms, क़ .. य़
        || (c >= 'ॸ' && c <= 'ॿ');

    /// <summary>
    /// Whether a character is part of a syllable rather than a break between
    /// them. Danda and digits are deliberately NOT: a sign right after a full
    /// stop is as stranded as one after a space.
    /// </summary>
    private static bool AttachesToCluster(char c) =>
        IsConsonant(c)
        || c == Halant
        || c == Nukta
        || (c >= 'ा' && c <= 'ौ')  // the vowel signs
        || (c >= 'ऀ' && c <= 'ः')  // candrabindu, anusvara, visarga
        || (c >= '॑' && c <= 'ॗ')  // accents and further signs
        || (c >= 'ॢ' && c <= 'ॣ');

    /// <summary>
    /// Moves a combining mark back across the space it was painted after.
    ///
    /// The same book showed "नही ंजाता" where "नहीं जाता" was meant: the
    /// anusvara was recorded after the word space rather than before it. Only
    /// the mark and the space swap, so the space between the words survives.
    /// </summary>
    public static string JoinStrandedMarks(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        StringBuilder? built = null;

        for (int i = 1; i < text.Length; i++)
        {
            if (text[i - 1] != ' ' || !IsCombiningMark(text[i]) || i - 2 < 0)
            {
                continue;
            }

            if (!AttachesToCluster(text[i - 2]))
            {
                // Nothing before the space for the mark to belong to.
                continue;
            }

            built ??= new StringBuilder(text);
            built[i - 1] = text[i];
            built[i] = ' ';
        }

        return built?.ToString() ?? text;
    }

    private static bool IsCombiningMark(char c) =>
        (c >= 'ऀ' && c <= 'ः') || (c >= 'ा' && c <= '्');

    /// <summary>Both repairs, in the order they have to run.</summary>
    public static string Repair(string? text) =>
        JoinStrandedMarks(ToLogicalOrder(text));
}
