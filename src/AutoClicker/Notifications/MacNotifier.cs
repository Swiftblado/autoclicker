using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AutoClicker.Notifications;

/// <summary>
/// Posts through AppleScript's <c>display notification</c>. A native notification needs a
/// notarized app with notification permission; this works for an ad-hoc signed one, at the cost
/// of the notification being labelled as coming from Script Editor.
/// </summary>
[SupportedOSPlatform("macos")]
sealed class MacNotifier : ISystemNotifier
{
    public void Show(string title, string message, NotificationKind kind)
    {
        string script = "display notification " + Quote(message) + " with title " + Quote(title);
        try
        {
            var info = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add(script);
            Process.Start(info)?.Dispose();
        }
        catch (Exception) { /* a missed notification isn't worth failing the run over */ }
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void Hide() { }
    public void Dispose() { }
}
