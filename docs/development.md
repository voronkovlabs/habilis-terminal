# Development notes

How the project is built, tested and checked by hand. Most of this is not visible from the code.

## Where things are
- **`SPEC.md`**: the product spec, and the source of truth for behaviour. Far Manager parity is the default answer when the spec is silent.
- **`PLAN.md`**: the 12 v1 stages. All are done; v0.1.0 was released on 2026-11-01.
- **`docs/stageN-findings.md`, `docs/v0.1.1-findings.md`**: per-stage and per-release logs with causes, fixes and what was checked.
- **GitHub issues labelled `claude`**: what is open and what is done. See `CLAUDE.md`.
- **Naming.** The product was renamed on 2026-10-31 from "Terminal Commander" / `tcmd.exe` to **Habilis Terminal** / `habi.exe`. The local folder is still `terminal-commander`.
- **Projects.**
  - `src/HabilisTerminal.App`: WPF.
  - `src/HabilisTerminal.Core`: panels, file system, settings, viewer, editor.
  - `src/HabilisTerminal.Shell`: ConPTY and pwsh integration.
  - `src/HabilisTerminal.Themes`.
  - Tests are in `tests/`.

## Build and test
```
dotnet build -c Release
dotnet test -c Release --no-build
```
- **Expected result:** every test passes and one is skipped (creating a symlink needs a privilege this machine lacks).
- **Shell tests** start pwsh with `-NoProfile` and PSReadLine `SaveNothing`, so they never touch the owner's profile or history.
- **Temporary directories only.** Tests and experiments run in `%TEMP%\habi-tests\...` (UI and test data) or `%TEMP%\habi-bench`. Never touch the owner's real files. Reading metadata of system folders (for example to time a folder count) is fine; writing anything outside these folders is not.
- **Clipboard.** A test that uses the clipboard saves it first and restores it afterwards.

## UI checks
- **Make a test copy:** `tools/ui/New-TestCopy.ps1 -Name v0.1.1-devN`.
  - It builds Release, copies the output to `%TEMP%\habi-tests\<Name>` with a `portable` marker, starts it and prints the pid.
  - Thanks to the marker, settings and state stay next to the exe and never reach the owner's `%APPDATA%` or `%LOCALAPPDATA%`.
  - Use a new name each time, so every copy starts with a clean state.
  - Close every copy you started when your checks are done: `tools/ui/Stop-TestCopies.ps1` stops every habi under `%TEMP%habi-tests` and prints what it stopped (never any other habi). Leave a copy running only when the owner asks ("запусти"), and tell them its path.
- **Drive the window** with `tools/ui/uitool.ps1`. Always target the copy with `-Process pid:N`: the owner may be running their own habi at the same time.
  - **Keys.** `keys` only sends while the target is in the foreground. Once the owner's typing landed in a test window, so test in your own copy and never in theirs.
  - **Screenshots.**
    - `shot` uses `PrintWindow`, which does not capture popups (the F9 menu, tooltips). Use `screen` for those.
    - Screenshots have DWM's 1 px border on the edges.
  - **Fast frames.** Each script run compiles its helper type and takes about a second. For frames milliseconds apart (progress, cancellation), dot-source the script once and call `[UiTool]::Chord` / `[UiTool]::Shot` directly. Example: the F3 count of 75,000 files finishes in under 250 ms with a warm cache.
  - **Pixel checks.** For 1 px questions, load the PNG with `System.Drawing.Bitmap` and dump `GetPixel` rows. This is how the title bar's 1 px overlap was found.
- **Show test data.** A fresh copy opens in the home folder. Type `cd '<dir>'` in its terminal, and the active panel follows the shell. Click a panel (for example at 150,70) to focus it.
- **Trace.** Set `HABI_TRACE=<file>` before starting habi.exe to log key routing and focus (`MainWindow.Trace`) and swallowed exceptions from fire-and-forget commands.
- **Crash reports (issue #22).**
  - `HABI_TEST_CRASH=ui|thread|task` crashes a copy 3 s after its window opens (`CrashCapture.StartTestTrigger`). The report lands in `<state>\crashes\crash-*.json` (next to habi.exe in a test copy) and the next start offers it.
  - Builds without a token open the prefilled new-issue page in the browser instead of sending. That is the only way to test sending here: **never create real issues through the API without the owner's go-ahead.**
  - `HABI_REPORT_TOKEN=<token>` gives a development build a token for one deliberate real send. Release builds get `CRASH_REPORT_TOKEN` from the release workflow (base64 of the reversed token in `AssemblyMetadata("CrashReportToken")`).
  - **Real API check:** the manual `report-smoke` workflow (`gh workflow run report-smoke.yml`) runs `tools/ReportSmoke` in CI with the secret. It creates a test crash issue, repeats the crash (which must become a comment) and creates a test feedback issue, then closes them. Run it only with the owner's go-ahead.
  - While a dialog is open, `uitool.ps1 keys -Process pid:N` sends keys to the main window if that is in the foreground. Click the dialog first (`click`), then keys reach it.
- **Not available on this machine:**
  - Only one 1920×1080 monitor at 100% scale.
  - Changing the Windows scale or other system settings is not allowed.
  - Installing into the owner's profile is not allowed.
  - Multi-monitor, DPI and installer checks therefore need the owner (issues #2, #3).

## Keyboard routing: things learned the hard way
- **Order of processing.** WPF handles a key message in `HwndSource.ThreadFilterMessage` before `GlobalKeyHook` sees it in `ThreadPreprocessMessage`.
- **Arrow keys.** An arrow that no element handles runs WPF directional navigation and is marked handled. `DirectionalNavigation="None"` does not stop it, so arrows are taken in the window's `PreviewKeyDown` (`MainWindow.OnPreviewArrowKey`).
- **Keys typed into the terminal** never reach WPF key events, so the hook handles those. Layout keys typed in the terminal (Ctrl+U, Ctrl+P, Ctrl+F1 / Ctrl+F2) act only while the shell is at the prompt (`ShellState.Idle`).
- **Popups opened from the terminal.** A WPF popup gets its keys through the main window. Win32 focus must leave the terminal first, or Esc and typing go to the shell. A plain `SetFocus` on the window does not work, because WPF hands focus straight back to the child window it remembers. `MainWindow.TakeFocusFromTerminal` focuses a WPF element instead.
- **PSReadLine buffer.** Only PSReadLine knows what is in the buffer. Anything that depends on it, like Tab on an empty line, is decided in `Scripts/HabilisTerminal.ps1`, which reports back with `OSC 7731;habi;<message>`. Host-side switches are flag files in the channel directory (for example `tab-to-panel`).

## Releases
- **Pushing a tag `vX.Y.Z`** runs `.github/workflows/build.yml`. It publishes self-contained builds per RID, runs `vpk pack --noPortable` (installer and update packages), builds our own portable zip with the `portable` marker, and publishes to GitHub Releases.
- **Never create or move a tag without the owner's explicit go-ahead.** A tag publishes binaries.
- **Code signing:** none yet, so SmartScreen warns about the installer (issue #13).

## Known oddities
- **oh-my-posh prompt after a panel cd.** After a panel-driven `cd`, the prompt is redrawn by `InvokePrompt()` in a different style, and Nerd Font glyphs show as `?`. Plain Windows Terminal does the same with `InvokePrompt()`, so this is an oh-my-posh / PSReadLine interaction (stage 2, issue #15).
- **Prompt position after hiding panels.** When the terminal grows because panels are hidden, the prompt stays where it was and the space below stays empty, as in Windows Terminal. Far instead shows the console filled up to the prompt (stage 1, issue #15).
