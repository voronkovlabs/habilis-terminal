using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.Shell.Tests;

public class ShellHostTests
{
    [WindowsFact]
    public async Task Reports_idle_state_and_starting_directory()
    {
        using var shell = new TestShell();

        await shell.WaitIdleAsync();
        await TestShell.WaitUntilAsync(() => shell.Host.CurrentDirectory is not null, "current directory");

        Assert.Equal(shell.WorkingDirectory, shell.Host.CurrentDirectory, ignoreCase: true);
    }

    [WindowsFact]
    public async Task Tracks_command_execution_and_shell_cd()
    {
        using var shell = new TestShell();
        var states = new List<ShellState>();
        shell.Host.StateChanged += state => { lock (states) { states.Add(state); } };
        var target = shell.CreateDirectory("from shell");
        await shell.WaitIdleAsync();

        shell.Host.Write($"Set-Location -LiteralPath '{target}'\r");

        await TestShell.WaitUntilAsync(() => string.Equals(shell.Host.CurrentDirectory, target, StringComparison.OrdinalIgnoreCase), "cwd update");
        await shell.WaitIdleAsync();
        lock (states)
        {
            Assert.Contains(ShellState.Busy, states);
        }
    }

    [WindowsFact]
    public async Task Change_directory_keeps_typed_text_and_adds_no_history()
    {
        using var shell = new TestShell();
        var target = shell.CreateDirectory("panel target");
        await shell.WaitIdleAsync();
        shell.Host.Write("\"BASE=$((Get-History).Count)\"\r");
        // "BASE=" also appears in the echoed command line; wait for the number itself.
        await TestShell.WaitUntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(shell.Text, "BASE=[0-9]"), "history baseline");
        await shell.WaitIdleAsync();
        var baseline = int.Parse(System.Text.RegularExpressions.Regex.Match(shell.Text, "BASE=(\\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        shell.Host.Write("Write-Output ('typed' + '-kept'); \"HISTORY=$((Get-History).Count)\"");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var status = await shell.WaitCompletedAsync(shell.Host.ChangeDirectory(target));

        Assert.Equal("ok", status);
        await TestShell.WaitUntilAsync(() => string.Equals(shell.Host.CurrentDirectory, target, StringComparison.OrdinalIgnoreCase), "cwd update");

        shell.Host.Write("\r");
        await shell.WaitForTextAsync("typed-kept");
        await TestShell.WaitUntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(shell.Text, "HISTORY=[0-9]"), "history count");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var count = System.Text.RegularExpressions.Regex.Match(shell.Text, "HISTORY=(\\d+)").Groups[1].Value;
        // Only the BASE command was added; the cd request left no trace.
        Assert.Equal((baseline + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), count);
    }

    [WindowsFact]
    public async Task Change_directory_after_the_console_got_lower_draws_the_prompt_again()
    {
        using var shell = new TestShell();
        var target = shell.CreateDirectory("after shrink");
        await shell.WaitIdleAsync();

        // The prompt near the bottom of a 30-row console, then the console only 10 rows high: PSReadLine still aims at
        // the old row when it draws the prompt again (#263).
        shell.Host.Write("1..26 | ForEach-Object { \"row $_\" }\r");
        await shell.WaitForTextAsync("row 26");
        await shell.WaitIdleAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        shell.Host.Resize(120, 10);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var status = await shell.WaitCompletedAsync(shell.Host.ChangeDirectory(target));

        Assert.Equal("ok", status);
        await TestShell.WaitUntilAsync(() => string.Equals(shell.Host.CurrentDirectory, target, StringComparison.OrdinalIgnoreCase), "cwd update");
        await shell.WaitForTextAsync("after shrink>");
        shell.Host.Write("\"AFTER=$($PWD.Path.Length)\"\r");
        await TestShell.WaitUntilAsync(() => System.Text.RegularExpressions.Regex.IsMatch(shell.Text, "AFTER=[0-9]"), "the shell still answers");
    }

    [WindowsFact]
    public async Task Change_directory_reports_errors()
    {
        using var shell = new TestShell();
        await shell.WaitIdleAsync();

        var status = await shell.WaitCompletedAsync(shell.Host.ChangeDirectory(Path.Combine(shell.WorkingDirectory, "missing")));

        Assert.StartsWith("error;", status, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Change_directory_while_busy_is_applied_at_the_next_prompt()
    {
        using var shell = new TestShell();
        var first = shell.CreateDirectory("first");
        var second = shell.CreateDirectory("second");
        await shell.WaitIdleAsync();

        shell.Host.Write("Start-Sleep -Milliseconds 1500\r");
        await TestShell.WaitUntilAsync(() => shell.Host.State == ShellState.Busy, "busy");
        shell.Host.ChangeDirectory(first);
        var lastId = shell.Host.ChangeDirectory(second);

        Assert.Equal("ok", await shell.WaitCompletedAsync(lastId));
        Assert.Equal(second, shell.Host.CurrentDirectory, ignoreCase: true);
    }

    [WindowsFact]
    public async Task Run_executes_command_and_restores_typed_text()
    {
        using var shell = new TestShell();
        await shell.WaitIdleAsync();

        shell.Host.Write("Write-Output ('restored' + '-text')");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal("ok", await shell.WaitCompletedAsync(shell.Host.Run("Write-Output ('ran' + '-command')")));
        await shell.WaitForTextAsync("ran-command");

        // The restore request runs at the next prompt; then Enter executes the restored text.
        Assert.Equal("ok", await shell.WaitOpCompletedAsync("restore"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        shell.Host.Write("\r");
        await shell.WaitForTextAsync("restored-text");

        shell.Host.Write("\"LAST=$((Get-History)[-1].CommandLine)\"\r");
        await shell.WaitForTextAsync("LAST=Write-Output ('restored' + '-text')");
    }

    [WindowsFact]
    public async Task Esc_on_an_empty_line_asks_for_the_panel_only_while_panels_are_shown()
    {
        using var shell = new TestShell();
        var requests = 0;
        shell.Host.FocusPanelRequested += () => Interlocked.Increment(ref requests);
        await shell.WaitIdleAsync();

        shell.Host.SendKey(0x1B, '\x1b');
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref requests));

        shell.Host.PanelsShown = true;
        shell.Host.SendKey(0x1B, '\x1b');
        await TestShell.WaitUntilAsync(() => Volatile.Read(ref requests) == 1, "focus-panel request");

        // With text on the line Esc clears it as before.
        shell.Host.Write("'NOT-' + 'RUN'");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        shell.Host.SendKey(0x1B, '\x1b');
        shell.Host.Write("'CLEARED-' + 'OK'\r");
        await shell.WaitForTextAsync("CLEARED-OK");
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.DoesNotContain("NOT-RUN", shell.Text);

        shell.Host.PanelsShown = false;
        Assert.False(shell.Host.PanelsShown);
    }

    [WindowsFact]
    public async Task Keys_handled_by_the_script_draw_cyrillic_text()
    {
        using var shell = new TestShell();
        await shell.WaitIdleAsync();

        // A script key handler runs in the console's code page; drawing the changed line must still be Unicode.
        shell.Host.Write("'\u043f\u0440\u0438\u0432\u0435\u0442' + 'x'zz");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var before = shell.Text.Length;
        shell.Host.SendKey(0x08, '\b');
        await Task.Delay(500, TestContext.Current.CancellationToken);
        shell.Host.SendKey(0x24);
        shell.Host.SendKey(0x2E);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("???", shell.Text[before..]);
    }

    [WindowsFact]
    public async Task Line_keys_come_back_to_the_panel_on_an_empty_line_only_in_panel_mode()
    {
        using var shell = new TestShell();
        var returned = new System.Collections.Concurrent.ConcurrentQueue<string>();
        shell.Host.PanelKeyReturned += returned.Enqueue;
        await shell.WaitIdleAsync();

        // Virtual keys as the console receives them (issue #90): the keys an empty line hands back to the panel.
        (int Key, char Char, string Name)[] keys =
        [
            (0x0D, '\r', "Enter"), (0x25, '\0', "Left"), (0x27, '\0', "Right"), (0x24, '\0', "Home"), (0x23, '\0', "End"),
            (0x2E, '\0', "Delete"), (0x08, '\b', "Backspace"), (0x1B, '\x1b', "Escape"), (0x09, '\t', "Tab"),
        ];

        shell.Host.PanelMode = true;
        foreach (var (key, ch, name) in keys)
        {
            shell.Host.SendKey(key, ch);
            await TestShell.WaitUntilAsync(() => returned.Count == 1, $"{name} returned");
            Assert.True(returned.TryDequeue(out var back));
            Assert.Equal(name, back);
        }

        foreach (var name in new[] { "Add", "Subtract", "Multiply" })
        {
            shell.Host.SendGrayKey(name);
            await TestShell.WaitUntilAsync(() => returned.Count == 1, $"{name} returned");
            Assert.True(returned.TryDequeue(out var back));
            Assert.Equal(name, back);
        }

        // Gray keys are typed on a line with text (as in Far).
        shell.Host.Write("'GRAY' + '");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        shell.Host.SendGrayKey("Add");
        shell.Host.SendGrayKey("Subtract");
        shell.Host.Write("'\r");
        await shell.WaitForTextAsync("GRAY+-");

        // With text the keys edit the line and Enter runs it; Tab completes; nothing comes back.
        shell.Host.Write("x'PANEL-' + 'EDITq'z");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        foreach (var (key, ch) in new[] { (0x24, '\0'), (0x2E, '\0'), (0x23, '\0'), (0x08, '\b'), (0x25, '\0'), (0x08, '\b'), (0x27, '\0'), (0x0D, '\r') })
        {
            shell.Host.SendKey(key, ch);
        }

        await shell.WaitForTextAsync("PANEL-EDIT");
        shell.Host.Write("Write-Outp");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        shell.Host.SendKey(0x09, '\t');
        await shell.WaitForTextAsync("Write-Output");
        Assert.Empty(returned);

        // Inside the console (no panel mode) an empty line keeps the keys: Enter just shows a new prompt.
        shell.Host.SendKey(0x1B, '\x1b');
        shell.Host.PanelMode = false;
        Assert.False(shell.Host.PanelMode);
        shell.Host.SendKey(0x25, '\0');
        shell.Host.SendKey(0x0D, '\r');
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(returned);
    }

    [WindowsFact]
    public async Task Enter_on_a_plugin_prefix_reports_the_address_instead_of_running_it()
    {
        using var shell = new TestShell();
        var addresses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        shell.Host.AddressEntered += addresses.Enqueue;
        shell.Host.AddressPrefixes = ["demo", "sqs"];
        await shell.WaitIdleAsync();

        // A bare prefix opens the plugin's default location.
        shell.Host.Write("sqs:\r");
        await TestShell.WaitUntilAsync(() => addresses.Count == 1, "open-address message for a bare prefix");
        Assert.True(addresses.TryDequeue(out var bare));
        Assert.Equal("sqs:", bare);

        shell.Host.Write("sqs:prod/eu-west-1\r");
        await TestShell.WaitUntilAsync(() => addresses.Count == 1, "open-address message");
        Assert.True(addresses.TryPeek(out var address));
        Assert.Equal("sqs:prod/eu-west-1", address);

        // Other lines, including ones with an unknown prefix or a drive letter, still run.
        shell.Host.Write("'RAN-' + 'OK'\r");
        await shell.WaitForTextAsync("RAN-OK");
        shell.Host.Write("Set-Location C:; 'ON-' + 'C'\r");
        await shell.WaitForTextAsync("ON-C");
        Assert.Single(addresses);
    }

    [WindowsFact]
    public async Task Cd_with_a_variable_or_to_a_file_is_reported_as_in_far()
    {
        // Issue #245: PowerShell expands no %VARIABLES% and cannot cd to a file.
        using var shell = new TestShell();
        var paths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        shell.Host.CdEntered += paths.Enqueue;
        await shell.WaitIdleAsync();
        var folder = Path.Combine(shell.WorkingDirectory, "sub dir");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "x");
        // The shell may see %TEMP% in its short 8.3 form (the CI runner does): the folder is recognised by a file in it.
        var marker = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "habi-cd-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(marker, "x");
        try
        {
            foreach (var (line, isTemp) in new[]
            {
                (@"cd 'sub dir\notes.txt'", false),
                ("cd %TEMP%", true),
                (@"Set-Location ""%TEMP%""", true),
            })
            {
                shell.Host.Write(line + "\r");
                await TestShell.WaitUntilAsync(() => !paths.IsEmpty, "cd message for: " + line);
                Assert.True(paths.TryDequeue(out var path));
                if (isTemp)
                {
                    Assert.True(File.Exists(Path.Combine(path, Path.GetFileName(marker))), path);
                }
                else
                {
                    Assert.Equal(file, path, ignoreCase: true);
                }
            }
        }
        finally
        {
            File.Delete(marker);
        }

        // A folder without variables, or a place that is not there, is cd as ever.
        shell.Host.Write("cd 'sub dir'\r");
        await TestShell.WaitUntilAsync(() => string.Equals(shell.Host.CurrentDirectory, folder, StringComparison.OrdinalIgnoreCase), "plain cd");
        shell.Host.Write("cd %HABI_NO_SUCH_245%\r");
        await shell.WaitForTextAsync("HABI_NO_SUCH_245");
        Assert.Empty(paths);
    }

    [WindowsFact]
    public async Task Goto_reports_what_follows_it_instead_of_running_it()
    {
        using var shell = new TestShell();
        var targets = new System.Collections.Concurrent.ConcurrentQueue<string>();
        shell.Host.GotoEntered += targets.Enqueue;
        await shell.WaitIdleAsync();

        foreach (var (line, expected) in new[]
        {
            (@"goto C:\Program Files\a b.txt", @"C:\Program Files\a b.txt"),
            (@"  GOTO   ""C:\x y""  ", @"""C:\x y"""),
            ("goto s3://bucket/a/", "s3://bucket/a/"),
        })
        {
            shell.Host.Write(line + "\r");
            await TestShell.WaitUntilAsync(() => !targets.IsEmpty, "goto message for: " + line);
            Assert.True(targets.TryDequeue(out var target));
            Assert.Equal(expected, target);
        }

        // Without a target, or as part of something else, it is an ordinary line.
        shell.Host.Write("function gotox { 'GOTO-' + 'X' }\r");
        await shell.WaitIdleAsync();
        shell.Host.Write("gotox y\r");
        await shell.WaitForTextAsync("GOTO-X");
        shell.Host.Write("function goto { 'BARE-' + 'GOTO' }\r");
        await shell.WaitIdleAsync();
        shell.Host.Write("goto\r");
        await shell.WaitForTextAsync("BARE-GOTO");
        Assert.Empty(targets);
    }

    [WindowsFact]
    public async Task Enter_on_a_registered_command_reports_the_address_instead_of_running_it()
    {
        using var shell = new TestShell();
        var addresses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        shell.Host.AddressEntered += addresses.Enqueue;
        shell.Host.AddressCommands = ["cd demo"];
        await shell.WaitIdleAsync();

        foreach (var (line, expected) in new[]
        {
            ("cd demo:", "demo:"),
            ("cd demo://bucket/a/", "demo://bucket/a/"),
            ("  CD   \"demo://bucket/my folder/\"  ", "demo://bucket/my folder/"),
            ("cd 'demo://b/'", "demo://b/"),
        })
        {
            shell.Host.Write(line + "\r");
            await TestShell.WaitUntilAsync(() => !addresses.IsEmpty, "open-address message for: " + line);
            Assert.True(addresses.TryDequeue(out var address));
            Assert.Equal(expected, address);
        }

        // The same command with anything else, and the address with another command, still run.
        shell.Host.Write("cd C:\\; 'ON-' + 'C'\r");
        await shell.WaitForTextAsync("ON-C");
        shell.Host.Write("function foo { 'FOO-' + 'RAN' }\r");
        await shell.WaitIdleAsync();
        shell.Host.Write("foo demo:x\r");
        await shell.WaitForTextAsync("FOO-RAN");
        Assert.Empty(addresses);
    }

    [WindowsFact]
    public async Task Non_filesystem_location_does_not_change_directory()
    {
        using var shell = new TestShell();
        await shell.WaitIdleAsync();
        await TestShell.WaitUntilAsync(() => shell.Host.CurrentDirectory is not null, "current directory");
        var before = shell.Host.CurrentDirectory;

        shell.Host.Write("Set-Location Env:; 'IN-ENV'\r");
        await shell.WaitForTextAsync("IN-ENV");
        await shell.WaitIdleAsync();

        Assert.Equal(before, shell.Host.CurrentDirectory);
    }
}
