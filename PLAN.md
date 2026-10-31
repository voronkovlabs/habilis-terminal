# Habilis Terminal — Implementation Plan (v1)

Each stage ends with a verifiable result. A stage is done only when its checks pass.
All automated tests and experiments run in temporary directories only (see SPEC §16).

## Stage 1 — Walking skeleton

Scope:
- `git init`, `.gitignore`, `.editorconfig`, `LICENSE` (MIT), `Directory.Build.props`, `Directory.Packages.props`.
- Solution with all projects from SPEC §16 (empty where not needed yet) and one passing sandbox test.
- Re-check package versions on NuGet; add `EasyWindowsTerminalControl`, `CI.Microsoft.Terminal.Wpf`,
  `Microsoft.Windows.Console.ConPTY`.
- WPF window with a live `pwsh` in the terminal control (user profile loaded, no integration script yet).
- Empty panels layer (two placeholder panels styled with the bundled Dark Modern colours).
- Layout from SPEC §3: both / left / right / none; Ctrl+O, Ctrl+F1, Ctrl+F2; outer window keeps its size;
  terminal snapped to cells; PTY resized on every transition.
- Focus model: Ctrl+Tab, mouse click, global intercepts (Ctrl+O, Ctrl+Tab, Alt+F4); printable keys from panel focus go to the terminal.
- Per-Monitor V2 manifest.

Done when:
- `dotnet build` and `dotnet test` pass.
- Side-by-side manual checklist against Windows Terminal with the same profile, all identical:
  oh-my-posh prompt (colours, Nerd Font glyphs, multi-line prompt), PSReadLine predictions (inline and list view, F2),
  Ctrl+R history search, syntax colouring, Ctrl+Space completion menu, Unicode / emoji, `winget` progress bar,
  a full-screen TUI program, Ctrl+C interrupt.
- Toggling Ctrl+O / Ctrl+F1 / Ctrl+F2 repeatedly while a prompt and while a long-running command are on screen:
  output reflows correctly, nothing is corrupted, window size never changes.
- With panels hidden, every key reaches the shell except Ctrl+O and Alt+F4.
- Moving the window between monitors with different scaling keeps the terminal sharp and correctly sized
  (or the gap is documented with a fix plan).
- A short findings note on the risks: terminal control quality, resize behaviour, DPI.

## Stage 2 — Shell integration

Scope: `HabilisTerminal.ps1` (prompt wrapper, OSC 133 / 9;9 / 7, PSReadLine handlers on F13–F24),
OSC parser on the ConPTY output, session channel directory, win32-input-mode key injection, shell state tracking
(idle / busy), deferred cd, run-file-through-shell, Ctrl+E / Ctrl+X / Ctrl+Space translation.

Done when:
- Shell tests (real pwsh, `-NoProfile` + script) prove: correct OSC sequences; cd from the channel preserves typed text
  and cursor position and adds nothing to history; run-file adds the command to history and restores typed text;
  non-filesystem locations emit no cwd.
- Manual: with the user's real profile and oh-my-posh, the prompt looks exactly as before; `cd` in the shell updates the
  placeholder panel's path label; a test command from the panel layer changes the shell cwd without touching typed text.

## Stage 3 — Core: directory model and enumeration

Scope: entry model, `FileSystemEnumerable`-based reader with batches, long-path / UNC helpers, sorting (all modes,
logical name order), masks (Far syntax), selection model, change watcher with batching and overflow re-read,
sandbox guard for tests.

Done when:
- Unit tests for sorting, masks, long paths, UNC path handling, symlink / junction detection, watcher batching.
- BenchmarkDotNet on a generated 100k-file temp directory: full enumeration + sort ≤ 500 ms, first batch ≤ 150 ms,
  memory ≈ 60 MB or less.

## Stage 4 — Panels

Scope: custom-drawn list control; view modes Ctrl+1…5, Ctrl+0; column headers and resizing; sorting keys and menu;
navigation keys; selection (Ins, Gray + / − / *); Alt+letter quick search; Ctrl+H; Enter; Ctrl+PgUp, Ctrl+\\;
Ctrl+U, Ctrl+R; Ctrl+Enter, Ctrl+[ / ], Ctrl+F, Ctrl+Ins family; active panel ↔ shell cwd sync; drive menu
(Alt+F1 / F2) with places; info panel (Ctrl+L); folder history (Alt+F12); splitter resizing (Ctrl+arrows, mouse).

Done when:
- 100k-file temp directory: first screen ≤ 150 ms, smooth scrolling (no frame drops visible in a profiler trace).
- Manual checklist covering every key in SPEC §5 that belongs to this stage.
- Navigating in the active panel changes the shell cwd; `cd` in the shell moves the active panel; inactive panel never affects the shell.
- Drive menu never blocks with a disconnected network drive (simulated with an unreachable mapped path).

## Stage 5 — Themes and icons

Scope: VS Code theme loader (JSONC, `include`, defaults by `type`); bundled Dark Modern / Light Modern;
Windows light / dark and accent colour; manual theme from `themes\`; live switching including terminal palette;
`tools/IconBuilder`; Seti and Material icon themes; Codicons; system icons for `.exe` / `.ico` / `.lnk`; overlays;
`ThirdPartyNotices.txt`.

Done when:
- Theme tests: a set of real VS Code theme files loads; missing keys fall back correctly; `include` resolves.
- Switching Windows between light and dark recolours panels and terminal live; accent colour change is picked up live.
- Icons are crisp at 100 / 150 / 200 %; switching icon theme works live.

## Stage 6 — Core: file operations

Scope: operation engine with progress, cancellation, pause, per-disk queue; copy (`CopyFile2`), move (`MoveFileEx`,
cross-volume copy + per-item delete), mkdir, delete (Recycle Bin via `IFileOperation`, permanent tree walk),
attributes (incl. compression and EFS), compare folders; conflict and error callbacks; Restart Manager lookup;
Far destination masks; reserved-name validation.

Done when:
- Tests in sandbox directories cover: every conflict choice; copy / move of symlinks and junctions with default and
  "contents" options; delete never enters link targets (target content verified intact); long paths > 260 chars;
  read-only, hidden, system files; locked file (process name reported); access denied via ACL; cancellation mid-file
  leaves no partial target; cross-volume move never loses data on error (uses a sandbox on a second volume configured
  via an environment variable, e.g. `HABI_TEST_SECOND_VOLUME`; skipped when not set); Recycle Bin delete on a temp
  directory; mask renames;
  compare folders with FAT time tolerance.

## Stage 7 — File operations UI

Scope: F5 / Shift+F5, F6 / Shift+F6, F7, F8 / Shift+Del, Ctrl+A dialogs; conflict dialog; error dialog;
"Cannot move to Recycle Bin" dialog; non-modal progress with Cancel / Pause; parallel operations; cursor placement rules;
`deleteToRecycleBin` setting.

Done when:
- Manual checklist of every dialog path on a temp directory tree (generated by a script in the scratch area).
- UI stays responsive during a multi-GB copy; cancel and pause work; two operations on different disks run in parallel,
  on the same disk are queued.

## Stage 8 — Viewer (F3) and quick view (Ctrl+Q)

Scope: memory-mapped viewer, text / hex, wrap, encodings, search, go to; images; F3 on a directory computes size.

Done when:
- A generated 5 GB temp log opens instantly and scrolls smoothly; search finds text near the end.
- 1251 and 866 encoded test files display correctly after switching encoding.

## Stage 9 — Editor (F4)

Scope: text detection; AvalonEdit integration with theme colours; Far editor keys; encoding / BOM / line-ending
preservation; atomic save via `ReplaceFile`; read-only and external-change handling; large-file prompt;
binary files to the shell `edit` / `open` verb; F6 viewer ↔ editor switch; syntax highlighting.

Done when:
- Round-trip tests: open + save without edits leaves files byte-identical for UTF-8 (with / without BOM), UTF-16, 1251,
  CRLF, LF and mixed endings.
- Save keeps attributes and creation time; read-only prompt works; external modification is detected.

## Stage 10 — Settings, state, menus, help

Scope: settings.json with schema, live reload, error notification; state.json; portable mode; Settings UI page
(generated from the schema); F9 top menu; F1 keyboard help; F10 exit confirmation; Alt+F11 history.

Done when:
- Editing settings.json (externally or via the built-in editor) applies live; an invalid file keeps last valid values and
  shows the line number.
- Restart restores panels, modes, sorting, splitters, window placement.
- Portable zip run from a temp directory stores everything next to the exe.

## Stage 11 — Terminal polish, DPI and monitors

Scope: mouse selection rules, right-click copy / paste, Ctrl+Shift+C / V, multi-line paste warning, bracketed paste,
scrollback, Ctrl+wheel; window placement across monitors; DPI edge cases.

Done when:
- Manual scenario from SPEC §13 passes (two monitors, 100 % / 150 %, unplug, live scale change).
- Clipboard behaviour matches Windows Terminal in a side-by-side check.

## Stage 12 — Packaging and release

Scope: Velopack packaging (x64, arm64), auto-update from GitHub Releases, portable zip, winget manifest, GitHub Actions
pipeline, About dialog with licenses, pwsh-missing screen.

Done when:
- Clean install on a fresh Windows user profile (or VM) without admin rights works; update from version N to N+1 applies
  after restart; uninstall leaves no program files behind (settings remain).
- CI produces installer, zip and release artefacts from a tag.
