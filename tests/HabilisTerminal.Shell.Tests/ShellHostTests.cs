using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.Shell.Tests;

public class ShellHostTests
{
    [Fact]
    public async Task Reports_idle_state_and_starting_directory()
    {
        using var shell = new TestShell();

        await shell.WaitIdleAsync();
        await TestShell.WaitUntilAsync(() => shell.Host.CurrentDirectory is not null, "current directory");

        Assert.Equal(shell.WorkingDirectory, shell.Host.CurrentDirectory, ignoreCase: true);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Change_directory_reports_errors()
    {
        using var shell = new TestShell();
        await shell.WaitIdleAsync();

        var status = await shell.WaitCompletedAsync(shell.Host.ChangeDirectory(Path.Combine(shell.WorkingDirectory, "missing")));

        Assert.StartsWith("error;", status, StringComparison.Ordinal);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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
