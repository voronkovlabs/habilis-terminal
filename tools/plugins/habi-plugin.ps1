<#
.SYNOPSIS
    Loads, reloads and unloads Habilis Terminal plugins while the app runs. See docs/plugin-api.md.
.EXAMPLE
    habi-plugin.ps1 load .\bin\Release\net10.0
.EXAMPLE
    dotnet build; habi-plugin.ps1 reload my.plugin
.EXAMPLE
    habi-plugin.ps1 list -Json
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('list', 'load', 'reload', 'unload', 'version')][string]$Command,
    # A plugin folder for load, a plugin id for reload and unload.
    [Parameter(Position = 1)][string]$Target,
    # The app's process id. By default HABI_PID, set in the app's own consoles, then the only habi running.
    [int]$ProcessId,
    # Prints the reply as it came, for scripts.
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

function Find-App {
    if ($ProcessId) { return $ProcessId }
    if ($env:HABI_PID) { return [int]$env:HABI_PID }
    $running = @(Get-Process habi -ErrorAction SilentlyContinue)
    if ($running.Count -eq 1) { return $running[0].Id }
    if ($running.Count -eq 0) { throw 'Habilis Terminal is not running.' }
    $list = ($running | ForEach-Object { "$($_.Id) $($_.Path)" }) -join "`n"
    throw "Several Habilis Terminal windows run. Choose one with -ProcessId:`n$list"
}

$request = switch ($Command) {
    'list' { @{ method = 'plugins.list' } }
    'version' { @{ method = 'api.version' } }
    'load' {
        if (-not $Target) { throw 'load needs a plugin folder.' }
        @{ method = 'plugins.load'; params = @{ folder = (Resolve-Path -LiteralPath $Target).Path } }
    }
    default {
        if (-not $Target) { throw "$Command needs a plugin id." }
        @{ method = "plugins.$Command"; params = @{ id = $Target } }
    }
}
$request.id = 1

$app = Find-App
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "habi-plugins-$app", [System.IO.Pipes.PipeDirection]::InOut)
try {
    try { $pipe.Connect(3000) }
    catch { throw "Cannot reach Habilis Terminal $app. It must be version 0.1.25 or newer and run as this Windows user." }
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
    $writer.NewLine = "`n"; $writer.AutoFlush = $true
    $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
    $line = [System.IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true).ReadLine()
}
finally { $pipe.Dispose() }

if ($Json) { $line; exit 0 }
$reply = $line | ConvertFrom-Json
if (-not $reply.ok) {
    [Console]::Error.WriteLine("$($reply.error.code): $($reply.error.message)")
    exit 1
}
if ($Command -eq 'list') { $reply.result | Format-Table id, version, state, collectible, source, error -AutoSize | Out-String -Width 200 }
else { $reply.result | Format-List | Out-String -Width 200 }
