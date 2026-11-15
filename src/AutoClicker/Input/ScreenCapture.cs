using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace AutoClicker.Input;

/// <summary>
/// A grab of part of the screen: 4 bytes a pixel, blue-green-red-alpha, rows packed top to bottom.
/// On a Retina Mac one point of <see cref="Area"/> holds <see cref="Scale"/>² pixels.
/// </summary>
public sealed class ScreenImage(PixelRect area, int width, int height, byte[] bgra)
{
    /// <summary>What was grabbed, in the same coordinates the mouse uses.</summary>
    public PixelRect Area { get; } = area;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;

    /// <summary>Image pixels per mouse unit: 1 except on high-density Mac screens.</summary>
    public double Scale => Area.Width > 0 ? (double)Width / Area.Width : 1;

    /// <summary>Where image pixel (x, y) is, in mouse coordinates.</summary>
    public PixelPoint ToScreen(double x, double y) =>
        new(Area.X + (int)Math.Floor(x / Scale), Area.Y + (int)Math.Floor(y / Scale));
}

/// <summary>Grabs pixels off the screen, for the macro steps that look for colors and images.</summary>
public static class ScreenCapture
{
    static readonly object Gate = new();
    static ICapture? capture;
    static bool tried;

    /// <summary>
    /// Grabs <paramref name="area"/>, trimmed to the screen. Null when nothing could be captured
    /// (no permission, nothing on screen there, or an OS without capture). Safe from any thread.
    /// </summary>
    public static ScreenImage? Grab(PixelRect area)
    {
        lock (Gate)
        {
            if (!tried)
            {
                tried = true;
                try
                {
                    if (OperatingSystem.IsWindows()) capture = new WindowsCapture();
                    else if (OperatingSystem.IsMacOS()) capture = MacCapture.TryCreate();
                    else if (OperatingSystem.IsLinux()) capture = LinuxCapture.TryCreate();
                }
                catch (Exception) { capture = null; }
            }
            if (capture == null) return null;

            var clipped = area.Intersect(capture.Bounds);
            if (clipped.Width <= 0 || clipped.Height <= 0) return null;
            try { return capture.Grab(clipped); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Everything on screen (every monitor on Windows and Linux, the main one on macOS).</summary>
    public static PixelRect ScreenBounds
    {
        get
        {
            Grab(new PixelRect(0, 0, 1, 1));   // makes sure the capture is set up
            lock (Gate) return capture?.Bounds ?? default;
        }
    }

    /// <summary>The color of one pixel as "#RRGGBB", or null if it couldn't be read.</summary>
    public static string? ColorAt(PixelPoint point)
    {
        var image = Grab(new PixelRect(point.X, point.Y, 1, 1));
        if (image == null || image.Bgra.Length < 4) return null;
        return "#" + image.Bgra[2].ToString("X2") + image.Bgra[1].ToString("X2") + image.Bgra[0].ToString("X2");
    }

    interface ICapture
    {
        PixelRect Bounds { get; }
        ScreenImage? Grab(PixelRect area);
    }

    /// <summary>GDI BitBlt from the whole desktop. No permission needed.</summary>
    [SupportedOSPlatform("windows")]
    sealed class WindowsCapture : ICapture
    {
        public PixelRect Bounds => new(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        public ScreenImage? Grab(PixelRect area)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr memory = CreateCompatibleDC(screen);
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = area.Width,
                biHeight = -area.Height,   // top-down
                biPlanes = 1,
                biBitCount = 32
            };
            IntPtr bitmap = CreateDIBSection(memory, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            try
            {
                if (bitmap == IntPtr.Zero) return null;
                IntPtr old = SelectObject(memory, bitmap);
                bool ok = BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SRCCOPY | CAPTUREBLT);
                GdiFlush();
                SelectObject(memory, old);
                if (!ok) return null;
                var bytes = new byte[area.Width * area.Height * 4];
                Marshal.Copy(bits, bytes, 0, bytes.Length);
                return new ScreenImage(area, area.Width, area.Height, bytes);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        const uint SRCCOPY = 0x00CC0020;
        const uint CAPTUREBLT = 0x40000000;   // include layered (see-through) windows
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
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
    /// Quartz display capture of the main display. Needs the Screen Recording permission; without
    /// it macOS returns only the desktop picture, so this asks once and stays off until relaunch.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class MacCapture : ICapture
    {
        const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public static MacCapture? TryCreate()
        {
            if (CGPreflightScreenCaptureAccess()) return new MacCapture();
            CGRequestScreenCaptureAccess();
            return null;
        }

        public PixelRect Bounds
        {
            get
            {
                var b = CGDisplayBounds(CGMainDisplayID());
                return new PixelRect((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
            }
        }

        public ScreenImage? Grab(PixelRect area)
        {
            uint display = CGMainDisplayID();
            var bounds = CGDisplayBounds(display);
            var rect = new CGRect { X = area.X - bounds.X, Y = area.Y - bounds.Y, Width = area.Width, Height = area.Height };
            IntPtr image = CGDisplayCreateImageForRect(display, rect);
            if (image == IntPtr.Zero) return null;
            try
            {
                int width = (int)CGImageGetWidth(image), height = (int)CGImageGetHeight(image);
                int rowBytes = (int)CGImageGetBytesPerRow(image);
                if (CGImageGetBitsPerPixel(image) != 32 || width <= 0 || height <= 0) return null;
                IntPtr data = CGDataProviderCopyData(CGImageGetDataProvider(image));
                if (data == IntPtr.Zero) return null;
                try
                {
                    // Display images come as 32-bit little-endian, alpha first: B, G, R, A in memory.
                    var bytes = new byte[width * height * 4];
                    IntPtr source = CFDataGetBytePtr(data);
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(source + y * rowBytes, bytes, y * width * 4, width * 4);
                    return new ScreenImage(area, width, height, bytes);
                }
                finally { CFRelease(data); }
            }
            finally { CFRelease(image); }
        }

        [StructLayout(LayoutKind.Sequential)] struct CGRect { public double X, Y, Width, Height; }

        [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGPreflightScreenCaptureAccess();
        [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGRequestScreenCaptureAccess();
        [DllImport(CoreGraphics)] static extern uint CGMainDisplayID();
        [DllImport(CoreGraphics)] static extern CGRect CGDisplayBounds(uint display);
        [DllImport(CoreGraphics)] static extern IntPtr CGDisplayCreateImageForRect(uint display, CGRect rect);
        [DllImport(CoreGraphics)] static extern nuint CGImageGetWidth(IntPtr image);
        [DllImport(CoreGraphics)] static extern nuint CGImageGetHeight(IntPtr image);
        [DllImport(CoreGraphics)] static extern nuint CGImageGetBytesPerRow(IntPtr image);
        [DllImport(CoreGraphics)] static extern nuint CGImageGetBitsPerPixel(IntPtr image);
        [DllImport(CoreGraphics)] static extern IntPtr CGImageGetDataProvider(IntPtr image);
        [DllImport(CoreGraphics)] static extern IntPtr CGDataProviderCopyData(IntPtr provider);
        [DllImport(CoreFoundation)] static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr obj);
    }

    /// <summary>
    /// XGetImage on the root window over a connection of its own. Callers keep the area on the
    /// screen: pixels off the edge are an X error, which by default ends the process.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed class LinuxCapture : ICapture
    {
        const string Lib = "libX11.so.6";
        const int ZPixmap = 2;

        readonly IntPtr display, root;

        LinuxCapture(IntPtr display)
        {
            this.display = display;
            root = XDefaultRootWindow(display);
            int screen = XDefaultScreen(display);
            Bounds = new PixelRect(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen));
        }

        public static LinuxCapture? TryCreate()
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            return display == IntPtr.Zero ? null : new LinuxCapture(display);
        }

        public PixelRect Bounds { get; }

        public ScreenImage? Grab(PixelRect area)
        {
            IntPtr image = XGetImage(display, root, area.X, area.Y, (uint)area.Width, (uint)area.Height, ~0UL, ZPixmap);
            if (image == IntPtr.Zero) return null;
            IntPtr data = Marshal.ReadIntPtr(image, 16);
            try
            {
                // struct XImage: char *data @16, int bytes_per_line @44, int bits_per_pixel @48
                int rowBytes = Marshal.ReadInt32(image, 44);
                if (Marshal.ReadInt32(image, 48) != 32 || data == IntPtr.Zero) return null;
                var bytes = new byte[area.Width * area.Height * 4];
                for (int y = 0; y < area.Height; y++)
                    Marshal.Copy(data + y * rowBytes, bytes, y * area.Width * 4, area.Width * 4);
                return new ScreenImage(area, area.Width, area.Height, bytes);
            }
            finally
            {
                // XDestroyImage is a macro; for an XGetImage result it frees exactly these two.
                if (data != IntPtr.Zero) XFree(data);
                XFree(image);
            }
        }

        [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] static extern int XDefaultScreen(IntPtr display);
        [DllImport(Lib)] static extern int XDisplayWidth(IntPtr display, int screen);
        [DllImport(Lib)] static extern int XDisplayHeight(IntPtr display, int screen);
        [DllImport(Lib)] static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y,
            uint width, uint height, ulong planeMask, int format);
        [DllImport(Lib)] static extern int XFree(IntPtr data);
    }
}
