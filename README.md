# Auto Clicker

A small, portable auto clicker and key presser for **Windows, macOS and Linux**. One app, nothing to install: download the file for your computer and open it.

- **Mouse clicks**: left, right or middle button, single or double click, at the cursor or at a fixed spot (use **Pick** to grab a spot). A **full click** (the default) holds the button down for 20, 35 or 50 ms like a real press, or for any time from 0 to 1000 ms with **Custom…**; a **half click** lets go instantly, like a light tap on a touchpad. A custom hold under 17 ms (one frame at 60 fps) counts as a half click. The **?** next to the hold explains which apps and games register short clicks. Macro click steps have the same choice.
- **Mouse drags**: hold a button down and glide from one point to another over a set time.
- **Key presses**: any key, optionally with Ctrl, Shift, Alt/Option or Win/Cmd/Super, and a set hold time.
- **Macros**: record what you do and replay it, or build a sequence by hand — clicks, drags, key presses and waits. Reorder or delete steps, and save macros as `.json` files to reuse later.
- **Timing**: an interval from 1 ms up to 24 hours, optional random variation (±ms), and either repeat until stopped or a set number of times.
- **Global hotkey**: F6 by default (F1–F12 selectable) starts and stops it from any window.
- **Key bindings**: save a setup (clicks, drags, key presses or a macro, with its timing) under a name and its own key or combination, like F7 or Ctrl+1; that key starts and stops it from any window. Turn on **Run alongside others** for bindings that may run at the same time; when two runs both need the mouse they take turns, so a click never lands in the middle of another run's click or drag. Bindings are kept in `bindings.json` next to the settings file.
- **Lag protection**: with **Slow down when lagging** on (the default), it watches for your computer or the app you're automating falling behind, including a game or web page that stops redrawing under the cursor because it can't keep up with the clicks, halves the speed (quarters it when the app freezes outright) and keeps slowing while the lag lasts, then eases back one step every 3 seconds without lag, waiting 30 seconds before retrying the speed that caused it. It also watches the screen for a moment before the first click to learn what normal looks like. A system notification tells you when it slows down and when it's back to normal; turn off **Notify me when it slows down** to keep it quiet.
- Settings are remembered between runs.
- **What's new** under Options lists every version's changes (also in [CHANGELOG.md](CHANGELOG.md)); it opens by itself once after an in-app update.

## Download

Get the file for your machine from the [latest release](https://github.com/Swiftblado/autoclicker/releases/latest):

| Your computer | Download |
| --- | --- |
| Windows 10/11 | `AutoClicker-windows-x64.exe` |
| Mac with Apple silicon (M1 and newer) | `AutoClicker-macos-arm64.zip` |
| Mac with an Intel processor | `AutoClicker-macos-x64.zip` |
| Linux (X11 session) | `AutoClicker-linux-x64.tar.gz` |

### First run

- **Windows** — SmartScreen warns about unsigned apps. Click **More info → Run anyway**.
- **macOS** — unzip and drag the app to Applications, then open it. It isn't notarized by Apple, so the first time macOS refuses: go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to Auto Clicker (on macOS 14 and older, right-clicking the app and choosing **Open** also works). macOS then needs Accessibility permission: **System Settings → Privacy & Security → Accessibility**, switch Auto Clicker on, then quit and reopen the app. No app can send clicks or keys without that permission; Auto Clicker shows a banner until it's granted.
- **Linux** — unpack, then either run `./install.sh` (adds it to your applications menu) or run `./AutoClicker` directly. Needs an **Xorg** session and `libX11`/`libXtst` (installed by default on most desktops).

## Usage

1. Choose **Mouse clicks** or **Key presses** and set the options.
2. Set the interval under **Timing**.
3. Hover over your target (or switch to the target window) and press **F6**. Press **F6** again to stop.

Clicking **Start** instead counts down 3 seconds first, so you can switch to the target window before it begins.

### Macros

Pick **Macro** from the dropdown, then either:

- **Record**: press **Record**, wait for the 2-second countdown, do the thing, then press **F6** to finish. What you did is added to the list as steps. The key you use to stop isn't recorded.
- **Build by hand**: choose a step type under **Add step**, fill in the details (**Pick** grabs a screen position after a countdown) and press **Add**. Select a step to move, delete or **Replace** it.

Recording keeps the shape of what you did rather than a literal trace: a press and release in one spot becomes a click, a press and release apart becomes a drag in a straight line between them, a key with modifiers held becomes one step, and the pauses in between become waits. Cursor movements on their own aren't recorded, since every click and drag carries its own coordinates.

**Save...** and **Open...** keep macros as `.json` files (in `Macros` next to the settings file by default). Whatever is in the list is remembered between runs, and the **Repeat the macro** section controls how often the whole sequence replays.

### Key bindings

1. Set something up as usual (any mode, plus its timing).
2. Under **Key bindings**, type a name, click the key box and press the key or combination that should start it (for example **F7** or **Ctrl+1**), and press **Add current setup**.
3. From any window, press that key to start it and again to stop it. **Stop** in the app stops everything that's running.

A binding with **Run alongside others** off stops whatever else is running when you start it, so only it runs. With it on, it starts next to whatever is already going. **Load into window** puts a binding's setup back into the window so you can change it; **Replace** saves the window's setup, name, key and option over the selected binding.

### Updating

The app checks GitHub for a newer release when it opens. If there is one, the button under **Options** reads **Update to v…**; press it, confirm, and Auto Clicker downloads the new version for your system, swaps it in and restarts. Settings and macros are kept. If it can't replace itself (for example the folder isn't writable, or on macOS it's still running from Downloads), it says why and opens the download page instead.

On macOS, the Accessibility permission is tied to the exact copy of the app, so after an update you need to switch Auto Clicker off and back on under **System Settings → Privacy & Security → Accessibility**. The app shows a banner with a button that opens that page.

Builds you make yourself are marked "development build" and don't update.

## Platform notes

- **Wayland (Linux):** Wayland deliberately stops apps from sending input to other apps, and there's no portal for it yet, so Auto Clicker needs an Xorg session. Pick "Ubuntu on Xorg" (or your desktop's X11 option) at the login screen. The app says so on screen if it detects Wayland.
- **Elevated windows (Windows):** Windows blocks non-admin apps from sending input into programs running as administrator. Right-click the `.exe` and choose **Run as administrator** to automate those.
- **Hotkey conflicts:** if another app already owns the hotkey, the app says so in the status line; pick a different F-key in **Options**. A key binding whose key is taken is listed with a warning; select it, set another key and press **Replace**.
- **Recording permissions:** watching input is a separate privilege from sending it. macOS covers both with the Accessibility permission above. Linux recording needs the X server's RECORD extension, which most distributions ship enabled.
- **Lag protection on macOS:** to see whether the app under the cursor is still redrawing, Auto Clicker looks at a small patch of the screen there, which macOS treats as screen recording. The first time it asks; allow Auto Clicker under **System Settings → Privacy & Security → Screen Recording** and reopen it. Without that it still slows down for the other kinds of lag. Nothing is saved or sent anywhere.

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The UI is [Avalonia](https://avaloniaui.net/), so the same code builds for all three systems.

```powershell
.\build.ps1                      # this machine
.\build.ps1 -Runtime linux-x64   # or osx-arm64, osx-x64, win-arm64...
```

```bash
dotnet publish src/AutoClicker/AutoClicker.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o dist/linux-x64
```

`tools/make-icons.ps1` regenerates the icon files (Windows `.ico`, macOS `.iconset`, PNGs). Every push builds on real Windows, macOS and Linux runners via [GitHub Actions](.github/workflows/build.yml); pushing a `v*` tag publishes a release with all four downloads attached.

## How it's put together

```
src/AutoClicker/
  Core/      Runner.cs (timing loop), Settings.cs, InputActions.cs,
             Macro.cs + MacroStep.cs + MacroAction.cs (macros)
             LagGovernor.cs + LagMonitor.cs (slowing down for lag),
             RunSetup.cs + RunSession.cs (one run each), KeyBinding.cs
  Input/     one backend per OS, behind IInputBackend:
             Windows SendInput · macOS CGEvent · Linux XTest;
             ScreenProbe.cs watches the screen under the cursor for lag
  Hotkeys/   global hotkey per OS:
             RegisterHotKey · Carbon RegisterEventHotKey · X11 XGrabKey
  Recording/ input capture per OS, behind IInputRecorder:
             Windows hooks · macOS CGEventTap · Linux XRecord,
             plus RecordingCompressor.cs (raw events -> steps)
  Notifications/ system notifications per OS:
             Windows tray balloon · macOS osascript · Linux notify-send
  Updates/   Updater.cs (download + swap per OS), AppVersion.cs
  Views/     MainWindow.axaml — the whole UI
packaging/   macOS .app bundling, Linux .desktop + installer, icons
```

## Status

On Windows, clicks, drags, key presses and macro playback are tested: the right events arrive, at the right places, at the interval set. The step-building logic that turns a recording into steps is covered by tests too.

Two things are **not** verified, so treat them as first drafts:

- **Recording real input.** The capture path can only be exercised by a person actually using the mouse and keyboard, which a test can't fake — synthetic input is deliberately ignored so playback never records itself.
- **The macOS and Linux builds** compile and start on real runners in CI, but nobody has used their UIs.

Please [open an issue](https://github.com/Swiftblado/autoclicker/issues) if something misbehaves.
