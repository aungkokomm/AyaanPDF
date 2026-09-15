using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Settles the one Myanmar confusion no recogniser can: the digit zero (၀) and
/// the letter wa (ဝ) are the same circle in almost every font.
/// </summary>
/// <remarks>
/// Decided by what the circle stands next to. Beside another digit it is a
/// zero ("၁ဝဝဝ" is a thousand); beside a letter or a vowel sign it is wa
/// ("၀င်" is "ဝင်"). A circle standing alone is left as it was read, since
/// either is possible and guessing would change correct text.
/// </remarks>
public static class MyanmarDigits
{
    private const char Zero = '၀';
    private const char Wa = 'ဝ';

    public static string Fix(string text)
    {
        if (text.IndexOf(Zero) < 0 && text.IndexOf(Wa) < 0)
        {
            return text;
        }

        var result = new StringBuilder(text);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsCircle(text[i]))
            {
                i++;
                continue;
            }

            // A run of circles is decided together: "ဝဝဝ" after a 1 is all zeros.
            int start = i;
            while (i < text.Length && IsCircle(text[i])) { i++; }
            char before = start > 0 ? text[start - 1] : ' ';
            char after = i < text.Length ? text[i] : ' ';

            char? settled = null;
            if (IsOtherDigit(before) || IsOtherDigit(after))
            {
                settled = Zero;
            }
            else if (IsLetterOrSign(before) || IsLetterOrSign(after))
            {
                settled = Wa;
            }

            if (settled is char c)
            {
                for (int k = start; k < i; k++) { result[k] = c; }
            }
        }

        return result.ToString();
    }

    private static bool IsCircle(char c) => c == Zero || c == Wa;

    private static bool IsOtherDigit(char c) => c >= '၁' && c <= '၉';

    /// <summary>A consonant or independent vowel (U+1000..U+102A, wa itself
    /// excluded) or a dependent sign, medial or asat (U+102B..U+103E).</summary>
    private static bool IsLetterOrSign(char c) =>
        (c >= 'က' && c <= 'ှ' && c != Wa) || (c >= 'ၐ' && c <= '႟' && (c < '႐' || c > '႙'));
}
