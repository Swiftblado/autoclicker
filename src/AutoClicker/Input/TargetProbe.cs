using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoClicker.Input;

/// <summary>
/// Measures how quickly the app being automated responds. Only Windows offers a reliable way
/// to ask another app "are you keeping up?"; elsewhere <see cref="Create"/> returns null and
/// lag is judged from this app's own timing alone.
/// </summary>
public interface ITargetProbe
{
    /// <summary>Milliseconds the slowest target took to answer, or null if there's nothing to ask.</summary>
    double? Measure();
}

public static class TargetProbe
{
    public static ITargetProbe? Create() =>
        OperatingSystem.IsWindows() ? new WindowsTargetProbe() : null;
}

/// <summary>
/// Sends WM_NULL, which does nothing, to the window under the cursor and the focused window, and
/// times the reply. An app answers once its message loop gets to it, so the round trip shows how
/// far behind it is on handling input, including the clicks we're sending it.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsTargetProbe : ITargetProbe
{
    /// <summary>Waiting longer tells us nothing more: past this it's lagging badly either way.</summary>
    const uint TimeoutMs = 500;

    readonly uint ownProcessId = (uint)Environment.ProcessId;

    public double? Measure()
    {
        IntPtr underCursor = GetCursorPos(out var point) ? GetAncestor(WindowFromPoint(point), GA_ROOT) : IntPtr.Zero;
        IntPtr focused = GetForegroundWindow();

        double? worst = Ping(underCursor);
        if (focused != underCursor)
        {
            double? other = Ping(focused);
            if (other > (worst ?? -1)) worst = other;
        }
        return worst;
    }

    double? Ping(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == ownProcessId) return null;

        long start = Stopwatch.GetTimestamp();
        IntPtr answered = SendMessageTimeout(window, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG, TimeoutMs, out _);
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (answered != IntPtr.Zero) return ms;

        // No answer: either it timed out or Windows already considers the app hung.
        int error = Marshal.GetLastWin32Error();
        return error == ERROR_TIMEOUT || IsHungAppWindow(window) ? Math.Max(ms, TimeoutMs) : null;
    }

    const uint WM_NULL = 0x0000;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint GA_ROOT = 2;
    const int ERROR_TIMEOUT = 1460;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
