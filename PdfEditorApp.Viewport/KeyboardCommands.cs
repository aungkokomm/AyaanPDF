namespace PdfEditorApp.Viewport;

/// <summary>A command the editor can be asked to run from the keyboard.</summary>
public enum EditorCommand
{
    None,
    Open,
    /// <summary>Write back over the open file. Falls back to Save As when there is none.</summary>
    Save,
    SaveAs,
    Print,
    Undo,
    Redo,
    Group,
    Ungroup,
    ToggleRulers,
    /// <summary>Turns the VIEW a quarter clockwise. Does not touch the document.</summary>
    RotateViewClockwise,
    RotateViewCounterClockwise,
    New,
    CloseDocument,
    NextTab,
    PreviousTab,
    FindNext,
    FindPrevious,
    GoToPage,
    BringForward,
    SendBackward,
    BringToFront,
    SendToBack,
    Duplicate,
    /// <summary>Back to where the reader was before the last jump.</summary>
    NavigateBack,
    NavigateForward,
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
    public const int KeyP = 0x50;
    public const int KeyZ = 0x5A;

    // Plus and minus, from the main row and the numeric keypad. Both, because
    // which one a laptop sends is not something the user should have to know.
    public const int KeyOemPlus = 0xBB;
    public const int KeyOemMinus = 0xBD;
    public const int KeyAdd = 0x6B;
    public const int KeySubtract = 0x6D;

    public const int KeyN = 0x4E;
    public const int KeyW = 0x57;
    public const int KeyTab = 0x09;
    public const int KeyF3 = 0x72;
    public const int KeyD = 0x44;

    // The bracket keys, as Windows names them: OEM_4 is '[' and OEM_6 is ']'
    // on a US layout. Layout-dependent, and deliberately so: these are
    // positional chords, and every drawing application binds the same two
    // physical keys.
    public const int KeyOpenBracket = 0xDB;
    public const int KeyCloseBracket = 0xDD;

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
    public const int KeyLeft = 0x25;
    public const int KeyRight = 0x27;

    public static EditorCommand Resolve(int keyCode, bool ctrl, bool shift, bool textFocused) =>
        Resolve(keyCode, ctrl, shift, textFocused, alt: false);

    /// <summary>
    /// As above, with Alt.
    ///
    /// A separate overload rather than a fifth argument on the old one, so the
    /// dozens of existing call sites and tests keep saying what they mean. Only
    /// the back and forward chords use it.
    /// </summary>
    public static EditorCommand Resolve(int keyCode, bool ctrl, bool shift, bool textFocused, bool alt)
    {
        // Alt+Left and Alt+Right, which is back and forward in every browser
        // and in Acrobat. Checked FIRST because the arrows are otherwise a
        // nudge or a scroll, and before the text-focus guard for the same
        // reason as F3: they type nothing, and there is no field that wants
        // them with Alt held.
        if (alt && !ctrl)
        {
            switch (keyCode)
            {
                case KeyLeft: return EditorCommand.NavigateBack;
                case KeyRight: return EditorCommand.NavigateForward;
            }
        }

        // Alt is not a modifier any other chord here takes, so a chord pressed
        // WITH it held is not that chord. Without this, Alt+S would save.
        if (alt)
        {
            return EditorCommand.None;
        }

        return ResolveCore(keyCode, ctrl, shift, textFocused);
    }

    private static EditorCommand ResolveCore(int keyCode, bool ctrl, bool shift, bool textFocused)
    {
        // F3 is answered BEFORE the text-focus guard, and deliberately.
        //
        // The guard exists because Ctrl+Z in a text box belongs to the text
        // box. F3 belongs to nothing: it types no character and there is no
        // field in this app that wants it. More to the point, the field it is
        // most likely to be pressed in is the FIND box, where "find the next
        // one" is exactly what is meant, and refusing it there would make the
        // key useless in the one place it matters most.
        if (keyCode == KeyF3)
        {
            return shift ? EditorCommand.FindPrevious : EditorCommand.FindNext;
        }

        if (!ctrl || textFocused) { return EditorCommand.None; }

        return keyCode switch
        {
            KeyO when !shift => EditorCommand.Open,
            // Ctrl+S saves, Ctrl+Shift+S saves a copy. Ctrl+S used to open the
            // Save As picker, which meant the commonest keystroke in any editor
            // could not save the file you already had open.
            KeyS => shift ? EditorCommand.SaveAs : EditorCommand.Save,
            KeyP when !shift => EditorCommand.Print,
            KeyR when !shift => EditorCommand.ToggleRulers,
            KeyG => shift ? EditorCommand.Ungroup : EditorCommand.Group,
            KeyZ => shift ? EditorCommand.Redo : EditorCommand.Undo,
            KeyY when !shift => EditorCommand.Redo,

            // Shift+Ctrl+plus and Shift+Ctrl+minus, which is what every other
            // reader uses to turn the view. SHIFT IS REQUIRED: Ctrl+plus and
            // Ctrl+minus are zoom everywhere, and claiming them unshifted would
            // turn the page when the reader meant to make it bigger.
            KeyOemPlus or KeyAdd when shift => EditorCommand.RotateViewClockwise,
            KeyOemMinus or KeySubtract when shift => EditorCommand.RotateViewCounterClockwise,

            // Ctrl+N new, Ctrl+Shift+N go to page. Not Ctrl+G, which every
            // other reader uses for it, because Ctrl+G is Group here and this
            // is an editor first. Acrobat makes the same swap.
            KeyN => shift ? EditorCommand.GoToPage : EditorCommand.New,

            KeyW when !shift => EditorCommand.CloseDocument,

            // Ctrl+Tab only. PLAIN Tab must stay the focus-traversal key: it
            // was bound once before and keyboard users could not move focus
            // anywhere in the app.
            KeyTab => shift ? EditorCommand.PreviousTab : EditorCommand.NextTab,

            // Z-order, on the brackets, which is where Illustrator, InDesign
            // and Photoshop all put it. Shift takes it all the way.
            KeyCloseBracket => shift ? EditorCommand.BringToFront : EditorCommand.BringForward,
            KeyOpenBracket => shift ? EditorCommand.SendToBack : EditorCommand.SendBackward,

            // Ctrl+D duplicates. Safe here specifically because it does NOT
            // touch the clipboard, which is why a Duplicate entry was kept off
            // the context menu: copy-paste would have been clobbered by it.
            KeyD when !shift => EditorCommand.Duplicate,

            _ => EditorCommand.None,
        };
    }
}
