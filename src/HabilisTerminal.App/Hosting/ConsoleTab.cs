using System.IO;
using HabilisTerminal.App.Panels;
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

    // The tab's own panels and their layout (issue #125): current while the tab is not in front; the window holds them while it is.
    public FilePanel Left { get; set; } = null!;

    public FilePanel Right { get; set; } = null!;

    public FilePanel Active { get; set; } = null!;

    public bool LeftVisible { get; set; } = true;

    public bool RightVisible { get; set; } = true;

    public (bool Left, bool Right) RestoreVisibility { get; set; } = (true, true);

    public HiddenPanelReveal Reveal { get; set; } = new();

    public bool RevealFromTerminal { get; set; }

    public int TerminalRows { get; set; }

    public double SplitRatio { get; set; }

    public double SinglePanelRatio { get; set; }

    /// <summary>The tab's colour in the strip and on the panel frame (issue #120).</summary>
    public int ColorIndex { get; set; }

    /// <summary>The command's output has gone past the console, as of the last report (issue #82).</summary>
    public TerminalOverflow Overflow { get; } = new();

    public Lock OverflowLock { get; } = new();

    public (int Rows, bool Alternate) OverflowPosted { get; set; }

    public int OverflowBase { get; set; }

    public bool OverflowSuppressed { get; set; }

    /// <summary>The console's growth over the panels while the tab is not in front.</summary>
    public int OverlayRows { get; set; }

    /// <summary>When the running or last command started.</summary>
    public DateTime CommandStartedAt { get; set; }

    /// <summary>How long the last command took.</summary>
    public TimeSpan LastDuration { get; set; }

    private bool ShowsCommand => !string.IsNullOrWhiteSpace(CommandLine) && (CommandRunning || Mark is ConsoleTabMark.Succeeded or ConsoleTabMark.Failed);

    /// <summary>The running command, or one that ended here unseen; else the shell's folder.</summary>
    public string Title
    {
        get
        {
            if (ShowsCommand)
            {
                return CommandLine!.Trim();
            }

            var folder = Host.CurrentDirectory?.TrimEnd('\\', '/');
            return string.IsNullOrEmpty(folder) ? "Console" : Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;
        }
    }

    /// <summary>After the title: how long the command runs or ran, and its code; else the folder's parent.</summary>
    public string Detail
    {
        get
        {
            if (CommandRunning)
            {
                return ConsoleTabRules.FormatElapsed(DateTime.UtcNow - CommandStartedAt);
            }

            if (ShowsCommand)
            {
                var took = ConsoleTabRules.FormatElapsed(LastDuration);
                return LastExitCode is { } code ? $"{took}, exit {code}" : took;
            }

            return Host.CurrentDirectory is { Length: > 0 } folder
                ? ConsoleTabRules.ParentForDisplay(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                : string.Empty;
        }
    }

    /// <summary>The tab's tooltip: everything the tab may have no room for.</summary>
    public string Description
    {
        get
        {
            var lines = new List<string>();
            if (ShowsCommand)
            {
                lines.Add(CommandLine!.Trim());
                lines.Add(CommandRunning ? $"Running for {ConsoleTabRules.FormatElapsed(DateTime.UtcNow - CommandStartedAt)}" : $"Finished in {Detail}");
            }

            lines.Add(Host.CurrentDirectory ?? "Starting…");
            return string.Join('\n', lines);
        }
    }

    public void Dispose() => Connection.Dispose();
}
