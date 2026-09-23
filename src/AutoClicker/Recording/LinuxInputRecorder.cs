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
/// Watches input through X11's RECORD extension. Needs two display connections: one to
/// control the context, one that blocks while delivering events.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class LinuxInputRecorder : IInputRecorder
{
    const string X11Lib = "libX11.so.6";
    const string XtstLib = "libXtst.so.6";

    const int KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;
    const int XRecordFromServer = 0;
    const int XRecordAllClients = 3;

    readonly InterceptCallback callback;   // kept alive while the context is enabled
    readonly Stopwatch clock = new();
    IntPtr controlDisplay, dataDisplay;
    IntPtr context;
    Thread? thread;
    volatile bool recording;
    string? startError;

    public event Action<RecordedEvent>? Recorded;

    public bool IsRecording => recording;

    public LinuxInputRecorder()
    {
        callback = OnIntercept;
    }

    public bool TryStart(out string? error)
    {
        Stop();
        startError = null;
        clock.Restart();

        try
        {
            controlDisplay = XOpenDisplay(IntPtr.Zero);
            dataDisplay = XOpenDisplay(IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            error = "libX11 is missing, so recording isn't available.";
            return false;
        }

        if (controlDisplay == IntPtr.Zero || dataDisplay == IntPtr.Zero)
        {
            Cleanup();
            error = "Couldn't connect to the X display, so recording isn't available.";
            return false;
        }

        if (XRecordQueryVersion(controlDisplay, out _, out _) == 0)
        {
            Cleanup();
            error = "This X server doesn't have the RECORD extension, which recording needs. " +
                    "On some systems it is enabled with: Section \"Module\" Load \"record\".";
            return false;
        }

        var range = XRecordAllocRange();
        if (range == IntPtr.Zero)
        {
            Cleanup();
            error = "Couldn't allocate an X RECORD range.";
            return false;
        }

        // XRecordRange is a struct of byte-pair ranges; device_events sits at offset 20
        // (after two XRecordRange8 and two XRecordRange16 members).
        Marshal.WriteByte(range, 20, KeyPress);
        Marshal.WriteByte(range, 21, MotionNotify);

        int clients = XRecordAllClients;
        context = XRecordCreateContext(controlDisplay, 0, ref clients, 1, ref range, 1);
        XFree(range);

        if (context == IntPtr.Zero)
        {
            Cleanup();
            error = "Couldn't create an X RECORD context.";
            return false;
        }

        XSync(controlDisplay, false);

        using var ready = new ManualResetEventSlim(false);
        thread = new Thread(() => RecordLoop(ready)) { IsBackground = true, Name = "AutoClicker.Recorder" };
        thread.Start();
        ready.Wait(2000);

        error = startError;
        return recording;
    }

    void RecordLoop(ManualResetEventSlim ready)
    {
        recording = true;
        ready.Set();

        // Blocks until the context is disabled from the control connection.
        if (XRecordEnableContext(dataDisplay, context, callback, IntPtr.Zero) == 0)
            startError = "X RECORD wouldn't start.";

        recording = false;
    }

    void OnIntercept(IntPtr closure, IntPtr recordedData)
    {
        try
        {
            if (!recording) return;

            var header = Marshal.PtrToStructure<XRecordInterceptData>(recordedData);
            if (header.Category != XRecordFromServer || header.Data == IntPtr.Zero || header.DataLength < 32)
                return;

            byte type = Marshal.ReadByte(header.Data, 0);
            byte detail = Marshal.ReadByte(header.Data, 1);
            short rootX = Marshal.ReadInt16(header.Data, 20);
            short rootY = Marshal.ReadInt16(header.Data, 22);
            var at = new PixelPoint(rootX, rootY);
            long now = clock.ElapsedMilliseconds;

            switch (type)
            {
                case ButtonPress:
                case ButtonRelease:
                {
                    // Buttons 4-7 are scroll wheel directions, which macros don't replay.
                    if (detail is < 1 or > 3) return;
                    var button = detail switch
                    {
                        2 => ClickButton.Middle,
                        3 => ClickButton.Right,
                        _ => ClickButton.Left
                    };
                    Recorded?.Invoke(new RecordedEvent(
                        type == ButtonPress ? RecordedKind.MouseDown : RecordedKind.MouseUp,
                        now, at, button, Key.None));
                    break;
                }

                case MotionNotify:
                    Recorded?.Invoke(new RecordedEvent(RecordedKind.MouseMove, now, at, ClickButton.Left, Key.None));
                    break;

                case KeyPress:
                case KeyRelease:
                {
                    var key = KeyFromKeycode(detail);
                    if (key != Key.None)
                        Recorded?.Invoke(new RecordedEvent(
                            type == KeyPress ? RecordedKind.KeyDown : RecordedKind.KeyUp,
                            now, at, ClickButton.Left, key));
                    break;
                }
            }
        }
        finally
        {
            XRecordFreeData(recordedData);
        }
    }

    Key KeyFromKeycode(byte keycode)
    {
        ulong keysym = XkbKeycodeToKeysym(controlDisplay, keycode, 0, 0);
        if (keysym == 0) return Key.None;
        var namePtr = XKeysymToString(keysym);
        if (namePtr == IntPtr.Zero) return Key.None;
        var name = Marshal.PtrToStringAnsi(namePtr);
        return name != null && KeyMap.TryFromX11Keysym(name, out var key) ? key : Key.None;
    }

    public void Stop()
    {
        if (context != IntPtr.Zero && controlDisplay != IntPtr.Zero)
        {
            XRecordDisableContext(controlDisplay, context);
            XSync(controlDisplay, false);
        }

        var current = thread;
        if (current != null)
        {
            current.Join(1000);
            thread = null;
        }

        recording = false;
        if (context != IntPtr.Zero && controlDisplay != IntPtr.Zero)
        {
            XRecordFreeContext(controlDisplay, context);
            context = IntPtr.Zero;
        }
        Cleanup();
    }

    void Cleanup()
    {
        if (dataDisplay != IntPtr.Zero) { XCloseDisplay(dataDisplay); dataDisplay = IntPtr.Zero; }
        if (controlDisplay != IntPtr.Zero) { XCloseDisplay(controlDisplay); controlDisplay = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    delegate void InterceptCallback(IntPtr closure, IntPtr recordedData);

    [StructLayout(LayoutKind.Sequential)]
    struct XRecordInterceptData
    {
        public IntPtr IdBase;
        public IntPtr ServerTime;
        public IntPtr ClientSequence;
        public int Category;
        public int ClientSwapped;
        public IntPtr Data;
        public IntPtr DataLength;
    }

    [DllImport(X11Lib)] static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport(X11Lib)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11Lib)] static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.I1)] bool discard);
    [DllImport(X11Lib)] static extern int XFree(IntPtr data);
    [DllImport(X11Lib)] static extern ulong XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);
    [DllImport(X11Lib)] static extern IntPtr XKeysymToString(ulong keysym);

    [DllImport(XtstLib)] static extern int XRecordQueryVersion(IntPtr display, out int major, out int minor);
    [DllImport(XtstLib)] static extern IntPtr XRecordAllocRange();
    [DllImport(XtstLib)] static extern IntPtr XRecordCreateContext(IntPtr display, int flags,
        ref int clientSpecs, int numClientSpecs, ref IntPtr ranges, int numRanges);
    [DllImport(XtstLib)] static extern int XRecordEnableContext(IntPtr display, IntPtr context,
        InterceptCallback callback, IntPtr closure);
    [DllImport(XtstLib)] static extern int XRecordDisableContext(IntPtr display, IntPtr context);
    [DllImport(XtstLib)] static extern int XRecordFreeContext(IntPtr display, IntPtr context);
    [DllImport(XtstLib)] static extern void XRecordFreeData(IntPtr data);
}
