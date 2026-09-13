namespace PdfEditorApp.Viewport;

/// <summary>
/// Which way typed text reaches a line being edited in place.
/// </summary>
/// <remarks>
/// ⚠️ A BURMESE LINE IS NEVER HANDED TO TEXT SERVICES. The page's text document
/// (3.41.0) exists so Hindi Phonetic, which COMPOSES, can type into the page.
/// While it is active the character path stands down, or plain typing arrives
/// twice. But the reader's Burmese keyboard, KeyMagic, does not compose: it
/// INJECTS finished characters (diag.log shows them as key=255), and those never
/// reach Text Services. So from 3.41.0 every Burmese letter typed into the page
/// was dropped while KeyMagic's own backspaces still deleted: the reader's log
/// had the rest of the line sliding LEFT with every keystroke and never right.
/// Burmese editing had worked on 2026-09-04 and was broken by Hindi work.
/// </remarks>
public static class TypingRoute
{
    /// <summary>
    /// Whether text services should host input for a line that says
    /// <paramref name="line"/> when its edit begins.
    /// </summary>
    public static bool HostsTextServices(string? line) => !IsBurmese(line);

    /// <summary>
    /// Whether a line has Burmese in it, and so is typed with KeyMagic: through
    /// the character path, and with a backspace that takes one character.
    /// </summary>
    public static bool IsBurmese(string? line)
    {
        if (line is null) { return false; }
        foreach (char c in line)
        {
            if (IsBurmese(c)) { return true; }
        }
        return false;
    }

    /// <summary>Myanmar, Myanmar Extended-A and Extended-B.</summary>
    private static bool IsBurmese(char c) =>
        c is (>= 'က' and <= '႟') or (>= 'ꩠ' and <= 'ꩿ') or (>= 'ꧠ' and <= '꧿');
}
