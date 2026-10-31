namespace HabilisTerminal.Shell.Integration;

/// <summary>A shell integration event decoded from an OSC payload (SPEC §7).</summary>
public abstract record ShellEvent
{
    /// <summary>OSC 133;A — the prompt starts.</summary>
    public sealed record PromptStarted : ShellEvent;

    /// <summary>OSC 133;B — the prompt ends, the shell waits for input.</summary>
    public sealed record InputStarted : ShellEvent;

    /// <summary>OSC 133;C — a command line was accepted and runs.</summary>
    public sealed record CommandStarted : ShellEvent;

    /// <summary>OSC 133;D[;code] — the command finished.</summary>
    public sealed record CommandFinished(int? ExitCode) : ShellEvent;

    /// <summary>OSC 9;9 or OSC 7 — current file system directory.</summary>
    public sealed record DirectoryChanged(string Path) : ShellEvent;

    /// <summary>OSC 7731;habi;… — a Habilis Terminal message from the integration script.</summary>
    public sealed record Message(string Name, IReadOnlyList<string> Arguments) : ShellEvent;
}
