using System;

namespace PdfEditorApp.Viewport;

/// <summary>
/// The version arithmetic behind the update check, kept apart from the network
/// so it can be tested.
/// </summary>
public static class UpdateVersions
{
    /// <summary>A release tag as a plain version: "v3.51.5" becomes "3.51.5".</summary>
    public static string Normalize(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    /// <summary>
    /// Whether <paramref name="latest"/> is a later version than
    /// <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ FALSE WHEN EITHER CANNOT BE READ. A tag nobody can compare, or a build
    /// that reports "unknown", is not a reason to tell the reader to update.
    /// </remarks>
    public static bool IsNewer(string latest, string current) =>
        TryParse(latest, out var a) && TryParse(current, out var b) && a > b;

    /// <summary>
    /// "3.51" and "3.51.0" are the same release, so a short version is padded
    /// to three parts before it is compared.
    /// </summary>
    private static bool TryParse(string text, out Version version)
    {
        string padded = text.Split('.').Length switch
        {
            1 => text + ".0.0",
            2 => text + ".0",
            _ => text,
        };
        return Version.TryParse(padded, out version!);
    }
}
