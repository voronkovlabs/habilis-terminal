# Plugins

Habilis Terminal loads plugins written against a public SDK, the way Far Manager loads its plugins. A plugin can
show its own panel (a list of items with columns, like the S3, SQS and SageMaker plugins), open a file as a folder
(like archives), add entries to the plugin menu and the drive menu, claim a command-line prefix such as `s3:`, and
offer a configuration dialog.

Plugins are developed as if by an outside vendor: they reference only the SDK, never the app's code. They load and
unload while the app runs, so developing one needs no restart.

**Where to read what:**

| You want | Read |
|---|---|
| A working plugin in five minutes | [Quick start](#quick-start) |
| What a plugin must and must not do | [Rules](#rules) |
| A file that looks like a folder, as archives do | [Recipe: a file format](#recipe-a-file-format) |
| Every interface | §4 |
| How loading works; load into a running app | §3, [plugin-api.md](plugin-api.md) |

## Quick start

You need the .NET 10 SDK and a running Habilis Terminal. `New-Plugin.ps1` and `habi-plugin.ps1` ship next to `habi.exe`, and are in `tools/plugins/` in the repository. They are not on `PATH`:

```powershell
$habi = "$env:LOCALAPPDATA\HabilisTerminal\current"    # the folder of habi.exe
& "$habi\New-Plugin.ps1" -Name MyPanel -Id me.mypanel -Path C:\work -Kind Panel -Prefix my
dotnet build C:\work\MyPanel -c Release
& "$habi\habi-plugin.ps1" load C:\work\MyPanel\bin\Release\net10.0
```

The plugin now runs: press F11, or type `my:` on the command line. Change the code, then:

```powershell
dotnet build C:\work\MyPanel -c Release
& "$habi\habi-plugin.ps1" reload me.mypanel
```

If it does not work, `habi-plugin.ps1 list -Json` shows why, with the whole exception. `habi-plugin.ps1 open my: -Json` opens it and returns the rows, so you can check it without a window.

The generated project compiles against `HabilisTerminal.Plugins.Sdk.dll` in the folder of `habi.exe`, with its comments in the `.xml` beside it. If that folder moves, change `HabiDir` in the project. The samples are in the repository, at https://github.com/voronkovlabs/habilis-terminal/tree/main/plugins.

To keep it after a restart, copy `bin\Release\net10.0` to `%LOCALAPPDATA%\HabilisTerminal\plugins\me.mypanel\`.

For a format, use `-Kind FileFormat -Extension myf`, then press Enter on a `.myf` file.

## Rules

**A plugin must:**

| Rule | Why |
|---|---|
| Have exactly one public non-abstract class derived from `Plugin`, with a parameterless constructor | That is how the host finds it |
| Have a `plugin.json` next to its assembly | §3 |
| Not copy the SDK into its folder (`Private=false`, or `PrivateAssets="all" ExcludeAssets="runtime"` on a package) | The host's copy is always used |
| Keep the constructor and `Initialize` fast, with no network | They run on the UI thread while the app starts |
| Give a `Location` that opens the same place again: an address its `OpenAddressAsync` accepts, or a path inside its file | Folder history and a reload reopen panels with it |
| Honour the `CancellationToken` everywhere | Cancel on the waiting card and leaving the panel cancel through it |
| Yield listing batches as they arrive | The first batch shows at once, and the card counts entries |
| Throw `PluginException` with a plain message for what the user should read | Anything else shows as "<plugin> failed: ..." |
| Stop timers in `DisposeAsync`; stop threads and free native libraries in `ShutdownAsync` | What keeps running keeps the old code in memory after a reload |
| Find its own files through `Host.PluginDirectory` | During development it runs from a temporary copy |
| Leave no partial file behind after a failed or cancelled transfer: write beside the target, then move into place | The user would take it for the whole file |
| Treat what is inside a file as untrusted: a name such as `../../x` must not write outside `LocalFolder` | Opening someone's file could overwrite anything the user can |
| Look at a file's size and first bytes before reading it in `OpenFileAsync` | Ctrl+PgDn asks about any file, however large |

**A plugin must not:**

| Rule | Why |
|---|---|
| Reference the app's code (`HabilisTerminal.Core`, `.App`, `.Shell`) | Only the SDK is a contract |
| Show its own windows or dialogs | All UI goes through `Host.Dialogs`, drawn in the app's theme (a panel may draw itself: `IPanelView`) |
| Store secrets in `Host.Settings` or write them to a log | Settings are a plain text file |
| Block the UI thread: `.Result`, `.Wait()`, long work in a property the host reads | The whole window would hang. `Title`, `Location`, `Info`, `FileExtensions`, `Prefixes` and the like must return at once |
| Claim file extensions of documents or programs in `FileExtensions` | They would stop opening in their own program on Enter (#138) |
| Keep its objects in statics of other assemblies or subscribe to process-wide events without leaving them on shutdown | The old code stays in memory after a reload |
| Depend on `AppContext.BaseDirectory` | It is the app's folder, not the plugin's |

## 1. Goals and constraints

From the three plugin briefs (#46 S3, #47 SageMaker training jobs, #48 SQS):

- **Panels for non-file data:** lists of buckets / objects, queues, training jobs, with the host's keys, sorting,
  selection, quick search and view modes.
- **Custom columns and layouts:** message counts, job status, duration, cost; several layouts to switch between.
- **Operations:** copy both ways between a plugin panel and a local panel (download, upload, move), view, delete,
  make folder, and plugin commands (purge, show the full record, filter, switch profile).
- **Progress and cancel** for every long operation; the host never freezes.
- **Plugin UI through the host:** confirmations, messages, input, pickers and settings forms are described by the
  plugin and drawn by the host in its theme. Plugins have no UI framework dependency.
- **Settings** kept by the host per plugin. Never secrets: the SDK documents this and plugins keep credentials in
  memory only.
- **Robustness:** a failing plugin shows a plain-language message; it never crashes or hangs the host.
- **Install without admin rights:** the plugin manager installs a zip or a folder into the user's plugins folder; copying a plugin folder there by hand works too.
- **Develop without restarting:** plugins load, reload and unload while the app runs.

## 2. Pieces

| Piece | Where | What |
|---|---|---|
| SDK | `src/HabilisTerminal.Plugins.Sdk` | Contracts only (`net10.0`, no WPF). Packed as NuGet `HabilisTerminal.Plugins.Sdk`. |
| Host support | `src/HabilisTerminal.App/Plugins`, `src/HabilisTerminal.Core/Plugins` | Discovery, loading, host services, plugin panels. |
| Plugins | `plugins/` | Own solution `plugins/Plugins.slnx`; reference the SDK only through the local NuGet feed. |
| Tools | `tools/plugins/` | `New-Plugin.ps1` makes a project, `habi-plugin.ps1` talks to a running app. Both ship next to `habi.exe`. |
| Local feed | `artifacts/nuget/` (ignored by git) | `tools/plugins/Build-Plugins.ps1` packs the SDK here, then builds the plugins. |

## 3. Discovery and loading

- **Folders:** `<app>\plugins\<id>\` (the standard plugins shipped with the app, issue #58; also test copies) and
  `%LOCALAPPDATA%\HabilisTerminal\plugins\<id>\` (plugins the user installs). When both hold the same plugin id, the
  higher manifest version loads; on a tie the shipped one.
- **Manifest:** `plugin.json` in each plugin folder:
  ```json
  { "id": "aws.sqs", "name": "SQS Queues", "version": "1.0.0", "assembly": "Habilis.Aws.Sqs.dll", "sdk": "1.0" }
  ```
  `id`: letters, digits, `.`, `-`, `_`; use your own prefix. `sdk`: the oldest SDK you need, as `major.minor`. Optional
  `"collectible": false` keeps the plugin's code in memory after an unload, for code that cannot run unloadable.
- **Isolation:** each plugin runs in its own collectible `AssemblyLoadContext` with an `AssemblyDependencyResolver` on
  its `.deps.json`, so plugins can bring their own dependencies (the AWS SDK) without clashing. The SDK assembly is
  always the host's copy.
- **Hot load:** the same plugin can be loaded, reloaded and unloaded while the app runs: [plugin-api.md](plugin-api.md),
  or F5 in the plugin manager. A plugin loaded this way runs from a temporary copy. Native libraries a plugin carries
  go in its own folder; for several architectures use `native\win-x64\`, `native\win-arm64\`, and pick by
  `RuntimeInformation.ProcessArchitecture`, as `Habilis.Archives` does.
- **Compatibility:** the plugin's `sdk` major version must equal the host's; a newer minor version than the host's
  is refused with a message.
- **Entry point:** the assembly has exactly one public non-abstract class derived from `Plugin`.
- **Failures:** a plugin that fails to load or initialise is listed in the plugin manager with the reason; the
  others still load.
- **Plugin manager (#166):** Alt+Shift+F9, or F9 > Options > Plugin manager. All of it works with no restart:
  - **Ins** installs from a `.zip` or a folder with a `plugin.json`, at the top or in one folder inside. It copies the
    plugin into the user's folder and loads it. A plugin that does not load is not kept, and what ran goes on.
  - **Space** turns a plugin off or on. It stays off across restarts and updates (`disabled.json` in the user's folder).
  - **F8** or **Del** removes what the user installed. Plugins that come with the app can only be turned off. Removing a
    newer copy of one brings back the version that comes with the app. Files of a plugin loaded at startup stay locked
    after the unload: the folder gets a `.remove` marker, no longer counts, and goes at the next start.
  - **F5** reloads, **Enter** opens the plugin's settings.
  - A copy no newer than the one that comes with the app is refused, as that one would load in its place.
  - In a portable copy both kinds share one folder: a `.user-installed` marker tells them apart, so a folder copied there
    by hand counts as coming with the app.

## 4. SDK

### Plugin
```csharp
public abstract class Plugin
{
    public virtual void Initialize(IPluginHost host);                  // once, after loading
    public virtual IEnumerable<PluginMenuItem> MenuItems { get; }      // F11 plugin menu
    public virtual IEnumerable<PluginMenuItem> DriveMenuItems { get; } // Alt+F1 / Alt+F2
    public virtual IEnumerable<string> Prefixes { get; }               // "s3", "sqs"...
    public virtual IEnumerable<string> AddressCommands { get; }        // SDK 1.4: "cd", for "cd s3://bucket/"
    public virtual Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken token);
    public virtual IEnumerable<string> FileExtensions { get; }         // SDK 1.5: "zip", "tar.gz": opened as a folder
    public virtual Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken token); // SDK 1.5
    public virtual Task ShutdownAsync();                               // SDK 1.5: being unloaded
    public virtual IEnumerable<PluginCommand> Commands { get; }        // SDK 1.6: anywhere, in the command line
    public virtual bool HasConfiguration { get; }
    public virtual Task ConfigureAsync(CancellationToken token);       // Enter in the plugin manager, Alt+Shift+F9
}

public sealed record PluginMenuItem(string Title, Func<CancellationToken, Task<IPanelProvider?>> Open)
{
    public char? DefaultHotkey { get; init; }
}
public sealed record PluginCommand(string Title, string? Key, Func<CancellationToken, Task> Run);   // SDK 1.6

// SDK 1.5. InnerPath: a place inside the file such as docs\\sub, or empty; from folder history, so it may be gone. Compare it
// the way your format compares names. AnyFormat: Ctrl+PgDn or a location from history, so look at the content.
public sealed record FileOpenRequest(string Path, string InnerPath, bool AnyFormat);
```
`OpenAddressAsync` gets the full text after Enter on the command line, e.g. `s3://bucket/path/` or
`sqs:prod/eu-west-1`. With a command from `AddressCommands` in front it gets the address alone, without quotes.
 A provider returned from any entry point opens in the active panel.

### Host services
```csharp
public interface IPluginHost
{
    IPluginDialogs Dialogs { get; }
    IPluginSettings Settings { get; }      // JSON per plugin, next to the app's settings; never secrets
    string TempDirectory { get; }          // per plugin, emptied on start and on a reload
    string PluginDirectory { get; }        // SDK 1.5: the folder the plugin runs from, with its files
    Task ViewFileAsync(string path, string? title = null);
    Task ViewTextAsync(string title, string text);
    void Refresh(IPanelProvider provider); // re-list open panels, e.g. after the settings changed
    void ShowStatus(string message);
    Version HostVersion { get; }
}
```
Dialogs: `ShowMessageAsync`, `ConfirmAsync(title, message, items, destructive)` (lists the items affected),
`InputAsync`, `InputSecretAsync` (SDK 1.5, a password), `ChooseAsync` (a list picker, e.g. profile or region), `EditAsync(PluginForm)` for forms with text,
number, toggle, choice and multi-line list fields. Forms return the edited values; the host draws them like its own
dialogs.

### Panels
```csharp
public interface IPanelProvider : IAsyncDisposable
{
    string Title { get; }                                 // panel title, e.g. "SQS prod/eu-west-1"
    string Location { get; }                              // shown as the path; goes into folder history
    IReadOnlyList<PanelLayout> Layouts { get; }           // Ctrl+1... switch between them
    IReadOnlyList<PanelSortKey> SortKeys { get; }         // in addition to name; Ctrl+F12 lists them
    string? DefaultSortKey { get; }                       // optional initial custom-column sort
    bool DefaultSortDescending { get; }                   // direction of DefaultSortKey
    bool StartsWithoutCursor { get; }                     // optional cursorless initial view
    bool FreezeOrderWhileCursorVisible { get; }           // stable rows until Escape hides the cursor
    IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync(CancellationToken token);  // batches
    Task<bool> EnterAsync(PanelEntry entry, CancellationToken token);  // a folder: change location
    Task<bool> GoUpAsync(CancellationToken token);        // false at the top: the host leaves the plugin
    IReadOnlyList<InfoLine> Info { get; }                 // Ctrl+L info panel and the status line
}
// When ListAsync, EnterAsync or GoUpAsync takes longer than 0.4 s, the host shows a card with Cancel, which cancels the
// token; a listing also shows the count of entries so far, so yield batches as they arrive. IPluginWindow.Refresh and
// IPanelViewHost.Refresh show no card: they are for live panels.

public sealed record PanelEntry(string Name, bool IsFolder = false, long? Size = null, DateTime? Modified = null)
{
    public IReadOnlyDictionary<string, CellValue>? Cells { get; init; }   // custom columns
    public int Group { get; init; }                                      // sorted first: e.g. running jobs
    public RowTone Tone { get; init; }                                   // Normal, Accent, Good, Warning, Bad, Dim
    public object? Tag { get; init; }                                    // plugin data
    public string? Icon { get; init; }                                   // SDK 1.1: PanelIcons.Bucket, Gpu, Process
}
```
- **Columns:** `PanelLayout(Name, Columns)` where a column is the name, size, modified date or a custom column
  (`key`, title, width, alignment). `CellValue` carries the display text and a sort value (number, time or text).
- **Sorting** is done by the host: by `Group` first, then by the chosen key (name, size, date, or a custom column's
  sort value), in either direction (Ctrl+F3... and clicking a header, as for files). `DefaultSortKey` and
  `DefaultSortDescending` choose the initial custom-column sort; both have harmless defaults.
- **Live lists** may set `StartsWithoutCursor` and `FreezeOrderWhileCursorVisible`: the host then auto-sorts without a
  highlighted row, freezes the displayed row order after a cursor move or click, and resumes sorting from the top on Esc.
- **Row tones** map to theme colours.
- **Listing** streams batches: the first batch shows at once, as for large local folders; cancelling (leaving the
  panel, Esc on the progress) never shows a partial list as complete.

Optional capabilities, by interface:

| Interface | Host key | Use |
|---|---|---|
| `IGetFiles` | F5 / F6 from the plugin panel; F3 | Download to a local folder; F3 asks for a temporary copy and opens the viewer. |
| `IPutFiles` | F5 / F6 into the plugin panel | Upload local files and folders; F6 deletes the originals after success. |
| `IDeleteEntries` | F8 / Del | The host confirms with the item list, then the plugin deletes. |
| `IMakeFolder` | F7 | |
| `IViewEntry` | F3 | A plugin-made view (text or a local file), e.g. a job summary. |
| `IPanelProvider.ShowsParent` | ".." row | SDK 1.2: false hides ".." (flat lists such as queues or jobs); Ctrl+PgUp still leaves the panel. |
| `IPanelProvider.NameColumns` | name columns | SDK 1.3: above 1 the names flow in that many columns and the layout's columns are not drawn (S3 buckets). Asked after each listing. |
| `IPanelView` (`HabilisTerminal.Plugins.Sdk.Wpf`) | the panel | SDK 1.2: the plugin draws the panel itself with WPF, using `HostBrushes` resource keys; the panel keys stay the host's. Target `net10.0-windows` with WPF. |
| `IQuickViewEntry` | Ctrl+Q | SDK 1.1: text for the other panel's quick view (monospace; block characters draw charts). Asked on every cursor move and refresh. |
| `IPanelCommands` | plugin keys, F9 > Panel, F11 | Commands with a title and an optional key: purge, filter, switch profile, show the full record. |

Every operation gets an `IOperationProgress` (current item, items and bytes done / total) and a cancellation token
from the host. The host shows it in its operation toasts with Cancel.

The contracts of the operations, in full. The same with comments is in `HabilisTerminal.Plugins.Sdk.xml`, next to the SDK:

```csharp
public interface IGetFiles
{
    bool CanMove { get; }                  // F6 from the panel is offered
    Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken token);
}
public interface IPutFiles
{
    Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken token);
}
public interface IDeleteEntries { Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken token); }
public interface IMakeFolder { Task MakeFolderAsync(string name, CancellationToken token); }
public interface IViewEntry { Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken token); } // null: view a temporary copy, through IGetFiles
public interface IQuickViewEntry { string? QuickView(PanelEntry entry); }  // asked on every cursor move: from data you have
public interface IPanelCommands { IReadOnlyList<PanelCommand> Commands { get; } }

public sealed class TransferRequest { IReadOnlyList<PanelEntry> Entries; string LocalFolder; bool Move; Func<string, Task<OverwriteChoice>> AskOverwrite; }
public enum OverwriteChoice { Overwrite, Skip, OverwriteAll, SkipAll, Cancel }
public sealed record OperationOutcome(int Succeeded, int Skipped, int Failed, string? Message = null);
public interface IOperationProgress { void SetTotals(long? items, long? bytes); void Begin(string item); void AddBytes(long bytes); void CompleteItem(); }
public sealed record InfoLine(string Label, string Value);
```

### The operations contract

| Question | Answer |
|---|---|
| Who remembers "Overwrite all" and "Skip all"? | **The plugin.** The host may ask again each time. If the user chose a policy up front, the host answers at once |
| The user cancels on the card | The token fires. Throw `OperationCanceledException` (`token.ThrowIfCancellationRequested`): the host shows nothing further, and re-lists the panel |
| The user answers Cancel to `AskOverwrite` | Stop, and return an outcome with what was done so far |
| One item fails, the rest can go on | Count it in `Failed` and go on. The host shows "N failed, M done", or your `Message` if you give one |
| The whole operation cannot go on | Throw `PluginException`: the host shows the message in a dialog |
| Can the user move around the panel during an operation? | **Yes.** Operations run in the background on a thread-pool thread. Take what you need from your current location at the start, or carry it in `PanelEntry.Tag` |
| `PanelEntry.Modified` | Local time is converted; UTC and unspecified are taken as UTC |
| `EnterAsync` returns false for a file | The host views it, as F3 does |
| F3 | `IViewEntry` first. If absent, or it returns null: `GetFilesAsync` into a temporary folder, then the viewer. Neither: "Not supported" |
| Ctrl+R | Calls `ListAsync` again on the same provider. Re-read your source there, if it can change |
| A slow `OpenFileAsync` or `OpenAddressAsync` | After 0.4 s the host shows a card with Cancel, which cancels the token |

### Errors
A plugin throws `PluginException(message)` for errors the user should read ("The credentials have expired: run
`aws sso login --profile prod` and refresh"). Any other exception from a plugin call is caught by the host and shown
as "<plugin> failed: <message>". Plugin calls run off the UI thread or are awaited; the host never blocks on them.

## 5. Host behaviour

- A plugin panel replaces the panel's file listing until the user goes up from its top level (`..` or
  Ctrl+PgUp), opens a local folder (Alt+F1, typing `cd`), or closes it; then the panel returns to the folder it
  showed before.
- Keys keep their meaning: Enter enters a folder entry, F3 views, F5 / F6 copy and move across panels, F7 makes a
  folder, F8 deletes, Ctrl+R re-lists, Ins / Gray + / quick search select and search, Ctrl+L shows the plugin's info
  lines in the other panel.
- A key the provider does not support shows "Not supported by <plugin>" in the status line.
- A file a plugin claims (`FileExtensions`): Enter calls `OpenFileAsync`; null there means Enter does what it does with any
  file. Ctrl+PgDn on any file asks the claiming plugins first, then every other plugin with `FileExtensions`, with
  `AnyFormat` set. A location with a file in it, from folder history or pasted into the drive menu (`C:\a.zip\docs`),
  opens the same way with `InnerPath`. ".." at the top returns to the file's folder with the cursor on the file.
- The command line: Enter on a line starting with a registered prefix and a colon (`s3:`, `sqs:`, `sm:`) opens the
  plugin panel instead of running the line in PowerShell (PSReadLine Enter handler, as for Tab). So does a line with a
  command the plugin registered in front of such an address (`AddressCommands`: `cd s3://bucket/`).
- F11 lists the plugins' menu items; the plugin manager (Alt+Shift+F9) lists all plugins, also failed and turned off, and opens
  their configuration; F9 > Commands > Plugin panel commands lists the commands of the plugin panel with their keys.
- After a plugin command the host re-lists the panel. Ctrl+1... switch the provider's layouts, Ctrl+F12 adds its sort
  keys, and a click on a custom column's header sorts by it.
- The shell stays in the local folder the plugin panel was opened from; a plugin panel's location is never sent to
  the shell as a directory.
- Plugin locations are kept in folder history (Alt+F12) and reopen through the plugin's `OpenAddressAsync`.

## 6. Packaging and testing

- `tools/plugins/Build-Plugins.ps1`: `dotnet pack` the SDK into `artifacts/nuget`, build `plugins/Plugins.slnx`
  against it, and publish each plugin with its `plugin.json` into `artifacts/plugins/<id>/`.
- `tools/ui/New-TestCopy.ps1` copies the archives, demo and GPU plugins into the test copy's `plugins\`;
  `-WithPlugins` copies all of them, with AWS.
- **Standard plugins (issue #58):** the archives, S3, SageMaker, SQS, GPU and sample plugins ship with the app. CI runs the script on
  every push (build and plugin tests); a release runs it and copies `artifacts/plugins` into each `publish/<rid>/plugins`
  before `vpk pack` and the portable zip, so the installer, the portable zip and auto-updates carry them. They are still
  developed against the SDK package only.
- Plugin tests run without network access: the AWS clients sit behind small interfaces with fakes.
- Each plugin has a README (install, settings, AWS permissions) and a manual test plan for a real account.
- **To hand a plugin to someone:** zip what `artifacts/plugins/<id>/` holds. It installs with Ins in the plugin manager.

## 7. Standard plugins

**Archives** (`plugins/Habilis.Archives`, #51, #149): archives as folders on 7-Zip. Claims the usual archive extensions; Ctrl+PgDn tries any file.

### AWS plugins

Shared by the three plugins, in `plugins/Habilis.Aws.Common`: AWS profiles from the standard shared config and
credentials files (including role assumption and SSO profiles) through the AWS SDK for .NET (Apache 2.0); the
region list; plain-language error mapping (expired credentials, missing permission, throttling, network).
Credentials stay in memory; nothing is logged.

| Plugin | Prefix | Panel |
|---|---|---|
| SQS queues (#48) | `sqs:` | Queues with messages available / in flight; purge with confirmation; prefix and exclusion filters; profile / region switch; info summary. |
| SageMaker training jobs (#47) | `sm:` | Jobs with status, secondary status, warm pool, duration, estimated cost; running first; filters; layouts; summary and full record views; F5 saves descriptions as JSON. Read-only. |
| S3 browser (#46) | `s3:` | Buckets, prefixes as folders, objects as files; download / upload / move up, delete, make folder; multipart parallel transfers with a limit; F3 view. |

## 8. Recipes

### Recipe: a file format

A file that looks like a folder, as archives do. **The app needs no change:** a plugin claims the extension and gives a panel. `plugins/Habilis.Samples.FileFormat` is a whole one in one file ([on GitHub](https://github.com/voronkovlabs/habilis-terminal/blob/main/plugins/Habilis.Samples.FileFormat/BundlePlugin.cs)); copy it. What `New-Plugin.ps1 -Kind FileFormat` generates is only a start: it does not look at the content or go anywhere.

```csharp
public sealed class MyFormatPlugin : Plugin
{
    public override IEnumerable<string> FileExtensions => ["myf"];

    public override async Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken token)
    {
        var data = await Read(request.Path, token);       // IOException etc.: throw PluginException
        if (data is null) return null;                    // not this format: others are asked, or Enter opens it normally
        return new MyPanel(request.Path, data, request.InnerPath);
    }
}
```

The panel is an `IPanelProvider`:

| Member | What to do |
|---|---|
| `Location` | The file's path, then the inner folder: `C:\data\a.myf\docs`. The host splits it at the file, so folder history and a reload work |
| Constructor | Go to `InnerPath` if it still exists, else stay at the top |
| `ListAsync` | The current inner folder |
| `EnterAsync` | Change the inner folder; false for a file |
| `GoUpAsync` | Go up; **false at the top**, and the host returns to the file |
| `Layouts` | At least one: `new PanelLayout("Default", [PanelColumn.Size, PanelColumn.Modified])` |

Then add what the format can do:

| Interface | Gives |
|---|---|
| `IGetFiles` | F5 copies out, and F3 views through a temporary copy |
| `IViewEntry` | F3 with your own text, instead of a copy |
| `IPutFiles`, `IDeleteEntries`, `IMakeFolder` | F5 / F6 in, F8, F7. Write a new file next to the old one, then replace it, so a failure leaves it whole. See `Habilis.Archives` |

An encrypted format asks with `await Host.Dialogs.InputSecretAsync(...)` and keeps the answer in memory.

Before reading a file in `OpenFileAsync`, check its size and first bytes, and reject inner names that could leave the target folder. The sample does both.

### Recipe: testing a plugin

Reference the plugin from a test project and call it directly, with no app and no window. `plugins/Habilis.Samples.FileFormat.Tests` is a small example with a do-nothing `IOperationProgress`; `plugins/Habilis.Aws.Tests/FakeHost.cs` is a whole fake `IPluginHost` with scripted dialogs, which you can copy. A plugin's own logic is checked this way, and how it looks in the app with `habi-plugin.ps1 open` and `state`.

### Recipe: a panel with its own data

`New-Plugin.ps1 -Kind Panel` gives a start; `plugins/Habilis.Samples.Demo` shows every feature. Set `Prefixes` to open by address (`my:`), `MenuItems` for F11, `DriveMenuItems` for Alt+F1. To catch `cd my://...` in the console too, add `AddressCommands => ["cd"]`.

### Recipe: columns, tones, commands

```csharp
public IReadOnlyList<PanelLayout> Layouts { get; } =
    [new PanelLayout("Jobs", [new PanelColumn("state", "State", 96), PanelColumn.Modified])];

new PanelEntry(job.Name)
{
    Cells = new Dictionary<string, CellValue> { ["state"] = new(job.State, job.StateOrder) },
    Tone = job.Failed ? RowTone.Bad : RowTone.Normal,
    Group = job.Running ? 0 : 1,    // sorted first
    Tag = job,                      // your own data, back in EnterAsync and the operations
};

// IPanelCommands: shown in F9 > Panel, with an optional key
public IReadOnlyList<PanelCommand> Commands => [new("Purge", "Shift+F8", PurgeAsync)];
```

### Recipe: commands in the command line

The command line (Ctrl+Shift+P, in the title bar) shows the commands that apply now, with their keys. A plugin is in it in three ways:

| What | Shown | Declared by |
|---|---|---|
| Commands of an open panel | While that panel is active | `IPanelCommands`. Set `AppliesTo` to hide one for the current entry |
| Commands available anywhere | While the plugin is loaded | `Plugin.Commands` |
| Opening the plugin | While the plugin is loaded, as `Open: <title>` | `MenuItems`: nothing more to do |

```csharp
public override IEnumerable<PluginCommand> Commands =>
    [new("Refresh the cache", "Ctrl+Alt+J", token => RefreshAsync(token))];

public IReadOnlyList<PanelCommand> Commands =>
    [new("Purge", "Shift+F8", PurgeAsync) { AppliesTo = (current, selected) => selected.Count > 0 }];
```

The host's own commands are hidden for what a provider does not support: Copy needs `IGetFiles`, Delete `IDeleteEntries`, Make folder `IMakeFolder`. Keys: a panel command's key goes before the host's in that panel; a global command's key is ignored and not shown if the host uses it. `AppliesTo` runs when the command line opens, so it must return at once. `habi-plugin.ps1 commands` shows the list with no window.

### Recipe: a panel you draw yourself

Target `net10.0-windows` with `<UseWPF>true</UseWPF>`, reference `HabilisTerminal.Plugins.Sdk.Wpf` as well, and implement `IPanelView` on the provider. Use `HostBrushes` keys so the view follows the theme, and keep elements non-focusable: the keyboard belongs to the panel. See `plugins/Habilis.Gpu`.

## 9. Lifecycle

```
constructor       once, on load. Fast, no network.
Initialize(host)  once, right after. base.Initialize sets Host. Fast, no network.
  OpenAddressAsync / OpenFileAsync / menu item  -> a new IPanelProvider each time a panel opens one
    ListAsync, EnterAsync, GoUpAsync, operations   any number, off the UI thread or awaited
    DisposeAsync                                   when the panel leaves it, and before a shutdown
ShutdownAsync     on unload or reload, after every panel has closed. 5 s at most.
```

- A reload makes a **new** plugin object in a new load context: fields do not survive. Keep what must survive in `Host.Settings`.
- After a shutdown, nothing may run. A leftover `Task.Run` loop or timer keeps the old code in memory and still runs.
- The app does not call `ShutdownAsync` when it exits.

## 10. Checklist before calling a plugin done

Through the API (`habi-plugin.ps1`), with no window:

- [ ] It loads with `load`, and `list` shows `loaded`.
- [ ] `open` shows the rows you expect, and a place inside it opens too.
- [ ] `reload` with a panel open returns to the same place, with `panelsReopened` equal to `panelsClosed`, and `buildId` has changed.
- [ ] `oldVersionFreed` is `true`. `false` means something still holds it: a static, a thread, a timer.
- [ ] A panel plugin: `unload` removes its menu entries, and its prefix then runs in the shell as usual.
- [ ] `commands` lists its commands while its panel is open, with their keys, and none that do not apply.
- [ ] A format plugin: `open` on a file that is not its format gives `not_found`, and after `unload` Enter opens its files in their own program.
- [ ] Tests run without network and only in temporary folders.

In a window:

- [ ] Cancel on the waiting card stops a slow listing.
- [ ] A failing call shows a plain message, not an exception's text meant for developers.
- [ ] Folder history (Alt+F12) reopens a place inside it.
- [ ] A format plugin: ".." at the top returns to the file with the cursor on it, and Ctrl+PgDn on another file does not hang or take it.
- [ ] A README: what it does, how to install it, settings, permissions.

## 11. Common mistakes

| Symptom | Cause |
|---|---|
| `load_failed`: "has no public class derived from Plugin" | The class is `internal`, or abstract |
| `load_failed`: "has 2 plugin classes" | Only one public class may derive from `Plugin`; make the other internal |
| `incompatible_sdk` | `"sdk"` asks for a newer SDK than the app has |
| `MissingMethodException` or `TypeLoadException` in `list` | A copy of the SDK sits in the plugin's folder, or the plugin uses a member newer than its `"sdk"` |
| A dependency's `FileNotFoundException` | The project lacks `<EnableDynamicLoading>true</EnableDynamicLoading>`, so there is no `.deps.json` and no copies |
| `plugin.json` missing from the output | It is not in the project with `CopyToOutputDirectory` |
| Enter on your file opens another program | The name does not end in your `FileExtensions`, or `OpenFileAsync` returned null |
| ".." never leaves the panel | `GoUpAsync` returns true at the top |
| After a reload the panel goes back to the local folder | `Location` is not something `OpenAddressAsync` or `OpenFileAsync` can open |
| `oldVersionFreed: false` every time | A timer, a thread, a static or an event subscription is left. Three cases report `false` regardless: the first reload of a plugin the app loaded at startup, a plugin whose WPF view has been shown, and one whose rows the command line has just shown (§12) |
| The window hangs | A property does slow work, or `.Result` on the UI thread |

## 12. Known limits

- Folders loaded through the API are not remembered across restarts.
- The cursor returns to the top after a reload, and does not move onto a file named in an address.
- A plugin the app loaded at startup reports `oldVersionFreed: false` on its first reload, and its files stay locked until the app restarts. Develop from your build folder with `load`, which never locks it.
- A plugin whose WPF view has been shown (`IPanelView`) reports `oldVersionFreed: false`: WPF keeps hold of the view's types. Reloading works, but each old copy stays in memory until the app restarts. This is seen with the GPU plugin.
- A plugin whose commands were shown in the command line since its last reload reports `oldVersionFreed: false` on the next reload, and that one copy stays in memory until the app restarts. The cause is not known; emptying the list when it closes did not help. The next reload reports `true` if the box was not opened in between.
- Whether the AWS plugins free their memory on unload has not been checked: tests never open them with real credentials.
