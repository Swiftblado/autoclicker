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
- **macOS:** unzip, drag the app to Applications, then right-click it and choose **Open** (it isn't signed by Apple). macOS also asks for Accessibility permission: **System Settings → Privacy & Security → Accessibility**, switch Auto Clicker on, then quit and reopen it. Nothing can send clicks without that permission.
- **Linux:** unpack and run `./install.sh`, or just run `./AutoClicker`. Requires an **Xorg** session; Wayland blocks apps from sending input to other windows.

**What it does**

- Mouse: left, right or middle button, single or double click, at the cursor or a fixed spot
- Keyboard: any key, with Ctrl/Shift/Alt/Cmd, and a set hold time
- Interval from 1 ms to 24 hours, optional random variation, repeat forever or a set number of times
- A global start/stop hotkey (F6 by default) that works from any window

🤖 Generated with [Claude Code](https://claude.com/claude-code)
