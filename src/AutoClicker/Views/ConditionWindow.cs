using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AutoClicker.Core;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoClicker.Views;

/// <summary>
/// Builds the condition of an If, Else if or While out of screen checks, joined with "and" and
/// "or", in nested groups, each one optionally turned around with "not". For a While it also
/// sets how often the condition is looked at and for how long the loop may run.
/// </summary>
sealed class ConditionWindow : Window
{
    public sealed record Result(Condition Condition, int IntervalMs, int LimitMs);

    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3B82F6"));
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B95A7"));
    static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FBBF24"));
    static readonly IBrush GroupLine = new SolidColorBrush(Color.Parse("#3B4252"));

    readonly IInputBackend backend;
    readonly string heading;
    readonly bool isWhile;
    readonly Condition root;

    readonly StackPanel tree = new() { Spacing = 6 };
    readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640, FontWeight = FontWeight.SemiBold };

    readonly NumericUpDown interval = new()
    {
        Minimum = 0, Maximum = 3_600_000, Width = 90, FormatString = "0", Increment = 10, VerticalAlignment = VerticalAlignment.Center
    };
    readonly NumericUpDown limit = new()
    {
        Minimum = 0, Maximum = 86_400, Width = 90, FormatString = "0.##", Increment = 1, VerticalAlignment = VerticalAlignment.Center
    };
    readonly TextBlock busyWarning = new()
    {
        Text = "Checking this often uses a lot of CPU, especially for image checks, and can slow down your computer or the app you're automating.",
        Foreground = Warning, TextWrapping = TextWrapping.Wrap, MaxWidth = 600
    };

    readonly Button testButton = new() { Content = "Test now", Width = 90 };
    readonly TextBlock testResult = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    readonly TextBlock problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxWidth = 600 };

    bool editingCheck;

    /// <summary>
    /// Opens the editor and returns what was set up, or null if cancelled. <paramref name="heading"/>
    /// is the line's name ("If", "Else if", "While").
    /// </summary>
    public static Task<Result?> EditAsync(Window owner, IInputBackend backend, string heading, Condition? existing,
        bool isWhile, int intervalMs = MacroStep.DefaultIntervalMs, int limitMs = MacroStep.DefaultLimitMs) =>
        new ConditionWindow(owner, backend, heading, existing, isWhile, intervalMs, limitMs).ShowDialog<Result?>(owner);

    ConditionWindow(Window owner, IInputBackend backend, string heading, Condition? existing, bool isWhile, int intervalMs, int limitMs)
    {
        this.backend = backend;
        this.heading = heading;
        this.isWhile = isWhile;
        root = existing?.Clone() ?? Condition.Group();
        if (!root.IsGroup) root = Condition.Group(LogicJoin.And, root);

        Title = heading + " condition";
        Icon = owner.Icon;
        ShowInTaskbar = false;
        CanMinimize = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = owner.Topmost;

        interval.Value = Math.Max(0, intervalMs);
        limit.Value = Math.Max(0, limitMs) / 1000m;
        interval.ValueChanged += (_, _) => UpdateWarning();

        Content = Build();
        Rebuild();
        UpdateWarning();
        testButton.Click += async (_, _) => await TestAsync();

        // A brand-new condition starts by adding its first check.
        if (root.Items.Count == 0) Opened += async (_, _) => await AddCheckAsync(root);
        Opened += (_, _) => FitOnScreen();
        SizeChanged += (_, _) => FitOnScreen();
    }

    // ---------------------------------------------------------------- layout

    Control Build()
    {
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);

        var body = new StackPanel
        {
            Margin = new Thickness(18, 14, 18, 10),
            Spacing = 10,
            MinWidth = 640,
            Children =
            {
                Section("Condition"),
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#11141B")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8),
                    Child = preview
                },
                Hint("Each check says found or not found. \"not\" turns one around. A group is true when all of its items are (and) or when any one is (or). Groups can go inside groups, for things like (A and not B) or C."),
                tree
            }
        };

        if (isWhile)
        {
            body.Children.Add(Section("Looping"));
            body.Children.Add(Row(Label("Check the condition every"), interval, Label("ms   (0 = nonstop)")));
            body.Children.Add(busyWarning);
            body.Children.Add(Row(Label("Stop looping after"), limit, Label("s   (0 = no limit)")));
            body.Children.Add(Hint("The loop goes round while the condition is true. When time runs out it stops, and the macro carries on after End while."));
        }

        var footer = new Border
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            BorderBrush = GroupLine,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 10, 18, 14),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Row(testButton, testResult),
                    problem,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            }
        };

        return new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            }
        };
    }

    /// <summary>Redraws the whole tree; it's small, and it keeps the view honest after any change.</summary>
    void Rebuild()
    {
        tree.Children.Clear();
        tree.Children.Add(GroupView(root, parent: null));
        UpdatePreview();
    }

    void UpdatePreview()
    {
        preview.Text = heading + " " + (root.Items.Count == 0 ? "…  (add a check)" : root.Describe());
        ShowProblem(null);
    }

    Control GroupView(Condition group, Condition? parent)
    {
        var not = new CheckBox { Content = "not", IsChecked = group.Not, VerticalAlignment = VerticalAlignment.Center };
        not.IsCheckedChanged += (_, _) => { group.Not = not.IsChecked == true; UpdatePreview(); };

        var join = new ComboBox
        {
            Width = 250,
            ItemsSource = new[] { "All of these are true (and)", "Any of these is true (or)" },
            SelectedIndex = (int)group.Join
        };
        join.SelectionChanged += (_, _) => { group.Join = (LogicJoin)Math.Max(0, join.SelectedIndex); Rebuild(); };

        var header = Row(not, join);
        if (parent != null)
        {
            var remove = new Button { Content = "Remove group" };
            remove.Click += (_, _) => { parent.Items.Remove(group); Rebuild(); };
            header.Children.Add(remove);
        }

        var items = new StackPanel { Spacing = 4 };
        for (int k = 0; k < group.Items.Count; k++)
        {
            if (k > 0)
                items.Children.Add(new TextBlock
                {
                    Text = group.Join == LogicJoin.And ? "and" : "or",
                    Foreground = Accent, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 0, 0)
                });
            var item = group.Items[k];
            items.Children.Add(item.IsGroup ? GroupView(item, group) : CheckView(item, group));
        }
        if (group.Items.Count == 0) items.Children.Add(Hint("Empty: add a check or a group."));

        var addCheck = new Button { Content = "+ Check…" };
        addCheck.Click += async (_, _) => await AddCheckAsync(group);
        var addGroup = new Button { Content = "+ Group" };
        addGroup.Click += (_, _) => { group.Items.Add(Condition.Group(group.Join == LogicJoin.And ? LogicJoin.Or : LogicJoin.And)); Rebuild(); };

        var content = new StackPanel { Spacing = 6, Children = { header, items, Row(addCheck, addGroup) } };
        return new Border
        {
            BorderBrush = parent == null ? Brushes.Transparent : GroupLine,
            BorderThickness = parent == null ? new Thickness(0) : new Thickness(2, 0, 0, 0),
            Padding = parent == null ? new Thickness(0) : new Thickness(12, 4, 0, 4),
            Margin = parent == null ? new Thickness(0) : new Thickness(8, 2, 0, 2),
            Child = content
        };
    }

    Control CheckView(Condition item, Condition parent)
    {
        var not = new CheckBox { Content = "not", IsChecked = item.Not, VerticalAlignment = VerticalAlignment.Center };
        not.IsCheckedChanged += (_, _) => { item.Not = not.IsChecked == true; UpdatePreview(); };

        var text = new TextBlock
        {
            Text = item.Check!.DescribeLook(),
            Width = 330,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(text, item.Check.DescribeLook());

        var edit = new Button { Content = "Edit…" };
        edit.Click += async (_, _) =>
        {
            if (editingCheck) return;
            editingCheck = true;
            try
            {
                var changed = await CheckStepWindow.EditAsync(this, backend, item.Check.Clone());
                if (changed != null) { item.Check = changed; Rebuild(); }
            }
            finally { editingCheck = false; }
        };
        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) => { parent.Items.Remove(item); Rebuild(); };

        var swatch = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
            Background = item.Check.Target == CheckTarget.Color && ColorMatch.TryParse(item.Check.Color, out byte r, out byte g, out byte b)
                ? new SolidColorBrush(Color.FromRgb(r, g, b))
                : Muted
        };
        return Row(not, swatch, text, edit, remove);
    }

    async Task AddCheckAsync(Condition group)
    {
        if (editingCheck) return;
        editingCheck = true;
        try
        {
            var check = await CheckStepWindow.EditAsync(this, backend, null);
            if (check == null) return;
            group.Items.Add(Condition.Of(check));
            Rebuild();
        }
        finally { editingCheck = false; }
    }

    void UpdateWarning() => busyWarning.IsVisible = isWhile && (interval.Value ?? 0) < MacroStep.BusyIntervalMs;

    // ---------------------------------------------------------------- finishing

    void ShowProblem(string? text)
    {
        problem.Text = text;
        problem.IsVisible = text != null;
    }

    void Accept()
    {
        if (root.Problem() is { } trouble) { ShowProblem(trouble); return; }
        Close(new Result(root.Clone(), (int)(interval.Value ?? 0), (int)Math.Round((limit.Value ?? 0) * 1000)));
    }

    async Task TestAsync()
    {
        if (root.Problem() is { } trouble) { ShowProblem(trouble); return; }
        ShowProblem(null);
        testResult.Text = "Looking…";

        var condition = root.Clone();
        bool holds;
        long ms;
        using (WindowHider.Hide())
        {
            await Task.Delay(250);   // let the windows leave the screen before looking
            (holds, ms) = await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                bool result = condition.Evaluate(check =>
                    ScreenCheck.Find(check, check.Target == CheckTarget.Image ? ImageTemplate.FromBase64(check.ImagePng) : null) != null);
                return (result, clock.ElapsedMilliseconds);
            });
        }
        string what = isWhile ? "the loop would go round" : "its lines would run";
        testResult.Text = (holds ? "True right now: " + what : "False right now: " + (isWhile ? "the loop would be skipped" : "its lines would be skipped")) +
                          " (" + ms + " ms).";
    }

    /// <summary>Keeps the whole window, buttons included, inside the screen's working area.</summary>
    void FitOnScreen()
    {
        if (WindowHider.IsHiding) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        MaxHeight = area.Height / scaling - 16;

        var size = new PixelSize((int)(Bounds.Width * scaling), (int)(Math.Min(Bounds.Height, MaxHeight) * scaling));
        int x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height - (int)(40 * scaling)));
        if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
    }

    static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, -4) };
    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
    static TextBlock Hint(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };

    static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange(children);
        return row;
    }
}
