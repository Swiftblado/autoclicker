using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Core;

/// <summary>Runs every step of a <see cref="Macro"/> once, in order.</summary>
public sealed class MacroAction : IInputAction
{
    static readonly Modifiers[] ModifierOrder = [Modifiers.Control, Modifiers.Shift, Modifiers.Alt, Modifiers.Meta];

    readonly IInputBackend backend;
    readonly List<MacroStep> steps;
    readonly RunControl control;
    readonly LagGovernor? lag;
    int currentStepIndex = -1;

    /// <summary>
    /// Index of the step currently executing (or held before, while paused), or -1 between passes
    /// and when not running. Written by the run thread, read by the UI thread.
    /// </summary>
    public int CurrentStepIndex => Volatile.Read(ref currentStepIndex);

    /// <param name="control">Pause, step and go-to-line from outside; a fresh one when not given.</param>
    /// <param name="lag">Told when the macro is only waiting or looking, so lag protection doesn't judge those times.</param>
    public MacroAction(IInputBackend backend, Macro macro, RunControl? control = null, LagGovernor? lag = null)
    {
        this.backend = backend;
        this.control = control ?? new RunControl();
        this.lag = lag;
        // Snapshot so edits made in the UI while this runs can't mutate what's executing.
        steps = new List<MacroStep>(macro.Steps);
        blocks = MacroBlocks.Analyze(steps);
    }

    /// <summary>Decoded pictures for image checks, by check; decoded once, not on every pass.</summary>
    readonly Dictionary<MacroStep, ImageTemplate?> templates = new(ReferenceEqualityComparer.Instance);

    readonly MacroBlocks blocks;

    /// <summary>Set when a Stop line ran: the runner ends the run after this pass.</summary>
    public bool EndRun { get; private set; }

    /// <summary>
    /// How long the last pass spent clicking, dragging and pressing keys, in ms; waits, checks and
    /// pauses left out. Lag protection stretches the gap after a pass by this, not by the whole pass.
    /// </summary>
    public double ActiveMs { get; private set; }

    /// <summary>A While or For that's going round: where it starts and how far it's got.</summary>
    sealed class Loop(int start, double now)
    {
        public int Start { get; } = start;
        /// <summary>Times below are on <see cref="MacroAction.Now"/>, which leaves out time spent paused.</summary>
        public double StartMs { get; } = now;
        public int Rounds { get; set; }
        /// <summary>When a While last looked at its condition.</summary>
        public double LastCheckMs { get; set; } = now;
        /// <summary>When the current round began.</summary>
        public double RoundStartMs { get; set; } = now;
    }

    readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Running time in ms, not counting time held by pause.</summary>
    double Now => clock.Elapsed.TotalMilliseconds - control.HeldMs;

    public void Perform(CancellationToken cancel)
    {
        EndRun = false;
        ActiveMs = 0;
        // Badly nested blocks can't run; the editor won't start such a macro, but a key binding
        // saved elsewhere might hold one.
        if (blocks.Problem != null) return;

        var loops = new List<Loop>();
        try
        {
            int i = 0;
            while (i < steps.Count)
            {
                if (cancel.IsCancellationRequested) return;
                Volatile.Write(ref currentStepIndex, i);
                Quiet(true);
                if (!control.BeforeLine(cancel))
                {
                    if (cancel.IsCancellationRequested) return;
                    if (control.TakeJump() is { } jumpTo) i = Jump(loops, jumpTo);
                    continue;
                }
                var step = steps[i];

                switch (step.Kind)
                {
                    case StepKind.If:
                        i = TakeBranch(i, cancel);
                        continue;

                    // Reached by finishing the part above it: that part ran, so skip the rest of the If.
                    case StepKind.ElseIf or StepKind.Else:
                        i = blocks.End[i] + 1;
                        continue;

                    case StepKind.While:
                    {
                        Drop(loops, i);
                        var loop = new Loop(i, Now);
                        bool holds = Holds(step, cancel);
                        if (cancel.IsCancellationRequested) return;
                        loop.LastCheckMs = Now;
                        if (holds)
                        {
                            loops.Add(loop);
                            i++;
                        }
                        else i = blocks.End[i] + 1;
                        continue;
                    }

                    case StepKind.EndWhile:
                    {
                        int start = blocks.Open[i];
                        var opener = steps[start];
                        var loop = Find(loops, start, Now);
                        double Ran() => Now - loop.StartMs;
                        if (opener.LimitMs > 0 && Ran() >= opener.LimitMs)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }
                        // Look again no sooner than the loop's interval after the last look.
                        double rest = opener.IntervalMs - (Now - loop.LastCheckMs);
                        if (opener.LimitMs > 0) rest = Math.Min(rest, opener.LimitMs - Ran());
                        if (rest >= 1 && !control.Sleep((int)Math.Ceiling(rest), cancel)) continue;   // cut short: see the top
                        if (opener.LimitMs > 0 && Ran() >= opener.LimitMs)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }

                        Volatile.Write(ref currentStepIndex, start);
                        bool holds = Holds(opener, cancel);
                        loop.LastCheckMs = Now;
                        if (cancel.IsCancellationRequested) return;
                        if (holds) i = start + 1;
                        else
                        {
                            loops.Remove(loop);
                            i++;
                        }
                        continue;
                    }

                    case StepKind.For:
                    {
                        Drop(loops, i);
                        bool none = step.ForSeconds ? step.DurationMs <= 0 : step.Count <= 0;
                        if (none)
                        {
                            i = blocks.End[i] + 1;
                            continue;
                        }
                        loops.Add(new Loop(i, Now));
                        i++;
                        continue;
                    }

                    case StepKind.EndFor:
                    {
                        int start = blocks.Open[i];
                        var opener = steps[start];
                        var loop = Find(loops, start, Now);
                        loop.Rounds++;
                        // Time is only looked at between rounds, so a round is never cut off halfway.
                        bool done = opener.ForSeconds
                            ? Now - loop.StartMs >= opener.DurationMs
                            : loop.Rounds >= opener.Count;
                        if (done)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }
                        // A timed loop whose round takes no time at all would spin flat out.
                        if (opener.ForSeconds && Now - loop.RoundStartMs < 1 && !control.Sleep(1, cancel)) continue;
                        loop.RoundStartMs = Now;
                        i = start + 1;
                        continue;
                    }

                    case StepKind.GoTo:
                    {
                        int target = Math.Clamp(step.Line, 1, steps.Count) - 1;
                        // A loop made of jumps could otherwise spin flat out; 1 ms costs nothing next to a step.
                        if (target <= i && !control.Sleep(1, cancel)) continue;
                        i = Jump(loops, target);
                        continue;
                    }

                    case StepKind.Stop:
                        EndRun = true;
                        return;

                    // End if needs nothing; a lone old check can't run (Problem() reports it).
                    case StepKind.EndIf or StepKind.Check:
                        break;

                    // Waits leave the mouse and keyboard free for another run, and can be paused
                    // or cut short by a go-to-line.
                    case StepKind.Wait:
                        if (!control.Sleep(step.DurationMs, cancel)) continue;
                        break;

                    default:
                        RunInput(step, cancel);
                        break;
                }
                i++;
            }
        }
        finally
        {
            Quiet(false);
            Volatile.Write(ref currentStepIndex, -1);
        }
    }

    void Quiet(bool quiet)
    {
        if (lag != null) lag.Quiet = quiet;
    }

    /// <summary>
    /// Carries on from line <paramref name="target"/> (from 0). Leaving a loop ends it; loops the
    /// target is inside keep going. Returns the target.
    /// </summary>
    int Jump(List<Loop> loops, int target)
    {
        target = Math.Clamp(target, 0, Math.Max(0, steps.Count - 1));
        loops.RemoveAll(loop => target < loop.Start || target > blocks.End[loop.Start]);
        return target;
    }

    /// <summary>
    /// Works out an If: tries its condition, then each Else if in turn, and returns the first line
    /// of the part that runs, or the line after End if when none does.
    /// </summary>
    int TakeBranch(int index, CancellationToken cancel)
    {
        int part = index;
        while (true)
        {
            var step = steps[part];
            switch (step.Kind)
            {
                case StepKind.If or StepKind.ElseIf:
                    Volatile.Write(ref currentStepIndex, part);
                    if (Holds(step, cancel)) return part + 1;
                    if (cancel.IsCancellationRequested) return steps.Count;
                    part = blocks.Next[part];
                    break;
                default:
                    // Else runs its part; End if means nothing matched.
                    return part + 1;
            }
        }
    }

    ImageTemplate? TemplateOf(MacroStep check)
    {
        if (!templates.TryGetValue(check, out var template))
            templates[check] = template = check.Target == CheckTarget.Image ? ImageTemplate.FromBase64(check.ImagePng) : null;
        return template;
    }

    /// <summary>Whether a line's condition is true on screen right now.</summary>
    bool Holds(MacroStep step, CancellationToken cancel) =>
        step.Condition != null && step.Condition.Evaluate(check =>
            !cancel.IsCancellationRequested && ScreenCheck.Find(check, TemplateOf(check)) != null);

    /// <summary>The running loop that starts at <paramref name="start"/>. One jumped into from outside starts now.</summary>
    static Loop Find(List<Loop> loops, int start, double now)
    {
        int at = loops.FindLastIndex(loop => loop.Start == start);
        if (at >= 0)
        {
            // Anything opened after it and still listed was left without finishing.
            loops.RemoveRange(at + 1, loops.Count - at - 1);
            return loops[at];
        }
        var fresh = new Loop(start, now);
        loops.Add(fresh);
        return fresh;
    }

    /// <summary>Forgets the loop starting at <paramref name="start"/>, so reaching its first line again starts it over.</summary>
    static void Drop(List<Loop> loops, int start) => loops.RemoveAll(loop => loop.Start == start);

    /// <summary>
    /// A click, drag, move or key press. Any screen check aiming it looks first, outside the input
    /// lock; if it finds nothing the line is skipped. Then the gesture runs whole, before another
    /// run's gesture can start.
    /// </summary>
    void RunInput(MacroStep step, CancellationToken cancel)
    {
        var from = step.From;
        var to = step.To;
        if (step.Kind is StepKind.Click or StepKind.Drag)
        {
            if (step.FromCheck != null)
            {
                if (ScreenCheck.Aim(step.FromCheck, TemplateOf(step.FromCheck)) is not { } aimed) return;
                from = aimed;
            }
            if (step.Kind == StepKind.Drag && step.ToCheck != null)
            {
                if (ScreenCheck.Aim(step.ToCheck, TemplateOf(step.ToCheck)) is not { } aimed) return;
                to = aimed;
            }
        }
        if (cancel.IsCancellationRequested) return;

        Quiet(false);
        long started = Stopwatch.GetTimestamp();
        try
        {
            lock (InputGate.Lock)
            {
                switch (step.Kind)
                {
                    case StepKind.Move:
                        backend.SetCursorPosition(to);
                        break;

                    case StepKind.Click:
                        if (!step.AtCursor || step.FromCheck != null) backend.SetCursorPosition(from);
                        backend.SendClick(step.Button, Math.Max(1, step.ClickCount), Math.Max(0, step.HoldMs));
                        break;

                    case StepKind.Drag:
                        RunDrag(step.Button, from, to, step.DurationMs, cancel);
                        break;

                    case StepKind.Key:
                        RunKey(step, cancel);
                        break;
                }
            }
        }
        finally
        {
            ActiveMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    void RunDrag(ClickButton button, PixelPoint from, PixelPoint to, int durationMs, CancellationToken cancel)
    {
        backend.SetCursorPosition(from);
        backend.SendMouseButton(button, true);
        try
        {
            DragAction.Glide(backend, from, to, durationMs, cancel);
        }
        finally
        {
            // Always let go, even when stopped mid-drag, or the button stays stuck down.
            backend.SendMouseButton(button, false);
        }
    }

    void RunKey(MacroStep step, CancellationToken cancel)
    {
        if (step.Key == Key.None) return;

        var held = new List<Key>();
        try
        {
            foreach (var modifier in ModifierOrder)
            {
                if (!step.Modifiers.HasFlag(modifier)) continue;
                var modifierKey = KeyMap.ModifierKey(modifier);
                backend.SendKey(modifierKey, true);
                held.Add(modifierKey);
            }

            backend.SendKey(step.Key, true);
            Wait.For(step.DurationMs, cancel);
            backend.SendKey(step.Key, false);
        }
        finally
        {
            // Always release, even if a send above threw or the hold was cancelled.
            for (int i = held.Count - 1; i >= 0; i--)
                backend.SendKey(held[i], false);
        }
    }
}
