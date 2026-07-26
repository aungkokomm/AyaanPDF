using System;
using System.IO;

namespace PdfEditorApp;

/// <summary>
/// Writes crashes to a file, always.
///
/// Deliberately NOT gated behind the PDFEDITOR_DIAG switch that <see cref="Diag"/>
/// uses. Tracing is opt-in because it is noisy and only wanted while
/// investigating something; a crash is the one event that can never be
/// reproduced on demand, and by the time anyone thinks to turn on a switch the
/// evidence is gone. Every crash in this app so far has been visible only as a
/// Windows dialog with a hex code in it, which names neither the exception nor
/// the line.
///
/// Kept small and exception-proof: recording a crash must never cause one.
/// </summary>
internal static class Crash
{
    private static readonly object Gate = new();

    /// <summary>Where crashes are written: crash.log, beside the exe.</summary>
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");

    public static void Record(string origin, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path,
                    $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {origin} ==={Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Nothing useful is left to do if even this fails.
        }
    }
}
