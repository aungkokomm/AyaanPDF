namespace PdfEditorApp;

/// <summary>
/// Product identity in one place.
///
/// The display name appears in the window title, the About dialog and the
/// installer, and those drifting apart is exactly how an app ends up called
/// three different things. The assembly and executable keep their original
/// names deliberately: renaming those churns the manifest, the resource index
/// and every installer path for something no one sees outside Task Manager.
/// </summary>
internal static class AppInfo
{
    public const string Name = "Ayaan PDF";
}
