# Auto Clicker

A small, portable Windows app that automatically clicks the mouse or presses a key on a timer.

- **Mouse clicks**: left, right or middle button, single or double click, at the cursor or at a fixed spot (use **Pick** to grab a spot).
- **Key presses**: any key, optionally with Ctrl, Shift or Alt, and a set hold time.
- **Timing**: an interval from 1 ms up to 24 hours, optional random variation (±ms), and either repeat until stopped or a set number of times.
- **Global hotkey**: F6 by default (F1–F12 selectable) starts and stops it from any window.
- Settings are remembered between runs in `%APPDATA%\AutoClicker\settings.ini`.

## Download & run

Grab `AutoClicker.exe` and double-click it. It's a single ~45 KB file with no installer. It needs .NET Framework 4.x, which comes with Windows 10 and 11.

> Windows SmartScreen may warn about an unsigned app the first time. Click **More info → Run anyway**.

## Usage

1. Choose **Mouse clicks** or **Key presses** and set the options.
2. Set the interval under **Timing**.
3. Hover over your target (or switch to the target window) and press **F6**. Press **F6** again to stop.

If you click the **Start** button instead of using the hotkey, it counts down for 3 seconds so you can switch to the target window first.

**Note:** Windows blocks non-admin apps from sending input to programs running as administrator. To automate an elevated app, right-click `AutoClicker.exe` and choose **Run as administrator**.

## Build from source

No SDK is needed; the build uses the C# compiler that ships with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The output is `dist\AutoClicker.exe`. `tools\make-icon.ps1` regenerates `src\app.ico`.
