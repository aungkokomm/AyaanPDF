using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The app-layer decisions that used to live inside the view, where no test
/// could reach them.
///
/// Every case here is a regression that actually shipped on 2026-08-08. All
/// three had green builds and a green suite: the logic was correct and the
/// wiring was not, and nothing in the project could tell the difference. These
/// tests exist so that each of those failures is now a red test rather than a
/// build you have to look at.
/// </summary>
public class AppWiringTests
{
    // ---------------- Regression: the menu's accelerators are the real chords ----------------
    //
    // A MenuFlyoutItem's KeyboardAccelerator is dead until the flyout has been
    // opened once, and LIVE forever after, firing before the page's key
    // handler. Both halves have shipped bugs: six chords silently did nothing
    // because they were only ever declared as accelerators, and later Ctrl+S
    // went on opening the Save As picker because Save As kept the accelerator
    // after Save was added.
    //
    // So the XAML is read here and checked against KeyboardCommands, which is
    // the thing the key handler actually consults. A menu that advertises a
    // chord the resolver maps somewhere else is the bug, whichever way round.

    private static string MainPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "MainPage.xaml")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "PdfEditorApp", "MainPage.xaml"));
    }

    [Theory]
    [InlineData("Save_Click", "Control", "S", EditorCommand.Save)]
    [InlineData("SaveAs_Click", "Control,Shift", "S", EditorCommand.SaveAs)]
    [InlineData("OpenFile_Click", "Control", "O", EditorCommand.Open)]
    [InlineData("Print_Click", "Control", "P", EditorCommand.Print)]
    public void a_menu_item_advertises_the_chord_that_actually_runs_it(
        string handler, string modifiers, string key, EditorCommand expected)
    {
        string xaml = MainPageXaml();

        // The item, then everything up to the end of its accelerator block.
        int item = xaml.IndexOf($"Click=\"{handler}\"", StringComparison.Ordinal);
        Assert.True(item >= 0, $"no menu item calls {handler}");
        int end = xaml.IndexOf("</MenuFlyoutItem>", item, StringComparison.Ordinal);
        string block = xaml[item..end];

        Assert.True(
            block.Contains($"Modifiers=\"{modifiers}\"", StringComparison.Ordinal)
            && block.Contains($"Key=\"{key}\"", StringComparison.Ordinal),
            $"{handler} does not declare {modifiers}+{key}, so the menu and the key handler disagree");

        bool ctrl = modifiers.Contains("Control", StringComparison.Ordinal);
        bool shift = modifiers.Contains("Shift", StringComparison.Ordinal);
        Assert.Equal(expected, KeyboardCommands.Resolve(key[0], ctrl, shift, textFocused: false));
    }

    // ---------------- Regression 1: a drawn shape is written as drawn ----------------
    //
    // EndShape built the interop struct by hand and left the corner radius at
    // zero. The preview rounded the rectangle and the document got a square
    // one, so the shape changed the instant the pointer lifted.

    [Fact]
    public void a_drawn_rounded_rectangle_is_written_with_a_corner_radius()
    {
        var draft = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5);

        var spec = ShapeWriter.ForNewShape(draft, strokeWidthNorm: 0.004, captureWidth: 1000);

        Assert.Equal(ShapeKind.RoundedRectangle, spec.Kind);
        Assert.True(spec.CornerRadiusPx > 0,
            $"a rounded rectangle would be written with radius {spec.CornerRadiusPx}, so it would draw square");
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Arrow)]
    public void every_other_kind_is_written_with_no_radius(ShapeKind kind)
    {
        var spec = ShapeWriter.ForNewShape(
            new ShapeDraft(kind, 0.2, 0.3, 0.6, 0.5), 0.004, 1000);

        Assert.Equal(0, spec.CornerRadiusPx);
    }

    [Fact]
    public void the_written_shape_carries_the_drafts_corner_setting_not_the_default()
    {
        // The slider's value travels on the draft. If the writer reached for the
        // default instead, moving the slider would change the preview and not
        // the shape.
        var half = new ShapeDraft(ShapeKind.RoundedRectangle, 0.2, 0.3, 0.6, 0.5)
        {
            CornerFraction = 0.5,
        };
        var full = half with { CornerFraction = 1.0 };

        Assert.True(
            ShapeWriter.ForNewShape(full, 0.004, 1000).CornerRadiusPx
            > ShapeWriter.ForNewShape(half, 0.004, 1000).CornerRadiusPx);
    }

    [Fact]
    public void the_written_geometry_keeps_the_drag_direction()
    {
        // An arrow points where it was dragged. Normalising to a box here would
        // silently reverse arrows drawn right to left.
        var spec = ShapeWriter.ForNewShape(
            new ShapeDraft(ShapeKind.Arrow, 0.6, 0.5, 0.2, 0.3), 0.004, 1000);

        Assert.Equal(600, spec.X1, 3);
        Assert.Equal(200, spec.X2, 3);
    }

    // ---------------- Regression 2: the property bar shows what it should ----------------
    //
    // The corner slider's visibility was decided in a fill-flyout handler, which
    // only runs when the fill picker is opened. It could therefore never appear
    // from selecting a shape. It is now one field of one record computed in one
    // place, so a section cannot be wired up somewhere else by accident.

    private static PropertyBarState Selected(
        bool roundedRect = false, bool shape = false, bool textBox = false,
        bool multi = false, ToolOptions options = ToolOptions.None,
        bool toolIsShape = false, ShapeKind kind = ShapeKind.Rectangle) =>
        new(options, kind, toolIsShape, shape, textBox, roundedRect, multi,
            HasSelectedAnnotation: roundedRect || shape || textBox);

    [Fact]
    public void selecting_a_rounded_rectangle_shows_the_corner_slider()
    {
        var sections = PropertyBarLayout.For(Selected(roundedRect: true, shape: true));
        Assert.True(sections.CornerRadius, "the Corners slider would not appear for a rounded rectangle");
    }

    [Fact]
    public void arming_the_rounded_tool_with_nothing_selected_shows_the_corner_slider()
    {
        var sections = PropertyBarLayout.For(new PropertyBarState(
            ToolOptions.Shape, ShapeKind.RoundedRectangle, ToolIsShape: true,
            HasSelectedShape: false, HasSelectedTextBox: false,
            HasSelectedRoundedRect: false, HasMultiSelection: false,
            HasSelectedAnnotation: false));

        Assert.True(sections.CornerRadius);
    }

    [Fact]
    public void selecting_an_ordinary_shape_hides_the_corner_slider()
    {
        Assert.False(PropertyBarLayout.For(Selected(shape: true)).CornerRadius);
    }

    [Fact]
    public void selecting_a_shape_exposes_its_style_under_any_tool()
    {
        // Clicking a shape in Select mode has to expose colour, width and
        // opacity, the way Word does for text, even though the Select tool
        // offers none of them itself.
        var sections = PropertyBarLayout.For(Selected(shape: true));

        Assert.True(sections.Color);
        Assert.True(sections.Width);
        Assert.True(sections.Opacity);
        Assert.True(sections.TextStyle, "the Fill button lives here and applies to shapes");
        Assert.False(sections.TextAlign, "a shape has no text to align");
        Assert.False(sections.Outline, "a shape has no outline distinct from its stroke");
    }

    [Fact]
    public void selecting_a_text_box_exposes_the_text_sections_under_any_tool()
    {
        var sections = PropertyBarLayout.For(Selected(textBox: true));

        Assert.True(sections.Font);
        Assert.True(sections.FontSize);
        Assert.True(sections.TextStyle);
        Assert.True(sections.TextAlign);
        Assert.True(sections.Opacity);
    }

    [Fact]
    public void align_appears_only_for_a_real_multi_selection()
    {
        Assert.True(PropertyBarLayout.For(Selected(shape: true, multi: true)).Align);
        Assert.False(PropertyBarLayout.For(Selected(shape: true)).Align);
    }

    [Fact]
    public void a_bar_with_nothing_to_show_is_hidden_rather_than_left_empty()
    {
        // An empty pill floating over the page is worse than no bar.
        var nothing = PropertyBarLayout.For(new PropertyBarState(
            ToolOptions.None, ShapeKind.Rectangle, false, false, false, false, false, false));

        Assert.False(nothing.Bar);
        Assert.False(nothing.Row2);
    }

    [Fact]
    public void the_second_row_appears_only_when_one_of_its_own_sections_does()
    {
        // A simple tool stays a single row.
        Assert.True(PropertyBarLayout.For(Selected(shape: true)).Row2);
        Assert.False(PropertyBarLayout.For(new PropertyBarState(
            ToolOptions.Shape, ShapeKind.Rectangle, true, false, false, false, false, false)).Row2);
    }

    // ---------------- Regression 3: the keyboard chords exist ----------------
    //
    // Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z, Ctrl+R, Ctrl+G and Ctrl+Shift+G were
    // declared only on MenuFlyoutItems, whose accelerators are not live until
    // the flyout opens. Keyboard undo had never worked in this app.

    [Theory]
    [InlineData(KeyboardCommands.KeyZ, false, EditorCommand.Undo)]
    [InlineData(KeyboardCommands.KeyZ, true, EditorCommand.Redo)]
    [InlineData(KeyboardCommands.KeyY, false, EditorCommand.Redo)]
    [InlineData(KeyboardCommands.KeyG, false, EditorCommand.Group)]
    [InlineData(KeyboardCommands.KeyG, true, EditorCommand.Ungroup)]
    [InlineData(KeyboardCommands.KeyO, false, EditorCommand.Open)]
    // Ctrl+S saves the open file, Ctrl+Shift+S saves a copy. It used to open
    // the Save As picker either way, so the commonest keystroke in any editor
    // could not save the file you already had open.
    [InlineData(KeyboardCommands.KeyS, false, EditorCommand.Save)]
    [InlineData(KeyboardCommands.KeyS, true, EditorCommand.SaveAs)]
    [InlineData(KeyboardCommands.KeyR, false, EditorCommand.ToggleRulers)]
    public void every_menu_chord_resolves_to_its_command(int key, bool shift, EditorCommand expected)
    {
        Assert.Equal(expected,
            KeyboardCommands.Resolve(key, ctrl: true, shift: shift, textFocused: false));
    }

    [Fact]
    public void the_same_key_without_ctrl_is_not_a_command()
    {
        // Otherwise pressing G to pick a tool would group the selection.
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyG, ctrl: false, shift: false, textFocused: false));
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyZ, ctrl: false, shift: false, textFocused: false));
    }

    [Fact]
    public void a_chord_typed_into_a_text_field_belongs_to_the_text_field()
    {
        // Stealing Ctrl+Z while editing text would make typing unrecoverable.
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(KeyboardCommands.KeyZ, ctrl: true, shift: false, textFocused: true));
    }

    [Fact]
    public void an_unmapped_chord_resolves_to_nothing()
    {
        Assert.Equal(EditorCommand.None,
            KeyboardCommands.Resolve(0x51 /* Q */, ctrl: true, shift: false, textFocused: false));
    }
}
