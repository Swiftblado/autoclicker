using System;
using System.Threading;
using Avalonia;

namespace AutoClicker;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Mutex? singleInstance = null;
        try
        {
            // Named mutexes aren't available everywhere; a second instance is only a nuisance,
            // so fall through and start anyway when we can't check.
            // The Global\ prefix is a Windows convention and isn't valid in a Unix mutex name.
            string name = OperatingSystem.IsWindows()
                ? "Global\\AutoClicker.SingleInstance"
                : "AutoClicker.SingleInstance";
            singleInstance = new Mutex(true, name, out bool isFirst);
            if (!isFirst) return;
        }
        catch (Exception) { }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
