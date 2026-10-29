using System;
using System.Diagnostics;
using System.Threading;
using AutoClicker.Input;
using Avalonia;

namespace AutoClicker.Core;

/// <summary>
/// Runs alongside a run: every <see cref="LagGovernor.WindowMs"/> it checks on the target app
/// (where the OS allows), notes whether its own sleep ran late, and has the governor judge the
/// window. A second thread keeps looking at the screen around the cursor, which is where the
/// clicks land, to see whether the target is still drawing. Speed changes are raised on the
/// monitor thread.
/// </summary>
public sealed class LagMonitor(
    LagGovernor governor, ITargetProbe? probe, Func<IScreenProbe?> screenProbe, Func<PixelPoint> cursor)
{
    /// <summary>How long to watch the screen before the first click, to learn what "healthy" looks like.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(400);

    /// <summary>~33 looks a second: enough to see a 30 fps game drawing, cheap enough to run all the time.</summary>
    const int ScreenIntervalMs = 30;

    Thread? thread, screenThread;
    volatile bool stopRequested;
    readonly Stopwatch running = new();

    /// <summary>Raised on the monitor thread whenever the governor changes speed.</summary>
    public event Action<LagChange>? Changed;

    public bool IsRunning => thread is { IsAlive: true };

    /// <summary>How long the monitor has been watching, for the warm-up before the first click.</summary>
    public TimeSpan WatchingFor => IsRunning ? running.Elapsed : TimeSpan.Zero;

    public void Start()
    {
        Stop();
        governor.Reset();
        stopRequested = false;
        running.Restart();
        thread = new Thread(Loop) { IsBackground = true, Name = "AutoClicker.LagMonitor" };
        screenThread = new Thread(WatchScreen) { IsBackground = true, Name = "AutoClicker.ScreenWatch" };
        thread.Start();
        screenThread.Start();
    }

    public void Stop()
    {
        stopRequested = true;
        // The probe can block for its timeout; don't hold the UI up waiting for it.
        foreach (var t in new[] { thread, screenThread })
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
        thread = screenThread = null;
        running.Reset();
    }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        while (!stopRequested)
        {
            double before = clock.Elapsed.TotalMilliseconds;
            Thread.Sleep(LagGovernor.WindowMs);
            if (stopRequested) break;
            governor.ReportMonitorLateness(clock.Elapsed.TotalMilliseconds - before - LagGovernor.WindowMs);

            if (probe?.Measure() is { } targetMs) governor.ReportTargetResponse(targetMs);
            if (stopRequested) break;

            var change = governor.Evaluate();
            if (change != LagChange.None) Changed?.Invoke(change);
        }
    }

    void WatchScreen()
    {
        IScreenProbe? screen;
        try { screen = screenProbe(); }
        catch (Exception) { return; }
        if (screen == null) return;

        try
        {
            PixelPoint? last = null;
            while (!stopRequested)
            {
                var at = cursor();
                bool? changed = screen.Changed(at);
                // Only a look from the same spot as the last one says anything: when the cursor
                // moves, the patch changes because it shows somewhere else.
                if (changed is { } c && last == at) governor.ReportScreen(c);
                last = at;
                Thread.Sleep(ScreenIntervalMs);
            }
        }
        catch (Exception) { /* capture broke (e.g. display went away); carry on without it */ }
        finally
        {
            screen.Dispose();
        }
    }
}
