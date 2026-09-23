using System;
using System.Collections.Generic;
using System.Threading;
using AutoClicker.Input;
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

    public void Perform(CancellationToken cancel)
    {
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (cancel.IsCancellationRequested) return;
                Volatile.Write(ref currentStepIndex, i);
                RunStep(steps[i], cancel);
            }
        }
        finally
        {
            Volatile.Write(ref currentStepIndex, -1);
        }
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
                backend.SendClick(step.Button, Math.Max(1, step.ClickCount));
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
