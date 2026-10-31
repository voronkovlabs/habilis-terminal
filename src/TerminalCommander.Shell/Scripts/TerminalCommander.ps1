# Terminal Commander shell integration for PowerShell 7+ (SPEC §7).
#
# Dot-sourced after the user's profile. It wraps the prompt that the profile (or oh-my-posh) defined and adds
# invisible OSC sequences around it; the visible prompt is unchanged. Requests from Terminal Commander arrive
# as the F24 key, with the request itself in "$env:TCMD_CHANNEL\request.json".
#
#   OSC 133;A / 133;B        prompt starts / prompt ends, waiting for input
#   OSC 133;C / 133;D;code   command accepted / command finished
#   OSC 9;9;"path"           current file system directory
#   OSC 7731;tcmd;...        Terminal Commander messages (ack, restore-pending)

if ($global:__Tcmd -or -not $env:TCMD_CHANNEL) {
    return
}

$global:__Tcmd = [pscustomobject]@{
    Channel          = $env:TCMD_CHANNEL
    OriginalPrompt   = $function:prompt
    OriginalReadLine = $function:PSConsoleHostReadLine
    CommandRan       = $false
    Restore          = $null
}

function global:__TcmdOsc([string] $Payload) {
    "$([char]0x1b)]$Payload$([char]7)"
}

function global:prompt {
    # Must be the first statement: the status of the user's last command.
    $commandSucceeded = $global:?
    $state = $global:__Tcmd
    $result = [System.Text.StringBuilder]::new()

    if ($state.CommandRan) {
        $exitCode = if ($commandSucceeded) { 0 } elseif ($global:LASTEXITCODE) { $global:LASTEXITCODE } else { 1 }
        [void] $result.Append((__TcmdOsc "133;D;$exitCode"))
        $state.CommandRan = $false
    }

    [void] $result.Append((__TcmdOsc '133;A'))

    $location = $ExecutionContext.SessionState.Path.CurrentLocation
    if ($location.Provider.Name -eq 'FileSystem') {
        [void] $result.Append((__TcmdOsc "9;9;`"$($location.ProviderPath)`""))
    }

    if ($null -ne $state.Restore) {
        [void] $result.Append((__TcmdOsc '7731;tcmd;restore-pending'))
    }

    # Let the original prompt see the same $? as without us (the trick VS Code uses).
    if (-not $commandSucceeded) {
        Write-Error 'failure' -ErrorAction Ignore
    }

    [void] $result.Append((& $state.OriginalPrompt) -join '')
    [void] $result.Append((__TcmdOsc '133;B'))
    $result.ToString()
}

if ($global:__Tcmd.OriginalReadLine) {
    function global:PSConsoleHostReadLine {
        $line = & $global:__Tcmd.OriginalReadLine
        $global:__Tcmd.CommandRan = $true
        [Console]::Write((__TcmdOsc '133;C'))
        $line
    }
}

Set-PSReadLineKeyHandler -Chord F24 -BriefDescription TerminalCommanderRequest -Description 'Terminal Commander request' -ScriptBlock {
    $state = $global:__Tcmd
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

    [Console]::Write((__TcmdOsc "7731;tcmd;ack;$($request.id);$status"))
}
