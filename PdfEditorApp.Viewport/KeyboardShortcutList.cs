namespace PdfEditorApp.Viewport;

/// <summary>One key press: a virtual-key code and its modifiers.</summary>
public readonly record struct KeyChord(int Code, bool Ctrl = false, bool Shift = false, bool Alt = false);

/// <summary>
/// A line of Help > Keyboard shortcuts: the keys as a reader types them, and
/// what they do.
/// </summary>
/// <param name="Keys">The keys to press together, each one a keycap: ["Ctrl", "S"]. Alternatives are separate rows.</param>
/// <param name="Chords">
/// The chords behind the row, so a test can press them. Empty for a row that
/// is not one chord, such as the arrow keys or a mouse gesture.
/// </param>
public sealed record KeyboardShortcut(IReadOnlyList<string> Keys, string What, IReadOnlyList<KeyChord> Chords);

/// <summary>A heading in the shortcuts list and the rows under it.</summary>
public sealed record KeyboardShortcutGroup(string Title, IReadOnlyList<KeyboardShortcut> Shortcuts);

/// <summary>
/// Everything the keyboard does, for Help > Keyboard shortcuts.
/// </summary>
/// <remarks>
/// Written out by hand, because the keys are handled in several places (the
/// chord resolver, the window's key handler, full screen, the tool catalog),
/// and held to them by KeyboardShortcutListTests: every chord listed does
/// something, and every chord the resolver knows is listed. The tool letters
/// are read from <see cref="ToolCatalog"/> itself, so they cannot drift.
/// </remarks>
public static class KeyboardShortcutList
{
    private const int KeyA = 0x41;
    private const int KeyC = 0x43;
    private const int KeyF = 0x46;
    private const int KeyQ = 0x51;
    private const int KeyV = 0x56;
    private const int KeyX = 0x58;
    private const int Key0 = 0x30;
    private const int Key1 = 0x31;
    private const int Key2 = 0x32;
    private const int KeyF4 = 0x73;
    private const int KeyF6 = 0x75;
    private const int KeyF11 = 0x7A;
    private const int KeyEscape = 0x1B;
    private const int KeyDelete = 0x2E;
    private const int KeyBack = 0x08;
    private const int KeyHome = 0x24;
    private const int KeyEnd = 0x23;
    private const int KeyPageUp = 0x21;
    private const int KeyPageDown = 0x22;

    private static KeyboardShortcut Row(string what, string keys, params KeyChord[] chords) =>
        new(keys.Split('+'), what, chords);

    private static KeyChord Ctrl(int code, bool shift = false) => new(code, Ctrl: true, Shift: shift);

    public static IReadOnlyList<KeyboardShortcutGroup> Groups { get; } =
    [
        new("File",
        [
            Row("New document", "Ctrl+N", Ctrl(KeyboardCommands.KeyN)),
            Row("Open", "Ctrl+O", Ctrl(KeyboardCommands.KeyO)),
            Row("Save", "Ctrl+S", Ctrl(KeyboardCommands.KeyS)),
            Row("Save as", "Ctrl+Shift+S", Ctrl(KeyboardCommands.KeyS, shift: true)),
            Row("Print", "Ctrl+P", Ctrl(KeyboardCommands.KeyP)),
            Row("Document properties", "Alt+Enter", new KeyChord(KeyboardCommands.KeyEnter, Alt: true)),
            Row("Close the document", "Ctrl+W", Ctrl(KeyboardCommands.KeyW)),
            Row("Next tab", "Ctrl+Tab", Ctrl(KeyboardCommands.KeyTab)),
            Row("Previous tab", "Ctrl+Shift+Tab", Ctrl(KeyboardCommands.KeyTab, shift: true)),
            Row("Quit", "Ctrl+Q", Ctrl(KeyQ)),
        ]),
        new("Edit",
        [
            Row("Undo", "Ctrl+Z", Ctrl(KeyboardCommands.KeyZ)),
            Row("Redo", "Ctrl+Y", Ctrl(KeyboardCommands.KeyY)),
            Row("Redo", "Ctrl+Shift+Z", Ctrl(KeyboardCommands.KeyZ, shift: true)),
            Row("Cut", "Ctrl+X", Ctrl(KeyX)),
            Row("Copy", "Ctrl+C", Ctrl(KeyC)),
            Row("Paste", "Ctrl+V", Ctrl(KeyV)),
            Row("Duplicate", "Ctrl+D", Ctrl(KeyboardCommands.KeyD)),
            Row("Select everything on the page", "Ctrl+A", Ctrl(KeyA)),
            Row("Delete the selection", "Delete", new KeyChord(KeyDelete)),
            Row("Delete the selection", "Backspace", new KeyChord(KeyBack)),
            Row("Move the selection a little", "Arrow keys"),
            Row("Move the selection further", "Shift+Arrow keys"),
            Row("Clear the selection, or stop what is under way", "Esc", new KeyChord(KeyEscape)),
            Row("Group", "Ctrl+G", Ctrl(KeyboardCommands.KeyG)),
            Row("Ungroup", "Ctrl+Shift+G", Ctrl(KeyboardCommands.KeyG, shift: true)),
            Row("Bring forward", "Ctrl+]", Ctrl(KeyboardCommands.KeyCloseBracket)),
            Row("Bring to front", "Ctrl+Shift+]", Ctrl(KeyboardCommands.KeyCloseBracket, shift: true)),
            Row("Send backward", "Ctrl+[", Ctrl(KeyboardCommands.KeyOpenBracket)),
            Row("Send to back", "Ctrl+Shift+[", Ctrl(KeyboardCommands.KeyOpenBracket, shift: true)),
        ]),
        new("Typing in the page",
        [
            Row("Move the caret", "Arrow keys"),
            Row("Select while moving", "Shift+Arrow keys"),
            Row("Start of the line", "Home"),
            Row("End of the line", "End"),
            Row("Select all of the text", "Ctrl+A"),
            Row("Finish the edit", "Enter"),
            Row("Cancel the edit", "Esc"),
        ]),
        new("Find",
        [
            Row("Find", "Ctrl+F", Ctrl(KeyF)),
            Row("Next match", "F3", new KeyChord(KeyboardCommands.KeyF3)),
            Row("Previous match", "Shift+F3", new KeyChord(KeyboardCommands.KeyF3, Shift: true)),
        ]),
        new("Pages and view",
        [
            Row("Next page", "Page Down", new KeyChord(KeyPageDown)),
            Row("Previous page", "Page Up", new KeyChord(KeyPageUp)),
            Row("First page", "Home", new KeyChord(KeyHome)),
            Row("Last page", "End", new KeyChord(KeyEnd)),
            Row("Go to a page number", "Ctrl+Shift+N", Ctrl(KeyboardCommands.KeyN, shift: true)),
            Row("Back, after following a link", "Alt+Left", new KeyChord(KeyboardCommands.KeyLeft, Alt: true)),
            Row("Forward again", "Alt+Right", new KeyChord(KeyboardCommands.KeyRight, Alt: true)),
            Row("Bookmark this page", "Ctrl+B", Ctrl(KeyboardCommands.KeyB)),
            Row("Scroll, when nothing is selected", "Arrow keys"),
            Row("Hold to pan with the hand", "Space"),
            Row("Zoom", "Ctrl+Mouse wheel"),
            Row("Fit the page", "Ctrl+0", Ctrl(Key0)),
            Row("Actual size", "Ctrl+1", Ctrl(Key1)),
            Row("Fit the width", "Ctrl+2", Ctrl(Key2)),
            Row("Turn the view clockwise", "Ctrl+Shift+Plus",
                Ctrl(KeyboardCommands.KeyOemPlus, shift: true), Ctrl(KeyboardCommands.KeyAdd, shift: true)),
            Row("Turn the view anticlockwise", "Ctrl+Shift+Minus",
                Ctrl(KeyboardCommands.KeyOemMinus, shift: true), Ctrl(KeyboardCommands.KeySubtract, shift: true)),
            Row("Show or hide the rulers", "Ctrl+R", Ctrl(KeyboardCommands.KeyR)),
            Row("Page thumbnails", "F4", new KeyChord(KeyF4)),
            Row("Bookmarks", "F6", new KeyChord(KeyF6)),
            Row("Full screen", "F11", new KeyChord(KeyF11)),
            Row("Leave full screen", "Esc"),
        ]),
        new("Tools", ToolRows()),
        new("Help",
        [
            Row("Keyboard shortcuts", "F1", new KeyChord(KeyboardCommands.KeyF1)),
        ]),
    ];

    /// <summary>The tool letters, from the catalog the key handler reads them from.</summary>
    private static List<KeyboardShortcut> ToolRows()
    {
        var editOnly = ToolCatalog.All.Where(t => !ToolCatalog.Offers(AppMode.View, t.Mode)).ToHashSet();
        return ToolCatalog.All
            .Select(t => new KeyboardShortcut(
                [t.Shortcut.ToString()],
                editOnly.Contains(t) ? $"{t.Name} (in Edit)" : t.Name,
                [new KeyChord(t.Shortcut)]))
            .ToList();
    }
}
