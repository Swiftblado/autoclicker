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
/// It backs off fast and recovers slowly: two bad windows in a row halve the speed, and
/// <see cref="GoodWindowsToSpeedUp"/> good ones in a row double it again, one step at a time.
/// There's no floor on how slow it may go.
/// </summary>
public sealed class LagGovernor
{
    public const int WindowMs = 250;

    /// <summary>Consecutive bad windows (~0.5 s) before slowing. One spike isn't lag.</summary>
    const int BadWindowsToSlow = 2;

    /// <summary>Consecutive good windows (~3 s) before each step back up.</summary>
    public const int GoodWindowsToSpeedUp = 12;

    const double SlowFactor = 2;

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

    readonly object gate = new();
    readonly List<double> lateness = [];
    readonly List<double> sends = [];
    double worstTargetMs;
    double worstMonitorLateMs;

    int badStreak;
    int goodStreak;
    double multiplier = 1;

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

    public LagChange Evaluate()
    {
        lock (gate)
        {
            string? reason = JudgeWindow();
            lateness.Clear();
            sends.Clear();
            worstTargetMs = 0;
            worstMonitorLateMs = 0;

            if (reason != null)
            {
                goodStreak = 0;
                if (++badStreak < BadWindowsToSlow) return LagChange.None;

                badStreak = 0;
                bool wasFull = multiplier <= 1;
                multiplier = Math.Min(multiplier * SlowFactor, MaxMultiplier);
                Reason = reason;
                return wasFull ? LagChange.Slowed : LagChange.SlowedFurther;
            }

            badStreak = 0;
            if (multiplier <= 1) return LagChange.None;
            if (++goodStreak < GoodWindowsToSpeedUp) return LagChange.None;

            goodStreak = 0;
            multiplier /= SlowFactor;
            if (multiplier > 1.0001) return LagChange.SpedUp;
            multiplier = 1;
            return LagChange.Recovered;
        }
    }

    /// <summary>Null when this window looked healthy; otherwise what looked wrong.</summary>
    string? JudgeWindow()
    {
        if (worstTargetMs >= SlowTargetMs)
            return "the app you're automating is responding slowly";
        if (Troubled(sends, SlowSendMs, VerySlowSendMs))
            return "your computer is slow to accept input";
        if (Troubled(lateness, LateMs, VeryLateMs) || worstMonitorLateMs >= MonitorLateMs)
            return "your computer is falling behind";
        return null;
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

    public void Reset()
    {
        lock (gate)
        {
            lateness.Clear();
            sends.Clear();
            worstTargetMs = worstMonitorLateMs = 0;
            badStreak = goodStreak = 0;
            multiplier = 1;
            Reason = "";
        }
    }
}
