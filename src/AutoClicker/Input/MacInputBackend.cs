using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Input;

/// <summary>
/// Sends input through Quartz CGEvents. macOS refuses synthetic input unless the app is
/// listed under System Settings > Privacy &amp; Security > Accessibility.
/// </summary>
[SupportedOSPlatform("macos")]
sealed class MacInputBackend : IInputBackend
{
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    const uint kCGHIDEventTap = 0;
    const int kCGEventSourceStateHIDSystemState = 1;
    const uint kCGMouseEventClickState = 1;
    const uint kCGEventSourceUserData = 42;

    /// <summary>Stamped on every event we post so the recorder can tell ours from the user's.</summary>
    internal const long SyntheticMarker = 0x4155434C;   // 'AUCL'

    const uint kCGEventLeftMouseDown = 1, kCGEventLeftMouseUp = 2;
    const uint kCGEventRightMouseDown = 3, kCGEventRightMouseUp = 4;
    const uint kCGEventMouseMoved = 5;
    const uint kCGEventLeftMouseDragged = 6, kCGEventRightMouseDragged = 7;
    const uint kCGEventOtherMouseDown = 25, kCGEventOtherMouseUp = 26, kCGEventOtherMouseDragged = 27;

    const ulong kCGEventFlagMaskShift = 0x00020000;
    const ulong kCGEventFlagMaskControl = 0x00040000;
    const ulong kCGEventFlagMaskAlternate = 0x00080000;
    const ulong kCGEventFlagMaskCommand = 0x00100000;

    readonly IntPtr source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
    ulong flags;
    ClickButton? heldButton;

    public bool IsAvailable => AXIsProcessTrusted();

    public string UnavailableReason =>
        "macOS hasn't given this app permission to control your computer. Open System Settings > " +
        "Privacy & Security > Accessibility, switch Auto Clicker on, then quit and reopen the app.";

    public void SendClick(ClickButton button, int clickCount)
    {
        (uint downType, uint upType, uint macButton) = button switch
        {
            ClickButton.Right => (kCGEventRightMouseDown, kCGEventRightMouseUp, 1u),
            ClickButton.Middle => (kCGEventOtherMouseDown, kCGEventOtherMouseUp, 2u),
            _ => (kCGEventLeftMouseDown, kCGEventLeftMouseUp, 0u)
        };

        var at = CurrentLocation();
        for (int i = 1; i <= clickCount; i++)
        {
            // Click state tells macOS this is the 1st, 2nd... click of a multi-click.
            PostMouse(downType, at, macButton, i);
            PostMouse(upType, at, macButton, i);
        }
    }

    public void SendMouseButton(ClickButton button, bool down)
    {
        (uint type, uint macButton) = (button, down) switch
        {
            (ClickButton.Right, true) => (kCGEventRightMouseDown, 1u),
            (ClickButton.Right, false) => (kCGEventRightMouseUp, 1u),
            (ClickButton.Middle, true) => (kCGEventOtherMouseDown, 2u),
            (ClickButton.Middle, false) => (kCGEventOtherMouseUp, 2u),
            (_, true) => (kCGEventLeftMouseDown, 0u),
            (_, false) => (kCGEventLeftMouseUp, 0u)
        };

        heldButton = down ? button : null;
        PostMouse(type, CurrentLocation(), macButton, 1);
    }

    public void SendKey(Key key, bool down)
    {
        if (!KeyMap.TryGet(key, out var codes)) return;

        // Posting a modifier keycode doesn't update the global flags, so track them ourselves
        // and stamp them onto every event we post.
        ulong bit = key switch
        {
            Key.LeftShift or Key.RightShift => kCGEventFlagMaskShift,
            Key.LeftCtrl or Key.RightCtrl => kCGEventFlagMaskControl,
            Key.LeftAlt or Key.RightAlt => kCGEventFlagMaskAlternate,
            Key.LWin or Key.RWin => kCGEventFlagMaskCommand,
            _ => 0
        };
        if (bit != 0)
        {
            if (down) flags |= bit; else flags &= ~bit;
        }

        var evt = CGEventCreateKeyboardEvent(source, codes.MacKeyCode, down);
        if (evt == IntPtr.Zero) return;
        CGEventSetFlags(evt, flags);
        CGEventSetIntegerValueField(evt, kCGEventSourceUserData, SyntheticMarker);
        CGEventPost(kCGHIDEventTap, evt);
        CFRelease(evt);
    }

    public PixelPoint GetCursorPosition()
    {
        var point = CurrentLocation();
        return new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    public void SetCursorPosition(PixelPoint point)
    {
        var target = new CGPoint { X = point.X, Y = point.Y };

        // Warping doesn't generate the drag events apps listen for, so while a button is
        // held down we post a *MouseDragged event instead — that's what makes drags work.
        if (heldButton is { } button)
        {
            (uint type, uint macButton) = button switch
            {
                ClickButton.Right => (kCGEventRightMouseDragged, 1u),
                ClickButton.Middle => (kCGEventOtherMouseDragged, 2u),
                _ => (kCGEventLeftMouseDragged, 0u)
            };
            PostMouse(type, target, macButton, 1);
            return;
        }

        CGWarpMouseCursorPosition(target);
        // Re-link the hardware mouse to the cursor; warping briefly detaches it.
        CGAssociateMouseAndMouseCursorPosition(true);
    }

    public void Dispose()
    {
        if (source != IntPtr.Zero) CFRelease(source);
    }

    void PostMouse(uint type, CGPoint at, uint button, int clickState)
    {
        var evt = CGEventCreateMouseEvent(source, type, at, button);
        if (evt == IntPtr.Zero) return;
        CGEventSetIntegerValueField(evt, kCGMouseEventClickState, clickState);
        CGEventSetIntegerValueField(evt, kCGEventSourceUserData, SyntheticMarker);
        CGEventSetFlags(evt, flags);
        CGEventPost(kCGHIDEventTap, evt);
        CFRelease(evt);
    }

    static CGPoint CurrentLocation()
    {
        var probe = CGEventCreate(IntPtr.Zero);
        if (probe == IntPtr.Zero) return default;
        var point = CGEventGetLocation(probe);
        CFRelease(probe);
        return point;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint { public double X, Y; }

    [DllImport(CoreGraphics)] static extern IntPtr CGEventSourceCreate(int stateID);
    [DllImport(CoreGraphics)] static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CoreGraphics)] static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CoreGraphics)] static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);
    [DllImport(CoreGraphics)] static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);
    [DllImport(CoreGraphics)] static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CoreGraphics)] static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);
    [DllImport(CoreGraphics)] static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport(CoreGraphics)] static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);
    [DllImport(CoreGraphics)] static extern int CGAssociateMouseAndMouseCursorPosition([MarshalAs(UnmanagedType.I1)] bool connected);
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] static extern bool AXIsProcessTrusted();
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);
}
