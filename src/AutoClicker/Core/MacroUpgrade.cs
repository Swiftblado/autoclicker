using System;
using System.Collections.Generic;

namespace AutoClicker.Core;

/// <summary>
/// Turns macros from before 3.0 into the current form. Back then a check step did everything
/// itself: wait for something, click on it, jump to a step or stop. Now a check only says found
/// or not found, inside the condition of an If or While, so each old check becomes the
/// equivalent block of lines.
/// </summary>
public static class MacroUpgrade
{
    /// <summary>How often the While made from an old "wait until it appears" looks; what the old check did.</summary>
    const int OldPollMs = 50;

    /// <summary>
    /// Rewrites any old check steps in <paramref name="steps"/>, in place. Returns how many
    /// "click on it" settings had to be dropped (a search in an area or the whole screen no longer
    /// reports where it found something), or -1 if there was nothing to upgrade.
    /// </summary>
    public static int Upgrade(List<MacroStep> steps)
    {
        if (!steps.Exists(step => step.Kind == StepKind.Check)) return -1;

        var result = new List<MacroStep>();
        // Where each old step starts now, so "go to step N" can be pointed at the right line.
        var firstLine = new int[steps.Count];
        var gotos = new List<MacroStep>();
        int dropped = 0;

        for (int old = 0; old < steps.Count; old++)
        {
            firstLine[old] = result.Count;
            var step = steps[old];
            if (step.Kind != StepKind.Check)
            {
                result.Add(step);
                continue;
            }

            var look = LookOnly(step);

            if (step.WaitUntilFound)
            {
                result.Add(MacroStep.MakeWhile(Condition.Group(LogicJoin.And, Condition.Of(look, not: true)),
                    OldPollMs, Math.Max(0, step.TimeoutMs)));
                result.Add(MacroStep.Make(StepKind.EndWhile));
            }

            var whenFound = new List<MacroStep>();
            if (step.ClickFound)
            {
                if (KnownSpot(step) is { } spot)
                    whenFound.Add(MacroStep.MakeClick(step.Button, Math.Max(1, step.ClickCount), spot, holdMs: step.HoldMs));
                else
                    dropped++;
            }
            whenFound.AddRange(Branch(step.OnFound, step.FoundStep, gotos));
            var whenMissing = Branch(step.OnMissing, step.MissingStep, gotos);

            if (whenFound.Count == 0 && whenMissing.Count == 0) continue;

            if (whenFound.Count == 0)
            {
                // Only the "not found" side does anything: If not found … End if.
                result.Add(MacroStep.MakeIf(Condition.Group(LogicJoin.And, Condition.Of(look.Clone(), not: true))));
                result.AddRange(whenMissing);
            }
            else
            {
                result.Add(MacroStep.MakeIf(Condition.Group(LogicJoin.And, Condition.Of(look.Clone()))));
                result.AddRange(whenFound);
                if (whenMissing.Count > 0)
                {
                    result.Add(MacroStep.Make(StepKind.Else));
                    result.AddRange(whenMissing);
                }
            }
            result.Add(MacroStep.Make(StepKind.EndIf));
        }

        // Old "go to step N" (stored in Line for now) → the line that step starts on.
        foreach (var jump in gotos)
        {
            int old = Math.Clamp(jump.Line, 1, steps.Count) - 1;
            jump.Line = Math.Clamp(firstLine[old] + 1, 1, Math.Max(1, result.Count));
        }

        steps.Clear();
        steps.AddRange(result);
        return dropped;
    }

    /// <summary>The old step's look-for settings, minus what checks no longer do.</summary>
    static MacroStep LookOnly(MacroStep step)
    {
        var look = step.Clone();
        look.Kind = StepKind.Check;
        look.WaitUntilFound = false;
        look.ClickFound = false;
        look.OnFound = look.OnMissing = CheckBranch.Next;
        return look;
    }

    /// <summary>Where an old check would have clicked, when that doesn't depend on a search.</summary>
    static Avalonia.PixelPoint? KnownSpot(MacroStep step) => (step.Target, step.Where) switch
    {
        (CheckTarget.Color, CheckArea.Point) => step.From,
        (CheckTarget.Colors, CheckArea.Point) when step.Points.Count > 0 => new Avalonia.PixelPoint(step.Points[0].X, step.Points[0].Y),
        _ => null
    };

    static List<MacroStep> Branch(CheckBranch branch, int oldStep, List<MacroStep> gotos)
    {
        switch (branch)
        {
            case CheckBranch.GoTo:
                var jump = MacroStep.MakeGoTo(oldStep);
                gotos.Add(jump);
                return [jump];
            case CheckBranch.Stop:
                return [MacroStep.Make(StepKind.Stop)];
            default:
                return [];
        }
    }
}
