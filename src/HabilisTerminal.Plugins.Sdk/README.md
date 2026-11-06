# HabilisTerminal.Plugins.Sdk

Contracts for Habilis Terminal plugins. A plugin is a .NET 10 class library with one public class derived from
`Plugin` and a `plugin.json` manifest next to its assembly:

```json
{ "id": "example.hello", "name": "Hello", "version": "1.0.0", "assembly": "Example.Hello.dll", "sdk": "1.0" }
```

Install by copying the published folder into `%LOCALAPPDATA%\HabilisTerminal\plugins\<id>\` (or the `plugins`
folder next to a portable `habi.exe`).

- Entry points: `Plugin.MenuItems` (F11), `Plugin.DriveMenuItems` (Alt+F1 / Alt+F2), `Plugin.Prefixes` with
  `Plugin.OpenAddressAsync` (command line, e.g. `sqs:prod/eu-west-1`), `Plugin.FileExtensions` with
  `Plugin.OpenFileAsync` (a file opened as a folder, SDK 1.5), `Plugin.ConfigureAsync`.
- Panels: implement `IPanelProvider`; add `IGetFiles`, `IPutFiles`, `IDeleteEntries`, `IMakeFolder`, `IViewEntry`,
  `IPanelCommands` for the operations you support.
- UI: use `IPluginHost.Dialogs`; the host draws every dialog in its own look.
- Settings: `IPluginHost.Settings` stores JSON per plugin. **Never store secrets there**, and never log them.
- Errors: throw `PluginException` with a message for the user; anything else is shown as an internal error.

Develop without restarting the app: `habi-plugin.ps1 load <build output>`, then `reload <id>` after each build.

The guide is `docs/plugins.md` in the Habilis Terminal repository; the API is `docs/plugin-api.md`.
