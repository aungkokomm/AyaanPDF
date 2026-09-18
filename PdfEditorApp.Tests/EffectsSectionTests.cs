using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The Effects section of the property bar: when it shows, and that it is a
/// container rather than a control.
///
/// Visibility is decided in <see cref="PropertyBarLayout"/> with every other
/// section, for the reason that record exists: the corner slider once shipped
/// invisible because its visibility was worked out in a fill-flyout handler,
/// and nothing noticed until a person went looking for it.
///
/// EXTENSIBILITY IS THE POINT OF THE SHAPE OF THIS. Effects is ONE section
/// holding a list of effect rows, so a glow or an outer stroke later becomes
/// another row inside it and never another section, another field on
/// PropertyBarSections, or another line of bar layout. The property bar must
/// never scroll, and a section per effect is exactly how it would come to.
/// </summary>
public class EffectsSectionTests
{
    private static PropertyBarState State(
        bool shape = false, bool textBox = false, bool roundedRect = false,
        bool multi = false, ToolOptions options = ToolOptions.None,
        bool toolIsShape = false, ShapeKind kind = ShapeKind.Rectangle) =>
        new(options, kind, toolIsShape, shape, textBox, roundedRect, multi,
            HasSelectedAnnotation: roundedRect || shape || textBox);

    private static string FileFromRepo(params string[] relative)
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

    // ---------------- when it shows ----------------

    [Fact]
    public void selecting_a_shape_shows_the_effects_section()
    {
        // The main case: an effect belongs to a shape, so picking one under any
        // tool has to expose it, the same way colour and width already do.
        Assert.True(PropertyBarLayout.For(State(shape: true)).Effects);
    }

    [Fact]
    public void arming_the_shape_tool_shows_the_effects_section()
    {
        // Before the shape exists, so a shadow can be set up and then drawn
        // with, rather than having to be applied afterwards to every shape in
        // turn. The same rule the shape-kind picker follows.
        var sections = PropertyBarLayout.For(State(
            options: ToolOptions.Color | ToolOptions.Width | ToolOptions.Shape,
            toolIsShape: true));

        Assert.True(sections.Effects);
    }

    [Fact]
    public void a_rounded_rectangle_is_still_a_shape()
    {
        Assert.True(PropertyBarLayout.For(State(shape: true, roundedRect: true)).Effects);
    }

    // ---------------- when it does not ----------------

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void effects_stay_hidden_for_anything_that_is_not_a_shape(
        bool textBox, bool multi, bool toolIsShape)
    {
        // A text box has effects of its own one day, but not these: the model,
        // the tag and the renderer are all shape-only, so offering the section
        // would be offering something that cannot be applied.
        Assert.Equal(
            toolIsShape,
            PropertyBarLayout.For(State(textBox: textBox, multi: multi, toolIsShape: toolIsShape)).Effects);
    }

    [Fact]
    public void a_bar_with_nothing_in_it_has_no_effects_section_either()
    {
        var nothing = PropertyBarLayout.For(State());

        Assert.False(nothing.Effects);
    }

    // ---------------- where it lives ----------------

    [Fact]
    public void the_effects_section_brings_the_second_row_with_it()
    {
        // It lives in row 2, so a state that shows it must show the row. A
        // section visible inside a collapsed row is invisible, which is the
        // exact failure this record was introduced to stop.
        foreach (var state in new[]
                 {
                     State(shape: true),
                     State(options: ToolOptions.Shape, toolIsShape: true),
                 })
        {
            var sections = PropertyBarLayout.For(state);
            Assert.True(sections.Effects);
            Assert.True(sections.Row2, "the Effects section would be inside a collapsed row");
        }
    }

    [Fact]
    public void showing_effects_disturbs_no_other_section()
    {
        // Adding a section must not quietly turn its neighbours on. Compared
        // against a text box, which shares row 2 and shares none of the
        // conditions Effects is keyed on.
        var shape = PropertyBarLayout.For(State(shape: true));
        var text = PropertyBarLayout.For(State(textBox: true));

        Assert.False(shape.TextAlign);
        Assert.False(shape.Outline);
        Assert.False(shape.CornerRadius);
        Assert.False(shape.Stamp);
        Assert.False(shape.Align);
        Assert.False(text.Effects);
        Assert.True(text.Font, "the text sections must be untouched by this");
    }

    // ---------------- it is a container, not a control ----------------

    [Fact]
    public void there_is_exactly_one_effects_field_on_the_sections_record()
    {
        // The extensibility contract, stated where it can be checked. A glow
        // added later must become a ROW inside this section. If it arrives as
        // PropertyBarSections.Glow instead, the bar has gained a section per
        // effect and this fails.
        string source = FileFromRepo("PdfEditorApp.Viewport", "PropertyBarLayout.cs");

        int at = source.IndexOf(
            "public readonly record struct PropertyBarSections(", StringComparison.Ordinal);
        Assert.True(at > 0, "PropertyBarSections has been renamed; this test needs updating");

        int close = source.IndexOf(");", at, StringComparison.Ordinal);

        // The DECLARATIONS, not the prose about them: the record is documented
        // in terms of effects, so counting every mention would count the
        // comments explaining why there is only one.
        int fields = Regex
            .Matches(source[at..close], @"^\s*bool\s+(\w+)", RegexOptions.Multiline)
            .Count(m => m.Groups[1].Value.Contains("Effect", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, fields);
    }

    [Fact]
    public void the_section_holds_a_list_that_effect_rows_go_into()
    {
        // The other half of the same contract, in the view. The flyout carries
        // a named container so the next effect is a child added to it, not a
        // second button somewhere along the bar.
        string xaml = FileFromRepo("PdfEditorApp", "MainPage.xaml");

        Assert.Contains("x:Name=\"EffectsSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EffectRows\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void the_view_asks_the_layout_whether_to_show_it()
    {
        // Not decided in a handler somewhere. This is the wiring the corner
        // slider did not have.
        string code = FileFromRepo("PdfEditorApp", "MainPage.xaml.cs");

        Assert.Contains(
            "EffectsSection.Visibility = Show(sections.Effects);", code, StringComparison.Ordinal);
    }
}
