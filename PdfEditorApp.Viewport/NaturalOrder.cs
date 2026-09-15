using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Compares file names the way a person reads them: "Chapter 2" before
/// "Chapter 10", case ignored.
/// </summary>
/// <remarks>
/// An ordinal sort puts "Chapter 10" before "Chapter 2", which for thirty
/// chapter files dropped into Merge files is a scrambled book.
/// </remarks>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                // Compared as numbers of any length, without parsing them:
                // leading zeros off, then the longer number is the larger.
                string a = x[startX..i].TrimStart('0');
                string b = y[startY..j].TrimStart('0');
                if (a.Length != b.Length)
                {
                    return a.Length.CompareTo(b.Length);
                }

                int digits = string.CompareOrdinal(a, b);
                if (digits != 0)
                {
                    return digits;
                }

                continue;
            }

            int letters = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (letters != 0)
            {
                return letters;
            }

            i++;
            j++;
        }

        int rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
