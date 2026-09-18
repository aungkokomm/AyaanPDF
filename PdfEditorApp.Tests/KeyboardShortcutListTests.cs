using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Help > Keyboard shortcuts says what the keys really do: every chord it
/// lists is handled somewhere, and every chord the resolver knows is listed.
/// </summary>
public class KeyboardShortcutListTests
{
    private static string Read(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path)).Replace("\r\n", "\n");
    }

    private static IEnumerable<KeyboardShortcut> All =>
        KeyboardShortcutList.Groups.SelectMany(g => g.Shortcuts);

    private static EditorCommand Resolve(KeyChord c) =>
        KeyboardCommands.Resolve(c.Code, c.Ctrl, c.Shift, textFocused: false, alt: c.Alt);

    /// <summary>
    /// The chords the window's own key handler takes, outside the resolver,
    /// and the line in the source that takes each one.
    /// </summary>
    private static readonly Dictionary<KeyChord, (string File, string Handles)> HandledByTheWindow = new()
    {
        [new(0x51, Ctrl: true)] = ("MainWindow.xaml", "<KeyboardAccelerator Modifiers=\"Control\" Key=\"Q\""),
        [new(0x58, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.X when _isCtrlDown:"),
        [new(0x43, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.C when _isCtrlDown:"),
        [new(0x56, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.V when _isCtrlDown:"),
        [new(0x41, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.A when _isCtrlDown:"),
        [new(0x46, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.F when _isCtrlDown:"),
        [new(0x30, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.Number0 when _isCtrlDown:"),
        [new(0x31, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.Number1 when _isCtrlDown:"),
        [new(0x32, Ctrl: true)] = ("MainPage.xaml.cs", "case VirtualKey.Number2 when _isCtrlDown:"),
        [new(0x2E)] = ("MainPage.xaml.cs", "case VirtualKey.Delete:"),
        [new(0x08)] = ("MainPage.xaml.cs", "case VirtualKey.Back:"),
        [new(0x1B)] = ("MainPage.xaml.cs", "case VirtualKey.Escape:"),
        [new(0x22)] = ("MainPage.xaml.cs", "case VirtualKey.PageDown:"),
        [new(0x21)] = ("MainPage.xaml.cs", "case VirtualKey.PageUp:"),
        [new(0x24)] = ("MainPage.xaml.cs", "case VirtualKey.Home:"),
        [new(0x23)] = ("MainPage.xaml.cs", "case VirtualKey.End:"),
        [new(0x73)] = ("MainPage.xaml.cs", "case VirtualKey.F4:"),
        [new(0x75)] = ("MainPage.xaml.cs", "case VirtualKey.F6:"),
    };

    [Fact]
    public void every_chord_listed_does_something()
    {
        foreach (var shortcut in All)
        {
            foreach (var chord in shortcut.Chords)
            {
                if (Resolve(chord) != EditorCommand.None)
                {
                    continue;
                }
                if (chord.Code == PresentationKeys.KeyF11)
                {
                    Assert.Equal(PresentationAction.Enter, PresentationKeys.Resolve(chord.Code, isFullScreen: false, textFocused: false));
                    continue;
                }
                if (!chord.Ctrl && !chord.Shift && !chord.Alt && ToolCatalog.ForShortcut((char)chord.Code) is not null)
                {
                    continue;
                }
                Assert.True(HandledByTheWindow.TryGetValue(chord, out var where),
                    $"\"{shortcut.What}\" ({string.Join("+", shortcut.Keys)}) is listed but nothing handles it");
                Assert.Contains(where.Handles, Read("PdfEditorApp", where.File), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void every_command_the_keyboard_can_reach_is_listed()
    {
        var reachable = new HashSet<EditorCommand>();
        for (int code = 1; code < 255; code++)
        {
            foreach (bool ctrl in new[] { false, true })
            foreach (bool shift in new[] { false, true })
            foreach (bool alt in new[] { false, true })
            {
                var command = KeyboardCommands.Resolve(code, ctrl, shift, textFocused: false, alt: alt);
                if (command != EditorCommand.None)
                {
                    reachable.Add(command);
                }
            }
        }

        var listed = All.SelectMany(s => s.Chords).Select(Resolve).ToHashSet();
        Assert.Empty(reachable.Except(listed));
    }

    [Fact]
    public void the_keys_shown_are_the_keys_behind_each_row()
    {
        foreach (var shortcut in All.Where(s => s.Chords.Count > 0))
        {
            var chord = shortcut.Chords[0];
            Assert.Equal(chord.Ctrl, shortcut.Keys.Contains("Ctrl"));
            Assert.Equal(chord.Shift, shortcut.Keys.Contains("Shift"));
            Assert.Equal(chord.Alt, shortcut.Keys.Contains("Alt"));
        }
    }

    [Fact]
    public void the_tool_letters_are_the_catalogs()
    {
        var tools = KeyboardShortcutList.Groups.Single(g => g.Title == "Tools").Shortcuts;
        Assert.Equal(ToolCatalog.All.Select(t => t.Shortcut.ToString()), tools.Select(s => s.Keys.Single()));
        Assert.Contains(tools, s => s.What == "Text (in Edit)");
        Assert.Contains(tools, s => s.What == "Hand");
    }

    [Fact]
    public void f1_opens_the_list_even_from_a_text_field_and_is_on_the_help_menu()
    {
        Assert.Equal(EditorCommand.KeyboardShortcuts,
            KeyboardCommands.Resolve(KeyboardCommands.KeyF1, ctrl: false, shift: false, textFocused: true));
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyF1, ctrl: true, shift: false, textFocused: false));

        string page = Read("PdfEditorApp", "MainPage.xaml.cs");
        Assert.Contains("case EditorCommand.KeyboardShortcuts: KeyboardShortcuts_Click(this, null!); break;", page, StringComparison.Ordinal);
        Assert.Contains("inField is EditorCommand.FindNext or EditorCommand.FindPrevious or EditorCommand.KeyboardShortcuts", page, StringComparison.Ordinal);
        Assert.Contains("foreach (var group in KeyboardShortcutList.Groups)", page, StringComparison.Ordinal);

        Assert.Contains("<MenuFlyoutItem Text=\"Keyboard shortcuts\" Click=\"KeyboardShortcuts_Click\" KeyboardAcceleratorTextOverride=\"F1\" />",
            Read("PdfEditorApp", "MainPage.xaml"), StringComparison.Ordinal);
    }
}
