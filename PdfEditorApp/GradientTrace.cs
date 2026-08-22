using System.Collections.Generic;

namespace PdfEditorApp;

/// <summary>
/// TEMPORARY diagnostic for the gradient live-editing verification. Remove once
/// the behaviour is confirmed on screen.
///
/// Collapses a run of identical messages into one line and says how many were
/// suppressed when the message finally changes. The first pass at this logged
/// every call and produced 170 identical lines for one gesture; the pass before
/// that deduped silently and hid whether the call happened again at all. This
/// keeps both facts and stays short.
/// </summary>
internal static class GradientTrace
{
    private sealed class Run
    {
        public string Message = string.Empty;
        public int Repeats;
    }

    private static readonly Dictionary<string, Run> Last = new();

    public static void Once(string key, string message)
    {
        int suppressed;

        lock (Last)
        {
            if (!Last.TryGetValue(key, out var run))
            {
                Last[key] = new Run { Message = message };
                suppressed = 0;
            }
            else if (run.Message == message)
            {
                run.Repeats++;
                return;
            }
            else
            {
                suppressed = run.Repeats;
                run.Message = message;
                run.Repeats = 0;
            }
        }

        Diag.Log(
            suppressed > 0
                ? $"GTRACE {key} (previous line x{suppressed + 1}): {message}"
                : $"GTRACE {key}: {message}");
    }
}
