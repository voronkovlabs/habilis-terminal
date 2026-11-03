# Habilis Terminal plugins

Plugins built against the Habilis Terminal plugin SDK, as an outside developer would build them: this folder has its
own solution, build settings and package versions, and takes the SDK only as the `HabilisTerminal.Plugins.Sdk` NuGet
package from a local feed. Nothing here references the app's code. Design: `docs/plugins.md`.

| Plugin | Folder | Id | Prefix | Issue |
|---|---|---|---|---|
| SQS queues | `Habilis.Aws.Sqs` | `aws.sqs` | `sqs:` | #48 |
| SageMaker training jobs | `Habilis.Aws.SageMaker` | `aws.sagemaker` | `sm:` | #47 |
| S3 browser | `Habilis.Aws.S3` | `aws.s3` | `s3:` | #46 |
| Demo (in-memory sample, no network) | `Habilis.Samples.Demo` | `samples.demo` | `demo:` | #49 |
| GPU monitor (local NVIDIA GPUs, SageMaker jobs) | `Habilis.Gpu` | `gpu.monitor` | `gpu:`, `gpu:sm` | #70 |

`Habilis.Aws.Common` holds what the AWS plugins share: profiles and credentials, regions, plain-language errors.

## Build

```
pwsh tools/plugins/Build-Plugins.ps1
```

It packs the SDK into `artifacts/nuget`, restores the plugins into `artifacts/packages`, builds and tests them, and
publishes each into `artifacts/plugins/<id>/` with its `plugin.json`. These four plugins are standard: a release
copies them into the `plugins` folder next to `habi.exe` (issue #58). Install another plugin by copying its folder into
`%LOCALAPPDATA%\HabilisTerminal\plugins\` (or the `plugins` folder next to a portable `habi.exe`).
`tools/ui/New-TestCopy.ps1 -WithPlugins` makes a test copy with all of them.

## Writing a plugin

1. A class library targeting `net10.0`, referencing `HabilisTerminal.Plugins.Sdk` with
   `PrivateAssets="all" ExcludeAssets="runtime"` (the host provides the SDK at run time).
2. One public class derived from `Plugin`; a provider class implementing `IPanelProvider` and the optional operation
   interfaces it supports.
3. A `plugin.json` next to the assembly: id, name, version, assembly file name, SDK version.
4. All UI through `IPluginHost.Dialogs`; settings through `IPluginHost.Settings` (never secrets); user-facing errors
   as `PluginException`.

`Habilis.Samples.Demo` shows every feature in one file.
