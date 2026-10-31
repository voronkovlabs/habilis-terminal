# Stage 2 — Shell integration: findings

Date: 2026-10-31.

## What was built

- `Shell/Integration/OscScanner` + `OscDecoder`: streaming OSC detection (BEL and ST terminators, chunk splits) and
  decoding of OSC 133 A/B/C/D, OSC 9;9, OSC 7 and Terminal Commander messages (`OSC 7731;tcmd;…`).
- `Shell/Integration/ShellHost`: pwsh in a pseudo console with the integration script, shell state
  (Starting / Idle / Busy / Exited), current directory, and a request queue (`cd`, `run`, `insert`, `restore`)
  delivered through `%TEMP%\TerminalCommander\<pid>-<guid>\request.json` + the F24 key in win32-input-mode.
  Requests wait for the prompt; `cd` requests are coalesced (last one wins); 3 s timeout per request.
- `Shell/Scripts/TerminalCommander.ps1` (deployed as `shell\TerminalCommander.ps1`): wraps the existing `prompt`
  (after the user's profile / oh-my-posh), wraps `PSConsoleHostReadLine` for 133;C, registers the F24 handler.
  pwsh is started with `-NoExit -EncodedCommand` so paths with quotes are safe.
- `ConPtySession`: callbacks fixed before the read thread starts, per-process environment block (`TCMD_CHANNEL`),
  empty std handles (`STARTF_USESTDHANDLES`) so a parent with a console cannot leak its handles to the child.
- App: active panel follows `cd` in the shell; Ctrl+PgUp, Ctrl+\ and Tab in the panels change the shell directory
  without touching the typed command; Ctrl+E / Ctrl+X / Ctrl+Space from panel focus reach PSReadLine;
  cd errors are shown in the panel. WPF keyboard navigation is disabled (it moved focus on Tab / Ctrl+Tab
  behind our back).
- Dev hook renamed: `TCMD_SHELL_INIT` = PowerShell code run before the integration script.

## Verified

- ConPTY 1.25 passes every OSC we use to the terminal side, including the private 7731 (probe test).
- 26 shell tests with a real pwsh (`-NoProfile`, `HistorySaveStyle SaveNothing`, temp working directories):
  idle state and starting directory; command execution and shell `cd`; panel `cd` keeps typed text and cursor and
  adds nothing to history; cd errors are reported; cd while busy is applied at the next prompt (last wins);
  `run` executes through PSReadLine, lands in history, and the typed text is restored; non-filesystem locations
  (`Env:`) do not change the directory.
- In the app with the user's real profile (conda) and with oh-my-posh: shell `cd` → panel, Ctrl+PgUp / Ctrl+\ /
  Tab → shell with the typed command intact, Ctrl+E recalls history, focus stays where expected.

## Known issue: oh-my-posh redraw after a panel cd

After `InvokePrompt()` (used for panel → shell cd) the oh-my-posh prompt is drawn in a different style and Nerd Font
glyphs turn into `?`. The same happens in plain Windows Terminal without our script (verified with an F12 handler
calling `InvokePrompt()`), so it is an oh-my-posh / PSReadLine interaction. The typed command and the directory are
correct; only the redrawn prompt line looks different until the next regular prompt.
Options for later: report upstream; or redraw by other means (e.g. let oh-my-posh render the prompt itself).
