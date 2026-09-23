using System;
using Avalonia;
using Avalonia.Input;
using AutoClicker.Input;

namespace AutoClicker.Recording;

public enum RecordedKind { MouseDown, MouseUp, MouseMove, KeyDown, KeyUp }

/// <summary>A single real input event as it happened, with the time it arrived.</summary>
public readonly record struct RecordedEvent(
    RecordedKind Kind,
    long TimestampMs,
    PixelPoint Point,
    ClickButton Button,
    Key Key);

/// <summary>
/// Watches real mouse and keyboard input system-wide. One implementation per OS;
/// recording is a separate privilege from sending input on every platform.
/// </summary>
public interface IInputRecorder : IDisposable
{
    /// <summary>Raised on a background thread for every event while recording.</summary>
    event Action<RecordedEvent>? Recorded;

    bool IsRecording { get; }

    /// <summary>False if this OS build can't watch input; <paramref name="error"/> says why.</summary>
    bool TryStart(out string? error);

    void Stop();
}

public static class InputRecorder
{
    public static IInputRecorder Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsInputRecorder();
        if (OperatingSystem.IsMacOS()) return new MacInputRecorder();
        if (OperatingSystem.IsLinux()) return new LinuxInputRecorder();
        return new NullInputRecorder();
    }
}

sealed class NullInputRecorder : IInputRecorder
{
    public event Action<RecordedEvent>? Recorded { add { } remove { } }
    public bool IsRecording => false;

    public bool TryStart(out string? error)
    {
        error = "Recording isn't supported on this system.";
        return false;
    }

    public void Stop() { }
    public void Dispose() { }
}
