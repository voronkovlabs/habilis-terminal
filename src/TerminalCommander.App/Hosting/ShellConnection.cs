using Microsoft.Terminal.Wpf;
using TerminalCommander.Shell.Integration;

namespace TerminalCommander.App.Hosting;

/// <summary>Bridges the Windows Terminal WPF control to a <see cref="ShellHost"/>.</summary>
public sealed class ShellConnection : ITerminalConnection, IDisposable
{
    private int _columns = 120;
    private int _rows = 30;
    private bool _started;

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
        Host.Start(_columns, _rows);
    }

    /// <summary>Input from the terminal control: keys (win32-input-mode sequences) and replies.</summary>
    public void WriteInput(string data) => Host.Write(data);

    public void Resize(uint rows, uint columns)
    {
        _rows = (int)rows;
        _columns = (int)columns;
        Host.Resize(_columns, _rows);
    }

    public void Close() => Dispose();

    public void Dispose() => Host.Dispose();
}
