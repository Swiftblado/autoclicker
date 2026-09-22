using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AutoClicker.Input;
using Avalonia.Input;

namespace AutoClicker.Hotkeys;

/// <summary>
/// Win32 RegisterHotKey. The hotkey needs a window with a message loop, so this owns a
/// hidden message-only window on its own thread.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsHotkeyService : IHotkeyService
{
    const int HotkeyId = 1;
    const uint WM_HOTKEY = 0x0312;
    const uint WM_CLOSE = 0x0010;
    const uint WM_DESTROY = 0x0002;
    const uint MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly WndProcDelegate wndProc;   // kept alive for as long as the window exists
    readonly string className = "AutoClickerHotkeyWindow_" + Guid.NewGuid().ToString("N");
    Thread? thread;
    IntPtr hwnd;
    bool registered;
    string? registerError;

    public event Action? Pressed;

    public WindowsHotkeyService()
    {
        wndProc = WindowProc;
    }

    public bool TryRegister(Key key, out string? error)
    {
        Unregister();
        if (!KeyMap.TryGet(key, out var codes))
        {
            error = "That key can't be used as a hotkey.";
            return false;
        }

        using var ready = new ManualResetEventSlim(false);
        thread = new Thread(() => MessageLoop(codes.WindowsVk, ready))
        {
            IsBackground = true,
            Name = "AutoClicker.Hotkey"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(2000);

        error = registerError;
        return registered;
    }

    void MessageLoop(ushort vk, ManualResetEventSlim ready)
    {
        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };

        try
        {
            if (RegisterClassEx(ref wndClass) == 0)
            {
                registerError = "Couldn't create the hotkey window.";
                return;
            }

            hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                registerError = "Couldn't create the hotkey window.";
                return;
            }

            registered = RegisterHotKey(hwnd, HotkeyId, MOD_NOREPEAT, vk);
            if (!registered)
                registerError = "another app is already using it";
        }
        finally
        {
            ready.Set();
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (registered) UnregisterHotKey(hwnd, HotkeyId);
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        UnregisterClass(className, wndClass.hInstance);
        hwnd = IntPtr.Zero;
        registered = false;
    }

    IntPtr WindowProc(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            // Without this the GetMessage loop below would never return.
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, msg, wParam, lParam);
    }

    public void Unregister()
    {
        var current = thread;
        if (current == null) return;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        current.Join(1000);
        thread = null;
        registerError = null;
    }

    public void Dispose() => Unregister();

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? moduleName);
}
