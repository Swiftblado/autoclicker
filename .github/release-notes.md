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
- **macOS:** unzip, drag the app to Applications and open it. It isn't notarized by Apple, so the first time go to **System Settings → Privacy & Security** and click **Open Anyway** (on macOS 14 and older, right-click the app → **Open** also works). If it still won't open, run `xattr -cr /Applications/AutoClicker.app` in Terminal. macOS also asks for Accessibility permission: **System Settings → Privacy & Security → Accessibility**, switch Auto Clicker on, then quit and reopen it. Nothing can send clicks without that permission.
- **Linux:** unpack and run `./install.sh`, or just run `./AutoClicker`. Requires an **Xorg** session; Wayland blocks apps from sending input to other windows.

**What it does**

- Mouse: left, right or middle button, single or double click, at the cursor or a fixed spot
- Drags: hold a button and glide between two points over a set time
- Keyboard: any key, with Ctrl/Shift/Alt/Cmd, and a set hold time
- Macros: record what you do and replay it, or build a sequence of clicks, drags, keys and waits by hand — saved and reopened as `.json` files
- Interval from 1 ms to 24 hours, optional random variation, repeat forever or a set number of times
- A global start/stop hotkey (F6 by default) that works from any window

Recording real input is new and has only been tested on Windows by way of its building blocks — if it misbehaves on your system, please open an issue.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
