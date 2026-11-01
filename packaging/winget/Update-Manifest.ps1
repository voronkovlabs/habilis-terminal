<#
.SYNOPSIS
    Sets the version, URLs and SHA-256 hashes in the winget manifests from a published GitHub release.
.EXAMPLE
    ./Update-Manifest.ps1 -Version 0.2.0
    Then submit the three files to microsoft/winget-pkgs (manifests/v/VoronkovLabs/HabilisTerminal/0.2.0/).
#>
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$base = "https://github.com/voronkovlabs/habilis-terminal/releases/download/v$Version"
$work = Join-Path ([IO.Path]::GetTempPath()) "habi-winget-$Version"
New-Item -ItemType Directory -Force $work | Out-Null

$hashes = @{}
foreach ($rid in 'win-x64', 'win-arm64') {
    $file = Join-Path $work "HabilisTerminal-$rid-Setup.exe"
    Invoke-WebRequest "$base/HabilisTerminal-$rid-Setup.exe" -OutFile $file
    $hashes[$rid] = (Get-FileHash $file -Algorithm SHA256).Hash
}

foreach ($manifest in Get-ChildItem $PSScriptRoot -Filter '*.yaml') {
    $text = [IO.File]::ReadAllText($manifest.FullName)
    $text = $text -replace '(?m)^PackageVersion: .*$', "PackageVersion: $Version"
    $text = $text -replace 'download/v[^/]+/', "download/v$Version/"
    $text = [regex]::Replace($text, '(win-x64-Setup\.exe\s+InstallerSha256: )\w+', "`${1}$($hashes['win-x64'])")
    $text = [regex]::Replace($text, '(win-arm64-Setup\.exe\s+InstallerSha256: )\w+', "`${1}$($hashes['win-arm64'])")
    [IO.File]::WriteAllText($manifest.FullName, $text)
}

Remove-Item $work -Recurse -Force
