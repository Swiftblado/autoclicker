using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutoClicker.Core;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AutoClicker.Views;

/// <summary>
/// Sets up a check step: what to look for (a color, several colors, or an image), where, and what
/// the macro does when it's there and when it isn't.
/// </summary>
sealed class CheckStepWindow : Window
{
    const int PickSeconds = 3;

    readonly Window owner;
    readonly IInputBackend backend;
    readonly int stepCount;

    // What to look for
    readonly ComboBox targetBox = Combo(140, "A color", "Several colors", "An image");
    readonly Border colorSwatch = Swatch(26);
    readonly TextBox hexBox = new() { Width = 96, Watermark = "#RRGGBB" };
    readonly ColorView colorView = new()
    {
        ColorSpectrumShape = ColorSpectrumShape.Ring,
        IsAlphaEnabled = false,
        IsAlphaVisible = false,
        IsColorPaletteVisible = false
    };
    readonly Button colorDropper = new() { Content = "Dropper", Width = 90 };
    readonly StackPanel colorPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    readonly ObservableCollection<ColorPoint> points = [];
    readonly ListBox pointList = new() { Height = 120 };
    readonly NumericUpDown pointX = Number(-32000, 32000, 0, 76);
    readonly NumericUpDown pointY = Number(-32000, 32000, 0, 76);
    readonly TextBox pointHex = new() { Width = 96, Watermark = "#RRGGBB" };
    readonly Button pointPick = new() { Content = "Pick & add", Width = 100 };
    readonly Button pointAdd = new() { Content = "Add", Width = 60 };
    readonly Button pointRemove = new() { Content = "Remove", Width = 80 };
    readonly StackPanel colorsPanel = new() { Spacing = 6 };

    ImageTemplate? image;
    readonly Image imagePreview = new() { MaxWidth = 220, MaxHeight = 120, Stretch = Stretch.Uniform };
    readonly TextBlock imageInfo = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    readonly Button imageCapture = new() { Content = "Capture from screen…", Width = 170 };
    readonly Button imageLoad = new() { Content = "Load PNG…", Width = 100 };
    readonly StackPanel imagePanel = new() { Spacing = 6 };

    // Where
    readonly ComboBox whereBox = new() { Width = 250 };
    readonly NumericUpDown atX = Number(-32000, 32000, 0, 76);
    readonly NumericUpDown atY = Number(-32000, 32000, 0, 76);
    readonly Button atPick = new() { Content = "Pick", Width = 60 };
    readonly StackPanel atRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly NumericUpDown areaX = Number(-32000, 32000, 0, 70);
    readonly NumericUpDown areaY = Number(-32000, 32000, 0, 70);
    readonly NumericUpDown areaW = Number(1, 32000, 200, 70);
    readonly NumericUpDown areaH = Number(1, 32000, 200, 70);
    readonly Button areaDrag = new() { Content = "Drag area…", Width = 100 };
    readonly StackPanel areaRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly NumericUpDown tolerance = Number(0, 255, ColorMatch.DefaultTolerance, 60);

    // When
    readonly ComboBox modeBox = Combo(200, "Check once", "Wait until it appears");
    readonly NumericUpDown timeout = Number(0, 3_600_000, 10000, 90);
    readonly StackPanel timeoutRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    // Then
    readonly CheckBox clickFound = new() { Content = "Click on it with" };
    readonly ComboBox clickButton = Combo(88, "Left", "Right", "Middle");
    readonly ComboBox foundBranch = Combo(150, "Continue", "Go to step", "Stop the macro");
    readonly NumericUpDown foundStep = Number(1, 9999, 1, 60);
    readonly TextBlock missingLabel = new() { VerticalAlignment = VerticalAlignment.Center, Width = 200 };
    readonly ComboBox missingBranch = Combo(150, "Continue", "Go to step", "Stop the macro");
    readonly NumericUpDown missingStep = Number(1, 9999, 1, 60);

    readonly Button testButton = new() { Content = "Test now", Width = 90 };
    readonly TextBlock testResult = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
    readonly TextBlock problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    readonly DispatcherTimer pickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    int pickTicks;
    Button? pickButton;
    string pickButtonText = "";
    Action<PixelPoint, string?>? pickLive, pickDone;
    bool updatingColor;
    /// <summary>True while the windows are parked off screen for a capture.</summary>
    bool movedAway;

    /// <summary>
    /// Opens the editor on <paramref name="existing"/> (or a fresh check) and returns the step set
    /// up, or null if cancelled. <paramref name="stepCount"/> is how many steps the macro will have.
    /// </summary>
    public static Task<MacroStep?> EditAsync(Window owner, IInputBackend backend, MacroStep? existing, int stepCount) =>
        new CheckStepWindow(owner, backend, existing ?? NewCheck(), stepCount).ShowDialog<MacroStep?>(owner);

    static MacroStep NewCheck() => new() { Kind = StepKind.Check, Where = CheckArea.Point };

    CheckStepWindow(Window owner, IInputBackend backend, MacroStep step, int stepCount)
    {
        this.owner = owner;
        this.backend = backend;
        this.stepCount = Math.Max(1, stepCount);

        Title = "Check the screen";
        Icon = owner.Icon;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = owner.Topmost;

        foundStep.Maximum = missingStep.Maximum = this.stepCount;
        pointList.ItemsSource = points;
        pointList.ItemTemplate = new FuncDataTemplate<ColorPoint>((point, _) => point == null ? new TextBlock() : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Swatch(16, point.Color),
                new TextBlock { Text = "(" + point.X + ", " + point.Y + ")   " + point.Color, VerticalAlignment = VerticalAlignment.Center }
            }
        });

        Content = Build();
        Load(step);
        Wire();
        UpdateVisibility();
        Opened += (_, _) => FitOnScreen();
        SizeChanged += (_, _) => FitOnScreen();
    }

    // ---------------------------------------------------------------- layout

    Control Build()
    {
        colorPanel.Children.AddRange([
            new Button { Content = colorSwatch, Padding = new Thickness(4), Flyout = new Flyout { Content = colorView }, [ToolTip.TipProperty] = "Choose on a color wheel" },
            hexBox,
            colorDropper,
            Hint("Dropper: hover over the color; it's taken after " + PickSeconds + " s.")
        ]);

        colorsPanel.Children.AddRange([
            Hint("Every point must show its color. Searched for in an area, they keep their spacing."),
            pointList,
            Row(Label("X"), pointX, Label("Y"), pointY, pointHex, pointAdd, pointPick, pointRemove)
        ]);

        imagePanel.Children.AddRange([
            Row(imageCapture, imageLoad, imageInfo),
            imagePreview,
            Hint("Transparent parts of a PNG don't have to match. Small, distinctive images are found fastest.")
        ]);

        atRow.Children.AddRange([Label("X"), atX, Label("Y"), atY, atPick]);
        areaRow.Children.AddRange([Label("X"), areaX, Label("Y"), areaY, Label("W"), areaW, Label("H"), areaH, areaDrag]);
        timeoutRow.Children.AddRange([Label("Give up after"), timeout, Label("ms (0 = never)")]);

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);

        var footer = new Border
        {
            [DockPanel.DockProperty] = Dock.Bottom,
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128)),
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

        // The buttons stay put; the settings above them scroll if the screen is short.
        var settings = new StackPanel
        {
            Margin = new Thickness(18, 14, 18, 10),
            Spacing = 10,
            MinWidth = 560,
            Children =
            {
                Section("Look for"),
                targetBox,
                colorPanel,
                colorsPanel,
                imagePanel,

                Section("Where"),
                whereBox,
                atRow,
                areaRow,
                Row(Label("Tolerance"), tolerance, Hint("0 = exact color; each of red, green, blue may be off by this much.")),

                Section("When"),
                Row(modeBox, timeoutRow),

                Section("Then"),
                Row(new TextBlock { Text = "If found:", Width = 200, VerticalAlignment = VerticalAlignment.Center },
                    clickFound, clickButton),
                Row(new TextBlock { Text = "and then", Width = 200, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 },
                    foundBranch, foundStep),
                Row(missingLabel, missingBranch, missingStep)
            }
        };

        return new DockPanel
        {
            Children =
            {
                footer,
                new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            }
        };
    }

    /// <summary>Keeps the whole window, buttons included, inside the screen's working area.</summary>
    void FitOnScreen()
    {
        if (movedAway) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        MaxHeight = area.Height / scaling - 16;

        var size = new PixelSize((int)(Bounds.Width * scaling), (int)(Math.Min(Bounds.Height, MaxHeight) * scaling));
        int x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height - (int)(40 * scaling)));   // room for the title bar
        if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);
    }

    static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, -4) };
    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    static TextBlock Hint(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };

    static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange(children);
        return row;
    }

    static ComboBox Combo(double width, params string[] items) => new() { Width = width, ItemsSource = items, SelectedIndex = 0 };

    static NumericUpDown Number(int min, int max, int value, double width) => new()
    {
        Minimum = min, Maximum = max, Value = value, Width = width, FormatString = "0", Increment = 1
    };

    static Border Swatch(double size, string? hex = null) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        BorderBrush = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        Background = Brush(hex)
    };

    static IBrush Brush(string? hex) =>
        ColorMatch.TryParse(hex, out byte r, out byte g, out byte b) ? new SolidColorBrush(Color.FromRgb(r, g, b)) : Brushes.Transparent;

    // ---------------------------------------------------------------- behaviour

    void Wire()
    {
        targetBox.SelectionChanged += (_, _) => { FillWhereChoices(CheckAreaChosen()); UpdateVisibility(); };
        whereBox.SelectionChanged += (_, _) => UpdateVisibility();
        modeBox.SelectionChanged += (_, _) => UpdateVisibility();
        foundBranch.SelectionChanged += (_, _) => UpdateVisibility();
        missingBranch.SelectionChanged += (_, _) => UpdateVisibility();
        clickFound.IsCheckedChanged += (_, _) => UpdateVisibility();

        hexBox.TextChanged += (_, _) =>
        {
            colorSwatch.Background = Brush(hexBox.Text);
            if (updatingColor || !ColorMatch.TryParse(hexBox.Text, out byte r, out byte g, out byte b)) return;
            updatingColor = true;
            colorView.Color = Color.FromRgb(r, g, b);
            updatingColor = false;
        };
        colorView.ColorChanged += (_, e) =>
        {
            if (updatingColor) return;
            updatingColor = true;
            hexBox.Text = ColorMatch.Format(e.NewColor.R, e.NewColor.G, e.NewColor.B);
            updatingColor = false;
        };
        colorDropper.Click += (_, _) => StartPick(colorDropper,
            live: (_, hex) => { if (hex != null) hexBox.Text = hex; },
            done: (at, hex) =>
            {
                if (hex != null) hexBox.Text = hex;
                SetValue(atX, at.X);
                SetValue(atY, at.Y);
            });

        atPick.Click += (_, _) => StartPick(atPick,
            live: (at, _) => { SetValue(atX, at.X); SetValue(atY, at.Y); },
            done: (at, _) => { SetValue(atX, at.X); SetValue(atY, at.Y); });

        pointPick.Click += (_, _) => StartPick(pointPick,
            live: (at, hex) => ShowPoint(at, hex),
            done: (at, hex) =>
            {
                ShowPoint(at, hex);
                if (hex != null) points.Add(new ColorPoint { X = at.X, Y = at.Y, Color = hex });
            });
        pointAdd.Click += (_, _) =>
        {
            var hex = ColorMatch.Normalize(pointHex.Text);
            if (hex == null) { ShowProblem("Type the point's color as a hex code, like #FF8800."); return; }
            points.Add(new ColorPoint { X = (int)(pointX.Value ?? 0), Y = (int)(pointY.Value ?? 0), Color = hex });
            ShowProblem(null);
        };
        pointRemove.Click += (_, _) =>
        {
            if (pointList.SelectedIndex >= 0) points.RemoveAt(pointList.SelectedIndex);
        };
        pointList.SelectionChanged += (_, _) =>
        {
            if (pointList.SelectedItem is ColorPoint p) ShowPoint(new PixelPoint(p.X, p.Y), p.Color);
        };

        areaDrag.Click += async (_, _) =>
        {
            var rect = await WithWindowsHidden(() => AreaSelector.SelectAsync(owner, backend, "Drag a box around where to look."));
            if (rect is { } r) SetArea(r);
        };
        imageCapture.Click += async (_, _) => await CaptureImageAsync();
        imageLoad.Click += async (_, _) => await LoadImageAsync();
        testButton.Click += async (_, _) => await TestAsync();

        pickTimer.Tick += (_, _) => PickTick();
        Closed += (_, _) => pickTimer.Stop();
    }

    void ShowPoint(PixelPoint at, string? hex)
    {
        SetValue(pointX, at.X);
        SetValue(pointY, at.Y);
        if (hex != null) pointHex.Text = hex;
    }

    static void SetValue(NumericUpDown box, decimal value) =>
        box.Value = Math.Clamp(value, box.Minimum, box.Maximum);

    void SetArea(PixelRect r)
    {
        SetValue(areaX, r.X);
        SetValue(areaY, r.Y);
        SetValue(areaW, r.Width);
        SetValue(areaH, r.Height);
    }

    /// <summary>The "where" choices that make sense for the target, keeping <paramref name="keep"/> if it's one.</summary>
    void FillWhereChoices(CheckArea keep)
    {
        var target = (CheckTarget)Math.Max(0, targetBox.SelectedIndex);
        whereBox.ItemsSource = target switch
        {
            CheckTarget.Color => new[] { "At one point", "Anywhere in an area", "Anywhere on screen" },
            CheckTarget.Colors => new[] { "Exactly at these points", "As a pattern, anywhere in an area", "As a pattern, anywhere on screen" },
            _ => new[] { "Anywhere in an area", "Anywhere on screen" }
        };
        int index = target == CheckTarget.Image ? Math.Max(0, (int)keep - 1) : (int)keep;
        whereBox.SelectedIndex = Math.Clamp(index, 0, ((string[])whereBox.ItemsSource).Length - 1);
    }

    CheckArea CheckAreaChosen()
    {
        int index = Math.Max(0, whereBox.SelectedIndex);
        return targetBox.SelectedIndex == (int)CheckTarget.Image ? (CheckArea)(index + 1) : (CheckArea)index;
    }

    void UpdateVisibility()
    {
        var target = (CheckTarget)Math.Max(0, targetBox.SelectedIndex);
        var where = CheckAreaChosen();
        colorPanel.IsVisible = target == CheckTarget.Color;
        colorsPanel.IsVisible = target == CheckTarget.Colors;
        imagePanel.IsVisible = target == CheckTarget.Image;
        atRow.IsVisible = target == CheckTarget.Color && where == CheckArea.Point;
        areaRow.IsVisible = where == CheckArea.Area;

        bool waits = modeBox.SelectedIndex == 1;
        timeoutRow.IsVisible = waits;
        missingLabel.Text = waits ? "If it doesn't appear in time:" : "If not found:";
        clickButton.IsEnabled = clickFound.IsChecked == true;
        foundStep.IsVisible = foundBranch.SelectedIndex == (int)CheckBranch.GoTo;
        missingStep.IsVisible = missingBranch.SelectedIndex == (int)CheckBranch.GoTo;
    }

    void Load(MacroStep step)
    {
        targetBox.SelectedIndex = (int)step.Target;
        FillWhereChoices(step.Where);
        hexBox.Text = ColorMatch.Normalize(step.Color) ?? "#FF0000";
        SetValue(atX, step.FromX);
        SetValue(atY, step.FromY);
        foreach (var p in step.Points) points.Add(p.Clone());
        image = ImageTemplate.FromBase64(step.ImagePng);
        ShowImage();

        if (step.AreaWidth > 0 && step.AreaHeight > 0) SetArea(step.Area);
        else
        {
            // Start the area around the middle of the screen rather than at 0, 0.
            var screen = ScreenCapture.ScreenBounds;
            if (screen.Width > 0)
                SetArea(new PixelRect(screen.X + screen.Width / 4, screen.Y + screen.Height / 4, screen.Width / 2, screen.Height / 2));
        }
        SetValue(tolerance, step.Tolerance);
        modeBox.SelectedIndex = step.WaitUntilFound ? 1 : 0;
        SetValue(timeout, step.TimeoutMs);
        clickFound.IsChecked = step.ClickFound;
        clickButton.SelectedIndex = (int)step.Button;
        foundBranch.SelectedIndex = (int)step.OnFound;
        SetValue(foundStep, step.FoundStep);
        missingBranch.SelectedIndex = (int)step.OnMissing;
        SetValue(missingStep, step.MissingStep);
    }

    /// <summary>The step as set up now, whether or not it's complete.</summary>
    MacroStep Read()
    {
        var where = CheckAreaChosen();
        return new MacroStep
        {
            Kind = StepKind.Check,
            Target = (CheckTarget)Math.Max(0, targetBox.SelectedIndex),
            Where = where,
            Color = ColorMatch.Normalize(hexBox.Text) ?? hexBox.Text ?? "",
            FromX = (int)(atX.Value ?? 0),
            FromY = (int)(atY.Value ?? 0),
            Points = points.Select(p => p.Clone()).ToList(),
            ImagePng = image?.ToBase64(),
            AreaX = (int)(areaX.Value ?? 0),
            AreaY = (int)(areaY.Value ?? 0),
            AreaWidth = (int)(areaW.Value ?? 1),
            AreaHeight = (int)(areaH.Value ?? 1),
            Tolerance = (int)(tolerance.Value ?? ColorMatch.DefaultTolerance),
            WaitUntilFound = modeBox.SelectedIndex == 1,
            TimeoutMs = (int)(timeout.Value ?? 0),
            ClickFound = clickFound.IsChecked == true,
            Button = (ClickButton)Math.Clamp(clickButton.SelectedIndex, 0, 2),
            OnFound = (CheckBranch)Math.Max(0, foundBranch.SelectedIndex),
            FoundStep = (int)(foundStep.Value ?? 1),
            OnMissing = (CheckBranch)Math.Max(0, missingBranch.SelectedIndex),
            MissingStep = (int)(missingStep.Value ?? 1)
        };
    }

    string? Problem(MacroStep step)
    {
        switch (step.Target)
        {
            case CheckTarget.Color when ColorMatch.Normalize(step.Color) == null:
                return "Enter the color as a hex code, like #FF8800, or use the color wheel or dropper.";
            case CheckTarget.Colors when step.Points.Count == 0:
                return "Add at least one point: use Pick & add, or type X, Y and a hex code and press Add.";
            case CheckTarget.Image when image == null:
                return "Capture an image from the screen or load a PNG first.";
        }
        if (step.Where == CheckArea.Area && image != null && step.Target == CheckTarget.Image &&
            (image.Width > step.AreaWidth || image.Height > step.AreaHeight))
            return "The area is smaller than the image (" + image.Width + "×" + image.Height + "), so it can never be found there.";
        return null;
    }

    void ShowProblem(string? text)
    {
        problem.Text = text;
        problem.IsVisible = text != null;
    }

    void Accept()
    {
        var step = Read();
        var trouble = Problem(step);
        if (trouble != null) { ShowProblem(trouble); return; }
        Close(step);
    }

    // ---------------------------------------------------------------- dropper and picks

    /// <summary>
    /// Follows the mouse for a few seconds, like the main window's Pick, then takes the point and
    /// the color under it. <paramref name="live"/> runs as it goes, <paramref name="done"/> at the end.
    /// </summary>
    void StartPick(Button button, Action<PixelPoint, string?> live, Action<PixelPoint, string?> done)
    {
        if (pickButton != null) pickButton.Content = pickButtonText;   // one pick at a time
        pickButton = button;
        pickButtonText = button.Content as string ?? "";
        pickLive = live;
        pickDone = done;
        pickTicks = PickSeconds * 1000 / (int)pickTimer.Interval.TotalMilliseconds;
        pickTimer.Start();
        PickTick();
    }

    void PickTick()
    {
        if (pickButton == null) { pickTimer.Stop(); return; }
        var at = backend.GetCursorPosition();
        string? hex = ScreenCapture.ColorAt(at);
        pickTicks--;
        if (pickTicks <= 0)
        {
            pickTimer.Stop();
            pickButton.Content = pickButtonText;
            pickButton = null;
            pickDone?.Invoke(at, hex);
            if (hex == null) ShowProblem(ScreenCaptureMissing);
            return;
        }
        pickLive?.Invoke(at, hex);
        pickButton.Content = (int)Math.Ceiling(pickTicks * pickTimer.Interval.TotalMilliseconds / 1000) + "…";
    }

    static string ScreenCaptureMissing => OperatingSystem.IsMacOS()
        ? "Couldn't read the screen. Allow Auto Clicker under System Settings > Privacy & Security > Screen Recording, then reopen it."
        : "Couldn't read the screen here.";

    // ---------------------------------------------------------------- image

    void ShowImage()
    {
        imagePreview.Source = image?.ToBitmap();
        imagePreview.IsVisible = image != null;
        imageInfo.Text = image == null ? "No image yet." : image.Width + " × " + image.Height + " pixels";
    }

    async Task CaptureImageAsync()
    {
        PixelRect? chosen = null;
        var grabbed = await WithWindowsHidden(async () =>
        {
            chosen = await AreaSelector.SelectAsync(owner, backend, "Drag a box around what to look for.");
            if (chosen is not { } r || r.Width > ImageTemplate.MaxSide || r.Height > ImageTemplate.MaxSide) return null;
            // The overlay has to be gone from the screen before the grab, or it's in the picture.
            await Task.Delay(250);
            return ScreenCapture.Grab(r);
        });
        if (chosen is not { } box) return;
        if (box.Width > ImageTemplate.MaxSide || box.Height > ImageTemplate.MaxSide)
        {
            ShowProblem("That box is too big; keep images within " + ImageTemplate.MaxSide + " × " + ImageTemplate.MaxSide + ".");
            return;
        }
        if (grabbed == null) { ShowProblem(ScreenCaptureMissing); return; }
        image = ImageTemplate.FromScreen(grabbed);
        ShowImage();
        ShowProblem(null);
    }

    async Task LoadImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Image to look for",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg"] }]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        var loaded = ImageTemplate.Decode(stream);
        if (loaded == null)
        {
            ShowProblem("Couldn't use that image. It must be a readable picture no bigger than " +
                        ImageTemplate.MaxSide + " × " + ImageTemplate.MaxSide + " pixels.");
            return;
        }
        image = loaded;
        ShowImage();
        ShowProblem(null);
    }

    // ---------------------------------------------------------------- test

    async Task TestAsync()
    {
        var step = Read();
        var trouble = Problem(step);
        if (trouble != null) { ShowProblem(trouble); return; }
        ShowProblem(null);
        testResult.Text = "Looking…";

        var template = image;
        var (found, ms) = await WithWindowsHidden(async () =>
        {
            await Task.Delay(250);   // let the windows vanish before looking
            return await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var at = ScreenCheck.Find(step, template);
                return (at, clock.ElapsedMilliseconds);
            });
        });
        testResult.Text = found is { } at
            ? "Found at (" + at.X + ", " + at.Y + ") in " + ms + " ms."
            : "Not found right now (looked for " + ms + " ms).";
    }

    /// <summary>
    /// Moves this window and the main one off the screen while <paramref name="work"/> runs, so
    /// they're neither in the way nor in the picture. (Opacity alone leaves the title bars showing.)
    /// </summary>
    async Task<T> WithWindowsHidden<T>(Func<Task<T>> work)
    {
        var ownPosition = Position;
        var ownerPosition = owner.Position;
        var away = new PixelPoint(-30000, -30000);
        movedAway = true;
        owner.Position = away;
        Position = away;
        await Task.Delay(150);   // let the move reach the screen
        try
        {
            return await work();
        }
        finally
        {
            owner.Position = ownerPosition;
            Position = ownPosition;
            movedAway = false;
            Activate();
        }
    }
}
