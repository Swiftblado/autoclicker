using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoClicker
{
    internal sealed class MainForm : Form
    {
        const int HotkeyId = 1;
        const int StartCountdownSeconds = 3;
        static readonly string[] ButtonNames = { "Left", "Right", "Middle" };
        static readonly string[] ClickTypes = { "Single", "Double" };

        readonly float scale;
        readonly Runner runner = new Runner();
        readonly Settings settings = Settings.Load();

        RadioButton rbMouse, rbKeyboard;
        GroupBox grpMouse, grpKeyboard, grpTiming, grpOptions;

        ComboBox cboButton, cboClickType;
        RadioButton rbCurrentPos, rbFixedPos;
        NumericUpDown numX, numY;
        Button btnPick;

        KeyCaptureBox keyBox;
        CheckBox chkCtrl, chkShift, chkAlt;
        NumericUpDown numHold;

        NumericUpDown numHours, numMinutes, numSeconds, numMillis, numJitter, numRepeat;
        RadioButton rbRepeatForever, rbRepeatCount;

        ComboBox cboHotkey;
        CheckBox chkTopMost;
        Button btnStart, btnStop;
        Label lblStatus;

        readonly Timer uiTimer = new Timer();
        readonly Timer countdownTimer = new Timer();
        readonly Timer pickTimer = new Timer();
        int countdown;
        int pickTicks;
        bool wasRunning;
        long lastCount;
        bool hotkeyRegistered;
        bool hotkeyFailed;

        public MainForm()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            Text = "Auto Clicker";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(S(10));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            BuildUi();
            LoadSettings();

            uiTimer.Interval = 100;
            uiTimer.Tick += delegate { RefreshState(); };
            uiTimer.Start();

            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { CountdownTick(); };

            pickTimer.Interval = 50;
            pickTimer.Tick += delegate { PickTick(); };

            UpdateControls();
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        // ---------------------------------------------------------------- layout

        void BuildUi()
        {
            SuspendLayout();

            var root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;

            // Action selector
            rbMouse = new RadioButton { Text = "Mouse clicks", AutoSize = true, Checked = true };
            rbKeyboard = new RadioButton { Text = "Key presses", AutoSize = true };
            rbMouse.CheckedChanged += delegate { UpdateControls(); };
            rbKeyboard.CheckedChanged += delegate { UpdateControls(); };
            var actionRow = Row(Lbl("Automate:"), rbMouse, rbKeyboard);
            actionRow.Margin = new Padding(0, 0, 0, S(4));
            root.Controls.Add(actionRow);

            // Mouse
            var mouseGrid = Grid();
            cboButton = Combo(ButtonNames, 90);
            cboClickType = Combo(ClickTypes, 90);
            AddRow(mouseGrid, "Button:", Row(cboButton, Lbl("Click:"), cboClickType));
            rbCurrentPos = new RadioButton { Text = "Wherever the cursor is", AutoSize = true, Checked = true };
            rbFixedPos = new RadioButton { Text = "Fixed spot", AutoSize = true };
            rbFixedPos.CheckedChanged += delegate { UpdateControls(); };
            numX = Num(-32000, 32000, 0, 64);
            numY = Num(-32000, 32000, 0, 64);
            btnPick = new Button { Text = "Pick", AutoSize = true, Margin = new Padding(S(3), S(2), S(3), S(2)) };
            btnPick.Click += delegate { StartPick(); };
            var positionPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty
            };
            positionPanel.Controls.Add(rbCurrentPos);
            positionPanel.Controls.Add(Row(rbFixedPos, Lbl("X"), numX, Lbl("Y"), numY, btnPick));
            AddRow(mouseGrid, "Click at:", positionPanel);
            grpMouse = Group("Mouse", mouseGrid);
            root.Controls.Add(grpMouse);

            // Keyboard
            var keyGrid = Grid();
            keyBox = new KeyCaptureBox { Width = S(150), Margin = new Padding(S(3)) };
            keyBox.KeyCaptured += OnKeyCaptured;
            AddRow(keyGrid, "Key:", Row(keyBox));
            chkCtrl = new CheckBox { Text = "Ctrl", AutoSize = true };
            chkShift = new CheckBox { Text = "Shift", AutoSize = true };
            chkAlt = new CheckBox { Text = "Alt", AutoSize = true };
            AddRow(keyGrid, "With:", Row(chkCtrl, chkShift, chkAlt));
            numHold = Num(0, 10000, 30, 70);
            AddRow(keyGrid, "Hold for:", Row(numHold, Lbl("ms")));
            grpKeyboard = Group("Keyboard", keyGrid);
            root.Controls.Add(grpKeyboard);

            // Timing
            var timingGrid = Grid();
            numHours = Num(0, 23, 0, 46);
            numMinutes = Num(0, 59, 0, 46);
            numSeconds = Num(0, 59, 0, 46);
            numMillis = Num(0, 999, 100, 56);
            AddRow(timingGrid, "Every:", Row(numHours, Lbl("h"), numMinutes, Lbl("m"), numSeconds, Lbl("s"), numMillis, Lbl("ms")));
            numJitter = Num(0, 100000, 0, 70);
            AddRow(timingGrid, "Randomize:", Row(Lbl("±"), numJitter, Lbl("ms")));
            rbRepeatForever = new RadioButton { Text = "Until stopped", AutoSize = true, Checked = true };
            rbRepeatCount = new RadioButton { Text = "", AutoSize = true, Margin = new Padding(S(3), S(3), 0, S(3)) };
            rbRepeatCount.CheckedChanged += delegate { UpdateControls(); };
            numRepeat = Num(1, 10000000, 10, 80);
            numRepeat.ThousandsSeparator = true;
            AddRow(timingGrid, "Repeat:", Row(rbRepeatForever, rbRepeatCount, numRepeat, Lbl("times")));
            grpTiming = Group("Timing", timingGrid);
            root.Controls.Add(grpTiming);

            // Options
            var optionsGrid = Grid();
            var hotkeys = new string[12];
            for (int i = 0; i < 12; i++) hotkeys[i] = "F" + (i + 1);
            cboHotkey = Combo(hotkeys, 60);
            cboHotkey.SelectedIndex = 5;
            cboHotkey.SelectedIndexChanged += delegate { RegisterHotkey(); UpdateControls(); };
            chkTopMost = new CheckBox { Text = "Keep window on top", AutoSize = true };
            chkTopMost.CheckedChanged += delegate { TopMost = chkTopMost.Checked; };
            AddRow(optionsGrid, "Start/stop key:", Row(cboHotkey, chkTopMost));
            grpOptions = Group("Options", optionsGrid);
            root.Controls.Add(grpOptions);

            // Start / Stop
            var buttons = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, S(6), 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            btnStart = BigButton();
            btnStart.Click += delegate { BeginCountdown(); };
            btnStop = BigButton();
            btnStop.Click += delegate { StopRunning(); };
            buttons.Controls.Add(btnStart, 0, 0);
            buttons.Controls.Add(btnStop, 1, 0);
            root.Controls.Add(buttons);

            lblStatus = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(S(3), S(6), S(3), 0)
            };
            root.Controls.Add(lblStatus);

            Controls.Add(root);
            ResumeLayout(true);
        }

        Label Lbl(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(3), 0, S(3), 0) };
        }

        NumericUpDown Num(decimal min, decimal max, decimal value, int width)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Width = S(width),
                TextAlign = HorizontalAlignment.Right,
                Margin = new Padding(S(3))
            };
        }

        ComboBox Combo(string[] items, int width)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(width), Margin = new Padding(S(3)) };
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            return c;
        }

        Button BigButton()
        {
            return new Button
            {
                Dock = DockStyle.Fill,
                Height = S(38),
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
                Margin = new Padding(S(3))
            };
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty
            };
            row.Controls.AddRange(controls);
            return row;
        }

        static TableLayoutPanel Grid()
        {
            var grid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        void AddRow(TableLayoutPanel grid, string label, Control content)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = Lbl(label);
            l.MinimumSize = new Size(S(84), 0);
            grid.Controls.Add(l);
            grid.Controls.Add(content);
        }

        GroupBox Group(string title, Control content)
        {
            var group = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(S(6), S(4), S(6), S(4)),
                Margin = new Padding(0, S(4), 0, S(4))
            };
            group.Controls.Add(content);
            return group;
        }

        // ---------------------------------------------------------------- state

        bool IsBusy { get { return runner.IsRunning || countdownTimer.Enabled; } }

        string HotkeyName { get { return (string)cboHotkey.SelectedItem; } }

        Keys HotkeyKey { get { return Keys.F1 + cboHotkey.SelectedIndex; } }

        void UpdateControls()
        {
            bool busy = IsBusy;
            rbMouse.Enabled = rbKeyboard.Enabled = !busy;
            grpMouse.Enabled = !busy && rbMouse.Checked;
            grpKeyboard.Enabled = !busy && rbKeyboard.Checked;
            grpTiming.Enabled = !busy;
            grpOptions.Enabled = !busy;

            bool fixedPos = rbFixedPos.Checked;
            numX.Enabled = numY.Enabled = btnPick.Enabled = fixedPos;
            numRepeat.Enabled = rbRepeatCount.Checked;

            btnStart.Enabled = !busy;
            btnStop.Enabled = busy;
            btnStart.Text = "Start (" + HotkeyName + ")";
            btnStop.Text = "Stop (" + HotkeyName + ")";
            RefreshStatus();
        }

        void RefreshState()
        {
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
            string noun = rbMouse.Checked
                ? (count == 1 ? "click" : "clicks")
                : (count == 1 ? "key press" : "key presses");
            string counted = count.ToString("N0") + " " + noun;

            string text;
            if (countdownTimer.Enabled)
                text = "Starting in " + countdown + "… switch to your target window.";
            else if (runner.IsRunning)
                text = "Running · " + counted + ". Press " + HotkeyName + " to stop.";
            else if (hotkeyFailed)
                text = HotkeyName + " is already used by another app. Choose a different key.";
            else if (count > 0)
                text = "Stopped · " + counted + ".";
            else
                text = "Ready. Press " + HotkeyName + " anywhere to start or stop.";
            lblStatus.Text = text;
        }

        // ---------------------------------------------------------------- start / stop

        void BeginCountdown()
        {
            if (BuildAction() == null) return;
            // Starting from the button gives you a few seconds to move to the target window,
            // otherwise the clicks or keys would land on this window.
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

            double interval = (double)(numHours.Value * 3600000m + numMinutes.Value * 60000m +
                                       numSeconds.Value * 1000m + numMillis.Value);
            long repeat = rbRepeatCount.Checked ? (long)numRepeat.Value : 0;
            SaveSettings();
            runner.Start(action, interval, (int)numJitter.Value, repeat);
            UpdateControls();
        }

        void StopRunning()
        {
            countdownTimer.Stop();
            runner.Stop();
            UpdateControls();
        }

        IInputAction BuildAction()
        {
            decimal interval = numHours.Value + numMinutes.Value + numSeconds.Value + numMillis.Value;
            if (interval <= 0)
            {
                Warn("Set an interval of at least 1 ms.");
                return null;
            }

            if (rbMouse.Checked)
            {
                return new MouseClickAction(
                    (ClickButton)cboButton.SelectedIndex,
                    cboClickType.SelectedIndex == 1,
                    rbFixedPos.Checked,
                    (int)numX.Value,
                    (int)numY.Value);
            }

            if (keyBox.Key == Keys.None)
            {
                Warn("Click the Key box and press the key you want repeated.");
                keyBox.Focus();
                return null;
            }
            if (keyBox.Key == HotkeyKey)
            {
                Warn("The key to press can't be the same as the start/stop key (" + HotkeyName + ").");
                return null;
            }
            return new KeyPressAction(keyBox.Key, chkCtrl.Checked, chkShift.Checked, chkAlt.Checked, (int)numHold.Value);
        }

        void Warn(string message)
        {
            MessageBox.Show(this, message, "Auto Clicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------------------------------------------------------------- pick location

        void StartPick()
        {
            pickTicks = StartCountdownSeconds * 1000 / pickTimer.Interval;
            pickTimer.Start();
            PickTick();
        }

        void PickTick()
        {
            var pos = Cursor.Position;
            numX.Value = Math.Max(numX.Minimum, Math.Min(numX.Maximum, pos.X));
            numY.Value = Math.Max(numY.Minimum, Math.Min(numY.Maximum, pos.Y));

            pickTicks--;
            if (pickTicks <= 0)
            {
                pickTimer.Stop();
                btnPick.Text = "Pick";
                return;
            }
            int secondsLeft = (pickTicks * pickTimer.Interval + 999) / 1000;
            btnPick.Text = secondsLeft + "…";
        }

        // ---------------------------------------------------------------- keyboard

        void OnKeyCaptured(object sender, KeyEventArgs e)
        {
            chkCtrl.Checked = e.Control;
            chkShift.Checked = e.Shift;
            chkAlt.Checked = e.Alt;
        }

        // ---------------------------------------------------------------- hotkey

        void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            if (hotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(Handle, HotkeyId);
                hotkeyRegistered = false;
            }
            hotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HotkeyId, NativeMethods.MOD_NOREPEAT, (uint)HotkeyKey);
            hotkeyFailed = !hotkeyRegistered;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
            UpdateControls();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleFromHotkey();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            countdownTimer.Stop();
            runner.Stop();
            if (hotkeyRegistered) NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            SaveSettings();
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- settings

        void LoadSettings()
        {
            rbKeyboard.Checked = settings.GetBool("KeyboardMode", false);
            rbMouse.Checked = !rbKeyboard.Checked;
            SetIndex(cboButton, settings.GetInt("MouseButton", 0));
            SetIndex(cboClickType, settings.GetInt("ClickType", 0));
            rbFixedPos.Checked = settings.GetBool("FixedPosition", false);
            rbCurrentPos.Checked = !rbFixedPos.Checked;
            SetValue(numX, settings.GetDecimal("X", 0));
            SetValue(numY, settings.GetDecimal("Y", 0));

            keyBox.Key = (Keys)settings.GetInt("Key", 0);
            chkCtrl.Checked = settings.GetBool("Ctrl", false);
            chkShift.Checked = settings.GetBool("Shift", false);
            chkAlt.Checked = settings.GetBool("Alt", false);
            SetValue(numHold, settings.GetDecimal("HoldMs", 30));

            SetValue(numHours, settings.GetDecimal("Hours", 0));
            SetValue(numMinutes, settings.GetDecimal("Minutes", 0));
            SetValue(numSeconds, settings.GetDecimal("Seconds", 0));
            SetValue(numMillis, settings.GetDecimal("Milliseconds", 100));
            SetValue(numJitter, settings.GetDecimal("JitterMs", 0));
            rbRepeatCount.Checked = settings.GetBool("RepeatLimited", false);
            rbRepeatForever.Checked = !rbRepeatCount.Checked;
            SetValue(numRepeat, settings.GetDecimal("RepeatCount", 10));

            SetIndex(cboHotkey, settings.GetInt("Hotkey", 5));
            chkTopMost.Checked = settings.GetBool("TopMost", false);
        }

        void SaveSettings()
        {
            settings.Set("KeyboardMode", rbKeyboard.Checked);
            settings.Set("MouseButton", cboButton.SelectedIndex);
            settings.Set("ClickType", cboClickType.SelectedIndex);
            settings.Set("FixedPosition", rbFixedPos.Checked);
            settings.Set("X", numX.Value);
            settings.Set("Y", numY.Value);
            settings.Set("Key", (int)keyBox.Key);
            settings.Set("Ctrl", chkCtrl.Checked);
            settings.Set("Shift", chkShift.Checked);
            settings.Set("Alt", chkAlt.Checked);
            settings.Set("HoldMs", numHold.Value);
            settings.Set("Hours", numHours.Value);
            settings.Set("Minutes", numMinutes.Value);
            settings.Set("Seconds", numSeconds.Value);
            settings.Set("Milliseconds", numMillis.Value);
            settings.Set("JitterMs", numJitter.Value);
            settings.Set("RepeatLimited", rbRepeatCount.Checked);
            settings.Set("RepeatCount", numRepeat.Value);
            settings.Set("Hotkey", cboHotkey.SelectedIndex);
            settings.Set("TopMost", chkTopMost.Checked);
            settings.Save();
        }

        static void SetIndex(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count) combo.SelectedIndex = index;
        }

        static void SetValue(NumericUpDown num, decimal value)
        {
            num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, value));
        }
    }

    /// <summary>Read-only box that records the next key pressed while it has focus.</summary>
    internal sealed class KeyCaptureBox : TextBox
    {
        const string Placeholder = "Click, then press a key";
        Keys key = Keys.None;

        public event KeyEventHandler KeyCaptured;

        public KeyCaptureBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            UpdateText();
        }

        public Keys Key
        {
            get { return key; }
            set { key = value; UpdateText(); }
        }

        void UpdateText()
        {
            Text = key == Keys.None ? Placeholder : Input.KeyName(key);
            ForeColor = key == Keys.None ? SystemColors.GrayText : SystemColors.WindowText;
        }

        protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
        {
            e.IsInputKey = true;  // let Tab, arrows and Enter be captured too
            base.OnPreviewKeyDown(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Alt+key combinations would otherwise be eaten as menu mnemonics.
            if ((keyData & Keys.Alt) == Keys.Alt)
            {
                OnKeyDown(new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var code = e.KeyCode;
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                code == Keys.LControlKey || code == Keys.RControlKey ||
                code == Keys.LShiftKey || code == Keys.RShiftKey ||
                code == Keys.LMenu || code == Keys.RMenu)
                return;  // wait for the real key; modifiers are recorded alongside it

            Key = code;
            SelectionLength = 0;
            if (KeyCaptured != null) KeyCaptured(this, e);
        }
    }
}
