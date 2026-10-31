# Stage 3 — Core: directory model and enumeration: findings

Date: 2026-10-31. Machine: Intel Core i7-11800H, NVMe SSD, NTFS, .NET 10.0.5.

## What was built (`HabilisTerminal.Core.FileSystem`)

- `FileEntry`: compact value type (name, attributes, size, three times) filled from one enumeration pass.
- `DirectoryReader`: `FileSystemEnumerable` reader with batches (256 first, then 4096), cancellation, typed errors
  (`DirectoryReadException` with NotFound / AccessDenied / IoError), `ReadEntry` for single-name refreshes.
- `LongPath`: `\\?\` / `\\?\UNC\` conversion for Win32 calls in stage 6, display form, UNC detection.
- `EntrySorter`: name / extension / modified / size / unsorted / created / accessed, descending, directories first,
  stable (ties → name → enumeration order). Explorer-like name order via `CompareOptions.NumericOrdering`
  (new in .NET 10), precomputed as culture sort keys.
- `FileMask`: Far masks — `*`, `?`, `[a-z]`, `,` / `;` lists, quotes, `|` exclusions, `/regex/flags`,
  `*.*` = everything, `*.` = no extension.
- `Selection`: toggle, select / deselect by mask (files only unless asked), invert, summary, retain after refresh.
- `DirectoryWatcher` + `ListingUpdater`: batched `FileSystemWatcher` events (100 ms window), full refresh on
  overflow / error or bursts over 512 names, otherwise one stat per changed name.
- `Safety.DestructiveOperationGuard`: validator hook for tests; stage 6 operations call `Check(path)`.

## Benchmarks (100 000 files + 200 folders, BenchmarkDotNet 0.15.8)

| Measurement | Target (SPEC §11) | Result |
|---|---|---|
| First batch (first screen) | ≤ 150 ms | **14.9 ms** |
| Read whole directory | — | 58.7 ms |
| Sort by name | — | 77.3 ms |
| Sort by extension | — | 137.8 ms |
| Read + sort by name | ≤ 500 ms | **147.7 ms** |
| Select by mask `*.cs,*.json` | — | 34.7 ms |
| Memory retained by the listing | ≈ 60 MB | **10.4 MB** |

Sort keys cut name sorting from 252 ms (direct culture comparison, ~1.7 M calls) to 77 ms at the price of
~20 MB of short-lived allocations during the sort. A test checks that key order never contradicts direct comparison
(invariant, ru-RU, en-US; Cyrillic, digits, accents, case).

Run: `dotnet run -c Release --project tests/HabilisTerminal.Core.Benchmarks -- --filter *`;
`--memory` measures retained memory; `--clean` removes the generated files from `%TEMP%\habi-bench`.

## Tests

67 Core tests in temp sandboxes: attributes and sizes, batches, not found, access denied (deny-ACL on a temp
directory, restored afterwards), paths beyond MAX_PATH, junctions listed as reparse points and not traversed,
file symlinks (skipped without the symlink privilege — Developer Mode is off on this machine), entry refresh,
cancellation; all sort modes; masks; selection; watcher batching and full-refresh; listing updates; the guard.

## Finding for stage 6: recursive delete and junctions

`Directory.Delete(path, recursive: true)` in .NET 10 fails with "Access to the path 'junction' is denied" when the
tree contains a junction, while deleting the junction alone works. The test sandbox now deletes trees itself
(links removed as links, never entered). Stage 6 permanent delete must use its own tree walk anyway (SPEC §8).
