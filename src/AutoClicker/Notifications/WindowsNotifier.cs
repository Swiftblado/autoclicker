using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoClicker.Notifications;

/// <summary>
/// Shows notifications as tray-icon balloons, which Windows 10 and 11 display as ordinary toast
/// notifications. A portable .exe has no Start-menu registration, so a tray icon is the one way
/// it can post them. The icon only exists between <see cref="Show"/> and <see cref="Hide"/>.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsNotifier(Func<IntPtr> windowHandle) : ISystemNotifier
{
    const uint IconId = 1;

    IntPtr hwnd;
    IntPtr icon;
    bool ownsIcon;   // extracted from the .exe, so ours to destroy; the stock icon is shared
    bool added;

    public void Show(string title, string message, NotificationKind kind)
    {
        if (!added)
        {
            hwnd = windowHandle();
            if (hwnd == IntPtr.Zero) return;
            if (icon == IntPtr.Zero) (icon, ownsIcon) = LoadAppIcon();

            var add = NewData();
            add.uFlags = NIF_ICON | NIF_TIP;
            add.hIcon = icon;
            add.szTip = "Auto Clicker";
            if (!Shell_NotifyIcon(NIM_ADD, ref add)) return;
            added = true;
        }

        var balloon = NewData();
        balloon.uFlags = NIF_INFO;
        balloon.szInfoTitle = Truncate(title, 63);
        balloon.szInfo = Truncate(message, 255);
        balloon.dwInfoFlags = kind == NotificationKind.Warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref balloon);
    }

    public void Hide()
    {
        if (!added) return;
        var remove = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref remove);
        added = false;
    }

    public void Dispose()
    {
        Hide();
        if (ownsIcon) DestroyIcon(icon);
        icon = IntPtr.Zero;
    }

    NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = IconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    /// <summary>The icon embedded in the .exe, falling back to the stock application icon.</summary>
    static (IntPtr Icon, bool Owned) LoadAppIcon()
    {
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            return (small[0], true);
        return (LoadIcon(IntPtr.Zero, IDI_APPLICATION), false);
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2;
    static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
