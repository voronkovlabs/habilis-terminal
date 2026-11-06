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
  - `src/HabilisTerminal.Plugins.Sdk`: the public plugin SDK (contracts only, packed as a NuGet package).
  - Tests are in `tests/`.
  - `plugins/`: plugins built outside the app's solution (see Plugins below).

## Plugins
- **Guide:** `docs/plugins.md`; loading into a running app: `docs/plugin-api.md`. The host side is `src/HabilisTerminal.Core/Plugins` (manifest, loader, settings) and `src/HabilisTerminal.App/Plugins`, `MainWindow.Plugins.cs`, `Panels/FilePanel.Plugin.cs`.
- **Build the plugins:** `tools/plugins/Build-Plugins.ps1`. It packs the SDK into `artifacts/nuget`, builds `plugins/Plugins.slnx` against it (packages restore into `artifacts/packages`), runs the plugin tests and publishes each plugin into `artifacts/plugins/<id>`.
- **Try them:** `tools/ui/New-TestCopy.ps1 -Name ...` copies the archives, demo and GPU plugins from `artifacts/plugins`, so run `Build-Plugins.ps1` first; archives are a plugin now, so a copy needs it to open a zip. `-WithPlugins` copies all of them, with AWS. The demo plugin (`demo:` on the command line, or F11) exercises every plugin panel feature without network access.
- **AWS plugins are tested with fakes only.** Never use the owner's AWS profiles; the owner checks against a real account with each plugin's README test plan.
- **The SDK is a public contract.** A breaking change needs a new SDK major version (`PluginManifest.HostSdkVersion`, the package version, every `plugin.json`).

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
  - The running Explorer starts the copy, as a double click does, so it inherits nothing from the session running the script. A program started from an agent's session can behave differently: Acrobat showed a "Font Capture" error window on every PDF (#139), though clean environment variables did not help. Which part of the session causes it was not found. With `-NoLaunch` you start the copy yourself, for example to pass a test variable, and it inherits your session.
  - A test that opens outside programs leaves their windows on the owner's screen. Close them, also after an interrupted run.
  - Close every copy you started when your checks are done: `tools/ui/Stop-TestCopies.ps1` stops every habi under `%TEMP%\habi-tests\` and prints what it stopped (never any other habi). Leave a copy running only when the owner asks ("запусти"), and tell them its path.
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

## The command list
Every command is in `src/HabilisTerminal.Core/Commands/CommandCatalog.cs`: id, section, title, key, when it applies. The F9 menu, F1 help and the command line (Ctrl+Shift+P, `MainWindow.CommandList.cs`) take them from there. A new key needs a line there as well as its `case`; a catalog command runs as its key does, through `PanelKey`, unless `RunFor` says otherwise. In a viewer or editor it presses the real key.

## Keyboard routing: things learned the hard way
- **Order of processing.** WPF handles a key message in `HwndSource.ThreadFilterMessage` before `GlobalKeyHook` sees it in `ThreadPreprocessMessage`.
- **Arrow keys.** An arrow that no element handles runs WPF directional navigation and is marked handled. `DirectionalNavigation="None"` does not stop it, so arrows are taken in the window's `PreviewKeyDown` (`MainWindow.OnPreviewArrowKey`).
- **Keys typed into the terminal** never reach WPF key events, so the hook handles those. Layout keys typed in the terminal (Ctrl+U, Ctrl+P, Ctrl+F1 / Ctrl+F2) act only while the shell is at the prompt (`ShellState.Idle`).
- **Popups opened from the terminal.** A WPF popup gets its keys through the main window. Win32 focus must leave the terminal first, or Esc and typing go to the shell. A plain `SetFocus` on the window does not work, because WPF hands focus straight back to the child window it remembers. `MainWindow.TakeFocusFromTerminal` focuses a WPF element instead.
- **PSReadLine buffer.** Only PSReadLine knows what is in the buffer. Anything that depends on it, like Tab on an empty line, is decided in `Scripts/HabilisTerminal.ps1`, which reports back with `OSC 7731;habi;<message>`. Host-side switches are flag files in the channel directory (`panels-shown`, `panel-mode`); `panel-mode` is on while a panel has the keys: then Enter, Left, Right, Home, End, Del, Backspace, Esc and Tab on an empty line answer `panel-key;<name>` instead of acting in the line (issues #85, #90).
- **Focus model (issue #90).** The Win32 focus always stays in the console; the active panel "has the keys" as a window state (`FocusedPanel()`, `FilePanel.HasKeys`), not as WPF focus. `GlobalKeyHook` takes the console's keyboard messages in `ComponentDispatcher.ThreadFilterMessage`, before WPF, offers key-downs to `OnGlobalKey` and dispatches the rest to the console itself. Without that, WPF (which has no focused element while a child window has the Win32 focus) runs its keyboard navigation on arrows and focuses the first panel. A panel taking the WPF focus (a click, `panel.Focus()`) means "this panel gets the keys": `OnPanelGotFocus` puts the Win32 focus back into the console. A left click in the console is console mode; Esc on an empty line there comes back. `PutFocusInTerminal` clears WPF's focused element, so activating the window again does not restore a panel.

## Releases
- **Only the last two releases are kept** (the owner's wish): a step in `build.yml` deletes older ones after each release. Their git tags stay.
- **Pushing a tag `vX.Y.Z`** runs `.github/workflows/build.yml`. It publishes self-contained builds per RID, runs `vpk pack --noPortable` (installer and update packages), builds our own portable zip with the `portable` marker, and publishes to GitHub Releases.
- **Never create or move a tag without the owner's explicit go-ahead.** A tag publishes binaries.
- **Code signing:** none yet, so SmartScreen warns about the installer (issue #13).

## Known oddities
- **oh-my-posh prompt after a panel cd.** After a panel-driven `cd`, the prompt is redrawn by `InvokePrompt()` in a different style, and Nerd Font glyphs show as `?`. Plain Windows Terminal does the same with `InvokePrompt()`, so this is an oh-my-posh / PSReadLine interaction (stage 2, issue #15).
- **Prompt position after hiding panels.** When the terminal grows because panels are hidden, the prompt stays where it was and the space below stays empty, as in Windows Terminal. Far instead shows the console filled up to the prompt (stage 1, issue #15).

## Console tabs (#119)
- `Hosting/ConsoleTab.cs` holds what belongs to one shell: `ShellHost`, `ShellConnection`, its own `TerminalControl`, the overflow state, `CommandRunning`. `MainWindow.Tabs.cs` keeps the list and `_tab`, the tab in front; `_shell` and `Terminal` follow it. Events of a tab not in front must not touch panels or the layout: check `tab == _tab`.
- A tab not in front is `Visibility.Hidden` and `WorkspaceLayout` skips it, so it keeps its last size and its shell gets no resize until it is shown. Its control goes on taking output: 20,000 fast lines took 1.5 s hidden and 1.2 s in front, and all arrived.
- The strip sits below the console, which grows upwards over the panels, and shows only with two or more tabs. With both panels shown its height comes out of the panels, so the console keeps its size.
- Each tab owns a pair of `FilePanel`s and their layout (#125); only the pair in front is in `Workspace.Children`, and the window's `_left`, `_right`, `_active` and layout are the front tab's. `SavePanels` and `LoadPanels` move them on a switch. Closing a tab or the window saves its configuration: a new tab and the next start open with the last closed one. A plugin stays live in its tab, but a saved configuration keeps only its local folder.
- The tab's command line comes from the script (`command-line` over OSC 7731), the exit code from OSC 133;D. Without the integration script a tab has no running state, title or finish card.

## Performance diagnostics (#130)
- Setting `diagnostics.performance`, off by default. `Core/Diagnostics/Perf.cs` measures, `Reporting/PerfDiagnostics.cs` runs the UI watchdog and the files, `PerfReport` writes the issue.
- A measuring point is one line: `using var measure = Perf.Measure("name", detail);` for work that stays on its thread (wall and CPU), `Perf.MeasureAsync` for work with awaits (wall only). Off, it costs one check. Details must be impersonal: reports go to public issues, so never a path, file name, command or typed text.
- In a report, "computes" means optimise it, "waits" means make it asynchronous. A stall names the operation under way, or the dispatched method if none; one thread cannot read another's stack here, so add measuring points to narrow it down.
- Files: `diagnostics` in the state folder. A created issue deletes them; a failed send keeps them, up to 20 MB. Automatic sends need the release token; without one the menu item opens the browser. `last-report.md` shows the last text prepared.
