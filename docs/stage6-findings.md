# Stage 6 — Core: file operations: findings

Date: 2026-11-01.

## What was built (`HabilisTerminal.Core.Operations`, `HabilisTerminal.Core.Interop`)

- Operation model: existing-file policies, conflict decisions (with "apply to all"), errors with the locking
  process (Retry / Skip / Skip all / Cancel), confirmations (with "Yes to all"), progress in bytes and items,
  `OperationControl` (pause / resume / cancel), `OperationResult` (target paths for cursor placement).
- `FileOperation` base: error loop, confirmation memory, throttled progress (first chunk of each file always
  reported), initial scan that does not enter links, guard check on every changed path.
- `CopyOperation` (F5 / F6): destination rules (existing directory or trailing `\` → into it; single source → new
  name; several → new directory; Far masks), refusal to copy a directory into itself or to reserved names,
  free-space check, `CopyFileEx` with progress, pause and mid-file cancel (partial target removed by the system),
  unbuffered I/O from 256 MB, conflicts (overwrite / skip / rename `name (2).ext` / append / only newer),
  separate confirmation for read-only / hidden / system targets, optional ACL copy, links recreated (junctions via
  `FSCTL_SET_REPARSE_POINT`, symlinks with a privilege check and "copy contents instead" fallback).
  Moves: same volume → `MoveFileEx` (atomic, case-only renames, directory merge when the target exists);
  other volumes → copy then delete each source item only after it was copied.
- `DeleteOperation` (F8 / Shift+Del): Recycle Bin when possible; otherwise (UNC, network, removable, path too long
  for the shell, no bin) the user is asked before a permanent delete. Permanent delete walks the tree itself, deletes
  links as links, confirms non-empty directories and protected files.
- `AttributesOperation` (Ctrl+A): set / clear editable attributes, NTFS compression, EFS, three times, recursive
  without entering links. `DirectoryCreator` (F7): nested paths, `;` lists, validation. `FolderComparer`.
  `NameRules`: reserved device names, trailing dot / space, invalid characters, Far rename masks, next free name.
- `OperationScheduler`: one operation per volume at a time, volumes locked in a fixed order.
- Interop: `CopyFileEx`, `MoveFileEx`, `GetVolumePathName`, free space, junction creation, reparse tag via
  `FindFirstFile`, `FSCTL_SET_COMPRESSION`, Restart Manager, Recycle Bin (`SHQueryRecycleBin`, `SHFileOperation`).

## Tests (44 new, all in `%TEMP%\habi-tests` sandboxes)

The test assembly installs the guard for the whole run (module initializer): any destructive call outside
`%TEMP%\habi-tests` throws. Covered: copy of files and trees with times and attributes, new name, masks, several
sources, copy into itself, reserved names, every conflict choice, only-newer with apply-to-all, read-only target
confirmation, policy without asking, junction recreated and junction contents on request, long paths, mid-file
cancel without partial target, same-volume move (case-only, directories), cross-volume move keeping a locked source
(locking process reported), retry, guard blocking a path outside the sandbox, progress totals; permanent delete with
confirmations, declining a non-empty directory, deleting junctions never touches the target, locked file with
process name, access denied with Skip all, Recycle Bin on a fixed drive (one small file goes to the user's bin),
too-long path asks before permanent delete, attributes and times recursively, NTFS compression, F7 creator,
name rules, rename masks, next free name, folder compare, scheduler queueing.

## Deviations from SPEC

- `CopyFileEx` instead of `CopyFile2`: same features (progress, cancel, ADS, symlink copy, unbuffered), simpler
  interop.
- Recycle Bin through `SHFileOperation` per item instead of `IFileOperation`: our own checks decide whether recycling
  is possible first; `FOF_WANTNUKEWARNING` keeps the shell from silently deleting permanently. Cancel works between
  items.
- Cross-volume moves are exercised through a test-only switch (`ForceCrossVolume`); a real second volume was not
  available.
