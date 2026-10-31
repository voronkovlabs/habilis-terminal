# Stage 1 — Walking skeleton: findings

Date: 2026-10-31. Machine: Windows 11 Pro 10.0.26200, one monitor 1920×1080 at 100 %,
.NET SDK 10.0.201, pwsh 7.6.9, Windows Terminal 1.24.12741.

## What was built

- Solution per SPEC §16, central package management, MIT license, MTP test runner (`global.json`).
- `habi.exe`: WPF window, `WorkspaceLayout` (both / left / right / none, terminal snapped to cells),
  placeholder panels, focus model (Ctrl+Tab, click, printable keys from panels go to the shell),
  global intercepts (Ctrl+O always, Ctrl+Tab while panels are visible, Alt+F4), dark title bar,
  Per-Monitor V2 manifest.
- Own ConPTY layer: `HabilisTerminal.Shell.ConPty.ConPtySession` + `App.Hosting.ShellConnection`
  (an `ITerminalConnection` for `Microsoft.Terminal.Wpf.TerminalControl`).

## Decision taken during the stage: EasyWindowsTerminalControl dropped

`EasyWindowsTerminalControl` 1.0.38 creates the pseudo console with flags `0`. Windows Terminal uses
`PSEUDOCONSOLE_RESIZE_QUIRK | PSEUDOCONSOLE_WIN32_INPUT_MODE`. SPEC §7 also needs the raw output stream
(OSC parsing) and key injection, so the ConPTY layer is now ours (~250 lines) and only
`CI.Microsoft.Terminal.Wpf` + `Microsoft.Windows.Console.ConPTY` remain as dependencies.
Verified at runtime that `OpenConsole.exe` from the package is the host (not the inbox conhost).

Build quirk: the ConPTY package decides which `OpenConsole.exe` to copy from `PlatformTarget` before the
RID is applied; the app csproj sets `ConptyRequiresx64Host` / `ConptyRequiresARM64Host` explicitly.

## Checklist results (automated keystrokes + window screenshots)

| Check | Result |
|---|---|
| Live pwsh with the user profile (conda hook → `(base)` prompt) | ✅ |
| Printable keys typed with panel focus move focus to the terminal and run the command | ✅ |
| Ctrl+O / Ctrl+F1 / Ctrl+F2: all four layouts, outer window size unchanged | ✅ |
| PTY resized on every transition, long lines reflow correctly | ✅ |
| Full-screen TUI (Microsoft Edit `edit.exe`) at 10 rows and full window, resized live | ✅ |
| Ctrl+O intercepted while a TUI program runs | ✅ |
| Ctrl+O twice while a command streams output: no corruption | ✅ |
| PSReadLine: syntax colouring, inline prediction, list view (F2 reaches the shell) | ✅ |
| Ctrl+C interrupts a running command | ✅ |
| Unicode: Cyrillic, box drawing, CJK, colour emoji, skin-tone modifier | ✅ |
| Alt+F4 closes the window; OpenConsole child exits | ✅ |
| Shell exit (`exit`) closes the window | ✅ |
| oh-my-posh 31.9.1 (default theme), side by side with Windows Terminal | ✅ identical output (see below) |
| Moving between monitors with different DPI | ⏳ only one monitor available |
| Side-by-side "feel" (latency, scrolling) vs Windows Terminal | ⏳ needs the user |

## oh-my-posh check

Installed with winget (user scope, MSIX alias in `%LOCALAPPDATA%\Microsoft\WindowsApps`) and loaded only in
test sessions through a development variable (`HABI_SHELL_ARGS`, renamed `HABI_SHELL_INIT` in stage 2); the user profile was not modified.
The same command line was run in habi.exe and in Windows Terminal:

- Segments, colours, right-aligned block, transient prompt, PSReadLine colouring and predictions: identical.
- Powerline / Nerd Font glyphs render as the same fallback symbols in both, because Cascadia Mono has no Nerd Font
  glyphs. A Nerd Font (e.g. CaskaydiaCove NF) is needed for the intended look — a user setting (SPEC §10).
- After making the terminal narrower, the right-aligned block wraps onto the next line. Windows Terminal shows the same
  artefact (ConPTY reflow of a line padded to full width; PSReadLine does not redraw the prompt on resize).
- The user's PATH does not contain `%LOCALAPPDATA%\Microsoft\WindowsApps`, so `oh-my-posh` (and `winget`) are not found
  by name in either terminal until that folder is added to PATH.

## Observed behaviour worth a decision

**Growing the terminal keeps text top-anchored.** After Ctrl+O the last rows stay at the top and the area
below is empty; earlier output is in the scrollback. Windows Terminal 1.24 behaves the same way
(verified with a grown WT window), so this matches the "as in Windows Terminal" requirement.
It differs from Far, where hiding panels shows the console filled up to the prompt. Options for later:
keep as is, or scroll the control's viewport so the prompt lands on the bottom row after growth.

## Known gaps / follow-ups

- Terminal scrollbar is the native light one; needs theming or hiding (stage 5 / 11).
- Cell size is estimated from font metrics; matches at 100 % on this machine, to be re-checked at 125–200 %.
- Splitter dragging and Ctrl+arrow resizing belong to stage 4.
