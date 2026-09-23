using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Recording;

/// <summary>
/// Low-level Windows hooks. They must live on a thread with a message loop, so this owns one.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsInputRecorder : IInputRecorder
{
    const int WH_KEYBOARD_LL = 13;
    const int WH_MOUSE_LL = 14;
    const int HC_ACTION = 0;

    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    const uint WM_QUIT = 0x0012;

    const uint LLKHF_INJECTED = 0x10;
    const uint LLMHF_INJECTED = 0x01;

    readonly HookProc keyboardProc;
    readonly HookProc mouseProc;
    readonly Stopwatch clock = new();
    Thread? thread;
    uint threadId;
    IntPtr keyboardHook, mouseHook;
    volatile bool recording;
    string? startError;

    public event Action<RecordedEvent>? Recorded;

    public bool IsRecording => recording;

    public WindowsInputRecorder()
    {
        keyboardProc = KeyboardHook;
        mouseProc = MouseHook;
    }

    public bool TryStart(out string? error)
    {
        Stop();
        startError = null;
        clock.Restart();

        using var ready = new ManualResetEventSlim(false);
        thread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "AutoClicker.Recorder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(2000);

        error = startError;
        return recording;
    }

    void HookLoop(ManualResetEventSlim ready)
    {
        try
        {
            var module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            {
                startError = "Windows wouldn't let the app watch input.";
                return;
            }
            threadId = GetCurrentThreadId();
            recording = true;
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

        if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = IntPtr.Zero;
        recording = false;
    }

    IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && recording)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Skip anything we synthesised ourselves, or playback would record itself.
            if ((data.flags & LLKHF_INJECTED) == 0 && KeyMap.TryFromWindowsVk((ushort)data.vkCode, out var key))
            {
                int message = wParam.ToInt32();
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = message is WM_KEYUP or WM_SYSKEYUP;
                if (down || up)
                    Emit(new RecordedEvent(down ? RecordedKind.KeyDown : RecordedKind.KeyUp,
                        clock.ElapsedMilliseconds, default, ClickButton.Left, key));
            }
        }
        return CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && recording)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                var at = new PixelPoint(data.pt.X, data.pt.Y);
                switch (wParam.ToInt32())
                {
                    case WM_MOUSEMOVE:
                        Emit(new RecordedEvent(RecordedKind.MouseMove, clock.ElapsedMilliseconds, at, ClickButton.Left, Key.None));
                        break;
                    case WM_LBUTTONDOWN: EmitButton(RecordedKind.MouseDown, at, ClickButton.Left); break;
                    case WM_LBUTTONUP: EmitButton(RecordedKind.MouseUp, at, ClickButton.Left); break;
                    case WM_RBUTTONDOWN: EmitButton(RecordedKind.MouseDown, at, ClickButton.Right); break;
                    case WM_RBUTTONUP: EmitButton(RecordedKind.MouseUp, at, ClickButton.Right); break;
                    case WM_MBUTTONDOWN: EmitButton(RecordedKind.MouseDown, at, ClickButton.Middle); break;
                    case WM_MBUTTONUP: EmitButton(RecordedKind.MouseUp, at, ClickButton.Middle); break;
                }
            }
        }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    void EmitButton(RecordedKind kind, PixelPoint at, ClickButton button) =>
        Emit(new RecordedEvent(kind, clock.ElapsedMilliseconds, at, button, Key.None));

    void Emit(RecordedEvent recorded) => Recorded?.Invoke(recorded);

    public void Stop()
    {
        recording = false;
        var current = thread;
        if (current == null) return;
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        current.Join(1000);
        thread = null;
        threadId = 0;
    }

    public void Dispose() => Stop();

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
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
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? moduleName);
}
