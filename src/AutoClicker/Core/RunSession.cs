using System;
using AutoClicker.Input;
using Avalonia;

namespace AutoClicker.Core;

/// <summary>
/// One run with its own lag protection: the window's setup is one session, and each key binding
/// has another, so several can run at once and each slows down for lag on its own.
/// </summary>
public sealed class RunSession
{
    readonly IInputBackend backend;
    readonly Runner runner = new();
    readonly LagGovernor governor = new();
    readonly LagMonitor monitor;
    IDisposable? activity;

    public RunSession(IInputBackend backend, ITargetProbe? probe, Func<IScreenProbe?> screenProbe, Func<PixelPoint> cursor)
    {
        this.backend = backend;
        monitor = new LagMonitor(governor, probe, screenProbe, cursor);
        monitor.Changed += change => LagChanged?.Invoke(this, change);
    }

    /// <summary>Raised on the monitor thread whenever lag protection changes speed.</summary>
    public event Action<RunSession, LagChange>? LagChanged;

    public bool IsRunning => runner.IsRunning;
    public long Count => runner.Count;
    public double Multiplier => governor.Multiplier;
    public string LagReason => governor.Reason;

    /// <summary>The setup being run; null before the first start.</summary>
    public RunSetup? Setup { get; private set; }

    /// <summary>The macro being run, for its step counter; null for other modes.</summary>
    public MacroAction? Macro { get; private set; }

    /// <summary>Whether this run is currently slowed for lag. Kept by whoever handles <see cref="LagChanged"/>.</summary>
    public bool Slowed { get; set; }

    /// <summary>
    /// Starts watching for lag ahead of <see cref="Start"/>, e.g. during a countdown, so the warm-up
    /// before the first click has already been done by then.
    /// </summary>
    public void WatchAhead() => monitor.Start();

    /// <param name="minDelay">Hold the first action back at least this long.</param>
    public void Start(RunSetup setup, TimeSpan minDelay = default)
    {
        Stop();
        Setup = setup;
        // With the lag check on, every send is timed: a slow one is a sign of lag.
        var action = setup.Build(setup.LagCheck ? new TimedInputBackend(backend, governor.ReportSend) : backend);
        Macro = action as MacroAction;
        Slowed = false;

        // The lag monitor watches the target briefly before the first click (a countdown
        // usually covers that already).
        var delay = TimeSpan.Zero;
        if (setup.LagCheck)
        {
            if (!monitor.IsRunning) monitor.Start();
            delay = LagMonitor.WarmUp - monitor.WatchingFor;
        }
        else monitor.Stop();
        if (delay < minDelay) delay = minDelay;

        activity = RunActivity.Begin();
        runner.Start(action, setup.IntervalMs, setup.JitterMs, setup.Repeat, setup.LagCheck ? governor : null, delay);
    }

    /// <summary>Stops the run, or tidies up after one that finished its repeat count. Safe to call twice.</summary>
    public void Stop()
    {
        runner.Stop();
        monitor.Stop();
        activity?.Dispose();
        activity = null;
        Macro = null;
    }
}
