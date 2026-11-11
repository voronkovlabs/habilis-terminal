# Habilis Terminal — Specification (v1)

Status: agreed during the design interview on 2026-10-31.
Product name: **Habilis Terminal**. Executable: **`habi.exe`**. License: **MIT**.
UI language: English. Code, identifiers and comments: English.

## 1. Concept

A Windows file manager in the spirit of Far Manager, built on .NET.

- One window. Its base is a real terminal: PowerShell 7 (`pwsh.exe`) hosted through ConPTY.
  PSReadLine, oh-my-posh and TUI programs must behave exactly as in Windows Terminal.
- Two file panels with real graphics (not character cells). Visual style follows VS Code
  (Explorer lists, quick pick, command-palette-like dialogs). VS Code is a **visual reference only**;
  no VS Code behaviour or features are borrowed (no command palette, no keybindings.json; Ctrl+P is Far's).
- Keyboard shortcuts follow Far Manager as closely as possible, except where this spec explicitly deviates.
- Panels and terminal **never overlap**: they share the window in a grid layout (see §3).

### Out of scope for v1
Plugins, macros, archives, FTP, key rebinding, command palette, wipe (Alt+Del), descriptions
(`descript.ion`), owner and hard-link columns, tree panel (Ctrl+T), screens switching (F12),
recursive / content-based folder compare, mapping network drives.

## 2. Technology

| Item | Decision |
|---|---|
| Runtime | .NET 10 (LTS, supported until 2028-11-14), C# |
| UI | WPF |
| Terminal control | `CI.Microsoft.Terminal.Wpf` 1.25.260303002 (community repack of the Windows Terminal WPF control) + `Microsoft.Windows.Console.ConPTY` 1.25.260930003 (current conpty/OpenConsole) + own ConPTY session layer in `HabilisTerminal.Shell` (flags `RESIZE_QUIRK | WIN32_INPUT_MODE`, as Windows Terminal). `EasyWindowsTerminalControl` was dropped in stage 1, see `docs/stage1-findings.md` |
| Editor component | `AvalonEdit` 6.3.1.120 (MIT) |
| Installer / updates | `Velopack` 1.2.x (MIT) |
| Tests | xUnit; BenchmarkDotNet for performance |

Package versions were checked on NuGet on 2026-10-31 and must be re-checked when the solution is created.

Rejected alternatives: overlay window on top of the terminal (not needed once panels and terminal
stopped overlapping), WebView2 + xterm.js (UI would not be C#), Avalonia + own VT renderer
(too large), WinUI 3 (no packaged terminal control), C++ / fork of Windows Terminal (expensive to build and maintain).

**Risk:** `CI.Microsoft.Terminal.Wpf` is a community package and may lag behind Windows Terminal.
Fallback options if it proves unusable: build the WPF control from the Windows Terminal sources ourselves.

## 3. Window layout

The window draws its own title bar (VS Code style, `WindowChrome`): the app icon and the menu headers
Left / Files / Commands / Options / Right on the left, the caption buttons on the right, and the empty
rest drags the window like a normal caption. A click on a header opens that menu, as F9 does. The
maximize button supports Windows 11 Snap Layouts.

The outer window never changes size when panels are shown or hidden. Only the terminal area
(and therefore the PTY size) changes.

| Visible panels | Layout |
|---|---|
| Both | Panels on top (left / right), terminal below across the full width |
| Left only | Left panel on the left, terminal on the right, full height |
| Right only | Right panel on the right, terminal on the left, full height |
| None | Terminal fills the whole window |

- Terminal height under the panels: default **10 rows**, minimum 2 rows; the panels must keep at least
  a few list rows. Changed with **Ctrl+Up / Ctrl+Down** (panel focus) or by dragging the splitter. Remembered.
- Width of a single visible panel: default half the window; changed with **Ctrl+Left / Ctrl+Right**
  (panel focus) or by dragging the splitter. Remembered.
- Splitter positions are stored in terminal rows / columns, not pixels.
- Terminal area is always snapped to whole character cells.
- Every layout transition resizes the PTY (columns and rows); the shell and running programs reflow.
- **Ctrl+O** hides all visible panels; pressing it again restores the previous set (both or one) and the
  previously active panel. Hiding both panels one by one (Ctrl+F1, Ctrl+F2) is equivalent to Ctrl+O.
- Viewer, editor and settings open as full-window pages over panels and terminal.

## 4. Focus and input routing

There are exactly two focus targets: **panels** and **terminal**. No conditional, "is the command line empty" logic.

- **Ctrl+Tab** toggles focus between panels and terminal (only while panels are visible).
- A mouse click moves focus to what was clicked.
- When panels are hidden, focus is always on the terminal. When panels are shown with Ctrl+O, focus moves to the panels.
- Panel keys at the prompt (as in Far): while the shell waits at its prompt, **Ctrl+P, Ctrl+U, Ctrl+F1 / Ctrl+F2,
  Alt+F1 / Alt+F2, Ctrl+L and Ctrl+Q** act on the panels even with the terminal focused (Ctrl+L then does not clear the
  screen). A running program (TUI, ssh) gets every key; with no panels shown, Ctrl+L and Ctrl+Q go to the shell.
- Focus is visible: the active panel uses `list.activeSelectionBackground`, the inactive one
  `list.inactiveSelectionBackground`; the terminal shows a blinking cursor when focused and a hollow one otherwise.

### Panel focus
All keys are handled by the panels, except:
- **Printable characters** (including Space) move focus to the terminal and are sent to the shell.
  Typing a command with panels visible works like in Far; focus stays in the terminal afterwards.
- Quick search is **Alt+letter**, as in Far.
- **Ctrl+Alt+letter is a quick filter (#201):** the same box, but the panel shows only files and folders whose names
  contain the text, in any case. Enter keeps the filter, and its box stays below the list with a cross that
  clears it (#205); Esc clears it, in the box or on the panel; another folder clears it. Marks on hidden names are dropped.
  Every Ctrl+Alt+letter starts it: copying full paths moved to Ctrl+Shift+C and Ctrl+Shift+Ins (#203).
- **Alt+F7 options (#209, #210, #211).** The text can be a regular expression, matched within a line (a mask written
  `/expression/` matches names by one, as in Far). "Search in archives" looks inside files plugins open as folders,
  through `Plugin.SearchFile` (SDK 1.7): the plugin decides what the search sees; masks apply to the entries' names,
  results show as `archive\inner\path`, Go to opens the archive there, View and Edit show a temporary copy, and the
  temporary panel leaves them out. Text searches are cached per query (last 8, under the state folder): searching the
  same text again reads only files and entries whose size or date changed.
- **Alt+F7 finds files (#198)**, as Far's "Find file": masks (Far syntax, exclusions after `|`), optional text inside
  (case, whole words; a text file in its own encoding, any other file as ANSI, UTF-8 and UTF-16), folders too when no
  text is given, and where: from the current folder, the current folder only, the selected folders, the root of the
  drive, all local drives. Links are not walked. Results come as they are found: Enter goes to the file, F3 views and
  F4 edits it and then returns to the results, Panel shows them in a temporary panel (names with their folders; F5, F6
  and F8 work on the real files, Ctrl+R checks them again, `..` leaves), Stop ends the search. Not searched: inside
  archives and plugin panels.

### goto
`goto <path or address>` typed at the prompt (#152) does what a paste into the drive menu does: a folder opens, a file's path opens its folder with the cursor on it, a plugin's address opens the plugin. It is also taken relative to the shell's folder, with `~` and `%VARIABLES%`. The line does not reach the shell, and goes into its history. A program waiting for input gets the line as usual.

### Terminal focus
All keys go to the shell / running program, except these globally intercepted ones:
- **Ctrl+O** — always (even while a program runs).
- **Ctrl+Tab** — only while panels are visible.
- **Alt+F4** — closes the window.
- **Ctrl+Shift+C / Ctrl+Shift+V** — terminal-level copy / paste.
- **Ctrl+Shift+P** — the command line in the title bar (#155): commands that apply to what is selected, with their keys; a pasted path or address opens that place.

## 5. Keyboard map (panel focus)

Far Manager defaults unless noted.

| Keys | Action |
|---|---|
| Up/Down, PgUp/PgDn, Home/End, Left/Right | Move cursor (Left/Right: the neighbouring column in multi-column modes, Home/End in one-column modes; never another panel) |
| Tab | Switch active panel. With one panel shown (Ctrl+P) the terminal stands in for the other panel: Tab moves focus to it, and Tab on an empty command line moves back (otherwise Tab completes) |
| Enter | Open directory; open an archive as a folder (§8.1); run a program or script (`PATHEXT`, `.ps1`) through the shell (§7); open any other file in its associated program (issue #52) |
| Ctrl+PgDn | Enter a directory, or open any file as an archive by its signature (§8.1) |
| Ctrl+PgUp | Parent directory |
| Ctrl+\\ | Root of the drive |
| Ins | Toggle selection and move down |
| Ctrl+click | Toggle selection (the right button opens the context menu instead of selecting, unlike Far) |
| Right click, Shift+F10, Menu key | Windows shell context menu (Explorer's): for the selection when the clicked item is marked (keys: when anything is marked), else for the item; on empty space or ".." for the folder itself. Its "Rename" runs Shift+F6 |
| Gray + / Gray − / Gray * | Select by mask / deselect by mask / invert selection |
| Alt+letter | Quick search |
| Ctrl+Enter | Insert file name into the command line |
| Ctrl+[ / Ctrl+] | Insert left / right panel path into the command line |
| Ctrl+F | Insert full file name into the command line |
| Ctrl+Ins, Ctrl+C | Copy names to the clipboard (the item under the cursor, or the selection; Ctrl+C only with a panel focused, issue #54) |
| Ctrl+Shift+Ins / Alt+Shift+Ins / Ctrl+Shift+C | Copy full names (paths) to the clipboard; Ctrl+Shift+C copies the console's selection while the console has the keys |
| Ctrl+E / Ctrl+X | Previous / next command in the shell history (sent to PSReadLine) |
| Ctrl+Space | Completion in the command line (PSReadLine `MenuComplete`) |
| Alt+F1 / Alt+F2 | Drive menu for the left / right panel |
| Alt+F12 | Folder history |
| Alt+F11 | View / edit history |
| Ctrl+F1 / Ctrl+F2 | Hide / show left / right panel |
| Ctrl+O | Hide / show panels |
| Ctrl+P | Hide / show the inactive panel (the terminal takes its place) |
| Ctrl+U | Swap panels (a hidden panel stays hidden; with one panel, panel and terminal swap sides) |
| Ctrl+R | Re-read panel |
| Ctrl+H | Toggle hidden and system files |
| Ctrl+L | Info panel (in the opposite panel; a hidden opposite panel is shown, and hidden again when the info goes; also at the prompt) |
| Ctrl+Q | Quick view (in the opposite panel; same as Ctrl+L for a hidden panel and at the prompt) |
| Ctrl+1…5, Ctrl+0 | View modes (§6) |
| Ctrl+F3…F9 | Sort by name / extension / modified / size / unsorted / created / accessed |
| Ctrl+F12 | Sort menu |
| Shift+F12 | Selected first (toggle) |
| Ctrl+A | Attributes |
| Ctrl+Up/Down, Ctrl+Left/Right | Resize terminal height / single-panel width |
| F1 | Help (keyboard map) |
| F3 / F4 | View / edit (§9) |
| Alt+F3 | Markdown files rendered (§9); any other file in its associated program, as Far's external viewer (issue #57) |
| F5 / Shift+F5 | Copy / copy the item under the cursor |
| F6 / Shift+F6 | Move-rename / rename the item under the cursor |
| F7 | Make directory |
| F8 / Shift+Del | Delete (per setting) / delete permanently |
| F9 | Menu: Left / Files / Commands / Options / Right, the headers in the title bar (VS Code-styled) |
| F10 | Exit (confirmation configurable, as in Far) |
| F2 | Rename the file or folder under the cursor: one field with the name, the part before the extension selected; the cursor stays on it; nothing is overwritten (#159) |
| F11 | Unassigned in v1 |

Compare folders has no default hotkey (Far behaviour); it is available from F9 → Commands.

## 6. Panels: view modes, sorting, info

### View modes
| Key | Mode | Columns |
|---|---|---|
| Ctrl+1 | Brief | Names in several columns |
| Ctrl+2 | Medium | Two columns, each name with a narrow size column |
| Ctrl+3 | Full | Name, size, date, time |
| Ctrl+4 | Wide | Name, size |
| Ctrl+5 | Detailed | Name, size, all dates, attributes |
| Ctrl+0 | Alternative full | As in Far |

New panels start in Medium, as in Far.

- VS Code list / table look: column headers, file icons in all modes, resizable columns, hover and focus styles.
- Clicking a column header sorts by that column.

### Folder sizes
- Every size column shows file sizes at once and folder sizes as they are computed in the background (issue #36):
  one thread at the lowest priority in Windows background mode (lower disk priority too), visible folders first,
  re-ordered on scroll; leaving the folder stops its counting. Two panels on the same folder count it once.
- Only on local fixed drives. On network and removable drives folders are sized by F3 alone.
- A folder not counted yet shows an empty cell; the running total is dimmed; the final value is in the normal colour.
  A folder with unreadable parts shows a lower bound, e.g. "≥ 1.2 GB". Links show their type in the size column
  (Junction, Mount point, Symlink, WSL link, App alias), and neither count nor are entered; a cloud folder is no link and counts (#178).
  Ctrl+A on a link shows where it leads, where it ends, whether the target exists, and a file's other names. On local
  fixed drives a file on screen with several names gets a badge, and the status line says how many (#180);
  hard links may be counted twice. ".." has no size. Repaints happen at most a few times a second.
- Sizes are cached for the app's lifetime and shared by both panels, F3, the status line and the selection totals,
  so a folder never shows two numbers. Ctrl+R and the app's own copy, move, delete and editor saves drop the sizes
  of the affected folders and every folder above them; they are counted again.
- No live re-sort: when sorting by size, folders take their sizes into account on the next sort or Ctrl+R, and the
  cursor never jumps.
- Setting `panels.folderSizes` (default `true`) turns automatic counting off; F3 still works.

### Sorting
Name, extension, modified, size, unsorted, created, accessed; sort menu (Ctrl+F12) with
"directories first" and "selected first". Pressing the same sort key again reverses the direction.
Name comparison uses Explorer-style logical order (`StrCmpLogicalW`) by default.

### Info panel (Ctrl+L)
Drive letter, label, file system, total and free space; summary of the current selection.

### Drive menu (Alt+F1 / Alt+F2)
- Quick pick shown over the target panel. Each row: drive type icon, letter, label, file system,
  usage bar and "X free of Y".
- Drives ordered by letter, followed by a "places" group: home, Desktop, Downloads, Documents,
  the other panel's directory.
- Network and removable drives are queried asynchronously; rows appear immediately, sizes fill in later.
  A disconnected network drive is marked and never blocks the menu.
- Keys: drive letter selects immediately; Del disconnects a network drive / safely ejects a removable one
  (with confirmation); Ctrl+R refreshes; Esc closes.
- Every item but the drives (plugin items, places) has a hotkey, as in Far (issues #66, #72): automatically 1, 2,
  3... 9, 0 in menu order, then letters no drive uses. F4 on an item assigns its own letter or digit (a drive's
  letter is refused; a key taken by another item moves; empty goes back to automatic), and the automatic keys skip
  it. Items show as `1: S3 buckets`; the key picks the item at once. Assigned keys are kept in the state file
  (`PluginHotkeys`, by plugin id or "places" and the title).
- Cursor starts on the panel's current drive. Choosing a drive opens the last directory the panel
  visited on it, otherwise the root.
- UNC locations are reached with `cd \\server\share` in the shell; the active panel follows.

## 7. Shell integration (pwsh)

- Only PowerShell 7+ is supported. pwsh is located via `PATH` and standard install locations
  (MSI, Store, winget). If missing, a screen explains how to install it (`winget install Microsoft.PowerShell`).
- Launch: `pwsh -NoLogo -NoExit -EncodedCommand <". '<app>\shell\HabilisTerminal.ps1'">` (encoded, so any path is safe).
  The user's profile runs first as usual; user files are never modified. Integration can be disabled by a setting.
- The script **wraps the existing `prompt` function** (after the profile and oh-my-posh have defined it) and emits:
  - OSC 133;A/B (prompt ready), 133;C/D (command started / finished);
  - OSC 9;9 (Windows Terminal cwd format) and OSC 7, only when the location is on the FileSystem provider
    (`$PWD.ProviderPath`, so UNC works). Non-filesystem locations (e.g. `HKLM:`) leave the panel unchanged;
  - private messages as `OSC 7731;habi;<name>;<args>` (`ack;<id>;ok|error;<text>`, `restore-pending`).
    ConPTY 1.25 passes all of these through (verified in stage 2).
- The script registers **PSReadLine key handlers on F13–F24**, which we send through ConPTY via win32-input-mode.
- **Panel → shell cd:** the target path is written to the session channel directory
  (`%TEMP%\HabilisTerminal\<pid>\`), then F24 is sent. The handler runs `Set-Location -LiteralPath`
  and `[PSConsoleReadLine]::InvokePrompt()`. The typed text and cursor position are preserved; nothing is added to history.
- **Enter on a file → run through the shell:** the handler saves the typed text, replaces the buffer with
  `& '.\file.ext'`, accepts the line (it goes to history, as in Far), and restores the saved text after the next prompt.
- **Synchronisation follows the active panel:** its navigation changes the shell cwd; `cd` in the shell changes it.
  The inactive panel never affects the shell.
- **Shell busy** (between 133;C and the next prompt): panel navigation happens immediately; the shell cd is
  deferred to the next prompt, last value wins. If the command itself changed the directory, the shell report
  arrives later and the active panel follows the shell.
- The OSC parser runs on the ConPTY output stream; unknown sequences pass through untouched.

## 8. File operations

Common rules:
- Long paths everywhere via `\\?\` and `\\?\UNC\` prefixes.
- All operations are asynchronous, cancellable, report progress (bytes and items) and never block the UI.
- Progress is shown as a non-modal bar / notification (VS Code style) with Cancel and Pause.
  Several operations may run in parallel; operations targeting the same physical disk are queued.
- Error dialog: Win32 error text, plus the locking process name when a file is in use (Restart Manager API).
  Choices: Retry / Skip / Skip all / Cancel.
- Panels refresh through `FileSystemWatcher` (§11).
- Names Windows cannot handle normally (`CON`, `NUL`, …, trailing dots or spaces, invalid characters) are rejected
  even though `\\?\` would allow them.

### F5 copy / Shift+F5
- Dialog: destination prefilled with the other panel's path; options "Existing files: ask / overwrite / skip /
  rename / append / only newer", "Copy access rights", "Copy contents of symbolic links".
- Destination semantics: ends with `\` or is an existing directory → copy into it; otherwise for a single file it is
  the new name; for several items a non-existing path is created as a directory.
- Far-style masks in the destination name (`*.bak`, `*.*`, `new_*`, `?`). No preview (as in Far).
- Engine: Win32 `CopyFile2` via P/Invoke: progress callback, mid-file cancellation, copies ADS, attributes and
  modification time; unbuffered I/O for large files (≈256 MB and more).
- Symbolic links are copied **as links** by default (`COPY_FILE_COPY_SYMLINK`); the dialog option copies contents.
  Junctions are **not traversed**; they are recreated as junctions pointing to the same target.
  Creating a symlink without privileges (admin or Developer Mode) is an error with "Copy contents / Skip".
- Access rights are not copied by default (inherited from the target, as Explorer); optional.
- Conflict dialog: both files with size, date, icon and which one is newer. Overwrite / Skip / Rename
  (suggests `name (2).ext`) / Append / Only if newer / Cancel + "apply to all". Separate confirmation for
  read-only, hidden and system targets, as in Far.
- Before start: background size count, free-space check with a warning, refusal to copy a directory into itself.
- On cancel the partially written target file is deleted; completed files remain.
- After completion the cursor in the target panel is placed on the last copied item.

### F6 move-rename / Shift+F6
- F6 dialog prefilled with the other panel's path; Shift+F6 prefilled with the item's name.
- Same volume: `MoveFileEx` — atomic and instant, including directories and case-only renames.
- Different volume: copy with the F5 engine (same options, conflicts, errors), then delete the source
  **per item, only after that item was copied successfully**. On error or cancel nothing un-copied is lost.
  Read-only is cleared on the source before deletion.
- Far-style masks in the destination name; no preview.
- Moving a directory into itself is refused.

### F7 make directory
- Nested paths (`a\b\c`) are created in full; several names separated by `;` (Far 3 "process multiple names").
- Existing directory: no error, cursor moves onto it. Existing file with that name: error.
- Cursor moves onto the created directory.

### F8 / Shift+Del delete
- Setting `deleteToRecycleBin` (default `true`): F8 moves to the Recycle Bin; with `false` F8 deletes permanently.
  **Shift+Del always deletes permanently.**
- Recycle Bin: shell `IFileOperation` with `FOF_NO_UI` and `IFileOperationProgressSink`, on a dedicated STA thread,
  cancellable; our own progress and error UI.
- When the Recycle Bin is not available (network / UNC, removable media, path too long for the shell, item larger
  than the bin limit): "Cannot move to Recycle Bin: <reason>. Delete permanently?" — Yes / Yes to all / Skip / Cancel.
  Nothing is ever deleted permanently silently.
- Permanent delete: own tree walk with `DeleteFile` / `RemoveDirectory` on `\\?\` paths, read-only cleared first,
  progress, cancellation between files.
- **Links:** the link itself is deleted; directory symlinks and junctions are **never** entered; the target is untouched.
  Covered by dedicated tests.
- Confirmations as in Far: overall; per non-empty directory with "All" (permanent delete only);
  read-only / hidden / system with "All".
- Errors: access denied, file in use (with process name), directory not empty because of files appearing during deletion.
- After deletion the cursor moves to the item following the deleted ones.

### Ctrl+A attributes
- Editable: read-only, archive, hidden, system, not content indexed, temporary, compressed
  (`FSCTL_SET_COMPRESSION`), encrypted (EFS `EncryptFile` / `DecryptFile`); compressed and encrypted are mutually exclusive.
- Read-only display: sparse, offline, reparse point with link target, pinned / unpinned (OneDrive), hard links.
- Dates: created, modified, accessed with "Current" and "Original" buttons.
- Multiple selection uses tri-state check boxes ("leave as is").
- Directories: "Apply to subfolders"; links are not traversed (same rules as copy).

### Compare folders (F9 → Commands)
- Top level only; files with equal names in both panels are compared. In each panel, files missing in the other
  or newer than the counterpart are selected.
- Time comparison tolerance 2 s when either side is FAT / exFAT; size comparison optional.
- Directories are compared by presence only. No content compare, no recursion.

### 8.1 Archives as folders (issue #51)
As Far's arclite, on 7-Zip's `7z.dll` (26.04, `third_party/7-Zip`). This is the plugin `plugins/Habilis.Archives` (#149), on the SDK's file formats (`docs/plugins.md`).
- **Enter** on a file with an archive's extension (zip, 7z, rar, tar, gz, bz2, xz, iso, cab...)
  opens it as a folder in a plugin panel; when it is no archive after all, Enter does what it does with any file.
  Everything else 7-Zip can read but that is a document or a program (doc, xls, docx, xlsx, jar, apk, exe, msi, disk
  images...) opens in its program.
- **Ctrl+PgDn** on any file tries every format by signature.
- The panel path reads `C:\data\a.zip\docs`; folders only implied by item paths are shown. ".." at the archive's top
  returns to its folder with the cursor on the archive file. Folder history reopens such locations.
- **F3 / Enter** on a file views it from a temporary copy; **F5** copies files and folders out (times kept, overwrite
  asked); an archive inside an archive (e.g. `.tar` in `.zip`) opens in place from a temporary copy.
- Encrypted archives ask for the password once per archive.
- **Writing** where 7-Zip writes the format (zip, 7z, tar, wim...): F5 / F6 into the archive (overwrite asked),
  F6 out (moves), F8, F7. Each change writes an updated copy `~<name>.<id>.tmp` next to the archive and then replaces
  it; on failure or cancel the archive stays as it was. An archive inside another archive is read-only.
- 7z.dll objects are created and called on thread-pool threads only (they have no COM proxies for the UI thread).
- Not yet (issue #63): Shift+F1 pack into a new archive, Shift+F2 extract, Shift+F3 test, multi-volume archives.

### 8.2 GitHub (issue #214)
The plugin `plugins/Habilis.GitHub`, read-only, at `gh:` (F11, the drive menu, the command line).
- Levels: owners (the signed-in user first), an owner's repositories (Created, Last commit of the default branch,
  Kind), a repository's branches (Created = first commit not on the default branch, Last commit, Ahead, Behind, last
  author), a branch's folders and files (Size, Last commit). Costly dates come in the background.
- F3 views, F5 copies out; F9 > Panel: Open in browser, Clone address, Refresh from GitHub, Sign out of GitHub.
- Sign-in: GitHub's device flow with the project's OAuth App (client ID in the plugin, replaceable in its settings).
  The token is kept in Windows Credential Manager (`HabilisTerminal:github`). Straight HTTPS to `api.github.com`,
  REST and GraphQL. Sign-in also through the GitHub CLI's token or a personal access token (#224).

## 9. Viewer (F3) and editor (F4)

Both open as full-window pages over panels and terminal. Esc / F10 closes and returns the cursor where it was.
F6 switches between viewer and editor, as in Far. One page at a time (no F12 screens).

### Viewer (F3, Ctrl+Q)
- Own lightweight control: memory-mapped file, lazy line index — multi-GB files open instantly.
- **Text mode for small files (issue #56):** text files up to `viewer.textModeMaxMB` (10 MB; 0 turns it off) show in a
  read-only AvalonEdit with the editor's syntax colours, line numbers, mouse selection and Ctrl+C. A line over 10 000
  characters turns syntax colours off in the viewer, quick view and editor, as they would freeze the window; a line over
  2 000 000 keeps the viewer and quick view in the paged control, and F4 asks first (#163). The viewer keys
  stay: F2 wrap, F4 hex (the paged control at the caret's line; F4 again returns), F7 / Shift+F7 / Alt+F7 search,
  F8 / Shift+F8 encoding, Alt+F8 go to line or percent (a `0x` offset opens hex there). Larger and binary files
  use the paged control. The page opens at once in the paged control; the text mode is built in the background and
  takes over at about the same place when ready (issue #76), so a large file never freezes the window.
- Text / hex modes, word wrap on/off.
- Encodings: auto-detection (BOM, UTF-8 heuristic), manual switch to UTF-8, UTF-16 LE/BE, ANSI (1251), OEM (866).
- Search (F7, Shift+F7 next), go to line / offset.
- Images (png, jpg, gif, bmp, ico, webp) via WPF decoders.
- F3 on a directory computes its size in the background (cancellable, junctions not traversed) and stores it in the
  shared folder-size cache (§6);
  the cursor stays on it, the panel's status line shows the number of files and their size, and the size
  column shows the size. With folders selected F3 counts all of them; the status line then shows the
  selection's files and size, the contents of the counted folders included. While counting, the status line
  shows "Counting <folder>…" with the running totals; moving the cursor or leaving the directory stops it.
- Ctrl+Q shows the same control as quick view in the opposite panel, by the same rule as F3 (issue #74): text files up
  to `viewer.textModeMaxMB` in the read-only editor with syntax colours, larger ones in the paged control. Choosing a
  drive or place in Alt+F1 / Alt+F2 for the panel showing the quick view (or info) turns it back into a file panel
  there, as in Far (issue #75).

### Markdown view (Alt+F3, issue #57)
- `.md`, `.markdown`, `.mdown`, `.mkd`: Markdig (advanced extensions: tables, task lists, footnotes, autolinks) renders
  the file to HTML, shown in WebView2 in a GitHub-like style with the colour theme's editor colours.
- Raw HTML in the file is escaped and the page allows no scripts. The file's folder is served as
  `https://md.habi.local/`, so relative images load. Links to other Markdown files open in the same view, web links
  in the browser, other local files in their associated program.
- F3 plain viewer, F4 / F6 editor, Esc / F10 / Alt+F3 close (the cursor stays on the file).
- WebView2 keeps its profile in the state folder (`WebView2`); without the WebView2 Runtime the page says so.

### Editor (F4)
- Text detection by content (first 64 KB): BOM → text; no NUL bytes and few control characters → text; otherwise binary.
  - Text → built-in editor (AvalonEdit).
  - Binary → system handler via the shell `edit` verb, falling back to `open`.
  - Text larger than 50 MB → "Large file. Open in editor / Open in viewer / Cancel".
- Far editor keys: F2 save, Shift+F2 save as, Esc / F10 exit (asks if modified), F7 find, Ctrl+F7 replace,
  Shift+F7 next, Shift+F8 encoding, Ctrl+Z / Ctrl+Y, Alt+mouse or Alt+Shift+arrows for block selection,
  Ctrl+F10 sync panel with the file.
- Saving preserves encoding, BOM and line endings (CRLF / LF; dominant style for mixed files).
  Atomic write: temporary file, then `ReplaceFile` (keeps attributes, ACL, creation time).
  Read-only file: "Clear the attribute and save?". File changed on disk: warning on save.
- Syntax highlighting as in VS Code (#207): the TextMate grammars VS Code ships (TextMateSharp), coloured by the
  theme's `tokenColors` with TextMate's own selector matching, font styles included. Files with no TextMate grammar
  use AvalonEdit's built-in definitions. Can be disabled. Other colours come from the theme (`editor.*`,
  `editorLineNumber.foreground`).

## 10. Themes and icons

### Colour themes
- Theme files use the **VS Code color theme format** (JSONC, `type`, `colors`, `tokenColors`, `include`).
  Workbench keys colour the UI; `terminal.ansi*`, `terminal.background`, `terminal.foreground`,
  `terminalCursor.foreground`, `terminal.selectionBackground` colour the terminal.
  Missing keys fall back to defaults by `type`, as in VS Code.
- **Source: Windows only.** Light / dark mode (`AppsUseLightTheme`) selects the bundled **Light Modern** /
  **Dark Modern** themes (from the MIT-licensed VS Code repository). The Windows accent colour tints focus and
  selection keys (can be disabled). VS Code `settings.json` is **not** read.
- The user may put VS Code theme JSON files into `themes\` and choose one manually in settings.
- Bundled **Far Manager** theme (`"theme": "far"`, issue #42): Far's classic blue scheme (navy panels, aqua text,
  white folder icons, yellow marked files, teal cursor with black text, black terminal with the legacy console
  palette). It keeps its own cursor colour: the Windows accent does not tint it. Pairs well with a monospace
  `panels.fontFamily` such as Consolas.
- Theme changes apply live without restart (`WM_SETTINGCHANGE`, file watcher). The terminal is recoloured
  together with the panels through `TerminalTheme`. Invalid theme → fallback to the bundled theme with a notification.

### File icons
- Support the **VS Code file icon theme format** (`iconDefinitions`, `fileExtensions`, `fileNames`, `folderNames`,
  `languageIds`, light variants). `languageIds` resolve through a bundled language → extensions table taken from
  VS Code built-in extensions.
- Bundled sets: **Material Icon Theme** (default since issue #50; MIT; icons from Material Design Icons and Material
  Symbols, Apache 2.0) and **Seti** (the VS Code default look; from `seti-ui`, MIT).
- **Codicons** (code MIT, content CC BY 4.0) for all UI glyphs.
- A build-time tool converts SVG and font glyphs to WPF `Geometry` resources; no runtime SVG dependency.
- `.exe`, `.ico`, `.lnk` icons come from the system, lazily, for visible rows only.
  Links and junctions get an arrow overlay; hidden files are drawn semi-transparent.
- `ThirdPartyNotices.txt` and a Licenses section in About list all third-party licenses and attributions.

### Fonts
- Terminal: Cascadia Mono 11 pt by default, fallback Consolas; configurable (a Nerd Font such as CaskaydiaCove NF can be
  chosen for oh-my-posh; Nerd Fonts are not bundled).
- Panels and dialogs: Segoe UI.

## 11. Performance

Targets for a 100 000-entry local NTFS directory on SSD:
- first screen ≤ 150 ms; fully sorted list ≤ 500 ms;
- scrolling and cursor movement at 60 fps without stalls;
- ≈ 60 MB memory per panel for 100k entries;
- the UI never blocks on I/O; slow network directories show a progress indicator and are cancelled when the user leaves.

Approach:
- Enumeration with `FileSystemEnumerable` (one `NtQueryDirectoryFile` pass returns name, attributes, size, dates);
  compact struct entries; results delivered in batches, the first batch drawn immediately.
- **Custom list control** drawing only visible rows via `DrawingContext` with glyph caching; implements all Far modes
  including multi-column Brief; styled exactly like VS Code lists.
- Icons cached per extension; per-file icons loaded lazily in the background for visible rows.
- Sorting on a background thread.
- `FileSystemWatcher` changes are batched (~100 ms) and applied preserving cursor and selection; on buffer overflow the
  directory is re-read in the background and merged. Network paths add polling every few seconds while the panel is visible.
- Quick search and mask selection are linear passes over in-memory arrays.
- Verified by a BenchmarkDotNet suite generating 100k files in a temp directory and a manual in-app scenario.

## 12. Terminal behaviour

- Mouse: drag selects; double click selects a word, triple click a line; Alt+drag block selection. When the running
  program enables mouse mode, selection requires Shift (Windows Terminal behaviour).
- Right click: copy and clear if there is a selection, otherwise paste.
- Ctrl+Shift+C / Ctrl+Shift+V copy / paste at terminal level. Ctrl+C and Ctrl+Insert copy and clear a selection in the console; with none they go to the shell, as does Ctrl+V.
- Multi-line paste warning ("Paste N lines?"), can be disabled; bracketed paste passed through when enabled by the shell.
- Scrollback 9001 lines; mouse wheel scrolls the buffer; Ctrl+wheel changes the terminal font size.

## 13. DPI and multiple monitors

- Per-Monitor V2 DPI awareness via the application manifest.
- The terminal re-rasterises its font and recomputes columns / rows on DPI change, followed by a ConPTY resize.
  Microsoft.Terminal.Wpf DPI handling is verified in stage 1; if incomplete we forward `WM_DPICHANGED` ourselves.
- Fonts are specified in points; icons are vector only.
- Window placement is stored with monitor and DPI. If that monitor is missing at startup, the window moves to the nearest
  available work area. A maximised window restores maximised on its monitor.
- Test scenario: two monitors at 100% and 150%, moving the window both ways, unplugging a monitor while running,
  changing the scale in Windows settings live.

## 14. Settings and state

- **Settings:** `%APPDATA%\HabilisTerminal\settings.json` — JSONC with a JSON Schema; contains only values changed by
  the user; defaults live in code. Reloaded live via a file watcher. On a syntax / validation error the last valid values
  stay in effect and a notification shows the line number.
- **State:** `%LOCALAPPDATA%\VoronkovLabs\HabilisTerminal\state.json` — panel paths, modes, sorting, column widths, splitter positions,
  panel visibility, window placement, folder history, view / edit history. Written atomically on exit and periodically.
  Command history is PSReadLine's own.
- **Themes:** `%APPDATA%\HabilisTerminal\themes\*.json`.
- **Portable mode:** if `settings.json` or a `portable` marker lies next to `habi.exe`, everything is stored next to the executable.
- **Settings UI** (F9 → Options → Settings): a full-window page modelled on the VS Code Settings editor — search box,
  category tree (Appearance, Panels, Terminal, File operations, Confirmations, Viewer, Editor), settings list with title,
  description and a control by type, "modified" marker, "Reset to default", and an "Open settings.json" button that
  opens the file in the built-in editor. The page is generated from the JSON Schema.
- Notable settings: `theme`, `useAccentColor`, `iconTheme`, `deleteToRecycleBin`, confirmations, `shell.path`,
  `shell.args`, `shell.integration`, `terminal.fontFamily`, `terminal.fontSize`, `terminal.defaultRows`,
  `terminal.multilinePasteWarning`, `panels.font`, `panels.folderSizes`, `viewer.*`, `editor.largeFileThresholdMB`, `editor.syntaxHighlighting`,
  `confirmExit`.

## 15. Packaging and distribution

- Velopack: per-user installer without UAC into `%LOCALAPPDATA%`, background updates from GitHub Releases with a
  "Restart to update" notification. Self-contained .NET 10, x64 and arm64.
- F9 > Options > Check for updates (issue #71): an installed copy checks at once and downloads a found update (then
  "Restart to update"); portable and development builds ask GitHub for the latest release and, when it is newer,
  offer the releases page. The answer is a notice: no updates, update ready, update available, or why it failed.
- Portable zip with the `portable` marker.
- winget manifest pointing to the same installer.
- GitHub Actions on `windows-latest`: build → tests → `dotnet publish` → `vpk pack` → zip → release.
- Code signing deferred (Azure Trusted Signing or SignPath for OSS); v1 ships unsigned.
- pwsh is not bundled.

## 16. Solution structure and testing

```
src/HabilisTerminal.Core      file system model, enumeration, sorting, masks, operations, conflicts,
                                errors, progress, cancellation, Win32 P/Invoke. No UI.
src/HabilisTerminal.Shell     pwsh launch through a ConPTY abstraction, OSC parser, shell command channel,
                                integration script (.ps1). No UI.
src/HabilisTerminal.Themes    VS Code color theme loader (JSONC, include, defaults by type), file icon themes,
                                Windows light/dark and accent colour. No UI.
src/HabilisTerminal.App       WPF: window, layout, terminal, panels, dialogs, viewer, editor, settings. Builds habi.exe.
tests/*.Tests                   xUnit for Core, Shell, Themes.
tests/Core.Benchmarks           BenchmarkDotNet (100k-file directory).
tools/IconBuilder               build-time SVG / glyph → XAML Geometry converter.
```

- Repository: `git init`, `main`, .NET `.gitignore`, `.editorconfig`, `Directory.Build.props` (`net10.0`, nullable,
  warnings as errors), central package management (`Directory.Packages.props`), MIT `LICENSE`.
- **Tests and experiments run only in temporary directories.** Each test gets a sandbox
  `%TEMP%\habi-tests\<guid>` and removes it afterwards. In test builds Core has a guard that throws on any destructive
  operation outside the sandbox. The user's real files are never touched.
- Core tests cover long paths, symlinks and junctions (skipped without symlink privilege), read-only files, locked files,
  access denied (ACL on a temp directory), Recycle Bin on a temp directory of a fixed drive.
- Shell tests start a real pwsh with `-NoProfile` plus our script and assert the OSC stream and that cd preserves typed text.
