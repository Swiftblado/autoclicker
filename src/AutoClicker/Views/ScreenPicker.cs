using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoClicker.Views;

/// <summary>
/// Every "pick a spot on the screen" goes through here, so they all behave the same: the app's
/// windows get out of the way, a countdown follows the mouse, and the spot is taken when time
/// runs out (or at a click, when that's switched on).
/// </summary>
static class ScreenPicker
{
    public const int MinSeconds = 1, MaxSeconds = 60, DefaultSeconds = 3;

    /// <summary>How long a pick waits before taking the spot under the mouse. A global setting.</summary>
    public static int Seconds { get; set; } = DefaultSeconds;

    /// <summary>A left click picks straight away (and doesn't reach the app underneath). A global setting.</summary>
    public static bool PickOnClick { get; set; }

    const int TickMs = 50;

    /// <summary>
    /// Waits out the countdown (or a click) with the app's windows hidden, then returns the spot.
    /// <paramref name="live"/> runs as the mouse moves, to show where it would pick.
    /// </summary>
    public static async Task<PixelPoint> PickAsync(IInputBackend backend, Action<PixelPoint>? live = null)
    {
        using var hidden = WindowHider.Hide();
        var bubble = new TimerBubble();
        PixelPoint? clickedAt = null;
        using var catcher = PickOnClick
            ? ClickCatcher.Start(at => Dispatcher.UIThread.Post(() => clickedAt ??= at))
            : null;

        var clock = Stopwatch.StartNew();
        double total = Seconds * 1000.0;
        try
        {
            bubble.Show();
            while (clickedAt == null && clock.ElapsedMilliseconds < total)
            {
                var at = backend.GetCursorPosition();
                double left = (total - clock.ElapsedMilliseconds) / 1000;
                bubble.Update(at, left.ToString("0.0", CultureInfo.CurrentCulture) + " s" + (catcher != null ? "  ·  or click" : ""));
                live?.Invoke(at);
                await Task.Delay(TickMs);
            }
        }
        finally
        {
            bubble.Close();
        }

        // Let the bubble leave the screen, so a color read at this spot isn't the bubble's.
        await Task.Delay(80);
        var picked = clickedAt ?? backend.GetCursorPosition();
        live?.Invoke(picked);
        return picked;
    }

    /// <summary>The countdown next to the mouse: a small, borderless window that never takes focus.</summary>
    sealed class TimerBubble : Window
    {
        readonly TextBlock text = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeight.SemiBold };

        public TimerBubble()
        {
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            CanMinimize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-30000, -30000);
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Focusable = false;
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 30, 30, 30)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4),
                Child = text
            };
        }

        /// <summary>
        /// Below and right of the pointer, so the spot being picked stays uncovered; flipped to
        /// the other side near the screen's right or bottom edge.
        /// </summary>
        public void Update(PixelPoint cursor, string label)
        {
            text.Text = label;
            double scaling = RenderScaling;
            int offset = (int)(20 * scaling);
            int width = (int)(Bounds.Width * scaling), height = (int)(Bounds.Height * scaling);
            int x = cursor.X + offset, y = cursor.Y + offset;
            if (Screens.ScreenFromPoint(cursor) is { } screen)
            {
                var area = screen.WorkingArea;
                if (x + width > area.Right) x = cursor.X - offset - width;
                if (y + height > area.Bottom) y = cursor.Y - offset - height;
            }
            Position = new PixelPoint(x, y);
        }
    }
}

/// <summary>
/// Moves every open window of the app off the screen and back. Nested calls are fine: windows
/// come back when the outermost one is disposed. Windows opened while hidden (overlays, the
/// countdown) are left alone.
/// </summary>
static class WindowHider
{
    static readonly PixelPoint Away = new(-30000, -30000);
    static List<(Window Window, PixelPoint Position)> parked = [];
    static int depth;

    /// <summary>True while the app's windows are off screen; windows shouldn't move themselves back meanwhile.</summary>
    public static bool IsHiding => depth > 0;

    public static IDisposable Hide()
    {
        if (depth++ == 0)
        {
            var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                          ?? (IReadOnlyList<Window>)[];
            parked = windows.Where(w => w.IsVisible).Select(w => (w, w.Position)).ToList();
            foreach (var (window, _) in parked) window.Position = Away;
        }
        return new Restorer();
    }

    sealed class Restorer : IDisposable
    {
        bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            if (--depth > 0) return;
            foreach (var (window, position) in parked)
                if (window.IsVisible) window.Position = position;
            // The topmost-most recent (a dialog, usually) gets the keyboard back.
            parked.LastOrDefault().Window?.Activate();
            parked = [];
        }
    }
}
