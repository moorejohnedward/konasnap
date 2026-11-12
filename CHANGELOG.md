# Changelog - KonaSnap

## v2.1.1 - Nov 11, 2026 (Crash on open fixed)
- **Fixed: KonaSnap crashed with an "unhandled exception" every time it opened.** While setting itself up, KonaSnap asked for the screen's size settings in a way that made Windows build the window too early. Windows then tried to put the shortcut keys on buttons that didn't exist yet. It now reads those settings without building the window early, and only labels the buttons once they exist. (Found with the new crash report.)

## v2.1.0 - Nov 11, 2026 (Faster captures, in-app updates, crash reports)
- **Snappier captures:**
  - The hotkeys, taskbar menu and hover buttons start the picker right away. Before, every capture waited a fixed 0.3 seconds.
  - The picker opens faster, and dragging an area stays smooth on three monitors because only the part that changed is redrawn.
  - The markup editor opens as soon as you finish the capture. Saving the PNG happens in the background.
  - Drawing arrows, boxes and blur in the editor no longer rescales the whole picture on every mouse move.
- **Updates from inside KonaSnap:** the line at the bottom of the main window now reads "Check for updates". When a new version is out it turns orange ("Update ready"). One click shows what's new, then downloads, verifies and installs it and restarts KonaSnap.
- Your current KonaSnap is only replaced after the download passes its safety check (SHA-256 fingerprint). If anything fails, the old version stays.
- **Crash reports:** if KonaSnap hits an error, it now explains what happened, saves the details to `%LOCALAPPDATA%\KonaSnap\crash.log` and copies them to the clipboard so they can be pasted to Claude, instead of the generic .NET "unhandled exception" box.
- Any open markup windows still save before KonaSnap restarts for an update.
- The project now lives on GitHub (github.com/moorejohnedward/konasnap). Every new version is built there automatically, so updates no longer need build.bat.

## v2.0 - Nov 8, 2026
- Scrolling capture, panels inside windows (mouse wheel in the picker), full-text grab, new markup tools and a fresh look.
- Taskbar right-click menu and hover buttons for one-click captures.

## v1.0 - Nov 3, 2026
- First version: area, window and full-screen capture with markup.
