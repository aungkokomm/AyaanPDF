using System;
using System.Collections.Generic;
using System.Globalization;

namespace PdfEditorApp.Viewport;

/// <summary>
/// One line of the page's own text while it is being edited in place: what it
/// said, what it says now, where the caret is, and what is selected.
/// </summary>
/// <remarks>
/// ⚠️ NOTHING HERE DRAWS OR TYPES. It is the text and the caret and nothing
/// else, so the rules that decide what a keystroke means can be proved without
/// a window. The drawing reads <see cref="UnchangedPrefix"/> to know how much
/// of the page it must leave alone.
///
/// ⚠️ AND THE UNCHANGED PREFIX IS THE WHOLE POINT. Editing has to feel like
/// editing the page's own text, which means the page must not move or be
/// painted over while it is being edited. Everything before the first changed
/// character is still exactly what the page already draws, so it is left as
/// real page pixels and only the tail is ever redrawn. At the moment an edit
/// begins nothing has changed at all, so nothing covers the page and what the
/// reader sees is the document itself with a caret in it.
/// </remarks>
public sealed class LineEditBuffer
{
    public LineEditBuffer(string original, int caret)
    {
        Original = original ?? string.Empty;
        Text = Original;
        Caret = Clamp(caret);
        Anchor = Caret;
    }

    /// <summary>
    /// The offsets the PAGE can actually put a caret at, ascending.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE BUFFER IS TOLD, IT DOES NOT WORK THIS OUT. What may be stood
    /// between is a question about the SHAPING of the page's own type, and the
    /// answer differs by script and by font. Nothing here knows what a cluster
    /// is; it is handed the list and walks it, so Devanagari, Burmese and
    /// whatever comes next all move correctly without a line of script-specific
    /// code in the buffer.
    ///
    /// ⚠️ AND IT DESCRIBES THE TEXT THE PAGE DREW, so it is authority only as
    /// far as <see cref="UnchangedPrefix"/>. Past there the reader has typed and
    /// the page no longer draws it: the tail is laid out by the text engine,
    /// which snaps a caret to its own clusters, so text elements are the right
    /// answer there and the fall-through below is not a compromise.
    ///
    /// Empty means nobody knew, and the old behaviour is kept exactly.
    /// </remarks>
    public void SetPlaceableOffsets(IReadOnlyList<int>? offsets) =>
        _placeable = offsets ?? (IReadOnlyList<int>)Array.Empty<int>();

    private IReadOnlyList<int> _placeable = Array.Empty<int>();

    /// <summary>What the page draws today, unchanged for the life of the edit.</summary>
    public string Original { get; }

    /// <summary>What it says now.</summary>
    public string Text { get; private set; }

    /// <summary>Where the caret is, as an offset between characters.</summary>
    public int Caret { get; private set; }

    /// <summary>
    /// The other end of the selection.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE CARET IS THE MOVING END, THE ANCHOR IS THE FIXED ONE, and the two
    /// are equal when nothing is selected. Keeping them in that order is what
    /// makes shift-arrow extend the selection from where it started rather than
    /// flipping it, and it is why a selection knows which end to grow.
    /// </remarks>
    public int Anchor { get; private set; }

    public bool HasSelection => Anchor != Caret;

    public int SelectionStart => Math.Min(Anchor, Caret);

    public int SelectionEnd => Math.Max(Anchor, Caret);

    public int SelectionLength => SelectionEnd - SelectionStart;

    /// <summary>The selected text, empty when nothing is selected.</summary>
    public string SelectedText => Text[SelectionStart..SelectionEnd];

    public bool IsChanged => !string.Equals(Text, Original, StringComparison.Ordinal);

    /// <summary>
    /// How many characters at the start of the line are still exactly what the
    /// page already draws.
    /// </summary>
    /// <remarks>
    /// ⚠️ WHAT MUST NOT BE PAINTED OVER. The page's own glyphs are better than
    /// anything that could be drawn on top of them: they are the real type, at
    /// the real position, in the real font. Only from here to the end of the
    /// line does anything need to be covered and redrawn, so typing at the end
    /// of a line leaves almost all of it untouched, and an edit that has not
    /// changed anything yet covers nothing at all.
    /// </remarks>
    public int UnchangedPrefix
    {
        get
        {
            int n = Math.Min(Original.Length, Text.Length);
            int i = 0;
            while (i < n && Original[i] == Text[i]) { i++; }
            return i;
        }
    }

    // ---------------- what a keystroke means ----------------

    /// <summary>
    /// Types text in, replacing the selection when there is one.
    /// </summary>
    public void Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) { return; }

        DeleteSelection();
        Text = Text[..Caret] + s + Text[Caret..];
        Caret += s.Length;
        Anchor = Caret;
    }

    /// <summary>
    /// Replaces the characters from <paramref name="start"/> to
    /// <paramref name="end"/> with <paramref name="text"/>, leaving the caret
    /// after what was put in.
    /// </summary>
    /// <remarks>
    /// ⚠️ THIS IS WHAT AN INPUT METHOD NEEDS, AND ONLY IT. Windows Text
    /// Services does not type characters at a caret; it hands over a RANGE and
    /// what that range should now say, and it moves that range around while a
    /// word is being composed. Typing `kyon` on a Hindi phonetic keyboard is
    /// one range rewritten five times, not five separate letters, so an editor
    /// that can only insert at its own caret cannot host one.
    ///
    /// ⚠️ AND THE RANGE COMES FROM OUTSIDE, so it is clamped rather than
    /// trusted. A composition that is interrupted can name a range that no
    /// longer exists.
    /// </remarks>
    public void ReplaceRange(int start, int end, string text)
    {
        start = Clamp(start);
        end = Clamp(end);
        if (end < start) { (start, end) = (end, start); }

        Text = Text[..start] + text + Text[end..];
        Caret = start + (text?.Length ?? 0);
        Anchor = Caret;
    }

    /// <summary>
    /// Backspace: removes the selection, or the character before the caret.
    /// </summary>
    /// <remarks>
    /// ⚠️ ONE CHARACTER THE READER WOULD RECOGNISE, not one UTF-16 unit. A
    /// surrogate pair and a letter carrying combining marks are each one thing
    /// on screen, and deleting half of either leaves text that cannot be drawn.
    /// This user is writing Devanagari and Burmese, where that is the ordinary
    /// case rather than an edge one.
    /// </remarks>
    public void Backspace()
    {
        if (DeleteSelection()) { return; }
        if (Caret <= 0) { return; }

        int start = PreviousBoundary(Caret);
        Text = Text[..start] + Text[Caret..];
        Caret = start;
        Anchor = Caret;
    }

    /// <summary>
    /// Backspace that removes exactly one character (one code point), the way a
    /// Windows text box does.
    /// </summary>
    /// <remarks>
    /// ⚠️ WHAT KEYMAGIC NEEDS, AND WHY BURMESE CANNOT USE <see cref="Backspace"/>.
    /// KeyMagic reorders Burmese as it is typed by sending backspaces and then
    /// the corrected characters, and it counts on each backspace taking ONE
    /// character. The cluster backspace took a whole syllable, so its second
    /// backspace ate into text already on the line: the reader typed
    /// "မှောင်မိုက်နေသည် သို့သော်" after "ညတာသည်" and the page got
    /// "ညတာသှောင်နေသည်သော်".
    /// </remarks>
    public void BackspaceOneCodePoint()
    {
        if (DeleteSelection()) { return; }
        if (Caret <= 0) { return; }

        int start = Caret - 1;
        if (start > 0 && char.IsLowSurrogate(Text[start]) && char.IsHighSurrogate(Text[start - 1]))
        {
            start--;
        }
        Text = Text[..start] + Text[Caret..];
        Caret = start;
        Anchor = Caret;
    }

    /// <summary>
    /// Pasted text, reduced to something a LINE can hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ A LINE OF A PDF IS ONE LINE, and the clipboard rarely is. Whatever
    /// was copied may carry newlines, tabs and stray control characters, and
    /// putting those into a line would ask the writer to draw something the
    /// page has no room for. Each run of them becomes a single space, which
    /// keeps the words apart instead of running them together.
    ///
    /// ⚠️ AND THE RESULT IS TRIMMED, because copying a line almost always
    /// takes its newline with it, and nobody means to paste a trailing space.
    /// </remarks>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }

        var line = new System.Text.StringBuilder(text.Length);
        bool gap = false;
        foreach (char c in text)
        {
            if (c == '\t' || c == '\n' || c == '\r' || char.IsControl(c))
            {
                gap = true;
                continue;
            }
            if (gap && line.Length > 0) { line.Append(' '); }
            gap = false;
            line.Append(c);
        }

        return line.ToString().Trim();
    }

    /// <summary>Delete: removes the selection, or the character after the caret.</summary>
    public void Delete()
    {
        if (DeleteSelection()) { return; }
        if (Caret >= Text.Length) { return; }

        int end = NextBoundary(Caret);
        Text = Text[..Caret] + Text[end..];
        Anchor = Caret;
    }

    /// <summary>
    /// Removes the selection if there is one, and says whether it did.
    /// </summary>
    private bool DeleteSelection()
    {
        if (!HasSelection) { return false; }

        int start = SelectionStart, end = SelectionEnd;
        Text = Text[..start] + Text[end..];
        Caret = start;
        Anchor = start;
        return true;
    }

    // ---------------- moving, and selecting by moving ----------------
    //
    // ⚠️ EVERY MOVE TAKES extend, AND THAT IS THE WHOLE SELECTION MODEL for the
    // keyboard. Held shift keeps the anchor where it was and drags the caret;
    // released shift brings the anchor along. An arrow key pressed with a
    // selection up collapses it to the side it moved towards, which is what
    // every text editor does and what a reader will expect without thinking.

    public void MoveLeft(bool extend = false)
    {
        if (!extend && HasSelection) { Collapse(SelectionStart); return; }

        Caret = Caret <= 0 ? 0 : PreviousBoundary(Caret);
        if (!extend) { Anchor = Caret; }
    }

    public void MoveRight(bool extend = false)
    {
        if (!extend && HasSelection) { Collapse(SelectionEnd); return; }

        Caret = Caret >= Text.Length ? Text.Length : NextBoundary(Caret);
        if (!extend) { Anchor = Caret; }
    }

    public void MoveHome(bool extend = false)
    {
        Caret = 0;
        if (!extend) { Anchor = Caret; }
    }

    public void MoveEnd(bool extend = false)
    {
        Caret = Text.Length;
        if (!extend) { Anchor = Caret; }
    }

    private void Collapse(int at)
    {
        Caret = Clamp(at);
        Anchor = Caret;
    }

    /// <summary>
    /// Puts the caret at an offset chosen some other way, such as by a click
    /// resolved against the page's own glyphs.
    /// </summary>
    /// <param name="extend">
    /// True for a shift-click or a drag, which keeps the anchor and selects
    /// everything between it and here.
    /// </param>
    public void PlaceCaret(int offset, bool extend = false)
    {
        Caret = Clamp(offset);
        if (!extend) { Anchor = Caret; }
    }

    /// <summary>Selects the whole line.</summary>
    public void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
    }

    /// <summary>
    /// Selects the word around <paramref name="offset"/>, for a double-click.
    /// </summary>
    /// <remarks>
    /// A run of non-space characters, because that is what a reader means by a
    /// word when they double-click one. Landing on the space between two words
    /// selects that run of spaces, exactly as it does elsewhere.
    /// </remarks>
    public void SelectWordAt(int offset)
    {
        if (Text.Length == 0) { Collapse(0); return; }

        int at = Math.Clamp(offset, 0, Text.Length - 1);
        bool space = char.IsWhiteSpace(Text[at]);

        int start = at;
        while (start > 0 && char.IsWhiteSpace(Text[start - 1]) == space) { start--; }

        int end = at;
        while (end < Text.Length && char.IsWhiteSpace(Text[end]) == space) { end++; }

        Anchor = start;
        Caret = end;
    }

    // ---------------- character boundaries ----------------

    private int Clamp(int offset) => Math.Clamp(offset, 0, Text.Length);

    /// <summary>The start of the character ending at <paramref name="at"/>.</summary>
    private int PreviousBoundary(int at)
    {
        if (at <= 0) { return 0; }

        // The page's own cluster boundaries first, while they still describe
        // what is on the page. See SetPlaceableOffsets.
        if (_placeable.Count > 0 && at <= UnchangedPrefix)
        {
            int best = -1;
            for (int i = 0; i < _placeable.Count; i++)
            {
                if (_placeable[i] >= at) { break; }
                best = _placeable[i];
            }
            if (best >= 0) { return best; }
        }

        // StringInfo walks text elements, which is what a reader calls a
        // character: a base letter plus whatever combines onto it.
        var e = StringInfo.GetTextElementEnumerator(Text);
        int start = 0;
        while (e.MoveNext())
        {
            int elementStart = e.ElementIndex;
            int elementEnd = elementStart + ((string)e.Current).Length;
            if (elementEnd >= at) { return elementStart < at ? elementStart : start; }
            start = elementStart;
        }
        return start;
    }

    /// <summary>The end of the character starting at <paramref name="at"/>.</summary>
    private int NextBoundary(int at)
    {
        if (at >= Text.Length) { return Text.Length; }

        if (_placeable.Count > 0 && at < UnchangedPrefix)
        {
            for (int i = 0; i < _placeable.Count; i++)
            {
                if (_placeable[i] <= at) { continue; }

                // Only while the page still draws it. A cluster straddling the
                // end of the unchanged prefix belongs to text the reader has
                // already changed, so its far edge is not a position the page
                // has any more.
                return _placeable[i] <= UnchangedPrefix
                    ? _placeable[i]
                    : NextTextElement(at);
            }
        }

        return NextTextElement(at);
    }

    private int NextTextElement(int at)
    {
        var e = StringInfo.GetTextElementEnumerator(Text);
        while (e.MoveNext())
        {
            int elementStart = e.ElementIndex;
            int elementEnd = elementStart + ((string)e.Current).Length;
            if (elementStart >= at) { return elementEnd; }
            if (elementEnd > at) { return elementEnd; }
        }
        return Text.Length;
    }
}
