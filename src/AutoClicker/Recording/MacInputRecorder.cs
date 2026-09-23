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
/// A listen-only CGEventTap on its own run loop. Needs the same Accessibility permission
/// as sending input.
/// </summary>
[SupportedOSPlatform("macos")]
sealed class MacInputRecorder : IInputRecorder
{
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    const uint kCGSessionEventTap = 1;
    const uint kCGHeadInsertEventTap = 0;
    const uint kCGEventTapOptionListenOnly = 1;
    const uint kCGKeyboardEventKeycode = 9;
    const uint kCGEventSourceUserData = 42;

    const uint LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4;
    const uint MouseMoved = 5, LeftMouseDragged = 6, RightMouseDragged = 7;
    const uint KeyDown = 10, KeyUp = 11, FlagsChanged = 12;
    const uint OtherMouseDown = 25, OtherMouseUp = 26, OtherMouseDragged = 27;

    const ulong FlagShift = 0x00020000, FlagControl = 0x00040000;
    const ulong FlagAlternate = 0x00080000, FlagCommand = 0x00100000;

    readonly EventTapCallback callback;   // kept alive while the tap exists
    readonly Stopwatch clock = new();
    Thread? thread;
    IntPtr runLoop;
    IntPtr tapPort;
    IntPtr runLoopSource;
    volatile bool recording;
    string? startError;
    ulong lastFlags;

    public event Action<RecordedEvent>? Recorded;

    public bool IsRecording => recording;

    public MacInputRecorder()
    {
        callback = OnEvent;
    }

    public bool TryStart(out string? error)
    {
        Stop();
        startError = null;
        clock.Restart();

        if (!AXIsProcessTrusted())
        {
            error = "macOS hasn't given this app permission to watch input. Grant it under " +
                    "System Settings > Privacy & Security > Accessibility, then reopen the app.";
            return false;
        }

        using var ready = new ManualResetEventSlim(false);
        thread = new Thread(() => TapLoop(ready)) { IsBackground = true, Name = "AutoClicker.Recorder" };
        thread.Start();
        ready.Wait(3000);

        error = startError;
        return recording;
    }

    void TapLoop(ManualResetEventSlim ready)
    {
        try
        {
            ulong mask =
                (1UL << (int)LeftMouseDown) | (1UL << (int)LeftMouseUp) |
                (1UL << (int)RightMouseDown) | (1UL << (int)RightMouseUp) |
                (1UL << (int)OtherMouseDown) | (1UL << (int)OtherMouseUp) |
                (1UL << (int)MouseMoved) |
                (1UL << (int)LeftMouseDragged) | (1UL << (int)RightMouseDragged) |
                (1UL << (int)OtherMouseDragged) |
                (1UL << (int)KeyDown) | (1UL << (int)KeyUp) | (1UL << (int)FlagsChanged);

            tapPort = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap,
                kCGEventTapOptionListenOnly, mask, callback, IntPtr.Zero);
            if (tapPort == IntPtr.Zero)
            {
                startError = "macOS refused the event tap needed for recording.";
                return;
            }

            runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, tapPort, 0);
            runLoop = CFRunLoopGetCurrent();
            CFRunLoopAddSource(runLoop, runLoopSource, GetCommonModes());
            CGEventTapEnable(tapPort, true);
            recording = true;
        }
        finally
        {
            ready.Set();
        }

        if (recording) CFRunLoopRun();

        recording = false;
        if (tapPort != IntPtr.Zero)
        {
            CGEventTapEnable(tapPort, false);
            CFRelease(tapPort);
            tapPort = IntPtr.Zero;
        }
        if (runLoopSource != IntPtr.Zero)
        {
            CFRelease(runLoopSource);
            runLoopSource = IntPtr.Zero;
        }
        runLoop = IntPtr.Zero;
    }

    IntPtr OnEvent(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo)
    {
        if (!recording) return evt;

        // Ignore the events this app posts, or playback would record itself.
        if (CGEventGetIntegerValueField(evt, kCGEventSourceUserData) == MacInputBackend.SyntheticMarker)
            return evt;

        long now = clock.ElapsedMilliseconds;
        var location = CGEventGetLocation(evt);
        var at = new PixelPoint((int)Math.Round(location.X), (int)Math.Round(location.Y));

        switch (type)
        {
            case LeftMouseDown: Emit(RecordedKind.MouseDown, now, at, ClickButton.Left); break;
            case LeftMouseUp: Emit(RecordedKind.MouseUp, now, at, ClickButton.Left); break;
            case RightMouseDown: Emit(RecordedKind.MouseDown, now, at, ClickButton.Right); break;
            case RightMouseUp: Emit(RecordedKind.MouseUp, now, at, ClickButton.Right); break;
            case OtherMouseDown: Emit(RecordedKind.MouseDown, now, at, ClickButton.Middle); break;
            case OtherMouseUp: Emit(RecordedKind.MouseUp, now, at, ClickButton.Middle); break;

            case MouseMoved:
            case LeftMouseDragged:
            case RightMouseDragged:
            case OtherMouseDragged:
                Emit(RecordedKind.MouseMove, now, at, ClickButton.Left);
                break;

            case KeyDown:
            case KeyUp:
            {
                ushort code = (ushort)CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
                if (KeyMap.TryFromMacKeyCode(code, out var key))
                    Recorded?.Invoke(new RecordedEvent(
                        type == KeyDown ? RecordedKind.KeyDown : RecordedKind.KeyUp,
                        now, at, ClickButton.Left, key));
                break;
            }

            case FlagsChanged:
            {
                // Modifier keys arrive as a flag change, not a key event; whether the key went
                // down or up is the difference against the flags we saw last time.
                ushort code = (ushort)CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
                ulong flags = CGEventGetFlags(evt);
                if (KeyMap.TryFromMacKeyCode(code, out var key))
                {
                    ulong bit = KeyMap.ModifierOf(key) switch
                    {
                        Modifiers.Shift => FlagShift,
                        Modifiers.Control => FlagControl,
                        Modifiers.Alt => FlagAlternate,
                        Modifiers.Meta => FlagCommand,
                        _ => 0
                    };
                    if (bit != 0)
                    {
                        bool down = (flags & bit) != 0;
                        bool wasDown = (lastFlags & bit) != 0;
                        if (down != wasDown)
                            Recorded?.Invoke(new RecordedEvent(
                                down ? RecordedKind.KeyDown : RecordedKind.KeyUp,
                                now, at, ClickButton.Left, key));
                    }
                }
                lastFlags = flags;
                break;
            }
        }

        return evt;
    }

    void Emit(RecordedKind kind, long now, PixelPoint at, ClickButton button) =>
        Recorded?.Invoke(new RecordedEvent(kind, now, at, button, Key.None));

    public void Stop()
    {
        recording = false;
        var current = thread;
        if (current == null) return;
        if (runLoop != IntPtr.Zero) CFRunLoopStop(runLoop);
        current.Join(1000);
        thread = null;
    }

    public void Dispose() => Stop();

    static IntPtr GetCommonModes() => CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopCommonModes", 0x08000100);

    delegate IntPtr EventTapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo);

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint { public double X, Y; }

    [DllImport(CoreGraphics)]
    static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest,
        EventTapCallback callback, IntPtr userInfo);

    [DllImport(CoreGraphics)] static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
    [DllImport(CoreGraphics)] static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CoreGraphics)] static extern long CGEventGetIntegerValueField(IntPtr evt, uint field);
    [DllImport(CoreGraphics)] static extern ulong CGEventGetFlags(IntPtr evt);
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] static extern bool AXIsProcessTrusted();

    [DllImport(CoreFoundation)] static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, IntPtr order);
    [DllImport(CoreFoundation)] static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(CoreFoundation)] static extern void CFRunLoopRun();
    [DllImport(CoreFoundation)] static extern void CFRunLoopStop(IntPtr runLoop);
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
}
