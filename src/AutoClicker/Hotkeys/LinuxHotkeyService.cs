using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AutoClicker.Input;
using Avalonia.Input;
using static AutoClicker.Input.LinuxInputBackend;

namespace AutoClicker.Hotkeys;

/// <summary>
/// X11 XGrabKey on the root window, watched from a polling thread on its own display
/// connection. Wayland has no equivalent, so the hotkey is X11/XWayland only.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class LinuxHotkeyService : IHotkeyService
{
    const int KeyPress = 2;
    const int GrabModeAsync = 1;
    // Grab each combination of the "don't care" modifiers so Num Lock or Caps Lock don't block it.
    static readonly uint[] IgnoredModifierMasks = [0, 2 /* Lock */, 16 /* Mod2 */, 2 | 16];

    readonly object gate = new();
    readonly XErrorHandler errorHandler;   // kept alive while X11 may call back into it
    IntPtr display;
    IntPtr root;
    Thread? thread;
    volatile bool running;
    byte grabbedKeycode;
    volatile bool grabFailed;

    public event Action? Pressed;

    public LinuxHotkeyService()
    {
        errorHandler = ErrorHandler;
    }

    public bool TryRegister(Key key, out string? error)
    {
        Unregister();

        if (!KeyMap.TryGet(key, out var codes))
        {
            error = "That key can't be used as a hotkey.";
            return false;
        }

        try
        {
            display = X11.XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            error = "libX11 is missing, so global hotkeys aren't available.";
            return false;
        }

        if (display == IntPtr.Zero)
        {
            error = "no X display, so the hotkey can't be grabbed";
            return false;
        }

        root = X11.XDefaultRootWindow(display);
        ulong keysym = X11.XStringToKeysym(codes.X11Keysym);
        grabbedKeycode = keysym == 0 ? (byte)0 : X11.XKeysymToKeycode(display, keysym);
        if (grabbedKeycode == 0)
        {
            Cleanup();
            error = "your keyboard layout has no such key";
            return false;
        }

        grabFailed = false;
        var previousHandler = X11.XSetErrorHandler(errorHandler);
        foreach (var mask in IgnoredModifierMasks)
            X11.XGrabKey(display, grabbedKeycode, mask, root, true, GrabModeAsync, GrabModeAsync);
        X11.XSync(display, false);
        X11.XSetErrorHandler(previousHandler);

        if (grabFailed)
        {
            UngrabAll();
            Cleanup();
            error = "another app is already using it";
            return false;
        }

        running = true;
        thread = new Thread(WatchLoop) { IsBackground = true, Name = "AutoClicker.Hotkey" };
        thread.Start();

        error = null;
        return true;
    }

    int ErrorHandler(IntPtr display, IntPtr errorEvent)
    {
        grabFailed = true;
        return 0;
    }

    void WatchLoop()
    {
        // Polling (rather than blocking in XNextEvent) keeps shutdown simple and costs little.
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            while (running)
            {
                bool idle = true;
                lock (gate)
                {
                    if (display == IntPtr.Zero) break;
                    while (X11.XPending(display) > 0)
                    {
                        idle = false;
                        X11.XNextEvent(display, buffer);
                        if (Marshal.ReadInt32(buffer) == KeyPress) Pressed?.Invoke();
                    }
                }
                if (idle) Thread.Sleep(20);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Unregister()
    {
        running = false;
        var current = thread;
        if (current != null)
        {
            current.Join(500);
            thread = null;
        }
        lock (gate)
        {
            if (display == IntPtr.Zero) return;
            UngrabAll();
            Cleanup();
        }
    }

    void UngrabAll()
    {
        if (display == IntPtr.Zero || grabbedKeycode == 0) return;
        foreach (var mask in IgnoredModifierMasks)
            X11.XUngrabKey(display, grabbedKeycode, mask, root);
        X11.XFlush(display);
        grabbedKeycode = 0;
    }

    void Cleanup()
    {
        if (display != IntPtr.Zero)
        {
            X11.XCloseDisplay(display);
            display = IntPtr.Zero;
        }
        root = IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
