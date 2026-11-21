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

    const int DefaultStartDelaySeconds = 3;

    /// <summary>How many changes to the macro's lines Undo can take back.</summary>
    const int UndoLimit = 100;

    /// <summary>
    /// A binding with modifiers starts this much later, so the Ctrl or Shift still held from pressing
    /// it doesn't turn its first click into a Ctrl-click.
    /// </summary>
    static readonly TimeSpan ModifierReleaseTime = TimeSpan.FromMilliseconds(300);
    const int RecordCountdownSeconds = 2;
    // Positions in the step type box.
    const int TypeClick = 0, TypeDrag = 1, TypeKey = 2, TypeWait = 3, TypeIf = 4, TypeElseIf = 5, TypeElse = 6,
        TypeWhile = 7, TypeFor = 8, TypeGoTo = 9, TypeStop = 10;
    const string CurrentMacroFile = "current-macro.json";

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
    readonly DispatcherTimer recordCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer trayHideTimer = new() { Interval = TrayLingerTime };

    readonly Macro macro = new();
    readonly ObservableCollection<StepRow> stepRows = [];
    readonly ObservableCollection<string> libraryRows = [];
    readonly List<RecordedEvent> recordedEvents = [];
    readonly object recordLock = new();

    /// <summary>The macro's lines as they were before a change, for Undo and Redo.</summary>
    sealed record MacroSnapshot(List<MacroStep> Steps, string? Name, int Selected, string What);
    readonly List<MacroSnapshot> undoStack = [];
    readonly List<MacroSnapshot> redoStack = [];

    // The screen checks set up in the editor for a click, or the two ends of a drag.
    MacroStep? clickCheck;
    MacroStep? dragFromCheck;
    MacroStep? dragToCheck;

    UpdateInfo? availableUpdate;
    bool checkingForUpdate;
    double? updateProgress;   // null when no install is in progress
    Key chosenKey = Key.None;
    Key stepKey = Key.None;
    Key bindingKey = Key.None;
    Modifiers bindingModifiers = Modifiers.None;
    int countdown;
    int recordCountdown;
    bool picking;
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
        StepList.ItemTemplate = StepRow.Template;
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
        PickSecondsBox.ValueChanged += (_, _) => ApplyPickSettings();
        PickOnClickBox.IsCheckedChanged += (_, _) => ApplyPickSettings();
        HotkeyBox.SelectionChanged += (_, _) => { RegisterHotkey(); UpdateControls(); };

        StartButton.Click += (_, _) => BeginCountdown();
        PauseButton.Click += (_, _) => TogglePause();
        StopButton.Click += (_, _) => StopEverything();
        StepClickAimBox.SelectionChanged += (_, _) => UpdateControls();
        StepDragFromAimBox.SelectionChanged += (_, _) => UpdateControls();
        StepDragToAimBox.SelectionChanged += (_, _) => UpdateControls();
        StepClickCheckButton.Click += async (_, _) => { clickCheck = await EditAimAsync(clickCheck) ?? clickCheck; UpdateControls(); };
        StepDragFromCheckButton.Click += async (_, _) => { dragFromCheck = await EditAimAsync(dragFromCheck) ?? dragFromCheck; UpdateControls(); };
        StepDragToCheckButton.Click += async (_, _) => { dragToCheck = await EditAimAsync(dragToCheck) ?? dragToCheck; UpdateControls(); };
        StepUndoButton.Click += (_, _) => UndoMacro();
        StepRedoButton.Click += (_, _) => RedoMacro();
        KeyDown += OnWindowKeyDown;
        BindingActionBox.SelectionChanged += (_, _) => UpdateControls();
        PickButton.Click += (_, _) => StartPick(PositionX, PositionY, PickButton);
        DragFromPick.Click += (_, _) => StartPick(DragFromX, DragFromY, DragFromPick);
        DragToPick.Click += (_, _) => StartPick(DragToX, DragToY, DragToPick);
        StepClickPick.Click += (_, _) => StartPick(StepClickX, StepClickY, StepClickPick);
        StepDragFromPick.Click += (_, _) => StartPick(StepDragFromX, StepDragFromY, StepDragFromPick);
        StepDragToPick.Click += (_, _) => StartPick(StepDragToX, StepDragToY, StepDragToPick);

        RecordButton.Click += (_, _) => ToggleRecording();
        StepAddButton.Click += async (_, _) => await AddStepAsync(replaceSelected: false);
        StepReplaceButton.Click += async (_, _) => await AddStepAsync(replaceSelected: true);
        StepList.DoubleTapped += async (_, _) => await EditSelectedStepAsync();
        StepUpButton.Click += (_, _) => MoveStep(-1);
        StepDownButton.Click += (_, _) => MoveStep(1);
        StepDeleteButton.Click += (_, _) => DeleteStep();
        StepClearButton.Click += (_, _) => ClearSteps();
        MacroSaveButton.Click += async (_, _) => await SaveMacroAsync();
        LibraryList.ItemsSource = libraryRows;
        LibraryList.SelectionChanged += (_, _) => UpdateControls();
        LibraryList.DoubleTapped += async (_, _) => await LoadFromLibraryAsync();
        LibraryLoadButton.Click += async (_, _) => await LoadFromLibraryAsync();
        LibraryRenameButton.Click += async (_, _) => await RenameInLibraryAsync();
        LibraryDeleteButton.Click += async (_, _) => await DeleteFromLibraryAsync();
        LibraryDownloadButton.Click += async (_, _) => await DownloadFromLibraryAsync();
        LibraryImportButton.Click += async (_, _) => await ImportToLibraryAsync();
        StepForUnit.SelectionChanged += (_, _) => UpdateControls();
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
        recordCountdownTimer.Tick += (_, _) => RecordCountdownTick();
        recorder.Recorded += OnRecorded;
        trayHideTimer.Tick += (_, _) => { trayHideTimer.Stop(); notifier.Hide(); };

        LoadSettings();
        LoadCurrentMacro();
        RefreshLibrary();
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
        MacroSection.IsVisible = LibrarySection.IsVisible = mode == AutomationMode.Macro;

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
        StepUndoButton.IsEnabled = canEditMacro && undoStack.Count > 0;
        StepRedoButton.IsEnabled = canEditMacro && redoStack.Count > 0;
        ToolTip.SetTip(StepUndoButton, undoStack.Count > 0 ? "Undo " + undoStack[^1].What + " (Ctrl+Z)." : "Undoes the last change to the lines (Ctrl+Z).");
        ToolTip.SetTip(StepRedoButton, redoStack.Count > 0 ? "Redo " + redoStack[^1].What + " (Ctrl+Y)." : "Does again what Undo took back (Ctrl+Y).");
        MacroSaveButton.IsEnabled = canEditMacro && macro.Steps.Count > 0;
        StepClickPanel.IsEnabled = StepDragPanel.IsEnabled = StepKeyPanel.IsEnabled =
            StepWaitPanel.IsEnabled = StepForPanel.IsEnabled = StepGoToPanel.IsEnabled = canEditMacro;
        bool librarySelected = LibraryList.SelectedIndex >= 0 && LibraryList.SelectedIndex < libraryRows.Count;
        LibraryCard.IsEnabled = canEditMacro;
        LibraryEmpty.IsVisible = libraryRows.Count == 0;
        LibraryList.IsVisible = libraryRows.Count > 0;
        LibraryLoadButton.IsEnabled = LibraryRenameButton.IsEnabled = LibraryDeleteButton.IsEnabled =
            LibraryDownloadButton.IsEnabled = librarySelected;
        RecordButton.IsEnabled = !IsBusy;
        RecordButton.Content = recorder.IsRecording || recordCountdownTimer.IsEnabled ? "Stop recording" : "Record";

        int type = StepTypeBox.SelectedIndex;
        StepClickPanel.IsVisible = type == TypeClick;
        StepDragPanel.IsVisible = type == TypeDrag;
        StepKeyPanel.IsVisible = type == TypeKey;
        StepWaitPanel.IsVisible = type == TypeWait;
        StepForPanel.IsVisible = type == TypeFor;
        StepGoToPanel.IsVisible = type == TypeGoTo;
        bool clickAims = StepClickAimBox.SelectedIndex == 1;
        bool fromAims = StepDragFromAimBox.SelectedIndex == 1, toAims = StepDragToAimBox.SelectedIndex == 1;
        StepClickSpotRow.IsVisible = !clickAims;
        StepClickCheckRow.IsVisible = clickAims;
        StepDragFromSpotRow.IsVisible = !fromAims;
        StepDragFromCheckRow.IsVisible = fromAims;
        StepDragToSpotRow.IsVisible = !toAims;
        StepDragToCheckRow.IsVisible = toAims;
        ShowAim(StepClickCheckText, clickCheck);
        ShowAim(StepDragFromCheckText, dragFromCheck);
        ShowAim(StepDragToCheckText, dragToCheck);
        StepAimTip.IsVisible = (type == TypeClick && clickAims) || (type == TypeDrag && (fromAims || toAims));
        StepGoToLine.Maximum = Math.Max(1, macro.Steps.Count + 1);
        StepControlHint.Text = ControlHint(type);
        StepControlHint.IsVisible = StepControlHint.Text != null;

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
        BindingLoadButton.IsEnabled = bindingSelected && !busy && !bindings[binding].Binding.IsControl;
        var action = ChosenBindingAction;
        BindingLine.IsVisible = action == BindingAction.GoToLine;
        BindingLineLabel.IsVisible = BindingLine.IsVisible;
        BindingAlongside.IsVisible = action == BindingAction.StartStop;
        BindingAddButton.Content = action == BindingAction.StartStop ? "Add current setup" : "Add key";

        StartButton.IsEnabled = !busy && backend.IsAvailable;
        StopButton.IsEnabled = IsBusy || BindingsRunning > 0;
        StartButton.Content = hotkeyError == null ? "Start (" + HotkeyName + ")" : "Start";
        StopButton.Content = hotkeyError == null ? "Stop (" + HotkeyName + ")" : "Stop";
        PauseButton.IsEnabled = mainRun.IsRunning;
        string pauseKey = bindings.FirstOrDefault(s => s.Binding.Action == BindingAction.Pause && s.Error == null) is { } pauseSlot
            ? " (" + pauseSlot.Binding.KeyName + ")" : "";
        PauseButton.Content = (mainRun.IsPaused ? "Resume" : "Pause") + pauseKey;

        UpdateMacroSummary();
        RefreshStatus();
    }

    /// <summary>Shows what a click's or drag's screen check looks for, or that it isn't set up yet.</summary>
    static void ShowAim(TextBlock text, MacroStep? check)
    {
        text.Text = check?.DescribeAim() ?? "not set up yet";
        ToolTip.SetTip(text, check?.DescribeAim() ?? "Press Screen check… to choose what to look for.");
    }

    BindingAction ChosenBindingAction => (BindingAction)Math.Clamp(BindingActionBox.SelectedIndex, 0, 3);

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
        else if (running)
        {
            RefreshStatus();   // line counter, pause and resume countdown tick along while the count holds still
        }

        // While paused, the line it's held at is selected, so it's easy to see.
        bool paused = mainRun.IsPaused;
        if (paused && mainRun.Macro is { } held && Mode == AutomationMode.Macro)
        {
            int line = held.CurrentStepIndex;
            if (line >= 0 && line < stepRows.Count && StepList.SelectedIndex != line) StepList.SelectedIndex = line;
        }
        if (paused != wasPaused)
        {
            wasPaused = paused;
            UpdateControls();
        }
    }

    bool wasPaused;

    /// <summary>The Pause button and pause keys: holds the window's run, or lets it carry on after the resume delay.</summary>
    void TogglePause()
    {
        if (!mainRun.IsRunning) return;
        if (mainRun.IsPaused) mainRun.Control.Resume((int)Dec(ResumeDelayBox) * 1000);
        else mainRun.Control.Pause();
        RefreshState();
        UpdateControls();
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
        else if (mainRun.IsPaused)
            text = "Paused · " + Counted(mainRun, count) + "." + StepProgress() + " Press Resume" +
                   (KeyFor(BindingAction.Pause) is { } pauseKey ? " or " + pauseKey : "") + " to carry on" +
                   (mainRun.Macro != null && KeyFor(BindingAction.Step) is { } stepKeyName ? ", or " + stepKeyName + " to run one line" : "") + ".";
        else if (mainRun.IsRunning && mainRun.Control.ResumeLeftMs > 0)
            text = "Resuming in " + (mainRun.Control.ResumeLeftMs / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " s… switch to your target window.";
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

    /// <summary>The key of the first working binding that does <paramref name="action"/>, or null.</summary>
    string? KeyFor(BindingAction action) =>
        bindings.FirstOrDefault(s => s.Binding.Action == action && s.Error == null)?.Binding.KeyName;

    string StepProgress()
    {
        var running = mainRun.Macro;
        if (running == null) return "";
        int step = running.CurrentStepIndex;
        return step >= 0 ? " Line " + (step + 1) + " of " + (mainRun.Setup?.Steps.Count ?? 0) + "." : "";
    }

    // ---------------------------------------------------------------- start / stop

    void BeginCountdown()
    {
        var setup = BuildSetup();
        if (setup == null) { UpdateControls(); return; }
        // Started from the button, the first clicks or keys would land on this window,
        // so give the user a moment to switch to their target (unless they chose no delay).
        countdown = (int)Dec(StartDelayBox);
        if (countdown <= 0)
        {
            StartNow();
            return;
        }
        // Use the countdown to learn what the target looks like when it's keeping up.
        if (setup.LagCheck) mainRun.WatchAhead();
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

    /// <param name="startLine">For a macro: the line (from 0) the first pass starts at, for a Go to line key.</param>
    void StartNow(int startLine = 0)
    {
        var setup = BuildSetup();
        if (setup == null) { mainRun.Stop(); UpdateControls(); return; }
        SaveSettings();
        BeforeRunStarts();
        mainRun.Start(setup, startLine: startLine);
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
            if (macro.Steps.Count > 0) Remember("loading a binding's macro");
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
            Remember("recording");
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

    static string? ControlHint(int type) => type switch
    {
        TypeIf => "Add opens the condition window, where you add screen checks and choose and/or between them. The If comes " +
                  "with its End if; the lines between them run only when the condition is true. Double-click an If to change it.",
        TypeElseIf => "Goes inside an If, below its lines: tried when the If (and any Else if above) was false. Add opens its condition.",
        TypeElse => "Goes inside an If, below its lines and any Else if: its lines run when nothing above was true.",
        TypeWhile => "Add opens the condition window: its screen checks, how often to check, and how long it may loop. " +
                     "The lines up to End while repeat for as long as the condition is true.",
        TypeFor => null,
        TypeGoTo => null,
        TypeStop => "Ends the whole run, like pressing Stop.",
        _ => null
    };

    void RebuildStepRows(int selectIndex = -1)
    {
        var blocks = MacroBlocks.Analyze(macro.Steps);
        stepRows.Clear();
        for (int i = 0; i < macro.Steps.Count; i++)
            stepRows.Add(new StepRow(i + 1, blocks.Depth[i], macro.Steps[i].Describe(), macro.Steps[i].IsControl));

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
        string lines = count + (count == 1 ? " line" : " lines");
        if (Macro.Problem(macro.Steps) is { } problem)
        {
            MacroSummary.Text = lines + " · " + problem;
            return;
        }
        if (UnguardedAim() is { } line)
        {
            MacroSummary.Text = lines + " · Tip: line " + (line + 1) + " aims at a screen check outside any If or While; " +
                                "put it inside one that checks the same thing, so it only runs when the target is there.";
            return;
        }
        if (macro.Steps.Exists(step => step.IsControl))
        {
            MacroSummary.Text = lines + (string.IsNullOrEmpty(macro.Name) ? "" : " · " + macro.Name);
            return;
        }
        int ms = macro.TotalDurationMs;
        string length = ms >= 1000
            ? (ms / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " s"
            : ms + " ms";
        MacroSummary.Text = lines + " · one pass takes about " + length;
    }

    /// <summary>The first click or drag aimed by a screen check that isn't inside an If or While; null if there's none.</summary>
    int? UnguardedAim()
    {
        var blocks = MacroBlocks.Analyze(macro.Steps);
        for (int i = 0; i < macro.Steps.Count; i++)
        {
            if (!macro.Steps[i].AimChecks.Any()) continue;
            bool guarded = false;
            for (int j = 0; j < i && !guarded; j++)
                guarded = macro.Steps[j].Kind is StepKind.If or StepKind.While && blocks.End[j] > i;
            if (!guarded) return i;
        }
        return null;
    }

    MacroStep? BuildStepFromEditor()
    {
        const string NoCheck = "Press Screen check… to set up what the line aims at, or switch it back to A spot.";
        switch (StepTypeBox.SelectedIndex)
        {
            case TypeClick:
            {
                bool aims = StepClickAimBox.SelectedIndex == 1;
                if (aims && clickCheck == null) { validationMessage = NoCheck; return null; }
                var click = MacroStep.MakeClick(
                    (ClickButton)Math.Clamp(StepClickButtonBox.SelectedIndex, 0, 2),
                    StepClickTypeBox.SelectedIndex == 1 ? 2 : 1,
                    new PixelPoint((int)Dec(StepClickX), (int)Dec(StepClickY)),
                    holdMs: ReadHoldMs(StepPressBox, StepHoldBox, StepHoldCustom));
                click.FromCheck = aims ? clickCheck!.Clone() : null;
                return click;
            }

            case TypeDrag:
            {
                bool fromAims = StepDragFromAimBox.SelectedIndex == 1, toAims = StepDragToAimBox.SelectedIndex == 1;
                if ((fromAims && dragFromCheck == null) || (toAims && dragToCheck == null)) { validationMessage = NoCheck; return null; }
                var drag = MacroStep.MakeDrag(
                    (ClickButton)Math.Clamp(StepDragButtonBox.SelectedIndex, 0, 2),
                    new PixelPoint((int)Dec(StepDragFromX), (int)Dec(StepDragFromY)),
                    new PixelPoint((int)Dec(StepDragToX), (int)Dec(StepDragToY)),
                    (int)Dec(StepDragDuration));
                drag.FromCheck = fromAims ? dragFromCheck!.Clone() : null;
                drag.ToCheck = toAims ? dragToCheck!.Clone() : null;
                return drag;
            }

            case TypeKey:
                if (stepKey == Key.None)
                {
                    validationMessage = "Click the key box in the editor and press a key first.";
                    return null;
                }
                return MacroStep.MakeKey(stepKey,
                    ReadModifiers(StepModCtrl, StepModShift, StepModAlt, StepModMeta),
                    (int)Dec(StepKeyHold));

            case TypeElse:
                return MacroStep.Make(StepKind.Else);

            case TypeFor:
                return StepForUnit.SelectedIndex == 1
                    ? MacroStep.MakeForSeconds((int)Math.Round(Dec(StepForAmount) * 1000))
                    : MacroStep.MakeForTimes((int)Math.Round(Dec(StepForAmount)));

            case TypeGoTo:
                return MacroStep.MakeGoTo((int)Dec(StepGoToLine));

            case TypeStop:
                return MacroStep.Make(StepKind.Stop);

            default:
                return MacroStep.MakeWait((int)Dec(StepWaitMs));
        }
    }

    async System.Threading.Tasks.Task AddStepAsync(bool replaceSelected)
    {
        validationMessage = null;
        int selected = StepList.SelectedIndex;
        bool replacing = replaceSelected && selected >= 0 && selected < macro.Steps.Count;
        int type = StepTypeBox.SelectedIndex;

        MacroStep? step;
        if (type is TypeIf or TypeElseIf or TypeWhile)
        {
            // Replacing a line that has a condition starts from it; anything else starts fresh.
            var existing = replacing ? macro.Steps[selected] : null;
            step = await EditConditionAsync(type == TypeWhile ? StepKind.While : type == TypeIf ? StepKind.If : StepKind.ElseIf,
                existing?.Condition != null ? existing : null);
            if (step == null) return;
        }
        else
        {
            step = BuildStepFromEditor();
            if (step == null) { UpdateControls(); return; }
        }

        if (replacing) ReplaceStep(selected, step);
        else InsertStep(step);
    }

    /// <summary>
    /// Opens the condition editor for an If, Else if or While line, starting from
    /// <paramref name="from"/>'s condition (and loop settings) when given. Null if cancelled.
    /// </summary>
    async System.Threading.Tasks.Task<MacroStep?> EditConditionAsync(StepKind kind, MacroStep? from)
    {
        bool isWhile = kind == StepKind.While;
        var result = await ConditionWindow.EditAsync(this, backend, MacroBlocks.Name(kind), from?.Condition?.Clone(), isWhile,
            from?.Kind == StepKind.While ? from.IntervalMs : MacroStep.DefaultIntervalMs,
            from?.Kind == StepKind.While ? from.LimitMs : MacroStep.DefaultLimitMs);
        if (result == null) return null;
        return isWhile
            ? MacroStep.MakeWhile(result.Condition, result.IntervalMs, result.LimitMs)
            : MacroStep.MakeIf(result.Condition, elseIf: kind == StepKind.ElseIf);
    }

    /// <summary>Opens the screen check editor for a click or drag end; null if cancelled.</summary>
    async System.Threading.Tasks.Task<MacroStep?> EditAimAsync(MacroStep? current) =>
        await CheckStepWindow.EditAsync(this, backend, current?.Clone(), aim: true);

    /// <summary>
    /// Double-clicking an If, Else if or While opens its condition. Any other line is put in the
    /// editor below, ready to change and Replace.
    /// </summary>
    async System.Threading.Tasks.Task EditSelectedStepAsync()
    {
        int selected = StepList.SelectedIndex;
        if (IsBusy || IsRecording || selected < 0 || selected >= macro.Steps.Count) return;
        var step = macro.Steps[selected];
        switch (step.Kind)
        {
            case StepKind.If or StepKind.ElseIf or StepKind.While:
                StepTypeBox.SelectedIndex = step.Kind switch { StepKind.If => TypeIf, StepKind.ElseIf => TypeElseIf, _ => TypeWhile };
                var changed = await EditConditionAsync(step.Kind, step);
                if (changed != null) ReplaceStep(selected, changed);
                break;
            case StepKind.Click:
                StepTypeBox.SelectedIndex = TypeClick;
                StepClickButtonBox.SelectedIndex = (int)step.Button;
                StepClickTypeBox.SelectedIndex = step.ClickCount > 1 ? 1 : 0;
                SetValue(StepClickX, step.FromX);
                SetValue(StepClickY, step.FromY);
                SetHold(StepPressBox, StepHoldBox, StepHoldCustom, step.HoldMs);
                if (step.FromCheck != null) clickCheck = step.FromCheck.Clone();
                StepClickAimBox.SelectedIndex = step.FromCheck != null ? 1 : 0;
                break;
            case StepKind.Drag:
                StepTypeBox.SelectedIndex = TypeDrag;
                StepDragButtonBox.SelectedIndex = (int)step.Button;
                SetValue(StepDragFromX, step.FromX);
                SetValue(StepDragFromY, step.FromY);
                SetValue(StepDragToX, step.ToX);
                SetValue(StepDragToY, step.ToY);
                SetValue(StepDragDuration, step.DurationMs);
                if (step.FromCheck != null) dragFromCheck = step.FromCheck.Clone();
                if (step.ToCheck != null) dragToCheck = step.ToCheck.Clone();
                StepDragFromAimBox.SelectedIndex = step.FromCheck != null ? 1 : 0;
                StepDragToAimBox.SelectedIndex = step.ToCheck != null ? 1 : 0;
                break;
            case StepKind.Key:
                StepTypeBox.SelectedIndex = TypeKey;
                stepKey = KeyMap.IsSupported(step.Key) ? step.Key : Key.None;
                StepModCtrl.IsChecked = step.Modifiers.HasFlag(Modifiers.Control);
                StepModShift.IsChecked = step.Modifiers.HasFlag(Modifiers.Shift);
                StepModAlt.IsChecked = step.Modifiers.HasFlag(Modifiers.Alt);
                StepModMeta.IsChecked = step.Modifiers.HasFlag(Modifiers.Meta);
                SetValue(StepKeyHold, step.DurationMs);
                UpdateKeyCaptureText();
                break;
            case StepKind.Wait:
                StepTypeBox.SelectedIndex = TypeWait;
                SetValue(StepWaitMs, step.DurationMs);
                break;
            case StepKind.For:
                StepTypeBox.SelectedIndex = TypeFor;
                StepForUnit.SelectedIndex = step.ForSeconds ? 1 : 0;
                StepForAmount.Value = step.ForSeconds ? step.DurationMs / 1000m : step.Count;
                break;
            case StepKind.GoTo:
                StepTypeBox.SelectedIndex = TypeGoTo;
                StepGoToLine.Value = Math.Clamp(step.Line, 1, (int)StepGoToLine.Maximum);
                break;
            default:
                return;
        }
        transientMessage = "Line " + (selected + 1) + " is in the editor below. Change it, then press Replace.";
        UpdateControls();
    }

    /// <summary>
    /// Puts <paramref name="step"/> below the selected line, or at the end when nothing is selected.
    /// An If, While or For brings its End line along, and stays selected so the next lines go inside it.
    /// </summary>
    void InsertStep(MacroStep step)
    {
        Remember(step.OpensBlock ? "adding " + MacroBlocks.Name(step.Kind) : "adding a line");
        int selected = StepList.SelectedIndex;
        int at = selected >= 0 && selected < macro.Steps.Count ? selected + 1 : macro.Steps.Count;
        var added = new List<MacroStep> { step };
        if (step.Closer is { } closer) added.Add(MacroStep.Make(closer));

        macro.Steps.InsertRange(at, added);
        int shift = added.Count;
        Macro.Renumber(macro.Steps, n => n > at ? n + shift : n);
        RebuildStepRows(at);
        SaveCurrentMacro();
        UpdateControls();
    }

    /// <summary>
    /// Puts <paramref name="step"/> in place of line <paramref name="index"/>. Turning one kind of
    /// block into another (an If into a While, say) changes its End line to match; turning a plain
    /// line into a block adds its End line right below.
    /// </summary>
    void ReplaceStep(int index, MacroStep step)
    {
        Remember("replacing line " + (index + 1));
        var old = macro.Steps[index];
        int oldEnd = old.OpensBlock ? MacroBlocks.Analyze(macro.Steps).End[index] : -1;
        macro.Steps[index] = step;
        if (step.Closer is { } closer)
        {
            if (oldEnd >= 0) macro.Steps[oldEnd] = MacroStep.Make(closer);
            else if (!old.OpensBlock)
            {
                macro.Steps.Insert(index + 1, MacroStep.Make(closer));
                Macro.Renumber(macro.Steps, n => n > index + 1 ? n + 1 : n);
            }
        }
        RebuildStepRows(index);
        SaveCurrentMacro();
        UpdateControls();
    }

    void MoveStep(int offset)
    {
        int index = StepList.SelectedIndex;
        int target = index + offset;
        if (index < 0 || target < 0 || target >= macro.Steps.Count) return;
        Remember("moving line " + (index + 1));

        (macro.Steps[index], macro.Steps[target]) = (macro.Steps[target], macro.Steps[index]);
        int a = index + 1, b = target + 1;
        Macro.Renumber(macro.Steps, n => n == a ? b : n == b ? a : n);
        RebuildStepRows(target);
        SaveCurrentMacro();
        UpdateControls();
    }

    /// <summary>
    /// Deletes the selected line. For an If, While or For (or its End line), the block's frame goes
    /// with it (Else if, Else and End lines too) and the lines inside move out a level.
    /// </summary>
    void DeleteStep()
    {
        int index = StepList.SelectedIndex;
        if (index < 0 || index >= macro.Steps.Count) return;
        Remember("deleting line " + (index + 1));

        var remove = new SortedSet<int> { index };
        var blocks = MacroBlocks.Analyze(macro.Steps);
        var step = macro.Steps[index];
        int opener = step.OpensBlock ? index
            : step.Kind is StepKind.EndIf or StepKind.EndWhile or StepKind.EndFor ? blocks.Open[index]
            : -1;
        if (opener >= 0 && blocks.End[opener] >= 0)
        {
            remove.Add(opener);
            for (int part = opener; part >= 0 && part != blocks.End[opener]; part = blocks.Next[part])
                remove.Add(part);
            remove.Add(blocks.End[opener]);
        }

        // Every old line's new number; a removed line's jumps land on the line that follows it.
        var newLine = new int[macro.Steps.Count + 2];
        int kept = 0;
        for (int i = 0; i < macro.Steps.Count; i++)
        {
            if (!remove.Contains(i)) kept++;
            newLine[i + 1] = remove.Contains(i) ? kept + 1 : kept;
        }
        newLine[macro.Steps.Count + 1] = kept + 1;

        foreach (int i in remove.Reverse()) macro.Steps.RemoveAt(i);
        Macro.Renumber(macro.Steps, n => n >= 1 && n < newLine.Length ? newLine[n] : n);
        // The line above what was deleted stays selected, ready for the next edit there.
        RebuildStepRows(Math.Min(Math.Max(0, remove.Min - 1), macro.Steps.Count - 1));
        SaveCurrentMacro();
        UpdateControls();
    }

    void ClearSteps()
    {
        if (macro.Steps.Count == 0) return;
        Remember("clearing the macro");
        macro.Steps.Clear();
        macro.Name = null;
        RebuildStepRows();
        SaveCurrentMacro();
        UpdateControls();
    }

    // ---------------------------------------------------------------- undo

    MacroSnapshot Snapshot(string what) =>
        new(macro.Steps.Select(step => step.Clone()).ToList(), macro.Name, StepList.SelectedIndex, what);

    /// <summary>Keeps the lines as they are now, before a change described by <paramref name="what"/>, for Undo.</summary>
    void Remember(string what)
    {
        undoStack.Add(Snapshot(what));
        if (undoStack.Count > UndoLimit) undoStack.RemoveAt(0);
        redoStack.Clear();
    }

    void UndoMacro() => Swap(undoStack, redoStack, "Undid ");

    void RedoMacro() => Swap(redoStack, undoStack, "Redid ");

    /// <summary>Puts back the newest snapshot of <paramref name="from"/>, keeping the lines it replaces in <paramref name="to"/>.</summary>
    void Swap(List<MacroSnapshot> from, List<MacroSnapshot> to, string verb)
    {
        if (from.Count == 0 || IsBusy || IsRecording) return;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Snapshot(snapshot.What));

        macro.Steps.Clear();
        macro.Steps.AddRange(snapshot.Steps.Select(step => step.Clone()));
        macro.Name = snapshot.Name;
        RebuildStepRows(Math.Min(snapshot.Selected, macro.Steps.Count - 1));
        SaveCurrentMacro();
        validationMessage = null;
        transientMessage = verb + snapshot.What + ".";
        UpdateControls();
    }

    /// <summary>Ctrl+Z and Ctrl+Y (or Ctrl+Shift+Z; Cmd on a Mac) undo and redo macro changes, unless a text box takes them.</summary>
    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Mode != AutomationMode.Macro) return;
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!command || FocusManager?.GetFocusedElement() is TextBox) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) { UndoMacro(); e.Handled = true; }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { RedoMacro(); e.Handled = true; }
    }

    // ---------------------------------------------------------------- saved macros

    static FilePickerFileType MacroFileType => new("Auto Clicker macro") { Patterns = ["*.json"] };

    string? SelectedLibraryName =>
        LibraryList.SelectedIndex >= 0 && LibraryList.SelectedIndex < libraryRows.Count ? libraryRows[LibraryList.SelectedIndex] : null;

    void RefreshLibrary(string? select = null)
    {
        string? keep = select ?? SelectedLibraryName;
        libraryRows.Clear();
        foreach (var name in MacroLibrary.Names()) libraryRows.Add(name);
        if (keep != null)
        {
            int at = libraryRows.IndexOf(keep);
            if (at >= 0) LibraryList.SelectedIndex = at;
        }
        UpdateControls();
    }

    /// <summary>Save…: asks for a name and keeps the macro in Saved macros under it.</summary>
    async System.Threading.Tasks.Task SaveMacroAsync()
    {
        var name = await PromptAsync("Save macro", "Save this macro as", macro.Name ?? SelectedLibraryName ?? "",
            "Save", text => MacroLibrary.CleanName(text, out var problem) == null ? problem : null);
        if (name == null) return;
        name = MacroLibrary.CleanName(name, out _)!;

        if (MacroLibrary.Exists(name) &&
            !await ConfirmAsync("Save macro", "Replace \"" + name + "\"?",
                "A saved macro already has that name. Saving replaces it with this one.", "Replace", "Cancel"))
            return;

        try
        {
            MacroLibrary.Save(name, macro);
            macro.Name = name;
            SaveCurrentMacro();
            transientMessage = "Saved \"" + name + "\".";
            RefreshLibrary(name);
        }
        catch (Exception ex)
        {
            transientMessage = "Couldn't save the macro: " + ex.Message;
        }
        UpdateControls();
    }

    /// <summary>Whether the macro being edited is empty or exactly as saved, so replacing it loses nothing.</summary>
    bool EditedMacroIsSafe()
    {
        if (macro.Steps.Count == 0) return true;
        if (string.IsNullOrEmpty(macro.Name) || !MacroLibrary.Exists(macro.Name)) return false;
        var saved = MacroLibrary.Load(macro.Name);
        return saved != null && saved.ToJson() == new Macro { Name = macro.Name, Steps = macro.Steps }.ToJson();
    }

    async System.Threading.Tasks.Task LoadFromLibraryAsync()
    {
        if (SelectedLibraryName is not { } name || IsBusy || IsRecording) return;
        if (!EditedMacroIsSafe() &&
            !await ConfirmAsync("Load macro", "Replace the macro being edited?",
                "The steps above haven't been saved. Loading \"" + name + "\" replaces them.", "Load", "Cancel"))
            return;

        var loaded = MacroLibrary.Load(name);
        if (loaded == null)
        {
            transientMessage = "Couldn't read \"" + name + "\". It may be from a newer version.";
            UpdateControls();
            return;
        }
        // Keep the file in the current format once it's been brought up to date.
        if (loaded.UpgradeNote != null)
        {
            try { MacroLibrary.Save(name, loaded); }
            catch (Exception) { /* it still loads; it'll just be upgraded again next time */ }
        }

        Remember("loading \"" + name + "\"");
        macro.Steps.Clear();
        macro.Steps.AddRange(loaded.Steps);
        macro.Name = name;
        transientMessage = "Loaded \"" + name + "\"." + (loaded.UpgradeNote is { } note ? " " + note : "");
        RebuildStepRows();
        SaveCurrentMacro();
        UpdateControls();
    }

    async System.Threading.Tasks.Task RenameInLibraryAsync()
    {
        if (SelectedLibraryName is not { } from) return;
        var to = await PromptAsync("Rename macro", "New name for \"" + from + "\"", from, "Rename", text =>
        {
            var clean = MacroLibrary.CleanName(text, out var problem);
            if (clean == null) return problem;
            return !clean.Equals(from, StringComparison.OrdinalIgnoreCase) && MacroLibrary.Exists(clean)
                ? "Another saved macro already has that name."
                : null;
        });
        if (to == null) return;
        to = MacroLibrary.CleanName(to, out _)!;
        if (to == from) return;

        try
        {
            MacroLibrary.Rename(from, to);
            if (macro.Name == from)
            {
                macro.Name = to;
                SaveCurrentMacro();
            }
            transientMessage = "Renamed \"" + from + "\" to \"" + to + "\".";
            RefreshLibrary(to);
        }
        catch (Exception ex)
        {
            transientMessage = "Couldn't rename it: " + ex.Message;
        }
        UpdateControls();
    }

    async System.Threading.Tasks.Task DeleteFromLibraryAsync()
    {
        if (SelectedLibraryName is not { } name) return;
        if (!await ConfirmAsync("Delete macro", "Delete \"" + name + "\"?",
                "It's removed from Saved macros for good. The macro being edited above isn't affected.", "Delete", "Cancel"))
            return;
        try
        {
            MacroLibrary.Delete(name);
            transientMessage = "Deleted \"" + name + "\".";
        }
        catch (Exception ex)
        {
            transientMessage = "Couldn't delete it: " + ex.Message;
        }
        RefreshLibrary();
    }

    async System.Threading.Tasks.Task DownloadFromLibraryAsync()
    {
        if (SelectedLibraryName is not { } name) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download macro",
            SuggestedFileName = name + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [MacroFileType]
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            File.Copy(MacroLibrary.PathOf(name), path, overwrite: true);
            transientMessage = "Downloaded \"" + name + "\" to " + path + ".";
        }
        catch (Exception ex)
        {
            transientMessage = "Couldn't download it: " + ex.Message;
        }
        UpdateControls();
    }

    async System.Threading.Tasks.Task ImportToLibraryAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import macros",
            AllowMultiple = true,
            FileTypeFilter = [MacroFileType]
        });
        if (files.Count == 0) return;

        var imported = new List<string>();
        var failed = new List<string>();
        string? notes = null;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            var loaded = path == null ? null : Macro.Load(path);
            if (loaded == null)
            {
                failed.Add(file.Name);
                continue;
            }
            string baseName = MacroLibrary.CleanName(Path.GetFileNameWithoutExtension(file.Name), out _)
                              ?? MacroLibrary.CleanName(loaded.Name, out _)
                              ?? "Imported macro";
            string name = MacroLibrary.FreeName(baseName);
            try
            {
                MacroLibrary.Save(name, loaded);
                imported.Add(name);
                if (loaded.UpgradeNote != null) notes = loaded.UpgradeNote;
            }
            catch (Exception)
            {
                failed.Add(file.Name);
            }
        }

        var parts = new List<string>();
        if (imported.Count > 0) parts.Add("Imported " + string.Join(", ", imported.Select(n => "\"" + n + "\"")) + ".");
        if (failed.Count > 0) parts.Add(string.Join(", ", failed) + (failed.Count == 1 ? " isn't" : " aren't") + " a macro this version can read.");
        if (notes != null) parts.Add(notes);
        transientMessage = string.Join(" ", parts);
        RefreshLibrary(imported.LastOrDefault());
    }

    /// <summary>The macro being edited, kept between sessions.</summary>
    static string CurrentMacroPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoClicker", CurrentMacroFile);

    /// <summary>Where the macro being edited was kept before 3.0, inside what's now the saved macros folder.</summary>
    static string OldCurrentMacroPath => Path.Combine(Macro.DefaultFolder, MacroLibrary.OldWorkingCopyName + ".json");

    void LoadCurrentMacro()
    {
        var loaded = Macro.Load(CurrentMacroPath);
        if (loaded == null && File.Exists(OldCurrentMacroPath))
        {
            loaded = Macro.Load(OldCurrentMacroPath);
            if (loaded != null)
            {
                // Move it out of the saved macros folder, so it doesn't show up as a saved macro.
                try
                {
                    loaded.Save(CurrentMacroPath);
                    File.Delete(OldCurrentMacroPath);
                }
                catch (Exception) { /* try again next start */ }
            }
        }
        if (loaded != null)
        {
            macro.Steps.AddRange(loaded.Steps);
            macro.Name = loaded.Name;
            if (loaded.UpgradeNote != null)
            {
                transientMessage = "The macro was updated for this version. " + loaded.UpgradeNote;
                SaveCurrentMacro();
            }
        }
        RebuildStepRows();
    }

    void SaveCurrentMacro()
    {
        try { macro.Save(CurrentMacroPath); }
        catch (Exception) { /* the working copy is a convenience; Save… reports real failures */ }
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
        ToolTip.SetTip(yesButton, "Goes ahead (Enter).");
        yesButton.Classes.Add("accent");
        var noButton = new Button { Content = no, MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsCancel = true };
        ToolTip.SetTip(noButton, "Leaves everything as it is (Esc).");

        var dialog = new Window
        {
            Title = title,
            Icon = Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            CanMinimize = false,
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

    /// <summary>
    /// Asks for a line of text. <paramref name="problem"/> says what's wrong with an answer (null
    /// when it's fine), shown under the box. Returns the text, or null if cancelled.
    /// </summary>
    async System.Threading.Tasks.Task<string?> PromptAsync(string title, string heading, string initial, string yes,
        Func<string, string?> problem)
    {
        var box = new TextBox { Text = initial, MaxLength = MacroLibrary.MaxNameLength };
        ToolTip.SetTip(box, "Up to " + MacroLibrary.MaxNameLength + " characters. Letters, numbers, spaces and most punctuation work; / \\ : * ? \" < > | don't.");
        var error = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false
        };
        var yesButton = new Button { Content = yes, MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsDefault = true };
        ToolTip.SetTip(yesButton, "Uses this name (Enter).");
        yesButton.Classes.Add("accent");
        var noButton = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsCancel = true };
        ToolTip.SetTip(noButton, "Closes without changing anything (Esc).");

        var dialog = new Window
        {
            Title = title,
            Icon = Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            CanMinimize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Width = 360,
                Children =
                {
                    new TextBlock { Text = heading, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    box,
                    error,
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
        yesButton.Click += (_, _) =>
        {
            string text = box.Text ?? "";
            if (problem(text) is { } trouble)
            {
                error.Text = trouble;
                error.IsVisible = true;
                return;
            }
            dialog.Close(text);
        };
        noButton.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await dialog.ShowDialog<string?>(this);
    }

    // ---------------------------------------------------------------- pick a point

    /// <summary>The Options card's pick settings, handed to every picker.</summary>
    void ApplyPickSettings()
    {
        ScreenPicker.Seconds = (int)Math.Clamp(Dec(PickSecondsBox), ScreenPicker.MinSeconds, ScreenPicker.MaxSeconds);
        ScreenPicker.PickOnClick = PickOnClickBox.IsChecked == true;
    }

    /// <summary>
    /// Fills an X/Y pair with a spot picked on screen. All picking goes through
    /// <see cref="ScreenPicker"/>, so every Pick hides the windows and shows its countdown alike.
    /// </summary>
    async void StartPick(NumericUpDown x, NumericUpDown y, Button button)
    {
        if (picking) return;
        picking = true;
        try
        {
            void Show(PixelPoint at)
            {
                x.Value = Math.Clamp(at.X, (int)x.Minimum, (int)x.Maximum);
                y.Value = Math.Clamp(at.Y, (int)y.Minimum, (int)y.Maximum);
            }
            Show((await ScreenPicker.PickAsync(backend, Show)).Point);
        }
        finally
        {
            picking = false;
        }
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
        if (slot.Binding.IsControl)
        {
            ControlMainRun(slot.Binding);
            return;
        }
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

    /// <summary>A pause, step or go-to-line key: acts on the run started from the window.</summary>
    void ControlMainRun(KeyBinding binding)
    {
        switch (binding.Action)
        {
            case BindingAction.Pause:
                if (mainRun.IsRunning) TogglePause();
                break;

            case BindingAction.Step:
                // Only while paused; a running macro carries on as it is.
                if (mainRun.IsPaused && mainRun.Macro != null) mainRun.Control.Step();
                break;

            case BindingAction.GoToLine:
            {
                if (mainRun.IsRunning)
                {
                    if (mainRun.Macro == null) break;   // clicks, drags and keys have no lines
                    int count = mainRun.Setup?.Steps.Count ?? 0;
                    if (binding.Line > count)
                    {
                        transientMessage = BindingLabel(binding) + ": the running macro has only " + count + (count == 1 ? " line." : " lines.");
                        break;
                    }
                    mainRun.Control.JumpTo(binding.Line - 1);
                    break;
                }
                if (Mode != AutomationMode.Macro)
                {
                    transientMessage = BindingLabel(binding) + " goes to a line of the macro. Switch Automate to Macro first.";
                    break;
                }
                if (binding.Line > macro.Steps.Count)
                {
                    transientMessage = BindingLabel(binding) + ": the macro has only " + macro.Steps.Count + (macro.Steps.Count == 1 ? " line." : " lines.");
                    break;
                }
                // Not running: start it from that line, straight away like the start/stop key.
                countdownTimer.Stop();
                StartNow(binding.Line - 1);
                break;
            }
        }
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
                         (binding.IsControl ? binding.ControlText : binding.Setup.Describe() +
                                                                    (binding.RunAlongside ? " · alongside" : ""));
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
            BindingActionBox.SelectedIndex = (int)binding.Action;
            SetValue(BindingLine, binding.Line);
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

        var action = ChosenBindingAction;
        var binding = new KeyBinding
        {
            Name = (BindingName.Text ?? "").Trim(),
            Key = bindingKey,
            Modifiers = bindingModifiers,
            Action = action,
            Line = (int)Dec(BindingLine),
            RunAlongside = action == BindingAction.StartStop && BindingAlongside.IsChecked == true,
            // Control keys act on the window's run and keep no setup of their own.
            Setup = action == BindingAction.StartStop ? CaptureSetup() : new RunSetup()
        };

        string? problem = bindingKey == Key.None ? "Click the key box next to the name and press the key that should start it."
            : binding.SameKeyAs(HotkeyKey, Modifiers.None) ? HotkeyName + " is already the start/stop key. Pick another key."
            : bindings.FirstOrDefault(s => s != existing && s.Binding.SameKeyAs(bindingKey, bindingModifiers)) is { } taken
                ? binding.KeyName + " is already used by " + BindingLabel(taken.Binding) + ". Pick another key, or select that one and Replace it."
            : binding.IsControl ? null
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
        transientMessage = (existing == null ? "Added " : "Updated ") + BindingLabel(binding) + (binding.IsControl
            ? ". Press it in any window to " + char.ToLowerInvariant(binding.ControlText[0]) + binding.ControlText[1..] + "."
            : ". Its key starts and stops it anywhere.");
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
        SetValue(PickSecondsBox, Math.Clamp(settings.GetInt("PickSeconds", ScreenPicker.DefaultSeconds), ScreenPicker.MinSeconds, ScreenPicker.MaxSeconds));
        PickOnClickBox.IsChecked = settings.GetBool("PickOnClick", false);
        SetValue(StartDelayBox, settings.GetDecimal("StartDelaySeconds", DefaultStartDelaySeconds));
        SetValue(ResumeDelayBox, settings.GetDecimal("ResumeDelaySeconds", 0));
        ApplyPickSettings();
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
        settings.Set("PickSeconds", ScreenPicker.Seconds);
        settings.Set("PickOnClick", ScreenPicker.PickOnClick);
        settings.Set("StartDelaySeconds", Dec(StartDelayBox));
        settings.Set("ResumeDelaySeconds", Dec(ResumeDelayBox));
        settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        countdownTimer.Stop();
        recordCountdownTimer.Stop();
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
