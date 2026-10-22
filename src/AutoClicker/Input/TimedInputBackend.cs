using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Input;

/// <summary>
/// Passes everything through to another backend, timing each call that sends input. Handing an
/// event to the OS normally takes microseconds; when its input queue backs up, these calls
/// start to block, which is one of the signs of lag the run watches for.
/// </summary>
sealed class TimedInputBackend(IInputBackend inner, Action<double> reportSendMs) : IInputBackend
{
    public bool IsAvailable => inner.IsAvailable;
    public string? UnavailableReason => inner.UnavailableReason;

    public void SendClick(ClickButton button, int clickCount, int holdMs)
    {
        long start = Stopwatch.GetTimestamp();
        inner.SendClick(button, clickCount, holdMs);
        // The holds are deliberate sleeps, not the OS being slow, so leave them out.
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds - Math.Max(0, holdMs) * clickCount;
        reportSendMs(Math.Max(0, ms / Math.Max(1, clickCount)));
    }

    public void SendMouseButton(ClickButton button, bool down) =>
        Time(() => inner.SendMouseButton(button, down));

    public void SendKey(Key key, bool down) => Time(() => inner.SendKey(key, down));

    public void SetCursorPosition(PixelPoint point) => Time(() => inner.SetCursorPosition(point));

    public PixelPoint GetCursorPosition() => inner.GetCursorPosition();

    /// <summary>The wrapped backend belongs to the window, which disposes it.</summary>
    public void Dispose() { }

    void Time(Action send)
    {
        long start = Stopwatch.GetTimestamp();
        send();
        reportSendMs(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
