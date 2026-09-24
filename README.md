# Auto Clicker

A small, portable auto clicker and key presser for **Windows, macOS and Linux**. One app, nothing to install: download the file for your computer and open it.

- **Mouse clicks**: left, right or middle button, single or double click, at the cursor or at a fixed spot (use **Pick** to grab a spot).
- **Mouse drags**: hold a button down and glide from one point to another over a set time.
- **Key presses**: any key, optionally with Ctrl, Shift, Alt/Option or Win/Cmd/Super, and a set hold time.
- **Macros**: record what you do and replay it, or build a sequence by hand — clicks, drags, key presses and waits. Reorder or delete steps, and save macros as `.json` files to reuse later.
- **Timing**: an interval from 1 ms up to 24 hours, optional random variation (±ms), and either repeat until stopped or a set number of times.
- **Global hotkey**: F6 by default (F1–F12 selectable) starts and stops it from any window.
- Settings are remembered between runs.

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

## Platform notes

- **Wayland (Linux):** Wayland deliberately stops apps from sending input to other apps, and there's no portal for it yet, so Auto Clicker needs an Xorg session. Pick "Ubuntu on Xorg" (or your desktop's X11 option) at the login screen. The app says so on screen if it detects Wayland.
- **Elevated windows (Windows):** Windows blocks non-admin apps from sending input into programs running as administrator. Right-click the `.exe` and choose **Run as administrator** to automate those.
- **Hotkey conflicts:** if another app already owns the hotkey, the app says so in the status line; pick a different F-key in **Options**.
- **Recording permissions:** watching input is a separate privilege from sending it. macOS covers both with the Accessibility permission above. Linux recording needs the X server's RECORD extension, which most distributions ship enabled.

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
  Input/     one backend per OS, behind IInputBackend:
             Windows SendInput · macOS CGEvent · Linux XTest
  Hotkeys/   global hotkey per OS:
             RegisterHotKey · Carbon RegisterEventHotKey · X11 XGrabKey
  Recording/ input capture per OS, behind IInputRecorder:
             Windows hooks · macOS CGEventTap · Linux XRecord,
             plus RecordingCompressor.cs (raw events -> steps)
  Views/     MainWindow.axaml — the whole UI
packaging/   macOS .app bundling, Linux .desktop + installer, icons
```

## Status

On Windows, clicks, drags, key presses and macro playback are tested: the right events arrive, at the right places, at the interval set. The step-building logic that turns a recording into steps is covered by tests too.

Two things are **not** verified, so treat them as first drafts:

- **Recording real input.** The capture path can only be exercised by a person actually using the mouse and keyboard, which a test can't fake — synthetic input is deliberately ignored so playback never records itself.
- **The macOS and Linux builds** compile and start on real runners in CI, but nobody has used their UIs.

Please [open an issue](https://github.com/Swiftblado/autoclicker/issues) if something misbehaves.
