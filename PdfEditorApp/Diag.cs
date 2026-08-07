using System;
using System.IO;

namespace PdfEditorApp;

/// <summary>
/// File tracing, written next to the exe as diag.log.
///
/// Debug.WriteLine goes nowhere without a debugger attached, and this app can
/// only be diagnosed from its INSTALLED location (a dev build has no
/// self-contained WinAppSDK runtime and never opens a window), so traces have
/// to land in a file next to the exe. This is what finally showed that
/// recycled list containers were never requesting a thumbnail render, and
/// later that a group move was never calling the shape rebuild at all.
///
/// Deliberately ALWAYS ON, not gated behind an environment variable. A user
/// reporting "it did the wrong thing" cannot be asked to reproduce it a
/// second time with tracing switched on; the log has to already exist. The
/// costs that used to justify an off switch are handled here instead: the
/// file is capped so it cannot grow without bound, and callers are expected
/// to keep per-item logging out of hot loops.
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();

    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "diag.log");

    /// <summary>Roll the log once it passes this. Two megabytes is far more
    /// than any single session produces, and small enough to attach to a bug
    /// report.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>The previous log, kept so a roll mid-session does not throw
    /// away the part that explains what went wrong.</summary>
    private static string PreviousPath => Path + ".1";

    /// <summary>Bytes written since the last size check. Checking the file
    /// length on every single call would put a stat syscall in front of every
    /// trace line, so it is only consulted periodically.</summary>
    private static long _sinceCheck;

    private const long CheckEvery = 64 * 1024;

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                _sinceCheck += line.Length;
                if (_sinceCheck >= CheckEvery)
                {
                    _sinceCheck = 0;
                    RollIfTooBig();
                }

                File.AppendAllText(Path, line);
            }
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }

    /// <summary>Moves the log aside once it exceeds the cap, so at most two
    /// files exist and the newest is always the one being written.</summary>
    private static void RollIfTooBig()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            File.Delete(PreviousPath);
            File.Move(Path, PreviousPath);
        }
        catch
        {
            // A locked or missing file is not worth failing a trace over.
        }
    }
}
