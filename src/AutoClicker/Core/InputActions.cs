using System;
using System.Collections.Generic;
using System.Threading;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Core;

public interface IInputAction
{
    /// <summary>
    /// Do the thing once. Long actions (drags, macros) should give up promptly when
    /// <paramref name="cancel"/> fires, but must still release anything they are holding.
    /// </summary>
    void Perform(CancellationToken cancel);
}

public sealed class MouseClickAction(
    IInputBackend backend, ClickButton button, int clickCount, bool useFixedPosition, PixelPoint position)
    : IInputAction
{
    public void Perform(CancellationToken cancel)
    {
        if (useFixedPosition) backend.SetCursorPosition(position);
        backend.SendClick(button, clickCount);
    }
}

public sealed class KeyPressAction(IInputBackend backend, Key key, Modifiers modifiers, int holdMs)
    : IInputAction
{
    static readonly Modifiers[] Order = [Modifiers.Control, Modifiers.Shift, Modifiers.Alt, Modifiers.Meta];

    public void Perform(CancellationToken cancel)
    {
        var held = new List<Key>();
        foreach (var modifier in Order)
        {
            if (!modifiers.HasFlag(modifier)) continue;
            var modifierKey = KeyMap.ModifierKey(modifier);
            backend.SendKey(modifierKey, true);
            held.Add(modifierKey);
        }

        backend.SendKey(key, true);
        if (holdMs > 0) Wait.For(holdMs, cancel);
        backend.SendKey(key, false);

        for (int i = held.Count - 1; i >= 0; i--)
            backend.SendKey(held[i], false);
    }
}

/// <summary>
/// Presses a mouse button at one point, walks the cursor to another, and releases.
/// The cursor is moved in small steps because apps track drags through motion events;
/// teleporting from start to end reads as a click in most of them.
/// </summary>
public sealed class DragAction(
    IInputBackend backend, ClickButton button, PixelPoint from, PixelPoint to, int durationMs)
    : IInputAction
{
    const int StepIntervalMs = 10;

    public void Perform(CancellationToken cancel)
    {
        backend.SetCursorPosition(from);
        backend.SendMouseButton(button, true);
        try
        {
            Glide(backend, from, to, durationMs, cancel);
        }
        finally
        {
            // Always let go, even when stopped mid-drag, or the button stays stuck down.
            backend.SendMouseButton(button, false);
        }
    }

    internal static void Glide(IInputBackend backend, PixelPoint from, PixelPoint to, int durationMs,
        CancellationToken cancel)
    {
        int steps = Math.Max(1, durationMs / StepIntervalMs);
        for (int i = 1; i <= steps; i++)
        {
            if (cancel.IsCancellationRequested) break;
            double progress = (double)i / steps;
            backend.SetCursorPosition(new PixelPoint(
                (int)Math.Round(from.X + (to.X - from.X) * progress),
                (int)Math.Round(from.Y + (to.Y - from.Y) * progress)));
            Wait.For(durationMs / steps, cancel);
        }
        backend.SetCursorPosition(to);
    }
}

static class Wait
{
    /// <summary>Sleeps, but wakes immediately if the run is cancelled.</summary>
    public static void For(int ms, CancellationToken cancel)
    {
        if (ms <= 0) return;
        try { cancel.WaitHandle.WaitOne(ms); }
        catch (ObjectDisposedException) { Thread.Sleep(ms); }
    }
}
