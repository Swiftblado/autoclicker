using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AutoClicker.Input;
using Avalonia.Input;

namespace AutoClicker.Hotkeys;

/// <summary>
/// Carbon's RegisterEventHotKey. It rides the main thread's run loop, which Avalonia already
/// runs, and unlike CGEventTap it works without the Accessibility permission.
/// </summary>
[SupportedOSPlatform("macos")]
sealed class MacHotkeyService : IHotkeyService
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    const uint kEventClassKeyboard = 0x6B657962;   // 'keyb'
    const uint kEventHotKeyPressed = 5;
    const uint Signature = 0x4155434C;             // 'AUCL'
    const uint kEventParamDirectObject = 0x2D2D2D2D;  // '----'
    const uint typeEventHotKeyID = 0x686B6964;        // 'hkid'
    const int eventNotHandledErr = -9874;
    const uint cmdKey = 0x0100, shiftKey = 0x0200, optionKey = 0x0800, controlKey = 0x1000;

    /// <summary>Each service gets its own hotkey ID, so it can tell its key from another one's.</summary>
    static int lastId;
    readonly uint id = (uint)System.Threading.Interlocked.Increment(ref lastId);

    readonly EventHandlerProc handlerProc;   // kept alive while installed
    IntPtr handlerRef;
    IntPtr hotKeyRef;

    public event Action? Pressed;

    public MacHotkeyService()
    {
        handlerProc = OnHotKeyEvent;
    }

    public bool TryRegister(Key key, Modifiers modifiers, out string? error)
    {
        Unregister();
        if (!KeyMap.TryGet(key, out var codes))
        {
            error = "That key can't be used as a hotkey.";
            return false;
        }

        if (handlerRef == IntPtr.Zero)
        {
            var spec = new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed };
            if (InstallEventHandler(GetEventDispatcherTarget(), handlerProc, 1, [spec], IntPtr.Zero, out handlerRef) != 0)
            {
                handlerRef = IntPtr.Zero;
                error = "macOS wouldn't let the app listen for the hotkey.";
                return false;
            }
        }

        var hotKeyId = new EventHotKeyID { Signature = Signature, Id = id };
        if (RegisterEventHotKey(codes.MacKeyCode, MacModifiers(modifiers), hotKeyId, GetEventDispatcherTarget(), 0, out hotKeyRef) != 0)
        {
            hotKeyRef = IntPtr.Zero;
            error = "another app is already using it";
            return false;
        }

        error = null;
        return true;
    }

    static uint MacModifiers(Modifiers modifiers) =>
        (modifiers.HasFlag(Modifiers.Control) ? controlKey : 0) | (modifiers.HasFlag(Modifiers.Shift) ? shiftKey : 0) |
        (modifiers.HasFlag(Modifiers.Alt) ? optionKey : 0) | (modifiers.HasFlag(Modifiers.Meta) ? cmdKey : 0);

    int OnHotKeyEvent(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        // Every service's handler sees every hotkey; pass the ones that aren't ours along.
        if (GetEventParameter(evt, kEventParamDirectObject, typeEventHotKeyID, IntPtr.Zero,
                (nuint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out var pressed) != 0 ||
            pressed.Signature != Signature || pressed.Id != id || hotKeyRef == IntPtr.Zero)
            return eventNotHandledErr;
        Pressed?.Invoke();
        return 0;   // noErr
    }

    public void Unregister()
    {
        if (hotKeyRef != IntPtr.Zero)
        {
            UnregisterEventHotKey(hotKeyRef);
            hotKeyRef = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Unregister();
        if (handlerRef != IntPtr.Zero)
        {
            RemoveEventHandler(handlerRef);
            handlerRef = IntPtr.Zero;
        }
    }

    delegate int EventHandlerProc(IntPtr callRef, IntPtr evt, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    [StructLayout(LayoutKind.Sequential)]
    struct EventHotKeyID { public uint Signature; public uint Id; }

    [DllImport(Carbon)] static extern IntPtr GetEventDispatcherTarget();

    [DllImport(Carbon)] static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler,
        int numTypes, EventTypeSpec[] typeList, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)] static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)] static extern int RegisterEventHotKey(uint hotKeyCode, uint hotKeyModifiers,
        EventHotKeyID hotKeyID, IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)] static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)] static extern int GetEventParameter(IntPtr evt, uint name, uint desiredType, IntPtr actualType,
        nuint bufferSize, IntPtr actualSize, out EventHotKeyID data);
}
