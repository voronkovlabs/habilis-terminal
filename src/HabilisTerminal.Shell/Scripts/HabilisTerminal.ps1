# Habilis Terminal shell integration for PowerShell 7+ (SPEC §7).
#
# Dot-sourced after the user's profile. It wraps the prompt that the profile (or oh-my-posh) defined and adds
# invisible OSC sequences around it; the visible prompt is unchanged. Requests from Habilis Terminal arrive
# as the F24 key, with the request itself in "$env:HABI_CHANNEL\request.json".
#
#   OSC 133;A / 133;B        prompt starts / prompt ends, waiting for input
#   OSC 133;C / 133;D;code   command accepted / command finished
#   OSC 9;9;"path"           current file system directory
#   OSC 7731;habi;...        Habilis Terminal messages (ack, restore-pending, focus-panel)

if ($global:__Habi -or -not $env:HABI_CHANNEL) {
    return
}

$global:__Habi = [pscustomobject]@{
    Channel          = $env:HABI_CHANNEL
    OriginalPrompt   = $function:prompt
    OriginalReadLine = $function:PSConsoleHostReadLine
    CommandRan       = $false
    Restore          = $null
    TabFunction      = (Get-PSReadLineKeyHandler -Chord Tab | Select-Object -First 1).Function
    EnterFunction    = (Get-PSReadLineKeyHandler -Chord Enter | Select-Object -First 1).Function
}

function global:__HabiOsc([string] $Payload) {
    "$([char]0x1b)]$Payload$([char]7)"
}

function global:prompt {
    # Must be the first statement: the status of the user's last command.
    $commandSucceeded = $global:?
    $state = $global:__Habi
    $result = [System.Text.StringBuilder]::new()

    if ($state.CommandRan) {
        $exitCode = if ($commandSucceeded) { 0 } elseif ($global:LASTEXITCODE) { $global:LASTEXITCODE } else { 1 }
        [void] $result.Append((__HabiOsc "133;D;$exitCode"))
        $state.CommandRan = $false
    }

    [void] $result.Append((__HabiOsc '133;A'))

    $location = $ExecutionContext.SessionState.Path.CurrentLocation
    if ($location.Provider.Name -eq 'FileSystem') {
        [void] $result.Append((__HabiOsc "9;9;`"$($location.ProviderPath)`""))
    }

    if ($null -ne $state.Restore) {
        [void] $result.Append((__HabiOsc '7731;habi;restore-pending'))
    }

    # Let the original prompt see the same $? as without us (the trick VS Code uses).
    if (-not $commandSucceeded) {
        Write-Error 'failure' -ErrorAction Ignore
    }

    [void] $result.Append((& $state.OriginalPrompt) -join '')
    [void] $result.Append((__HabiOsc '133;B'))
    $result.ToString()
}

if ($global:__Habi.OriginalReadLine) {
    function global:PSConsoleHostReadLine {
        $line = & $global:__Habi.OriginalReadLine
        $global:__Habi.CommandRan = $true
        [Console]::Write((__HabiOsc '133;C'))
        $line
    }
}

Set-PSReadLineKeyHandler -Chord F24 -BriefDescription HabilisTerminalRequest -Description 'Habilis Terminal request' -ScriptBlock {
    $state = $global:__Habi
    $file = Join-Path $state.Channel 'request.json'
    if (-not (Test-Path -LiteralPath $file)) {
        return
    }

    $request = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
    Remove-Item -LiteralPath $file -Force -ErrorAction Ignore
    $status = 'ok'

    try {
        switch ($request.op) {
            'cd' {
                Set-Location -LiteralPath $request.path -ErrorAction Stop
                [Microsoft.PowerShell.PSConsoleReadLine]::InvokePrompt()
            }
            'run' {
                $line = $null
                $cursor = $null
                [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
                if ($line.Length -gt 0) {
                    $state.Restore = @{ Line = $line; Cursor = $cursor }
                }
                [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, $request.command)
                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
            'restore' {
                if ($null -ne $state.Restore) {
                    [Microsoft.PowerShell.PSConsoleReadLine]::Insert($state.Restore.Line)
                    [Microsoft.PowerShell.PSConsoleReadLine]::SetCursorPosition($state.Restore.Cursor)
                    $state.Restore = $null
                }
            }
            'insert' {
                [Microsoft.PowerShell.PSConsoleReadLine]::Insert($request.text)
            }
            default {
                $status = "error;unknown request '$($request.op)'"
            }
        }
    }
    catch {
        # Keep the message on one line and free of OSC terminators.
        $message = ($_.Exception.Message -replace '[\x00-\x1f;]', ' ').Trim()
        $status = "error;$message"
    }

    [Console]::Write((__HabiOsc "7731;habi;ack;$($request.id);$status"))
}

# Plugin addresses (issue #49): Enter on a line starting with a plugin prefix and a colon ("s3:...", "sqs:...")
# opens the plugin panel instead of running the line, as Far's command-line prefixes do. Habilis Terminal keeps the
# prefixes, one per line, in the 'prefixes' file of the channel. Other lines run as before.
Set-PSReadLineKeyHandler -Chord Enter -BriefDescription HabilisTerminalEnter -Description 'Run the line, or open a plugin address' -ScriptBlock {
    param($key, $arg)
    $state = $global:__Habi
    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
    $prefixes = Join-Path $state.Channel 'prefixes'
    if ($line -match '^\s*([A-Za-z][A-Za-z0-9]*):\S' -and (Test-Path -LiteralPath $prefixes)) {
        $prefix = $Matches[1]
        if (@(Get-Content -LiteralPath $prefixes -Encoding utf8) -contains $prefix) {
            [Microsoft.PowerShell.PSConsoleReadLine]::AddToHistory($line)
            [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine()
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line.Trim()))
            [Console]::Write((__HabiOsc "7731;habi;open-address;$encoded"))
            return
        }
    }

    # Whatever Enter did before (AcceptLine by default, ValidateAndAcceptLine in some profiles).
    $function = $state.EnterFunction
    if (-not $function -or -not [Microsoft.PowerShell.PSConsoleReadLine].GetMethod($function)) {
        $function = 'AcceptLine'
    }

    [Microsoft.PowerShell.PSConsoleReadLine]::$function($key, $arg)
}

# One panel shown (Ctrl+P): Tab on an empty command line returns focus to the panel, as Tab switches panels in
# Far. Habilis Terminal creates the 'tab-to-panel' file in the channel while that layout is on. Otherwise Tab
# does whatever it was bound to before (TabCompleteNext by default, MenuComplete in many profiles).
Set-PSReadLineKeyHandler -Chord Tab -BriefDescription HabilisTerminalTab -Description 'Complete, or go to the panel on an empty line' -ScriptBlock {
    param($key, $arg)
    $state = $global:__Habi
    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
    if ($line.Length -eq 0 -and (Test-Path -LiteralPath (Join-Path $state.Channel 'tab-to-panel'))) {
        [Console]::Write((__HabiOsc '7731;habi;focus-panel'))
        return
    }

    $function = $state.TabFunction
    if (-not $function -or -not [Microsoft.PowerShell.PSConsoleReadLine].GetMethod($function)) {
        $function = 'TabCompleteNext'
    }

    [Microsoft.PowerShell.PSConsoleReadLine]::$function($key, $arg)
}
