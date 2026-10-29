using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace AutoClicker.Input;

/// <summary>
/// Looks at a small patch of the screen and says whether it changed since the last look. While a
/// game keeps drawing, the patch around the click point changes many times a second; when the game
/// (or the browser page it runs in) is jammed, it stops. That is the one sign of lag visible from
/// outside a browser, which accepts input instantly no matter how far behind its page is.
/// </summary>
public interface IScreenProbe : IDisposable
{
    /// <summary>
    /// Captures the patch centred on <paramref name="center"/> and compares it with the previous
    /// capture. Null if nothing could be captured.
    /// </summary>
    bool? Changed(PixelPoint center);
}

public static class ScreenProbe
{
    /// <summary>Side of the square watched, in pixels (points on macOS).</summary>
    public const int Size = 96;

    public static IScreenProbe? Create()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new WindowsScreenProbe();
            if (OperatingSystem.IsMacOS()) return MacScreenProbe.TryCreate();
            if (OperatingSystem.IsLinux()) return LinuxScreenProbe.TryCreate();
        }
        catch (Exception) { /* no capture available; lag is judged without it */ }
        return null;
    }
}

/// <summary>Keeps the previous capture and compares new ones against it.</summary>
abstract class ComparingProbe : IScreenProbe
{
    byte[] previous = [];
    bool hasPrevious;

    protected bool? Compare(ReadOnlySpan<byte> current)
    {
        bool changed = hasPrevious && (current.Length != previous.Length || !current.SequenceEqual(previous));
        if (previous.Length != current.Length) previous = new byte[current.Length];
        current.CopyTo(previous);
        bool first = !hasPrevious;
        hasPrevious = true;
        return first ? null : changed;
    }

    public abstract bool? Changed(PixelPoint center);
    public virtual void Dispose() { }
}

/// <summary>GDI BitBlt from the screen into a small DIB. No permission needed.</summary>
[SupportedOSPlatform("windows")]
sealed class WindowsScreenProbe : ComparingProbe
{
    readonly IntPtr screen, memory, bitmap, bits;
    readonly byte[] buffer = new byte[ScreenProbe.Size * ScreenProbe.Size * 4];

    public WindowsScreenProbe()
    {
        screen = GetDC(IntPtr.Zero);
        memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = ScreenProbe.Size,
            biHeight = -ScreenProbe.Size,   // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        bitmap = CreateDIBSection(memory, ref info, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memory, bitmap);
    }

    public override bool? Changed(PixelPoint center)
    {
        int half = ScreenProbe.Size / 2;
        if (!BitBlt(memory, 0, 0, ScreenProbe.Size, ScreenProbe.Size, screen, center.X - half, center.Y - half, SRCCOPY))
            return null;
        GdiFlush();
        Marshal.Copy(bits, buffer, 0, buffer.Length);
        return Compare(buffer);
    }

    public override void Dispose()
    {
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}

/// <summary>
/// Quartz display capture. macOS only returns window contents once the user has granted Screen
/// Recording; the first time, this asks for it and stays off until the app is reopened.
/// </summary>
[SupportedOSPlatform("macos")]
sealed class MacScreenProbe : ComparingProbe
{
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static MacScreenProbe? TryCreate()
    {
        if (CGPreflightScreenCaptureAccess()) return new MacScreenProbe();
        CGRequestScreenCaptureAccess();   // shows the prompt once; takes effect after a relaunch
        return null;
    }

    public override bool? Changed(PixelPoint center)
    {
        var point = new CGPoint { X = center.X, Y = center.Y };
        if (CGGetDisplaysWithPoint(point, 1, out uint display, out uint count) != 0 || count == 0) return null;
        var bounds = CGDisplayBounds(display);
        double half = ScreenProbe.Size / 2.0;
        var rect = new CGRect { X = center.X - bounds.X - half, Y = center.Y - bounds.Y - half, Width = ScreenProbe.Size, Height = ScreenProbe.Size };

        IntPtr image = CGDisplayCreateImageForRect(display, rect);
        if (image == IntPtr.Zero) return null;
        try
        {
            IntPtr data = CGDataProviderCopyData(CGImageGetDataProvider(image));
            if (data == IntPtr.Zero) return null;
            try
            {
                unsafe
                {
                    return Compare(new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(data), (int)CFDataGetLength(data)));
                }
            }
            finally { CFRelease(data); }
        }
        finally { CFRelease(image); }
    }

    [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CGRect { public double X, Y, Width, Height; }

    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CoreGraphics)] static extern int CGGetDisplaysWithPoint(CGPoint point, uint max, out uint display, out uint count);
    [DllImport(CoreGraphics)] static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] static extern IntPtr CGDisplayCreateImageForRect(uint display, CGRect rect);
    [DllImport(CoreGraphics)] static extern IntPtr CGImageGetDataProvider(IntPtr image);
    [DllImport(CoreGraphics)] static extern IntPtr CGDataProviderCopyData(IntPtr provider);
    [DllImport(CoreFoundation)] static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr obj);
}

/// <summary>
/// XGetImage on the root window, over a connection of its own (Xlib connections aren't shared
/// across threads here). The area is kept inside the screen: asking for pixels off the edge is an
/// X error, and Xlib's default response to that is to end the process.
/// </summary>
[SupportedOSPlatform("linux")]
sealed class LinuxScreenProbe : ComparingProbe
{
    const string Lib = "libX11.so.6";
    const int ZPixmap = 2;

    readonly IntPtr display, root;
    readonly int screenWidth, screenHeight;

    LinuxScreenProbe(IntPtr display)
    {
        this.display = display;
        root = XDefaultRootWindow(display);
        int screen = XDefaultScreen(display);
        screenWidth = XDisplayWidth(display, screen);
        screenHeight = XDisplayHeight(display, screen);
    }

    public static LinuxScreenProbe? TryCreate()
    {
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        return display == IntPtr.Zero ? null : new LinuxScreenProbe(display);
    }

    public override bool? Changed(PixelPoint center)
    {
        int size = Math.Min(ScreenProbe.Size, Math.Min(screenWidth, screenHeight));
        int x = Math.Clamp(center.X - size / 2, 0, screenWidth - size);
        int y = Math.Clamp(center.Y - size / 2, 0, screenHeight - size);

        IntPtr image = XGetImage(display, root, x, y, (uint)size, (uint)size, ~0UL, ZPixmap);
        if (image == IntPtr.Zero) return null;
        try
        {
            // struct XImage { int width, height, xoffset, format; char *data; ... int bytes_per_line @ 44 }
            IntPtr data = Marshal.ReadIntPtr(image, 16);
            int bytesPerLine = Marshal.ReadInt32(image, 44);
            unsafe
            {
                return Compare(new ReadOnlySpan<byte>((void*)data, bytesPerLine * size));
            }
        }
        finally
        {
            // XDestroyImage is a macro; for an XGetImage result it frees exactly these two.
            IntPtr data = Marshal.ReadIntPtr(image, 16);
            if (data != IntPtr.Zero) XFree(data);
            XFree(image);
        }
    }

    public override void Dispose() => XCloseDisplay(display);

    [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] static extern int XDefaultScreen(IntPtr display);
    [DllImport(Lib)] static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(Lib)] static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(Lib)] static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y,
        uint width, uint height, ulong planeMask, int format);
    [DllImport(Lib)] static extern int XFree(IntPtr data);
}
