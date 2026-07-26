using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditorApp.Viewport;

/// <summary>What plain left-drag/click currently does in the viewport.</summary>
public enum ToolMode
{
    /// <summary>Drag selects text, or picks up and moves an annotation.</summary>
    Select,
    /// <summary>Drag selects text and immediately commits it as a permanent highlight.</summary>
    Highlight,
    /// <summary>Click places a sticky note.</summary>
    Note,
    /// <summary>Drag draws a freehand ink stroke.</summary>
    Draw,
    /// <summary>Plain left-drag pans, same as holding Space with any other tool active.</summary>
    Hand,
    /// <summary>Click places the chosen stamp image.</summary>
    Stamp,
}

/// <summary>Which extra controls a tool needs in the property bar.</summary>
[Flags]
public enum ToolOptions
{
    None = 0,
    /// <summary>Offers a colour, from the pen or highlighter palette.</summary>
    Color = 1,
    /// <summary>Offers a stroke thickness.</summary>
    Width = 2,
    /// <summary>Offers the stamp picker.</summary>
    Stamp = 4,
}

/// <summary>
/// One entry in the tool rail: everything the UI needs to draw a tool and
/// decide what it offers.
///
/// A record rather than a hand-written button, because every new tool used to
/// mean another block of XAML, another named field, and another line in the
/// method that highlights the active one. Three places to edit per tool is
/// three places to forget, and the rail is about to grow shapes, text boxes
/// and callouts.
/// </summary>
/// <param name="Glyph">Segoe MDL2 Assets code point.</param>
/// <param name="Shortcut">Single key that selects this tool, uppercase.</param>
public sealed record ToolDefinition(
    ToolMode Mode,
    string Name,
    string Glyph,
    char Shortcut,
    ToolOptions Options)
{
    /// <summary>Tooltip text, with the shortcut appended so it is discoverable.</summary>
    public string Tooltip => $"{Name} ({Shortcut})";

    public bool Offers(ToolOptions option) => (Options & option) != 0;
}

/// <summary>The tools the rail shows, in order.</summary>
public static class ToolCatalog
{
    /// <summary>
    /// Order matters: it is the order they appear, and it groups by what they
    /// do. Navigation first, then selection, then the marks, then the things
    /// that place an object.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        new(ToolMode.Hand, "Hand", "", 'H', ToolOptions.None),
        new(ToolMode.Select, "Select", "", 'V', ToolOptions.None),
        new(ToolMode.Highlight, "Highlight", "", 'U', ToolOptions.Color),
        new(ToolMode.Draw, "Draw", "", 'D', ToolOptions.Color | ToolOptions.Width),
        new(ToolMode.Note, "Note", "", 'N', ToolOptions.None),
        new(ToolMode.Stamp, "Stamp", "", 'S', ToolOptions.Stamp),
    ];

    public static ToolDefinition For(ToolMode mode) =>
        All.FirstOrDefault(t => t.Mode == mode) ?? All[0];

    /// <summary>
    /// The tool a key selects, or null.
    ///
    /// Lives here rather than in a switch in the key handler so that a tool
    /// and its shortcut are declared in exactly one place and cannot drift
    /// apart.
    /// </summary>
    public static ToolDefinition? ForShortcut(char key)
    {
        char upper = char.ToUpperInvariant(key);
        return All.FirstOrDefault(t => t.Shortcut == upper);
    }
}
