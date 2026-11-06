param(
    # Folder name under %TEMP%\habi-tests, e.g. v0.1.1-dev13. Must not exist yet.
    [Parameter(Mandatory)][string]$Name,
    [switch]$NoBuild,
    [switch]$NoLaunch,
    # Copy the plugins published by tools/plugins/Build-Plugins.ps1 (artifacts/plugins) into the copy's plugins folder.
    # Everything, with AWS: only for tests that really need them, as they use the owner's profiles.
    [switch]$WithPlugins,
    # The plugin ids a usual copy gets: nothing here reaches a network or the owner's AWS profiles on its own.
    [string[]]$Plugins = @('habi.archives', 'samples.demo', 'gpu.monitor')
)
# Builds Release, copies the output to %TEMP%\habi-tests\<Name> with the `portable` marker (settings and state then live
# next to habi.exe, never in the owner's %APPDATA% / %LOCALAPPDATA%) and starts it. Prints the process id for
# uitool.ps1 -Process pid:<id>. A fresh copy opens in the user's home folder (the working directory does not matter);
# to show test data, type `cd '<dir>'` in its terminal - the active panel follows the shell.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
if (-not $NoBuild) {
    dotnet build "$repo\src\HabilisTerminal.App" -c Release -nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$target = Join-Path $env:TEMP "habi-tests\$Name"
if (Test-Path $target) { throw "$target already exists; pick a new name." }
Copy-Item "$repo\src\HabilisTerminal.App\bin\Release\net10.0-windows10.0.19041.0\win-x64" $target -Recurse
New-Item "$target\portable" -ItemType File | Out-Null
if ($WithPlugins) {
    $plugins = Join-Path $repo 'artifacts\plugins'
    if (-not (Test-Path $plugins)) { throw "No published plugins in $plugins; run tools/plugins/Build-Plugins.ps1 first." }
    Copy-Item $plugins (Join-Path $target 'plugins') -Recurse
}
else {
    New-Item (Join-Path $target 'plugins') -ItemType Directory -Force | Out-Null
    foreach ($id in $Plugins) {
        $plugin = Join-Path $repo "artifacts\plugins\$id"
        if (-not (Test-Path $plugin)) { throw "No plugin $id in artifacts\plugins; run tools/plugins/Build-Plugins.ps1 first." }
        Copy-Item $plugin (Join-Path $target "plugins\$id") -Recurse
    }
}

if ($NoLaunch) { return $target }

# The running Explorer starts it, as a double click does. Started from an agent's session, the copy and every program
# it opens would inherit that session's state: Acrobat then failed with an error window (issue #139).
$shell = New-Object -ComObject Shell.Application
$desktop = $shell.Windows().FindWindowSW([ref]0, [ref]0, 8, [ref]0, 1)
$desktop.Document.Application.ShellExecute("$target\habi.exe", '', $target, '', 1)
Start-Sleep -Seconds 3
$process = Get-Process habi -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$target\habi.exe" } | Select-Object -First 1
if (-not $process) { throw "$target\habi.exe did not start." }
$process.Id
