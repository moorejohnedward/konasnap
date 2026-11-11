KonaSnap v2.1 - screen capture, scrolling capture, markup and text grab

UPDATES (v2.1 and later)
- KonaSnap updates itself. The line at the bottom of the main window says
  "Check for updates" and turns orange when a new version is ready. Click it,
  then "Update now". No need to run build.bat again.
- Source code and every version: https://github.com/moorejohnedward/konasnap
- If KonaSnap ever hits an error, the details are copied to your clipboard and
  saved in %LOCALAPPDATA%\KonaSnap\crash.log. Paste them to Claude.

SET UP (once, and again after each update)
1. Double-click build.bat. It closes any running KonaSnap, builds the new KonaSnap.exe
   in this folder and starts it.
   (If Windows SmartScreen warns about an unknown app, click "More info" > "Run anyway".
    It's flagged only because you built it yourself.)
2. While KonaSnap is open, right-click its icon on the taskbar > "Pin to taskbar".
3. Optional: to start it with Windows, press Win+R, type shell:startup, and drop a
   shortcut to KonaSnap.exe in that folder.

ONE-CLICK FROM THE TASKBAR (no keyboard needed)
- RIGHT-CLICK the KonaSnap icon on the taskbar. The menu lists:
    Grab text from a box  •  Scrolling capture  •  Capture area or window
    Capture full screen   •  Capture in 3 seconds
  Click one and it starts right away, even if KonaSnap isn't open yet.
  (The menu appears after you've run the new KonaSnap once.)
- Or HOVER over the icon: five small buttons appear under the preview window
  (same five actions, left to right). Hover a button to see its name.
- Plain click on the icon still opens the KonaSnap window with the big buttons.

THE PICKER (what you see after you start any capture)
- Drag to capture an area. Click to capture what's highlighted. Esc or right-click cancels.
- PANELS INSIDE WINDOWS: hover over a window and turn the MOUSE WHEEL.
  Wheel down = smaller (a panel, frame or box inside the window).
  Wheel up   = bigger (back out to the whole window).
  The label shows what's highlighted, e.g. "Scrolling panel" or "Page".
- The little zoom loupe by the cursor shows the exact pixel, its position and color.

CAPTURE
- Capture area or window: PrtScn or Ctrl+Shift+X.
- Scrolling capture: Ctrl+Alt+S.
  Scroll the page or panel to where you want to start, then press Ctrl+Alt+S and click it.
  KonaSnap scrolls it for you and stitches one tall picture. A small pill shows progress.
  Move the mouse to stop early. When it reaches the bottom it opens in the editor.
- Grab text: Ctrl+Alt+T.  (Best for course question boxes and other scrolling boxes.)
  Hover the box; it lights up blue (turn the mouse wheel if the outline is too big or
  too small). CLICK it and KonaSnap copies EVERY word in it, even the part you'd have to
  scroll to see. Or DRAG over text to read just that area.
  How it gets the words (the text window tells you which one it used):
    1. Straight from the page: exact words, nothing missed.
    2. If a page won't share its text, KonaSnap scrolls the box to the top, scrolls
       down through it, and reads the words off the screen. Don't move the mouse while
       it scrolls. Check the result once for small reading mistakes.
  The words are copied to the clipboard. In the text window, click "New Google Doc"
  to open a blank Google Doc, then press Ctrl+V.
- Full screen: the monitor your mouse is on.
- Capture in 3 seconds: time to open a menu first.
- Every picture capture is copied to the clipboard and saved in Pictures\KonaSnap.
  If PrtScn opens the Windows Snipping Tool instead, turn off
  Settings > Accessibility > Keyboard > "Use the Print screen key to open screen capture".

COURSE QUESTION -> GOOGLE DOC -> ASSIGNMENT PORTAL
1. Open the lesson. Press Ctrl+Alt+T and click the lesson box (wheel to adjust).
2. Click "New Google Doc" (or open your own doc) and press Ctrl+V. Write your answer there.
3. Select your answer in the doc, press Ctrl+C, and paste it into the assignment portal.

MARK UP (editor tools and shortcuts)
A Arrow   L Line   R Box   E Circle   H Highlight   P Pen   T Text   N Numbered step
S Spotlight (dims everything else)   M Magnifier lens   B Blur   C Crop
Hold Shift: straight lines at 45° steps, perfect circles and squares.
Each tool remembers its own color (the highlighter starts yellow).
Ctrl+Shift+C Copy the words in the image (long scrolling captures are read in slices)
Ctrl+Z Undo   Ctrl+Y Redo   Ctrl+C Copy   Ctrl+S Save   Ctrl+N New capture
Ctrl+mouse wheel Zoom   Ctrl+0 Fit   Ctrl+1 Actual size
Changes save automatically when you close the editor.
