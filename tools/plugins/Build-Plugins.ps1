param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)
# Builds the plugins in plugins/ the way an outside developer would: packs the plugin SDK into the local feed
# (artifacts/nuget), restores the plugins against it, runs their tests and publishes each plugin with its plugin.json
# into artifacts/plugins/<id>/, ready to copy into a Habilis Terminal plugins folder (docs/plugins.md §6).
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$feed = Join-Path $repo 'artifacts\nuget'
$packages = Join-Path $repo 'artifacts\packages'
$output = Join-Path $repo 'artifacts\plugins'

foreach ($sdk in 'HabilisTerminal.Plugins.Sdk', 'HabilisTerminal.Plugins.Sdk.Wpf') {
    dotnet pack "$repo\src\$sdk" -c $Configuration -o $feed -nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Packing $sdk failed." }

    # The SDK version stays the same while it is developed here: drop the restored copy so the fresh package is used.
    Remove-Item -Recurse -Force (Join-Path $packages $sdk.ToLowerInvariant()) -ErrorAction Ignore
}

$solution = "$repo\plugins\Plugins.slnx"
dotnet build $solution -c $Configuration -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Building the plugins failed.' }

if (-not $SkipTests) {
    dotnet test --solution $solution -c $Configuration --no-build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Plugin tests failed.' }
}

Remove-Item -Recurse -Force $output -ErrorAction Ignore
foreach ($manifest in Get-ChildItem "$repo\plugins\*\plugin.json") {
    $project = $manifest.Directory
    $id = (Get-Content $manifest -Raw | ConvertFrom-Json).id
    dotnet publish $project.FullName -c $Configuration -o (Join-Path $output $id) -nologo -v q --no-build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing $id failed." }
    # The README (install, settings, permissions, test plan) travels with the plugin.
    $readme = Join-Path $project.FullName 'README.md'
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $output $id) }
    Write-Host "plugin $id -> $(Join-Path $output $id)"
}
