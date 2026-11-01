param(
    # Folder name under %TEMP%\habi-tests, e.g. v0.1.1-dev13. Must not exist yet.
    [Parameter(Mandatory)][string]$Name,
    [switch]$NoBuild,
    [switch]$NoLaunch
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
Copy-Item "$repo\src\HabilisTerminal.App\bin\Release\net10.0-windows\win-x64" $target -Recurse
New-Item "$target\portable" -ItemType File | Out-Null
if ($NoLaunch) { return $target }

$process = Start-Process "$target\habi.exe" -WorkingDirectory $target -PassThru
Start-Sleep -Seconds 3
$process.Id
