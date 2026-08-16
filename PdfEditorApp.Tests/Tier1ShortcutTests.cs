using System;
using System.IO;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The chords that fill holes with no keyboard path at all.
///
/// Two of them, Ctrl+N and Ctrl+W, were DECLARED and dead: an accelerator on a
/// MenuFlyoutItem does not exist until its flyout has been opened once, so the
/// menu printed a promise the app did not keep. That failure mode is invisible
/// by construction, which is why the mapping lives somewhere a test can see it.
/// </summary>
public class Tier1ShortcutTests
{
    private const bool Ctrl = true;
    private const bool NoCtrl = false;
    private const bool Shift = true;
    private const bool NoShift = false;
    private const bool InAField = true;
    private const bool OnTheCanvas = false;

    private static EditorCommand Resolve(int key, bool ctrl, bool shift, bool textFocused = OnTheCanvas) =>
        KeyboardCommands.Resolve(key, ctrl, shift, textFocused);

    // ---------------- The chords ----------------

    [Fact]
    public void ctrl_n_makes_a_document_and_ctrl_shift_n_goes_to_a_page()
    {
        // Ctrl+G is the usual go-to-page and is taken by Group here, because
        // this is an editor first. Acrobat makes the same swap.
        Assert.Equal(EditorCommand.New, Resolve(KeyboardCommands.KeyN, Ctrl, NoShift));
        Assert.Equal(EditorCommand.GoToPage, Resolve(KeyboardCommands.KeyN, Ctrl, Shift));
    }

    [Fact]
    public void ctrl_w_closes_the_document()
    {
        Assert.Equal(EditorCommand.CloseDocument, Resolve(KeyboardCommands.KeyW, Ctrl, NoShift));
    }

    [Fact]
    public void ctrl_tab_moves_between_tabs()
    {
        Assert.Equal(EditorCommand.NextTab, Resolve(KeyboardCommands.KeyTab, Ctrl, NoShift));
        Assert.Equal(EditorCommand.PreviousTab, Resolve(KeyboardCommands.KeyTab, Ctrl, Shift));
    }

    [Fact]
    public void plain_tab_is_left_alone_so_focus_can_still_move()
    {
        // Binding Tab itself once meant keyboard users could not move focus
        // anywhere in the app, and a stray Tab arriving as a system dialog was
        // dismissed opened the pages panel on launch.
        Assert.Equal(EditorCommand.None, Resolve(KeyboardCommands.KeyTab, NoCtrl, NoShift));
        Assert.Equal(EditorCommand.None, Resolve(KeyboardCommands.KeyTab, NoCtrl, Shift));
    }

    [Fact]
    public void f3_steps_through_the_matches()
    {
        Assert.Equal(EditorCommand.FindNext, Resolve(KeyboardCommands.KeyF3, NoCtrl, NoShift));
        Assert.Equal(EditorCommand.FindPrevious, Resolve(KeyboardCommands.KeyF3, NoCtrl, Shift));
    }

    [Fact]
    public void f3_works_inside_the_find_box_which_is_where_it_will_be_pressed()
    {
        // THE case for F3. The reader has just typed a query and wants the next
        // hit without leaving the field. Every other command refuses while a
        // text field has focus, and refusing this one would make the key
        // useless in the one place it matters most.
        Assert.Equal(EditorCommand.FindNext, Resolve(KeyboardCommands.KeyF3, NoCtrl, NoShift, InAField));
        Assert.Equal(EditorCommand.FindPrevious, Resolve(KeyboardCommands.KeyF3, NoCtrl, Shift, InAField));
    }

    [Fact]
    public void every_other_new_chord_stays_out_of_text_fields()
    {
        // Ctrl+N in a text box is not "new document" to anyone typing.
        foreach (int key in new[]
        {
            KeyboardCommands.KeyN,
            KeyboardCommands.KeyW,
            KeyboardCommands.KeyTab,
        })
        {
            Assert.Equal(EditorCommand.None, Resolve(key, Ctrl, NoShift, InAField));
            Assert.Equal(EditorCommand.None, Resolve(key, Ctrl, Shift, InAField));
        }
    }

    [Fact]
    public void the_new_chords_need_their_modifier()
    {
        // N, W and Tab unmodified are a tool key, a tool key and focus
        // traversal. Claiming them bare would break typing everywhere.
        Assert.Equal(EditorCommand.None, Resolve(KeyboardCommands.KeyN, NoCtrl, NoShift));
        Assert.Equal(EditorCommand.None, Resolve(KeyboardCommands.KeyW, NoCtrl, NoShift));
    }

    [Fact]
    public void nothing_that_already_worked_was_taken_away()
    {
        // The chords that existed before, re-asserted here because this change
        // edited the middle of the switch they live in.
        Assert.Equal(EditorCommand.Open, Resolve(KeyboardCommands.KeyO, Ctrl, NoShift));
        Assert.Equal(EditorCommand.Save, Resolve(KeyboardCommands.KeyS, Ctrl, NoShift));
        Assert.Equal(EditorCommand.SaveAs, Resolve(KeyboardCommands.KeyS, Ctrl, Shift));
        Assert.Equal(EditorCommand.Print, Resolve(KeyboardCommands.KeyP, Ctrl, NoShift));
        Assert.Equal(EditorCommand.ToggleRulers, Resolve(KeyboardCommands.KeyR, Ctrl, NoShift));
        Assert.Equal(EditorCommand.Group, Resolve(KeyboardCommands.KeyG, Ctrl, NoShift));
        Assert.Equal(EditorCommand.Ungroup, Resolve(KeyboardCommands.KeyG, Ctrl, Shift));
        Assert.Equal(EditorCommand.Undo, Resolve(KeyboardCommands.KeyZ, Ctrl, NoShift));
        Assert.Equal(EditorCommand.Redo, Resolve(KeyboardCommands.KeyZ, Ctrl, Shift));
        Assert.Equal(EditorCommand.Redo, Resolve(KeyboardCommands.KeyY, Ctrl, NoShift));
        Assert.Equal(EditorCommand.RotateViewClockwise, Resolve(KeyboardCommands.KeyOemPlus, Ctrl, Shift));
        Assert.Equal(EditorCommand.RotateViewCounterClockwise, Resolve(KeyboardCommands.KeyOemMinus, Ctrl, Shift));
    }

    // ---------------- Tier 2: editor muscle memory ----------------

    [Theory]
    [InlineData(KeyboardCommands.KeyCloseBracket, NoShift, EditorCommand.BringForward)]
    [InlineData(KeyboardCommands.KeyCloseBracket, Shift, EditorCommand.BringToFront)]
    [InlineData(KeyboardCommands.KeyOpenBracket, NoShift, EditorCommand.SendBackward)]
    [InlineData(KeyboardCommands.KeyOpenBracket, Shift, EditorCommand.SendToBack)]
    public void the_brackets_move_an_object_through_the_stack(int key, bool shift, EditorCommand expected)
    {
        // Where Illustrator, InDesign and Photoshop all put z-order, and where
        // this app's own context menu had been advertising Ctrl+Shift+] since
        // before anything answered it.
        Assert.Equal(expected, Resolve(key, Ctrl, shift));
    }

    [Fact]
    public void ctrl_d_duplicates()
    {
        Assert.Equal(EditorCommand.Duplicate, Resolve(KeyboardCommands.KeyD, Ctrl, NoShift));
    }

    [Fact]
    public void the_tier_two_chords_stay_out_of_text_fields_and_need_ctrl()
    {
        foreach (int key in new[]
        {
            KeyboardCommands.KeyD,
            KeyboardCommands.KeyOpenBracket,
            KeyboardCommands.KeyCloseBracket,
        })
        {
            Assert.Equal(EditorCommand.None, Resolve(key, Ctrl, NoShift, InAField));
            Assert.Equal(EditorCommand.None, Resolve(key, NoCtrl, NoShift));
        }

        // Plain D is the Draw tool, and a bracket is a character somebody may
        // want to type into a text box.
        Assert.Equal(EditorCommand.None, Resolve(KeyboardCommands.KeyD, NoCtrl, NoShift, InAField));
    }

    [Fact]
    public void duplicating_offsets_the_copy_and_costs_one_undo()
    {
        // The drag version puts the copy at IDENTICAL bounds because the drag
        // separates them. From the keyboard there is no drag, so an unoffset
        // copy sits exactly on the original and reads as nothing happening.
        //
        // And one history step, not two: a step captures the state when it is
        // pushed, so the one taken before the duplicate already returns to
        // before both halves. Two would make one keystroke cost two undos.
        string vm = Read("PdfEditorApp", "ViewModels", "ViewportViewModel.cs");
        int at = vm.IndexOf("public bool DuplicateSelected()", StringComparison.Ordinal);
        Assert.True(at >= 0, "DuplicateSelected is gone; this test needs rewriting");

        string body = vm[at..(at + 500)];
        Assert.Contains("DuplicateSelectedForDrag()", body, StringComparison.Ordinal);
        Assert.Contains("recordHistory: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void every_command_that_claims_a_chord_can_be_reached_by_one()
    {
        // The real collision risk is not two commands on one chord, which a
        // switch cannot express: it is a command that has an entry in the enum,
        // a case in the dispatcher and a menu item, and no chord that reaches
        // it, because a later arm was shadowed by an earlier one. The compiler
        // does not warn when the arms differ only by a `when` clause.
        var reachable = new System.Collections.Generic.HashSet<EditorCommand>();

        for (int key = 0; key <= 0xFF; key++)
        {
            foreach (bool ctrl in new[] { false, true })
            {
                foreach (bool shift in new[] { false, true })
                {
                    reachable.Add(Resolve(key, ctrl, shift));
                }
            }
        }

        foreach (var command in Enum.GetValues<EditorCommand>())
        {
            if (command == EditorCommand.None)
            {
                continue;
            }

            Assert.True(
                reachable.Contains(command),
                $"{command} is in the enum but no chord resolves to it");
        }
    }

    // ---------------- The wiring ----------------

    private static string Read(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }

    [Fact]
    public void every_new_command_is_dispatched()
    {
        // A command that resolves and then falls off the end of the switch is
        // the same as no chord at all, and looks identical from here.
        string run = Read("PdfEditorApp", "MainPage.xaml.cs");

        foreach (var command in new[]
        {
            EditorCommand.New, EditorCommand.CloseDocument,
            EditorCommand.NextTab, EditorCommand.PreviousTab,
            EditorCommand.FindNext, EditorCommand.FindPrevious,
            EditorCommand.GoToPage,
        })
        {
            Assert.Contains($"case EditorCommand.{command}:", run, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void f3_survives_the_text_focus_early_return()
    {
        // The key handler bails out whenever a text field has focus, so
        // resolving F3 correctly is not enough: it has to be let through that
        // gate as well, or it works everywhere except the find box.
        string code = Read("PdfEditorApp", "MainPage.xaml.cs");
        int gate = code.IndexOf("if (IsTextInputFocused)", StringComparison.Ordinal);
        Assert.True(gate >= 0);

        string block = code[gate..(gate + 1400)];
        Assert.Contains("EditorCommand.FindNext or EditorCommand.FindPrevious", block, StringComparison.Ordinal);
    }

    [Fact]
    public void stepping_tabs_wraps_at_both_ends()
    {
        // With two tabs open, which is the common case, a Ctrl+Tab that
        // refuses to pass the end works in one direction only.
        string body = Read("PdfEditorApp", "MainWindow.xaml.cs");
        int at = body.IndexOf("public void StepTab", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string method = body[at..(at + 700)];
        Assert.Contains("% count", method, StringComparison.Ordinal);
        Assert.Contains("next += count", method, StringComparison.Ordinal);
    }

    [Fact]
    public void the_menu_still_prints_the_chords_it_promises()
    {
        // The accelerators stay on the menu items: they are what draws "Ctrl+N"
        // beside the entry. They are simply no longer the only declaration.
        string xaml = Read("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("<KeyboardAccelerator Modifiers=\"Control\" Key=\"N\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<KeyboardAccelerator Modifiers=\"Control\" Key=\"W\" />", xaml, StringComparison.Ordinal);
    }
}
