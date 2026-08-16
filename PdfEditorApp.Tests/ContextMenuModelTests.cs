using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// What a right-click offers, and when.
///
/// The menu is the app's answer to a discoverability problem: Cut, Copy, the
/// four z-order commands and Group all exist and are all reachable only from a
/// floating toolbar that appears after a selection, or from a chord nobody was
/// told about. So the important assertions here are not that a command works,
/// which is tested elsewhere, but that it is OFFERED, spelled correctly, and
/// enabled exactly when it would do something.
/// </summary>
public class ContextMenuModelTests
{
    private static ContextTarget OnObject => new()
    {
        DocumentOpen = true,
        OnObject = true,
        SelectionCount = 1,
    };

    private static ContextTarget OnPage => new()
    {
        DocumentOpen = true,
        OnObject = false,
    };

    private static ContextMenuItem Find(ContextTarget target, ContextCommand command) =>
        ContextMenuModel.For(target).Single(i => i.Command == command);

    private static bool Offers(ContextTarget target, ContextCommand command) =>
        ContextMenuModel.For(target).Any(i => i.Command == command);

    // ---------------- When there is a menu at all ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void a_closed_document_has_no_menu(bool onObject)
    {
        // Every command on this menu acts on a page or on something drawn on
        // one. With no document open a right-click has nothing to talk about,
        // and an all-disabled menu is worse than none.
        var target = new ContextTarget { DocumentOpen = false, OnObject = onObject };
        Assert.Empty(ContextMenuModel.For(target));
    }

    // ---------------- The object menu ----------------

    [Theory]
    [InlineData(ContextCommand.Cut)]
    [InlineData(ContextCommand.Copy)]
    [InlineData(ContextCommand.Paste)]
    [InlineData(ContextCommand.Delete)]
    [InlineData(ContextCommand.BringToFront)]
    [InlineData(ContextCommand.BringForward)]
    [InlineData(ContextCommand.SendBackward)]
    [InlineData(ContextCommand.SendToBack)]
    [InlineData(ContextCommand.Group)]
    [InlineData(ContextCommand.Ungroup)]
    public void a_click_on_an_object_offers_the_object_commands(ContextCommand command)
    {
        Assert.True(Offers(OnObject, command));
    }

    [Theory]
    [InlineData(ContextCommand.SelectAllOnPage)]
    [InlineData(ContextCommand.RotatePage)]
    public void the_object_menu_leaves_out_the_page_commands(ContextCommand command)
    {
        // Right-clicking a shape is a question about the shape. Page commands
        // sitting under it are how a user rotates a page while trying to send
        // an arrow backward.
        Assert.False(Offers(OnObject, command));
    }

    [Fact]
    public void the_four_z_order_commands_are_always_live_on_an_object()
    {
        // Whether a reorder is possible depends on what ELSE is on the page: a
        // mark from another editor in the way makes it destructive, and the
        // command refuses with an explanation. That check needs the whole page
        // stack and a target order, neither of which a menu has. Greying them
        // out on a guess would hide the explanation, which is the only part
        // that tells the user what is wrong.
        foreach (var c in new[]
                 {
                     ContextCommand.BringToFront, ContextCommand.BringForward,
                     ContextCommand.SendBackward, ContextCommand.SendToBack,
                 })
        {
            Assert.True(Find(OnObject, c).Enabled);
        }
    }

    // ---------------- The page menu ----------------

    [Theory]
    [InlineData(ContextCommand.Paste)]
    [InlineData(ContextCommand.SelectAllOnPage)]
    [InlineData(ContextCommand.RotatePage)]
    public void a_click_on_empty_page_offers_the_page_commands(ContextCommand command)
    {
        Assert.True(Offers(OnPage, command));
    }

    [Theory]
    [InlineData(ContextCommand.Cut)]
    [InlineData(ContextCommand.Copy)]
    [InlineData(ContextCommand.Delete)]
    [InlineData(ContextCommand.Group)]
    [InlineData(ContextCommand.Ungroup)]
    [InlineData(ContextCommand.EditText)]
    [InlineData(ContextCommand.BringToFront)]
    public void the_page_menu_leaves_out_the_object_commands(ContextCommand command)
    {
        // Nothing is selected once a right-click lands on empty paper, so every
        // one of these would be a disabled row explaining nothing.
        Assert.False(Offers(OnPage, command));
    }

    // ---------------- Enablement ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void paste_is_live_only_when_something_was_copied(bool hasClipboard)
    {
        var onObject = OnObject with { ClipboardHasContent = hasClipboard };
        var onPage = OnPage with { ClipboardHasContent = hasClipboard };

        Assert.Equal(hasClipboard, Find(onObject, ContextCommand.Paste).Enabled);
        Assert.Equal(hasClipboard, Find(onPage, ContextCommand.Paste).Enabled);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void group_needs_two_objects(int selectionCount, bool enabled)
    {
        var target = OnObject with { SelectionCount = selectionCount };
        Assert.Equal(enabled, Find(target, ContextCommand.Group).Enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ungroup_needs_the_click_to_be_on_a_group(bool canUngroup)
    {
        var target = OnObject with { CanUngroup = canUngroup };
        Assert.Equal(canUngroup, Find(target, ContextCommand.Ungroup).Enabled);
    }

    [Fact]
    public void a_command_that_would_do_nothing_is_shown_greyed_rather_than_hidden()
    {
        // Discoverability is the whole point. A menu that hides Group until you
        // have already worked out that you need two objects selected teaches
        // nobody that grouping exists.
        var items = ContextMenuModel.For(OnObject);
        Assert.Contains(items, i => i.Command == ContextCommand.Group && !i.Enabled);
    }

    // ---------------- Editing a text box ----------------

    [Fact]
    public void edit_text_leads_the_menu_for_a_single_text_box()
    {
        // Double-click already opens the editor, and nothing says so. This is
        // the only place the gesture is named, so it goes first, where the
        // primary action belongs.
        var items = ContextMenuModel.For(OnObject with { IsTextBox = true });
        Assert.Equal(ContextCommand.EditText, items[0].Command);
    }

    [Fact]
    public void edit_text_is_absent_for_anything_but_a_text_box()
    {
        Assert.False(Offers(OnObject with { IsTextBox = false }, ContextCommand.EditText));
    }

    [Fact]
    public void edit_text_is_absent_when_more_than_one_object_is_picked()
    {
        // The editor opens on one box. With three selected there is no answer
        // to which, and opening it on the anchor would silently drop the rest
        // of the selection.
        var target = OnObject with { IsTextBox = true, SelectionCount = 3 };
        Assert.False(Offers(target, ContextCommand.EditText));
    }

    // ---------------- Shape of the menu ----------------

    public static TheoryData<ContextTarget> EveryMenu() =>
    [
        OnObject,
        OnObject with { IsTextBox = true },
        OnObject with { SelectionCount = 4, CanUngroup = true, ClipboardHasContent = true },
        OnPage,
        OnPage with { ClipboardHasContent = true },
    ];

    [Theory]
    [MemberData(nameof(EveryMenu))]
    public void no_menu_starts_or_ends_with_a_divider(ContextTarget target)
    {
        var items = ContextMenuModel.For(target);
        Assert.NotEmpty(items);
        Assert.False(items[0].IsSeparator);
        Assert.False(items[^1].IsSeparator);
    }

    [Theory]
    [MemberData(nameof(EveryMenu))]
    public void no_menu_has_two_dividers_in_a_row(ContextTarget target)
    {
        // The obvious way to write this builder is to append a divider after
        // each optional block, which produces doubles the moment a block is
        // skipped. WinUI draws both.
        var items = ContextMenuModel.For(target);
        for (int i = 1; i < items.Count; i++)
        {
            Assert.False(items[i].IsSeparator && items[i - 1].IsSeparator);
        }
    }

    [Theory]
    [MemberData(nameof(EveryMenu))]
    public void every_row_is_labelled_and_appears_once(ContextTarget target)
    {
        var items = ContextMenuModel.For(target);

        foreach (var item in items.Where(i => !i.IsSeparator))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
        }

        var commands = items.Where(i => !i.IsSeparator).Select(i => i.Command).ToList();
        Assert.Equal(commands.Count, commands.Distinct().Count());
    }

    // ---------------- Accelerators ----------------

    [Theory]
    [InlineData(ContextCommand.Cut, "Ctrl+X")]
    [InlineData(ContextCommand.Copy, "Ctrl+C")]
    [InlineData(ContextCommand.Paste, "Ctrl+V")]
    [InlineData(ContextCommand.Delete, "Del")]
    [InlineData(ContextCommand.Group, "Ctrl+G")]
    [InlineData(ContextCommand.Ungroup, "Ctrl+Shift+G")]
    [InlineData(ContextCommand.BringToFront, "Ctrl+Shift+]")]
    public void a_shown_accelerator_is_a_key_the_app_actually_handles(
        ContextCommand command, string accelerator)
    {
        // These eight are the chords MainPage's key handler really implements.
        // A menu that advertises a shortcut which does nothing is worse than
        // one that advertises none: the user stops trusting the column.
        var target = command == ContextCommand.SelectAllOnPage ? OnPage : OnObject;
        Assert.Equal(accelerator, Find(target, command).Accelerator);
    }

    [Fact]
    public void select_all_advertises_its_chord()
    {
        Assert.Equal("Ctrl+A", Find(OnPage, ContextCommand.SelectAllOnPage).Accelerator);
    }

    [Theory]
    [InlineData(ContextCommand.BringForward, "Ctrl+]", KeyboardCommands.KeyCloseBracket, false, EditorCommand.BringForward)]
    [InlineData(ContextCommand.BringToFront, "Ctrl+Shift+]", KeyboardCommands.KeyCloseBracket, true, EditorCommand.BringToFront)]
    [InlineData(ContextCommand.SendBackward, "Ctrl+[", KeyboardCommands.KeyOpenBracket, false, EditorCommand.SendBackward)]
    [InlineData(ContextCommand.SendToBack, "Ctrl+Shift+[", KeyboardCommands.KeyOpenBracket, true, EditorCommand.SendToBack)]
    public void the_z_order_rows_advertise_chords_the_app_really_listens_for(
        ContextCommand command, string advertised, int key, bool shift, EditorCommand expected)
    {
        // This replaces a test asserting these three advertised NOTHING, which
        // was right while nothing was bound: claiming Ctrl+Shift+[ because
        // Illustrator has it would have been inventing a key. They are bound
        // now, so the menu says so, and the claim is checked against the
        // resolver rather than taken on trust. Menu text that promises a chord
        // nobody implements is the failure this whole file exists to prevent.
        Assert.Equal(advertised, Find(OnObject, command).Accelerator);

        Assert.Equal(
            expected,
            KeyboardCommands.Resolve(key, ctrl: true, shift: shift, textFocused: false));
    }
}
