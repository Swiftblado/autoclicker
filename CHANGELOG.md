# Changelog

## v2.5.0 (October 30, 2026)

- Key bindings: save the current setup (clicks, drags, key presses or a macro, with its timing) under a name and give it its own key or key combination, like F7 or Ctrl+1. Pressing that key from any window starts or stops it.
- Each binding can be allowed to run alongside others, so two patterns can run at the same time. When they both need the mouse, they take turns, so one click is never cut in half by another.
- New option: "Notify me when it slows down". Turn it off to stop the slow-down and back-to-full-speed notifications; lag protection keeps working either way.
- A "What's new" button shows this list, and it opens by itself once after an in-app update.

## v2.4.0 (October 28, 2026)

- Custom click hold: besides 20, 35 and 50 ms, "Custom…" takes any hold from 0 to 1000 ms. A hold under 17 ms (one frame at 60 fps) counts as a half click.
- A "?" button next to the hold explains which apps and games register short clicks.
- Lag protection now watches the screen under the cursor too, so it catches a game or web page that stops keeping up even while the browser itself still responds instantly.
- When the app freezes outright, it slows to a quarter speed instead of half.
- After lag, it waits 30 seconds before retrying the speed that caused it, so it doesn't bounce in and out of lag.
- A short warm-up before the first click learns what the screen normally looks like.
- macOS asks for Screen Recording permission for the new screen check; without it, the other lag checks still work.

## v2.3.0 (October 22, 2026)

- In-app updates: the Options card shows your version and an "Update" button when a newer release is out. It downloads the update, swaps it in and restarts, keeping your settings and macros.
- If the app can't replace itself (for example, it's in a folder you can't write to), it opens the download page instead.
- macOS: after an update, a banner explains how to turn the Accessibility permission back on, with a button that opens the right settings page.

## v2.2.0 (October 22, 2026)

- Full and half clicks: a full click (the default) holds the button for 20, 35 or 50 ms like a real press; a half click lets go instantly. Macro click steps get the same choice, and older macros load as full clicks.
- Lag protection, on by default: when your computer or the app you're automating falls behind, it halves the speed, keeps slowing while the lag lasts, and eases back once it clears.
- System notifications when it slows down and when it's back to full speed.
- Timing stays accurate while the window is in the background (no Windows 11 throttling or macOS App Nap during a run).
- New two-column layout, so every mode fits on a 1080p screen.

## v2.1.1 (September 24, 2026)

- Fixed macOS downloads being reported as "damaged" on Apple silicon Macs.
- Fixed the Intel Mac build; it's now built and tested on an Intel Mac.
- Linux: the app menu entry launches correctly right after installing.
- Clearer first-run steps for macOS ("Open Anyway").

## v2.1.0 (September 23, 2026)

- Cursor dragging: press a button at one point, glide to another over a set time, and release.
- Macros: record your real clicks, drags and key presses, or build a list of steps by hand, then replay them. Macros save to files and open again later.
- Stopping a long macro or drag is instant, and never leaves a button or key held down.

## v2.0.0 (September 22, 2026)

- Auto Clicker now runs on Windows, macOS (Apple silicon and Intel) and Linux.
- Global start/stop key that works while other apps have focus.
- macOS app bundle and a Linux installer with an app menu entry.
- Settings from version 1 are reset, because keys are now stored in a way that works on every system.

## v1.0 (September 22, 2026)

- First version, for Windows: repeats mouse clicks (left, right or middle; single or double; at the cursor or a fixed spot) or key presses at a set interval, with a start/stop key.
