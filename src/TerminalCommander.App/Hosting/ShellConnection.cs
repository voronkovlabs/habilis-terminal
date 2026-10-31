using Microsoft.Terminal.Wpf;
using TerminalCommander.Shell.ConPty;

namespace TerminalCommander.App.Hosting;

/// <summary>Bridges the Windows Terminal WPF control to a <see cref="ConPtySession"/>.</summary>
public sealed class ShellConnection : ITerminalConnection, IDisposable
{
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private ConPtySession? _session;
    private int _columns = 120;
    private int _rows = 30;

    public ShellConnection(string commandLine, string? workingDirectory)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    /// <summary>Raised on a background thread when the shell process exits.</summary>
    public event Action<int>? Exited;

    public void Start()
    {
        if (_session is not null)
        {
            return;
        }

        _session = ConPtySession.Start(_commandLine, _workingDirectory, _columns, _rows);
        _session.Output += text => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
        _session.Exited += code => Exited?.Invoke(code);
    }

    /// <summary>Input from the terminal control: keys (win32-input-mode sequences) and replies.</summary>
    public void WriteInput(string data) => _session?.Write(data);

    public void Resize(uint rows, uint columns)
    {
        _rows = (int)rows;
        _columns = (int)columns;
        _session?.Resize(_columns, _rows);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }
}
