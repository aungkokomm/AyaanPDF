using System.Text;

namespace PdfEditorApp.Viewport;

/// <summary>
/// Decides whether Windows Text Services is really delivering what is typed
/// into the page, from the characters that reach the window while it is
/// supposed to be.
/// </summary>
/// <remarks>
/// ⚠️ ON THE READER'S PC IT DELIVERED NOTHING. With UAC off every app runs
/// elevated, and there Text Services took the line and the focus and then never
/// sent a single update. The character route stands down while Text Services
/// hosts a line, or every letter would type twice, so English typed into the
/// page produced nothing at all. Burmese, which never goes through Text
/// Services, typed as before.
///
/// Where Text Services works it answers the first keystroke, a composing Hindi
/// keyboard included, so the characters held here are dropped unused and
/// nothing changes. Only when <see cref="SilentAfter"/> characters arrive with
/// no answer is it given up on, for the rest of the run.
/// </remarks>
public sealed class TypingWatch
{
    /// <summary>Characters that may arrive unanswered before Text Services is given up on.</summary>
    public const int SilentAfter = 3;

    private readonly StringBuilder _held = new();
    private bool _heard;

    /// <summary>Text Services has been caught swallowing keystrokes and is not offered the page again.</summary>
    public bool Silent { get; private set; }

    /// <summary>A line has started being edited: Text Services gets a fresh hearing.</summary>
    public void EditStarted()
    {
        _heard = false;
        _held.Clear();
    }

    /// <summary>Text Services delivered something, so it has the keystrokes.</summary>
    public void Delivered()
    {
        _heard = true;
        _held.Clear();
    }

    /// <summary>
    /// A character that reached the window while Text Services hosted the line.
    /// Null leaves it to Text Services. Text means Text Services is silent, and
    /// the caller inserts it: this character with the ones held before it.
    /// </summary>
    public string? Received(char c)
    {
        if (_heard) { return null; }

        _held.Append(c);
        if (_held.Length < SilentAfter) { return null; }

        Silent = true;
        string held = _held.ToString();
        _held.Clear();
        return held;
    }
}
