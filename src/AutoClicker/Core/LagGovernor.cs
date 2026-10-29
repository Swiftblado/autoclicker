using System;
using System.Collections.Generic;

namespace AutoClicker.Core;

public enum LagChange
{
    None,
    /// <summary>Was running at full speed and has just been slowed.</summary>
    Slowed,
    /// <summary>Already slowed, and lag continued, so slowed again.</summary>
    SlowedFurther,
    /// <summary>Lag eased off for a while, so speed stepped back up but isn't full yet.</summary>
    SpedUp,
    /// <summary>Back to the speed the user set.</summary>
    Recovered
}

/// <summary>
/// Decides how much to slow a run down because the computer or the target app is lagging.
/// Evidence arrives from several threads (<c>Report*</c>); <see cref="Evaluate"/> is called once
/// per <see cref="WindowMs"/> to judge that window and adjust <see cref="Multiplier"/>, the factor
/// the runner stretches each cycle by.
///
/// It backs off fast and recovers slowly: two bad windows in a row halve the speed (or quarter
/// it, when the target has frozen outright), and <see cref="GoodWindowsToSpeedUp"/> good ones in a
/// row double it again, one step at a time. It remembers the speed that caused the lag, and
/// waits much longer before trying that speed again, so it doesn't bounce in and out of lag.
/// There's no floor on how slow it may go.
/// </summary>
public sealed class LagGovernor
{
    public const int WindowMs = 250;

    /// <summary>Consecutive bad windows (~0.5 s) before slowing. One spike isn't lag.</summary>
    const int BadWindowsToSlow = 2;

    /// <summary>
    /// Once slowed, a backed-up target takes a moment to work through what it already received;
    /// waiting a little longer before slowing again keeps that from piling on extra slow-downs.
    /// </summary>
    const int BadWindowsToSlowFurther = 4;

    /// <summary>Consecutive good windows (~3 s) before each step back up.</summary>
    public const int GoodWindowsToSpeedUp = 12;

    /// <summary>Consecutive good windows (~30 s) before retrying the speed that last caused lag.</summary>
    public const int GoodWindowsToRetry = 120;

    /// <summary>Slow-downs this close together (~3 s) are one lag episode.</summary>
    const int EpisodeGapWindows = 12;

    const double SlowFactor = 2;
    const double FrozenSlowFactor = 4;

    /// <summary>Stops the multiplier growing past what a double can schedule sensibly.</summary>
    const double MaxMultiplier = 1e9;

    // A wake-up later than this is a sign the runner thread is being starved of CPU. Kept above
    // a 15.6 ms timer tick so coarse OS timers alone never count as lag.
    const double LateMs = 25;
    const double VeryLateMs = 100;

    // Handing an event to the OS normally takes microseconds; this long means its input
    // queue is backed up.
    const double SlowSendMs = 10;
    const double VerySlowSendMs = 100;

    // The target app took this long to answer "are you there?" — far slower than any app
    // that's keeping up, even a game at a low frame rate.
    const double SlowTargetMs = 150;

    // The monitor thread itself woke this late from its own sleep.
    const double MonitorLateMs = 50;

    // Screen: the patch around the click point counts as animated when it changed in at least
    // this share of looks recently. Lag is when it then drops to a quarter of that, and a freeze
    // when it drops to a tenth. A patch that never animates says nothing either way.
    const double LivelyShare = 0.25;
    const double StalledRatio = 0.25;
    const double FrozenRatio = 0.10;
    const int MinScreenSamples = 4;
    /// <summary>How many recent healthy windows (~3 s) set what "animated" looks like.</summary>
    const int LivelinessWindows = 12;
    /// <summary>Forget the target was animated after this long (~5 s) without it looking animated,
    /// e.g. a game that went to a still screen.</summary>
    const int LivelinessExpiryWindows = 20;

    readonly object gate = new();
    readonly List<double> lateness = [];
    readonly List<double> sends = [];
    double worstTargetMs;
    double worstMonitorLateMs;
    int screenChanges, screenSamples;
    readonly Queue<double> recentLiveliness = new();
    int windowsSinceLively = int.MaxValue;

    int badStreak;
    int goodStreak;
    double multiplier = 1;
    int windowsSinceSlowed = int.MaxValue;
    /// <summary>The multiplier that last ran into lag; 0 when none has.</summary>
    double laggedAt;

    /// <summary>1 at full speed; 2 means each cycle takes twice as long, and so on.</summary>
    public double Multiplier { get { lock (gate) return multiplier; } }

    /// <summary>Plain-words cause of the most recent slow-down, for the notification.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>How late the runner woke for a scheduled action, in ms.</summary>
    public void ReportLateness(double ms) { lock (gate) lateness.Add(ms); }

    /// <summary>How long one call into the OS input API took, excluding deliberate holds.</summary>
    public void ReportSend(double ms) { lock (gate) sends.Add(ms); }

    /// <summary>How long the target app took to respond; a timeout reports the timeout.</summary>
    public void ReportTargetResponse(double ms) { lock (gate) worstTargetMs = Math.Max(worstTargetMs, ms); }

    /// <summary>How late the monitor woke from its own fixed sleep.</summary>
    public void ReportMonitorLateness(double ms) { lock (gate) worstMonitorLateMs = Math.Max(worstMonitorLateMs, ms); }

    /// <summary>One look at the screen around the click point: did it change since the last look?</summary>
    public void ReportScreen(bool changed)
    {
        lock (gate)
        {
            screenSamples++;
            if (changed) screenChanges++;
        }
    }

    public LagChange Evaluate()
    {
        lock (gate)
        {
            var (reason, frozen) = JudgeWindow();
            ClearWindow();
            if (windowsSinceSlowed != int.MaxValue) windowsSinceSlowed++;

            if (reason != null)
            {
                goodStreak = 0;
                if (++badStreak < (multiplier > 1 ? BadWindowsToSlowFurther : BadWindowsToSlow)) return LagChange.None;

                badStreak = 0;
                // Remember the speed that caused this episode, not every step taken while the
                // target worked through its backlog.
                if (windowsSinceSlowed > EpisodeGapWindows) laggedAt = multiplier;
                windowsSinceSlowed = 0;

                bool wasFull = multiplier <= 1;
                multiplier = Math.Min(multiplier * (frozen ? FrozenSlowFactor : SlowFactor), MaxMultiplier);
                Reason = reason;
                return wasFull ? LagChange.Slowed : LagChange.SlowedFurther;
            }

            badStreak = 0;
            if (multiplier <= 1) return LagChange.None;

            double next = multiplier / SlowFactor;
            bool retryingLaggedSpeed = laggedAt > 0 && next <= laggedAt * 1.0001;
            if (++goodStreak < (retryingLaggedSpeed ? GoodWindowsToRetry : GoodWindowsToSpeedUp)) return LagChange.None;

            goodStreak = 0;
            multiplier = next;
            if (multiplier > 1.0001) return LagChange.SpedUp;
            multiplier = 1;
            return LagChange.Recovered;
        }
    }

    /// <summary>
    /// What looked wrong in this window, or null if it looked healthy; and whether the target
    /// looks frozen outright, which warrants a bigger slow-down.
    /// </summary>
    (string? Reason, bool Frozen) JudgeWindow()
    {
        if (ScreenStalled(out bool frozen))
            return ("the app you're automating has stopped keeping up on screen", frozen);
        if (worstTargetMs >= SlowTargetMs)
            return ("the app you're automating is responding slowly", false);
        if (Troubled(sends, SlowSendMs, VerySlowSendMs))
            return ("your computer is slow to accept input", false);
        if (Troubled(lateness, LateMs, VeryLateMs) || worstMonitorLateMs >= MonitorLateMs)
            return ("your computer is falling behind", false);
        return (null, false);
    }

    /// <summary>
    /// Compares this window's share of changed looks with how lively the patch has recently been.
    /// Healthy windows feed that baseline; stalled ones don't, so a long freeze can't redefine normal.
    /// </summary>
    bool ScreenStalled(out bool frozen)
    {
        frozen = false;
        if (screenSamples < MinScreenSamples) return false;
        double share = (double)screenChanges / screenSamples;

        if (windowsSinceLively != int.MaxValue && ++windowsSinceLively > LivelinessExpiryWindows)
        {
            recentLiveliness.Clear();
            windowsSinceLively = int.MaxValue;
        }

        double lively = 0;
        foreach (double s in recentLiveliness) lively = Math.Max(lively, s);

        if (lively >= LivelyShare && share <= lively * StalledRatio)
        {
            frozen = share <= lively * FrozenRatio;
            return true;
        }

        recentLiveliness.Enqueue(share);
        while (recentLiveliness.Count > LivelinessWindows) recentLiveliness.Dequeue();
        if (share >= LivelyShare) windowsSinceLively = 0;
        return false;
    }

    /// <summary>
    /// At least half the samples over <paramref name="slow"/>, or any one over <paramref name="verySlow"/>.
    /// A slowed run may send only one event per window, so one slow sample can be enough; a lone
    /// hiccup is still filtered out because it takes two bad windows in a row to slow down.
    /// </summary>
    static bool Troubled(List<double> samples, double slow, double verySlow)
    {
        int over = 0;
        foreach (double ms in samples)
        {
            if (ms >= verySlow) return true;
            if (ms >= slow) over++;
        }
        return over > 0 && over * 2 >= samples.Count;
    }

    void ClearWindow()
    {
        lateness.Clear();
        sends.Clear();
        worstTargetMs = worstMonitorLateMs = 0;
        screenChanges = screenSamples = 0;
    }

    public void Reset()
    {
        lock (gate)
        {
            ClearWindow();
            recentLiveliness.Clear();
            windowsSinceLively = int.MaxValue;
            badStreak = goodStreak = 0;
            multiplier = 1;
            windowsSinceSlowed = int.MaxValue;
            laggedAt = 0;
            Reason = "";
        }
    }
}
