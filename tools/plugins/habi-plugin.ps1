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
    [Parameter(Mandatory, Position = 0)][ValidateSet('list', 'load', 'reload', 'unload', 'version', 'open', 'state', 'commands')][string]$Command,
    # A plugin folder for load, a plugin id for reload and unload, an address or file path for open.
    [Parameter(Position = 1)][string]$Target,
    # The app's process id. By default HABI_PID, set in the app's own consoles, then the only habi running.
    [int]$ProcessId,
    # Prints the reply as it came, for scripts.
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

# Every failure looks the same, whether the app or this script found it.
function Fail($code, $message) {
    if ($Json) {
        @{ ok = $false; error = @{ code = $code; message = $message } } | ConvertTo-Json -Compress -Depth 5
        exit 0
    }

    [Console]::Error.WriteLine("${code}: $message")
    exit 1
}

function Find-App {
    if ($ProcessId) { return $ProcessId }
    if ($env:HABI_PID) { return [int]$env:HABI_PID }
    # Only windows have the pipe: the helper of performance diagnostics runs as habi too.
    $running = @(Get-Process habi -ErrorAction SilentlyContinue | Where-Object { Test-Path "\\.\pipe\habi-plugins-$($_.Id)" })
    if ($running.Count -eq 1) { return $running[0].Id }
    if ($running.Count -eq 0) { Fail 'no_app' 'Habilis Terminal is not running.' }
    $list = ($running | ForEach-Object { "$($_.Id) $($_.Path)" }) -join "; "
    Fail 'no_app' "Several Habilis Terminal windows run. Choose one with -ProcessId: $list"
}

$request = switch ($Command) {
    'list' { @{ method = 'plugins.list' } }
    'version' { @{ method = 'api.version' } }
    'state' { @{ method = 'panels.state' } }
    'commands' { @{ method = 'commands.list' } }
    'open' {
        if (-not $Target) { Fail 'bad_request' 'open needs an address or a file path.' }
        @{ method = 'panels.open'; params = @{ target = $Target } }
    }
    'load' {
        if (-not $Target) { Fail 'bad_request' 'load needs a plugin folder.' }
        if (-not (Test-Path -LiteralPath $Target -PathType Container)) { Fail 'bad_request' "There is no folder $Target." }
        @{ method = 'plugins.load'; params = @{ folder = (Resolve-Path -LiteralPath $Target).Path } }
    }
    default {
        if (-not $Target) { Fail 'bad_request' "$Command needs a plugin id." }
        @{ method = "plugins.$Command"; params = @{ id = $Target } }
    }
}
$request.id = 1

$app = Find-App
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', "habi-plugins-$app", [System.IO.Pipes.PipeDirection]::InOut)
try {
    try { $pipe.Connect(3000) }
    catch { Fail 'no_app' "Cannot reach Habilis Terminal $app. It must be version 0.1.25 or newer and run as this Windows user." }
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
if ($Command -eq 'list') { $reply.result | Format-Table id, version, state, collectible, buildId, source, error -AutoSize | Out-String -Width 200 }
elseif ($Command -eq 'commands') { $reply.result | Format-Table title, section, keys -AutoSize | Out-String -Width 200 }
elseif ($Command -in 'open', 'state') { "$($reply.result.location) $($reply.result.message)"; $reply.result.entries | Format-Table name, isFolder, size -AutoSize | Out-String -Width 200 }
else { $reply.result | Format-List | Out-String -Width 200 }
