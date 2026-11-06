# Plugin API

Load, reload and unload plugins while Habilis Terminal runs, so you can develop a plugin without restarting the app.
It is always on, and only the current Windows user can reach it.

For writing the plugin itself, see [plugins.md](plugins.md).

## The usual way: `habi-plugin.ps1`

The client ships next to `habi.exe`, and in the repository as `tools/plugins/habi-plugin.ps1`.

```powershell
habi-plugin.ps1 load C:\work\MyPlugin\bin\Release\net10.0   # first time: from its build output
dotnet build C:\work\MyPlugin -c Release                    # after each change...
habi-plugin.ps1 reload my.plugin                            # ...reads the same folder again
habi-plugin.ps1 list                                        # what runs, and why something failed
habi-plugin.ps1 open my:/some/place                         # opens it in the active panel, as the user would
habi-plugin.ps1 state                                       # the active panel's place and rows
habi-plugin.ps1 unload my.plugin
```

The scripts are not on `PATH`: use their full path. In an installed app that is `%LOCALAPPDATA%\HabilisTerminal\current\`.

| Option | What it does |
|---|---|
| `-ProcessId N` | Which app to talk to. By default `HABI_PID`, set in the app's own consoles, then the only `habi` running. With several it lists them and asks. |
| `-Json` | Prints the reply as it came, for scripts. The exit code stays 0; read `ok`. |

Without `-Json` a failure prints `code: message` to stderr and exits with 1. Failures the script finds itself (`no_app`, a folder that does not exist) look the same.

**Checking a reload took effect:** `buildId` in `list` changes with every compiled change. The manifest's version does not.

**Checking a plugin without a window:** `open` then `state -Json` show the rows the user would see. F3, F5 and the dialogs still need a window, or a test that calls the provider directly (see [plugins.md](plugins.md), §8).

## What happens on a load or reload

1. The new version is copied to a temporary folder and loaded from there. **Your build output is never locked**, so `dotnet build` can overwrite it at any time.
2. If it does not load, nothing else happens: **the running copy goes on as it was**, and you get the error.
3. If an operation of the plugin is running, you get `busy`, and the running copy goes on.
4. Its panels close in every console tab, `ShutdownAsync` runs (5 s at most), and the old copy is unloaded.
5. The new copy is initialised, and its panels reopen at the same `Location` when an address or a file can open them. The cursor returns to the top.

Temporary copies are removed once the old version is freed; what stays is cleaned at the next start. `plugins.load` of an id that already runs replaces it, wherever the new folder is. **Folders loaded this way are not remembered:** after a restart the app has its installed plugins again. To keep a plugin, install it (see [plugins.md](plugins.md), §3).

### `oldVersionFreed`

Whether the old code really left memory. `false` does no harm to your work: the new version runs. The old copy just stays in memory until the app restarts, and that usually means something still refers to it (see the rules in [plugins.md](plugins.md)). A plugin the app loaded **at startup** reports `false` on its first reload, even when written correctly; the cause is not known yet. From then on it reports `true`.

## The protocol

A named pipe `habi-plugins-<process id>`, created with `PipeOptions.CurrentUserOnly`. Each request and each reply is one JSON object on one line, UTF-8, ending in `\n`. One connection can carry many requests, answered in order.

```json
{ "id": 1, "method": "plugins.reload", "params": { "id": "my.plugin" } }
```

`id` is optional, any JSON value, and comes back in the reply.

```json
{ "id": 1, "ok": true, "result": { "id": "my.plugin", "state": "loaded", "oldVersionFreed": true, "panelsClosed": 1, "panelsReopened": 1 } }
{ "id": 1, "ok": false, "error": { "code": "busy", "message": "The plugin \"my.plugin\" has an operation under way. Finish or cancel it, then try again." } }
```

Fields with no value are left out.

### Methods

| Method | Params | Result |
|---|---|---|
| `api.version` | | `{ api, sdk, host }`: this protocol's version (1), the SDK the app implements, the app's version |
| `plugins.list` | | An array of `{ id, name, version, sdk, folder, source, state, error, errorDetail, collectible, buildId }` |
| `plugins.load` | `{ folder }`: a full path | An outcome |
| `plugins.reload` | `{ id }` | An outcome |
| `plugins.unload` | `{ id }` | An outcome |
| `panels.open` | `{ target }`: an address, a path, a place inside a file | A panel state. `not_found` when nothing opened it, with the panel's message |
| `panels.state` | | A panel state |

In a list, `state` is `loaded` or `failed`. `folder` is where it runs from, `source` where a reload reads it. `error` is the message and `errorDetail` the whole exception.

A panel state is the active panel's `{ plugin, location, title, message, count, entries }`, with `entries` as `{ name, isFolder, size }`, at most 1000, without `..`. `plugin` and `title` are left out for a local folder.

An outcome is `{ id, state, oldVersionFreed, panelsClosed, panelsReopened, error }`. `state` is `loaded`, `failed` (it loaded but `Initialize` threw: see `error`; the old copy is already gone) or `unloaded`. `oldVersionFreed` is left out when no old copy ran.

### Error codes

| Code | Meaning | What to do |
|---|---|---|
| `bad_request` | Not JSON, or a parameter is missing | Fix the request |
| `unknown_method` | No such method | Check `api.version` |
| `not_found` | No plugin with that id | `plugins.list`, or `plugins.load` first |
| `busy` | An operation of the plugin is running | Finish or cancel it, then retry |
| `load_failed` | The new version did not load: no manifest, a broken assembly, no `Plugin` class, a different id on reload | Read `message`; the running copy is unchanged |
| `incompatible_sdk` | The plugin needs a newer SDK than this app has | Update the app, or lower `"sdk"` |
| `internal` | Something unexpected | Report it |
| `no_app` | From the script: no app, or several, or it cannot be reached | Start it, or use `-ProcessId` |

### Calling it without the script

```powershell
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "habi-plugins-$pid", 'InOut')
$pipe.Connect(3000)
$writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true; $writer.NewLine = "`n"
$writer.WriteLine('{"method":"plugins.list"}')
[IO.StreamReader]::new($pipe).ReadLine()
```

## Safety

Anyone who can open the pipe can run code in the app, so it refuses other Windows users. A program already running as your user could do as much without it.
