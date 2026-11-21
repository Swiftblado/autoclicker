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
/// Sets up one screen check of a condition: what to look for (a color, several colors, or an
/// image) and where. A check only answers found or not found; the If or While around it decides
/// what happens.
/// </summary>
sealed class CheckStepWindow : Window
{


    readonly Window owner;
    readonly IInputBackend backend;

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
    readonly Button colorDropper = new() { Content = "Dropper", Width = 90, [ToolTip.TipProperty] = "Hides the windows; hover over any pixel and its color (and spot) is taken when the countdown by the mouse ends." };
    readonly StackPanel colorPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    readonly ObservableCollection<ColorPoint> points = [];
    readonly ListBox pointList = new() { Height = 120, [ToolTip.TipProperty] = "The colors to look for, each at its spot. Select one to see it below." };
    readonly NumericUpDown pointX = Number(-32000, 32000, 0, 76);
    readonly NumericUpDown pointY = Number(-32000, 32000, 0, 76);
    readonly TextBox pointHex = new() { Width = 96, Watermark = "#RRGGBB" };
    readonly Button pointPick = new() { Content = "Pick & add", Width = 100, [ToolTip.TipProperty] = "Hides the windows; the spot under the mouse and its color are added when the countdown ends." };
    readonly Button pointAdd = new() { Content = "Add", Width = 60, [ToolTip.TipProperty] = "Adds the X, Y and color typed to the left." };
    readonly Button pointRemove = new() { Content = "Remove", Width = 80, [ToolTip.TipProperty] = "Removes the selected point." };
    readonly StackPanel colorsPanel = new() { Spacing = 6 };

    ImageTemplate? image;
    readonly Image imagePreview = new() { MaxWidth = 220, MaxHeight = 120, Stretch = Stretch.Uniform };
    readonly TextBlock imageInfo = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    readonly Button imageCapture = new() { Content = "Capture from screen…", Width = 170, [ToolTip.TipProperty] = "Hides the windows; drag a box around what to look for, and that part of the screen becomes the image." };
    readonly Button imageLoad = new() { Content = "Load PNG…", Width = 100, [ToolTip.TipProperty] = "Uses a picture file. Transparent parts of a PNG don't have to match." };
    readonly StackPanel imagePanel = new() { Spacing = 6 };

    // Where
    readonly ComboBox whereBox = new() { Width = 250, [ToolTip.TipProperty] = "Where on the screen to look. A smaller area is found faster." };
    readonly NumericUpDown atX = Number(-32000, 32000, 0, 76);
    readonly NumericUpDown atY = Number(-32000, 32000, 0, 76);
    readonly Button atPick = new() { Content = "Pick", Width = 60, [ToolTip.TipProperty] = "Hides the windows; the spot under the mouse is taken when the countdown ends." };
    readonly StackPanel atRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly NumericUpDown areaX = Number(-32000, 32000, 0, 70);
    readonly NumericUpDown areaY = Number(-32000, 32000, 0, 70);
    readonly NumericUpDown areaW = Number(1, 32000, 200, 70);
    readonly NumericUpDown areaH = Number(1, 32000, 200, 70);
    readonly Button areaDrag = new() { Content = "Drag area…", Width = 100, [ToolTip.TipProperty] = "Hides the windows; drag a box over the screen to set where to look." };
    readonly StackPanel areaRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly NumericUpDown tolerance = Number(0, 255, ColorMatch.DefaultTolerance, 60);

    // Click at (only when the check aims a click or drag)
    readonly bool aim;
    readonly ComboBox spotBox = new()
    {
        Width = 300, SelectedIndex = 0,
        ItemsSource = new[] { "Its exact center", "Its center, moved by pixels", "Its center, moved by a share of its size" },
        [ToolTip.TipProperty] = "Which spot of what's found the click goes to."
    };
    readonly NumericUpDown offsetX = Number(-32000, 32000, 0, 80);
    readonly NumericUpDown offsetY = Number(-32000, 32000, 0, 80);
    readonly TextBlock offsetXUnit = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    readonly TextBlock offsetYUnit = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    readonly StackPanel offsetRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly TextBlock spotHint = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 520
    };

    readonly Button testButton = new() { Content = "Test now", Width = 90, [ToolTip.TipProperty] = "Hides the windows and looks once: says whether it's found right now, where, and how long the search took." };
    readonly TextBlock testResult = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
    readonly TextBlock problem = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    bool picking;
    bool updatingColor;

    /// <summary>
    /// Opens the editor on <paramref name="existing"/> (or a fresh check) and returns the check set
    /// up, or null if cancelled. With <paramref name="aim"/> the check aims a click or drag, and
    /// the window also asks which spot of what it finds to use.
    /// </summary>
    public static Task<MacroStep?> EditAsync(Window owner, IInputBackend backend, MacroStep? existing, bool aim = false) =>
        new CheckStepWindow(owner, backend, existing ?? MacroStep.NewCheck(), aim).ShowDialog<MacroStep?>(owner);

    CheckStepWindow(Window owner, IInputBackend backend, MacroStep step, bool aim)
    {
        this.owner = owner;
        this.backend = backend;
        this.aim = aim;

        Title = aim ? "Screen check to aim at" : "Screen check";
        Icon = owner.Icon;
        ShowInTaskbar = false;
        CanMinimize = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = owner.Topmost;

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
            Hint("Dropper: hover over the color; it's taken when the countdown by the mouse ends.")
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

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
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
        ToolTip.SetTip(ok, "Saves this check.");
        ToolTip.SetTip(cancel, "Closes without saving.");
        ToolTip.SetTip(targetBox, "A color: one color at a spot or anywhere in an area. Several colors: a set of colors at set spots, or that shape found anywhere. An image: a picture from the screen or a file.");
        ToolTip.SetTip(hexBox, "The color as a hex code, like #FF8800.");
        ToolTip.SetTip(tolerance, "How far each of red, green and blue may be off and still count as a match. 0 means the exact color; a little slack catches shading in games.");
        foreach (var box in new[] { atX, pointX, areaX }) ToolTip.SetTip(box, "Across the screen, in pixels from the left.");
        foreach (var box in new[] { atY, pointY, areaY }) ToolTip.SetTip(box, "Down the screen, in pixels from the top.");
        ToolTip.SetTip(areaW, "Width of the area to look in, in pixels.");
        ToolTip.SetTip(areaH, "Height of the area to look in, in pixels.");
        ToolTip.SetTip(pointHex, "The point's color as a hex code, like #FF8800.");
        ToolTip.SetTip(offsetX, "How far right of the center to click; a negative number goes left.");
        ToolTip.SetTip(offsetY, "How far up from the center to click; a negative number goes down.");

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
                Row(Label("Tolerance"), tolerance, Hint("0 = exact color; each of red, green, blue may be off by this much."))
            }
        };

        if (aim)
        {
            offsetRow.Children.AddRange([Label("X"), offsetX, offsetXUnit, Label("Y"), offsetY, offsetYUnit]);
            offsetRow.Children[3].Margin = new Thickness(10, 0, 0, 0);
            settings.Children.AddRange([
                Section("Click at"),
                spotBox,
                offsetRow,
                spotHint,
                new TextBlock
                {
                    Text = "If it finds nothing, this line is skipped. Tip: put the line inside an If or While that checks the same thing, so it only runs when the target is there.",
                    Foreground = new SolidColorBrush(Color.Parse("#FBBF24")), TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            ]);
        }
        else
        {
            settings.Children.Add(Hint("A check only says found or not found. Put it in the condition of an If or While to decide what happens."));
        }

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
        if (WindowHider.IsHiding) return;
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
    static TextBlock Hint(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };

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
        spotBox.SelectionChanged += (_, _) => UpdateVisibility();

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
        colorDropper.Click += (_, _) => StartPick(
            live: (_, hex) => { if (hex != null) hexBox.Text = hex; },
            done: (at, hex) =>
            {
                if (hex != null) hexBox.Text = hex;
                SetValue(atX, at.X);
                SetValue(atY, at.Y);
            });

        atPick.Click += (_, _) => StartPick(
            live: (at, _) => { SetValue(atX, at.X); SetValue(atY, at.Y); },
            done: (at, _) => { SetValue(atX, at.X); SetValue(atY, at.Y); });

        pointPick.Click += (_, _) => StartPick(
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

        var spot = (CheckSpot)Math.Max(0, spotBox.SelectedIndex);
        offsetRow.IsVisible = spot != CheckSpot.Center;
        string unit = spot == CheckSpot.OffsetPercent ? "%" : "px";
        offsetXUnit.Text = unit + " (+ right)";
        offsetYUnit.Text = unit + " (+ up)";
        spotHint.Text = spot switch
        {
            CheckSpot.OffsetPercent =>
                "A share of the size of what's found, from its center: X 100% is its right edge and -100% its left edge; " +
                "Y 100% is its top edge and -100% its bottom edge. 0% is the center.",
            CheckSpot.OffsetPixels => "Pixels from the center of what's found: + X goes right, + Y goes up.",
            _ => target switch
            {
                CheckTarget.Color => "The middle of the patch of that color it finds.",
                CheckTarget.Colors => "The middle of the box around all the points.",
                _ => "The middle of the image where it's found."
            }
        };
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
        spotBox.SelectedIndex = (int)step.Spot;
        SetValue(offsetX, step.OffsetX);
        SetValue(offsetY, step.OffsetY);
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
            Spot = aim ? (CheckSpot)Math.Max(0, spotBox.SelectedIndex) : CheckSpot.Center,
            OffsetX = aim ? (int)(offsetX.Value ?? 0) : 0,
            OffsetY = aim ? (int)(offsetY.Value ?? 0) : 0
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
    /// Picks a spot with the shared <see cref="ScreenPicker"/> (windows hidden, countdown by the
    /// mouse) and the color under it. <paramref name="live"/> runs as it goes, <paramref name="done"/> at the end.
    /// </summary>
    async void StartPick(Action<PixelPoint, string?> live, Action<PixelPoint, string?> done)
    {
        if (picking) return;
        picking = true;
        try
        {
            // The color comes from the picker, read while the windows were hidden. Reading it
            // here would be too late: the windows are back, and it would be their own color.
            var (at, hex) = await ScreenPicker.PickAsync(backend, p => live(p, ScreenCapture.ColorAt(p)));
            done(at, hex);
            ShowProblem(hex == null ? ScreenCaptureMissing : null);
        }
        finally
        {
            picking = false;
        }
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
                var result = ScreenCheck.Find(step, template, measure: aim);
                return (result, clock.ElapsedMilliseconds);
            });
        });
        if (found is not { } hit)
            testResult.Text = "Not found right now (looked for " + ms + " ms).";
        else if (aim)
        {
            var click = ScreenCheck.SpotIn(step, hit.Box);
            testResult.Text = "Found: " + hit.Box.Width + "×" + hit.Box.Height + " at (" + hit.Box.X + ", " + hit.Box.Y +
                              "). It would click at (" + click.X + ", " + click.Y + "). Took " + ms + " ms.";
        }
        else testResult.Text = "Found at (" + hit.At.X + ", " + hit.At.Y + ") in " + ms + " ms.";
    }

    /// <summary>
    /// Moves the app's windows off the screen while <paramref name="work"/> runs, so they're
    /// neither in the way nor in the picture. (Opacity alone leaves the title bars showing.)
    /// </summary>
    static async Task<T> WithWindowsHidden<T>(Func<Task<T>> work)
    {
        using var hidden = WindowHider.Hide();
        await Task.Delay(150);   // let the move reach the screen
        return await work();
    }
}
