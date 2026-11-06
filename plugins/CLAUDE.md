# Writing Habilis Terminal plugins

Read `docs/plugins.md` before writing a plugin: it has a quick start, the rules, every contract and recipes. `docs/plugin-api.md` covers loading into a running app.

## The loop
1. `tools/plugins/New-Plugin.ps1 -Name X -Id me.x -Path <folder> -Kind Panel -Prefix x` or `-Kind FileFormat -Extension x` makes a project that builds and loads as it is.
2. `dotnet build <project> -c Release`
3. `tools/plugins/habi-plugin.ps1 load <project>\bin\Release\net10.0`, then `reload <id>` after each build. No restart.
4. `habi-plugin.ps1 list -Json` shows why something failed, with the whole exception, and a `buildId` that changes with every compiled change.
5. `habi-plugin.ps1 open <address or file> -Json` opens it in the active panel and returns its rows, and `state` reads them. So you can check a plugin without sending keys.

A plugin in this folder is built by `tools/plugins/Build-Plugins.ps1` instead, against the SDK package.

## The rules that matter most
- Reference only the SDK, never the app's code.
- Exactly one public class derived from `Plugin`, with a parameterless constructor. No network or slow work in it or in `Initialize`.
- All UI through `Host.Dialogs`. Errors for the user as `PluginException`. Secrets never in `Host.Settings`, never in logs.
- `Location` must open the same place again: an address your `OpenAddressAsync` accepts, or a path inside your file.
- Stop timers in `DisposeAsync`, and threads and native libraries in `ShutdownAsync`. Leave no static holding your objects, or the old code stays in memory after a reload.
- Find your files through `Host.PluginDirectory`, never `AppContext.BaseDirectory`.
- `FileExtensions`: only files that are containers to the user. Return null from `OpenFileAsync` when the content is not yours; any file of any size can arrive there.
- Inner paths come from the file and are not to be trusted: what F5 writes must stay inside `LocalFolder`.
- Read `docs/plugins.md` "The operations contract" before writing `IGetFiles` or `IPutFiles`.

## Examples
- `Habilis.Samples.FileFormat`: a whole file format in one file. Start here for "looks like going inside an archive".
- `Habilis.Samples.Demo`: every panel feature.
- `Habilis.Archives`: a real format with native code, writing and passwords.
- `Habilis.Gpu`: a panel drawn with WPF.

## Testing here
Tests run without network, in temporary folders. Never use the owner's AWS profiles: AWS plugins are tested with fakes. A test copy (`tools/ui/New-TestCopy.ps1`) gets no AWS plugins by default; keep it that way.
