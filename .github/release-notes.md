Auto Clicker runs on Windows, macOS and Linux. Pick the download for your machine:

| Your computer | Download |
| --- | --- |
| Windows 10/11 | `AutoClicker-windows-x64.exe` |
| Mac with Apple silicon (M1 and newer) | `AutoClicker-macos-arm64.zip` |
| Mac with an Intel processor | `AutoClicker-macos-x64.zip` |
| Linux (X11 session) | `AutoClicker-linux-x64.tar.gz` |

Nothing to install — each download is the whole app.

**First run on each system**

- **Windows:** SmartScreen warns about unsigned apps. Click **More info → Run anyway**.
- **macOS:** unzip, drag the app to Applications and open it. It isn't notarized by Apple, so the first time go to **System Settings → Privacy & Security** and click **Open Anyway** (on macOS 14 and older, right-click the app → **Open** also works). macOS also asks for Accessibility permission: **System Settings → Privacy & Security → Accessibility**, switch Auto Clicker on, then quit and reopen it. Nothing can send clicks without that permission.
- **Linux:** unpack and run `./install.sh`, or just run `./AutoClicker`. Requires an **Xorg** session; Wayland blocks apps from sending input to other windows.

**What it does**

- Mouse: left, right or middle button, single or double click, at the cursor or a fixed spot; full clicks (held 20–50 ms) or half clicks (an instant tap)
- Drags: hold a button and glide between two points over a set time
- Keyboard: any key, with Ctrl/Shift/Alt/Cmd, and a set hold time
- Macros: record what you do and replay it, or build one by hand from clicks, drags, keys and waits, with If / Else if / Else, While and For blocks, Go to line and Stop
- Conditions for If and While: screen checks for a color (hex code, color wheel or dropper), a pattern of several colors, or an image, each with its own and / or switch and not, plus groups for brackets
- Clicks and drags that aim at what a screen check finds: its center, or the center moved by pixels or by a share of its size
- Undo and Redo for macro edits (Ctrl+Z / Ctrl+Y)
- Saved macros listed in the app, with rename, delete, download as `.json` and import
- Interval from 1 ms to 24 hours, optional random variation, repeat forever or a set number of times
- Lag protection: slows down automatically when your computer or the target app falls behind (including a game that stops redrawing), with an optional notification, and speeds back up once it clears
- A global start/stop hotkey (F6 by default) that works from any window
- Key bindings: give saved setups their own keys (like Ctrl+1), and let some run alongside each other; or make keys that pause, step one line of, or move the macro running from the window
- Pause and Resume, with a choosable start delay and resume delay
- Hover over any control for an explanation
- Updates itself: press **Update** under Options when a new version is out

Recording real input is new and has only been tested on Windows by way of its building blocks — if it misbehaves on your system, please open an issue.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
