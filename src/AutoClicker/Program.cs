using System;
using System.Threading;
using Avalonia;
using AutoClicker.Updates;

namespace AutoClicker;

internal static class Program
{
    /// <summary>True when this launch is the new copy started by an in-app update.</summary>
    public static bool JustUpdated { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Started by an update: let the old copy finish exiting first, or the single-instance
        // check below would see it and quit.
        JustUpdated = Updater.CleanUpAfterUpdate(args);

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
