namespace PdfEditorApp;

/// <summary>
/// Product identity in one place.
///
/// The display name appears in the window title, the About dialog and the
/// installer, and those drifting apart is exactly how an app ends up called
/// three different things. The executable now carries this name too.
///
/// This is a CONSTANT, not the assembly name, and must stay one: it is also
/// the folder under %LOCALAPPDATA% holding the user's own stamp images.
/// Deriving it from the assembly would mean a rename silently moved that
/// folder, and the user's signature images would appear to have vanished.
/// </summary>
internal static class AppInfo
{
    public const string Name = "Ayaan PDF";
}
