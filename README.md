# Habilis Terminal

A two-panel file manager for Windows with a real PowerShell 7 terminal underneath — Far Manager keys, VS Code looks.

- **Terminal:** pwsh 7 through ConPTY in the Windows Terminal control, kept in sync with the panels (`cd` moves the
  active panel, panel navigation moves the shell).
- **Panels:** fast custom-drawn lists (100 000 files: first screen in ~25 ms), Far view modes and sorting, quick search,
  masks, info panel, quick view, drive menu, folder history.
- **Files:** copy / move / delete / attributes with background progress, conflicts, Recycle Bin, long paths, links.
- **Viewer and editor:** F3 opens files of any size instantly (memory-mapped); F4 edits text with AvalonEdit and saves
  byte-for-byte (encoding, BOM, line endings).
- **Looks:** VS Code Dark / Light Modern following the Windows mode and accent colour, Seti or Material file icons,
  any VS Code colour theme from the `themes` folder.
- **Settings:** `settings.json` with a schema and live reload, plus a VS Code-style Settings page (F9 → Options).

## Install

- Installer (per user, no admin rights, updates itself from GitHub Releases): `HabilisTerminal-win-x64-Setup.exe` or
  `HabilisTerminal-win-arm64-Setup.exe` from [Releases](https://github.com/voronkovlabs/habilis-terminal/releases).
- Portable: unzip `HabilisTerminal-<version>-<arch>-portable.zip` anywhere; settings and state stay next to `habi.exe`.

PowerShell 7 is required:

```
winget install Microsoft.PowerShell
```

## Keys

F1 in the app shows the full map. The essentials: Ctrl+Tab panels ↔ command line, Tab other panel, Ctrl+O hide panels,
F3 view, F4 edit, F5 copy, F6 move, F7 folder, F8 delete, F9 menu, F10 exit, Alt+F1 / Alt+F2 drives, Alt+F12 folder
history, Ctrl+Shift+C / V terminal clipboard.

## Build

Requires the .NET 10 SDK and PowerShell 7.

```
dotnet build
dotnet test
dotnet run --project src/HabilisTerminal.App
```

Tests touch only `%TEMP%\habi-tests`. A release is built by pushing a `vX.Y.Z` tag (see `.github/workflows/build.yml`);
`packaging/winget` holds the winget manifests and a script that fills in a release's hashes.

## License

MIT — see [LICENSE](LICENSE). Bundled third-party material is listed in [ThirdPartyNotices.txt](ThirdPartyNotices.txt).
