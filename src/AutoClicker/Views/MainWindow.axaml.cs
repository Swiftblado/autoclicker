using System;
using System.Collections.Generic;
using System.Globalization;
using AutoClicker.Core;
using AutoClicker.Hotkeys;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AutoClicker.Views;

public partial class MainWindow : Window
{
    const int StartCountdownSeconds = 3;
    static readonly Key[] HotkeyChoices =
    [
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
        Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12
    ];

    readonly IInputBackend backend = InputBackend.Create();
    readonly IHotkeyService hotkeys = HotkeyService.Create();
    readonly Runner runner = new();
    readonly Settings settings = Settings.Load();

    readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer pickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    Key chosenKey = Key.None;
    int countdown;
    int pickTicks;
    bool wasRunning;
    bool wasAvailable = true;
    long lastCount;
    string? hotkeyError;
    string? validationMessage;

    public MainWindow()
    {
        InitializeComponent();

        ModAlt.Content = KeyMap.AltName;
        ModMeta.Content = KeyMap.MetaName;
        HotkeyBox.ItemsSource = BuildHotkeyNames();

        ModeMouse.IsCheckedChanged += OnSettingChanged;
        ModeKeyboard.IsCheckedChanged += OnSettingChanged;
        PositionFixed.IsCheckedChanged += OnSettingChanged;
        PositionCursor.IsCheckedChanged += OnSettingChanged;
        RepeatForever.IsCheckedChanged += OnSettingChanged;
        RepeatLimited.IsCheckedChanged += OnSettingChanged;
        TopMostBox.IsCheckedChanged += (_, _) => Topmost = TopMostBox.IsChecked == true;
        HotkeyBox.SelectionChanged += (_, _) => { RegisterHotkey(); UpdateControls(); };

        StartButton.Click += (_, _) => BeginCountdown();
        StopButton.Click += (_, _) => StopRunning();
        PickButton.Click += (_, _) => StartPick();

        KeyCapture.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        KeyCapture.PointerPressed += (_, _) => KeyCapture.Focus();

        uiTimer.Tick += (_, _) => RefreshState();
        countdownTimer.Tick += (_, _) => CountdownTick();
        pickTimer.Tick += (_, _) => PickTick();

        LoadSettings();
        hotkeys.Pressed += () => Dispatcher.UIThread.Post(ToggleFromHotkey);

        uiTimer.Start();
        ShowBackendWarning();
        UpdateControls();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RegisterHotkey();
        UpdateControls();
    }

    static List<string> BuildHotkeyNames()
    {
        var names = new List<string>();
        foreach (var key in HotkeyChoices) names.Add(KeyMap.Name(key));
        return names;
    }

    // ---------------------------------------------------------------- state

    bool IsBusy => runner.IsRunning || countdownTimer.IsEnabled;

    Key HotkeyKey => HotkeyChoices[Math.Clamp(HotkeyBox.SelectedIndex, 0, HotkeyChoices.Length - 1)];

    string HotkeyName => KeyMap.Name(HotkeyKey);

    void OnSettingChanged(object? sender, RoutedEventArgs e) => UpdateControls();

    void ShowBackendWarning()
    {
        wasAvailable = backend.IsAvailable;
        WarningText.Text = backend.UnavailableReason;
        WarningBar.IsVisible = !wasAvailable;
    }

    void UpdateControls()
    {
        bool busy = IsBusy;
        bool mouseMode = ModeMouse.IsChecked == true;

        ModeMouse.IsEnabled = ModeKeyboard.IsEnabled = !busy;
        MouseCard.IsEnabled = !busy && mouseMode;
        KeyboardCard.IsEnabled = !busy && !mouseMode;
        TimingCard.IsEnabled = !busy;
        OptionsCard.IsEnabled = !busy;

        bool fixedPosition = PositionFixed.IsChecked == true;
        PositionX.IsEnabled = PositionY.IsEnabled = PickButton.IsEnabled = fixedPosition;
        RepeatCount.IsEnabled = RepeatLimited.IsChecked == true;

        StartButton.IsEnabled = !busy && backend.IsAvailable;
        StopButton.IsEnabled = busy;
        StartButton.Content = hotkeyError == null ? "Start (" + HotkeyName + ")" : "Start";
        StopButton.Content = hotkeyError == null ? "Stop (" + HotkeyName + ")" : "Stop";

        RefreshStatus();
    }

    void RefreshState()
    {
        // On macOS the Accessibility permission can be granted while we're running,
        // so re-check rather than deciding once at startup.
        if (backend.IsAvailable != wasAvailable)
        {
            ShowBackendWarning();
            UpdateControls();
        }

        bool running = runner.IsRunning;
        long count = runner.Count;
        if (running != wasRunning || count != lastCount)
        {
            wasRunning = running;
            lastCount = count;
            UpdateControls();
        }
    }

    void RefreshStatus()
    {
        long count = runner.Count;
        string noun = ModeMouse.IsChecked == true
            ? (count == 1 ? "click" : "clicks")
            : (count == 1 ? "key press" : "key presses");
        string counted = count.ToString("N0", CultureInfo.CurrentCulture) + " " + noun;

        string text;
        if (validationMessage != null)
            text = validationMessage;
        else if (countdownTimer.IsEnabled)
            text = "Starting in " + countdown + "… switch to your target window.";
        else if (runner.IsRunning)
            text = "Running · " + counted + "." + (hotkeyError == null ? " Press " + HotkeyName + " to stop." : "");
        else if (count > 0)
            text = "Stopped · " + counted + ".";
        else if (hotkeyError != null)
            text = HotkeyName + " isn't available as a start/stop key (" + hotkeyError +
                   "). Use the buttons below or pick another key.";
        else
            text = "Ready. Press " + HotkeyName + " from any window to start or stop.";

        StatusText.Text = text;
    }

    // ---------------------------------------------------------------- start / stop

    void BeginCountdown()
    {
        if (BuildAction() == null) { UpdateControls(); return; }
        // Started from the button, the first clicks or keys would land on this window,
        // so give the user a moment to switch to their target.
        countdown = StartCountdownSeconds;
        countdownTimer.Start();
        UpdateControls();
    }

    void CountdownTick()
    {
        countdown--;
        if (countdown <= 0)
        {
            countdownTimer.Stop();
            StartNow();
        }
        UpdateControls();
    }

    void ToggleFromHotkey()
    {
        if (IsBusy) StopRunning();
        else StartNow();
    }

    void StartNow()
    {
        var action = BuildAction();
        if (action == null) { UpdateControls(); return; }

        double interval = (double)(Dec(Hours) * 3600000m + Dec(Minutes) * 60000m +
                                   Dec(Seconds) * 1000m + Dec(Millis));
        long repeat = RepeatLimited.IsChecked == true ? (long)Dec(RepeatCount) : 0;
        SaveSettings();
        runner.Start(action, interval, (int)Dec(Jitter), repeat);
        UpdateControls();
    }

    void StopRunning()
    {
        countdownTimer.Stop();
        runner.Stop();
        UpdateControls();
    }

    IInputAction? BuildAction()
    {
        validationMessage = null;

        if (!backend.IsAvailable)
        {
            validationMessage = backend.UnavailableReason;
            return null;
        }

        if (Dec(Hours) + Dec(Minutes) + Dec(Seconds) + Dec(Millis) <= 0)
        {
            validationMessage = "Set an interval of at least 1 ms.";
            return null;
        }

        if (ModeMouse.IsChecked == true)
        {
            return new MouseClickAction(
                backend,
                (ClickButton)Math.Clamp(ButtonBox.SelectedIndex, 0, 2),
                ClickTypeBox.SelectedIndex == 1 ? 2 : 1,
                PositionFixed.IsChecked == true,
                new PixelPoint((int)Dec(PositionX), (int)Dec(PositionY)));
        }

        if (chosenKey == Key.None)
        {
            validationMessage = "Click the Key box and press the key you want repeated.";
            return null;
        }
        if (chosenKey == HotkeyKey && hotkeyError == null)
        {
            validationMessage = "The key to press can't also be the start/stop key (" + HotkeyName + ").";
            return null;
        }

        var modifiers = Modifiers.None;
        if (ModCtrl.IsChecked == true) modifiers |= Modifiers.Control;
        if (ModShift.IsChecked == true) modifiers |= Modifiers.Shift;
        if (ModAlt.IsChecked == true) modifiers |= Modifiers.Alt;
        if (ModMeta.IsChecked == true) modifiers |= Modifiers.Meta;

        return new KeyPressAction(backend, chosenKey, modifiers, (int)Dec(HoldMs));
    }

    // ---------------------------------------------------------------- pick location

    void StartPick()
    {
        pickTicks = StartCountdownSeconds * 1000 / (int)pickTimer.Interval.TotalMilliseconds;
        pickTimer.Start();
        PickTick();
    }

    void PickTick()
    {
        var position = backend.GetCursorPosition();
        PositionX.Value = Math.Clamp(position.X, (int)PositionX.Minimum, (int)PositionX.Maximum);
        PositionY.Value = Math.Clamp(position.Y, (int)PositionY.Minimum, (int)PositionY.Maximum);

        pickTicks--;
        if (pickTicks <= 0)
        {
            pickTimer.Stop();
            PickButton.Content = "Pick";
            return;
        }
        int secondsLeft = (int)Math.Ceiling(pickTicks * pickTimer.Interval.TotalMilliseconds / 1000);
        PickButton.Content = secondsLeft + "…";
    }

    // ---------------------------------------------------------------- key capture

    void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;   // wait for the real key; modifiers are read off it below

        if (!KeyMap.IsSupported(e.Key))
        {
            validationMessage = "That key isn't supported yet. Try a letter, number, arrow or function key.";
            UpdateControls();
            return;
        }

        chosenKey = e.Key;
        ModCtrl.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        ModShift.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ModAlt.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        ModMeta.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        validationMessage = null;
        UpdateKeyCaptureText();
        UpdateControls();
    }

    void UpdateKeyCaptureText()
    {
        bool chosen = chosenKey != Key.None;
        KeyCaptureText.Text = chosen ? KeyMap.Name(chosenKey) : "Click here, then press a key";
        KeyCaptureText.Opacity = chosen ? 1 : 0.6;
    }

    // ---------------------------------------------------------------- hotkey

    void RegisterHotkey()
    {
        hotkeys.Unregister();
        hotkeyError = hotkeys.TryRegister(HotkeyKey, out var error) ? null : error ?? "not available";
    }

    // ---------------------------------------------------------------- settings

    static decimal Dec(NumericUpDown control) => control.Value ?? 0;

    static void SetValue(NumericUpDown control, decimal value) =>
        control.Value = Math.Clamp(value, control.Minimum, control.Maximum);

    static void SetIndex(ComboBox combo, int index, int count)
    {
        if (index >= 0 && index < count) combo.SelectedIndex = index;
    }

    void LoadSettings()
    {
        ModeKeyboard.IsChecked = settings.GetBool("KeyboardMode", false);
        ModeMouse.IsChecked = ModeKeyboard.IsChecked != true;
        SetIndex(ButtonBox, settings.GetInt("MouseButton", 0), 3);
        SetIndex(ClickTypeBox, settings.GetInt("ClickType", 0), 2);
        PositionFixed.IsChecked = settings.GetBool("FixedPosition", false);
        PositionCursor.IsChecked = PositionFixed.IsChecked != true;
        SetValue(PositionX, settings.GetDecimal("X", 0));
        SetValue(PositionY, settings.GetDecimal("Y", 0));

        chosenKey = Enum.TryParse<Key>(settings.GetString("Key", "None"), out var key) ? key : Key.None;
        if (!KeyMap.IsSupported(chosenKey)) chosenKey = Key.None;
        UpdateKeyCaptureText();
        ModCtrl.IsChecked = settings.GetBool("Ctrl", false);
        ModShift.IsChecked = settings.GetBool("Shift", false);
        ModAlt.IsChecked = settings.GetBool("Alt", false);
        ModMeta.IsChecked = settings.GetBool("Meta", false);
        SetValue(HoldMs, settings.GetDecimal("HoldMs", 30));

        SetValue(Hours, settings.GetDecimal("Hours", 0));
        SetValue(Minutes, settings.GetDecimal("Minutes", 0));
        SetValue(Seconds, settings.GetDecimal("Seconds", 0));
        SetValue(Millis, settings.GetDecimal("Milliseconds", 100));
        SetValue(Jitter, settings.GetDecimal("JitterMs", 0));
        RepeatLimited.IsChecked = settings.GetBool("RepeatLimited", false);
        RepeatForever.IsChecked = RepeatLimited.IsChecked != true;
        SetValue(RepeatCount, settings.GetDecimal("RepeatCount", 10));

        SetIndex(HotkeyBox, settings.GetInt("Hotkey", 5), HotkeyChoices.Length);
        TopMostBox.IsChecked = settings.GetBool("TopMost", false);
        Topmost = TopMostBox.IsChecked == true;
    }

    void SaveSettings()
    {
        settings.Set("KeyboardMode", ModeKeyboard.IsChecked == true);
        settings.Set("MouseButton", ButtonBox.SelectedIndex);
        settings.Set("ClickType", ClickTypeBox.SelectedIndex);
        settings.Set("FixedPosition", PositionFixed.IsChecked == true);
        settings.Set("X", Dec(PositionX));
        settings.Set("Y", Dec(PositionY));
        settings.Set("Key", chosenKey);
        settings.Set("Ctrl", ModCtrl.IsChecked == true);
        settings.Set("Shift", ModShift.IsChecked == true);
        settings.Set("Alt", ModAlt.IsChecked == true);
        settings.Set("Meta", ModMeta.IsChecked == true);
        settings.Set("HoldMs", Dec(HoldMs));
        settings.Set("Hours", Dec(Hours));
        settings.Set("Minutes", Dec(Minutes));
        settings.Set("Seconds", Dec(Seconds));
        settings.Set("Milliseconds", Dec(Millis));
        settings.Set("JitterMs", Dec(Jitter));
        settings.Set("RepeatLimited", RepeatLimited.IsChecked == true);
        settings.Set("RepeatCount", Dec(RepeatCount));
        settings.Set("Hotkey", HotkeyBox.SelectedIndex);
        settings.Set("TopMost", TopMostBox.IsChecked == true);
        settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        countdownTimer.Stop();
        pickTimer.Stop();
        uiTimer.Stop();
        runner.Stop();
        SaveSettings();
        hotkeys.Dispose();
        backend.Dispose();
        base.OnClosed(e);
    }
}
