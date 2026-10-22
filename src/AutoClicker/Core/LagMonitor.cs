using System;
using System.Diagnostics;
using System.Threading;
using AutoClicker.Input;

namespace AutoClicker.Core;

/// <summary>
/// Runs alongside a run: every <see cref="LagGovernor.WindowMs"/> it checks on the target app
/// (where the OS allows), notes whether its own sleep ran late, and has the governor judge the
/// window. Speed changes are raised on this thread.
/// </summary>
public sealed class LagMonitor(LagGovernor governor, ITargetProbe? probe)
{
    Thread? thread;
    volatile bool stopRequested;

    /// <summary>Raised on the monitor thread whenever the governor changes speed.</summary>
    public event Action<LagChange>? Changed;

    public void Start()
    {
        Stop();
        governor.Reset();
        stopRequested = false;
        thread = new Thread(Loop) { IsBackground = true, Name = "AutoClicker.LagMonitor" };
        thread.Start();
    }

    public void Stop()
    {
        stopRequested = true;
        var t = thread;
        // The probe can block for its timeout; don't hold the UI up waiting for it.
        if (t != null && t != Thread.CurrentThread) t.Join(1000);
        thread = null;
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
}
