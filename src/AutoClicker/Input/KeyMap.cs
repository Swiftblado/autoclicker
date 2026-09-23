using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace AutoClicker.Input;

/// <summary>
/// One row per key: how it is named for the user, plus its code on each platform
/// (Windows virtual-key, macOS virtual keycode, X11 keysym name).
/// </summary>
public readonly record struct KeyCodes(string Name, ushort WindowsVk, ushort MacKeyCode, string X11Keysym);

public static class KeyMap
{
    // macOS keycodes come from Carbon's kVK_* constants; X11 names from keysymdef.h.
    static readonly Dictionary<Key, KeyCodes> Map = new()
    {
        [Key.A] = new("A", 0x41, 0, "a"),
        [Key.B] = new("B", 0x42, 11, "b"),
        [Key.C] = new("C", 0x43, 8, "c"),
        [Key.D] = new("D", 0x44, 2, "d"),
        [Key.E] = new("E", 0x45, 14, "e"),
        [Key.F] = new("F", 0x46, 3, "f"),
        [Key.G] = new("G", 0x47, 5, "g"),
        [Key.H] = new("H", 0x48, 4, "h"),
        [Key.I] = new("I", 0x49, 34, "i"),
        [Key.J] = new("J", 0x4A, 38, "j"),
        [Key.K] = new("K", 0x4B, 40, "k"),
        [Key.L] = new("L", 0x4C, 37, "l"),
        [Key.M] = new("M", 0x4D, 46, "m"),
        [Key.N] = new("N", 0x4E, 45, "n"),
        [Key.O] = new("O", 0x4F, 31, "o"),
        [Key.P] = new("P", 0x50, 35, "p"),
        [Key.Q] = new("Q", 0x51, 12, "q"),
        [Key.R] = new("R", 0x52, 15, "r"),
        [Key.S] = new("S", 0x53, 1, "s"),
        [Key.T] = new("T", 0x54, 17, "t"),
        [Key.U] = new("U", 0x55, 32, "u"),
        [Key.V] = new("V", 0x56, 9, "v"),
        [Key.W] = new("W", 0x57, 13, "w"),
        [Key.X] = new("X", 0x58, 7, "x"),
        [Key.Y] = new("Y", 0x59, 16, "y"),
        [Key.Z] = new("Z", 0x5A, 6, "z"),

        [Key.D0] = new("0", 0x30, 29, "0"),
        [Key.D1] = new("1", 0x31, 18, "1"),
        [Key.D2] = new("2", 0x32, 19, "2"),
        [Key.D3] = new("3", 0x33, 20, "3"),
        [Key.D4] = new("4", 0x34, 21, "4"),
        [Key.D5] = new("5", 0x35, 23, "5"),
        [Key.D6] = new("6", 0x36, 22, "6"),
        [Key.D7] = new("7", 0x37, 26, "7"),
        [Key.D8] = new("8", 0x38, 28, "8"),
        [Key.D9] = new("9", 0x39, 25, "9"),

        [Key.F1] = new("F1", 0x70, 122, "F1"),
        [Key.F2] = new("F2", 0x71, 120, "F2"),
        [Key.F3] = new("F3", 0x72, 99, "F3"),
        [Key.F4] = new("F4", 0x73, 118, "F4"),
        [Key.F5] = new("F5", 0x74, 96, "F5"),
        [Key.F6] = new("F6", 0x75, 97, "F6"),
        [Key.F7] = new("F7", 0x76, 98, "F7"),
        [Key.F8] = new("F8", 0x77, 100, "F8"),
        [Key.F9] = new("F9", 0x78, 101, "F9"),
        [Key.F10] = new("F10", 0x79, 109, "F10"),
        [Key.F11] = new("F11", 0x7A, 103, "F11"),
        [Key.F12] = new("F12", 0x7B, 111, "F12"),

        [Key.Space] = new("Space", 0x20, 49, "space"),
        [Key.Enter] = new("Enter", 0x0D, 36, "Return"),
        [Key.Tab] = new("Tab", 0x09, 48, "Tab"),
        [Key.Escape] = new("Esc", 0x1B, 53, "Escape"),
        [Key.Back] = new("Backspace", 0x08, 51, "BackSpace"),
        [Key.Delete] = new("Delete", 0x2E, 117, "Delete"),
        [Key.Insert] = new("Insert", 0x2D, 114, "Insert"),
        [Key.Home] = new("Home", 0x24, 115, "Home"),
        [Key.End] = new("End", 0x23, 119, "End"),
        [Key.PageUp] = new("Page Up", 0x21, 116, "Prior"),
        [Key.PageDown] = new("Page Down", 0x22, 121, "Next"),
        [Key.Left] = new("Left", 0x25, 123, "Left"),
        [Key.Up] = new("Up", 0x26, 126, "Up"),
        [Key.Right] = new("Right", 0x27, 124, "Right"),
        [Key.Down] = new("Down", 0x28, 125, "Down"),
        [Key.CapsLock] = new("Caps Lock", 0x14, 57, "Caps_Lock"),

        [Key.NumPad0] = new("Num 0", 0x60, 82, "KP_0"),
        [Key.NumPad1] = new("Num 1", 0x61, 83, "KP_1"),
        [Key.NumPad2] = new("Num 2", 0x62, 84, "KP_2"),
        [Key.NumPad3] = new("Num 3", 0x63, 85, "KP_3"),
        [Key.NumPad4] = new("Num 4", 0x64, 86, "KP_4"),
        [Key.NumPad5] = new("Num 5", 0x65, 87, "KP_5"),
        [Key.NumPad6] = new("Num 6", 0x66, 88, "KP_6"),
        [Key.NumPad7] = new("Num 7", 0x67, 89, "KP_7"),
        [Key.NumPad8] = new("Num 8", 0x68, 91, "KP_8"),
        [Key.NumPad9] = new("Num 9", 0x69, 92, "KP_9"),
        [Key.Multiply] = new("Num *", 0x6A, 67, "KP_Multiply"),
        [Key.Add] = new("Num +", 0x6B, 69, "KP_Add"),
        [Key.Subtract] = new("Num -", 0x6D, 78, "KP_Subtract"),
        [Key.Decimal] = new("Num .", 0x6E, 65, "KP_Decimal"),
        [Key.Divide] = new("Num /", 0x6F, 75, "KP_Divide"),

        [Key.OemComma] = new(",", 0xBC, 43, "comma"),
        [Key.OemPeriod] = new(".", 0xBE, 47, "period"),
        [Key.OemMinus] = new("-", 0xBD, 27, "minus"),
        [Key.OemPlus] = new("=", 0xBB, 24, "equal"),
        [Key.OemQuestion] = new("/", 0xBF, 44, "slash"),
        [Key.OemSemicolon] = new(";", 0xBA, 41, "semicolon"),
        [Key.OemQuotes] = new("'", 0xDE, 39, "apostrophe"),
        [Key.OemOpenBrackets] = new("[", 0xDB, 33, "bracketleft"),
        [Key.OemCloseBrackets] = new("]", 0xDD, 30, "bracketright"),
        [Key.OemPipe] = new("\\", 0xDC, 42, "backslash"),
        [Key.OemTilde] = new("`", 0xC0, 50, "grave"),

        [Key.LeftCtrl] = new("Ctrl", 0xA2, 59, "Control_L"),
        [Key.RightCtrl] = new("Right Ctrl", 0xA3, 62, "Control_R"),
        [Key.LeftShift] = new("Shift", 0xA0, 56, "Shift_L"),
        [Key.RightShift] = new("Right Shift", 0xA1, 60, "Shift_R"),
        [Key.LeftAlt] = new("Alt", 0xA4, 58, "Alt_L"),
        [Key.RightAlt] = new("Right Alt", 0xA5, 61, "Alt_R"),
        [Key.LWin] = new("Meta", 0x5B, 55, "Super_L"),
        [Key.RWin] = new("Right Meta", 0x5C, 54, "Super_R"),
    };

    // Reverse lookups, used when recording turns real platform events back into keys.
    static readonly Dictionary<ushort, Key> ByWindowsVk = [];
    static readonly Dictionary<ushort, Key> ByMacKeyCode = [];
    static readonly Dictionary<string, Key> ByX11Keysym = new(StringComparer.Ordinal);

    static KeyMap()
    {
        foreach (var (key, codes) in Map)
        {
            ByWindowsVk.TryAdd(codes.WindowsVk, key);
            ByMacKeyCode.TryAdd(codes.MacKeyCode, key);
            ByX11Keysym.TryAdd(codes.X11Keysym, key);
        }
    }

    public static bool TryFromWindowsVk(ushort vk, out Key key) => ByWindowsVk.TryGetValue(vk, out key);

    public static bool TryFromMacKeyCode(ushort code, out Key key) => ByMacKeyCode.TryGetValue(code, out key);

    public static bool TryFromX11Keysym(string name, out Key key) => ByX11Keysym.TryGetValue(name, out key);

    /// <summary>The modifier flag a key contributes, or None if it isn't a modifier.</summary>
    public static Modifiers ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => Modifiers.Control,
        Key.LeftShift or Key.RightShift => Modifiers.Shift,
        Key.LeftAlt or Key.RightAlt => Modifiers.Alt,
        Key.LWin or Key.RWin => Modifiers.Meta,
        _ => Modifiers.None
    };

    public static bool TryGet(Key key, out KeyCodes codes) => Map.TryGetValue(key, out codes);

    public static bool IsSupported(Key key) => Map.ContainsKey(key);

    public static string Name(Key key) => Map.TryGetValue(key, out var codes) ? codes.Name : key.ToString();

    public static Key ModifierKey(Modifiers modifier) => modifier switch
    {
        Modifiers.Control => Key.LeftCtrl,
        Modifiers.Shift => Key.LeftShift,
        Modifiers.Alt => Key.LeftAlt,
        Modifiers.Meta => Key.LWin,
        _ => Key.None
    };

    /// <summary>What to call the Meta key in the UI on the machine we're running on.</summary>
    public static string MetaName =>
        OperatingSystem.IsMacOS() ? "Cmd" : OperatingSystem.IsWindows() ? "Win" : "Super";

    /// <summary>On macOS, Alt is labelled Option.</summary>
    public static string AltName => OperatingSystem.IsMacOS() ? "Option" : "Alt";
}
