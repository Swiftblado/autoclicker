using System;

namespace AutoClicker.Notifications;

public enum NotificationKind { Info, Warning }

/// <summary>Pops up an OS notification, so it's seen even while this window is behind another app.</summary>
public interface ISystemNotifier : IDisposable
{
    void Show(string title, string message, NotificationKind kind);

    /// <summary>
    /// Clears anything <see cref="Show"/> left on screen. On Windows that's the tray icon the
    /// notifications come from; elsewhere the OS manages notifications itself and this does nothing.
    /// </summary>
    void Hide();
}

public static class SystemNotifier
{
    /// <param name="windowHandle">The main window's native handle, which Windows ties the tray icon to.</param>
    public static ISystemNotifier Create(Func<IntPtr> windowHandle)
    {
        if (OperatingSystem.IsWindows()) return new WindowsNotifier(windowHandle);
        if (OperatingSystem.IsMacOS()) return new MacNotifier();
        if (OperatingSystem.IsLinux()) return new LinuxNotifier();
        return new NullNotifier();
    }
}

sealed class NullNotifier : ISystemNotifier
{
    public void Show(string title, string message, NotificationKind kind) { }
    public void Hide() { }
    public void Dispose() { }
}
