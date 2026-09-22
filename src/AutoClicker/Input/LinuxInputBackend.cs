using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Input;

/// <summary>
/// Sends input through X11's XTest extension. On a Wayland session this reaches XWayland
/// windows only, so the app asks users to log into an Xorg session.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class LinuxInputBackend : IInputBackend
{
    readonly IntPtr display;
    readonly string? failure;

    public LinuxInputBackend()
    {
        try
        {
            X11.XInitThreads();
            display = X11.XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                failure = "Couldn't connect to the X display. " + WaylandHint();
            }
            else if (XTest.XTestQueryExtension(display, out _, out _, out _, out _) == 0)
            {
                failure = "This X server doesn't have the XTest extension, which is needed to send clicks and keys.";
            }
        }
        catch (DllNotFoundException)
        {
            failure = "libX11 / libXtst are missing. Install them, e.g. 'sudo apt install libx11-6 libxtst6'.";
        }
    }

    static string WaylandHint() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
            ? "You're on a Wayland session, which blocks apps from sending input to other windows. Log out and pick an Xorg session at the login screen."
            : "Make sure DISPLAY is set and you're running inside a graphical session.";

    public bool IsAvailable => failure == null;
    public string? UnavailableReason => failure;

    public void SendClick(ClickButton button, int clickCount)
    {
        if (!IsAvailable) return;
        uint x11Button = button switch
        {
            ClickButton.Right => 3u,
            ClickButton.Middle => 2u,
            _ => 1u
        };
        for (int i = 0; i < clickCount; i++)
        {
            XTest.XTestFakeButtonEvent(display, x11Button, true, 0);
            XTest.XTestFakeButtonEvent(display, x11Button, false, 0);
        }
        X11.XFlush(display);
    }

    public void SendKey(Key key, bool down)
    {
        if (!IsAvailable || !KeyMap.TryGet(key, out var codes)) return;
        ulong keysym = X11.XStringToKeysym(codes.X11Keysym);
        if (keysym == 0) return;
        byte keycode = X11.XKeysymToKeycode(display, keysym);
        if (keycode == 0) return;
        XTest.XTestFakeKeyEvent(display, keycode, down, 0);
        X11.XFlush(display);
    }

    public PixelPoint GetCursorPosition()
    {
        if (!IsAvailable) return default;
        var root = X11.XDefaultRootWindow(display);
        if (X11.XQueryPointer(display, root, out _, out _, out int rootX, out int rootY,
                out _, out _, out _) == 0)
            return default;
        return new PixelPoint(rootX, rootY);
    }

    public void SetCursorPosition(PixelPoint point)
    {
        if (!IsAvailable) return;
        var root = X11.XDefaultRootWindow(display);
        X11.XWarpPointer(display, IntPtr.Zero, root, 0, 0, 0, 0, point.X, point.Y);
        X11.XFlush(display);
    }

    public void Dispose()
    {
        if (display != IntPtr.Zero) X11.XCloseDisplay(display);
    }

    internal delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    internal static class X11
    {
        const string Lib = "libX11.so.6";
        [DllImport(Lib)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers,
            IntPtr grabWindow, [MarshalAs(UnmanagedType.I1)] bool ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(Lib)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
        [DllImport(Lib)] public static extern IntPtr XSetErrorHandler(XErrorHandler handler);
        [DllImport(Lib)] public static extern IntPtr XSetErrorHandler(IntPtr handler);
        [DllImport(Lib)] public static extern int XPending(IntPtr display);
        [DllImport(Lib)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
        [DllImport(Lib)] public static extern int XInitThreads();
        [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr display);
        [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr display);
        [DllImport(Lib)] public static extern int XFlush(IntPtr display);
        [DllImport(Lib)] public static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.I1)] bool discard);
        [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern ulong XStringToKeysym(string name);
        [DllImport(Lib)] public static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
        [DllImport(Lib)] public static extern int XWarpPointer(IntPtr display, IntPtr srcWindow, IntPtr destWindow,
            int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
        [DllImport(Lib)] public static extern int XQueryPointer(IntPtr display, IntPtr window,
            out IntPtr rootReturn, out IntPtr childReturn, out int rootX, out int rootY,
            out int winX, out int winY, out uint maskReturn);
    }

    static class XTest
    {
        const string Lib = "libXtst.so.6";
        [DllImport(Lib)] public static extern int XTestQueryExtension(IntPtr display,
            out int eventBase, out int errorBase, out int majorVersion, out int minorVersion);
        [DllImport(Lib)] public static extern int XTestFakeButtonEvent(IntPtr display, uint button,
            [MarshalAs(UnmanagedType.I1)] bool isPress, ulong delay);
        [DllImport(Lib)] public static extern int XTestFakeKeyEvent(IntPtr display, byte keycode,
            [MarshalAs(UnmanagedType.I1)] bool isPress, ulong delay);
    }
}
