# Stage 10 — Settings, state, menus, help: findings

Date: 2026-11-01.

## What was built

- `Core/Settings`:
  - `AppPaths`: `%APPDATA%\HabilisTerminal` (settings, themes) and `%LOCALAPPDATA%\HabilisTerminal` (state), or
    everything next to `habi.exe` when a `portable` marker or `settings.json` lies beside it.
  - `SettingsCatalog`: every setting with category, title, description, type, default, range / values and
    "applies after restart"; generates `settings.schema.json` (draft-07), written next to settings.json and referenced
    through `$schema`, so editors offer completion.
  - `SettingsDocument`: JSONC parsing (comments, trailing commas) with 1-based line numbers for syntax errors, invalid
    values and unknown keys; single-property edits that keep the rest of the text, comments and line endings intact.
  - `SettingsStore`: load, debounced live reload through a file watcher, last valid values kept on a syntax error
    (whole file) or an invalid value (that key), only non-default values written.
  - `AppState` / `StateStore`: panels (path, view mode, sort, selected first, hidden files, visibility), active panel,
    terminal rows, splitter ratios, window placement, folder history, view / edit history; atomic writes, a broken file
    gives defaults.
- Settings replace the development variables (`HABI_THEME`, `HABI_USE_ACCENT`, `HABI_ICON_THEME`,
  `HABI_DELETE_TO_RECYCLE_BIN`, `HABI_SHELL_INIT`); only `HABI_TRACE` remains for debugging.
- Applied live: theme ("auto", a built-in name, a file in `themes\` or a path), accent, icon theme, panel font (rows
  and default column widths scale with it), hidden files, terminal font, viewer font / initial wrap, editor font, tab
  size, spaces, syntax highlighting, large-file threshold, delete target, delete and exit confirmations. Shell path,
  arguments, integration on / off, init command and scrollback apply on the next start.
- Errors in settings.json or a missing theme show a notification card with the line number and "Open settings.json".
- State is restored on start (the window only if it is still on a screen) and saved on exit and every minute. The
  shell starts in the restored folder of the active panel.
- F9 top menu (Left / Files / Commands / Options / Right): Far keys, VS Code styling, check marks for the current view
  mode, info / quick view and hidden files. Commands run the same key handlers as the keyboard, so the two cannot
  disagree. Options: Settings, Open settings.json, colour theme and icon theme pickers, keyboard help.
- Settings page modelled on the VS Code Settings editor, generated from the catalog: search (Ctrl+F, Esc clears),
  category list that follows scrolling, "Category: **Title**", description, a control per type (checkbox, number /
  text box with validation, drop-down through the quick pick, theme box with a picker), blue bar on modified values,
  "Reset to default", file location and "Open settings.json". Tab moves between controls on this page only.
- F1 keyboard help page; Alt+F11 view / edit history (Enter opens the file the same way again); F10 asks before
  exiting when `confirmExit` is on; closing the window by any means asks about unsaved editor changes.

## Verified

- 13 new Core tests: JSONC values, syntax error line, invalid / unknown values with lines, case-insensitive enums,
  comment-preserving set / add / remove, empty file / object, store keeping last valid values, defaults not written,
  live reload through the watcher, schema covers the catalog, portable detection, state round trip and broken state
  file. All 219 tests: 218 passed, 1 skipped (symlink privilege).
- In the app, run as a portable copy from `%TEMP%\habi-tests\portable` (nothing was created in `%APPDATA%` or
  `%LOCALAPPDATA%`): state.json and settings.schema.json appeared next to the exe; writing settings.json externally
  switched to Light Modern and a 16 px panel font at once; a missing comma showed "settings.json, line 4" with the old
  values kept; fixing it removed the card. F9 menu, Options → Settings, search "accent", Tab, Space wrote
  `"useAccentColor": false` and recoloured the UI; "Open settings.json" opened the editor and Esc returned without
  moving the panel; F1, Alt+F11, F10 → No / Yes. After restart: Medium view on the left, size sort on the right,
  right panel active, taller terminal, shell in the right panel's folder.

## Notes

- The Settings page and the schema are generated from the same C# catalog rather than the page parsing the schema
  file: one source, no JSON Schema interpreter needed.
- WPF combo boxes do not pick up the theme without a full template, so drop-downs are buttons that open the quick
  pick — closer to VS Code anyway.
- Removing the last property leaves a trailing comma behind the previous one; JSONC allows it and VS Code accepts it.
- Window placement is checked against the virtual screen only; per-monitor DPI and unplugged monitors are stage 11.
