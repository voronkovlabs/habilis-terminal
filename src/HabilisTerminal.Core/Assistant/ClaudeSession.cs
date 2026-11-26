using System.Diagnostics;

namespace HabilisTerminal.Core.Assistant;

/// <summary>
/// One Claude Code process talking stream-json (issue #197). Events arrive on a reader thread through
/// <see cref="Event"/>; the caller moves them to its UI thread. Messages and answers may be sent from any thread.
/// </summary>
public sealed class ClaudeSession : IDisposable
{
    private readonly Process _process;
    private readonly string? _cleanup;
    private readonly object _writeGate = new();
    private readonly List<string> _errors = [];
    private bool _disposed;

    private ClaudeSession(Process process, string? cleanup)
    {
        _process = process;
        _cleanup = cleanup;
    }

    /// <summary>Raised on a reader thread.</summary>
    public event Action<AssistantEvent>? Event;

    /// <summary>The process ended; raised once, after the last event.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => !_disposed && !_process.HasExited;

    /// <summary>Starts Claude Code; <paramref name="cleanup"/> is a file to delete when the session ends.</summary>
    public static ClaudeSession Start(ProcessStartInfo start, string? cleanup = null)
    {
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Claude Code did not start.");
        }

        var session = new ClaudeSession(process, cleanup);
        var output = new Thread(session.ReadOutput) { IsBackground = true, Name = "Assistant output" };
        var errors = new Thread(session.ReadErrors) { IsBackground = true, Name = "Assistant errors" };
        output.Start();
        errors.Start();
        return session;
    }

    /// <summary>The user's message; starts a turn.</summary>
    public void Send(string text) => Write(ClaudeProtocol.UserMessage(text));

    public void Allow(AssistantAsks permission) => Write(ClaudeProtocol.Allow(permission));

    public void Deny(AssistantAsks permission, string message) => Write(ClaudeProtocol.Deny(permission, message));

    /// <summary>Stops the turn under way.</summary>
    public void Interrupt() => Write(ClaudeProtocol.Interrupt());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _process.Dispose();
        DeleteCleanup();
    }

    private void Write(string line)
    {
        lock (_writeGate)
        {
            if (_disposed || _process.HasExited)
            {
                throw new InvalidOperationException("The assistant's session has ended.");
            }

            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
    }

    private void ReadOutput()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                foreach (var item in ClaudeProtocol.Parse(line))
                {
                    Event?.Invoke(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away.
        }

        var code = 0;
        try
        {
            _process.WaitForExit();
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        if (code != 0 && !_disposed)
        {
            string text;
            lock (_errors)
            {
                text = string.Join("\n", _errors.TakeLast(10));
            }

            Event?.Invoke(new AssistantProblem(text.Length > 0 ? text : $"Claude Code ended with code {code}."));
        }

        DeleteCleanup();
        Exited?.Invoke(code);
    }

    private void ReadErrors()
    {
        try
        {
            while (_process.StandardError.ReadLine() is { } line)
            {
                lock (_errors)
                {
                    _errors.Add(line);
                    if (_errors.Count > 50)
                    {
                        _errors.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void DeleteCleanup()
    {
        if (_cleanup is null)
        {
            return;
        }

        try
        {
            File.Delete(_cleanup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
