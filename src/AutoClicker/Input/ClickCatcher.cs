using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia;

namespace AutoClicker.Input;

/// <summary>
/// Catches the next left click anywhere on screen and keeps it from reaching the app under the
/// mouse, so picking a spot by clicking doesn't also press whatever is there. Clicks this app
/// sends itself are let through.
/// </summary>
public static class ClickCatcher
{
    /// <summary>
    /// Starts catching. <paramref name="clicked"/> runs once, on a background thread, with where
    /// the button went down. Dispose to stop; the release of a caught click is swallowed too, even
    /// if that comes after Dispose. Null if this system won't allow it (no permission, say).
    /// </summary>
    public static IDisposable? Start(Action<PixelPoint> clicked)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsCatcher.Start(clicked);
            if (OperatingSystem.IsMacOS()) return MacCatcher.Start(clicked);
            if (OperatingSystem.IsLinux()) return LinuxCatcher.Start(clicked);
        }
        catch (Exception) { /* no catching; the countdown still picks */ }
        return null;
    }

    /// <summary>How long a caught press waits for its release before the catcher gives up on it.</summary>
    const int ReleaseWaitMs = 3000;

    /// <summary>Low-level mouse hook on a thread of its own with a message loop.</summary>
    [SupportedOSPlatform("windows")]
    sealed class WindowsCatcher : IDisposable
    {
        const int WH_MOUSE_LL = 14, HC_ACTION = 0;
        const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        const uint WM_QUIT = 0x0012, LLMHF_INJECTED = 0x01;

        readonly Action<PixelPoint> clicked;
        readonly HookProc proc;   // kept alive while hooked
        readonly ManualResetEventSlim released = new(false);
        IntPtr hook;
        uint threadId;
        volatile bool caught, active = true;

        WindowsCatcher(Action<PixelPoint> clicked)
        {
            this.clicked = clicked;
            proc = OnMouse;
        }

        public static WindowsCatcher? Start(Action<PixelPoint> clicked)
        {
            var catcher = new WindowsCatcher(clicked);
            using var ready = new ManualResetEventSlim(false);
            new Thread(() => catcher.Loop(ready)) { IsBackground = true, Name = "AutoClicker.ClickCatcher" }.Start();
            ready.Wait(2000);
            return catcher.hook != IntPtr.Zero ? catcher : null;
        }

        void Loop(ManualResetEventSlim ready)
        {
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            threadId = GetCurrentThreadId();
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref msg);
            UnhookWindowsHookEx(hook);
        }

        IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HC_ACTION)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool ours = (data.flags & LLMHF_INJECTED) != 0;
                int message = wParam.ToInt32();
                if (!ours && message == WM_LBUTTONDOWN && active && !caught)
                {
                    caught = true;
                    clicked(new PixelPoint(data.pt.X, data.pt.Y));
                    return 1;   // swallowed
                }
                if (!ours && message == WM_LBUTTONUP && caught && !released.IsSet)
                {
                    released.Set();
                    return 1;
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            active = false;
            // Hold on until the caught click's release has been swallowed, or the app under the
            // mouse would get a release with no press.
            new Thread(() =>
            {
                if (caught) released.Wait(ReleaseWaitMs);
                if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }) { IsBackground = true }.Start();
        }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    }

    /// <summary>
    /// An active (not listen-only) event tap: returning null from it drops the event. Needs the
    /// Accessibility permission the app already asks for to send clicks.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class MacCatcher : IDisposable
    {
        const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const uint kCGSessionEventTap = 1, kCGHeadInsertEventTap = 0, kCGEventTapOptionDefault = 0;
        const uint LeftMouseDown = 1, LeftMouseUp = 2;
        const uint TapDisabledByTimeout = 0xFFFFFFFE, TapDisabledByUserInput = 0xFFFFFFFF;
        const uint kCGEventSourceUserData = 42;

        readonly Action<PixelPoint> clicked;
        readonly TapCallback callback;   // kept alive while the tap exists
        readonly ManualResetEventSlim released = new(false);
        IntPtr tap, runLoop;
        volatile bool caught, active = true;

        MacCatcher(Action<PixelPoint> clicked)
        {
            this.clicked = clicked;
            callback = OnEvent;
        }

        public static MacCatcher? Start(Action<PixelPoint> clicked)
        {
            var catcher = new MacCatcher(clicked);
            using var ready = new ManualResetEventSlim(false);
            new Thread(() => catcher.Loop(ready)) { IsBackground = true, Name = "AutoClicker.ClickCatcher" }.Start();
            ready.Wait(3000);
            return catcher.tap != IntPtr.Zero ? catcher : null;
        }

        void Loop(ManualResetEventSlim ready)
        {
            IntPtr source = IntPtr.Zero;
            try
            {
                tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionDefault,
                    (1UL << (int)LeftMouseDown) | (1UL << (int)LeftMouseUp), callback, IntPtr.Zero);
                if (tap == IntPtr.Zero) return;
                source = CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, IntPtr.Zero);
                runLoop = CFRunLoopGetCurrent();
                CFRunLoopAddSource(runLoop, source, CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopCommonModes", 0x08000100));
                CGEventTapEnable(tap, true);
            }
            finally
            {
                ready.Set();
            }

            CFRunLoopRun();
            CGEventTapEnable(tap, false);
            if (source != IntPtr.Zero) CFRelease(source);
            CFRelease(tap);
        }

        IntPtr OnEvent(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo)
        {
            if (type is TapDisabledByTimeout or TapDisabledByUserInput)
            {
                CGEventTapEnable(tap, true);
                return evt;
            }
            if (CGEventGetIntegerValueField(evt, kCGEventSourceUserData) == MacInputBackend.SyntheticMarker) return evt;

            if (type == LeftMouseDown && active && !caught)
            {
                caught = true;
                var at = CGEventGetLocation(evt);
                clicked(new PixelPoint((int)Math.Round(at.X), (int)Math.Round(at.Y)));
                return IntPtr.Zero;
            }
            if (type == LeftMouseUp && caught && !released.IsSet)
            {
                released.Set();
                return IntPtr.Zero;
            }
            return evt;
        }

        public void Dispose()
        {
            active = false;
            new Thread(() =>
            {
                if (caught) released.Wait(ReleaseWaitMs);
                if (runLoop != IntPtr.Zero) CFRunLoopStop(runLoop);
            }) { IsBackground = true }.Start();
        }

        delegate IntPtr TapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo);
        [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }

        [DllImport(CoreGraphics)] static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong mask, TapCallback callback, IntPtr userInfo);
        [DllImport(CoreGraphics)] static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
        [DllImport(CoreGraphics)] static extern CGPoint CGEventGetLocation(IntPtr evt);
        [DllImport(CoreGraphics)] static extern long CGEventGetIntegerValueField(IntPtr evt, uint field);
        [DllImport(CoreFoundation)] static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, IntPtr order);
        [DllImport(CoreFoundation)] static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CoreFoundation)] static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
        [DllImport(CoreFoundation)] static extern void CFRunLoopRun();
        [DllImport(CoreFoundation)] static extern void CFRunLoopStop(IntPtr runLoop);
        [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);
        [DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    }

    /// <summary>
    /// Grabs the pointer on the root window over a connection of its own: while grabbed, button
    /// presses come to us instead of the window under the mouse.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed class LinuxCatcher : IDisposable
    {
        const string Lib = "libX11.so.6";
        const int ButtonPress = 4, ButtonRelease = 5, GrabModeAsync = 1, GrabSuccess = 0;
        const long ButtonPressMask = 1L << 2, ButtonReleaseMask = 1L << 3;

        readonly IntPtr display;
        readonly Action<PixelPoint> clicked;
        volatile bool active = true;

        LinuxCatcher(IntPtr display, Action<PixelPoint> clicked)
        {
            this.display = display;
            this.clicked = clicked;
        }

        public static LinuxCatcher? Start(Action<PixelPoint> clicked)
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            IntPtr root = XDefaultRootWindow(display);
            if (XGrabPointer(display, root, false, (uint)(ButtonPressMask | ButtonReleaseMask),
                    GrabModeAsync, GrabModeAsync, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != GrabSuccess)
            {
                XCloseDisplay(display);
                return null;
            }
            XFlush(display);
            var catcher = new LinuxCatcher(display, clicked);
            new Thread(catcher.Loop) { IsBackground = true, Name = "AutoClicker.ClickCatcher" }.Start();
            return catcher;
        }

        void Loop()
        {
            var evt = Marshal.AllocHGlobal(192);   // sizeof(XEvent)
            bool caught = false;
            long deadline = 0;
            try
            {
                while (true)
                {
                    if (!active && (!caught || Environment.TickCount64 > deadline)) break;
                    if (XPending(display) == 0) { Thread.Sleep(10); continue; }
                    XNextEvent(display, evt);
                    int type = Marshal.ReadInt32(evt, 0);
                    // XButtonEvent: x_root @72, y_root @76, button @84 (64-bit layout)
                    uint button = (uint)Marshal.ReadInt32(evt, 84);
                    if (type == ButtonPress && button == 1 && active && !caught)
                    {
                        caught = true;
                        deadline = Environment.TickCount64 + ReleaseWaitMs;
                        clicked(new PixelPoint(Marshal.ReadInt32(evt, 72), Marshal.ReadInt32(evt, 76)));
                    }
                    else if (type == ButtonRelease && button == 1 && caught) break;
                }
            }
            finally
            {
                XUngrabPointer(display, IntPtr.Zero);
                XCloseDisplay(display);
                Marshal.FreeHGlobal(evt);
            }
        }

        public void Dispose() => active = false;

        [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] static extern int XGrabPointer(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
            uint eventMask, int pointerMode, int keyboardMode, IntPtr confineTo, IntPtr cursor, IntPtr time);
        [DllImport(Lib)] static extern int XUngrabPointer(IntPtr display, IntPtr time);
        [DllImport(Lib)] static extern int XPending(IntPtr display);
        [DllImport(Lib)] static extern int XNextEvent(IntPtr display, IntPtr evt);
        [DllImport(Lib)] static extern int XFlush(IntPtr display);
    }
}
