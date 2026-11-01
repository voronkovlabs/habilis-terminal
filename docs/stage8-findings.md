# Stage 8 — Viewer (F3) and quick view (Ctrl+Q): findings

Date: 2026-11-01.

## What was built

- `Core/Viewing/ViewerDocument`: memory-mapped, read-only (others may keep writing), positions are byte offsets
  with no line index — next / previous line by scanning, lines longer than 16 KB split for display, line numbers and
  go-to-line on demand, search forward / backward (ordinal, optional case) on 1 MB decoded chunks with overlap.
- `Core/Viewing/TextEncoding`: BOM, UTF-16 without BOM (zero-byte columns plus printable low bytes), UTF-8 validity,
  else the system ANSI code page (`GetACP`); OEM from `GetOEMCP`; choices UTF-8, ANSI, OEM, UTF-16 LE/BE, 1251, 866,
  1252, KOI8-R; text-or-binary check for F4.
- `Core/Viewing/DirectorySize`: F3 on folders, links not entered, inaccessible folders counted.
- `App/Viewing/ViewerView` (custom drawn text / wrap / hex, highlight, horizontal scroll) and `ViewerPage`
  (Far keys: F2 wrap, F4 hex, F7 / Shift+F7 / Alt+F7 search, F8 ANSI↔OEM, Shift+F8 encoding menu,
  Alt+F8 go to line / 0x offset / percent, Ctrl+Home / End, F6 editor, Esc / F3 / F10). Images via WPF decoders.
- Pages are shown over the window with the workspace hidden (not collapsed), so the terminal keeps its size.
- `Panels/QuickView` (Ctrl+Q) follows the other panel's cursor: text, images, folder summary.
- Folder sizes from F3 appear in the size column until the directory changes.

## Verified

- 12 Core tests (encodings, CRLF / LF / CR, UTF-16 with BOM, 1251 / 866, search across a chunk border, byte offsets
  after multi-byte characters, go to line, empty file, long lines, 120 MB log reaching the end in < 500 ms,
  folder size without entering a junction).
- In the app: a generated 5 GB log opened instantly; Ctrl+End instant; search for a marker at the very end found and
  highlighted in a few seconds; hex mode; 1251 and 866 files correct through Shift+F8 (this machine's system code
  pages are 1252 / 437, so plain F8 toggles those, as in Far); image quick view; F3 on a folder showed its size.

## Notes

- The first search implementation (per-character offset map, culture comparison) was far too slow for 5 GB; the
  chunk-decode + ordinal search replaced it.
