using System;
using AutoClicker.Input;
using Avalonia.Input;

namespace AutoClicker.Hotkeys;

/// <summary>
/// A single system-wide hotkey (a key, optionally with modifiers) that works while other apps have
/// focus. Create one service per hotkey.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Raised on some background thread; callers marshal to the UI thread themselves.</summary>
    event Action? Pressed;

    bool TryRegister(Key key, Modifiers modifiers, out string? error);
    void Unregister();
}

public static class HotkeyService
{
    public static IHotkeyService Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHotkeyService();
        if (OperatingSystem.IsMacOS()) return new MacHotkeyService();
        if (OperatingSystem.IsLinux()) return new LinuxHotkeyService();
        return new NullHotkeyService();
    }
}

sealed class NullHotkeyService : IHotkeyService
{
    public event Action? Pressed { add { } remove { } }

    public bool TryRegister(Key key, Modifiers modifiers, out string? error)
    {
        error = "Global hotkeys aren't supported on this system.";
        return false;
    }

    public void Unregister() { }
    public void Dispose() { }
}
