using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AutoClicker.Views;

/// <summary>
/// Dims every screen and lets the user drag a box over it, like a snipping tool. The box is
/// measured with the mouse's own position at press and release, so it comes out in the same
/// coordinates clicks and screen grabs use, whatever the display scaling.
/// </summary>
static class AreaSelector
{
    /// <summary>
    /// Shows the overlay and waits for a box. Null if cancelled with Esc or a right-click.
    /// The caller hides its own windows first if they'd be in the way.
    /// </summary>
    public static async Task<PixelRect?> SelectAsync(Window owner, IInputBackend backend, string hint)
    {
        var done = new TaskCompletionSource<PixelRect?>();
        var overlays = new List<Window>();
        bool finished = false;
        bool dragging = false;
        var timers = new List<(Window Window, Border Bubble, TextBlock Text)>();
        var countdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double total = ScreenPicker.Seconds * 1000.0;
        using var hidden = WindowHider.Hide();

        void Finish(PixelRect? result)
        {
            if (finished) return;
            finished = true;
            countdown.Stop();
            foreach (var overlay in overlays) overlay.Close();
            done.TrySetResult(result);
        }

        foreach (var screen in owner.Screens.All)
        {
            var box = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246)),
                IsVisible = false
            };
            var bubbleText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeight.SemiBold };
            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 30, 30, 30)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4),
                IsHitTestVisible = false,
                IsVisible = false,
                Child = bubbleText
            };
            var canvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
                Cursor = new Cursor(StandardCursorType.Cross),
                Children =
                {
                    box,
                    bubble,
                    new Border
                    {
                        [Canvas.LeftProperty] = 24.0,
                        [Canvas.TopProperty] = 24.0,
                        Background = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8),
                        Child = new TextBlock { Text = hint + "   Esc or right-click cancels.", Foreground = Brushes.White }
                    }
                }
            };

            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                Topmost = true,
                ShowInTaskbar = false,
                CanMinimize = false,
                CanResize = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = screen.Bounds.Position,
                Width = screen.Bounds.Width / screen.Scaling,
                Height = screen.Bounds.Height / screen.Scaling,
                Content = canvas
            };

            Point? startLocal = null;
            PixelPoint startScreen = default;

            canvas.PointerPressed += (_, e) =>
            {
                var point = e.GetCurrentPoint(canvas);
                if (point.Properties.IsRightButtonPressed) { Finish(null); return; }
                if (!point.Properties.IsLeftButtonPressed) return;
                startLocal = point.Position;
                startScreen = backend.GetCursorPosition();
                dragging = true;
                e.Pointer.Capture(canvas);
            };
            canvas.PointerMoved += (_, e) =>
            {
                if (startLocal is not { } start) return;
                var now = e.GetPosition(canvas);
                box.IsVisible = true;
                Canvas.SetLeft(box, Math.Min(start.X, now.X));
                Canvas.SetTop(box, Math.Min(start.Y, now.Y));
                box.Width = Math.Abs(now.X - start.X);
                box.Height = Math.Abs(now.Y - start.Y);
            };
            canvas.PointerReleased += (_, e) =>
            {
                if (startLocal == null) return;
                startLocal = null;
                dragging = false;
                var end = backend.GetCursorPosition();
                int left = Math.Min(startScreen.X, end.X), top = Math.Min(startScreen.Y, end.Y);
                int width = Math.Abs(end.X - startScreen.X) + 1, height = Math.Abs(end.Y - startScreen.Y) + 1;
                // A click without a drag is almost certainly a slip, not a 1-pixel box.
                if (width < 3 || height < 3) { box.IsVisible = false; return; }
                Finish(new PixelRect(left, top, width, height));
            };
            window.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
            window.Closed += (_, _) => Finish(null);
            timers.Add((window, bubble, bubbleText));
            overlays.Add(window);
        }

        if (overlays.Count == 0) return null;

        // The countdown follows the mouse on whichever screen it's on. Time running out cancels,
        // but never cuts off a box that's being dragged.
        countdown.Tick += (_, _) =>
        {
            double left = (total - clock.ElapsedMilliseconds) / 1000;
            if (left <= 0 && !dragging) { Finish(null); return; }
            var cursor = backend.GetCursorPosition();
            foreach (var (window, bubble, text) in timers)
            {
                var local = window.PointToClient(cursor);
                bool here = local.X >= 0 && local.Y >= 0 && local.X < window.Bounds.Width && local.Y < window.Bounds.Height;
                bubble.IsVisible = here;
                if (!here) continue;
                text.Text = dragging ? "Let go to finish" : Math.Max(0, left).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " s";
                // Flip to the other side of the mouse near the right or bottom edge.
                var size = bubble.Bounds.Size;
                Canvas.SetLeft(bubble, local.X + 20 + size.Width > window.Bounds.Width ? local.X - 20 - size.Width : local.X + 20);
                Canvas.SetTop(bubble, local.Y + 20 + size.Height > window.Bounds.Height ? local.Y - 20 - size.Height : local.Y + 20);
            }
        };
        countdown.Start();
        foreach (var overlay in overlays) overlay.Show();
        // Give the screen under the mouse the keyboard, so Esc works straight away.
        Dispatcher.UIThread.Post(() => (overlays.FirstOrDefault(o => o.IsPointerOver) ?? overlays[0]).Activate());
        return await done.Task;
    }
}
