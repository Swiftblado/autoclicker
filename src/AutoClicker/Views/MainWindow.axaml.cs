using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using AutoClicker.Core;
using AutoClicker.Hotkeys;
using AutoClicker.Input;
using AutoClicker.Notifications;
using AutoClicker.Recording;
using AutoClicker.Updates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeyBinding = AutoClicker.Core.KeyBinding;

namespace AutoClicker.Views;

public partial class MainWindow : Window
{
    /// <summary>A saved binding with what it needs while the app is open: its hotkey and its run.</summary>
    sealed class BindingSlot(KeyBinding binding, IHotkeyService hotkey, RunSession session)
    {
        public KeyBinding Binding { get; set; } = binding;
        public IHotkeyService Hotkey { get; set; } = hotkey;
        public RunSession Session { get; } = session;
        /// <summary>Why the key couldn't be registered, or null when it works.</summary>
        public string? Error { get; set; }
        public bool WasRunning { get; set; }
    }

    const int StartCountdownSeconds = 3;

    /// <summary>
    /// A binding with modifiers starts this much later, so the Ctrl or Shift still held from pressing
    /// it doesn't turn its first click into a Ctrl-click.
    /// </summary>
    static readonly TimeSpan ModifierReleaseTime = TimeSpan.FromMilliseconds(300);
    const int RecordCountdownSeconds = 2;
    const string CurrentMacroFile = "current.json";

    /// <summary>How long the Windows tray icon lingers after the "back to full speed" notification.</summary>
    static readonly TimeSpan TrayLingerTime = TimeSpan.FromSeconds(10);

    static readonly Key[] HotkeyChoices =
    [
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
        Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12
    ];

    readonly IInputBackend backend = InputBackend.Create();
    readonly IHotkeyService hotkeys = HotkeyService.Create();
    readonly IInputRecorder recorder = InputRecorder.Create();
    readonly Settings settings = Settings.Load();
    /// <summary>The run of the setup shown in the window, started with Start or the start/stop key.</summary>
    readonly RunSession mainRun;
    readonly List<BindingSlot> bindings = [];
    readonly ObservableCollection<string> bindingRows = [];
    readonly ISystemNotifier notifier;

    readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer pickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer recordCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer trayHideTimer = new() { Interval = TrayLingerTime };

    readonly Macro macro = new();
    readonly ObservableCollection<string> stepRows = [];
    readonly List<RecordedEvent> recordedEvents = [];
    readonly object recordLock = new();

    UpdateInfo? availableUpdate;
    bool checkingForUpdate;
    double? updateProgress;   // null when no install is in progress
    Key chosenKey = Key.None;
    Key stepKey = Key.None;
    Key bindingKey = Key.None;
    Modifiers bindingModifiers = Modifiers.None;
    int countdown;
    int recordCountdown;
    int pickTicks;
    NumericUpDown? pickTargetX, pickTargetY;
    Button? pickTargetButton;
    bool wasRunning;
    bool wasAvailable = true;
    long lastCount;
    string? hotkeyError;
    string? validationMessage;
    string? transientMessage;

    public MainWindow()
    {
        InitializeComponent();

        mainRun = NewSession();
        notifier = SystemNotifier.Create(() => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        ModAlt.Content = StepModAlt.Content = KeyMap.AltName;
        ModMeta.Content = StepModMeta.Content = KeyMap.MetaName;
        HotkeyBox.ItemsSource = HotkeyChoices.Select(KeyMap.Name).ToList();
        StepList.ItemsSource = stepRows;
        var holdChoices = ClickHold.ChoicesMs.Select(ms => ms + " ms").Append("Custom…").ToList();
        HoldBox.ItemsSource = holdChoices;
        StepHoldBox.ItemsSource = holdChoices;
        HoldBox.SelectedIndex = StepHoldBox.SelectedIndex = Array.IndexOf(ClickHold.ChoicesMs, ClickHold.DefaultMs);
        HoldCustom.Maximum = StepHoldCustom.Maximum = ClickHold.MaxCustomMs;
        HoldCustom.Value = StepHoldCustom.Value = ClickHold.DefaultMs;
        foreach (var help in new[] { HoldHelp, StepHoldHelp })
        {
            ToolTip.SetTip(help, "What can be clicked this fast?");
            help.Flyout = new Flyout
            {
                Content = new TextBlock { Text = ClickHold.HelpText, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320 }
            };
        }

        ModeBox.SelectionChanged += (_, _) => { UpdateControls(); };
        StepTypeBox.SelectionChanged += (_, _) => UpdateControls();
        StepList.SelectionChanged += (_, _) => UpdateControls();
        PressBox.SelectionChanged += (_, _) => UpdateControls();
        StepPressBox.SelectionChanged += (_, _) => UpdateControls();
        HoldBox.SelectionChanged += (_, _) => UpdateControls();
        StepHoldBox.SelectionChanged += (_, _) => UpdateControls();
        HoldCustom.ValueChanged += (_, _) => UpdateControls();
        StepHoldCustom.ValueChanged += (_, _) => UpdateControls();
        PositionFixed.IsCheckedChanged += OnSettingChanged;
        PositionCursor.IsCheckedChanged += OnSettingChanged;
        RepeatForever.IsCheckedChanged += OnSettingChanged;
        RepeatLimited.IsCheckedChanged += OnSettingChanged;
        TopMostBox.IsCheckedChanged += (_, _) => Topmost = TopMostBox.IsChecked == true;
        LagCheckBox.IsCheckedChanged += OnSettingChanged;
        HotkeyBox.SelectionChanged += (_, _) => { RegisterHotkey(); UpdateControls(); };

        StartButton.Click += (_, _) => BeginCountdown();
        StopButton.Click += (_, _) => StopEverything();
        PickButton.Click += (_, _) => StartPick(PositionX, PositionY, PickButton);
        DragFromPick.Click += (_, _) => StartPick(DragFromX, DragFromY, DragFromPick);
        DragToPick.Click += (_, _) => StartPick(DragToX, DragToY, DragToPick);
        StepClickPick.Click += (_, _) => StartPick(StepClickX, StepClickY, StepClickPick);
        StepDragFromPick.Click += (_, _) => StartPick(StepDragFromX, StepDragFromY, StepDragFromPick);
        StepDragToPick.Click += (_, _) => StartPick(StepDragToX, StepDragToY, StepDragToPick);

        RecordButton.Click += (_, _) => ToggleRecording();
        StepAddButton.Click += (_, _) => AddStep(replaceSelected: false);
        StepReplaceButton.Click += (_, _) => AddStep(replaceSelected: true);
        StepUpButton.Click += (_, _) => MoveStep(-1);
        StepDownButton.Click += (_, _) => MoveStep(1);
        StepDeleteButton.Click += (_, _) => DeleteStep();
        StepClearButton.Click += (_, _) => ClearSteps();
        MacroOpenButton.Click += async (_, _) => await OpenMacroAsync();
        MacroSaveButton.Click += async (_, _) => await SaveMacroAsync();
        UpdateButton.Click += async (_, _) => await OnUpdateClickedAsync();
        WhatsNewButton.Click += async (_, _) => await ChangelogWindow.ShowAsync(this);
        BindingList.ItemsSource = bindingRows;
        BindingList.SelectionChanged += (_, _) => OnBindingSelected();
        BindingAddButton.Click += (_, _) => AddBinding(replaceSelected: false);
        BindingReplaceButton.Click += (_, _) => AddBinding(replaceSelected: true);
        BindingLoadButton.Click += (_, _) => LoadBindingIntoWindow();
        BindingRemoveButton.Click += (_, _) => RemoveBinding();
        BindingKeyCapture.AddHandler(KeyDownEvent, OnBindingCaptureKeyDown, RoutingStrategies.Tunnel);
        BindingKeyCapture.PointerPressed += (_, _) => BindingKeyCapture.Focus();
        OpenAccessibilityButton.Click += (_, _) =>
            Updater.OpenInBrowser("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        VersionText.Text = AppVersion.Display;

        KeyCapture.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        KeyCapture.PointerPressed += (_, _) => KeyCapture.Focus();
        StepKeyCapture.AddHandler(KeyDownEvent, OnStepCaptureKeyDown, RoutingStrategies.Tunnel);
        StepKeyCapture.PointerPressed += (_, _) => StepKeyCapture.Focus();

        uiTimer.Tick += (_, _) => RefreshState();
        countdownTimer.Tick += (_, _) => CountdownTick();
        pickTimer.Tick += (_, _) => PickTick();
        recordCountdownTimer.Tick += (_, _) => RecordCountdownTick();
        recorder.Recorded += OnRecorded;
        trayHideTimer.Tick += (_, _) => { trayHideTimer.Stop(); notifier.Hide(); };

        LoadSettings();
        LoadCurrentMacro();
        LoadBindings();
        hotkeys.Pressed += () => Dispatcher.UIThread.Post(ToggleFromHotkey);

        uiTimer.Start();
        ShowBackendWarning();
        UpdateControls();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RegisterHotkey();
        RegisterBindings();
        UpdateControls();
        if (AppVersion.IsUpdatable) _ = CheckForUpdateAsync(quiet: true);
        // Once after an in-app update, show what it brought.
        if (Program.JustUpdated) Dispatcher.UIThread.Post(async () => await ChangelogWindow.ShowAsync(this));
    }

    RunSession NewSession()
    {
        var session = new RunSession(backend, TargetProbe.Create(), ScreenProbe.Create, backend.GetCursorPosition);
        session.LagChanged += (s, change) => Dispatcher.UIThread.Post(() => OnLagChanged(s, change));
        return session;
    }

    // ---------------------------------------------------------------- state

    AutomationMode Mode => (AutomationMode)Math.Clamp(ModeBox.SelectedIndex, 0, 3);

    /// <summary>The window's own setup is counting down or running (key bindings aside).</summary>
    bool IsBusy => mainRun.IsRunning || countdownTimer.IsEnabled;

    int BindingsRunning => bindings.Count(slot => slot.Session.IsRunning);

    bool IsRecording => recorder.IsRecording || recordCountdownTimer.IsEnabled;

    Key HotkeyKey => HotkeyChoices[Math.Clamp(HotkeyBox.SelectedIndex, 0, HotkeyChoices.Length - 1)];

    string HotkeyName => KeyMap.Name(HotkeyKey);

    void OnSettingChanged(object? sender, RoutedEventArgs e) => UpdateControls();

    void ShowBackendWarning()
    {
        wasAvailable = backend.IsAvailable;
        WarningText.Text = Program.JustUpdated && OperatingSystem.IsMacOS()
            ? "Auto Clicker was just updated, and macOS ties the Accessibility permission to the exact copy " +
              "of the app, so it needs turning on again. In Privacy & Security > Accessibility, switch Auto Clicker " +
              "off and back on (if that doesn't work, select it, remove it with −, then add it again), then quit and reopen the app."
            : backend.UnavailableReason;
        WarningBar.IsVisible = !wasAvailable;
        OpenAccessibilityButton.IsVisible = !wasAvailable && OperatingSystem.IsMacOS();
    }

    void UpdateControls()
    {
        bool busy = IsBusy || IsRecording;
        var mode = Mode;

        MouseSection.IsVisible = mode == AutomationMode.Clicks;
        DragSection.IsVisible = mode == AutomationMode.Drags;
        KeyboardSection.IsVisible = mode == AutomationMode.Keys;
        MacroSection.IsVisible = mode == AutomationMode.Macro;

        ModeBox.IsEnabled = !busy;
        MouseSection.IsEnabled = DragSection.IsEnabled = KeyboardSection.IsEnabled = !busy;
        TimingCard.IsEnabled = !busy;
        OptionsCard.IsEnabled = !busy;

        PositionX.IsEnabled = PositionY.IsEnabled = PickButton.IsEnabled = PositionFixed.IsChecked == true;
        ShowHold(PressBox, HoldLabel, HoldBox, HoldCustomRow, HoldCustom, HoldHalfNote);
        ShowHold(StepPressBox, StepHoldLabel, StepHoldBox, StepHoldCustomRow, StepHoldCustom, StepHoldHalfNote);
        RepeatCount.IsEnabled = RepeatLimited.IsChecked == true;
        RepeatUnit.Text = mode == AutomationMode.Macro ? "passes" : "times";
        TimingTitle.Text = mode == AutomationMode.Macro ? "Repeat the macro" : "Timing";

        // Macro editing stays available while recording only for the Record button itself.
        bool canEditMacro = !busy;
        int selected = StepList.SelectedIndex;
        StepTypeBox.IsEnabled = StepAddButton.IsEnabled = canEditMacro;
        StepReplaceButton.IsEnabled = canEditMacro && selected >= 0;
        StepUpButton.IsEnabled = canEditMacro && selected > 0;
        StepDownButton.IsEnabled = canEditMacro && selected >= 0 && selected < macro.Steps.Count - 1;
        StepDeleteButton.IsEnabled = canEditMacro && selected >= 0;
        StepClearButton.IsEnabled = canEditMacro && macro.Steps.Count > 0;
        MacroOpenButton.IsEnabled = MacroSaveButton.IsEnabled = canEditMacro;
        StepClickPanel.IsEnabled = StepDragPanel.IsEnabled = StepKeyPanel.IsEnabled =
            StepWaitPanel.IsEnabled = canEditMacro;
        RecordButton.IsEnabled = !IsBusy;
        RecordButton.Content = recorder.IsRecording || recordCountdownTimer.IsEnabled ? "Stop recording" : "Record";

        StepClickPanel.IsVisible = StepTypeBox.SelectedIndex == 0;
        StepDragPanel.IsVisible = StepTypeBox.SelectedIndex == 1;
        StepKeyPanel.IsVisible = StepTypeBox.SelectedIndex == 2;
        StepWaitPanel.IsVisible = StepTypeBox.SelectedIndex == 3;

        UpdateButton.IsEnabled = AppVersion.IsUpdatable && !checkingForUpdate && updateProgress == null;
        UpdateButton.Content = updateProgress is { } fraction ? "Updating… " + (int)(fraction * 100) + "%"
            : checkingForUpdate ? "Checking…"
            : availableUpdate != null ? "Update to v" + AppVersion.Format(availableUpdate.Version)
            : "Check for updates";

        LagNotifyBox.IsEnabled = LagCheckBox.IsChecked == true;

        int binding = BindingList.SelectedIndex;
        bool bindingSelected = binding >= 0 && binding < bindings.Count;
        BindingsCard.IsEnabled = !IsRecording;
        BindingsEmpty.IsVisible = bindings.Count == 0;
        BindingList.IsVisible = bindings.Count > 0;
        BindingReplaceButton.IsEnabled = BindingLoadButton.IsEnabled = BindingRemoveButton.IsEnabled = bindingSelected;
        BindingLoadButton.IsEnabled = bindingSelected && !busy;

        StartButton.IsEnabled = !busy && backend.IsAvailable;
        StopButton.IsEnabled = IsBusy || BindingsRunning > 0;
        StartButton.Content = hotkeyError == null ? "Start (" + HotkeyName + ")" : "Start";
        StopButton.Content = hotkeyError == null ? "Stop (" + HotkeyName + ")" : "Stop";

        UpdateMacroSummary();
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

        bool bindingsChanged = false;
        foreach (var slot in bindings)
        {
            bool slotRunning = slot.Session.IsRunning;
            if (slotRunning == slot.WasRunning) continue;
            slot.WasRunning = slotRunning;
            if (!slotRunning) EndSession(slot.Session);   // finished its repeat count
            bindingsChanged = true;
        }
        if (bindingsChanged) RebuildBindingRows();

        bool running = mainRun.IsRunning;
        long count = mainRun.Count;
        if (running != wasRunning || count != lastCount || bindingsChanged)
        {
            if (running != wasRunning && !running) EndSession(mainRun);
            wasRunning = running;
            lastCount = count;
            UpdateControls();
        }
        else if (running && mainRun.Macro != null)
        {
            RefreshStatus();   // step counter ticks along while the pass count holds still
        }
    }

    void RefreshStatus()
    {
        if (transientMessage != null) { StatusText.Text = transientMessage; return; }
        if (validationMessage != null) { StatusText.Text = validationMessage; return; }

        long count = mainRun.Count;
        string text;

        if (recordCountdownTimer.IsEnabled)
            text = "Recording starts in " + recordCountdown + "… get ready.";
        else if (recorder.IsRecording)
            text = "Recording your clicks and keys. Press " + HotkeyName + " to finish.";
        else if (countdownTimer.IsEnabled)
            text = "Starting in " + countdown + "… switch to your target window.";
        else if (mainRun.IsRunning)
            text = "Running · " + Counted(mainRun, count) + "." + StepProgress() + LagNote(mainRun) +
                   (hotkeyError == null ? " Press " + HotkeyName + " to stop." : "");
        else if (count > 0)
            text = "Stopped · " + Counted(mainRun, count) + ".";
        else if (hotkeyError != null)
            text = HotkeyName + " isn't available as a start/stop key (" + hotkeyError +
                   "). Use the buttons below or pick another key.";
        else
            text = "Ready. Press " + HotkeyName + " from any window to start or stop.";

        int bindingsRunning = BindingsRunning;
        if (bindingsRunning > 0)
            text += " " + bindingsRunning + (bindingsRunning == 1 ? " key binding is" : " key bindings are") +
                    " running; Stop stops everything.";

        StatusText.Text = text;
    }

    static string Counted(RunSession session, long count) =>
        count.ToString("N0", CultureInfo.CurrentCulture) + " " + (session.Setup?.CountNoun(count) ?? "");

    static string LagNote(RunSession session)
    {
        double multiplier = session.Multiplier;
        if (!session.Slowed || multiplier <= 1) return "";
        return " Slowed " + multiplier.ToString("N0", CultureInfo.CurrentCulture) + "× for lag.";
    }

    string StepProgress()
    {
        var running = mainRun.Macro;
        if (running == null) return "";
        int step = running.CurrentStepIndex;
        return step >= 0 ? " Step " + (step + 1) + " of " + (mainRun.Setup?.Steps.Count ?? 0) + "." : "";
    }

    // ---------------------------------------------------------------- start / stop

    void BeginCountdown()
    {
        var setup = BuildSetup();
        if (setup == null) { UpdateControls(); return; }
        // Use the countdown to learn what the target looks like when it's keeping up.
        if (setup.LagCheck) mainRun.WatchAhead();
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
        if (IsRecording) StopRecording();
        else if (IsBusy) StopMainRun();
        else StartNow();
    }

    void StartNow()
    {
        var setup = BuildSetup();
        if (setup == null) { mainRun.Stop(); UpdateControls(); return; }
        SaveSettings();
        BeforeRunStarts();
        mainRun.Start(setup);
        UpdateControls();
    }

    /// <summary>The window's own run stops; key bindings keep going.</summary>
    void StopMainRun()
    {
        countdownTimer.Stop();
        EndSession(mainRun);
        UpdateControls();
    }

    /// <summary>The Stop button: the window's run and every key binding.</summary>
    void StopEverything()
    {
        countdownTimer.Stop();
        EndSession(mainRun);
        foreach (var slot in bindings) EndSession(slot.Session);
        RebuildBindingRows();
        UpdateControls();
    }

    void BeforeRunStarts()
    {
        // A notification left over from an earlier run is out of date now.
        if (!AllSessions().Any(s => s.IsRunning && s.Slowed))
        {
            trayHideTimer.Stop();
            notifier.Hide();
        }
    }

    /// <summary>Stops a run (or tidies up after one that finished its repeat count).</summary>
    void EndSession(RunSession session)
    {
        session.Stop();
        if (!session.Slowed) return;
        session.Slowed = false;
        // Nothing to recover from now; take the tray icon away rather than leave it behind.
        if (!AllSessions().Any(s => s.Slowed))
        {
            trayHideTimer.Stop();
            notifier.Hide();
        }
    }

    IEnumerable<RunSession> AllSessions() => bindings.Select(slot => slot.Session).Prepend(mainRun);

    void OnLagChanged(RunSession session, LagChange change)
    {
        if (!session.IsRunning) return;   // posted just before the run ended

        // Name the binding, so it's clear which run slowed down when several are going.
        var slot = bindings.FirstOrDefault(s => s.Session == session);
        string which = slot == null ? "" : " (" + BindingLabel(slot.Binding) + ")";
        bool notify = LagNotifyBox.IsChecked == true;

        switch (change)
        {
            case LagChange.Slowed:
                session.Slowed = true;
                if (!notify) break;
                trayHideTimer.Stop();
                notifier.Show("Auto Clicker slowed down" + which,
                    "Lag detected: " + session.LagReason + ". Running at half speed, and slower if it continues, until it clears.",
                    NotificationKind.Warning);
                break;

            case LagChange.Recovered:
                session.Slowed = false;
                if (!notify) break;
                notifier.Show("Auto Clicker back to full speed" + which, "The lag has cleared.", NotificationKind.Info);
                trayHideTimer.Start();
                break;
        }
        RefreshStatus();
    }

    /// <summary>The window's setup, checked and ready to run; null (with a message) when it can't run.</summary>
    RunSetup? BuildSetup()
    {
        validationMessage = null;
        transientMessage = null;

        if (!backend.IsAvailable)
        {
            validationMessage = backend.UnavailableReason;
            return null;
        }

        var setup = CaptureSetup();
        validationMessage = setup.Problem() ?? KeyClash(setup, null);
        return validationMessage == null ? setup : null;
    }

    /// <summary>
    /// A key-press setup can't press a key that starts or stops something, or each press would
    /// toggle a run. <paramref name="own"/> is the binding the setup is for, if any.
    /// </summary>
    string? KeyClash(RunSetup setup, KeyBinding? own)
    {
        if (setup.Mode != AutomationMode.Keys) return null;
        if (setup.Modifiers == Modifiers.None && setup.Key == HotkeyKey && hotkeyError == null)
            return "The key to press can't also be the start/stop key (" + HotkeyName + ").";
        if (own != null && own.SameKeyAs(setup.Key, setup.Modifiers))
            return "The key to press can't also be the binding's own key (" + own.KeyName + ").";
        var other = bindings.FirstOrDefault(slot => slot.Binding != own && slot.Binding.SameKeyAs(setup.Key, setup.Modifiers));
        return other == null ? null
            : "The key to press can't be " + other.Binding.KeyName + ", which starts the key binding " + BindingLabel(other.Binding) + ".";
    }

    /// <summary>Everything set up in the window, as one setup.</summary>
    RunSetup CaptureSetup() => new()
    {
        Mode = Mode,
        Button = (ClickButton)Math.Clamp(ButtonBox.SelectedIndex, 0, 2),
        ClickCount = ClickTypeBox.SelectedIndex == 1 ? 2 : 1,
        HoldMs = ReadHoldMs(PressBox, HoldBox, HoldCustom),
        FixedPosition = PositionFixed.IsChecked == true,
        X = (int)Dec(PositionX),
        Y = (int)Dec(PositionY),
        DragButton = (ClickButton)Math.Clamp(DragButtonBox.SelectedIndex, 0, 2),
        DragFromX = (int)Dec(DragFromX),
        DragFromY = (int)Dec(DragFromY),
        DragToX = (int)Dec(DragToX),
        DragToY = (int)Dec(DragToY),
        DragDurationMs = (int)Dec(DragDuration),
        Key = chosenKey,
        Modifiers = ReadModifiers(ModCtrl, ModShift, ModAlt, ModMeta),
        KeyHoldMs = (int)Dec(HoldMs),
        Steps = macro.Clone().Steps,
        IntervalMs = (double)(Dec(Hours) * 3600000m + Dec(Minutes) * 60000m + Dec(Seconds) * 1000m + Dec(Millis)),
        JitterMs = (int)Dec(Jitter),
        Repeat = RepeatLimited.IsChecked == true ? (long)Dec(RepeatCount) : 0,
        LagCheck = LagCheckBox.IsChecked == true
    };

    /// <summary>Puts a saved setup back into the window's controls, to look at or change it.</summary>
    void ShowSetup(RunSetup setup)
    {
        ModeBox.SelectedIndex = (int)setup.Mode;
        ButtonBox.SelectedIndex = (int)setup.Button;
        ClickTypeBox.SelectedIndex = setup.ClickCount > 1 ? 1 : 0;
        SetHold(PressBox, HoldBox, HoldCustom, setup.HoldMs);
        PositionFixed.IsChecked = setup.FixedPosition;
        PositionCursor.IsChecked = !setup.FixedPosition;
        SetValue(PositionX, setup.X);
        SetValue(PositionY, setup.Y);
        DragButtonBox.SelectedIndex = (int)setup.DragButton;
        SetValue(DragFromX, setup.DragFromX);
        SetValue(DragFromY, setup.DragFromY);
        SetValue(DragToX, setup.DragToX);
        SetValue(DragToY, setup.DragToY);
        SetValue(DragDuration, setup.DragDurationMs);
        chosenKey = KeyMap.IsSupported(setup.Key) ? setup.Key : Key.None;
        ModCtrl.IsChecked = setup.Modifiers.HasFlag(Modifiers.Control);
        ModShift.IsChecked = setup.Modifiers.HasFlag(Modifiers.Shift);
        ModAlt.IsChecked = setup.Modifiers.HasFlag(Modifiers.Alt);
        ModMeta.IsChecked = setup.Modifiers.HasFlag(Modifiers.Meta);
        SetValue(HoldMs, setup.KeyHoldMs);
        UpdateKeyCaptureText();

        if (setup.Mode == AutomationMode.Macro)
        {
            macro.Steps.Clear();
            macro.Steps.AddRange(setup.Steps.Select(step => step.Clone()));
            RebuildStepRows();
            SaveCurrentMacro();
        }

        long ms = (long)Math.Round(setup.IntervalMs);
        SetValue(Hours, ms / 3600000);
        SetValue(Minutes, ms / 60000 % 60);
        SetValue(Seconds, ms / 1000 % 60);
        SetValue(Millis, ms % 1000);
        SetValue(Jitter, setup.JitterMs);
        RepeatLimited.IsChecked = setup.Repeat > 0;
        RepeatForever.IsChecked = setup.Repeat <= 0;
        if (setup.Repeat > 0) SetValue(RepeatCount, setup.Repeat);
        LagCheckBox.IsChecked = setup.LagCheck;
    }

    /// <summary>The reverse of <see cref="ReadHoldMs"/>: picks the preset that matches, or "Custom…".</summary>
    static void SetHold(ComboBox press, ComboBox hold, NumericUpDown custom, int holdMs)
    {
        press.SelectedIndex = holdMs == ClickHold.HalfClickMs ? 1 : 0;
        if (holdMs == ClickHold.HalfClickMs) return;
        int preset = Array.IndexOf(ClickHold.ChoicesMs, holdMs);
        hold.SelectedIndex = preset >= 0 ? preset : ClickHold.ChoicesMs.Length;
        if (preset < 0) SetValue(custom, holdMs);
    }

    /// <summary>The hold dropdown's last entry, "Custom…", which takes the hold from a number box.</summary>
    static bool IsCustomHold(ComboBox hold) => hold.SelectedIndex == ClickHold.ChoicesMs.Length;

    /// <summary>The hold choices show only for a full click; the number box only for "Custom…".</summary>
    static void ShowHold(ComboBox press, TextBlock label, ComboBox hold, Control customRow, NumericUpDown custom, TextBlock halfNote)
    {
        bool full = press.SelectedIndex == 0;
        label.IsVisible = hold.IsVisible = full;
        customRow.IsVisible = full && IsCustomHold(hold);
        halfNote.IsVisible = ClickHold.IsHalf((int)Dec(custom));
    }

    /// <summary>Hold time picked in a Full/Half + hold pair of boxes; 0 for a half click.</summary>
    static int ReadHoldMs(ComboBox press, ComboBox hold, NumericUpDown custom) =>
        press.SelectedIndex == 1 ? ClickHold.HalfClickMs
        : IsCustomHold(hold) ? (int)Math.Clamp(Dec(custom), 0, ClickHold.MaxCustomMs)
        : ClickHold.ChoicesMs[Math.Clamp(hold.SelectedIndex, 0, ClickHold.ChoicesMs.Length - 1)];

    static Modifiers ReadModifiers(CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox meta)
    {
        var modifiers = Modifiers.None;
        if (ctrl.IsChecked == true) modifiers |= Modifiers.Control;
        if (shift.IsChecked == true) modifiers |= Modifiers.Shift;
        if (alt.IsChecked == true) modifiers |= Modifiers.Alt;
        if (meta.IsChecked == true) modifiers |= Modifiers.Meta;
        return modifiers;
    }

    // ---------------------------------------------------------------- recording

    void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else BeginRecordCountdown();
    }

    void BeginRecordCountdown()
    {
        lock (recordLock) recordedEvents.Clear();
        recordCountdown = RecordCountdownSeconds;
        recordCountdownTimer.Start();
        transientMessage = null;
        UpdateControls();
    }

    void RecordCountdownTick()
    {
        recordCountdown--;
        if (recordCountdown <= 0)
        {
            recordCountdownTimer.Stop();
            if (!recorder.TryStart(out var error))
                transientMessage = error ?? "Recording isn't available on this system.";
        }
        UpdateControls();
    }

    void StopRecording()
    {
        recordCountdownTimer.Stop();
        recorder.Stop();

        List<RecordedEvent> captured;
        lock (recordLock)
        {
            captured = new List<RecordedEvent>(recordedEvents);
            recordedEvents.Clear();
        }

        // The hotkey that ends recording shouldn't become part of the macro.
        var steps = RecordingCompressor.ToSteps(captured, HotkeyKey);
        if (steps.Count == 0)
        {
            transientMessage = "Nothing was recorded.";
        }
        else
        {
            macro.Steps.AddRange(steps);
            transientMessage = "Added " + steps.Count + (steps.Count == 1 ? " step" : " steps") + " from the recording.";
            RebuildStepRows();
            SaveCurrentMacro();
        }
        UpdateControls();
    }

    void OnRecorded(RecordedEvent recorded)
    {
        lock (recordLock) recordedEvents.Add(recorded);
    }

    // ---------------------------------------------------------------- macro editing

    void RebuildStepRows(int selectIndex = -1)
    {
        stepRows.Clear();
        for (int i = 0; i < macro.Steps.Count; i++)
            stepRows.Add((i + 1) + ".  " + macro.Steps[i].Describe());

        if (selectIndex >= 0 && selectIndex < stepRows.Count) StepList.SelectedIndex = selectIndex;
        UpdateMacroSummary();
    }

    void UpdateMacroSummary()
    {
        int count = macro.Steps.Count;
        if (count == 0)
        {
            MacroSummary.Text = "No steps yet — record one, or add steps below.";
            return;
        }
        int ms = macro.TotalDurationMs;
        string length = ms >= 1000
            ? (ms / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " s"
            : ms + " ms";
        MacroSummary.Text = count + (count == 1 ? " step" : " steps") + " · one pass takes about " + length;
    }

    MacroStep? BuildStepFromEditor()
    {
        switch (StepTypeBox.SelectedIndex)
        {
            case 0:
                return MacroStep.MakeClick(
                    (ClickButton)Math.Clamp(StepClickButtonBox.SelectedIndex, 0, 2),
                    StepClickTypeBox.SelectedIndex == 1 ? 2 : 1,
                    new PixelPoint((int)Dec(StepClickX), (int)Dec(StepClickY)),
                    holdMs: ReadHoldMs(StepPressBox, StepHoldBox, StepHoldCustom));

            case 1:
                return MacroStep.MakeDrag(
                    (ClickButton)Math.Clamp(StepDragButtonBox.SelectedIndex, 0, 2),
                    new PixelPoint((int)Dec(StepDragFromX), (int)Dec(StepDragFromY)),
                    new PixelPoint((int)Dec(StepDragToX), (int)Dec(StepDragToY)),
                    (int)Dec(StepDragDuration));

            case 2:
                if (stepKey == Key.None)
                {
                    validationMessage = "Click the key box in the editor and press a key first.";
                    return null;
                }
                return MacroStep.MakeKey(stepKey,
                    ReadModifiers(StepModCtrl, StepModShift, StepModAlt, StepModMeta),
                    (int)Dec(StepKeyHold));

            default:
                return MacroStep.MakeWait((int)Dec(StepWaitMs));
        }
    }

    void AddStep(bool replaceSelected)
    {
        validationMessage = null;
        var step = BuildStepFromEditor();
        if (step == null) { UpdateControls(); return; }

        int selected = StepList.SelectedIndex;
        if (replaceSelected && selected >= 0 && selected < macro.Steps.Count)
        {
            macro.Steps[selected] = step;
            RebuildStepRows(selected);
        }
        else
        {
            macro.Steps.Add(step);
            RebuildStepRows(macro.Steps.Count - 1);
        }
        SaveCurrentMacro();
        UpdateControls();
    }

    void MoveStep(int offset)
    {
        int index = StepList.SelectedIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= macro.Steps.Count) return;

        (macro.Steps[index], macro.Steps[target]) = (macro.Steps[target], macro.Steps[index]);
        RebuildStepRows(target);
        SaveCurrentMacro();
        UpdateControls();
    }

    void DeleteStep()
    {
        int index = StepList.SelectedIndex;
        if (index < 0 || index >= macro.Steps.Count) return;
        macro.Steps.RemoveAt(index);
        RebuildStepRows(Math.Min(index, macro.Steps.Count - 1));
        SaveCurrentMacro();
        UpdateControls();
    }

    void ClearSteps()
    {
        macro.Steps.Clear();
        RebuildStepRows();
        SaveCurrentMacro();
        UpdateControls();
    }

    // ---------------------------------------------------------------- macro files

    static FilePickerFileType MacroFileType => new("Auto Clicker macro") { Patterns = ["*.json"] };

    async System.Threading.Tasks.Task<IStorageFolder?> MacroFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(Macro.DefaultFolder);
            return await StorageProvider.TryGetFolderFromPathAsync(Macro.DefaultFolder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    async System.Threading.Tasks.Task OpenMacroAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open macro",
            AllowMultiple = false,
            FileTypeFilter = [MacroFileType],
            SuggestedStartLocation = await MacroFolderAsync()
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file?.TryGetLocalPath() is not { } path) return;

        var loaded = Macro.Load(path);
        if (loaded == null)
        {
            transientMessage = "That file isn't a macro this version can read.";
        }
        else
        {
            macro.Steps.Clear();
            macro.Steps.AddRange(loaded.Steps);
            macro.Name = loaded.Name ?? Path.GetFileNameWithoutExtension(path);
            transientMessage = "Opened " + Path.GetFileName(path) + ".";
            RebuildStepRows();
            SaveCurrentMacro();
        }
        UpdateControls();
    }

    async System.Threading.Tasks.Task SaveMacroAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save macro",
            SuggestedFileName = string.IsNullOrWhiteSpace(macro.Name) ? "macro.json" : macro.Name + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [MacroFileType],
            SuggestedStartLocation = await MacroFolderAsync()
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            macro.Name = Path.GetFileNameWithoutExtension(path);
            macro.Save(path);
            transientMessage = "Saved " + Path.GetFileName(path) + ".";
        }
        catch (Exception ex)
        {
            transientMessage = "Couldn't save the macro: " + ex.Message;
        }
        UpdateControls();
    }

    static string CurrentMacroPath => Path.Combine(Macro.DefaultFolder, CurrentMacroFile);

    void LoadCurrentMacro()
    {
        var loaded = Macro.Load(CurrentMacroPath);
        if (loaded != null)
        {
            macro.Steps.AddRange(loaded.Steps);
            macro.Name = loaded.Name;
        }
        RebuildStepRows();
    }

    void SaveCurrentMacro()
    {
        try { macro.Save(CurrentMacroPath); }
        catch (Exception) { /* the working copy is a convenience; Save... reports real failures */ }
    }

    // ---------------------------------------------------------------- updates

    /// <summary>Asks GitHub for a newer release. Quiet mode (the startup check) reports nothing on failure.</summary>
    async System.Threading.Tasks.Task CheckForUpdateAsync(bool quiet)
    {
        if (checkingForUpdate) return;
        checkingForUpdate = true;
        UpdateControls();
        try
        {
            availableUpdate = await Updater.CheckAsync();
            if (!quiet)
                transientMessage = availableUpdate == null
                    ? "You're on the latest version (" + AppVersion.Display + ")."
                    : null;
        }
        catch (Exception ex)
        {
            if (!quiet) transientMessage = "Couldn't check for updates: " + ex.Message;
        }
        finally
        {
            checkingForUpdate = false;
            UpdateControls();
        }
    }

    async System.Threading.Tasks.Task OnUpdateClickedAsync()
    {
        if (availableUpdate == null)
        {
            await CheckForUpdateAsync(quiet: false);
            if (availableUpdate == null) return;
        }

        var update = availableUpdate;
        string from = AppVersion.Display;
        string to = "v" + AppVersion.Format(update.Version);
        if (!await ConfirmAsync("Update Auto Clicker", "Update from " + from + " to " + to + "?",
                "Auto Clicker will download the update, then restart. Your settings and macros are kept.",
                "Update", "Not now"))
            return;

        updateProgress = 0;
        transientMessage = "Downloading " + to + "…";
        UpdateControls();
        try
        {
            var progress = new Progress<double>(fraction =>
            {
                updateProgress = fraction;
                UpdateControls();
            });
            await Updater.InstallAsync(update, progress);
            transientMessage = "Restarting into " + to + "…";
            UpdateControls();
            Close();   // the new copy is waiting for this one to exit
        }
        catch (UpdateBlockedException ex)
        {
            transientMessage = ex.Message + " Opening the download page instead.";
            Updater.OpenInBrowser(ex.PageUrl);
        }
        catch (Exception ex)
        {
            transientMessage = "The update failed: " + ex.Message;
        }
        finally
        {
            updateProgress = null;
            UpdateControls();
        }
    }

    /// <summary>A small modal yes/no box; Avalonia has no built-in message box.</summary>
    async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string heading, string detail, string yes, string no)
    {
        var yesButton = new Button { Content = yes, MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsDefault = true };
        yesButton.Classes.Add("accent");
        var noButton = new Button { Content = no, MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Icon = Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Width = 360,
                Children =
                {
                    new TextBlock { Text = heading, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 6, 0, 0),
                        Children = { yesButton, noButton }
                    }
                }
            }
        };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    // ---------------------------------------------------------------- pick a point

    void StartPick(NumericUpDown x, NumericUpDown y, Button button)
    {
        pickTargetX = x;
        pickTargetY = y;
        pickTargetButton = button;
        pickTicks = StartCountdownSeconds * 1000 / (int)pickTimer.Interval.TotalMilliseconds;
        pickTimer.Start();
        PickTick();
    }

    void PickTick()
    {
        if (pickTargetX == null || pickTargetY == null || pickTargetButton == null)
        {
            pickTimer.Stop();
            return;
        }

        var position = backend.GetCursorPosition();
        pickTargetX.Value = Math.Clamp(position.X, (int)pickTargetX.Minimum, (int)pickTargetX.Maximum);
        pickTargetY.Value = Math.Clamp(position.Y, (int)pickTargetY.Minimum, (int)pickTargetY.Maximum);

        pickTicks--;
        if (pickTicks <= 0)
        {
            pickTimer.Stop();
            pickTargetButton.Content = "Pick";
            pickTargetX = pickTargetY = null;
            pickTargetButton = null;
            return;
        }
        int secondsLeft = (int)Math.Ceiling(pickTicks * pickTimer.Interval.TotalMilliseconds / 1000);
        pickTargetButton.Content = secondsLeft + "…";
    }

    // ---------------------------------------------------------------- key capture

    void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!TryCapture(e, out var key)) return;
        chosenKey = key;
        ModCtrl.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        ModShift.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ModAlt.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        ModMeta.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        UpdateKeyCaptureText();
        UpdateControls();
    }

    void OnStepCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!TryCapture(e, out var key)) return;
        stepKey = key;
        StepModCtrl.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        StepModShift.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        StepModAlt.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        StepModMeta.IsChecked = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        UpdateKeyCaptureText();
        UpdateControls();
    }

    bool TryCapture(KeyEventArgs e, out Key key)
    {
        e.Handled = true;
        key = e.Key;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return false;   // wait for the real key; modifiers are read off it

        if (!KeyMap.IsSupported(e.Key))
        {
            validationMessage = "That key isn't supported yet. Try a letter, number, arrow or function key.";
            UpdateControls();
            return false;
        }

        validationMessage = null;
        return true;
    }

    void UpdateKeyCaptureText()
    {
        KeyCaptureText.Text = chosenKey == Key.None ? "Click here, then press a key" : KeyMap.Name(chosenKey);
        KeyCaptureText.Opacity = chosenKey == Key.None ? 0.6 : 1;
        StepKeyCaptureText.Text = stepKey == Key.None ? "Click here, then press a key" : KeyMap.Name(stepKey);
        StepKeyCaptureText.Opacity = stepKey == Key.None ? 0.6 : 1;
    }

    // ---------------------------------------------------------------- hotkey

    void RegisterHotkey()
    {
        hotkeys.Unregister();
        var clash = bindings.FirstOrDefault(slot => slot.Binding.SameKeyAs(HotkeyKey, Modifiers.None));
        hotkeyError = clash != null ? "the key binding " + BindingLabel(clash.Binding) + " uses it"
            : hotkeys.TryRegister(HotkeyKey, Modifiers.None, out var error) ? null : error ?? "not available";
    }

    // ---------------------------------------------------------------- key bindings

    static string BindingLabel(KeyBinding binding) =>
        string.IsNullOrWhiteSpace(binding.Name) ? binding.KeyName : "“" + binding.Name + "” (" + binding.KeyName + ")";

    void LoadBindings()
    {
        foreach (var binding in KeyBindingStore.Load())
            bindings.Add(new BindingSlot(binding, HotkeyService.Create(), NewSession()));
        RebuildBindingRows();
    }

    void SaveBindings() => KeyBindingStore.Save(bindings.Select(slot => slot.Binding).ToList());

    /// <summary>Registers every binding's key; ones that are taken stay listed with the reason.</summary>
    void RegisterBindings()
    {
        foreach (var slot in bindings) slot.Error = Register(slot);
        RebuildBindingRows();
    }

    /// <summary>Registers the slot's key; returns why it couldn't be, or null.</summary>
    string? Register(BindingSlot slot)
    {
        slot.Hotkey.Unregister();
        var binding = slot.Binding;
        if (!slot.Hotkey.TryRegister(binding.Key, binding.Modifiers, out var error)) return error ?? "not available";
        // One handler per service; registering again only changes the key it listens for.
        if (!slotsWithHandler.Contains(slot.Hotkey))
        {
            var hotkey = slot.Hotkey;
            hotkey.Pressed += () => Dispatcher.UIThread.Post(() =>
            {
                var pressed = bindings.FirstOrDefault(s => s.Hotkey == hotkey);
                if (pressed != null) ToggleBinding(pressed);
            });
            slotsWithHandler.Add(hotkey);
        }
        return null;
    }

    readonly HashSet<IHotkeyService> slotsWithHandler = [];

    void ToggleBinding(BindingSlot slot)
    {
        if (IsRecording) return;   // the key is probably being pressed to be recorded
        if (slot.Session.IsRunning)
        {
            EndSession(slot.Session);
            RebuildBindingRows();
            UpdateControls();
            return;
        }

        var binding = slot.Binding;
        transientMessage = !backend.IsAvailable ? backend.UnavailableReason
            : (binding.Setup.Problem() ?? KeyClash(binding.Setup, binding)) is { } problem ? BindingLabel(binding) + ": " + problem
            : null;
        if (transientMessage != null) { UpdateControls(); return; }

        if (!binding.RunAlongside)
        {
            countdownTimer.Stop();
            EndSession(mainRun);
            foreach (var other in bindings)
                if (other != slot) EndSession(other.Session);
        }

        BeforeRunStarts();
        slot.Session.Start(binding.Setup.Clone(), binding.Modifiers == Modifiers.None ? TimeSpan.Zero : ModifierReleaseTime);
        slot.WasRunning = true;
        RebuildBindingRows();
        UpdateControls();
    }

    void RebuildBindingRows()
    {
        int selected = BindingList.SelectedIndex;
        bindingRows.Clear();
        foreach (var slot in bindings)
        {
            var binding = slot.Binding;
            string row = binding.KeyName + "  ·  " +
                         (string.IsNullOrWhiteSpace(binding.Name) ? "" : binding.Name + ": ") +
                         binding.Setup.Describe() +
                         (binding.RunAlongside ? " · alongside" : "");
            if (slot.Error != null) row = "⚠ " + row + " · key unavailable: " + slot.Error;
            else if (slot.Session.IsRunning) row = "▶ " + row + " · running";
            bindingRows.Add(row);
        }
        if (selected >= 0 && selected < bindingRows.Count) BindingList.SelectedIndex = selected;
    }

    void OnBindingSelected()
    {
        int index = BindingList.SelectedIndex;
        if (index >= 0 && index < bindings.Count)
        {
            var binding = bindings[index].Binding;
            BindingName.Text = binding.Name;
            bindingKey = binding.Key;
            bindingModifiers = binding.Modifiers;
            BindingAlongside.IsChecked = binding.RunAlongside;
            UpdateBindingKeyText();
        }
        UpdateControls();
    }

    void OnBindingCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!TryCapture(e, out var key)) return;
        bindingKey = key;
        bindingModifiers = Modifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) bindingModifiers |= Modifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) bindingModifiers |= Modifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) bindingModifiers |= Modifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) bindingModifiers |= Modifiers.Meta;
        UpdateBindingKeyText();
        UpdateControls();
    }

    void UpdateBindingKeyText()
    {
        BindingKeyText.Text = bindingKey == Key.None ? "Click to set key" : KeyMap.ComboName(bindingKey, bindingModifiers);
        BindingKeyText.Opacity = bindingKey == Key.None ? 0.6 : 1;
    }

    /// <summary>Saves the window's setup under the name and key in the editor, as a new binding or over the selected one.</summary>
    void AddBinding(bool replaceSelected)
    {
        validationMessage = transientMessage = null;
        int index = replaceSelected ? BindingList.SelectedIndex : -1;
        if (replaceSelected && (index < 0 || index >= bindings.Count)) return;
        var existing = index >= 0 ? bindings[index] : null;

        var binding = new KeyBinding
        {
            Name = (BindingName.Text ?? "").Trim(),
            Key = bindingKey,
            Modifiers = bindingModifiers,
            RunAlongside = BindingAlongside.IsChecked == true,
            Setup = CaptureSetup()
        };

        string? problem = bindingKey == Key.None ? "Click the key box next to the name and press the key that should start it."
            : binding.SameKeyAs(HotkeyKey, Modifiers.None) ? HotkeyName + " is already the start/stop key. Pick another key."
            : bindings.FirstOrDefault(s => s != existing && s.Binding.SameKeyAs(bindingKey, bindingModifiers)) is { } taken
                ? binding.KeyName + " already starts " + BindingLabel(taken.Binding) + ". Pick another key, or select that one and Replace it."
            : binding.Setup.Problem() ?? KeyClash(binding.Setup, binding);
        if (problem != null) { transientMessage = problem; UpdateControls(); return; }

        var slot = existing ?? new BindingSlot(binding, HotkeyService.Create(), NewSession());
        var previous = slot.Binding;
        if (existing != null) EndSession(existing.Session);
        slot.Binding = binding;
        if (Register(slot) is { } error)
        {
            // Keep things as they were rather than end up with a binding whose key doesn't work.
            slot.Binding = previous;
            if (existing != null) slot.Error = Register(slot);
            else slot.Hotkey.Dispose();
            transientMessage = binding.KeyName + " can't be used (" + error + "). Pick another key.";
            UpdateControls();
            return;
        }
        slot.Error = null;

        if (existing == null)
        {
            bindings.Add(slot);
            RebuildBindingRows();
            BindingList.SelectedIndex = bindings.Count - 1;
        }
        else RebuildBindingRows();

        SaveBindings();
        transientMessage = (existing == null ? "Added " : "Updated ") + BindingLabel(binding) + ". Its key starts and stops it anywhere.";
        UpdateControls();
    }

    void LoadBindingIntoWindow()
    {
        int index = BindingList.SelectedIndex;
        if (index < 0 || index >= bindings.Count || IsBusy) return;
        var binding = bindings[index].Binding;
        ShowSetup(binding.Setup);
        transientMessage = "Loaded " + BindingLabel(binding) + ". Change it, then press Replace to save it to the binding.";
        UpdateControls();
    }

    void RemoveBinding()
    {
        int index = BindingList.SelectedIndex;
        if (index < 0 || index >= bindings.Count) return;
        var slot = bindings[index];
        EndSession(slot.Session);
        slot.Hotkey.Dispose();
        slotsWithHandler.Remove(slot.Hotkey);
        bindings.RemoveAt(index);
        RebuildBindingRows();
        BindingList.SelectedIndex = Math.Min(index, bindings.Count - 1);
        SaveBindings();
        transientMessage = "Removed " + BindingLabel(slot.Binding) + ".";
        // The start/stop key may have been waiting on this binding's key.
        RegisterHotkey();
        UpdateControls();
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
        SetIndex(ModeBox, settings.GetInt("Mode", 0), 4);
        SetIndex(ButtonBox, settings.GetInt("MouseButton", 0), 3);
        SetIndex(ClickTypeBox, settings.GetInt("ClickType", 0), 2);
        SetIndex(PressBox, settings.GetInt("Press", 0), 2);
        SetIndex(HoldBox, settings.GetInt("Hold", HoldBox.SelectedIndex), ClickHold.ChoicesMs.Length + 1);
        SetValue(HoldCustom, settings.GetDecimal("HoldCustomMs", ClickHold.DefaultMs));
        PositionFixed.IsChecked = settings.GetBool("FixedPosition", false);
        PositionCursor.IsChecked = PositionFixed.IsChecked != true;
        SetValue(PositionX, settings.GetDecimal("X", 0));
        SetValue(PositionY, settings.GetDecimal("Y", 0));

        SetIndex(DragButtonBox, settings.GetInt("DragButton", 0), 3);
        SetValue(DragFromX, settings.GetDecimal("DragFromX", 0));
        SetValue(DragFromY, settings.GetDecimal("DragFromY", 0));
        SetValue(DragToX, settings.GetDecimal("DragToX", 0));
        SetValue(DragToY, settings.GetDecimal("DragToY", 0));
        SetValue(DragDuration, settings.GetDecimal("DragDurationMs", 400));

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
        LagCheckBox.IsChecked = settings.GetBool("LagCheck", true);
        LagNotifyBox.IsChecked = settings.GetBool("LagNotify", true);
        Topmost = TopMostBox.IsChecked == true;
    }

    void SaveSettings()
    {
        settings.Set("Mode", ModeBox.SelectedIndex);
        settings.Set("MouseButton", ButtonBox.SelectedIndex);
        settings.Set("ClickType", ClickTypeBox.SelectedIndex);
        settings.Set("Press", PressBox.SelectedIndex);
        settings.Set("Hold", HoldBox.SelectedIndex);
        settings.Set("HoldCustomMs", Dec(HoldCustom));
        settings.Set("FixedPosition", PositionFixed.IsChecked == true);
        settings.Set("X", Dec(PositionX));
        settings.Set("Y", Dec(PositionY));
        settings.Set("DragButton", DragButtonBox.SelectedIndex);
        settings.Set("DragFromX", Dec(DragFromX));
        settings.Set("DragFromY", Dec(DragFromY));
        settings.Set("DragToX", Dec(DragToX));
        settings.Set("DragToY", Dec(DragToY));
        settings.Set("DragDurationMs", Dec(DragDuration));
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
        settings.Set("LagCheck", LagCheckBox.IsChecked == true);
        settings.Set("LagNotify", LagNotifyBox.IsChecked == true);
        settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        countdownTimer.Stop();
        recordCountdownTimer.Stop();
        pickTimer.Stop();
        trayHideTimer.Stop();
        uiTimer.Stop();
        foreach (var slot in bindings) { slot.Session.Stop(); slot.Hotkey.Dispose(); }
        mainRun.Stop();
        notifier.Dispose();
        recorder.Dispose();
        SaveSettings();
        SaveCurrentMacro();
        hotkeys.Dispose();
        backend.Dispose();
        base.OnClosed(e);
    }
}
