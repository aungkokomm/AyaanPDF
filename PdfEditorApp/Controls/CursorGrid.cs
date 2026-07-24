using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PdfEditorApp.Controls;

/// <summary>
/// Plain Grid that exposes cursor changes publicly. UIElement.ProtectedCursor
/// is, as the name says, `protected` — only a subclass can set it, so the
/// viewport host needs to be one to let MainPage swap in the hand cursor for
/// Phase 1's Space-hand tool.
/// </summary>
public sealed class CursorGrid : Grid
{
    public void SetCursorShape(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
