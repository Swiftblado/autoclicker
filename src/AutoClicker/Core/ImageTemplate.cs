using System;
using System.IO;
using System.Runtime.InteropServices;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AutoClicker.Core;

/// <summary>
/// The picture an image check looks for: blue-green-red-alpha pixels, rows packed. Kept in a
/// macro as a base64 PNG, so the macro file carries it along. Pixels that are mostly transparent
/// (from a PNG with cut-outs) don't have to match anything.
/// </summary>
public sealed class ImageTemplate(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;

    /// <summary>Largest side accepted: past this a search gets slow and the macro file gets large.</summary>
    public const int MaxSide = 600;

    /// <summary>A grab of the screen as a template. Screen grabs carry no real alpha, so it's made opaque.</summary>
    public static ImageTemplate FromScreen(ScreenImage image)
    {
        var bytes = (byte[])image.Bgra.Clone();
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        return new ImageTemplate(image.Width, image.Height, bytes);
    }

    /// <summary>Decodes a PNG (or any format Avalonia reads). Null if it can't be read or is too large.</summary>
    public static ImageTemplate? Decode(Stream stream)
    {
        try
        {
            using var bitmap = new Bitmap(stream);
            int width = bitmap.PixelSize.Width, height = bitmap.PixelSize.Height;
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) return null;

            using var converted = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var frame = converted.Lock())
            {
                bitmap.CopyPixels(frame, AlphaFormat.Unpremul);
                var bytes = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    Marshal.Copy(frame.Address + y * frame.RowBytes, bytes, y * width * 4, width * 4);
                return new ImageTemplate(width, height, bytes);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageTemplate? FromBase64(string? png)
    {
        if (string.IsNullOrEmpty(png)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(png));
            return Decode(stream);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public Bitmap ToBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var frame = bitmap.Lock();
        for (int y = 0; y < Height; y++)
            Marshal.Copy(Bgra, y * Width * 4, frame.Address + y * frame.RowBytes, Width * 4);
        return bitmap;
    }

    public string ToBase64()
    {
        using var bitmap = ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream);   // PNG
        return Convert.ToBase64String(stream.ToArray());
    }
}
