# Stage 12 — Packaging and release: findings

Date: 2026-11-01.

## What was built

- `Program.Main` runs `VelopackApp.Build().Run()` before WPF starts (App.xaml is a page, not the application
  definition); `App.OnStartup` loads settings and state, checks for PowerShell and then opens the window.
- `Updates/UpdateService` (Velopack 1.2, `GithubSource` on this repository): only for installed copies — portable and
  development builds never check. 15 s after start it checks, downloads quietly and shows "Update ready" with
  "Restart to update" (asks about unsaved editor changes first); otherwise the update is applied when the app exits.
  Each architecture updates within its own channel (`win-x64`, `win-arm64`).
- pwsh-missing screen: a themed dialog before the main window, with the winget command (copyable), the download page
  and "Check again"; `shell.path` is honoured.
- About (F9 → Options): version, install mode (installed / portable / development), .NET and architecture, shell and
  settings paths, licences, "Third-party notices" (opens in the viewer) and GitHub.
- App icon (`Assets/habi.ico`, generated: two panels over a prompt), used for the exe, the window and the installer.
- `Version` 0.1.0 in Directory.Build.props; release builds pass `-p:Version` from the tag.
- `vpk` as a local dotnet tool (`dotnet-tools.json`).
- `.github/workflows/build.yml`: build and test on every push and pull request; a `vX.Y.Z` tag publishes
  self-contained x64 and arm64 builds, downloads the previous release for delta packages, runs `vpk pack`, replaces
  Velopack's portable zip with ours (app + `portable` marker) and uploads everything to a GitHub release.
- `packaging/winget`: version / installer / locale manifests (user scope, `--silent`, depends on
  Microsoft.PowerShell) and `Update-Manifest.ps1`, which fills in version, URLs and SHA-256 from a published release.
- README; ThirdPartyNotices now also lists the Windows Terminal control / ConPTY and Velopack; LICENSE is copied to
  the output.

## Verified

- `dotnet build -c Release` with no warnings; all 221 tests pass in Release (1 skipped: symlink privilege).
- Self-contained `dotnet publish` (win-x64, 260 MB unpacked): habi.exe, conpty.dll, `x64` / `arm64` OpenConsole.exe and
  the shell script are in place.
- `vpk pack` in `%TEMP%\habi-tests\pack` produced Setup.exe (85 MB), the full nupkg, RELEASES and the releases JSON.
- Our portable zip of the Release build, unpacked in a temp folder: starts through `Program.Main`, About shows
  "0.1.0 (portable)", settings.schema.json and state.json are written next to the exe, nothing in `%APPDATA%` or
  `%LOCALAPPDATA%`; Third-party notices open in the viewer; F10 exits cleanly.

## Not verified here

- Running the installer, updating from N to N+1 and uninstalling: that installs into this user's profile (Start menu,
  `%LOCALAPPDATA%`), which these tests must not touch. It needs a clean profile or VM and a first tagged release.
- The release job itself runs only on a tag; no tag was pushed. Creating the first release (`git tag v0.1.0` and
  push) publishes binaries, so it is left to the owner.
- The pwsh-missing screen: this machine has PowerShell 7 in Program Files, and hiding it would mean changing the
  system.
- Code signing: none yet (SmartScreen will warn on the installer).
