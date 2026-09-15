using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace PdfEditorApp;

/// <summary>
/// Says what the UI thread was doing when it froze, in diag.log.
/// </summary>
/// <remarks>
/// ⚠️ WRITTEN BECAUSE THE LOG COULD ONLY SAY THAT IT FROZE. Scrolling from
/// page 743 to 744 of a 39881-page book took about 40 seconds: the renders
/// themselves took milliseconds, but the next one was not even asked for until
/// 12 to 35 seconds later, and the reading-position save that fires a second
/// after scrolling stops did not fire for 84 seconds. Nothing in the log said
/// which code was holding the thread.
///
/// Two halves. A timer on the UI thread beats every 100 ms, and a background
/// thread reports when the beat stops, naming the section the UI thread is in
/// at that moment; it reports again at 2, 4, 8 seconds so a long freeze shows
/// whether it stayed in one place. Sections are opened at the top of the
/// handlers worth suspecting, and one that takes 100 ms or more says so when it
/// ends. Only open sections in code that does not await: a section held across
/// an await would name the wrong code while other work runs in between.
/// </remarks>
internal static class UiStall
{
    private const string Outside = "outside any timed section";
    private const int BeatMs = 100;
    private const int StallMs = 1000;
    private const double SlowSectionMs = 100;

    private static volatile string _where = Outside;
    private static long _lastBeat = Environment.TickCount64;
    private static int _started;

    /// <summary>Held so the timer is not collected, which would read as a stall.</summary>
    private static DispatcherQueueTimer? _beat;

    public static void Start(DispatcherQueue queue)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _beat = queue.CreateTimer();
        _beat.Interval = TimeSpan.FromMilliseconds(BeatMs);
        _beat.IsRepeating = true;
        _beat.Tick += (_, _) => Interlocked.Exchange(ref _lastBeat, Environment.TickCount64);
        _beat.Start();

        new Thread(Watch) { IsBackground = true, Name = "UI stall watch" }.Start();
    }

    /// <summary>Marks the UI thread as inside <paramref name="name"/> until the scope is disposed.</summary>
    public static Scope Section(string name)
    {
        string outer = _where;
        _where = ReferenceEquals(outer, Outside) ? name : outer + " > " + name;
        return new Scope(outer, name, Stopwatch.GetTimestamp());
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string _outer;
        private readonly string _name;
        private readonly long _start;

        internal Scope(string outer, string name, long start)
        {
            _outer = outer;
            _name = name;
            _start = start;
        }

        public void Dispose()
        {
            _where = _outer;
            double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= SlowSectionMs)
            {
                Diag.Log($"slow: {_name} took {ms:F0} ms");
            }
        }
    }

    private static void Watch()
    {
        long stalledSince = 0;
        long nextReport = StallMs;

        while (true)
        {
            Thread.Sleep(250);
            long beat = Interlocked.Read(ref _lastBeat);
            long gap = Environment.TickCount64 - beat;

            if (gap >= nextReport)
            {
                stalledSince = beat;
                Diag.Log($"UI STALL {gap} ms so far, in: {_where}");
                nextReport *= 2;
            }
            else if (gap < StallMs && stalledSince != 0)
            {
                Diag.Log($"UI back after about {beat - stalledSince} ms");
                stalledSince = 0;
                nextReport = StallMs;
            }
        }
    }
}
