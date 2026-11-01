# Stage 11 — Terminal polish, DPI and monitors: findings

Date: 2026-11-01.

## What was built

- Clipboard (SPEC §12):
  - Ctrl+Shift+C / Ctrl+Shift+V while the terminal has focus (global intercept); Ctrl+C / Ctrl+V still go to the shell.
  - Right click without a selection pastes through our own path; with a selection the terminal control copies and
    clears it itself.
  - Every paste goes through `ShellHost.PrepareForPaste`: line breaks become CR (as typed Enter), and text is wrapped in
    ESC[200~ … ESC[201~ when the shell enabled bracketed paste (DECSET 2004, tracked across output chunks by
    `BracketedPasteTracker`). A pasted end marker is stripped, so pasted text cannot escape the bracket.
  - Multi-line paste asks "Paste N lines?" with a preview of the first lines (`terminal.multilinePasteWarning`).
- Mouse selection (drag, double / triple click, Alt block selection, Shift with mouse-mode programs) and wheel
  scrolling come from the Windows Terminal control unchanged.
- Ctrl+wheel over the terminal changes the font size for the session (6–48 pt on top of `terminal.fontSize`); the
  layout re-snaps to the new cell size.
- Window placement uses `GetWindowPlacement` / `SetWindowPlacement` (physical pixels, workspace coordinates) instead
  of WPF DIPs: the window comes back on its monitor, re-scaled by Windows when that monitor's DPI differs, maximised on
  that monitor, and moved into view when the monitor is gone.
- The `terminal.scrollback` setting from stage 10 was removed: the control's buffer is fixed at 9001 lines inside the
  native `HwndTerminal`, which matches SPEC, so a setting would have done nothing.

## Verified

- 2 new tests for the bracketed paste tracker (split sequences, CR line breaks, bracket wrapping, end-marker
  stripping). All 221 tests: 220 passed, 1 skipped (symlink privilege).
- In the app (portable copy under `%TEMP%\habi-tests`; the user's clipboard was saved and restored):
  - three-line clipboard + Ctrl+Shift+V → "Paste 3 lines?" with the preview; Paste ran the lines one by one (PSReadLine
    does not enable bracketed paste, so this matches Windows Terminal);
  - a single line pasted without a prompt;
  - three Ctrl+wheel steps enlarged the terminal font and the rows re-snapped;
  - state with a window at x = 4000 on a 1920-px screen opened fully visible at the right edge; a maximised state
    opened maximised; bounds round-trip exactly (the 7 px difference is the invisible resize border).

## Not verified here

- Two monitors with different scale factors, unplugging a monitor while running and changing the scale live: this
  machine has one 1920×1080 monitor, and changing the Windows scale is a system setting we do not touch. The code
  paths are Windows' own (`SetWindowPlacement`, per-monitor v2 manifest, `TerminalDpiChanged` in the control, our
  cell size recomputed on `DpiChanged`), which stage 1 also could not exercise with a single monitor.
- Clipboard side-by-side comparison with Windows Terminal beyond the cases above.
