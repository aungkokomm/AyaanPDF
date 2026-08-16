using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>What a row of the right-click menu asks the editor to do.</summary>
public enum ContextCommand
{
    /// <summary>A divider. Carries no action; the view draws a line.</summary>
    Separator,

    /// <summary>Reopen a text box's words for editing, which is otherwise only
    /// reachable by double-clicking it and is nowhere written down.</summary>
    EditText,

    Cut,
    Copy,
    Paste,
    Delete,

    BringToFront,
    BringForward,
    SendBackward,
    SendToBack,

    Group,
    Ungroup,

    SelectAllOnPage,
    RotatePage,
}

/// <summary>
/// One row. <paramref name="Accelerator"/> is display text only: the key is
/// handled by the window, not by this menu, so an entry here is a CLAIM that a
/// chord exists and has to match what the key handler really implements.
/// </summary>
public readonly record struct ContextMenuItem(
    ContextCommand Command,
    string Label,
    bool Enabled = true,
    string Accelerator = "")
{
    public bool IsSeparator => Command == ContextCommand.Separator;
}

/// <summary>
/// Everything the menu needs to know about where the user right-clicked.
///
/// Named properties rather than a positional record: seven flags in a row is
/// an argument list nobody can read at the call site, and getting two of them
/// the wrong way round produces a menu that is subtly wrong rather than one
/// that fails to compile.
/// </summary>
public readonly record struct ContextTarget
{
    /// <summary>False when no document is open, which is the one case with no
    /// menu at all.</summary>
    public bool DocumentOpen { get; init; }

    /// <summary>Whether the click landed on an object AND that object is now
    /// part of the selection. The view guarantees the second half: a right
    /// click selects what it lands on before asking for a menu, the way every
    /// editor does, so that the commands below have a target.</summary>
    public bool OnObject { get; init; }

    /// <summary>How many objects are selected, anchor included.</summary>
    public int SelectionCount { get; init; }

    /// <summary>Whether the clicked object belongs to a group.</summary>
    public bool CanUngroup { get; init; }

    /// <summary>Whether the clicked object is one of our text boxes, the only
    /// kind with words to reopen.</summary>
    public bool IsTextBox { get; init; }

    /// <summary>Whether a previous Copy or Cut left anything to paste.</summary>
    public bool ClipboardHasContent { get; init; }
}

/// <summary>
/// The right-click menu, as data.
///
/// Ayaan grew a lot of commands that are only reachable from a floating toolbar
/// that appears after a selection, or from a chord that was never announced.
/// Cut, Copy, the four z-order commands and Group are all in that position. A
/// context menu is where a user looks for them, so the point of this menu is
/// less that the commands run and more that they can be FOUND.
///
/// Pure data, in the viewport library, for the same reason the tool rail and
/// the property bar are: which rows appear and which are greyed is a set of
/// rules with real edge cases (a divider left stranded at the top, Group
/// offered on a single object, a shortcut advertised that the app does not
/// listen for), and none of them can be asserted once they are spread across
/// event handlers in a XAML code-behind.
/// </summary>
public static class ContextMenuModel
{
    private static readonly ContextMenuItem Divider = new(ContextCommand.Separator, string.Empty);

    /// <summary>The rows to show, in order, or nothing when there is no menu.</summary>
    public static IReadOnlyList<ContextMenuItem> For(ContextTarget target)
    {
        if (!target.DocumentOpen)
        {
            return [];
        }

        return target.OnObject ? ObjectMenu(target) : PageMenu(target);
    }

    /// <summary>
    /// The menu for a mark on the page.
    ///
    /// Ordered the way Windows orders this menu everywhere else: the object's
    /// own primary action first, then the clipboard, then arrangement, then
    /// grouping. Nothing is hidden for being unavailable, because a row the
    /// user cannot click still tells them the command exists and roughly what
    /// it needs; hiding it teaches nothing.
    /// </summary>
    private static List<ContextMenuItem> ObjectMenu(ContextTarget t)
    {
        var items = new List<ContextMenuItem>();

        // Only for a single box: the editor opens on ONE, and opening it on the
        // anchor of a three-object selection would quietly drop the other two.
        if (t.IsTextBox && t.SelectionCount == 1)
        {
            items.Add(new(ContextCommand.EditText, "Edit text"));
            items.Add(Divider);
        }

        items.Add(new(ContextCommand.Cut, "Cut", Accelerator: "Ctrl+X"));
        items.Add(new(ContextCommand.Copy, "Copy", Accelerator: "Ctrl+C"));
        items.Add(new(ContextCommand.Paste, "Paste", t.ClipboardHasContent, "Ctrl+V"));
        items.Add(new(ContextCommand.Delete, "Delete", Accelerator: "Del"));
        items.Add(Divider);

        // Always live. Whether a reorder is actually possible depends on the
        // whole page stack and on the order being aimed at, and the command
        // works that out and refuses with an explanation when a mark from
        // another editor is in the way. Greying these out on a guess would hide
        // that explanation, which is the only part that says what is wrong.
        // The bracket chords, as every drawing application binds them. "Bring
        // to front" advertised Ctrl+Shift+] here long before anything answered
        // it; the other three now have chords too, so they say so.
        items.Add(new(ContextCommand.BringToFront, "Bring to front", Accelerator: "Ctrl+Shift+]"));
        items.Add(new(ContextCommand.BringForward, "Bring forward", Accelerator: "Ctrl+]"));
        items.Add(new(ContextCommand.SendBackward, "Send backward", Accelerator: "Ctrl+["));
        items.Add(new(ContextCommand.SendToBack, "Send to back", Accelerator: "Ctrl+Shift+["));
        items.Add(Divider);

        items.Add(new(ContextCommand.Group, "Group", t.SelectionCount >= 2, "Ctrl+G"));
        items.Add(new(ContextCommand.Ungroup, "Ungroup", t.CanUngroup, "Ctrl+Shift+G"));

        return items;
    }

    /// <summary>
    /// The menu for bare paper.
    ///
    /// Deliberately short. A right-click that hits nothing has no object to
    /// talk about, so what is left is the one clipboard command that still
    /// applies, a way to pick everything up, and the page itself.
    /// </summary>
    private static List<ContextMenuItem> PageMenu(ContextTarget t) =>
    [
        new(ContextCommand.Paste, "Paste", t.ClipboardHasContent, "Ctrl+V"),
        Divider,
        new(ContextCommand.SelectAllOnPage, "Select all on page", Accelerator: "Ctrl+A"),
        Divider,
        new(ContextCommand.RotatePage, "Rotate page 90"),
    ];
}
