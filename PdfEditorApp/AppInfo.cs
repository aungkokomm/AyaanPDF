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

    /// <summary>
    /// The running build's version, read from the assembly. Stamped into every
    /// file the app saves, as the producer.
    ///
    /// Read rather than declared, unlike the name above: the version already
    /// has one home in the csproj, which the installer also reads off the built
    /// exe. A second copy here would be one more place to forget on a release.
    /// </summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>What a saved file names as its producer: "Ayaan PDF 3.47.0".</summary>
    public static string Producer => $"{Name} {Version}";
}
