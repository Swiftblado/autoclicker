using System;
using System.Collections.Generic;
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
    int currentStepIndex = -1;

    /// <summary>
    /// Index of the step currently executing, or -1 between steps / when not running.
    /// Written by the run thread, read by the UI thread to highlight the running step.
    /// </summary>
    public int CurrentStepIndex => Volatile.Read(ref currentStepIndex);

    public MacroAction(IInputBackend backend, Macro macro)
    {
        this.backend = backend;
        // Snapshot so edits made in the UI while this runs can't mutate what's executing.
        steps = new List<MacroStep>(macro.Steps);
        blocks = MacroBlocks.Analyze(steps);
    }

    /// <summary>Decoded pictures for image checks, by check; decoded once, not on every pass.</summary>
    readonly Dictionary<MacroStep, ImageTemplate?> templates = new(ReferenceEqualityComparer.Instance);

    readonly MacroBlocks blocks;

    /// <summary>Set when a Stop line ran: the runner ends the run after this pass.</summary>
    public bool EndRun { get; private set; }

    /// <summary>A While or For that's going round: where it starts and how far it's got.</summary>
    sealed class Loop(int start)
    {
        public int Start { get; } = start;
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public int Rounds { get; set; }
        /// <summary>Clock time of the last look at a While's condition, in ms.</summary>
        public long LastCheckMs { get; set; }
        /// <summary>Clock time when the current round began, in ms.</summary>
        public long RoundStartMs { get; set; }
    }

    public void Perform(CancellationToken cancel)
    {
        EndRun = false;
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
                        var loop = new Loop(i);
                        bool holds = Holds(step, cancel);
                        if (cancel.IsCancellationRequested) return;
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
                        var loop = Find(loops, start);
                        if (loop.Clock.ElapsedMilliseconds >= opener.LimitMs && opener.LimitMs > 0)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }
                        // Look again no sooner than the loop's interval after the last look.
                        long rest = opener.IntervalMs - (loop.Clock.ElapsedMilliseconds - loop.LastCheckMs);
                        if (opener.LimitMs > 0) rest = Math.Min(rest, opener.LimitMs - loop.Clock.ElapsedMilliseconds);
                        if (rest > 0) Wait.For((int)rest, cancel);
                        if (cancel.IsCancellationRequested) return;
                        if (opener.LimitMs > 0 && loop.Clock.ElapsedMilliseconds >= opener.LimitMs)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }

                        Volatile.Write(ref currentStepIndex, start);
                        bool holds = Holds(opener, cancel);
                        loop.LastCheckMs = loop.Clock.ElapsedMilliseconds;
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
                        loops.Add(new Loop(i));
                        i++;
                        continue;
                    }

                    case StepKind.EndFor:
                    {
                        int start = blocks.Open[i];
                        var opener = steps[start];
                        var loop = Find(loops, start);
                        loop.Rounds++;
                        // Time is only looked at between rounds, so a round is never cut off halfway.
                        bool done = opener.ForSeconds
                            ? loop.Clock.ElapsedMilliseconds >= opener.DurationMs
                            : loop.Rounds >= opener.Count;
                        if (done)
                        {
                            loops.Remove(loop);
                            i++;
                            continue;
                        }
                        // A timed loop whose round takes no time at all would spin flat out.
                        if (opener.ForSeconds && loop.Clock.ElapsedMilliseconds - loop.RoundStartMs < 1) Wait.For(1, cancel);
                        loop.RoundStartMs = loop.Clock.ElapsedMilliseconds;
                        i = start + 1;
                        continue;
                    }

                    case StepKind.GoTo:
                    {
                        int target = Math.Clamp(step.Line, 1, steps.Count) - 1;
                        // Leaving a loop ends it; loops the target is inside keep going.
                        loops.RemoveAll(loop => target < loop.Start || target > blocks.End[loop.Start]);
                        // A loop made of jumps could otherwise spin flat out; 1 ms costs nothing next to a step.
                        if (target <= i) Wait.For(1, cancel);
                        i = target;
                        continue;
                    }

                    case StepKind.Stop:
                        EndRun = true;
                        return;

                    // End if needs nothing; a lone old check can't run (Problem() reports it).
                    case StepKind.EndIf or StepKind.Check:
                        break;

                    // Waits leave the mouse and keyboard free for another run; every other step
                    // finishes before another run's gesture starts.
                    case StepKind.Wait:
                        RunStep(step, cancel);
                        break;

                    default:
                        lock (InputGate.Lock) RunStep(step, cancel);
                        break;
                }
                i++;
            }
        }
        finally
        {
            Volatile.Write(ref currentStepIndex, -1);
        }
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

    /// <summary>Whether a line's condition is true on screen right now.</summary>
    bool Holds(MacroStep step, CancellationToken cancel) =>
        step.Condition != null && step.Condition.Evaluate(check =>
        {
            if (cancel.IsCancellationRequested) return false;
            if (!templates.TryGetValue(check, out var template))
                templates[check] = template = check.Target == CheckTarget.Image ? ImageTemplate.FromBase64(check.ImagePng) : null;
            return ScreenCheck.Find(check, template) != null;
        });

    /// <summary>The running loop that starts at <paramref name="start"/>. One jumped into from outside starts now.</summary>
    static Loop Find(List<Loop> loops, int start)
    {
        int at = loops.FindLastIndex(loop => loop.Start == start);
        if (at >= 0)
        {
            // Anything opened after it and still listed was left without finishing.
            loops.RemoveRange(at + 1, loops.Count - at - 1);
            return loops[at];
        }
        var fresh = new Loop(start);
        loops.Add(fresh);
        return fresh;
    }

    /// <summary>Forgets the loop starting at <paramref name="start"/>, so reaching its first line again starts it over.</summary>
    static void Drop(List<Loop> loops, int start) => loops.RemoveAll(loop => loop.Start == start);

    void RunStep(MacroStep step, CancellationToken cancel)
    {
        switch (step.Kind)
        {
            case StepKind.Wait:
                Wait.For(step.DurationMs, cancel);
                break;

            case StepKind.Move:
                backend.SetCursorPosition(step.To);
                break;

            case StepKind.Click:
                if (!step.AtCursor) backend.SetCursorPosition(step.From);
                backend.SendClick(step.Button, Math.Max(1, step.ClickCount), Math.Max(0, step.HoldMs));
                break;

            case StepKind.Drag:
                RunDrag(step, cancel);
                break;

            case StepKind.Key:
                RunKey(step, cancel);
                break;
        }
    }

    void RunDrag(MacroStep step, CancellationToken cancel)
    {
        backend.SetCursorPosition(step.From);
        backend.SendMouseButton(step.Button, true);
        try
        {
            DragAction.Glide(backend, step.From, step.To, step.DurationMs, cancel);
        }
        finally
        {
            // Always let go, even when stopped mid-drag, or the button stays stuck down.
            backend.SendMouseButton(step.Button, false);
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
