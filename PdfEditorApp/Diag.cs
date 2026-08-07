using System;
using System.IO;

namespace PdfEditorApp;

/// <summary>
/// Opt-in file tracing, enabled by setting PDFEDITOR_DIAG=1.
///
/// Debug.WriteLine goes nowhere without a debugger attached, and this app can
/// only be diagnosed from its INSTALLED location (a dev build has no
/// self-contained WinAppSDK runtime and never opens a window), so traces have
/// to land in a file next to the exe. This is what finally showed that
/// recycled list containers were never requesting a thumbnail render.
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();
    // Kept force-enabled for the Phase A group work so users don't have to
    // set an env var to capture traces. Revert to the env-var check before
    // shipping (see PDFEDITOR_DIAG history in git).
    private static readonly bool Enabled = true;
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }
}
