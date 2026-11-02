# Plugins

Habilis Terminal loads plugins written against a public SDK, the way Far Manager loads its plugins. A plugin can
show its own panel (a list of items with columns, like the S3, SQS and SageMaker plugins), add entries to the plugin
menu and the drive menu, claim a command-line prefix such as `s3:`, and offer a configuration dialog.

Plugins are developed as if by an outside vendor: they reference only the SDK NuGet package, never the app's code,
and live in `plugins/` with their own solution (issue #49).

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
- **Install without admin rights:** copying a plugin folder into the user's plugins folder is enough.

## 2. Pieces

| Piece | Where | What |
|---|---|---|
| SDK | `src/HabilisTerminal.Plugins.Sdk` | Contracts only (`net10.0`, no WPF). Packed as NuGet `HabilisTerminal.Plugins.Sdk`. |
| Host support | `src/HabilisTerminal.App/Plugins`, `src/HabilisTerminal.Core/Plugins` | Discovery, loading, host services, plugin panels. |
| Plugins | `plugins/` | Own solution `plugins/Plugins.slnx`; reference the SDK only through the local NuGet feed. |
| Local feed | `artifacts/nuget/` (ignored by git) | `tools/plugins/Build-Plugins.ps1` packs the SDK here, then builds the plugins. |

## 3. Discovery and loading

- **Folders:** `<app>\plugins\<id>\` (the standard plugins shipped with the app, issue #58; also test copies) and
  `%LOCALAPPDATA%\HabilisTerminal\plugins\<id>\` (plugins the user installs). When both hold the same plugin id, the
  higher manifest version loads; on a tie the shipped one.
- **Manifest:** `plugin.json` in each plugin folder:
  ```json
  { "id": "aws.sqs", "name": "SQS Queues", "version": "1.0.0", "assembly": "Habilis.Aws.Sqs.dll", "sdk": "1.0" }
  ```
- **Isolation:** each plugin runs in its own `AssemblyLoadContext` with an `AssemblyDependencyResolver` on its
  `.deps.json`, so plugins can bring their own dependencies (the AWS SDK) without clashing. The SDK assembly is
  always the host's copy.
- **Compatibility:** the plugin's `sdk` major version must equal the host's; a newer minor version than the host's
  is refused with a message.
- **Entry point:** the assembly has exactly one public non-abstract class derived from `Plugin`.
- **Failures:** a plugin that fails to load or initialise is listed in F9 > Options > Plugins with the reason; the
  others still load.

## 4. SDK

### Plugin
```csharp
public abstract class Plugin
{
    public virtual void Initialize(IPluginHost host);                  // once, after loading
    public virtual IEnumerable<PluginMenuItem> MenuItems { get; }      // F11 plugin menu
    public virtual IEnumerable<PluginMenuItem> DriveMenuItems { get; } // Alt+F1 / Alt+F2
    public virtual IEnumerable<string> Prefixes { get; }               // "s3", "sqs"...
    public virtual Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken token);
    public virtual bool HasConfiguration { get; }
    public virtual Task ConfigureAsync(CancellationToken token);       // F9 > Options > Plugins, Alt+Shift+F9
}

public sealed record PluginMenuItem(string Title, Func<CancellationToken, Task<IPanelProvider?>> Open);
```
`OpenAddressAsync` gets the full text after Enter on the command line, e.g. `s3://bucket/path/` or
`sqs:prod/eu-west-1`. A provider returned from any entry point opens in the active panel.

### Host services
```csharp
public interface IPluginHost
{
    IPluginDialogs Dialogs { get; }
    IPluginSettings Settings { get; }      // JSON per plugin, next to the app's settings; never secrets
    string TempDirectory { get; }          // per plugin, emptied on start
    Task ViewFileAsync(string path, string? title = null);
    Task ViewTextAsync(string title, string text);
    void Refresh(IPanelProvider provider); // re-list open panels, e.g. after the settings changed
    void ShowStatus(string message);
    Version HostVersion { get; }
}
```
Dialogs: `ShowMessageAsync`, `ConfirmAsync(title, message, items, destructive)` (lists the items affected),
`InputAsync`, `ChooseAsync` (a list picker, e.g. profile or region), `EditAsync(PluginForm)` for forms with text,
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
    IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync(CancellationToken token);  // batches
    Task<bool> EnterAsync(PanelEntry entry, CancellationToken token);  // a folder: change location
    Task<bool> GoUpAsync(CancellationToken token);        // false at the top: the host leaves the plugin
    IReadOnlyList<InfoLine> Info { get; }                 // Ctrl+L info panel and the status line
}

public sealed record PanelEntry(string Name, bool IsFolder, long? Size = null, DateTime? Modified = null)
{
    public IReadOnlyDictionary<string, CellValue>? Cells { get; init; }   // custom columns
    public int Group { get; init; }                                      // sorted first: e.g. running jobs
    public RowTone Tone { get; init; }                                   // Normal, Accent, Good, Warning, Bad, Dim
    public object? Tag { get; init; }                                    // plugin data
}
```
- **Columns:** `PanelLayout(Name, Columns)` where a column is the name, size, modified date or a custom column
  (`key`, title, width, alignment). `CellValue` carries the display text and a sort value (number, time or text).
- **Sorting** is done by the host: by `Group` first, then by the chosen key (name, size, date, or a custom column's
  sort value), in either direction (Ctrl+F3... and clicking a header, as for files).
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
| `IPanelCommands` | plugin keys, F9 > Panel, F11 | Commands with a title and an optional key: purge, filter, switch profile, show the full record. |

Every operation gets an `IOperationProgress` (current item, items and bytes done / total) and a cancellation token
from the host. The host shows it in its operation toasts with Cancel.

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
- The command line: Enter on a line starting with a registered prefix and a colon (`s3:`, `sqs:`, `sm:`) opens the
  plugin panel instead of running the line in PowerShell (PSReadLine Enter handler, as for Tab).
- F11 lists the plugins' menu items; F9 > Options > Plugins (Alt+Shift+F9) lists loaded and failed plugins and opens
  their configuration; F9 > Commands > Plugin panel commands lists the commands of the plugin panel with their keys.
- After a plugin command the host re-lists the panel. Ctrl+1... switch the provider's layouts, Ctrl+F12 adds its sort
  keys, and a click on a custom column's header sorts by it.
- The shell stays in the local folder the plugin panel was opened from; a plugin panel's location is never sent to
  the shell as a directory.
- Plugin locations are kept in folder history (Alt+F12) and reopen through the plugin's `OpenAddressAsync`.

## 6. Packaging and testing

- `tools/plugins/Build-Plugins.ps1`: `dotnet pack` the SDK into `artifacts/nuget`, build `plugins/Plugins.slnx`
  against it, and publish each plugin with its `plugin.json` into `artifacts/plugins/<id>/`.
- `tools/ui/New-TestCopy.ps1 -WithPlugins` copies those folders into the test copy's `plugins\`.
- **Standard plugins (issue #58):** the S3, SageMaker, SQS and demo plugins ship with the app. CI runs the script on
  every push (build and plugin tests); a release runs it and copies `artifacts/plugins` into each `publish/<rid>/plugins`
  before `vpk pack` and the portable zip, so the installer, the portable zip and auto-updates carry them. They are still
  developed against the SDK package only.
- Plugin tests run without network access: the AWS clients sit behind small interfaces with fakes.
- Each plugin has a README (install, settings, AWS permissions) and a manual test plan for a real account.

## 7. AWS plugins

Shared by the three plugins, in `plugins/Habilis.Aws.Common`: AWS profiles from the standard shared config and
credentials files (including role assumption and SSO profiles) through the AWS SDK for .NET (Apache 2.0); the
region list; plain-language error mapping (expired credentials, missing permission, throttling, network).
Credentials stay in memory; nothing is logged.

| Plugin | Prefix | Panel |
|---|---|---|
| SQS queues (#48) | `sqs:` | Queues with messages available / in flight; purge with confirmation; prefix and exclusion filters; profile / region switch; info summary. |
| SageMaker training jobs (#47) | `sm:` | Jobs with status, secondary status, warm pool, duration, estimated cost; running first; filters; layouts; summary and full record views; F5 saves descriptions as JSON. Read-only. |
| S3 browser (#46) | `s3:` | Buckets, prefixes as folders, objects as files; download / upload / move up, delete, make folder; multipart parallel transfers with a limit; F3 view. |
