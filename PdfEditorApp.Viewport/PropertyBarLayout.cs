namespace PdfEditorApp.Viewport;

/// <summary>
/// What the editor looks like to the property bar: the armed tool's options,
/// and what is currently selected. Everything the bar's layout depends on, and
/// nothing else.
/// </summary>
public readonly record struct PropertyBarState(
    ToolOptions ToolOptions,
    ShapeKind ActiveShapeKind,
    bool ToolIsShape,
    bool HasSelectedShape,
    bool HasSelectedTextBox,
    bool HasSelectedRoundedRect,
    bool HasMultiSelection,
    bool HasSelectedAnnotation);

/// <summary>
/// Which sections of the property bar are visible.
///
/// EVERY section, in one record, on purpose. The corner-radius slider shipped
/// invisible because its visibility was decided in a fill-flyout handler
/// instead of alongside the others, and nothing noticed. A section that is a
/// field of this record cannot be left out of the decision: adding one without
/// setting it does not compile.
/// </summary>
public readonly record struct PropertyBarSections(
    bool Bar,
    bool Row2,
    bool Color,
    bool Width,
    bool Opacity,
    bool Stamp,
    bool Align,
    bool CornerRadius,
    bool Shape,
    bool FontSize,
    bool Font,
    bool TextStyle,
    bool TextAlign,
    bool Outline);

/// <summary>
/// The property bar's layout rules, as a pure function.
///
/// Lives here rather than in the view because the view cannot be loaded by a
/// test assembly: the app is a WinUI project and the test project is plain
/// net10.0. Anything left in the view is only ever exercised by running the app
/// and looking, which is how a control ships invisible.
/// </summary>
public static class PropertyBarLayout
{
    public static PropertyBarSections For(PropertyBarState s)
    {
        // Colour and width show for tools that offer them AND for a selected
        // shape under any tool, so clicking a shape in Select mode still
        // exposes its style, the way Word does for text.
        bool color = s.ToolOptions.HasFlag(ToolOptions.Color) || s.HasSelectedShape;
        bool width = s.ToolOptions.HasFlag(ToolOptions.Width) || s.HasSelectedShape;

        // Opacity is UNIVERSAL: it applies to the primary colour of whatever is
        // selected (a text box's text, a shape's stroke, or the tool's next
        // mark).
        bool opacity = color || s.HasSelectedTextBox;

        bool stamp = s.ToolOptions.HasFlag(ToolOptions.Stamp);

        // Alignment on one object is a no-op, so it needs a real multi-selection.
        bool align = s.HasMultiSelection;

        bool cornerRadius = ShapePropertyBar.ShouldShowCornerRadius(
            s.HasSelectedRoundedRect, s.ToolIsShape, s.ActiveShapeKind, s.HasSelectedAnnotation);

        bool shape = s.ToolOptions.HasFlag(ToolOptions.Shape);

        // The text sections show for the Text tool AND whenever a text box is
        // selected under any tool; without the second half, clicking a text box
        // in Select mode gave no way to change its style.
        bool textSections = s.ToolOptions.HasFlag(ToolOptions.FontSize) || s.HasSelectedTextBox;

        // TextStyleSection also carries the Fill button, which applies to shapes
        // too. The alignment sub-row and Outline stay hidden for a shape: it has
        // no text to align and no outline distinct from its stroke.
        bool textStyle = textSections || s.HasSelectedShape;

        // Row 2 exists only when one of its own sections does, so a simple tool
        // stays a single row.
        bool row2 = s.ToolOptions.HasFlag(ToolOptions.FontSize) || s.HasSelectedTextBox
                 || textStyle || opacity || stamp || align;

        // A bar with everything collapsed is an empty pill floating over the
        // page, so the whole thing goes when the tool offers nothing and no
        // selection-driven section is showing.
        bool bar = s.ToolOptions != ToolOptions.None || s.HasMultiSelection;

        return new PropertyBarSections(
            Bar: bar,
            Row2: row2,
            Color: color,
            Width: width,
            Opacity: opacity,
            Stamp: stamp,
            Align: align,
            CornerRadius: cornerRadius,
            Shape: shape,
            FontSize: textSections,
            Font: textSections,
            TextStyle: textStyle,
            TextAlign: textSections,
            Outline: textSections);
    }
}
