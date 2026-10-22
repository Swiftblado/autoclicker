using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoClicker.Core;

/// <summary>Repeats an input action on a background thread with millisecond-accurate pacing.</summary>
public sealed class Runner
{
    Thread? thread;
    CancellationTokenSource? cancellation;
    volatile bool stopRequested;
    long count;

    public bool IsRunning
    {
        get { var t = thread; return t != null && t.IsAlive; }
    }

    public long Count => Interlocked.Read(ref count);

    /// <summary>
    /// Starts repeating <paramref name="action"/>. With a <paramref name="lag"/> governor, each cycle
    /// is stretched by its multiplier and late wake-ups are reported to it.
    /// </summary>
    public void Start(IInputAction action, double intervalMs, int jitterMs, long repeat, LagGovernor? lag = null)
    {
        Stop();
        stopRequested = false;
        cancellation = new CancellationTokenSource();
        var cancel = cancellation.Token;
        Interlocked.Exchange(ref count, 0);
        thread = new Thread(() => Loop(action, intervalMs, jitterMs, repeat, lag, cancel))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "AutoClicker.Runner"
        };
        thread.Start();
    }

    public void Stop()
    {
        stopRequested = true;
        // A macro can be mid-sequence; the token lets it abandon its remaining steps.
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var t = thread;
        if (t != null && t != Thread.CurrentThread)
            t.Join(2000);
    }

    void Loop(IInputAction action, double intervalMs, int jitterMs, long repeat, LagGovernor? lag,
        CancellationToken cancel)
    {
        RunActivity.KeepTimersPrecise();
        // Windows' default timer resolution is ~15 ms, too coarse for short intervals.
        bool raisedTimerResolution = OperatingSystem.IsWindows() && WinMm.timeBeginPeriod(1) == 0;
        try
        {
            var rng = new Random();
            var clock = Stopwatch.StartNew();
            double next = 0;

            while (!stopRequested)
            {
                double started = clock.Elapsed.TotalMilliseconds;
                action.Perform(cancel);
                if (stopRequested) break;
                long done = Interlocked.Increment(ref count);
                if (repeat > 0 && done >= repeat) break;

                double delay = intervalMs;
                if (jitterMs > 0) delay += rng.Next(-jitterMs, jitterMs + 1);
                if (delay < 1) delay = 1;
                double performMs = clock.Elapsed.TotalMilliseconds - started;

                // Slowed for lag: stretch the whole cycle, not just the gap after it. With a
                // short interval and a long action (a held click, a macro) the gap is a sliver,
                // and doubling it alone would barely slow anything.
                double Planned(double multiplier) =>
                    multiplier > 1 ? Math.Max(delay, performMs) * multiplier : delay;

                double multiplierNow = lag?.Multiplier ?? 1;

                // If we fell far behind (e.g. a long key hold), don't burst to catch up.
                double now = clock.Elapsed.TotalMilliseconds;
                if (next < now - Planned(multiplierNow)) next = now;
                double cycleStart = next;

                // Only a wait that started ahead of time can wake late; if we were already
                // behind (a long macro, say) that's the action's doing, not lag.
                bool aheadOfTime = cycleStart + Planned(multiplierNow) > now;
                bool multiplierMoved = WaitUntil(clock, cycleStart, Planned, lag, multiplierNow, out next);
                if (aheadOfTime && !multiplierMoved && !stopRequested)
                    lag?.ReportLateness(clock.Elapsed.TotalMilliseconds - next);
            }
        }
        finally
        {
            if (raisedTimerResolution) WinMm.timeEndPeriod(1);
        }
    }

    /// <summary>
    /// Waits until <paramref name="cycleStart"/> plus the planned delay. The plan is re-read as it
    /// waits, so easing off a big slowdown takes effect now rather than after the long wait it set.
    /// Returns whether the multiplier changed meanwhile; <paramref name="target"/> is where it ended.
    /// </summary>
    bool WaitUntil(Stopwatch clock, double cycleStart, Func<double, double> planned, LagGovernor? lag,
        double multiplier, out double target)
    {
        bool moved = false;
        target = cycleStart + planned(multiplier);
        while (!stopRequested)
        {
            double current = lag?.Multiplier ?? 1;
            if (current != multiplier)
            {
                multiplier = current;
                moved = true;
                target = cycleStart + planned(multiplier);
            }

            double remaining = target - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return moved;
            if (remaining > 2)
                Thread.Sleep((int)Math.Min(remaining - 1, 50));
            else
                Thread.Yield();
        }
        return moved;
    }

    static class WinMm
    {
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
    }
}
