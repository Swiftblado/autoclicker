using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutoClicker.Core;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoClicker.Views;

/// <summary>
/// Builds the condition of an If, Else if or While: a list of screen checks, each one optionally
/// turned around with "not", with an and/or switch between each check and the one above it.
/// Groups (brackets) are there for the rare condition that needs them. For a While it also sets
/// how often the condition is looked at and for how long the loop may run.
/// </summary>
sealed class ConditionWindow : Window
{
    public sealed record Result(Condition Condition, int IntervalMs, int LimitMs);

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
        Minimum = 0, Maximum = 3_600_000, Width = 90, FormatString = "0", Increment = 10, VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = "The least time between two looks at the condition. Longer is lighter on your computer; 0 looks again straight away."
    };
    readonly NumericUpDown limit = new()
    {
        Minimum = 0, Maximum = 86_400, Width = 90, FormatString = "0.##", Increment = 1, VerticalAlignment = VerticalAlignment.Center,
        [ToolTip.TipProperty] = "After this long the loop stops even if the condition is still true, and the macro carries on after End while. 0 lets it loop for as long as the condition holds."
    };
    readonly TextBlock busyWarning = new()
    {
        Text = "Checking this often uses a lot of CPU, especially for image checks, and can slow down your computer or the app you're automating.",
        Foreground = Warning, TextWrapping = TextWrapping.Wrap, MaxWidth = 600
    };

    readonly Button ok = new()
    {
        Content = "OK", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" },
        [ToolTip.TipProperty] = "Saves the condition to the line. It needs at least one screen check."
    };
    readonly TextBlock needsCheck = new()
    {
        Text = "Add at least one screen check to save.", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
    };
    readonly Button testButton = new()
    {
        Content = "Test now", Width = 90,
        [ToolTip.TipProperty] = "Hides this window, looks at the screen once and says whether the whole condition is true right now."
    };
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
        Opened += (_, _) => FitOnScreen();
        SizeChanged += (_, _) => FitOnScreen();
    }

    // ---------------------------------------------------------------- layout

    Control Build()
    {
        var cancel = new Button
        {
            Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center,
            [ToolTip.TipProperty] = "Closes without changing the line."
        };
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
                    Child = preview,
                    [ToolTip.TipProperty] = "The whole condition in words. Brackets show which checks go together."
                },
                Hint("Each check says found or not found; tick \"not\" to turn it around. Between two checks, choose \"and\" " +
                     "(both must be true) or \"or\" (either is enough). \"and\" goes first, so A or B and C means A or (B and C)."),
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
                    new DockPanel
                    {
                        Children =
                        {
                            new StackPanel
                            {
                                [DockPanel.DockProperty] = Dock.Right,
                                Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel }
                            },
                            needsCheck
                        }
                    }
                }
            }
        };

        return new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            }
        };
    }

    /// <summary>Redraws the whole list; it's small, and it keeps the view honest after any change.</summary>
    void Rebuild()
    {
        tree.Children.Clear();
        tree.Children.Add(ItemsView(root));
        UpdatePreview();
    }

    void UpdatePreview()
    {
        bool hasCheck = root.Checks().Any();
        preview.Text = heading + " " + (hasCheck ? root.Describe() : "…  (add a screen check)");
        ok.IsEnabled = hasCheck;
        needsCheck.IsVisible = !hasCheck;
        ShowProblem(null);
    }

    /// <summary>A group's items, an and/or switch between each pair, and the buttons that add more.</summary>
    Control ItemsView(Condition group)
    {
        var items = new StackPanel { Spacing = 4 };
        for (int k = 0; k < group.Items.Count; k++)
        {
            if (k > 0) items.Children.Add(LinkSwitch(group, k));
            var item = group.Items[k];
            items.Children.Add(item.IsGroup ? GroupView(item, group) : CheckView(item, group));
        }
        if (group.Items.Count == 0)
            items.Children.Add(Hint(group == root ? "No checks yet. Press + Screen check… to add the first one." : "Empty group: add a check to it, or remove it."));

        var addCheck = new Button
        {
            Content = "+ Screen check…",
            [ToolTip.TipProperty] = "Opens the screen check editor. The new check goes at the end, joined with \"and\"; switch it to \"or\" if you like."
        };
        addCheck.Click += async (_, _) => await AddCheckAsync(group);
        var addGroup = new Button
        {
            Content = "+ Group",
            [ToolTip.TipProperty] = "For brackets that \"and goes first\" can't give, like (A or B) and C: the checks in a group are worked out together, as one item."
        };
        addGroup.Click += (_, _) =>
        {
            group.Items.Add(new Condition { Join = LogicJoin.And, Link = LogicJoin.And });
            Rebuild();
        };

        return new StackPanel { Spacing = 6, Children = { items, Row(addCheck, addGroup) } };
    }

    /// <summary>"and | or" between item <paramref name="index"/> and the one above it.</summary>
    Control LinkSwitch(Condition group, int index)
    {
        var link = group.LinkOf(index);
        ToggleButton Choice(string text, LogicJoin join, string tip)
        {
            var button = new ToggleButton
            {
                Content = text, IsChecked = link == join, MinWidth = 52, Padding = new Thickness(8, 2),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                [ToolTip.TipProperty] = tip
            };
            button.Click += (_, _) =>
            {
                group.Items[index].Link = join;
                Rebuild();
            };
            return button;
        }
        var row = Row(
            Choice("and", LogicJoin.And, "Both this and the line above must be true."),
            Choice("or", LogicJoin.Or, "Either this or the line above is enough. \"and\" is worked out before \"or\"."));
        row.Spacing = 2;
        row.Margin = new Thickness(22, 0, 0, 0);
        return row;
    }

    CheckBox NotBox(Condition item, string tip)
    {
        var not = new CheckBox
        {
            Content = "not", IsChecked = item.Not, VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = tip
        };
        not.IsCheckedChanged += (_, _) => { item.Not = not.IsChecked == true; UpdatePreview(); };
        return not;
    }

    Control GroupView(Condition group, Condition parent)
    {
        var remove = new Button
        {
            Content = "Remove group",
            [ToolTip.TipProperty] = "Removes this group and every check inside it."
        };
        remove.Click += (_, _) => { parent.Items.Remove(group); Rebuild(); };

        var header = Row(NotBox(group, "True when the group as a whole is false, and the other way round."),
            new TextBlock { Text = "Group", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, remove);

        return new Border
        {
            BorderBrush = GroupLine,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Margin = new Thickness(8, 2, 0, 2),
            Child = new StackPanel { Spacing = 6, Children = { header, ItemsView(group) } }
        };
    }

    Control CheckView(Condition item, Condition parent)
    {
        var check = item.Check!;
        var text = new TextBlock
        {
            Text = check.DescribeLook(),
            Width = 330,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = check.DescribeLook()
        };

        var edit = new Button { Content = "Edit…", [ToolTip.TipProperty] = "Change what this check looks for and where." };
        edit.Click += async (_, _) =>
        {
            if (editingCheck) return;
            editingCheck = true;
            try
            {
                var changed = await CheckStepWindow.EditAsync(this, backend, check.Clone());
                if (changed != null) { item.Check = changed; Rebuild(); }
            }
            finally { editingCheck = false; }
        };
        var remove = new Button { Content = "Remove", [ToolTip.TipProperty] = "Takes this check out of the condition." };
        remove.Click += (_, _) => { parent.Items.Remove(item); Rebuild(); };

        var swatch = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
            Background = check.Target == CheckTarget.Color && ColorMatch.TryParse(check.Color, out byte r, out byte g, out byte b)
                ? new SolidColorBrush(Color.FromRgb(r, g, b))
                : Muted
        };
        return Row(NotBox(item, "True when this check finds nothing, and false when it finds its target."), swatch, text, edit, remove);
    }

    async Task AddCheckAsync(Condition group)
    {
        if (editingCheck) return;
        editingCheck = true;
        try
        {
            var check = await CheckStepWindow.EditAsync(this, backend, null);
            if (check == null) return;
            var item = Condition.Of(check);
            if (group.Items.Count > 0) item.Link = LogicJoin.And;
            group.Items.Add(item);
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
        if (!root.Checks().Any()) { ShowProblem("Add at least one screen check first."); return; }
        if (root.Problem() is { } trouble) { ShowProblem(trouble); return; }
        var condition = root.Clone();
        condition.FixLinks();
        Close(new Result(condition, (int)(interval.Value ?? 0), (int)Math.Round((limit.Value ?? 0) * 1000)));
    }

    async Task TestAsync()
    {
        if (!root.Checks().Any()) { ShowProblem("Add at least one screen check first."); return; }
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
