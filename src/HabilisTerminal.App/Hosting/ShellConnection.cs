using Microsoft.Terminal.Wpf;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App.Hosting;

/// <summary>Bridges the Windows Terminal WPF control to a <see cref="ShellHost"/>.</summary>
public sealed class ShellConnection : ITerminalConnection, IDisposable
{
    private int _columns = 120;
    private int _rows = 30;
    private bool _started;

    /// <summary>The control answers terminal queries in replayed output; those answers must not reach the shell as typing.</summary>
    private bool _replaying;

    public ShellConnection(ShellHost host)
    {
        Host = host;
        Host.Output += text => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public ShellHost Host { get; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _replaying = true;
        try
        {
            Host.Start(_columns, _rows); // a shell taken over from another process replays its output here
        }
        finally
        {
            _replaying = false;
        }
    }

    /// <summary>Input from the terminal control: keys (win32-input-mode sequences) and replies.</summary>
    public void WriteInput(string data)
    {
        if (!_replaying)
        {
            Host.Write(data);
        }
    }

    public void Resize(uint rows, uint columns)
    {
        _rows = (int)rows;
        _columns = (int)columns;
        Host.Resize(_columns, _rows);
    }

    /// <summary>Prototype (issue #122): a new control takes over; the running shell is not started again.</summary>
    public void Reattach()
    {
        _started = true;

        // The old control would go on answering terminal queries: a second answer reaches the shell as typing.
        TerminalOutput = null;
    }

    /// <summary>Prototype (issue #122): a new control gets all the output so far.</summary>
    public void ReplayOutput()
    {
        _replaying = true;
        try
        {
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(Host.OutputSoFar()));
        }
        finally
        {
            _replaying = false;
        }
    }

    public void Close() => Dispose();

    public void Dispose() => Host.Dispose();
}
