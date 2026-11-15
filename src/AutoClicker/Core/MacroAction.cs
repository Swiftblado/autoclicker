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
    }

    /// <summary>How often a "wait until found" check looks again.</summary>
    const int CheckPollMs = 50;

    /// <summary>Decoded pictures for image checks, by step index; decoded once, not on every pass.</summary>
    readonly Dictionary<int, ImageTemplate?> templates = new();

    /// <summary>Set when a check step said "stop": the runner ends the run after this pass.</summary>
    public bool EndRun { get; private set; }

    public void Perform(CancellationToken cancel)
    {
        EndRun = false;
        try
        {
            int i = 0;
            while (i < steps.Count)
            {
                if (cancel.IsCancellationRequested) return;
                Volatile.Write(ref currentStepIndex, i);
                var step = steps[i];

                if (step.Kind == StepKind.Check)
                {
                    var branch = RunCheck(step, i, cancel, out int goTo);
                    if (branch == CheckBranch.Stop) { EndRun = true; return; }
                    if (branch == CheckBranch.GoTo && goTo >= 0 && goTo < steps.Count)
                    {
                        // A loop of checks and jumps could otherwise spin flat out; a 1 ms breather
                        // costs nothing next to a screen grab.
                        if (goTo <= i) Wait.For(1, cancel);
                        i = goTo;
                        continue;
                    }
                    // GoTo past the end finishes the pass.
                    if (branch == CheckBranch.GoTo) return;
                }
                // Waits leave the mouse and keyboard free for another run; every other step
                // finishes before another run's gesture starts.
                else if (step.Kind == StepKind.Wait) RunStep(step, cancel);
                else lock (InputGate.Lock) RunStep(step, cancel);
                i++;
            }
        }
        finally
        {
            Volatile.Write(ref currentStepIndex, -1);
        }
    }

    /// <summary>Looks (once, or until found or timed out), clicks if asked, and says where to go next.</summary>
    CheckBranch RunCheck(MacroStep step, int index, CancellationToken cancel, out int goTo)
    {
        if (!templates.TryGetValue(index, out var template))
            templates[index] = template = step.Target == CheckTarget.Image ? ImageTemplate.FromBase64(step.ImagePng) : null;

        PixelPoint? found = ScreenCheck.Find(step, template);
        if (found == null && step.WaitUntilFound)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (found == null && !cancel.IsCancellationRequested &&
                   (step.TimeoutMs <= 0 || clock.ElapsedMilliseconds < step.TimeoutMs))
            {
                Wait.For(CheckPollMs, cancel);
                if (cancel.IsCancellationRequested) break;
                found = ScreenCheck.Find(step, template);
            }
        }

        // Stopped meanwhile: the step loop notices and returns.
        if (cancel.IsCancellationRequested) { goTo = -1; return CheckBranch.Next; }

        if (found is { } at)
        {
            if (step.ClickFound)
            {
                lock (InputGate.Lock)
                {
                    backend.SetCursorPosition(at);
                    backend.SendClick(step.Button, Math.Max(1, step.ClickCount), Math.Max(0, step.HoldMs));
                }
            }
            goTo = step.FoundStep - 1;
            return step.OnFound;
        }

        goTo = step.MissingStep - 1;
        return step.OnMissing;
    }

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
