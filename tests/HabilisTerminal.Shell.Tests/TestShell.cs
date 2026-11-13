using System.Text;
using System.Text.RegularExpressions;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.Shell.Tests;

/// <summary>
/// A real pwsh (no user profile, history not saved) with the integration script, working in its own temp directory; or,
/// with <c>zsh: true</c>, the system's zsh with habilis.zsh and no user startup files (issue #268).
/// </summary>
internal sealed partial class TestShell : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly StringBuilder _output = new();
    private readonly List<(int Id, string Op, string Status)> _completed = [];

    public TestShell(bool zsh = false)
    {
        WorkingDirectory = Path.Combine(Path.GetTempPath(), "habi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkingDirectory);

        Host = new ShellHost(zsh
            ? new ShellHostOptions("/bin/zsh", ShellScripts.IntegrationScriptPath, WorkingDirectory, LoadProfile: false)
            : new ShellHostOptions(
                PwshLocator.Find() ?? throw new InvalidOperationException("pwsh.exe not found"),
                ShellScripts.IntegrationScriptPath,
                WorkingDirectory,
                InitCommand: "Set-PSReadLineOption -HistorySaveStyle SaveNothing -PredictionSource None",
                LoadProfile: false));

        Host.Output += text =>
        {
            lock (_output)
            {
                _output.Append(text);
            }
        };
        Host.RequestCompleted += (id, op, status) =>
        {
            lock (_completed)
            {
                _completed.Add((id, op, status));
            }
        };

        Host.Start(120, 30);
    }

    public ShellHost Host { get; }

    public string WorkingDirectory { get; }

    /// <summary>Output with escape sequences removed.</summary>
    public string Text
    {
        get
        {
            lock (_output)
            {
                return EscapeSequences().Replace(_output.ToString(), string.Empty);
            }
        }
    }

    public string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(WorkingDirectory, name)).FullName;

    public Task WaitIdleAsync() => WaitUntilAsync(() => Host.State == ShellState.Idle, "shell idle");

    public Task WaitForTextAsync(string text) => WaitUntilAsync(() => Text.Contains(text, StringComparison.Ordinal), $"output '{text}'");

    public async Task<string> WaitCompletedAsync(int id)
    {
        await WaitUntilAsync(() => { lock (_completed) { return _completed.Any(c => c.Id == id); } }, $"request {id}");
        lock (_completed)
        {
            return _completed.First(c => c.Id == id).Status;
        }
    }

    public async Task<string> WaitOpCompletedAsync(string op)
    {
        await WaitUntilAsync(() => { lock (_completed) { return _completed.Any(c => c.Op == op); } }, $"{op} request");
        lock (_completed)
        {
            return _completed.First(c => c.Op == op).Status;
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        Host.Dispose();

        // pwsh holds its current directory until it has exited, which takes a moment after the pseudo console closes.
        for (var attempt = 0; attempt < 50 && Directory.Exists(WorkingDirectory); attempt++)
        {
            try
            {
                Directory.Delete(WorkingDirectory, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [GeneratedRegex(@"\x1b(\][^\a\x1b]*(\a|\x1b\\)|\[[0-9;?]*[ -/]*[@-~]|[()][0-9A-Za-z]|[=>78])")]
    private static partial Regex EscapeSequences();
}
