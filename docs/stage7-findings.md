# Stage 7 — File operations UI: findings

Date: 2026-11-01.

## What was built

- `Theming/Controls.xaml`: VS Code-like Button (primary for the default button), TextBox, CheckBox (tri-state),
  RadioButton as segmented chips, ProgressBar — all coloured through `Habi.*` theme resources
  (`button.*`, `input.*` keys added to the palette and to the per-type defaults).
- `Widgets/ThemedDialog`: borderless themed modal dialog centered on the owner; Enter = default, Esc = cancel,
  access keys work without Alt (as in Far dialogs) when focus is not in a text box.
- `Widgets/OperationDialogs`: F5/F6 (destination, existing-file policy, access rights, link contents), F7 (multiple
  names), F8/Shift+Del confirmation, conflict (sizes, dates, newer marker, apply to all), error (with locking
  process), confirmations (protected files, non-empty folders, no Recycle Bin, symlink privilege, free space),
  Ctrl+A attributes (tri-state boxes, compression / encryption exclusive, dates with Current / Original, recursion).
- `Widgets/OperationToasts`: progress cards in a popup above the terminal, bottom-right; bytes and items, current
  file, Pause / Resume, Cancel; "waiting for another operation on the same disk" when queued.
- `MainWindow.Operations`: runs operations through `OperationScheduler` with `UiOperationHandler`
  (dialogs on the UI thread while the worker waits), refreshes panels, cursor rules (last copied item; item after
  the deleted ones; created folder), compare folders (bound to the F9 menu in stage 10).
- Keys: F5, Shift+F5, F6, Shift+F6, F7, F8 / Del (Recycle Bin by default, `HABI_DELETE_TO_RECYCLE_BIN=0` until
  settings), Shift+Del, Ctrl+A.

## Verified (screenshots, temp tree only)

F7 dialog and folder creation, F5 dialog, copy of two files with the cursor on the last copied item, conflict
dialog with Overwrite via the access key, Shift+Del confirmation and permanent delete, progress card during a 3 GB
copy (1.7 GB of 3.0 GB), Ctrl+A dialog.

## Fixed during the stage

- Focus could stay in the terminal window while WPF considered a panel focused: panel focus now follows the real
  Win32 focus, and focusing a panel takes Win32 focus back first.
- A resource key used for both a brush and a style (`Habi.Description`) broke dialogs; styles are now
  `Habi.LabelStyle` / `Habi.DescriptionStyle`.
- Fire-and-forget commands swallowed exceptions; `Fire` traces them (`HABI_TRACE=path`) and shows a message.

## Not verified

Pause / Resume by mouse (the control is covered by Core; the button was not clicked in this run).
