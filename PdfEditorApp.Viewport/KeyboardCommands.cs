namespace PdfEditorApp.Viewport;

/// <summary>A command the editor can be asked to run from the keyboard.</summary>
public enum EditorCommand
{
    None,
    Open,
    /// <summary>Write back over the open file. Falls back to Save As when there is none.</summary>
    Save,
    SaveAs,
    Undo,
    Redo,
    Group,
    Ungroup,
    ToggleRulers,
}

/// <summary>
/// Which command a Ctrl chord means.
///
/// This exists because these eight chords were declared ONLY as accelerators on
/// MenuFlyoutItems, whose accelerators are not live until the flyout is opened.
/// Keyboard undo had therefore never worked in this app, and nothing could tell:
/// the declaration looked right, and no test could reach it.
///
/// The mapping now lives somewhere a test can assert on it. That does not prove
/// the window routes keys here, but it does mean a chord that is supposed to
/// exist and does not is a failing test rather than a silent hole.
///
/// Keys are passed as raw virtual-key codes so this library stays free of WinUI
/// types; the caller casts its VirtualKey. Codes match the standard Windows
/// values, which are also the ASCII codes for letters and digits.
/// </summary>
public static class KeyboardCommands
{
    public const int KeyO = 0x4F;
    public const int KeyR = 0x52;
    public const int KeyS = 0x53;
    public const int KeyG = 0x47;
    public const int KeyY = 0x59;
    public const int KeyZ = 0x5A;

    /// <summary>
    /// The command for a chord, or <see cref="EditorCommand.None"/>.
    ///
    /// Returns None while a text field has focus: Ctrl+Z in a text box belongs
    /// to the text box, and stealing it would make typing unrecoverable.
    ///
    /// Only the chords that are plain "run this command" live here. Ctrl+C, X, V
    /// and F stay in the view because whether they count as handled depends on
    /// what is selected, and that is not a mapping.
    /// </summary>
    public static EditorCommand Resolve(int keyCode, bool ctrl, bool shift, bool textFocused)
    {
        if (!ctrl || textFocused) { return EditorCommand.None; }

        return keyCode switch
        {
            KeyO when !shift => EditorCommand.Open,
            // Ctrl+S saves, Ctrl+Shift+S saves a copy. Ctrl+S used to open the
            // Save As picker, which meant the commonest keystroke in any editor
            // could not save the file you already had open.
            KeyS => shift ? EditorCommand.SaveAs : EditorCommand.Save,
            KeyR when !shift => EditorCommand.ToggleRulers,
            KeyG => shift ? EditorCommand.Ungroup : EditorCommand.Group,
            KeyZ => shift ? EditorCommand.Redo : EditorCommand.Undo,
            KeyY when !shift => EditorCommand.Redo,
            _ => EditorCommand.None,
        };
    }
}
