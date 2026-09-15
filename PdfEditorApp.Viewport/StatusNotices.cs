using System;

namespace PdfEditorApp.Viewport;

/// <summary>How a status message reaches the reader, if it does at all.</summary>
public enum NoticeKind
{
    /// <summary>Kept quiet: a confirmation of something the page already shows.</summary>
    None,

    /// <summary>Said, because the reader has something to do or should know.</summary>
    Information,

    /// <summary>Said as a warning: something the reader asked for did not happen.</summary>
    Warning,
}

/// <summary>
/// Decides which status messages reach the notice bar.
/// </summary>
/// <remarks>
/// ⚠️ `Status` WAS WRITTEN IN 148 PLACES AND SHOWN IN NONE. Every refusal that
/// only set it ("Could not add that stamp.", "That link could not be removed.")
/// was a click that did nothing and said nothing. The bar shows those, and the
/// instructions a reader has to follow ("Click where the signature should go."),
/// and keeps quiet about confirmations of changes the page already shows: a
/// banner after every retyped line would be one more thing to dismiss.
///
/// ⚠️ AND NEVER TWICE. A click on text the app cannot edit is explained by the
/// label beside that text, and the same refusal is often written to `Status` as
/// well. A message raised in the same turn as such a label is dropped, whichever
/// of the two came first.
/// </remarks>
public sealed class StatusNotices
{
    private readonly object _gate = new();
    private string? _pending;
    private bool _labelled;

    /// <summary>A message written to the status.</summary>
    public void Status(string? message)
    {
        lock (_gate) { _pending = message; }
    }

    /// <summary>A label shown beside the text a click landed on.</summary>
    public void Label(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return; }
        lock (_gate) { _labelled = true; }
    }

    /// <summary>
    /// What the bar should say for everything written since the last flush, or
    /// null when it should say nothing. Starts the next turn clean either way.
    /// </summary>
    public (string Message, NoticeKind Kind)? Flush()
    {
        string? message;
        bool labelled;
        lock (_gate)
        {
            (message, labelled) = (_pending, _labelled);
            (_pending, _labelled) = (null, false);
        }

        if (labelled || message is null) { return null; }
        NoticeKind kind = Classify(message);
        return kind == NoticeKind.None ? null : (message.Trim(), kind);
    }

    /// <summary>
    /// Words that only a refusal uses. Checked before the instructions, because
    /// "Select it again" inside a refusal is still a refusal.
    /// </summary>
    private static readonly string[] Refusals =
    {
        "could not", "cannot", "can't", "isn't", "failed", "not supported",
        "not installed", "password protected", "too small", "has changed since",
        "offers no choices", "still saving", "not read back", "does not name",
        "would scramble", "is not removed", "more than one font",
        "would even out", "edit a word instead",
        "has no address", "will not open", "not one Ayaan can read",
    };

    /// <summary>How the app's instructions to the reader begin.</summary>
    private static readonly string[] Instructions =
    {
        "Click ", "Drag ", "Draw ", "Choose ", "Select ", "Nothing selected",
        "Full screen", "Recovered ",
    };

    /// <summary>How a message would be shown on its own.</summary>
    public static NoticeKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return NoticeKind.None; }
        string text = message.Trim();

        // ⚠️ WHAT WAS CLICKED, NOT SOMETHING THAT HAPPENED. A selection writes
        // its description here ("Line: “…”  •  …", "“…”  •  Calibri 12pt"), and
        // that description quotes the document's own text, which can contain any
        // word at all. The label beside the text is what explains a refusal.
        if (text.StartsWith("Line: ", StringComparison.Ordinal)
            || text.StartsWith("Word: ", StringComparison.Ordinal)
            || text.StartsWith('“'))
        {
            return NoticeKind.None;
        }

        foreach (string refusal in Refusals)
        {
            if (text.Contains(refusal, StringComparison.OrdinalIgnoreCase)) { return NoticeKind.Warning; }
        }
        foreach (string instruction in Instructions)
        {
            if (text.StartsWith(instruction, StringComparison.Ordinal)) { return NoticeKind.Information; }
        }
        return NoticeKind.None;
    }
}
