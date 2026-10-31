# Stage 4 — Panels: findings

Date: 2026-11-01.

## What was built

- `Core/Panels/PanelState`: path, sorted items with "..", cursor, selection, sort (repeat key reverses),
  selected-first, hidden filter, view mode, quick search (prefix or Far wildcards, wrap-around), target entries;
  `FolderHistory`. 10 unit tests.
- `Core/Drives/DriveCatalog`: instant drive list, per-drive details with a 2 s timeout, places (Home, Desktop,
  Downloads via known folder, Documents), network drive disconnect.
- `App/Panels/FileListView`: custom-drawn list (only visible rows, text cache), view modes Ctrl+1…5 and Ctrl+0,
  multi-column Brief/Medium scrolling by columns, headers with sort arrows, click-to-sort, resizable detail columns,
  hover, VS Code list colours, placeholder icons (stage 5 replaces them), link overlay, dimmed hidden/system files.
- `App/Panels/FilePanel`: header (folder + path), status line (current item or selection summary), error line,
  background loading (first batch, then full listing; refresh keeps the old listing until the new one is ready),
  `DirectoryWatcher` updates, Far keys, Alt+letter quick search (keyboard layout aware) with Ctrl+Enter /
  Ctrl+Shift+Enter for next / previous match.
- `App/Panels/InfoPanelView` (Ctrl+L): drive, directory, selection, read timings.
- `App/Widgets/QuickPick`: VS Code-like quick pick used for the drive menu (Alt+F1/F2, letters accept at once,
  Del disconnects / ejects with confirmation), folder history (Alt+F12), sort menu (Ctrl+F12) and mask input
  (Gray + / Gray −).
- Window: Tab, Ctrl+U, Ctrl+F1/F2, Ctrl+L, Ctrl+Enter / Ctrl+[ / Ctrl+] / Ctrl+F into the command line,
  Ctrl+Ins family to the clipboard, Ctrl+↑/↓ terminal height, Ctrl+←/→ split, draggable splitters, F10 exit,
  Enter on a file runs `& .\name` through PSReadLine.

## Verified in the app (screenshots, temp test trees only)

- Full / Brief / Detailed modes, sorting by name and size, hidden file dimmed, junction shown as "Link".
- Ins selection (yellow), Alt+R quick search, Gray + with `*.json`, Ctrl+L info panel, Alt+F2 drive menu with free
  space bars and places, Ctrl+F12 sort menu, Alt+F12 history.
- 100 000-file directory: first screen 23 ms, whole listing read and sorted 299 ms (Debug build);
  End / PgUp in Brief mode instant.
- Enter on `hello.ps1` ran it in the shell (in history), Ctrl+Enter inserted the name, Ctrl+U swapped panels,
  Ctrl+↑ and Ctrl+→ resized.

## Fixed during the stage

- Drive menu Esc moved focus (and the shell) to the target panel; focus now returns to the previously active panel.
- Mask input appended to the initial `*.*`; the initial value is now selected so typing replaces it.
- WPF popups are separate windows, so `PrintWindow` does not capture them; UI checks use a screen capture limited
  to the window rectangle for popups.

## Not done here (later stages)

- Icons (stage 5), file operations (6–7), Ctrl+Q quick view (8), persistence of panel paths / modes / splitters
  and F9 menu / F1 help (10). Polling for unwatchable network directories is still open.
- The info panel queries the drive synchronously; fine for local drives, to revisit for slow network drives.
