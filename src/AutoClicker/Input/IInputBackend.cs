using System;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Input;

public enum ClickButton { Left, Right, Middle }

/// <summary>Modifier keys, named neutrally: Meta is Win on Windows, Command on macOS, Super on Linux.</summary>
[Flags]
public enum Modifiers { None = 0, Control = 1, Shift = 2, Alt = 4, Meta = 8 }

/// <summary>Sends synthetic mouse and keyboard input. One implementation per operating system.</summary>
public interface IInputBackend : IDisposable
{
    /// <summary>False when this OS can send input in principle but something is missing or not permitted.</summary>
    bool IsAvailable { get; }

    /// <summary>Explains to the user what to do about <see cref="IsAvailable"/> being false.</summary>
    string? UnavailableReason { get; }

    void SendClick(ClickButton button, int clickCount);

    /// <summary>Press or release a mouse button on its own — the halves of a drag.</summary>
    void SendMouseButton(ClickButton button, bool down);

    void SendKey(Key key, bool down);
    PixelPoint GetCursorPosition();
    void SetCursorPosition(PixelPoint point);
}

public static class InputBackend
{
    public static IInputBackend Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsInputBackend();
        if (OperatingSystem.IsMacOS()) return new MacInputBackend();
        if (OperatingSystem.IsLinux()) return new LinuxInputBackend();
        return new UnsupportedInputBackend();
    }
}

sealed class UnsupportedInputBackend : IInputBackend
{
    public bool IsAvailable => false;
    public string UnavailableReason => "This operating system isn't supported.";
    public void SendClick(ClickButton button, int clickCount) { }
    public void SendMouseButton(ClickButton button, bool down) { }
    public void SendKey(Key key, bool down) { }
    public PixelPoint GetCursorPosition() => default;
    public void SetCursorPosition(PixelPoint point) { }
    public void Dispose() { }
}
