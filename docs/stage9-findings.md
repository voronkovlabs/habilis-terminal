# Stage 9 — Editor (F4): findings

Date: 2026-11-01.

## What was built

- `Core/Editing/TextFile`: loads text with its encoding, BOM and dominant line ending; `Save` writes a temporary file
  next to the target and swaps it in with `File.Replace` (attributes, ACL and creation time kept, nothing left behind
  on failure), checks the destructive-operation guard, supports "save as" and another encoding; `ChangedOnDisk`;
  `Reinterpret` for Shift+F8 before editing. Existing line endings are kept as they are.
- `App/Editing/EditorPage` on AvalonEdit 6.3 (MIT): F2 save, Shift+F2 save as, Esc / F10 close with
  Save / Don't save / Cancel, F7 find, Shift+F7 next, Ctrl+F7 replace all (one undo step), Shift+F8 encoding (reloads
  when unmodified, otherwise changes the encoding used for saving), F6 viewer, Ctrl+F10 back to the file in the panel,
  Ctrl+Z / Y, Alt+mouse and Alt+Shift+arrows block selection (AvalonEdit rectangular selection), line numbers.
- Read-only files: "Clear the attribute and save?"; files changed by another program: "Overwrite it?".
- Theme: `editor.background / foreground / selectionBackground / lineHighlightBackground`, `editorLineNumber.foreground`,
  `editorCursor.foreground`; reapplied on theme changes.
- Syntax highlighting from AvalonEdit's built-in definitions (by extension), recoloured from the theme's `tokenColors`
  with TextMate prefix matching (the longest rule scope that is a prefix of the token scope wins; later rules win
  ties). Bold / italic from the built-in definitions is dropped to match VS Code.
- F4 routing: the first 64 KB decide text vs binary (`TextEncoding.LooksLikeText`); binary goes to the shell verb
  "edit", else "open"; text over 50 MB asks Editor / Viewer (default) / Cancel. F6 in the viewer opens the editor.

## Verified

- 15 Core tests: byte-identical round trips (UTF-8 with / without BOM, UTF-16 LE / BE, LF, mixed endings, no final
  newline, 1251 after choosing the code page), dominant line ending, attributes / creation time kept with no temp
  files, save as, external change detection, read-only refusal. All 206 tests: 205 passed, 1 skipped (symlink
  privilege).
- In the app on a temp tree: C# file opened with theme colours; typing marked the title with ●; Esc asked and "Save"
  wrote the file with its LF endings intact and no temp files; F7 / Shift+F7 found and selected; Del + F2 on a
  read-only file asked, cleared the attribute and saved; F6 to the viewer and back; Esc returned to the panel with the
  cursor on the file and the new size shown.

## Notes

- The first version of the scope matcher compared prefixes the wrong way round, so rules such as
  `keyword.control.anchor.regexp` captured plain keywords; fixed by matching rule-scope-as-prefix and preferring the
  longest.
- Highlighting is limited to AvalonEdit's bundled languages (C#, XML / XAML, HTML, JS, CSS, C++, Java, PHP, Python,
  PowerShell, Markdown, SQL…). A setting to turn it off arrives with settings in stage 10.
- The binary path (system editor) was not exercised interactively to avoid launching external programs; it is a
  plain ShellExecute with verb fallback.
