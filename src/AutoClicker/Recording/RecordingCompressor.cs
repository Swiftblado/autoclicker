using System;
using System.Collections.Generic;
using AutoClicker.Core;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Recording;

/// <summary>
/// Turns the raw stream of recorded events into readable macro steps: a press and release
/// in the same spot becomes one Click, a press and release apart becomes a Drag, and the
/// idle time between them becomes Wait steps.
/// </summary>
public static class RecordingCompressor
{
    /// <summary>How far the cursor may travel between press and release and still count as a click.</summary>
    const int ClickSlopPixels = 5;

    /// <summary>Two clicks closer together than this, in the same spot, are one double-click.</summary>
    const int DoubleClickMs = 400;

    /// <summary>Gaps shorter than this aren't worth a Wait step of their own.</summary>
    const int MinWaitMs = 15;

    /// <summary>Key holds shorter than this are treated as a plain tap.</summary>
    const int MinHoldMs = 40;

    public static List<MacroStep> ToSteps(IEnumerable<RecordedEvent> events, Key ignoreKey = Key.None)
    {
        var steps = new List<MacroStep>();
        long lastEndMs = -1;

        RecordedEvent? mouseDown = null;
        var keyDowns = new Dictionary<Key, long>();
        var heldModifiers = new Dictionary<Key, bool>();   // modifier key -> was it used with another key
        long lastClickMs = long.MinValue;
        PixelPoint lastClickAt = default;
        ClickButton lastClickButton = ClickButton.Left;

        foreach (var e in events)
        {
            if (e.Key != Key.None && e.Key == ignoreKey) continue;

            switch (e.Kind)
            {
                case RecordedKind.MouseDown:
                    mouseDown = e;
                    break;

                case RecordedKind.MouseUp:
                {
                    if (mouseDown is not { } down || down.Button != e.Button) break;
                    mouseDown = null;

                    long duration = Math.Max(0, e.TimestampMs - down.TimestampMs);
                    bool moved = Math.Abs(e.Point.X - down.Point.X) > ClickSlopPixels ||
                                 Math.Abs(e.Point.Y - down.Point.Y) > ClickSlopPixels;

                    if (moved)
                    {
                        AddWait(steps, ref lastEndMs, down.TimestampMs);
                        steps.Add(MacroStep.MakeDrag(down.Button, down.Point, e.Point, Math.Max(50, (int)duration)));
                        lastClickMs = long.MinValue;
                    }
                    else if (IsSecondClick(steps, down, lastClickMs, lastClickAt, lastClickButton))
                    {
                        // Fold this into the click just recorded, dropping the pause between them.
                        var previous = steps[^1];
                        previous.ClickCount = 2;
                        lastClickMs = long.MinValue;
                    }
                    else
                    {
                        AddWait(steps, ref lastEndMs, down.TimestampMs);
                        steps.Add(MacroStep.MakeClick(down.Button, 1, down.Point));
                        lastClickMs = e.TimestampMs;
                        lastClickAt = down.Point;
                        lastClickButton = down.Button;
                    }

                    lastEndMs = e.TimestampMs;
                    break;
                }

                case RecordedKind.KeyDown:
                {
                    if (KeyMap.ModifierOf(e.Key) != Modifiers.None)
                    {
                        heldModifiers.TryAdd(e.Key, false);
                        keyDowns[e.Key] = e.TimestampMs;
                    }
                    else
                    {
                        keyDowns[e.Key] = e.TimestampMs;
                        // Mark every held modifier as "used", so it isn't emitted on its own.
                        foreach (var modifier in new List<Key>(heldModifiers.Keys))
                            heldModifiers[modifier] = true;
                    }
                    break;
                }

                case RecordedKind.KeyUp:
                {
                    if (!keyDowns.TryGetValue(e.Key, out long downAt)) break;
                    keyDowns.Remove(e.Key);
                    long hold = Math.Max(0, e.TimestampMs - downAt);

                    if (KeyMap.ModifierOf(e.Key) != Modifiers.None)
                    {
                        bool used = heldModifiers.TryGetValue(e.Key, out bool wasUsed) && wasUsed;
                        heldModifiers.Remove(e.Key);
                        if (used) break;   // it was folded into another key's step
                    }

                    AddWait(steps, ref lastEndMs, downAt);
                    steps.Add(MacroStep.MakeKey(e.Key, ModifiersHeld(heldModifiers, e.Key),
                        hold < MinHoldMs ? 0 : (int)hold));
                    lastEndMs = e.TimestampMs;
                    lastClickMs = long.MinValue;
                    break;
                }

                case RecordedKind.MouseMove:
                    // Cursor travel is implied by the coordinates of the next click or drag.
                    break;
            }
        }

        return steps;
    }

    static Modifiers ModifiersHeld(Dictionary<Key, bool> heldModifiers, Key exclude)
    {
        var modifiers = Modifiers.None;
        foreach (var key in heldModifiers.Keys)
            if (key != exclude)
                modifiers |= KeyMap.ModifierOf(key);
        return modifiers;
    }

    static bool IsSecondClick(List<MacroStep> steps, RecordedEvent down, long lastClickMs,
        PixelPoint lastClickAt, ClickButton lastClickButton) =>
        steps.Count > 0 &&
        steps[^1].Kind == StepKind.Click &&
        steps[^1].ClickCount == 1 &&
        down.Button == lastClickButton &&
        down.TimestampMs - lastClickMs <= DoubleClickMs &&
        Math.Abs(down.Point.X - lastClickAt.X) <= ClickSlopPixels &&
        Math.Abs(down.Point.Y - lastClickAt.Y) <= ClickSlopPixels;

    static void AddWait(List<MacroStep> steps, ref long lastEndMs, long startMs)
    {
        if (lastEndMs < 0)
        {
            lastEndMs = startMs;   // don't record the pause before the first action
            return;
        }
        long gap = startMs - lastEndMs;
        if (gap >= MinWaitMs) steps.Add(MacroStep.MakeWait((int)gap));
    }
}
