using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The arithmetic of typing into a line through an ordinary text box, for a PC
/// where Windows Text Services does not answer the page.
/// </summary>
/// <remarks>
/// ⚠️ ON THE READER'S PC THE PAGE'S OWN TEXT DOCUMENT IS DEAD AND A TEXTBOX IS
/// NOT. With UAC off, the page's <c>CoreTextEditContext</c> is given focus and
/// then never hears from Text Services, so a Hindi Phonetic keyboard had nothing
/// to compose into: its keys arrived as plain English letters and its floating
/// list of suggestions never opened. The same keyboard types Devanagari into
/// Ayaan's Find box, an ordinary <c>TextBox</c>. So where the page's document is
/// silent, the line is typed through a hidden box of that kind, and every change
/// the box makes is carried into the line as one replaced range.
/// </remarks>
public static class TextSink
{
    /// <summary>
    /// The one stretch of <paramref name="before"/> that was rewritten to give
    /// <paramref name="after"/>, and what was written there.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE CARET SETTLES WHICH ONE WHEN MORE THAN ONE WOULD DO. Typing "a"
    /// at the start of "aa" gives "aaa", which comparing the text alone reads as
    /// an "a" added at the end. The text comes out the same, but the line's
    /// placement follows the range, so the edit is taken to start no later than
    /// where the caret was. An input method revising a word it is composing
    /// starts BEFORE the caret, which this allows.
    ///
    /// ⚠️ AND A SURROGATE PAIR IS NEVER CUT. A character outside the basic plane
    /// is two UTF-16 units, and a range that split one would write half a
    /// character into the line.
    /// </remarks>
    public static (int Start, int End, string Inserted) Edit(string before, string after, int caret)
    {
        int shorter = Math.Min(before.Length, after.Length);
        int prefix = 0;
        while (prefix < shorter && before[prefix] == after[prefix])
        {
            prefix++;
        }
        prefix = Math.Min(prefix, Math.Clamp(caret, 0, before.Length));
        if (prefix > 0 && char.IsHighSurrogate(before[prefix - 1]))
        {
            prefix--;
        }

        int suffix = 0;
        int room = shorter - prefix;
        while (suffix < room
               && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }
        if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix]))
        {
            suffix--;
        }

        return (prefix, before.Length - suffix,
            after.Substring(prefix, after.Length - prefix - suffix));
    }

    /// <summary>
    /// Whether a key pressed in the box is the page's to act on rather than the
    /// box's to type.
    /// </summary>
    /// <remarks>
    /// ⚠️ EVERYTHING THAT IS NOT TEXT GOES TO THE PAGE, so editing behaves
    /// exactly as it does without the box: Enter commits, Escape cancels, the
    /// arrows cross into the next line at its ends, Backspace takes a cluster,
    /// and every Ctrl shortcut is the page's. Left to the box, each of those
    /// would do the box's version of it to a copy of the line.
    ///
    /// ⚠️ BUT NOT CTRL AND ALT TOGETHER. That is AltGr, which TYPES: a
    /// keyboard's third-level characters are text.
    ///
    /// ⚠️ AND A KEY AN INPUT METHOD IS COMPOSING WITH NEVER GETS HERE. Text
    /// Services hands it to the keyboard before the window sees it, which is why
    /// Backspace inside a half-typed Hindi word revises the word rather than the
    /// line.
    /// </remarks>
    public static bool PageKey(int virtualKey, bool ctrl, bool alt)
    {
        if (ctrl && alt) { return false; }
        if (ctrl) { return true; }

        const int Back = 8, Tab = 9, Enter = 13, Escape = 27;
        const int PageUp = 33, Down = 40, Delete = 46, F1 = 112, F24 = 135;
        return virtualKey is Back or Tab or Enter or Escape or Delete
            or (>= PageUp and <= Down)
            or (>= F1 and <= F24);
    }
}
