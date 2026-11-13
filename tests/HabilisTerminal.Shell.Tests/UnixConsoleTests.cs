using System.Collections.Concurrent;
using System.Text;
using HabilisTerminal.Shell.Integration;
using HabilisTerminal.Shell.Pty;

namespace HabilisTerminal.Shell.Tests;

/// <summary>The console on macOS and Linux (issue #268): a pty through forkpty, and zsh with habilis.zsh.</summary>
public class UnixConsoleTests
{
    private static async Task<(string Output, int ExitCode)> RunAsync(string program, string[] arguments, Action<IPseudoTerminal>? started = null)
    {
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pty = PseudoTerminal.Start(
            new PtyStartInfo(program, string.Empty, Path.GetTempPath(), 80, 24, ArgumentList: arguments),
            text => { lock (output) { output.Append(text); } },
            code => exited.TrySetResult(code));
        started?.Invoke(pty);
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (output)
        {
            return (output.ToString(), code);
        }
    }

    [UnixFact]
    public async Task A_program_runs_on_a_terminal_and_its_output_and_exit_code_come_back()
    {
        var (output, code) = await RunAsync("/bin/sh", ["-c", "[ -t 0 ] && echo on-a-tty; printf 'привет\\n'; exit 3"]);

        Assert.Contains("on-a-tty", output, StringComparison.Ordinal);
        Assert.Contains("привет", output, StringComparison.Ordinal); // UTF-8 kept across reads
        Assert.Equal(3, code);
    }

    [UnixFact]
    public async Task The_terminal_has_the_size_asked_for_and_follows_a_resize()
    {
        // The second size is asked for after the first was printed.
        var (output, _) = await RunAsync("/bin/sh", ["-c", "stty size; read go; stty size"], pty =>
        {
            Thread.Sleep(1500);
            pty.Resize(100, 40);
            pty.Write("\r");
        });

        var first = output.IndexOf("24 80", StringComparison.Ordinal);
        Assert.True(first >= 0, output);
        Assert.True(output.IndexOf("40 100", first, StringComparison.Ordinal) > first, output);
    }

    [UnixFact]
    public async Task Typed_input_reaches_the_program()
    {
        var (output, _) = await RunAsync("/bin/sh", ["-c", "read line; echo \"got:$line\""], pty => pty.Write("hello\r"));

        Assert.Contains("got:hello", output, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task Zsh_reports_idle_and_its_folder_and_follows_a_cd_request()
    {
        using var shell = new TestShell(zsh: true);
        await shell.WaitIdleAsync();
        await TestShell.WaitUntilAsync(() => SamePath(shell.Host.CurrentDirectory, shell.WorkingDirectory), "starting folder");

        var target = shell.CreateDirectory("sub dir with 'quote'");
        var id = shell.Host.ChangeDirectory(target);

        Assert.Equal("ok", await shell.WaitCompletedAsync(id));
        await TestShell.WaitUntilAsync(() => SamePath(shell.Host.CurrentDirectory, target), "folder after cd");
    }

    [UnixFact]
    public async Task Zsh_reports_commands_their_lines_and_exit_codes()
    {
        using var shell = new TestShell(zsh: true);
        var lines = new ConcurrentQueue<string>();
        var codes = new ConcurrentQueue<int?>();
        shell.Host.CommandLineAccepted += lines.Enqueue;
        shell.Host.CommandFinished += codes.Enqueue;
        await shell.WaitIdleAsync();

        shell.Host.Write("echo hi-from-zsh; false\r");
        await shell.WaitForTextAsync("hi-from-zsh");
        await TestShell.WaitUntilAsync(() => !codes.IsEmpty, "command finished");

        Assert.Equal("echo hi-from-zsh; false", Assert.Single(lines));
        Assert.Equal(1, Assert.Single(codes));
    }

    [UnixFact]
    public async Task Zsh_takes_goto_cd_to_a_file_and_plugin_addresses()
    {
        using var shell = new TestShell(zsh: true);
        var gotos = new ConcurrentQueue<string>();
        var cds = new ConcurrentQueue<string>();
        var addresses = new ConcurrentQueue<string>();
        shell.Host.GotoEntered += gotos.Enqueue;
        shell.Host.CdEntered += cds.Enqueue;
        shell.Host.AddressEntered += addresses.Enqueue;
        shell.Host.AddressPrefixes = ["s3"];
        shell.Host.AddressCommands = ["cd s3"];
        var file = Path.Combine(shell.CreateDirectory("docs"), "notes.txt");
        File.WriteAllText(file, "x");
        await shell.WaitIdleAsync();

        shell.Host.Write("goto /tmp/some place\r");
        await TestShell.WaitUntilAsync(() => !gotos.IsEmpty, "goto");
        Assert.Equal("/tmp/some place", Assert.Single(gotos));

        shell.Host.Write("cd docs/notes.txt\r");
        await TestShell.WaitUntilAsync(() => !cds.IsEmpty, "cd to a file");
        Assert.True(SamePath(Assert.Single(cds), file));

        shell.Host.Write("s3:bucket/a\r");
        await TestShell.WaitUntilAsync(() => addresses.Count == 1, "prefix");
        shell.Host.Write("cd 's3://bucket/b c/'\r");
        await TestShell.WaitUntilAsync(() => addresses.Count == 2, "command with an address");
        Assert.Equal(["s3:bucket/a", "s3://bucket/b c/"], addresses.ToArray());

        // A folder, without anything to expand, is the shell's own cd.
        shell.Host.Write("cd docs\r");
        await TestShell.WaitUntilAsync(() => SamePath(shell.Host.CurrentDirectory, Path.GetDirectoryName(file)), "plain cd");
        Assert.Single(cds);
    }

    [UnixFact]
    public async Task Zsh_runs_a_command_and_gives_the_typed_text_back()
    {
        using var shell = new TestShell(zsh: true);
        await shell.WaitIdleAsync();
        shell.Host.Write("echo typed-before");
        await Task.Delay(500); // the line editor has taken the keys

        var id = shell.Host.Run("echo ran-by-habilis");

        Assert.Equal("ok", await shell.WaitCompletedAsync(id));
        await shell.WaitForTextAsync("ran-by-habilis\r\n");
        await shell.WaitOpCompletedAsync("restore");

        // The typed line is back: Enter runs it.
        var before = CountOf(shell.Text, "typed-before\r\n");
        shell.Host.Write("\r");
        await TestShell.WaitUntilAsync(() => CountOf(shell.Text, "typed-before\r\n") > before, "the restored line run");
    }

    [UnixFact]
    public async Task Zsh_gives_an_empty_line_key_to_the_panel_in_panel_mode()
    {
        using var shell = new TestShell(zsh: true);
        var keys = new ConcurrentQueue<string>();
        shell.Host.PanelKeyReturned += keys.Enqueue;
        await shell.WaitIdleAsync();
        shell.Host.PanelMode = true;

        shell.Host.Write("\r");
        shell.Host.Write("\u001b[D");
        await TestShell.WaitUntilAsync(() => keys.Count == 2, "panel keys");
        Assert.Equal(["Enter", "Left"], keys.ToArray());

        // With text on the line the keys edit it.
        shell.Host.Write("echo x\u001b[D\u001b[D");
        await Task.Delay(500);
        Assert.Equal(2, keys.Count);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>macOS reaches /var through /private/var: the same folder by either name.</summary>
    private static bool SamePath(string? a, string? b)
    {
        static string Real(string path) => path.StartsWith("/private/", StringComparison.Ordinal) ? path["/private".Length..] : path;
        return a is not null && b is not null && string.Equals(Real(a.TrimEnd('/')), Real(b.TrimEnd('/')), StringComparison.Ordinal);
    }
}
