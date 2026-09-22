using System;
using System.Collections.Generic;
using System.Threading;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Core;

public interface IInputAction
{
    void Perform();
}

public sealed class MouseClickAction(
    IInputBackend backend, ClickButton button, int clickCount, bool useFixedPosition, PixelPoint position)
    : IInputAction
{
    public void Perform()
    {
        if (useFixedPosition) backend.SetCursorPosition(position);
        backend.SendClick(button, clickCount);
    }
}

public sealed class KeyPressAction(IInputBackend backend, Key key, Modifiers modifiers, int holdMs)
    : IInputAction
{
    static readonly Modifiers[] Order = [Modifiers.Control, Modifiers.Shift, Modifiers.Alt, Modifiers.Meta];

    public void Perform()
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
        if (holdMs > 0) Thread.Sleep(holdMs);
        backend.SendKey(key, false);

        for (int i = held.Count - 1; i >= 0; i--)
            backend.SendKey(held[i], false);
    }
}
