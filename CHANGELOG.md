# Changelog

## v3.1.0 (November 21, 2026)

- Fixed: a macro waiting in a While loop (or a long Wait) no longer sets off lag protection. While a macro only waits, looks at the screen or is paused, it sends nothing, so that time isn't judged as lag, and a slow-down only stretches the time a pass spends clicking and typing.
- In the condition window, each screen check has its own "and" / "or" switch with the check above it. "and" goes first, as usual, and the preview shows the brackets. Groups are still there for the rare condition that needs them. Conditions from 3.0 keep their meaning.
- Adding an If, Else if or While no longer opens a screen check by itself; press "+ Screen check…" when you're ready. A condition can't be saved without at least one check.
- Clicks and drags can aim at a screen check: they click where it finds its target, at its exact center, the center moved by pixels, or the center moved by a share of its size (100% is the right or top edge, -100% the left or bottom edge). If it finds nothing, the line is skipped, and the app suggests putting such lines inside an If or While.
- Undo and Redo in the Macro tab, also as Ctrl+Z and Ctrl+Y, for every change to the lines.
- After deleting a line, the line above it is selected.
- Double-click any line to load it into the editor, not just For and Go to.
- New option "Start delay": how long Start waits before the first click or key, 0 to 60 seconds (3 by default).
- Pause and Resume: a new button, and key bindings can now pause or resume the window's run, step one line of a paused macro, or make the macro go to a line (starting it from there if it isn't running). New option "Resume delay" sets how long Resume waits before carrying on (0 by default). Pausing holds Waits and loop time limits too.
- Every control explains itself when you hover over it.

## v3.0.0 (November 20, 2026)

- Macros get control flow: If / Else if / Else, While and For blocks, plus Go to line and Stop macro. Lines inside a block are indented, and blocks can go inside each other.
- If and While test a condition made of screen checks, combined with and, or and not, in groups that can go inside each other, like (A and not B) or C. Test now works out the whole condition right away.
- Screen checks now only say found or not found; what happens next is up to the If or While around them. Macros from before 3.0 are converted when opened: "wait until it appears" becomes a While, "go to step" becomes Go to line, and "click on it" becomes a click at the same spot. Checks that searched an area or the whole screen can't click where they found something anymore, so those clicks are left out (the app says so when it opens such a macro).
- While loops check their condition every 100 ms by default (0 checks nonstop; under 10 ms shows a warning about CPU use) and stop after 60 seconds by default (0 means no limit), then the macro carries on after the loop.
- For loops run a number of times or a number of seconds; a timed loop always finishes the round in progress.
- New lines go below the selected line instead of at the end. Adding an If, While or For adds its End line too. Deleting a block's first or End line removes the block's frame and keeps the lines inside.
- Saved macros: Save… keeps the macro under a name in a list in the Macro tab, where it can be loaded, renamed, deleted, downloaded as a .json file, or imported from one. Macros saved in the Macros folder by earlier versions show up in the list.
- A new dark look with a blue accent, rounder controls and cards, and a cleaner header.

## v2.7.1 (November 16, 2026)

- Fixed: Dropper and Pick & add could take the color of the Auto Clicker window itself instead of what was under it. The color is now read while the windows are still hidden.
- On Windows, the app's windows are also left out of screen captures while picking, so no dropper can ever see them.

## v2.7.0 (November 15, 2026)

- Every way of picking something on the screen now hides the app's windows while you pick: Pick, Dropper, Pick & add, Drag area and Capture from screen.
- A countdown next to the mouse shows how much time is left to pick.
- New option "Time to pick a spot": 1 to 60 seconds, 3 by default.
- New option "A click picks right away", off by default: a left click picks at once instead of waiting for the countdown, and that click doesn't reach whatever is under the mouse.
- Drag-a-box picking now has the same time limit: if you haven't started a box when time runs out, it cancels. A box you're dragging is never cut off.

## v2.6.1 (November 15, 2026)

- In a screen check, Dropper, Pick and Pick & add now hide the app's windows during the countdown, so they're never in the way of what you're picking.
- Only the main window can be minimized. The screen check, What's new and update windows can't, so they can't get lost behind it.

## v2.6.0 (November 15, 2026)

- New macro step: Check screen. It looks for something on screen and decides what the macro does next.
- Look for a color: type a hex code, choose it on a color wheel, or use the Dropper to take the color (and position) of any pixel on screen.
- Look for several colors at once: each point has its own color and all must match, either exactly where they are or as a pattern found anywhere in an area.
- Look for an image: drag a box over the screen to capture it, or load a PNG. Transparent parts of a PNG are ignored, and small differences like a blinking cursor still match.
- Look at one point, inside an area, or anywhere on screen, with an adjustable color tolerance.
- Check once, or wait until it appears, with an optional time limit.
- When found, optionally click on it, then continue, go to another step, or stop the macro. Choose separately what happens when it isn't found. Going back to an earlier step makes a loop.
- Test now shows straight away whether a check finds its target, where, and how long the search took.
- macOS needs the Screen Recording permission for screen checks.

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
