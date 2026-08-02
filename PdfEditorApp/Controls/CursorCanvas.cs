using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PdfEditorApp.Controls;

/// <summary>
/// Canvas subclass with a public cursor setter, same story as CursorGrid:
/// UIElement.ProtectedCursor can only be reached from a subclass. Used by
/// the top/left rulers so hovering them can show a "pull-a-guide-out" hand
/// cursor rather than the default arrow.
/// </summary>
public sealed class CursorCanvas : Canvas
{
    public void SetCursorShape(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
    public void ClearCursor() => ProtectedCursor = null;
}
