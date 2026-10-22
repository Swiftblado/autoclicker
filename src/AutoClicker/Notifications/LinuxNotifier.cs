using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AutoClicker.Notifications;

/// <summary>Posts through <c>notify-send</c>, which every major desktop ships with libnotify.</summary>
[SupportedOSPlatform("linux")]
sealed class LinuxNotifier : ISystemNotifier
{
    public void Show(string title, string message, NotificationKind kind)
    {
        try
        {
            var info = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--app-name=Auto Clicker");
            info.ArgumentList.Add("--icon=autoclicker");   // installed by install.sh; ignored if missing
            info.ArgumentList.Add("--urgency=" + (kind == NotificationKind.Warning ? "normal" : "low"));
            info.ArgumentList.Add(title);
            info.ArgumentList.Add(message);
            Process.Start(info)?.Dispose();
        }
        catch (Exception) { /* notify-send not installed: nothing else to fall back to */ }
    }

    public void Hide() { }
    public void Dispose() { }
}
