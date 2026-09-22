using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoClicker.Core;

/// <summary>Repeats an input action on a background thread with millisecond-accurate pacing.</summary>
public sealed class Runner
{
    Thread? thread;
    volatile bool stopRequested;
    long count;

    public bool IsRunning
    {
        get { var t = thread; return t != null && t.IsAlive; }
    }

    public long Count => Interlocked.Read(ref count);

    public void Start(IInputAction action, double intervalMs, int jitterMs, long repeat)
    {
        Stop();
        stopRequested = false;
        Interlocked.Exchange(ref count, 0);
        thread = new Thread(() => Loop(action, intervalMs, jitterMs, repeat))
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
        var t = thread;
        if (t != null && t != Thread.CurrentThread)
            t.Join(1000);
    }

    void Loop(IInputAction action, double intervalMs, int jitterMs, long repeat)
    {
        // Windows' default timer resolution is ~15 ms, too coarse for short intervals.
        bool raisedTimerResolution = OperatingSystem.IsWindows() && WinMm.timeBeginPeriod(1) == 0;
        try
        {
            var rng = new Random();
            var clock = Stopwatch.StartNew();
            double next = 0;

            while (!stopRequested)
            {
                action.Perform();
                long done = Interlocked.Increment(ref count);
                if (repeat > 0 && done >= repeat) break;

                double delay = intervalMs;
                if (jitterMs > 0) delay += rng.Next(-jitterMs, jitterMs + 1);
                if (delay < 1) delay = 1;

                // If we fell far behind (e.g. a long key hold), don't burst to catch up.
                double now = clock.Elapsed.TotalMilliseconds;
                if (next < now - delay) next = now;
                next += delay;

                WaitUntil(clock, next);
            }
        }
        finally
        {
            if (raisedTimerResolution) WinMm.timeEndPeriod(1);
        }
    }

    void WaitUntil(Stopwatch clock, double target)
    {
        while (!stopRequested)
        {
            double remaining = target - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 2)
                Thread.Sleep((int)Math.Min(remaining - 1, 50));
            else
                Thread.Yield();
        }
    }

    static class WinMm
    {
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
    }
}
