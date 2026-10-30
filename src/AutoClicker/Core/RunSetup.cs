using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Core;

public enum AutomationMode { Clicks, Drags, Keys, Macro }

/// <summary>
/// Everything one run needs: what to do, how often, and for how long. The window builds one
/// from its controls; a key binding keeps one of its own, saved as JSON.
/// </summary>
public sealed class RunSetup
{
    public AutomationMode Mode { get; set; }

    // Mouse clicks
    public ClickButton Button { get; set; } = ClickButton.Left;
    public int ClickCount { get; set; } = 1;
    public int HoldMs { get; set; } = ClickHold.DefaultMs;
    public bool FixedPosition { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    // Drags
    public ClickButton DragButton { get; set; } = ClickButton.Left;
    public int DragFromX { get; set; }
    public int DragFromY { get; set; }
    public int DragToX { get; set; }
    public int DragToY { get; set; }
    public int DragDurationMs { get; set; } = 400;

    // Key presses
    public Key Key { get; set; } = Key.None;
    public Modifiers Modifiers { get; set; } = Modifiers.None;
    public int KeyHoldMs { get; set; } = 30;

    // Macro
    public List<MacroStep> Steps { get; set; } = new();

    // Timing
    public double IntervalMs { get; set; } = 100;
    public int JitterMs { get; set; }
    /// <summary>How many times (or macro passes) to run; 0 runs until stopped.</summary>
    public long Repeat { get; set; }
    public bool LagCheck { get; set; } = true;

    /// <summary>Why this setup can't run, in words for the user; null when it can.</summary>
    public string? Problem()
    {
        if (IntervalMs < 1) return "Set an interval of at least 1 ms.";
        if (Mode == AutomationMode.Keys && Key == Key.None)
            return "Click the Key box and press the key you want repeated.";
        if (Mode == AutomationMode.Macro && Steps.Count == 0)
            return "The macro is empty. Record one, or add steps below.";
        return null;
    }

    /// <param name="target">Where the action sends its input: the backend itself, or a wrapper round it.</param>
    public IInputAction Build(IInputBackend target) => Mode switch
    {
        AutomationMode.Clicks => new MouseClickAction(target, Button, ClickCount, HoldMs, FixedPosition, new PixelPoint(X, Y)),
        AutomationMode.Drags => new DragAction(target, DragButton, new PixelPoint(DragFromX, DragFromY),
            new PixelPoint(DragToX, DragToY), DragDurationMs),
        AutomationMode.Keys => new KeyPressAction(target, Key, Modifiers, KeyHoldMs),
        _ => new MacroAction(target, new Macro { Steps = Steps })
    };

    /// <summary>"click", "drag", "key press" or "pass", for counting what a run has done.</summary>
    public string CountNoun(long count) => Mode switch
    {
        AutomationMode.Clicks => count == 1 ? "click" : "clicks",
        AutomationMode.Drags => count == 1 ? "drag" : "drags",
        AutomationMode.Keys => count == 1 ? "key press" : "key presses",
        _ => count == 1 ? "pass" : "passes"
    };

    /// <summary>One line for the bindings list, e.g. "Half left clicks at (200, 300) every 10 ms".</summary>
    public string Describe()
    {
        string what = Mode switch
        {
            AutomationMode.Clicks =>
                (ClickHold.IsHalf(HoldMs) ? "Half " : "") +
                (ClickHold.IsHalf(HoldMs) ? ButtonName(Button) : Capitalized(ButtonName(Button))) +
                (ClickCount > 1 ? " double-clicks" : " clicks") +
                (FixedPosition ? " at (" + X + ", " + Y + ")" : ""),
            AutomationMode.Drags => Capitalized(ButtonName(DragButton)) + " drags (" + DragFromX + ", " + DragFromY +
                                    ") → (" + DragToX + ", " + DragToY + ")",
            AutomationMode.Keys => "Press " + KeyMap.ComboName(Key, Modifiers),
            _ => "Macro, " + Steps.Count + (Steps.Count == 1 ? " step" : " steps")
        };
        string often = Mode == AutomationMode.Macro ? ", again after " : " every ";
        string times = Repeat > 0 ? ", " + Repeat.ToString("N0", CultureInfo.CurrentCulture) + (Repeat == 1 ? " time" : " times") : "";
        return what + often + Interval(IntervalMs) + times;
    }

    static string ButtonName(ClickButton button) => button.ToString().ToLowerInvariant();

    static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    static string Interval(double ms)
    {
        if (ms < 1000) return ms.ToString("0.#", CultureInfo.CurrentCulture) + " ms";
        if (ms < 60000) return (ms / 1000).ToString("0.##", CultureInfo.CurrentCulture) + " s";
        if (ms < 3600000) return (ms / 60000).ToString("0.##", CultureInfo.CurrentCulture) + " min";
        return (ms / 3600000).ToString("0.##", CultureInfo.CurrentCulture) + " h";
    }

    public RunSetup Clone()
    {
        var clone = (RunSetup)MemberwiseClone();
        clone.Steps = Steps.Select(step => step.Clone()).ToList();
        return clone;
    }
}
