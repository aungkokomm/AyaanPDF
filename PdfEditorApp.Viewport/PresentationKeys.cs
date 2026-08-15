namespace PdfEditorApp.Viewport;

/// <summary>What a key press means for full-screen reading.</summary>
public enum PresentationAction
{
    None,
    Enter,
    Exit,
}

/// <summary>
/// Which keys enter and leave full screen.
///
/// Here rather than in the key handler because Escape is the hazard. It
/// already cancels a drag, dismisses the text editor and clears the selection,
/// and a full-screen exit that swallowed it would break all three everywhere in
/// the app. The rule is that Escape means "leave full screen" ONLY while full
/// screen, and otherwise is not ours at all.
/// </summary>
public static class PresentationKeys
{
    /// <summary>Standard Windows virtual-key codes, passed as ints so this
    /// library stays free of WinUI types.</summary>
    public const int KeyEscape = 0x1B;

    public const int KeyF11 = 0x7A;

    /// <summary>
    /// What this key press should do.
    /// </summary>
    /// <param name="isFullScreen">Whether the window is already presenting.</param>
    /// <param name="textFocused">
    /// Whether a text field has focus. Escape belongs to the field then, and so
    /// does F11: someone typing has not asked to change the window.
    /// </param>
    public static PresentationAction Resolve(int keyCode, bool isFullScreen, bool textFocused)
    {
        if (textFocused)
        {
            return PresentationAction.None;
        }

        return keyCode switch
        {
            // F11 toggles, which is what every browser and reader does.
            KeyF11 => isFullScreen ? PresentationAction.Exit : PresentationAction.Enter,

            // Escape only ever LEAVES. It never enters, and while not
            // presenting it is not ours: the canvas needs it.
            KeyEscape when isFullScreen => PresentationAction.Exit,

            _ => PresentationAction.None,
        };
    }
}

/// <summary>
/// The chrome a page was showing before it went full screen, so leaving puts
/// back exactly what was there.
///
/// Captured rather than assumed. Restoring a fixed set would turn the rulers on
/// for someone who had them off, and reopen a panel they had closed, which is
/// the sort of thing that makes a mode feel like it broke something.
/// </summary>
/// <param name="Rulers">Rulers were visible.</param>
/// <param name="Thumbnails">The pages panel was open.</param>
/// <param name="Bookmarks">The bookmarks panel was open.</param>
public readonly record struct ChromeState(bool Rulers, bool Thumbnails, bool Bookmarks)
{
    /// <summary>Nothing showing, which is what presenting looks like.</summary>
    public static ChromeState Hidden => new(false, false, false);
}
