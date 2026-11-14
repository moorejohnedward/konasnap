# KonaSnap: Development Log

**Developer:** John Moore
**Project:** KonaSnap, a Snagit-style screen capture, scrolling capture, markup and text-grab tool for Windows (C#, Windows Forms, .NET Framework 4)
**Goal:** A free capture tool that does what I actually use Snagit for, built so I understand every part of it.

Each entry records what was built, why, and the concepts behind it. The Git commit history on GitHub is the detailed, timestamped record of every change.

---

## 2026-11-03: v1, First version

**Built:** area, window and full-screen capture, a markup editor (arrows, boxes, text, highlight), copy to clipboard and save to Pictures\KonaSnap. Kept in `history/KonaSnap v1.cs.txt`.

**Concepts:** Windows Forms, GDI+ drawing, screen capture with `Graphics.CopyFromScreen`, global hotkeys through the Windows API (P/Invoke), building with the C# compiler that ships with Windows.

---

## 2026-11-08: v2, Scrolling capture, panels and text grab

**Built**
- Scrolling capture that stitches a long page or panel into one tall image.
- Picking a panel inside a window with the mouse wheel, using Windows UI Automation to find the parts of a window.
- "Grab text" that reads every word in a box, even the scrolled-off part, with Windows' built-in OCR as a fallback.
- Taskbar jump list and hover buttons (COM interop with the Windows shell).

**Concepts:** UI Automation tree walking, image stitching by matching overlapping rows, COM interfaces declared in C#, single-instance apps with a named mutex and window messages (WM_COPYDATA).

---

## 2026-11-11: v2.1, Speed, in-app updates, GitHub and crash reports

**Problem:** KonaSnap showed a generic ".NET Framework unhandled exception" box every time it opened, with no record of what went wrong, and every change meant rebuilding on my PC with build.bat.

**Problem 2:** Captures felt sluggish.

**Speed fixes (profiling the capture path step by step)**
- Removed a fixed 300 ms wait before every capture. It's only needed when KonaSnap's own window has to get out of the way, and then 140 ms is enough.
- Screenshots now use the premultiplied-alpha pixel format (32bppPArgb). GDI+ draws it several times faster than the default format.
- The picker repaints only the region that changed between mouse moves (a symmetric difference of the old and new rectangles plus their borders and labels) instead of both whole rectangles.
- The editor opens first, while a background thread writes the PNG. A lock keeps that save from overlapping with the editor's own saves.
- The editor caches the picture at the current zoom level, and blur boxes cache their pixelated patch, so markup drawing is just a pixel copy.

**Built**
- **Crash reporter** (`Updater.InstallCrashHandlers`, `Updater.Report`): catches errors on the UI thread and in the background, writes the full stack trace with the Windows and .NET versions to `%LOCALAPPDATA%\KonaSnap\crash.log`, copies it to the clipboard, and shows a plain-English message.
- **In-app updater** (`Updater`, `UpdateForm`): checks the GitHub Releases API for a newer version, shows the release notes, downloads the new .exe in the background with a progress bar, checks its SHA-256 fingerprint, swaps itself and restarts.
- **Safe self-replacement:** Windows won't let a running .exe be overwritten, but it allows renaming one. KonaSnap renames itself to `.old`, moves the new file into place (putting the old one back if that fails), starts the new copy with `--updated` and deletes `.old` on the next start.
- **Single-instance handoff:** the new copy waits for the old one to release its named mutex instead of thinking KonaSnap is already open.
- **GitHub repository + automated builds (CI/CD):**
  - `build.yml` compiles KonaSnap on GitHub's Windows servers with the same `build.bat` on every push.
  - `release.yml`: when `Updater.Version` goes up, GitHub builds KonaSnap.exe, attaches its fingerprint and publishes a Release with notes from CHANGELOG.md.

**Concepts:** performance profiling, dirty-region repainting, caching, pixel formats, thread safety with locks, version control with Git, semantic versioning, continuous integration and delivery (GitHub Actions), REST APIs and JSON (parsed with regular expressions, since .NET Framework 4's compiler has no built-in JSON), TLS 1.2 for HTTPS on older .NET, background threads with `ThreadPool` and marshaling back to the UI thread with `BeginInvoke`, cryptographic hashing, global exception handling.

**Result:** After this one install, new versions arrive with one click from inside KonaSnap, and any crash leaves a report that pinpoints the line that failed.

---

## 2026-11-11: v2.1.1, The startup crash, found and fixed

**Problem:** KonaSnap crashed on every launch. The new crash reporter caught it on the first try: `NullReferenceException in MainForm.OnHandleCreated`.

**Cause:** the constructor called `CreateGraphics()` to read the screen DPI. That forces Windows to create the window handle immediately, which fires `OnHandleCreated` before the constructor has created the buttons, so setting `regionBtn.Key` hit a null reference.

**Fix:** read the DPI from the desktop (`Graphics.FromHwnd(IntPtr.Zero)`) so the handle isn't created early, store the hotkey labels, and apply them only once the buttons exist.

**Lesson:** in Windows Forms, anything that touches `Handle` (including `CreateGraphics`) inside a constructor can run window-creation code before the object is fully built. This is the value of crash logging: hours of guessing became a one-line stack trace.

---

## 2026-11-13: v2.1.2, Scrolling capture stopped itself

**Problem:** Scrolling capture never scrolled. It always came back as a normal screenshot.

**Diagnosis:** `Pictures\KonaSnap\scroll-log.txt` showed: "Cursor set to {X=5981,Y=1433}, reads back as {X=5963,Y=1402}", then "Mouse moved to {X=5946,Y=1377}; stopping." Between those two readings KonaSnap didn't move the pointer, but it kept drifting up and to the left. That was my hand settling after the click. The "did the user move the mouse?" check allowed only 40 pixels, which is tiny on a 4K screen.

**Fix:** a 1.5-second grace period where drift is undone instead of treated as "stop", a tolerance sized to the screen (height / 12, about 180 px at 4K), and Esc as a clear way to stop (`GetAsyncKeyState`).

**Concepts:** reading your own diagnostic logs, input tolerance that scales with screen resolution, polling key state with the Windows API.

**Lesson:** a log line that records both what the program did and what it observed ("set to X, reads back as Y") made the cause visible without a debugger.

