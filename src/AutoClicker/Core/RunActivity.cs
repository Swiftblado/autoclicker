using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoClicker.Core;

/// <summary>
/// Tells the OS a run is time-critical even though this window is usually hidden behind the
/// app being automated. Left alone, both Windows and macOS throttle background apps' timers,
/// which makes clicks late and would make the lag check see lag that isn't there.
/// </summary>
public static class RunActivity
{
    /// <summary>
    /// Windows 11 ignores <c>timeBeginPeriod</c> for processes whose windows are hidden or
    /// minimized, falling back to ~15.6 ms ticks. Opting out keeps the 1 ms timer the runner asks for.
    /// </summary>
    public static void KeepTimersPrecise()
    {
        if (OperatingSystem.IsWindows()) WindowsPower.OptOutOfThrottling();
    }

    /// <summary>
    /// On macOS, suspends App Nap until the returned handle is disposed. Elsewhere it's a no-op.
    /// Call on the UI thread.
    /// </summary>
    public static IDisposable Begin() =>
        OperatingSystem.IsMacOS() ? MacActivity.Begin() : NoActivity.Instance;

    sealed class NoActivity : IDisposable
    {
        public static readonly NoActivity Instance = new();
        public void Dispose() { }
    }

    [SupportedOSPlatform("windows")]
    static class WindowsPower
    {
        const int ProcessPowerThrottling = 4;
        const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        public static void OptOutOfThrottling()
        {
            // Naming a flag in ControlMask and leaving it clear in StateMask turns that throttle off.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0
            };
            try
            {
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            catch (EntryPointNotFoundException) { /* Windows 7/8: no such throttling to opt out of */ }
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr process, int infoClass,
            ref PROCESS_POWER_THROTTLING_STATE info, uint size);
    }

    /// <summary><c>[[NSProcessInfo processInfo] beginActivityWithOptions:reason:]</c>, by hand.</summary>
    [SupportedOSPlatform("macos")]
    sealed class MacActivity : IDisposable
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        // NSActivityUserInitiated | NSActivityLatencyCritical
        const ulong Options = 0x00FFFFFFUL | 0xFF00000000UL;

        IntPtr token;

        MacActivity(IntPtr token) => this.token = token;

        public static IDisposable Begin()
        {
            try
            {
                IntPtr info = Send(objc_getClass("NSProcessInfo"), Sel("processInfo"));
                IntPtr reason = SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"),
                    "Sending clicks and key presses on a timer");
                IntPtr token = SendActivity(info, Sel("beginActivityWithOptions:reason:"), Options, reason);
                if (token == IntPtr.Zero) return NoActivity.Instance;
                Send(token, Sel("retain"));   // returned autoreleased; keep it until Dispose
                return new MacActivity(token);
            }
            catch (Exception)
            {
                return NoActivity.Instance;
            }
        }

        public void Dispose()
        {
            if (token == IntPtr.Zero) return;
            try
            {
                IntPtr info = Send(objc_getClass("NSProcessInfo"), Sel("processInfo"));
                SendPtr(info, Sel("endActivity:"), token);
                Send(token, Sel("release"));
            }
            catch (Exception) { }
            token = IntPtr.Zero;
        }

        static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendString(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendActivity(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);
    }
}
