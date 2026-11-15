using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoClicker.Input;
using Avalonia;

namespace AutoClicker.Core;

/// <summary>Hex color codes and how close two colors must be to count as the same.</summary>
public static class ColorMatch
{
    /// <summary>Games shade and blend; a little slack per channel catches that without false hits.</summary>
    public const int DefaultTolerance = 10;

    /// <summary>Reads "#RGB", "#RRGGBB" or the same without "#". False for anything else.</summary>
    public static bool TryParse(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (text == null) return false;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = new string([hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]]);
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            return false;
        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }

    /// <summary>"#RRGGBB" in capitals, or null if <paramref name="text"/> isn't a color.</summary>
    public static string? Normalize(string? text) =>
        TryParse(text, out byte r, out byte g, out byte b) ? Format(r, g, b) : null;

    public static string Format(byte r, byte g, byte b) => "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");

    /// <summary>Whether the BGRA pixel at <paramref name="offset"/> is within tolerance of r, g, b.</summary>
    internal static bool Near(byte[] bgra, int offset, byte r, byte g, byte b, int tolerance) =>
        Math.Abs(bgra[offset + 2] - r) <= tolerance &&
        Math.Abs(bgra[offset + 1] - g) <= tolerance &&
        Math.Abs(bgra[offset] - b) <= tolerance;
}

/// <summary>
/// Does the looking for a <see cref="StepKind.Check"/> step: grabs the part of the screen it
/// watches and searches it. Used by a running macro and by the editor's Test button.
/// </summary>
public static class ScreenCheck
{
    /// <summary>
    /// Share of an image's pixels that may differ (past the tolerance) and still count as a match:
    /// enough to ride out a blinking cursor or a little animation, not enough to match something else.
    /// </summary>
    const double ImageSlack = 0.02;

    /// <summary>Pixels of the image looked at first, at each place, before comparing all of them.</summary>
    const int ProbeCount = 12;

    /// <summary>
    /// Where the step's target is on screen right now (the found pixel, the pattern's first point,
    /// or the middle of the image), or null if it isn't there. <paramref name="template"/> is the
    /// decoded image for image checks.
    /// </summary>
    public static PixelPoint? Find(MacroStep step, ImageTemplate? template)
    {
        int tolerance = Math.Clamp(step.Tolerance, 0, 255);
        switch (step.Target)
        {
            case CheckTarget.Color:
            {
                if (!ColorMatch.TryParse(step.Color, out byte r, out byte g, out byte b)) return null;
                var area = step.Where == CheckArea.Point ? new PixelRect(step.FromX, step.FromY, 1, 1) : SearchArea(step);
                var image = ScreenCapture.Grab(area);
                return image == null ? null : FindColor(image, r, g, b, tolerance);
            }

            case CheckTarget.Colors:
                if (step.Points.Count == 0) return null;
                return step.Where == CheckArea.Point
                    ? CheckFixedPoints(step.Points, tolerance)
                    : FindPattern(step.Points, SearchArea(step), tolerance);

            default:
            {
                if (template == null) return null;
                var image = ScreenCapture.Grab(SearchArea(step));
                return image == null ? null : FindImage(image, template, tolerance);
            }
        }
    }

    static PixelRect SearchArea(MacroStep step) =>
        step.Where == CheckArea.Screen || step.AreaWidth <= 0 || step.AreaHeight <= 0
            ? ScreenCapture.ScreenBounds
            : step.Area;

    static PixelPoint? FindColor(ScreenImage image, byte r, byte g, byte b, int tolerance)
    {
        var bgra = image.Bgra;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (!ColorMatch.Near(bgra, i, r, g, b, tolerance)) continue;
            int pixel = i / 4;
            return image.ToScreen(pixel % image.Width, pixel / image.Width);
        }
        return null;
    }

    /// <summary>Every point must show its own color, where it is. Found means at the first point.</summary>
    static PixelPoint? CheckFixedPoints(List<ColorPoint> points, int tolerance)
    {
        // Grab them all in one go when they're close, so they're all read from the same frame.
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X); top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
        }
        long span = (long)(right - left + 1) * (bottom - top + 1);
        var shared = span <= 1_000_000 ? ScreenCapture.Grab(new PixelRect(left, top, right - left + 1, bottom - top + 1)) : null;

        foreach (var p in points)
        {
            if (!ColorMatch.TryParse(p.Color, out byte r, out byte g, out byte b)) return null;
            var image = shared ?? ScreenCapture.Grab(new PixelRect(p.X, p.Y, 1, 1));
            if (image == null) return null;
            int x = (int)((p.X - image.Area.X) * image.Scale), y = (int)((p.Y - image.Area.Y) * image.Scale);
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
            if (!ColorMatch.Near(image.Bgra, (y * image.Width + x) * 4, r, g, b, tolerance)) return null;
        }
        return new PixelPoint(points[0].X, points[0].Y);
    }

    /// <summary>
    /// The points as a shape: their spacing kept, searched for anywhere in <paramref name="area"/>.
    /// Found means where the first point lands.
    /// </summary>
    static PixelPoint? FindPattern(List<ColorPoint> points, PixelRect area, int tolerance)
    {
        var image = ScreenCapture.Grab(area);
        if (image == null) return null;
        double scale = image.Scale;

        int n = points.Count;
        var dx = new int[n]; var dy = new int[n];
        var r = new byte[n]; var g = new byte[n]; var b = new byte[n];
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ColorMatch.TryParse(points[i].Color, out r[i], out g[i], out b[i])) return null;
            dx[i] = (int)Math.Round((points[i].X - points[0].X) * scale);
            dy[i] = (int)Math.Round((points[i].Y - points[0].Y) * scale);
            minX = Math.Min(minX, dx[i]); maxX = Math.Max(maxX, dx[i]);
            minY = Math.Min(minY, dy[i]); maxY = Math.Max(maxY, dy[i]);
        }

        var bgra = image.Bgra;
        int width = image.Width;
        for (int y = -minY; y < image.Height - maxY; y++)
        for (int x = -minX; x < width - maxX; x++)
        {
            int i = 0;
            while (i < n && ColorMatch.Near(bgra, ((y + dy[i]) * width + x + dx[i]) * 4, r[i], g[i], b[i], tolerance)) i++;
            if (i == n) return image.ToScreen(x, y);
        }
        return null;
    }

    /// <summary>
    /// Slides the template over the grab and returns the middle of the first place (top to bottom,
    /// left to right) where nearly all its pixels match. A few spread-out pixels are tried first at
    /// each place, which throws out almost every wrong place after two or three comparisons.
    /// </summary>
    static PixelPoint? FindImage(ScreenImage image, ImageTemplate template, int tolerance)
    {
        int tw = template.Width, th = template.Height;
        if (tw > image.Width || th > image.Height) return null;

        // Only the opaque pixels of the template have to match. They're compared rarest color
        // first: an image that's mostly plain background would otherwise "match" every plain
        // patch of screen for a long way before failing.
        var opaque = new List<int>();
        var bucketOf = new Dictionary<int, int>();
        for (int y = 0; y < th; y++)
        for (int x = 0; x < tw; x++)
        {
            int t = (y * tw + x) * 4;
            if (template.Bgra[t + 3] < 128) continue;
            opaque.Add(y * tw + x);
            int bucket = (template.Bgra[t + 2] >> 4 << 8) | (template.Bgra[t + 1] >> 4 << 4) | (template.Bgra[t] >> 4);
            bucketOf[bucket] = bucketOf.GetValueOrDefault(bucket) + 1;
        }
        if (opaque.Count == 0) return null;
        int Rarity(int index)
        {
            int t = index * 4;
            return bucketOf[(template.Bgra[t + 2] >> 4 << 8) | (template.Bgra[t + 1] >> 4 << 4) | (template.Bgra[t] >> 4)];
        }
        // Stable, so equally rare pixels keep their spread across the image.
        var ordered = opaque.OrderBy(Rarity).ToArray();

        int allowedMisses = (int)(opaque.Count * ImageSlack);
        int probeMisses = Math.Min(allowedMisses, 2);
        int probes = Math.Min(ProbeCount, ordered.Length);
        var probeAt = ordered[..probes];

        var screen = image.Bgra;
        var picture = template.Bgra;
        int width = image.Width;
        int rows = image.Height - th + 1, columns = width - tw + 1;
        long best = long.MaxValue;

        bool MatchesAt(int left, int top, int index)
        {
            int px = index % tw, py = index / tw;
            int s = ((top + py) * width + left + px) * 4, t = index * 4;
            return ColorMatch.Near(screen, s, picture[t + 2], picture[t + 1], picture[t], tolerance);
        }

        Parallel.For(0, rows, (top, state) =>
        {
            if ((long)top * columns >= Interlocked.Read(ref best)) { state.Break(); return; }
            for (int left = 0; left < columns; left++)
            {
                int misses = 0;
                for (int p = 0; p < probes && misses <= probeMisses; p++)
                    if (!MatchesAt(left, top, probeAt[p])) misses++;
                if (misses > probeMisses) continue;

                misses = 0;
                foreach (int index in ordered)
                    if (!MatchesAt(left, top, index) && ++misses > allowedMisses) break;
                if (misses > allowedMisses) continue;

                long found = (long)top * columns + left;
                long current;
                while (found < (current = Interlocked.Read(ref best)) &&
                       Interlocked.CompareExchange(ref best, found, current) != current) { }
                return;
            }
        });

        if (best == long.MaxValue) return null;
        int foundTop = (int)(best / columns), foundLeft = (int)(best % columns);
        return image.ToScreen(foundLeft + tw / 2.0, foundTop + th / 2.0);
    }
}
