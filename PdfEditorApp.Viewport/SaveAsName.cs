using System;
using System.IO;

namespace PdfEditorApp.Viewport;

/// <summary>
/// What Save As offers to call the copy.
/// </summary>
/// <remarks>
/// ⚠️ IT USED TO OFFER "edited" FOR EVERY FILE, so each copy had to be renamed
/// by hand and two of them made in a row collided. The reader asked for the
/// original's own name with "_1" after it. A copy already there with that
/// number is stepped past, so the name offered is one nothing is called yet.
/// </remarks>
public static class SaveAsName
{
    /// <summary>
    /// The name, without ".pdf", to offer for a copy of
    /// <paramref name="documentPath"/>.
    /// </summary>
    /// <param name="documentPath">The open file, or null for one never saved.</param>
    /// <param name="flatten">Whether the copy is the flattened one.</param>
    /// <param name="exists">Whether a file at a path already exists.</param>
    public static string For(string? documentPath, bool flatten, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return flatten ? "flattened" : "edited";
        }

        string name = Path.GetFileNameWithoutExtension(documentPath);
        if (flatten)
        {
            return $"{name}_flattened";
        }

        string folder = Path.GetDirectoryName(documentPath) ?? string.Empty;
        int n = 1;
        while (n < 1000 && exists(Path.Combine(folder, $"{name}_{n}.pdf")))
        {
            n++;
        }
        return $"{name}_{n}";
    }
}
