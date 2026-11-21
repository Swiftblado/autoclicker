using System;
using System.Diagnostics;
using System.Threading;

namespace AutoClicker.Core;

/// <summary>
/// Pause, step and go-to-line for a run, pressed from the window or a key while the run goes on
/// its own thread. A run is only ever held between lines (or inside a Wait), never halfway
/// through a click, drag or key press, so nothing is left held down.
/// </summary>
public sealed class RunControl
{
    readonly object gate = new();
    readonly ManualResetEventSlim wake = new(false);
    readonly Stopwatch clock = Stopwatch.StartNew();
    bool paused;
    int steps;
    int? jump;
    double resumeAtMs;

    /// <summary>
    /// The line currently allowed to run while paused, because Step was pressed for it. Only
    /// touched by the run's own thread.
    /// </summary>
    bool stepping;

    /// <summary>
    /// Run thread: how long it has spent held by pause or a resume delay, in ms, so loop timers
    /// (a While's limit, a timed For) don't count time spent paused.
    /// </summary>
    public double HeldMs { get; private set; }

    public bool IsPaused { get { lock (gate) return paused; } }

    /// <summary>How long until a resume takes effect, in ms; 0 when not waiting to resume.</summary>
    public double ResumeLeftMs
    {
        get { lock (gate) return paused ? 0 : Math.Max(0, resumeAtMs - clock.Elapsed.TotalMilliseconds); }
    }

    public void Pause() => Change(() => { paused = true; steps = 0; });

    /// <summary>Carries on after <paramref name="delayMs"/>; pressing Pause again in that time stays paused.</summary>
    public void Resume(int delayMs) => Change(() =>
    {
        paused = false;
        steps = 0;
        resumeAtMs = clock.Elapsed.TotalMilliseconds + Math.Max(0, delayMs);
    });

    /// <summary>While paused: lets exactly one more line run. Does nothing otherwise.</summary>
    public void Step() => Change(() => { if (paused) steps++; });

    /// <summary>Carries on from line <paramref name="index"/> (counted from 0), cutting short any Wait in progress.</summary>
    public void JumpTo(int index) => Change(() => jump = index);

    /// <summary>Back to running, with nothing pending; for a fresh start.</summary>
    public void Reset() => Change(() => { paused = false; steps = 0; jump = null; resumeAtMs = 0; });

    void Change(Action change)
    {
        lock (gate)
        {
            change();
            wake.Set();
        }
    }

    /// <summary>Takes a pending go-to-line, if there is one.</summary>
    public int? TakeJump()
    {
        lock (gate)
        {
            var target = jump;
            jump = null;
            return target;
        }
    }

    /// <summary>
    /// Run thread, before each line: holds while paused (until Resume, or Step lets this one line
    /// through) and through a resume delay. Returns false when a go-to-line is waiting instead,
    /// or the run was stopped.
    /// </summary>
    public bool BeforeLine(CancellationToken cancel)
    {
        stepping = false;
        double started = clock.Elapsed.TotalMilliseconds;
        try { return HoldBeforeLine(cancel); }
        finally { HeldMs += clock.Elapsed.TotalMilliseconds - started; }
    }

    bool HoldBeforeLine(CancellationToken cancel)
    {
        while (true)
        {
            if (cancel.IsCancellationRequested) return false;
            int waitMs;
            lock (gate)
            {
                wake.Reset();
                if (jump != null) return false;
                if (paused)
                {
                    if (steps > 0)
                    {
                        steps--;
                        stepping = true;
                        return true;
                    }
                    waitMs = Timeout.Infinite;
                }
                else
                {
                    double left = resumeAtMs - clock.Elapsed.TotalMilliseconds;
                    if (left <= 0) return true;
                    waitMs = (int)Math.Ceiling(left);
                }
            }
            WaitHandle.WaitAny([wake.WaitHandle, cancel.WaitHandle], waitMs);
        }
    }

    /// <summary>
    /// Run thread: waits <paramref name="ms"/> of running time. Time spent paused (or waiting to
    /// resume) doesn't count, so a Wait picks up where it left off. Returns false when cut short
    /// by a go-to-line or by stopping.
    /// </summary>
    public bool Sleep(int ms, CancellationToken cancel)
    {
        double left = ms;
        double last = clock.Elapsed.TotalMilliseconds;
        // Whether the time since `last` was running time. It's decided when each stretch starts,
        // since a pause or resume pressed during it only takes effect from then on.
        bool counted = true;
        while (true)
        {
            if (cancel.IsCancellationRequested) return false;
            int waitMs;
            lock (gate)
            {
                wake.Reset();
                if (jump != null) return false;
                double now = clock.Elapsed.TotalMilliseconds;
                if (counted) left -= now - last;
                else HeldMs += now - last;
                last = now;
                if (counted && left <= 0) return true;

                if (paused && !stepping && steps > 0)
                {
                    // Step pressed while this line was frozen: let it finish.
                    steps--;
                    stepping = true;
                }
                bool counting = (!paused || stepping) && now >= resumeAtMs;
                counted = counting;
                if (counting && left <= 0) return true;
                waitMs = counting ? (int)Math.Ceiling(left)
                    : paused && !stepping ? Timeout.Infinite
                    : (int)Math.Ceiling(resumeAtMs - now);
            }
            WaitHandle.WaitAny([wake.WaitHandle, cancel.WaitHandle], waitMs == Timeout.Infinite ? Timeout.Infinite : Math.Max(1, waitMs));
        }
    }
}
