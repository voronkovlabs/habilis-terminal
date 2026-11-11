# Habilis Terminal shell integration for PowerShell 7+ (SPEC §7).
#
# Dot-sourced after the user's profile. It wraps the prompt that the profile (or oh-my-posh) defined and adds
# invisible OSC sequences around it; the visible prompt is unchanged. Requests from Habilis Terminal arrive
# as the F24 key, with the request itself in "$env:HABI_CHANNEL\request.json".
#
#   OSC 133;A / 133;B        prompt starts / prompt ends, waiting for input
#   OSC 133;C / 133;D;code   command accepted / command finished
#   OSC 9;9;"path"           current file system directory
#   OSC 7731;habi;...        Habilis Terminal messages (ack, restore-pending, focus-panel, panel-key, command-start, command-line)

if ($global:__Habi -or -not $env:HABI_CHANNEL) {
    return
}

$global:__Habi = [pscustomobject]@{
    Channel          = $env:HABI_CHANNEL
    OriginalPrompt   = $function:prompt
    OriginalReadLine = $function:PSConsoleHostReadLine
    CommandRan       = $false
    Restore          = $null
    EscapeFunction   = (Get-PSReadLineKeyHandler -Chord Escape | Select-Object -First 1).Function
    EnterFunction    = (Get-PSReadLineKeyHandler -Chord Enter | Select-Object -First 1).Function
}

function global:__HabiOsc([string] $Payload) {
    "$([char]0x1b)]$Payload$([char]7)"
}

# PSReadLine draws in UTF-8, but gives a script key handler the console's original code page. A handler that changes the
# line would draw every character outside that code page as '?' (Cyrillic under 437), so it switches first. PSReadLine
# sets its own encoding again after the handler, and the original one when the line is done.
function global:__HabiDrawUnicode {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
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
        # The line, for the console's tab (issue #119).
        [Console]::Write((__HabiOsc "7731;habi;command-line;$([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$line)))"))
        # Where the output starts and the console size, for the console that grows over the panels (issue #82).
        try {
            [Console]::Write((__HabiOsc "7731;habi;command-start;$([Console]::CursorTop);$([Console]::WindowHeight);$([Console]::WindowWidth)"))
        } catch {
            # No console size (redirected): the host simply does not grow the console.
        }
        [Console]::Write((__HabiOsc '133;C'))
        $line
    }
}

Set-PSReadLineKeyHandler -Chord F24 -BriefDescription HabilisTerminalRequest -Description 'Habilis Terminal request' -ScriptBlock {
    __HabiDrawUnicode
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
# prefixes, one per line, in the 'prefixes' file of the channel. A plugin can also catch a command in front of its
# address ("cd s3://bucket/", issue #140): the 'commands' file holds "cd s3", one per line. Other lines run as before.
function global:__HabiSendLine($line, $message, $text) {
    [Microsoft.PowerShell.PSConsoleReadLine]::AddToHistory($line)
    [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine()
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
    [Console]::Write((__HabiOsc "7731;habi;$message;$encoded"))
}

function global:__HabiOpenAddress($line, $address) {
    __HabiSendLine $line 'open-address' $address
}

function global:__HabiEnter($key, $arg) {
    __HabiDrawUnicode
    $state = $global:__Habi
    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)

    # "goto <path or address>" (issue #152): the panel goes there, as a paste into the drive menu does.
    if ($line -match '^\s*goto\s+(\S.*?)\s*$') {
        __HabiSendLine $line 'goto' $Matches[1]
        return
    }

    # "cd" as in Far (issue #245): %VARIABLES% are expanded, as cmd and Far do, and a file's path goes to its folder
    # with the file under the cursor. Only a place that exists is taken; any other cd runs as before.
    if ($line -match '^\s*(?:cd|chdir|sl|Set-Location)\s+(["'']?)([^"'']+?)\1\s*$') {
        $target = $Matches[2]
        $expanded = [Environment]::ExpandEnvironmentVariables($target)
        $isFile = $false
        $exists = $false
        try {
            $isFile = Test-Path -LiteralPath $expanded -PathType Leaf
            $exists = $isFile -or (Test-Path -LiteralPath $expanded -PathType Container)
        }
        catch {
        }

        if ($exists -and ($isFile -or $expanded -ne $target)) {
            __HabiSendLine $line 'cd-path' (Resolve-Path -LiteralPath $expanded).ProviderPath
            return
        }
    }

    $prefixes = Join-Path $state.Channel 'prefixes'
    # "sqs:" alone counts too (the plugin's default location).
    if ($line -match '^\s*([A-Za-z][A-Za-z0-9]*):(\S|\s*$)' -and (Test-Path -LiteralPath $prefixes)) {
        $prefix = $Matches[1]
        if (@(Get-Content -LiteralPath $prefixes -Encoding utf8) -contains $prefix) {
            __HabiOpenAddress $line $line.Trim()
            return
        }
    }

    $commands = Join-Path $state.Channel 'commands'
    if ($line -match '^\s*(\S+)\s+(["'']?)(([A-Za-z][A-Za-z0-9]*):.*?)\2\s*$' -and (Test-Path -LiteralPath $commands)) {
        $address = $Matches[3]
        if (@(Get-Content -LiteralPath $commands -Encoding utf8) -contains "$($Matches[1]) $($Matches[4])") {
            __HabiOpenAddress $line $address
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

Set-PSReadLineKeyHandler -Chord Enter -BriefDescription HabilisTerminalEnter -Description 'Run the line, open a plugin address, or open the panel item on an empty line' -ScriptBlock {
    param($key, $arg)
    if (-not (__HabiPanelKey 'Enter')) {
        __HabiEnter $key $arg
    }
}

# Keys whose meaning depends on the command line, as in Far (issues #85, #90). While a panel has the keys, Habilis
# Terminal keeps the 'panel-mode' file in the channel; on an empty line these keys then go back to the panel
# (OSC 7731;habi;panel-key;<name>). With text on the line, or inside the console (reached by a click), they do
# whatever they were bound to before.
function global:__HabiPanelKey([string] $Name) {
    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
    if ($line.Length -eq 0 -and (Test-Path -LiteralPath (Join-Path $global:__Habi.Channel 'panel-mode'))) {
        [Console]::Write((__HabiOsc "7731;habi;panel-key;$Name"))
        return $true
    }

    $false
}

$global:__Habi | Add-Member -NotePropertyName PanelKeys -NotePropertyValue @{}
foreach ($entry in @(
        @('LeftArrow', 'Left', 'BackwardChar'),
        @('RightArrow', 'Right', 'ForwardChar'),
        @('Home', 'Home', 'BeginningOfLine'),
        @('End', 'End', 'EndOfLine'),
        @('Delete', 'Delete', 'DeleteChar'),
        @('Backspace', 'Backspace', 'BackwardDeleteChar'),
        @('Tab', 'Tab', 'TabCompleteNext'))) {
    $chord, $name, $default = $entry
    $function = (Get-PSReadLineKeyHandler -Chord $chord | Select-Object -First 1).Function
    if (-not $function -or -not [Microsoft.PowerShell.PSConsoleReadLine].GetMethod($function)) {
        $function = $default
    }

    $global:__Habi.PanelKeys[$chord] = @($name, $function)
    Set-PSReadLineKeyHandler -Chord $chord -BriefDescription "HabilisTerminal$name" -Description "$name, or the panel's $name on an empty line" -ScriptBlock {
        param($key, $arg)
        $name, $function = $global:__Habi.PanelKeys[$key.Key.ToString()]
        if (-not (__HabiPanelKey $name)) {
            __HabiDrawUnicode
            [Microsoft.PowerShell.PSConsoleReadLine]::$function($key, $arg)
        }
    }
}

# Gray +, - and *: select files on an empty line, typed on a line with text, as in Far. PSReadLine cannot tell them
# from the main keyboard's, so Habilis Terminal sends them as F13..F15 while a panel has the keys.
$global:__Habi | Add-Member -NotePropertyName GrayKeys -NotePropertyValue @{
    F13 = @('Add', '+')
    F14 = @('Subtract', '-')
    F15 = @('Multiply', '*')
}
foreach ($chord in $global:__Habi.GrayKeys.Keys) {
    Set-PSReadLineKeyHandler -Chord $chord -BriefDescription "HabilisTerminalGray$chord" -Description 'Habilis Terminal gray key' -ScriptBlock {
        param($key, $arg)
        $name, $text = $global:__Habi.GrayKeys[$key.Key.ToString()]
        if (-not (__HabiPanelKey $name)) {
            __HabiDrawUnicode
            [Microsoft.PowerShell.PSConsoleReadLine]::Insert($text)
        }
    }
}

# Esc: on an empty line the panel's Esc while a panel has the keys; inside the console it goes back to the panels
# while they are shown ('panels-shown' file in the channel). Otherwise whatever it was bound to before (RevertLine).
Set-PSReadLineKeyHandler -Chord Escape -BriefDescription HabilisTerminalEscape -Description 'Clear the line, or go back to the panel on an empty line' -ScriptBlock {
    param($key, $arg)
    $state = $global:__Habi
    if (__HabiPanelKey 'Escape') {
        return
    }

    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
    if ($line.Length -eq 0 -and (Test-Path -LiteralPath (Join-Path $state.Channel 'panels-shown'))) {
        [Console]::Write((__HabiOsc '7731;habi;focus-panel'))
        return
    }

    __HabiDrawUnicode
    $function = $state.EscapeFunction
    if (-not $function -or -not [Microsoft.PowerShell.PSConsoleReadLine].GetMethod($function)) {
        $function = 'RevertLine'
    }

    [Microsoft.PowerShell.PSConsoleReadLine]::$function($key, $arg)
}
