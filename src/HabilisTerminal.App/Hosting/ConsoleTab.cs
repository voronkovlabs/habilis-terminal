using System.IO;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Shell.Integration;
using Microsoft.Terminal.Wpf;

namespace HabilisTerminal.App.Hosting;

/// <summary>How a tab's last command went, for its mark in the strip (issue #119).</summary>
internal enum ConsoleTabMark
{
    None,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One console of the window (issue #119): a shell with its own terminal control, which keeps the screen and the history
/// while the tab is not in front, and what belongs to that shell alone. A tab not in front is hidden, not collapsed, and
/// keeps its size until it is shown again.
/// </summary>
internal sealed class ConsoleTab : IDisposable
{
    public ConsoleTab(ShellHost host)
    {
        Connection = new ShellConnection(host);
        Terminal = new TerminalControl { AutoResize = true };
    }

    public ShellConnection Connection { get; }

    public ShellHost Host => Connection.Host;

    public TerminalControl Terminal { get; }

    /// <summary>Between a command's start and the next prompt: the console has every key.</summary>
    public bool CommandRunning { get; set; }

    /// <summary>The last value given to the shell's panel-mode flag, so the flag file is written only on changes.</summary>
    public bool? ShellPanelMode { get; set; }

    /// <summary>The line of the running or last command.</summary>
    public string? CommandLine { get; set; }

    public int? LastExitCode { get; set; }

    public ConsoleTabMark Mark { get; set; }

    /// <summary>The command's output has gone past the console, as of the last report (issue #82).</summary>
    public TerminalOverflow Overflow { get; } = new();

    public Lock OverflowLock { get; } = new();

    public (int Rows, bool Alternate) OverflowPosted { get; set; }

    public int OverflowBase { get; set; }

    public bool OverflowSuppressed { get; set; }

    /// <summary>The console's growth over the panels while the tab is not in front.</summary>
    public int OverlayRows { get; set; }

    /// <summary>The running command, else the shell's folder.</summary>
    public string Title
    {
        get
        {
            if (CommandRunning && !string.IsNullOrWhiteSpace(CommandLine))
            {
                return CommandLine.Trim();
            }

            var folder = Host.CurrentDirectory?.TrimEnd('\\', '/');
            return string.IsNullOrEmpty(folder) ? "Console" : Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;
        }
    }

    public void Dispose() => Connection.Dispose();
}
