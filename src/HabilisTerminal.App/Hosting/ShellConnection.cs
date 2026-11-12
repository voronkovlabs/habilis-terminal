using HabilisTerminal.Shell.Integration;
using HabilisTerminal.Terminal;

namespace HabilisTerminal.App.Hosting;

/// <summary>
/// Joins a <see cref="ShellHost"/> to the console view (issue #253): the shell's output goes into the emulator, keys
/// and the emulator's replies go back to the shell, and the view's grid size becomes the pseudo console's.
/// </summary>
public sealed class ShellConnection : IDisposable
{
    private bool _started;

    public ShellConnection(ShellHost host, TerminalView view)
    {
        Host = host;
        View = view;
        Host.Output += text =>
        {
            using var measure = Core.Diagnostics.Perf.Measure("console.output");
            view.Emulator.Feed(text);
        };
        view.Emulator.Reply += Host.Write;
        view.Input += Host.Write;
        view.GridResized += (columns, rows) =>
        {
            if (_started)
            {
                Host.Resize(columns, rows);
            }
        };
    }

    public ShellHost Host { get; }

    public TerminalView View { get; }

    public TerminalEmulator Emulator => View.Emulator;

    /// <summary>Starts the shell at the view's current grid.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            Host.Start(View.Columns, View.Rows);
        }
        catch (PlatformNotSupportedException ex)
        {
            // The portable build has no pseudo terminal yet (#259): the panels work, the console says why it is empty.
            View.Emulator.Feed($"\u001b[33mNo console on this system yet: {ex.Message}\u001b[0m\r\n");
        }
    }

    /// <summary>Input for the console: keys (win32-input-mode sequences), text and replies.</summary>
    public void WriteInput(string data) => Host.Write(data);

    public void Dispose() => Host.Dispose();
}
