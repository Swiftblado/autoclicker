using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Input;

/// <summary>Sends input through the Win32 SendInput API.</summary>
[SupportedOSPlatform("windows")]
sealed class WindowsInputBackend : IInputBackend
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public void SendClick(ClickButton button, int clickCount)
    {
        (uint down, uint up) = button switch
        {
            ClickButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            ClickButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        var inputs = new INPUT[clickCount * 2];
        for (int i = 0; i < clickCount; i++)
        {
            inputs[i * 2] = MouseInput(down);
            inputs[i * 2 + 1] = MouseInput(up);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void SendMouseButton(ClickButton button, bool down)
    {
        uint flags = (button, down) switch
        {
            (ClickButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (ClickButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (ClickButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (ClickButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            (_, true) => MOUSEEVENTF_LEFTDOWN,
            (_, false) => MOUSEEVENTF_LEFTUP
        };
        SendInput(1, [MouseInput(flags)], Marshal.SizeOf<INPUT>());
    }

    public void SendKey(Key key, bool down)
    {
        if (!KeyMap.TryGet(key, out var codes)) return;

        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wVk = codes.WindowsVk;
        input.U.ki.wScan = (ushort)MapVirtualKey(codes.WindowsVk, 0);
        uint flags = 0;
        if (IsExtended(key)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        input.U.ki.dwFlags = flags;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public PixelPoint GetCursorPosition() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    public void SetCursorPosition(PixelPoint point) => SetCursorPos(point.X, point.Y);

    public void Dispose() { }

    static bool IsExtended(Key key) => key is Key.Insert or Key.Delete or Key.Home or Key.End
        or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down
        or Key.NumLock or Key.Divide or Key.PrintScreen
        or Key.RightCtrl or Key.RightAlt or Key.LWin or Key.RWin;

    static INPUT MouseInput(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dwFlags = flags;
        return input;
    }

    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetCursorPos(int x, int y);
}
