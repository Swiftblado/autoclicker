using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using AutoClicker.Core;
using AutoClicker.Hotkeys;
using AutoClicker.Input;
using AutoClicker.Recording;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AutoClicker.Views;

enum AutomationMode { Clicks, Drags, Keys, Macro }

public partial class MainWindow : Window
{
    const int StartCountdownSeconds = 3;
    const int RecordCountdownSeconds = 2;
    const string CurrentMacroFile = "current.json";

    static readonly Key[] HotkeyChoices =
    [
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
        Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12
    ];

    readonly IInputBackend backend = InputBackend.Create();
    readonly IHotkeyService hotkeys = HotkeyService.Create();
    readonly IInputRecorder recorder = InputRecorder.Create();
    readonly Runner runner = new();
    readonly Settings settings = Settings.Load();

    readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer pickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer recordCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Macro macro = new();
    readonly ObservableCollection<string> stepRows = [];
    readonly List<RecordedEvent> recordedEvents = [];
    readonly object recordLock = new();

    MacroAction? runningMacro;
    Key chosenKey = Key.None;
    Key stepKey = Key.None;
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

        ModAlt.Content = StepModAlt.Content = KeyMap.AltName;
        ModMeta.Content = StepModMeta.Content = KeyMap.MetaName;
        HotkeyBox.ItemsSource = HotkeyChoices.Select(KeyMap.Name).ToList();
        StepList.ItemsSource = stepRows;

        ModeBox.SelectionChanged += (_, _) => { UpdateControls(); };
        StepTypeBox.SelectionChanged += (_, _) => UpdateControls();
        StepList.SelectionChanged += (_, _) => UpdateControls();
        PositionFixed.IsCheckedChanged += OnSettingChanged;
        PositionCursor.IsCheckedChanged += OnSettingChanged;
        RepeatForever.IsCheckedChanged += OnSettingChanged;
        RepeatLimited.IsCheckedChanged += OnSettingChanged;
        TopMostBox.IsCheckedChanged += (_, _) => Topmost = TopMostBox.IsChecked == true;
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

        KeyCapture.AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        KeyCapture.PointerPressed += (_, _) => KeyCapture.Focus();
        StepKeyCapture.AddHandler(KeyDownEvent, OnStepCaptureKeyDown, RoutingStrategies.Tunnel);
        StepKeyCapture.PointerPressed += (_, _) => StepKeyCapture.Focus();

        uiTimer.Tick += (_, _) => RefreshState();
        countdownTimer.Tick += (_, _) => CountdownTick();
        pickTimer.Tick += (_, _) => PickTick();
        recordCountdownTimer.Tick += (_, _) => RecordCountdownTick();
        recorder.Recorded += OnRecorded;

        LoadSettings();
        LoadCurrentMacro();
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

    // ---------------------------------------------------------------- state

    AutomationMode Mode => (AutomationMode)Math.Clamp(ModeBox.SelectedIndex, 0, 3);

    bool IsBusy => runner.IsRunning || countdownTimer.IsEnabled;

    bool IsRecording => recorder.IsRecording || recordCountdownTimer.IsEnabled;

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

        StartButton.IsEnabled = !busy && backend.IsAvailable;
        StopButton.IsEnabled = IsBusy;
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

        bool running = runner.IsRunning;
        long count = runner.Count;
        if (running != wasRunning || count != lastCount)
        {
            wasRunning = running;
            lastCount = count;
            if (!running) runningMacro = null;
            UpdateControls();
        }
        else if (running && Mode == AutomationMode.Macro)
        {
            RefreshStatus();   // step counter ticks along while the pass count holds still
        }
    }

    void RefreshStatus()
    {
        if (transientMessage != null) { StatusText.Text = transientMessage; return; }
        if (validationMessage != null) { StatusText.Text = validationMessage; return; }

        long count = runner.Count;
        string text;

        if (recordCountdownTimer.IsEnabled)
            text = "Recording starts in " + recordCountdown + "… get ready.";
        else if (recorder.IsRecording)
            text = "Recording your clicks and keys. Press " + HotkeyName + " to finish.";
        else if (countdownTimer.IsEnabled)
            text = "Starting in " + countdown + "… switch to your target window.";
        else if (runner.IsRunning)
            text = "Running · " + Counted(count) + "." + StepProgress() +
                   (hotkeyError == null ? " Press " + HotkeyName + " to stop." : "");
        else if (count > 0)
            text = "Stopped · " + Counted(count) + ".";
        else if (hotkeyError != null)
            text = HotkeyName + " isn't available as a start/stop key (" + hotkeyError +
                   "). Use the buttons below or pick another key.";
        else
            text = "Ready. Press " + HotkeyName + " from any window to start or stop.";

        StatusText.Text = text;
    }

    string Counted(long count)
    {
        string noun = Mode switch
        {
            AutomationMode.Clicks => count == 1 ? "click" : "clicks",
            AutomationMode.Drags => count == 1 ? "drag" : "drags",
            AutomationMode.Keys => count == 1 ? "key press" : "key presses",
            _ => count == 1 ? "pass" : "passes"
        };
        return count.ToString("N0", CultureInfo.CurrentCulture) + " " + noun;
    }

    string StepProgress()
    {
        var running = runningMacro;
        if (running == null) return "";
        int step = running.CurrentStepIndex;
        return step >= 0 ? " Step " + (step + 1) + " of " + macro.Steps.Count + "." : "";
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
        if (IsRecording) StopRecording();
        else if (IsBusy) StopEverything();
        else StartNow();
    }

    void StartNow()
    {
        var action = BuildAction();
        if (action == null) { UpdateControls(); return; }

        double interval = (double)(Dec(Hours) * 3600000m + Dec(Minutes) * 60000m +
                                   Dec(Seconds) * 1000m + Dec(Millis));
        long repeat = RepeatLimited.IsChecked == true ? (long)Dec(RepeatCount) : 0;
        runningMacro = action as MacroAction;
        SaveSettings();
        runner.Start(action, interval, (int)Dec(Jitter), repeat);
        UpdateControls();
    }

    void StopEverything()
    {
        countdownTimer.Stop();
        runner.Stop();
        runningMacro = null;
        UpdateControls();
    }

    IInputAction? BuildAction()
    {
        validationMessage = null;
        transientMessage = null;

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

        switch (Mode)
        {
            case AutomationMode.Clicks:
                return new MouseClickAction(
                    backend,
                    (ClickButton)Math.Clamp(ButtonBox.SelectedIndex, 0, 2),
                    ClickTypeBox.SelectedIndex == 1 ? 2 : 1,
                    PositionFixed.IsChecked == true,
                    new PixelPoint((int)Dec(PositionX), (int)Dec(PositionY)));

            case AutomationMode.Drags:
                return new DragAction(
                    backend,
                    (ClickButton)Math.Clamp(DragButtonBox.SelectedIndex, 0, 2),
                    new PixelPoint((int)Dec(DragFromX), (int)Dec(DragFromY)),
                    new PixelPoint((int)Dec(DragToX), (int)Dec(DragToY)),
                    (int)Dec(DragDuration));

            case AutomationMode.Keys:
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
                return new KeyPressAction(backend, chosenKey, ReadModifiers(ModCtrl, ModShift, ModAlt, ModMeta),
                    (int)Dec(HoldMs));

            default:
                if (macro.Steps.Count == 0)
                {
                    validationMessage = "The macro is empty. Record one, or add steps below.";
                    return null;
                }
                return new MacroAction(backend, macro);
        }
    }

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
                    new PixelPoint((int)Dec(StepClickX), (int)Dec(StepClickY)));

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
        SetIndex(ModeBox, settings.GetInt("Mode", 0), 4);
        SetIndex(ButtonBox, settings.GetInt("MouseButton", 0), 3);
        SetIndex(ClickTypeBox, settings.GetInt("ClickType", 0), 2);
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
        Topmost = TopMostBox.IsChecked == true;
    }

    void SaveSettings()
    {
        settings.Set("Mode", ModeBox.SelectedIndex);
        settings.Set("MouseButton", ButtonBox.SelectedIndex);
        settings.Set("ClickType", ClickTypeBox.SelectedIndex);
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
        settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        countdownTimer.Stop();
        recordCountdownTimer.Stop();
        pickTimer.Stop();
        uiTimer.Stop();
        runner.Stop();
        recorder.Dispose();
        SaveSettings();
        SaveCurrentMacro();
        hotkeys.Dispose();
        backend.Dispose();
        base.OnClosed(e);
    }
}
